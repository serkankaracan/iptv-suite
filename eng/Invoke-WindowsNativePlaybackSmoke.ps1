#requires -Version 5.1

[CmdletBinding()]
param(
    [ValidateSet("Release")]
    [string]$Configuration = "Release",

    [string]$DotNetPath = "dotnet",

    [ValidateRange(2, 100)]
    [int]$SwitchCount = 25,

    [ValidateRange(0, 480)]
    [int]$SoakMinutes = 0,

    [ValidateRange(0, 7)]
    [int]$NetworkInterruptionCount = 0
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
if ($SoakMinutes -gt 0 -and $SwitchCount -ne 100) {
    throw "A native playback soak requires exactly 100 alternating switches."
}
if ($NetworkInterruptionCount -gt 0 -and $SwitchCount -ne 100) {
    throw "A native playback network interruption probe requires exactly 100 alternating switches."
}
Add-Type -AssemblyName System.IO.Compression.FileSystem -ErrorAction Stop

$activationInterop = @'
using System;
using System.Runtime.InteropServices;

namespace IptvSuite.NativePlaybackSmoke
{
    [Flags]
    internal enum ActivateOptions : uint { NoErrorUi = 0x00000002 }

    [ComImport]
    [Guid("2E941141-7F97-4756-BA1D-9DECDE894A3D")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IApplicationActivationManager
    {
        [PreserveSig]
        int ActivateApplication(
            [MarshalAs(UnmanagedType.LPWStr)] string appUserModelId,
            [MarshalAs(UnmanagedType.LPWStr)] string arguments,
            ActivateOptions options,
            out uint processId);
    }

    public static class PackagedApplicationActivator
    {
        public static int Activate(string appUserModelId, string arguments)
        {
            Guid classId = new Guid("45BA127D-10A8-46EA-8AB7-56EA9078943C");
            Guid interfaceId = new Guid("2E941141-7F97-4756-BA1D-9DECDE894A3D");
            object manager;
            int creationResult = CoCreateInstance(ref classId, IntPtr.Zero, 4, ref interfaceId, out manager);
            if (creationResult < 0) throw new COMException("Packaged activation service creation failed.", creationResult);
            try
            {
                uint processId;
                int result = ((IApplicationActivationManager)manager).ActivateApplication(
                    appUserModelId, arguments, ActivateOptions.NoErrorUi, out processId);
                if (result < 0) throw new COMException("Packaged activation failed.", result);
                if (processId == 0 || processId > Int32.MaxValue) throw new InvalidOperationException("Invalid activation process identifier.");
                return (int)processId;
            }
            finally
            {
                if (Marshal.IsComObject(manager)) Marshal.FinalReleaseComObject(manager);
            }
        }

        [DllImport("ole32.dll", ExactSpelling = true, PreserveSig = true)]
        private static extern int CoCreateInstance(
            [In] ref Guid classId,
            IntPtr outer,
            uint classContext,
            [In] ref Guid interfaceId,
            [MarshalAs(UnmanagedType.Interface)] out object value);
    }
}
'@

$tlsServerSource = @'
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace IptvSuite.NativePlaybackSmoke
{
    public sealed class TierATlsServer : IDisposable
    {
        private readonly string root;
        private readonly X509Certificate2 certificate;
        private readonly TcpListener listener;
        private readonly CancellationTokenSource cancellation = new CancellationTokenSource();
        private readonly Task acceptLoop;
        private int requestCount;
        private int failureCount;
        private int completedResponseCount;
        private int ioAbortCount;
        private int headRequestCount;
        private int rangeRequestCount;
        private int openEndedRangeCount;
        private int suffixRangeCount;
        private int boundedRangeCount;
        private int armedMediaFailure;
        private int pendingRecovery;
        private int injectedFailureCount;
        private int recoveryCount;
        private long completedBodyBytes;

        public TierATlsServer(string root, X509Certificate2 certificate)
        {
            this.root = Path.GetFullPath(root);
            this.certificate = certificate;
            listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start(8);
            acceptLoop = Task.Run((Func<Task>)AcceptLoopAsync);
        }

        public int Port { get { return ((IPEndPoint)listener.LocalEndpoint).Port; } }
        public int RequestCount { get { return Volatile.Read(ref requestCount); } }
        public int FailureCount { get { return Volatile.Read(ref failureCount); } }
        public int CompletedResponseCount { get { return Volatile.Read(ref completedResponseCount); } }
        public int IoAbortCount { get { return Volatile.Read(ref ioAbortCount); } }
        public int HeadRequestCount { get { return Volatile.Read(ref headRequestCount); } }
        public int RangeRequestCount { get { return Volatile.Read(ref rangeRequestCount); } }
        public int OpenEndedRangeCount { get { return Volatile.Read(ref openEndedRangeCount); } }
        public int SuffixRangeCount { get { return Volatile.Read(ref suffixRangeCount); } }
        public int BoundedRangeCount { get { return Volatile.Read(ref boundedRangeCount); } }
        public int InjectedFailureCount { get { return Volatile.Read(ref injectedFailureCount); } }
        public int RecoveryCount { get { return Volatile.Read(ref recoveryCount); } }
        public long CompletedBodyBytes { get { return Interlocked.Read(ref completedBodyBytes); } }

        public void ArmNextMediaRequestFailure()
        {
            if (Volatile.Read(ref pendingRecovery) != 0 ||
                Interlocked.CompareExchange(ref armedMediaFailure, 1, 0) != 0)
            {
                throw new InvalidOperationException("A media fault is already pending.");
            }
        }

        private async Task AcceptLoopAsync()
        {
            while (!cancellation.IsCancellationRequested)
            {
                try
                {
                    TcpClient client = await listener.AcceptTcpClientAsync().ConfigureAwait(false);
                    Task ignored = Task.Run(() => HandleAsync(client));
                }
                catch (ObjectDisposedException) { if (cancellation.IsCancellationRequested) return; throw; }
                catch (SocketException) { if (cancellation.IsCancellationRequested) return; throw; }
                catch { Interlocked.Increment(ref failureCount); }
            }
        }

        private async Task HandleAsync(TcpClient client)
        {
            using (client)
            using (var ssl = new SslStream(client.GetStream(), false))
            {
                try
                {
                    ssl.AuthenticateAsServer(certificate, false, SslProtocols.Tls12, false);
                    byte[] headerBuffer = new byte[16384];
                    int length = 0;
                    while (length < headerBuffer.Length)
                    {
                        int read = await ssl.ReadAsync(headerBuffer, length, headerBuffer.Length - length).ConfigureAwait(false);
                        if (read == 0) return;
                        length += read;
                        if (length >= 4 && FindHeaderEnd(headerBuffer, length) >= 0) break;
                    }
                    if (FindHeaderEnd(headerBuffer, length) < 0) { await WriteStatusAsync(ssl, 431).ConfigureAwait(false); return; }

                    string header = Encoding.ASCII.GetString(headerBuffer, 0, length);
                    string[] lines = header.Split(new[] { "\r\n" }, StringSplitOptions.None);
                    string[] request = lines[0].Split(' ');
                    if (request.Length != 3 || (request[0] != "GET" && request[0] != "HEAD")) { await WriteStatusAsync(ssl, 405).ConfigureAwait(false); return; }
                    string fileName;
                    string contentType;
                    if (!TryMap(request[1], out fileName, out contentType)) { await WriteStatusAsync(ssl, 404).ConfigureAwait(false); return; }

                    string filePath = Path.GetFullPath(Path.Combine(root, fileName));
                    if (!filePath.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || !File.Exists(filePath))
                    { await WriteStatusAsync(ssl, 404).ConfigureAwait(false); return; }

                    long total = new FileInfo(filePath).Length;
                    long start = 0;
                    long end = total - 1;
                    bool partial = false;
                    int rangeShape = 0;
                    foreach (string line in lines)
                    {
                        if (line.StartsWith("Range: bytes=", StringComparison.OrdinalIgnoreCase))
                        {
                            string range = line.Substring(13);
                            string[] bounds = range.Split('-');
                            if (range.IndexOf(',') >= 0 || bounds.Length != 2)
                            { await WriteStatusAsync(ssl, 416).ConfigureAwait(false); return; }
                            if (bounds[0].Length == 0)
                            {
                                rangeShape = 2;
                                long suffixLength;
                                if (!Int64.TryParse(bounds[1], NumberStyles.None, CultureInfo.InvariantCulture, out suffixLength) || suffixLength <= 0)
                                { await WriteStatusAsync(ssl, 416).ConfigureAwait(false); return; }
                                start = Math.Max(0, total - suffixLength);
                                end = total - 1;
                            }
                            else
                            {
                                rangeShape = bounds[1].Length == 0 ? 1 : 3;
                                if (!Int64.TryParse(bounds[0], NumberStyles.None, CultureInfo.InvariantCulture, out start) || start < 0 || start >= total)
                                { await WriteStatusAsync(ssl, 416).ConfigureAwait(false); return; }
                                if (bounds[1].Length > 0 && (!Int64.TryParse(bounds[1], NumberStyles.None, CultureInfo.InvariantCulture, out end) || end < start))
                                { await WriteStatusAsync(ssl, 416).ConfigureAwait(false); return; }
                                end = Math.Min(end, total - 1);
                            }
                            partial = true;
                        }
                    }

                    long contentLength = end - start + 1;
                    Interlocked.Increment(ref requestCount);
                    if (Interlocked.Exchange(ref armedMediaFailure, 0) == 1)
                    {
                        Interlocked.Exchange(ref pendingRecovery, 1);
                        Interlocked.Increment(ref injectedFailureCount);
                        await WriteStatusAsync(ssl, 503).ConfigureAwait(false);
                        return;
                    }
                    if (request[0] == "HEAD") Interlocked.Increment(ref headRequestCount);
                    if (partial)
                    {
                        Interlocked.Increment(ref rangeRequestCount);
                        if (rangeShape == 1) Interlocked.Increment(ref openEndedRangeCount);
                        else if (rangeShape == 2) Interlocked.Increment(ref suffixRangeCount);
                        else if (rangeShape == 3) Interlocked.Increment(ref boundedRangeCount);
                    }
                    var response = new StringBuilder();
                    response.Append(partial ? "HTTP/1.1 206 Partial Content\r\n" : "HTTP/1.1 200 OK\r\n");
                    response.Append("Content-Type: ").Append(contentType).Append("\r\n");
                    response.Append("Content-Length: ").Append(contentLength.ToString(CultureInfo.InvariantCulture)).Append("\r\n");
                    response.Append("Accept-Ranges: bytes\r\n");
                    if (partial) response.Append("Content-Range: bytes ").Append(start).Append('-').Append(end).Append('/').Append(total).Append("\r\n");
                    response.Append("Cache-Control: no-store\r\nConnection: close\r\n\r\n");
                    byte[] responseBytes = Encoding.ASCII.GetBytes(response.ToString());
                    await ssl.WriteAsync(responseBytes, 0, responseBytes.Length).ConfigureAwait(false);
                    if (request[0] == "GET")
                    {
                        using (var file = File.OpenRead(filePath))
                        {
                            file.Position = start;
                            byte[] buffer = new byte[65536];
                            long remaining = contentLength;
                            while (remaining > 0)
                            {
                                int read = await file.ReadAsync(buffer, 0, (int)Math.Min(buffer.Length, remaining)).ConfigureAwait(false);
                                if (read == 0) throw new EndOfStreamException();
                                await ssl.WriteAsync(buffer, 0, read).ConfigureAwait(false);
                                remaining -= read;
                            }
                        }
                    }
                    await ssl.FlushAsync().ConfigureAwait(false);
                    if (request[0] == "GET") Interlocked.Add(ref completedBodyBytes, contentLength);
                    Interlocked.Increment(ref completedResponseCount);
                    if (Interlocked.Exchange(ref pendingRecovery, 0) == 1)
                    {
                        Interlocked.Increment(ref recoveryCount);
                    }
                }
                catch (IOException) { Interlocked.Increment(ref ioAbortCount); }
                catch (AuthenticationException) { Interlocked.Increment(ref failureCount); }
                catch { Interlocked.Increment(ref failureCount); }
            }
        }

        private static bool TryMap(string path, out string fileName, out string contentType)
        {
            contentType = "video/mp2t";
            switch (path)
            {
                case "/direct-h264-aac.ts": fileName = "direct-h264-aac.ts"; return true;
                case "/hls.m3u8": fileName = "hls.m3u8"; contentType = "application/vnd.apple.mpegurl"; return true;
                case "/hls-000.ts": fileName = "hls-000.ts"; return true;
                case "/hls-001.ts": fileName = "hls-001.ts"; return true;
                case "/hls-002.ts": fileName = "hls-002.ts"; return true;
                case "/hls-003.ts": fileName = "hls-003.ts"; return true;
                default: fileName = null; return false;
            }
        }

        private static int FindHeaderEnd(byte[] value, int length)
        {
            for (int i = 3; i < length; i++) if (value[i - 3] == 13 && value[i - 2] == 10 && value[i - 1] == 13 && value[i] == 10) return i - 3;
            return -1;
        }

        private static async Task WriteStatusAsync(Stream stream, int status)
        {
            byte[] value = Encoding.ASCII.GetBytes("HTTP/1.1 " + status + " Rejected\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
            await stream.WriteAsync(value, 0, value.Length).ConfigureAwait(false);
        }

        public void Dispose()
        {
            cancellation.Cancel();
            listener.Stop();
            try { acceptLoop.Wait(TimeSpan.FromSeconds(5)); } catch (AggregateException) { }
            cancellation.Dispose();
        }
    }
}
'@

Add-Type -TypeDefinition $activationInterop -Language CSharp -ErrorAction Stop
Add-Type -TypeDefinition $tlsServerSource -Language CSharp -ErrorAction Stop

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$projectPath = Join-Path $repositoryRoot "apps\windows\tests\IptvSuite.NativePlaybackCompatibilitySpike\IptvSuite.NativePlaybackCompatibilitySpike.csproj"
$manifestPath = Join-Path (Split-Path -Parent $projectPath) "Package.appxmanifest"
$fixtureRoot = Join-Path $repositoryRoot "apps\windows\tests\fixtures\playback\tier-a"
$artifactRoot = Join-Path $repositoryRoot ".artifacts\native-playback-smoke"
$runId = [Guid]::NewGuid().ToString("N")
$packageOutput = Join-Path $artifactRoot "packages\$runId"
$signingCertificatePath = Join-Path $artifactRoot "$runId-signing.cer"
$tlsCertificatePath = Join-Path $artifactRoot "$runId-tls.cer"
$evidencePath = Join-Path $artifactRoot "last-success.json"
$expectedName = "NativePlaybackCompatibilitySpike.Local.a47d1387"
$expectedPublisher = "CN=Native Playback Compatibility Spike Local Test"
$expectedApplicationId = "App"
$h264DecoderClass = "Registry::HKEY_CLASSES_ROOT\CLSID\{62CE7E72-4C71-4D20-B15D-452831A87D9D}\InprocServer32"
$aacDecoderClass = "Registry::HKEY_CLASSES_ROOT\CLSID\{32D186A7-218F-4C75-8876-DD77273A8999}\InprocServer32"
$signingCertificate = $null
$tlsCertificate = $null
$tlsServer = $null
$installedPackage = $null
$installAttempted = $false
$launchedProcess = $null
$environmentBackup = @{}
$cleanupFailures = [System.Collections.Generic.List[string]]::new()
$msBuildEnvironment = @{
    AppxBundle = "Never"
    AppxPackageDir = "$packageOutput\"
    AppxPackageSigningEnabled = "true"
    AppxSymbolPackageEnabled = "false"
    DebugSymbols = "false"
    DebugType = "None"
    GenerateAppxPackageOnBuild = "true"
    UapAppxPackageBuildMode = "SideloadOnly"
}

function Remove-ExactPackage {
    Get-AppxPackage -Name $expectedName -ErrorAction SilentlyContinue |
        Where-Object { $_.Publisher -eq $expectedPublisher } |
        ForEach-Object { Remove-AppxPackage -Package $_.PackageFullName -ErrorAction Stop }
}

function Assert-PackagePayload([string]$PackagePath) {
    $archive = [System.IO.Compression.ZipFile]::OpenRead($PackagePath)
    try {
        $forbidden = @($archive.Entries | Where-Object {
            $_.FullName -match '(?i)(libvlc|videolan|libx264|direct-h264-aac|hls-00[0-3]\.ts|hls\.m3u8)'
        })
        if ($forbidden.Count -ne 0) { throw "The disposable native package contains forbidden candidate or fixture payload." }
    }
    finally { $archive.Dispose() }
}

try {
    $principal = [Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent())
    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw "Native packaged playback smoke requires an elevated Windows PowerShell session."
    }
    $enableLua = Get-ItemPropertyValue "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System" -Name EnableLUA
    if ($enableLua -ne 1) { throw "Package activation requires the Windows app-model UAC service." }

    $expectedSdk = (Get-Content -Raw (Join-Path $repositoryRoot "global.json") | ConvertFrom-Json).sdk.version
    $actualSdk = (& $DotNetPath --version).Trim()
    if ($LASTEXITCODE -ne 0 -or $actualSdk -ne $expectedSdk) { throw "Expected .NET SDK $expectedSdk, received '$actualSdk'." }
    $h264DecoderRegistered = Test-Path -LiteralPath $h264DecoderClass -PathType Container
    $aacDecoderRegistered = Test-Path -LiteralPath $aacDecoderClass -PathType Container
    $audioServiceRunning = (Get-Service -Name Audiosrv -ErrorAction SilentlyContinue).Status -eq "Running"
    $audioEndpointServiceRunning = (Get-Service -Name AudioEndpointBuilder -ErrorAction SilentlyContinue).Status -eq "Running"
    $userInteractive = [Environment]::UserInteractive
    $installationType = Get-ItemPropertyValue `
        -LiteralPath "HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion" `
        -Name InstallationType `
        -ErrorAction SilentlyContinue

    [xml]$manifest = Get-Content -Raw $manifestPath
    $identity = $manifest.SelectSingleNode("/*[local-name()='Package']/*[local-name()='Identity']")
    $application = $manifest.SelectSingleNode("/*[local-name()='Package']/*[local-name()='Applications']/*[local-name()='Application']")
    if ($identity.Name -ne $expectedName -or $identity.Publisher -ne $expectedPublisher -or $application.Id -ne $expectedApplicationId) {
        throw "The disposable native playback manifest identity changed."
    }

    New-Item -ItemType Directory -Path $packageOutput -Force | Out-Null
    $signingCertificate = New-SelfSignedCertificate -Type Custom -Subject $expectedPublisher `
        -CertStoreLocation "Cert:\CurrentUser\My" -KeyAlgorithm RSA -KeyLength 2048 -HashAlgorithm SHA256 `
        -KeyExportPolicy NonExportable -KeyUsage DigitalSignature -NotAfter (Get-Date).AddDays(2) `
        -TextExtension @("2.5.29.37={text}1.3.6.1.5.5.7.3.3", "2.5.29.19={text}")
    Export-Certificate -Cert $signingCertificate -FilePath $signingCertificatePath | Out-Null
    Import-Certificate -FilePath $signingCertificatePath -CertStoreLocation "Cert:\LocalMachine\TrustedPeople" | Out-Null

    $tlsCertificate = New-SelfSignedCertificate -DnsName "localhost" -CertStoreLocation "Cert:\CurrentUser\My" `
        -KeyAlgorithm RSA -KeyLength 2048 -HashAlgorithm SHA256 -KeyExportPolicy NonExportable `
        -NotAfter (Get-Date).AddDays(2) -TextExtension @("2.5.29.37={text}1.3.6.1.5.5.7.3.1")
    Export-Certificate -Cert $tlsCertificate -FilePath $tlsCertificatePath | Out-Null
    Import-Certificate -FilePath $tlsCertificatePath -CertStoreLocation "Cert:\LocalMachine\Root" | Out-Null
    Write-Host "Ephemeral package-signing and loopback TLS certificates are prepared."

    $msBuildEnvironment.PackageCertificateThumbprint = $signingCertificate.Thumbprint
    foreach ($entry in $msBuildEnvironment.GetEnumerator()) {
        $environmentBackup[$entry.Key] = [Environment]::GetEnvironmentVariable($entry.Key, "Process")
        [Environment]::SetEnvironmentVariable($entry.Key, $entry.Value, "Process")
    }
    & $DotNetPath restore $projectPath --locked-mode --configfile (Join-Path $repositoryRoot "NuGet.config") -p:Platform=x64 --nologo
    if ($LASTEXITCODE -ne 0) { throw "Locked native playback restore failed." }
    & $DotNetPath build $projectPath -c $Configuration -p:Platform=x64 --no-restore --nologo
    if ($LASTEXITCODE -ne 0) { throw "Signed native playback package build failed." }
    Write-Host "Disposable native playback package build completed."

    $packages = @(Get-ChildItem $packageOutput -Filter "IptvSuite.NativePlaybackCompatibilitySpike_*.msix" -Recurse -File |
        Where-Object { $_.FullName -notmatch '[\\/]Dependencies[\\/]' })
    $dependencies = @(Get-ChildItem $packageOutput -Filter "Microsoft.WindowsAppRuntime.2.msix" -Recurse -File |
        Where-Object { $_.Directory.Name -eq "x64" })
    if ($packages.Count -ne 1 -or $dependencies.Count -ne 1) { throw "Expected one native playback MSIX and one x64 runtime dependency." }
    $signature = Get-AuthenticodeSignature $packages[0].FullName
    if ($null -eq $signature.SignerCertificate -or $signature.SignerCertificate.Thumbprint -ne $signingCertificate.Thumbprint -or $signature.Status -in @("HashMismatch", "NotSigned")) {
        throw "Native playback MSIX signature validation failed."
    }
    Assert-PackagePayload $packages[0].FullName

    Remove-ExactPackage
    $installAttempted = $true
    Add-AppxPackage -Path $packages[0].FullName -DependencyPath $dependencies[0].FullName
    $installed = @(Get-AppxPackage -Name $expectedName | Where-Object { $_.Publisher -eq $expectedPublisher })
    if ($installed.Count -ne 1 -or $installed[0].Architecture -ne "X64") { throw "Disposable native playback package installation is ambiguous." }
    $installedPackage = $installed[0]
    Write-Host "Disposable native playback package installation completed."

    $tlsServer = [IptvSuite.NativePlaybackSmoke.TierATlsServer]::new($fixtureRoot, $tlsCertificate)
    $authority = "https://localhost:$($tlsServer.Port)"
    $arguments = "probe $authority/direct-h264-aac.ts $authority/hls.m3u8 $SwitchCount $SoakMinutes"
    $aumid = "$($installedPackage.PackageFamilyName)!$expectedApplicationId"
    $processId = [IptvSuite.NativePlaybackSmoke.PackagedApplicationActivator]::Activate($aumid, $arguments)
    $launchedProcess = Get-Process -Id $processId -ErrorAction Stop
    $null = $launchedProcess.Handle
    if ($launchedProcess.ProcessName -ne "IptvSuite.NativePlaybackCompatibilitySpike") { throw "Package activation returned an unexpected process." }
    Write-Host "Native playback probe activation completed."

    $packageEvidencePath = Join-Path $env:LOCALAPPDATA "Packages\$($installedPackage.PackageFamilyName)\LocalCache\M10NativePlayback\last-result.json"
    $deadline = (Get-Date).AddMinutes([Math]::Max(15, $SoakMinutes + 15))
    $probeStarted = Get-Date
    $scheduledInterruptionCount = 0
    while (-not (Test-Path -LiteralPath $packageEvidencePath -PathType Leaf) -and (Get-Date) -lt $deadline) {
        $launchedProcess.Refresh()
        if ($launchedProcess.HasExited) { throw "Native playback probe exited before writing evidence." }
        if ($scheduledInterruptionCount -lt $NetworkInterruptionCount -and
            $scheduledInterruptionCount -eq $tlsServer.InjectedFailureCount -and
            $tlsServer.InjectedFailureCount -eq $tlsServer.RecoveryCount) {
            $nextInterruption = $scheduledInterruptionCount + 1
            $interruptionDue = if ($SoakMinutes -gt 0) {
                ((Get-Date) - $probeStarted).TotalSeconds -ge
                    (($SoakMinutes * 60.0 / ($NetworkInterruptionCount + 1)) * $nextInterruption)
            }
            else {
                $tlsServer.RequestCount -ge
                    [Math]::Ceiling(($SwitchCount * 1.0 / ($NetworkInterruptionCount + 1)) * $nextInterruption)
            }
            if ($interruptionDue) {
                $tlsServer.ArmNextMediaRequestFailure()
                $scheduledInterruptionCount++
            }
        }
        Start-Sleep -Milliseconds 250
    }
    if (-not (Test-Path -LiteralPath $packageEvidencePath -PathType Leaf)) { throw "Native playback probe evidence deadline expired." }

    $probe = Get-Content -Raw $packageEvidencePath | ConvertFrom-Json
    $expectedSurfaceTransitions = if ($SwitchCount -ge 25) { 6 } else { 0 }
    if ($probe.Success -ne $true -or $probe.Failure -ne "None" -or
        [int]$probe.SwitchCount -ne $SwitchCount -or
        [int]$probe.SurfaceTransitionCount -ne $expectedSurfaceTransitions) {
        throw "Native playback probe failed with category '$($probe.Failure)': completedSwitches=$($probe.SwitchCount), surfaceTransitions=$($probe.SurfaceTransitionCount), injectedInterruptions=$($tlsServer.InjectedFailureCount), recoveries=$($tlsServer.RecoveryCount), h264Decoder=$h264DecoderRegistered, aacDecoder=$aacDecoderRegistered, audioService=$audioServiceRunning, audioEndpointService=$audioEndpointServiceRunning, userInteractive=$userInteractive, installationType=$installationType, accepted=$($tlsServer.RequestCount), completed=$($tlsServer.CompletedResponseCount), head=$($tlsServer.HeadRequestCount), range=$($tlsServer.RangeRequestCount), openEnded=$($tlsServer.OpenEndedRangeCount), suffix=$($tlsServer.SuffixRangeCount), bounded=$($tlsServer.BoundedRangeCount), bodyBytes=$($tlsServer.CompletedBodyBytes), ioAbort=$($tlsServer.IoAbortCount), transportFailure=$($tlsServer.FailureCount)."
    }
    if ([double]$probe.StartupP95Milliseconds -gt 3000 -or [double]$probe.StartupMaximumMilliseconds -gt 5000) {
        throw "Native playback startup budget failed: p95=$($probe.StartupP95Milliseconds), maximum=$($probe.StartupMaximumMilliseconds), hlsP95=$($probe.HlsStartupP95Milliseconds), directP95=$($probe.DirectStartupP95Milliseconds)."
    }
    if ($SoakMinutes -gt 0 -and (
        [int]$probe.SoakMinutes -ne $SoakMinutes -or
        [int]$probe.ResourceSampleCount -lt [Math]::Max(2, [Math]::Floor($SoakMinutes / 5) - 2) -or
        [bool]$probe.MemoryMonotonicIncrease -or
        [long]$probe.MemoryNetGrowthBytes -gt 104857600 -or
        [double]$probe.MemoryNetGrowthPercent -gt 10)) {
        throw "Native playback soak resource budget failed."
    }
    if ($tlsServer.FailureCount -ne 0 -or $tlsServer.RequestCount -lt $SwitchCount) { throw "Loopback media request invariant failed." }
    if ($scheduledInterruptionCount -ne $NetworkInterruptionCount -or
        $tlsServer.InjectedFailureCount -ne $NetworkInterruptionCount -or
        $tlsServer.RecoveryCount -ne $NetworkInterruptionCount) {
        throw "Native playback network interruption/recovery invariant failed."
    }

    $summary = [ordered]@{
        SchemaVersion = 4
        Stage = "M10NativeTierAPlayback"
        Result = "Passed"
        SwitchCount = $SwitchCount
        StartupP95Milliseconds = [Math]::Round([double]$probe.StartupP95Milliseconds, 3)
        StartupMaximumMilliseconds = [Math]::Round([double]$probe.StartupMaximumMilliseconds, 3)
        HlsStartupP95Milliseconds = [Math]::Round([double]$probe.HlsStartupP95Milliseconds, 3)
        DirectStartupP95Milliseconds = [Math]::Round([double]$probe.DirectStartupP95Milliseconds, 3)
        SoakMinutes = [int]$probe.SoakMinutes
        ResourceSampleCount = [int]$probe.ResourceSampleCount
        WarmupPrivateBytes = [long]$probe.WarmupPrivateBytes
        MemoryNetGrowthBytes = [long]$probe.MemoryNetGrowthBytes
        MemoryNetGrowthPercent = [Math]::Round([double]$probe.MemoryNetGrowthPercent, 3)
        MemoryMonotonicIncrease = [bool]$probe.MemoryMonotonicIncrease
        WarmupHandleCount = [int]$probe.WarmupHandleCount
        HandleNetGrowth = [int]$probe.HandleNetGrowth
        SurfaceTransitionCount = [int]$probe.SurfaceTransitionCount
        NetworkInterruptionCount = $tlsServer.InjectedFailureCount
        NetworkRecoveryCount = $tlsServer.RecoveryCount
        InitialPrivateBytes = [long]$probe.InitialPrivateBytes
        FinalPrivateBytes = [long]$probe.FinalPrivateBytes
        InitialHandleCount = [int]$probe.InitialHandleCount
        FinalHandleCount = [int]$probe.FinalHandleCount
        LoopbackRequestCount = $tlsServer.RequestCount
        H264DecoderRegistered = $h264DecoderRegistered
        AacDecoderRegistered = $aacDecoderRegistered
        Transport = "Tls12LoopbackAllowlist"
        Fixtures = @("DirectH264AacMpegTs", "HlsH264AacMpegTs")
        PackageSha256 = (Get-FileHash $packages[0].FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    }
    New-Item -ItemType Directory -Path $artifactRoot -Force | Out-Null
    $summary | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $evidencePath -Encoding UTF8
    Write-Host "Native packaged Tier A playback smoke passed: $SwitchCount alternating switches."
}
finally {
    if ($null -ne $launchedProcess) {
        try { if (-not $launchedProcess.HasExited) { $null = $launchedProcess.CloseMainWindow(); if (-not $launchedProcess.WaitForExit(5000)) { $launchedProcess.Kill(); $launchedProcess.WaitForExit() } } } catch { $cleanupFailures.Add("ProcessCleanup") }
        $launchedProcess.Dispose()
    }
    if ($null -ne $tlsServer) { try { $tlsServer.Dispose() } catch { $cleanupFailures.Add("TlsServerCleanup") } }
    if ($installAttempted) { try { Remove-ExactPackage } catch { $cleanupFailures.Add("PackageCleanup") } }
    foreach ($entry in $environmentBackup.GetEnumerator()) { [Environment]::SetEnvironmentVariable($entry.Key, $entry.Value, "Process") }
    if ($null -ne $tlsCertificate) {
        foreach ($path in @("Cert:\LocalMachine\Root\$($tlsCertificate.Thumbprint)", "Cert:\CurrentUser\My\$($tlsCertificate.Thumbprint)")) {
            try { if (Test-Path $path) { Remove-Item -LiteralPath $path -Force } } catch { $cleanupFailures.Add("TlsCertificateCleanup") }
        }
    }
    if ($null -ne $signingCertificate) {
        foreach ($path in @("Cert:\LocalMachine\TrustedPeople\$($signingCertificate.Thumbprint)", "Cert:\CurrentUser\My\$($signingCertificate.Thumbprint)")) {
            try { if (Test-Path $path) { Remove-Item -LiteralPath $path -Force } } catch { $cleanupFailures.Add("SigningCertificateCleanup") }
        }
    }
    Remove-Item -LiteralPath $signingCertificatePath -Force -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath $tlsCertificatePath -Force -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath $packageOutput -Recurse -Force -ErrorAction SilentlyContinue
    if ($cleanupFailures.Count -ne 0) { throw "Native playback smoke cleanup failed: $($cleanupFailures -join ', ')." }
}
