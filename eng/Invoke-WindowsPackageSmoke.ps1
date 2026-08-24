[CmdletBinding()]
param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",

    [string]$DotNetPath = "dotnet"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.IO.Compression -ErrorAction Stop
Add-Type -AssemblyName UIAutomationClient -ErrorAction Stop
Add-Type -AssemblyName UIAutomationTypes -ErrorAction Stop

$activationInterop = @'
using System;
using System.Runtime.InteropServices;

namespace IptvSuite.PackageSmoke
{
    [Flags]
    internal enum ActivateOptions : uint
    {
        NoErrorUi = 0x00000002,
    }

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
        private const uint LocalServer = 0x00000004;
        private static readonly Guid ClassId =
            new Guid("45BA127D-10A8-46EA-8AB7-56EA9078943C");
        private static readonly Guid InterfaceId =
            new Guid("2E941141-7F97-4756-BA1D-9DECDE894A3D");

        [DllImport("ole32.dll", ExactSpelling = true, PreserveSig = true)]
        private static extern int CoCreateInstance(
            [In] ref Guid classId,
            IntPtr outer,
            uint classContext,
            [In] ref Guid interfaceId,
            [MarshalAs(UnmanagedType.Interface)] out object value);

        public static int Activate(string appUserModelId)
        {
            if (String.IsNullOrWhiteSpace(appUserModelId))
            {
                throw new ArgumentException(
                    "The application user model ID is required.",
                    "appUserModelId");
            }

            Guid classId = ClassId;
            Guid interfaceId = InterfaceId;
            object activationManager;
            int creationResult = CoCreateInstance(
                ref classId,
                IntPtr.Zero,
                LocalServer,
                ref interfaceId,
                out activationManager);
            if (creationResult < 0)
            {
                throw new COMException(
                    String.Format(
                        "Packaged application activation service creation failed (HRESULT 0x{0:X8}).",
                        unchecked((uint)creationResult)),
                    creationResult);
            }

            try
            {
                uint processId;
                int result = ((IApplicationActivationManager)activationManager).ActivateApplication(
                    appUserModelId,
                    null,
                    ActivateOptions.NoErrorUi,
                    out processId);
                if (result < 0)
                {
                    throw new COMException(
                        String.Format(
                            "Packaged application activation failed (HRESULT 0x{0:X8}).",
                            unchecked((uint)result)),
                        result);
                }

                if (processId == 0 || processId > Int32.MaxValue)
                {
                    throw new InvalidOperationException(
                        "Package activation returned an invalid process identifier.");
                }

                return (int)processId;
            }
            finally
            {
                if (Marshal.IsComObject(activationManager))
                {
                    Marshal.FinalReleaseComObject(activationManager);
                }
            }
        }
    }

    public static class WindowInspector
    {
        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool IsWindowVisible(IntPtr windowHandle);

        [DllImport("user32.dll")]
        public static extern uint GetWindowThreadProcessId(
            IntPtr windowHandle,
            out uint processId);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetForegroundWindow(IntPtr windowHandle);

        [DllImport("user32.dll")]
        public static extern IntPtr GetForegroundWindow();
    }

    public static class KeyboardInspector
    {
        private const ushort VirtualKeyTab = 0x09;
        private const ushort VirtualKeyEnter = 0x0D;
        private const ushort VirtualKeyPageUp = 0x21;
        private const ushort VirtualKeyPageDown = 0x22;
        private const ushort VirtualKeyHome = 0x24;
        private const uint InputKeyboard = 1;
        private const uint KeyEventKeyUp = 0x0002;

        [StructLayout(LayoutKind.Sequential)]
        private struct Input
        {
            internal uint Type;
            internal InputUnion Data;
        }

        [StructLayout(LayoutKind.Explicit, Size = 32)]
        private struct InputUnion
        {
            [FieldOffset(0)]
            internal KeyboardInput Keyboard;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct KeyboardInput
        {
            internal ushort VirtualKey;
            internal ushort ScanCode;
            internal uint Flags;
            internal uint Time;
            internal UIntPtr ExtraInfo;
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint SendInput(
            uint inputCount,
            [In] Input[] inputs,
            int inputSize);

        public static void PressTab()
        {
            Press(VirtualKeyTab);
        }

        public static void PressEnter()
        {
            Press(VirtualKeyEnter);
        }

        public static void PressHome()
        {
            Press(VirtualKeyHome);
        }

        public static void PressPageUp()
        {
            Press(VirtualKeyPageUp);
        }

        public static void PressPageDown()
        {
            Press(VirtualKeyPageDown);
        }

        private static void Press(ushort virtualKey)
        {
            Input[] inputs =
            {
                CreateKeyboardInput(virtualKey, 0),
                CreateKeyboardInput(virtualKey, KeyEventKeyUp),
            };

            if (SendInput((uint)inputs.Length, inputs, Marshal.SizeOf(typeof(Input))) != (uint)inputs.Length)
            {
                throw new InvalidOperationException("The packaged keyboard input pair was not delivered atomically.");
            }
        }

        private static Input CreateKeyboardInput(ushort virtualKey, uint flags)
        {
            return new Input
            {
                Type = InputKeyboard,
                Data = new InputUnion
                {
                    Keyboard = new KeyboardInput
                    {
                        VirtualKey = virtualKey,
                        Flags = flags,
                    },
                },
            };
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct UnsignedRatio
    {
        internal uint Numerator;
        internal uint Denominator;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    internal struct DwmTimingInfo
    {
        internal uint Size;
        internal UnsignedRatio RateRefresh;
        internal ulong QpcRefreshPeriod;
        internal UnsignedRatio RateCompose;
        internal ulong QpcVBlank;
        internal ulong Refresh;
        internal uint DxRefresh;
        internal ulong QpcCompose;
        internal ulong Frame;
        internal uint DxPresent;
        internal ulong RefreshFrame;
        internal ulong FrameSubmitted;
        internal uint DxPresentSubmitted;
        internal ulong FrameConfirmed;
        internal uint DxPresentConfirmed;
        internal ulong RefreshConfirmed;
        internal uint DxRefreshConfirmed;
        internal ulong FramesLate;
        internal uint FramesOutstanding;
        internal ulong FrameDisplayed;
        internal ulong QpcFrameDisplayed;
        internal ulong RefreshFrameDisplayed;
        internal ulong FrameComplete;
        internal ulong QpcFrameComplete;
        internal ulong FramePending;
        internal ulong QpcFramePending;
        internal ulong FramesDisplayed;
        internal ulong FramesComplete;
        internal ulong FramesPending;
        internal ulong FramesAvailable;
        internal ulong FramesDropped;
        internal ulong FramesMissed;
        internal ulong RefreshNextDisplayed;
        internal ulong RefreshNextPresented;
        internal ulong RefreshesDisplayed;
        internal ulong RefreshesPresented;
        internal ulong RefreshStarted;
        internal ulong PixelsReceived;
        internal ulong PixelsDrawn;
        internal ulong BuffersEmpty;
    }

    public sealed class DwmFrameSampleResult
    {
        public double P95Milliseconds { get; internal set; }
        public double MaximumMilliseconds { get; internal set; }
        public double DroppedPercent { get; internal set; }
        public int IntervalCount { get; internal set; }
    }

    public static class DwmFrameSampler
    {
        private static readonly object Sync = new object();
        private static System.Threading.Thread worker;
        private static bool running;
        private static Exception failure;
        private static readonly System.Collections.Generic.List<ulong> Timestamps =
            new System.Collections.Generic.List<ulong>();
        private static ulong displayed;
        private static ulong dropped;

        [DllImport("dwmapi.dll", PreserveSig = true)]
        private static extern int DwmGetCompositionTimingInfo(
            IntPtr windowHandle,
            ref DwmTimingInfo timingInfo);

        public static void Start()
        {
            lock (Sync)
            {
                if (Marshal.SizeOf(typeof(DwmTimingInfo)) != 292)
                {
                    throw new InvalidOperationException("The DWM timing ABI size is invalid.");
                }
                if (running || worker != null)
                {
                    throw new InvalidOperationException("The DWM frame sampler is already active.");
                }
                Timestamps.Clear();
                displayed = 0;
                dropped = 0;
                failure = null;
                running = true;
                worker = new System.Threading.Thread(SampleLoop);
                worker.IsBackground = true;
                worker.Name = "IptvSuite M9 DWM sampler";
                worker.Start();
            }
        }

        public static DwmFrameSampleResult Stop()
        {
            System.Threading.Thread thread;
            lock (Sync)
            {
                if (worker == null)
                {
                    throw new InvalidOperationException("The DWM frame sampler is not active.");
                }
                running = false;
                thread = worker;
            }
            if (!thread.Join(5000))
            {
                throw new TimeoutException("The DWM frame sampler did not stop.");
            }
            lock (Sync)
            {
                worker = null;
                if (failure != null)
                {
                    throw new InvalidOperationException(
                        string.Format(
                            System.Globalization.CultureInfo.InvariantCulture,
                            "The DWM frame sampler failed ({0}, HRESULT 0x{1:X8}).",
                            failure.GetType().Name,
                            unchecked((uint)failure.HResult)),
                        failure);
                }
                if (Timestamps.Count < 31)
                {
                    throw new InvalidOperationException("The DWM frame sample is too small.");
                }
                var intervals = new System.Collections.Generic.List<double>(Timestamps.Count - 1);
                for (int index = 1; index < Timestamps.Count; index++)
                {
                    if (Timestamps[index] > Timestamps[index - 1])
                    {
                        intervals.Add(
                            (Timestamps[index] - Timestamps[index - 1]) * 1000.0 /
                            System.Diagnostics.Stopwatch.Frequency);
                    }
                }
                if (intervals.Count < 30)
                {
                    throw new InvalidOperationException("The DWM frame interval sample is too small.");
                }
                intervals.Sort();
                int percentileIndex = Math.Max(0, (int)Math.Ceiling(intervals.Count * 0.95) - 1);
                ulong denominator = displayed + dropped;
                if (denominator == 0)
                {
                    throw new InvalidOperationException("The DWM frame counters are unavailable.");
                }
                return new DwmFrameSampleResult
                {
                    P95Milliseconds = intervals[percentileIndex],
                    MaximumMilliseconds = intervals[intervals.Count - 1],
                    DroppedPercent = dropped * 100.0 / denominator,
                    IntervalCount = intervals.Count,
                };
            }
        }

        private static void SampleLoop()
        {
            try
            {
                ulong previousTimestamp = 0;
                ulong previousRefresh = 0;
                ulong previousLate = 0;
                while (true)
                {
                    lock (Sync)
                    {
                        if (!running) return;
                    }
                    DwmTimingInfo timing = new DwmTimingInfo();
                    timing.Size = (uint)Marshal.SizeOf(typeof(DwmTimingInfo));
                    int result = DwmGetCompositionTimingInfo(IntPtr.Zero, ref timing);
                    if (result < 0)
                    {
                        throw new COMException("DWM composition timing is unavailable.", result);
                    }
                    if (timing.QpcVBlank != 0 && timing.QpcVBlank != previousTimestamp)
                    {
                        lock (Sync)
                        {
                            Timestamps.Add(timing.QpcVBlank);
                            if (previousTimestamp != 0 &&
                                timing.Refresh >= previousRefresh &&
                                timing.FramesLate >= previousLate)
                            {
                                ulong refreshDelta = timing.Refresh - previousRefresh;
                                ulong lateDelta = Math.Min(
                                    timing.FramesLate - previousLate,
                                    refreshDelta);
                                displayed += refreshDelta - lateDelta;
                                dropped += lateDelta;
                            }
                        }
                        previousTimestamp = timing.QpcVBlank;
                        previousRefresh = timing.Refresh;
                        previousLate = timing.FramesLate;
                    }
                    System.Threading.Thread.Sleep(1);
                }
            }
            catch (Exception exception)
            {
                lock (Sync)
                {
                    failure = exception;
                    running = false;
                }
            }
        }
    }
}
'@
Add-Type -TypeDefinition $activationInterop -Language CSharp -ErrorAction Stop

$expectedName = "IptvSuite.LocalDev.6f0d9a64"
$expectedPublisher = "CN=IptvSuite Local Development"
$expectedApplicationId = "App"
$testCanaryMarker = "IPTVSUITE_TEST_ONLY_CANARY_V1"
$expectedRuntimeDependencyName = "Microsoft.WindowsAppRuntime.2"
$expectedRuntimeDependencyPublisher = "CN=Microsoft Corporation, O=Microsoft Corporation, L=Redmond, S=Washington, C=US"
$expectedRuntimeDependencyPublisherId = "8wekyb3d8bbwe"
$expectedRuntimeDependencyVersion = "2.4.0.0"
$expectedCatalogSourceName = "Synthetic 50k source"
$expectedPlaybackSourceName = "00 Synthetic protected playback source"
$expectedPlaybackChannelAName = "Synthetic protected Tier A channel A"
$expectedPlaybackChannelBName = "Synthetic protected Tier A channel B"
$expectedPlaybackCertificateSubject = "CN=IPTVSuite Synthetic Loopback"
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$projectPath = Join-Path $repositoryRoot "apps\windows\src\IptvSuite.Windows\IptvSuite.Windows.csproj"
$catalogUiHarnessProjectPath = Join-Path $repositoryRoot "apps\windows\tests\IptvSuite.CatalogUiAcceptanceHarness\IptvSuite.CatalogUiAcceptanceHarness.csproj"
$catalogUiHarnessAssemblyPath = Join-Path $repositoryRoot "apps\windows\tests\IptvSuite.CatalogUiAcceptanceHarness\bin\x64\$Configuration\net10.0\IptvSuite.CatalogUiAcceptanceHarness.dll"
$playbackUiHarnessProjectPath = Join-Path $repositoryRoot "apps\windows\tests\IptvSuite.PlaybackUiAcceptanceHarness\IptvSuite.PlaybackUiAcceptanceHarness.csproj"
$playbackUiHarnessAssemblyPath = Join-Path $repositoryRoot "apps\windows\tests\IptvSuite.PlaybackUiAcceptanceHarness\bin\x64\$Configuration\net10.0\IptvSuite.PlaybackUiAcceptanceHarness.dll"
$playbackFixtureRoot = Join-Path $repositoryRoot "apps\windows\tests\fixtures\playback\tier-a"
$sourceManifestPath = Join-Path $repositoryRoot "apps\windows\src\IptvSuite.Windows\Package.appxmanifest"
$artifactRoot = Join-Path $repositoryRoot ".artifacts\msix-smoke"
$runId = [Guid]::NewGuid().ToString("N")
$packageOutput = Join-Path $artifactRoot "packages\$runId"
$playbackControlRoot = Join-Path $artifactRoot "playback-ui"
$playbackControlDirectory = Join-Path $playbackControlRoot $runId
$playbackReadyPath = Join-Path $playbackControlDirectory "ready.json"
$playbackResultPath = Join-Path $playbackControlDirectory "result.json"
$playbackStopSignalPath = Join-Path $playbackControlDirectory "stop.signal"
$playbackPublicCertificatePath = Join-Path $playbackControlDirectory "loopback.cer"
$publicCertificatePath = Join-Path $artifactRoot "$runId.cer"
$evidencePath = Join-Path $artifactRoot "last-success.json"
$failureEvidencePath = Join-Path $artifactRoot "last-failure.json"

$certificate = $null
$installedPackage = $null
$launchedProcess = $null
$playbackHarnessProcess = $null
$playbackLoopbackCertificate = $null
$playbackLoopbackCertificateThumbprint = $null
$playbackLoopbackCertificateImported = $false
$playbackHarnessReady = $false
$playbackStopSignalCreated = $false
$installAttempted = $false
$environmentBackup = @{}
$primaryFailure = $null
$successEvidence = $null
$successMessage = $null
$protectedStoreDirectoryInitialized = $false
$catalogUiaContractVerified = $false
$catalogKeyboardFocusOrderVerified = $false
$catalog50kSeedVerified = $false
$catalogRealizedContainerBoundVerified = $false
$catalogRealizedContainerCount = 0
$catalogInputResponseP95Milliseconds = 0.0
$catalogFrameP95Milliseconds = 0.0
$catalogFrameMaximumMilliseconds = 0.0
$catalogDroppedFramePercent = 0.0
$catalogFrameIntervalCount = 0
$playbackUiAcceptanceVerified = $false
$playbackVolumeControlVerified = $false
$playbackMuteControlVerified = $false
$playbackAspectControlVerified = $false
$playbackFullscreenEnterVerified = $false
$playbackFullscreenExitVerified = $false
$playbackFullscreenFocusRestored = $false
$playbackRapidSwitchVerified = $false
$playbackRapidSwitchCount = 0
$playbackRapidSwitchP95Milliseconds = 0.0
$playbackRapidSwitchMaximumMilliseconds = 0.0
$playbackActiveCloseVerified = $false
$playbackUiRequestCount = 0
$playbackUiCompletedResponseCount = 0
$playbackUiCompletedBodyBytes = 0L
$playbackChannelARequestCount = 0
$playbackChannelBRequestCount = 0
$windowsAppRuntimeDisposition = "NotStarted"
$cleanupFailures = [System.Collections.Generic.List[string]]::new()
$msBuildEnvironment = @{
    AppxBundle                    = "Never"
    AppxPackageDir                = "$packageOutput\"
    AppxPackageSigningEnabled     = "true"
    AppxSymbolPackageEnabled      = "false"
    DebugSymbols                  = "false"
    DebugType                     = "None"
    GenerateAppxPackageOnBuild    = "true"
    UapAppxPackageBuildMode       = "SideloadOnly"
}

New-Item -ItemType Directory -Path $artifactRoot -Force | Out-Null

function Assert-ManifestPolicy {
    param(
        [Parameter(Mandatory)]
        [xml]$Manifest
    )

    $identity = $Manifest.SelectSingleNode("/*[local-name()='Package']/*[local-name()='Identity']")
    if ($null -eq $identity) {
        throw "Package identity is missing."
    }

    if ($identity.GetAttribute("Name") -ne $expectedName) {
        throw "Unexpected package name."
    }

    if ($identity.GetAttribute("Publisher") -ne $expectedPublisher) {
        throw "Unexpected package publisher."
    }

    $applications = @($Manifest.SelectNodes("/*[local-name()='Package']/*[local-name()='Applications']/*[local-name()='Application']"))
    if ($applications.Count -ne 1 -or $applications[0].GetAttribute("Id") -ne $expectedApplicationId) {
        throw "The package must contain exactly the M1 application."
    }

    $capabilities = @(
        $Manifest.SelectNodes("/*[local-name()='Package']/*[local-name()='Capabilities']/*") |
            ForEach-Object { $_.GetAttribute("Name") }
    )
    $capabilityDifference = @(Compare-Object -ReferenceObject @("runFullTrust") -DifferenceObject $capabilities)
    if ($capabilityDifference.Count -ne 0) {
        throw "Unexpected capability set: $($capabilities -join ', ')"
    }
}

function Assert-BuiltManifestPolicy {
    param(
        [Parameter(Mandatory)]
        [xml]$Manifest
    )

    Assert-ManifestPolicy -Manifest $Manifest

    $identity = $Manifest.SelectSingleNode("/*[local-name()='Package']/*[local-name()='Identity']")
    if ($identity.GetAttribute("ProcessorArchitecture") -ne "x64") {
        throw "The built package must target x64 only."
    }

    $targetFamilies = @($Manifest.SelectNodes("/*[local-name()='Package']/*[local-name()='Dependencies']/*[local-name()='TargetDeviceFamily']"))
    if ($targetFamilies.Count -ne 1 -or
        $targetFamilies[0].GetAttribute("Name") -ne "Windows.Desktop" -or
        $targetFamilies[0].GetAttribute("MinVersion") -ne "10.0.26100.0") {
        throw "Unexpected Windows device-family baseline."
    }

    $frameworkDependencies = @($Manifest.SelectNodes("/*[local-name()='Package']/*[local-name()='Dependencies']/*[local-name()='PackageDependency']"))
    if ($frameworkDependencies.Count -ne 1 -or
        $frameworkDependencies[0].GetAttribute("Name") -ne "Microsoft.WindowsAppRuntime.2" -or
        $frameworkDependencies[0].GetAttribute("MinVersion") -ne "2.4.0.0") {
        throw "The MSIX must remain framework-dependent on Windows App Runtime 2.4.0."
    }
}

function Expand-MsixForInspection {
    param(
        [Parameter(Mandatory)]
        [string]$PackagePath,

        [Parameter(Mandatory)]
        [string]$DestinationPath
    )

    $packageFullPath = [System.IO.Path]::GetFullPath($PackagePath)
    $destinationFullPath = [System.IO.Path]::GetFullPath($DestinationPath)
    if (-not [System.IO.File]::Exists($packageFullPath)) {
        throw "The MSIX inspection input does not exist."
    }

    if (Test-Path -LiteralPath $destinationFullPath) {
        throw "The MSIX inspection destination already exists."
    }

    [System.IO.Directory]::CreateDirectory($destinationFullPath) | Out-Null
    if (([System.IO.File]::GetAttributes($destinationFullPath) -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "The MSIX inspection destination must not be a reparse point."
    }

    $directorySeparator = [System.IO.Path]::DirectorySeparatorChar
    $destinationPrefix = $destinationFullPath
    if (-not $destinationPrefix.EndsWith($directorySeparator)) {
        $destinationPrefix += $directorySeparator
    }

    $seenTargets = [System.Collections.Generic.HashSet[string]]::new(
        [System.StringComparer]::OrdinalIgnoreCase)
    $maximumEntryCount = 25000
    [long]$maximumExtractedBytes = 2147483648
    [long]$totalExtractedBytes = 0
    $packageStream = $null
    $archive = $null

    try {
        $packageStream = [System.IO.File]::Open(
            $packageFullPath,
            [System.IO.FileMode]::Open,
            [System.IO.FileAccess]::Read,
            [System.IO.FileShare]::Read)
        $archive = [System.IO.Compression.ZipArchive]::new(
            $packageStream,
            [System.IO.Compression.ZipArchiveMode]::Read,
            $false)

        if ($archive.Entries.Count -gt $maximumEntryCount) {
            throw "The MSIX contains too many entries for safe inspection."
        }

        foreach ($entry in $archive.Entries) {
            if ($entry.Length -gt ($maximumExtractedBytes - $totalExtractedBytes)) {
                throw "The MSIX exceeds the safe inspection size limit."
            }
            $totalExtractedBytes += $entry.Length

            $normalizedEntryName = $entry.FullName.Replace('\', '/')
            if ([string]::IsNullOrWhiteSpace($normalizedEntryName) -or
                $normalizedEntryName.StartsWith('/')) {
                throw "The MSIX contains an invalid archive path."
            }

            $isDirectory = $normalizedEntryName.EndsWith('/')
            $segments = @(
                $normalizedEntryName.Split(
                    [char]'/',
                    [System.StringSplitOptions]::RemoveEmptyEntries)
            )
            if ($segments.Count -eq 0) {
                throw "The MSIX contains an empty archive path."
            }

            foreach ($segment in $segments) {
                $deviceName = $segment.Split('.')[0]
                if ($segment -in @('.', '..') -or
                    $segment.IndexOf(':') -ge 0 -or
                    $segment.IndexOfAny([System.IO.Path]::GetInvalidFileNameChars()) -ge 0 -or
                    $segment.EndsWith('.') -or
                    $segment.EndsWith(' ') -or
                    $deviceName -match '^(?i:CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])$') {
                    throw "The MSIX contains an unsafe archive path segment."
                }
            }

            $relativePath = [string]::Join([string]$directorySeparator, [string[]]$segments)
            $targetPath = [System.IO.Path]::GetFullPath(
                [System.IO.Path]::Combine($destinationFullPath, $relativePath))
            if (-not $targetPath.StartsWith(
                    $destinationPrefix,
                    [System.StringComparison]::OrdinalIgnoreCase)) {
                throw "The MSIX archive path escapes the inspection directory."
            }

            if (-not $seenTargets.Add($targetPath)) {
                throw "The MSIX contains duplicate or case-colliding archive paths."
            }

            if ($isDirectory) {
                if ($entry.Length -ne 0) {
                    throw "The MSIX contains a directory entry with data."
                }
                [System.IO.Directory]::CreateDirectory($targetPath) | Out-Null
                continue
            }

            $targetDirectory = [System.IO.Path]::GetDirectoryName($targetPath)
            [System.IO.Directory]::CreateDirectory($targetDirectory) | Out-Null
            if (([System.IO.File]::GetAttributes($targetDirectory) -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw "The MSIX extraction target must not be a reparse point."
            }

            $entryStream = $null
            $outputStream = $null
            try {
                $entryStream = $entry.Open()
                $outputStream = [System.IO.FileStream]::new(
                    $targetPath,
                    [System.IO.FileMode]::CreateNew,
                    [System.IO.FileAccess]::Write,
                    [System.IO.FileShare]::None,
                    81920,
                    [System.IO.FileOptions]::SequentialScan)
                $copyBuffer = New-Object byte[] 81920
                [long]$written = 0
                while (($read = $entryStream.Read($copyBuffer, 0, $copyBuffer.Length)) -gt 0) {
                    if ($written -gt ($entry.Length - $read)) {
                        throw "An MSIX entry expanded beyond its declared size."
                    }
                    $outputStream.Write($copyBuffer, 0, $read)
                    $written += $read
                }

                if ($written -ne $entry.Length) {
                    throw "An MSIX entry did not match its declared size."
                }
            }
            finally {
                if ($null -ne $outputStream) {
                    $outputStream.Dispose()
                }
                if ($null -ne $entryStream) {
                    $entryStream.Dispose()
                }
            }
        }
    }
    finally {
        if ($null -ne $archive) {
            $archive.Dispose()
        }
        if ($null -ne $packageStream) {
            $packageStream.Dispose()
        }
    }
}

function Test-FileContainsByteSequence {
    param(
        [Parameter(Mandatory)]
        [string]$Path,

        [Parameter(Mandatory)]
        [byte[]]$Sequence
    )

    if ($Sequence.Length -eq 0) {
        throw "The payload scan sequence must not be empty."
    }

    $prefixTable = New-Object int[] $Sequence.Length
    $prefixLength = 0
    for ($index = 1; $index -lt $Sequence.Length; $index++) {
        while ($prefixLength -gt 0 -and $Sequence[$index] -ne $Sequence[$prefixLength]) {
            $prefixLength = $prefixTable[$prefixLength - 1]
        }

        if ($Sequence[$index] -eq $Sequence[$prefixLength]) {
            $prefixLength++
            $prefixTable[$index] = $prefixLength
        }
    }

    $stream = $null
    try {
        $stream = [System.IO.File]::Open(
            $Path,
            [System.IO.FileMode]::Open,
            [System.IO.FileAccess]::Read,
            [System.IO.FileShare]::Read)
        $buffer = New-Object byte[] 65536
        $matched = 0
        while (($read = $stream.Read($buffer, 0, $buffer.Length)) -gt 0) {
            for ($index = 0; $index -lt $read; $index++) {
                $current = $buffer[$index]
                while ($matched -gt 0 -and $current -ne $Sequence[$matched]) {
                    $matched = $prefixTable[$matched - 1]
                }

                if ($current -eq $Sequence[$matched]) {
                    $matched++
                    if ($matched -eq $Sequence.Length) {
                        return $true
                    }
                }
            }
        }

        return $false
    }
    finally {
        if ($null -ne $stream) {
            $stream.Dispose()
        }
    }
}

function Assert-ProductionPackagePayload {
    param(
        [Parameter(Mandatory)]
        [string]$PackagePath
    )

    $inspectionId = [Guid]::NewGuid().ToString('N')
    $inspectionLeaf = "IptvSuite-MsixInspection-$inspectionId"
    $temporaryRoot = [System.IO.Path]::GetFullPath([System.IO.Path]::GetTempPath())
    $inspectionRoot = [System.IO.Path]::GetFullPath(
        [System.IO.Path]::Combine($temporaryRoot, $inspectionLeaf))
    $expectedParent = [System.IO.Path]::GetDirectoryName($inspectionRoot)
    if (-not $expectedParent.Equals(
            $temporaryRoot.TrimEnd(
                [System.IO.Path]::DirectorySeparatorChar,
                [System.IO.Path]::AltDirectorySeparatorChar),
            [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "The MSIX inspection directory is outside the operating-system temp root."
    }

    $markerUtf8 = [System.Text.Encoding]::UTF8.GetBytes($testCanaryMarker)
    $base64StablePrefixLength = $testCanaryMarker.Length - ($testCanaryMarker.Length % 3)
    $base64StablePrefix = $testCanaryMarker.Substring(0, $base64StablePrefixLength)
    $markerPatterns = @(
        [pscustomobject]@{ Name = 'UTF-8'; Bytes = $markerUtf8 },
        [pscustomobject]@{ Name = 'UTF-16LE'; Bytes = [System.Text.Encoding]::Unicode.GetBytes($testCanaryMarker) },
        [pscustomobject]@{ Name = 'UTF-16BE'; Bytes = [System.Text.Encoding]::BigEndianUnicode.GetBytes($testCanaryMarker) },
        [pscustomobject]@{
            Name = 'Base64 UTF-8 prefix'
            Bytes = [System.Text.Encoding]::ASCII.GetBytes(
                [Convert]::ToBase64String([System.Text.Encoding]::UTF8.GetBytes($base64StablePrefix)))
        }
    )

    try {
        Expand-MsixForInspection -PackagePath $PackagePath -DestinationPath $inspectionRoot

        $payloadEntries = @(
            Get-ChildItem -LiteralPath $inspectionRoot -Force -Recurse |
                Sort-Object -Property FullName
        )
        if ($payloadEntries.Count -eq 0) {
            throw "The MSIX inspection produced an empty payload."
        }

        foreach ($entry in $payloadEntries) {
            if (($entry.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw "The extracted MSIX payload contains a reparse point."
            }

            $relativeEntryPath = $entry.FullName.Substring($inspectionRoot.Length).TrimStart('\', '/')
            if ($relativeEntryPath.IndexOf(
                    $testCanaryMarker,
                    [System.StringComparison]::Ordinal) -ge 0) {
                throw "Test canary marker detected in a production payload path."
            }

            if ($entry.PSIsContainer -and
                $entry.Name -match '^(?i:testdata|fixtures?)$') {
                throw "Forbidden test-data directory in production payload: $relativeEntryPath"
            }

            if ($entry.Name -match '^(?i:IptvSuite\.SecretStoreSpike(?:\..*)?)$') {
                throw "Forbidden test infrastructure in production payload: $relativeEntryPath"
            }

            if ($entry.Name -match '^(?i:IptvSuite\.ProtectedCatalogSpike(?:\..*)?)$') {
                throw "Forbidden test infrastructure in production payload: $relativeEntryPath"
            }

            if ($entry.Name -match '^(?i:IptvSuite\.PackageLifecycleHarness(?:\..*)?)$') {
                throw "Forbidden test infrastructure in production payload: $relativeEntryPath"
            }

            if ($entry.Name -match '^(?i:IptvSuite\.DpapiUserBoundaryHarness(?:\..*)?)$') {
                throw "Forbidden test infrastructure in production payload: $relativeEntryPath"
            }

            if ($entry.Name -match '^(?i:IptvSuite\.CatalogCrashHarness(?:\..*)?)$') {
                throw "Forbidden test infrastructure in production payload: $relativeEntryPath"
            }

            if ($entry.Name -match '^(?i:IptvSuite\.CatalogUiAcceptanceHarness(?:\..*)?)$') {
                throw "Forbidden test infrastructure in production payload: $relativeEntryPath"
            }

            if ($entry.Name -match '^(?i:IptvSuite\.PlaybackUiAcceptanceHarness(?:\..*)?)$') {
                throw "Forbidden test infrastructure in production payload: $relativeEntryPath"
            }

            if ($entry.Name -match '^(?i:IptvSuite\.Testing(?:\..*)?)$') {
                throw "Forbidden test infrastructure in production payload: $relativeEntryPath"
            }

            if ($entry.Name -match '^(?i:IptvSuite\.PlaybackCompatibilitySpike(?:\..*)?)$') {
                throw "Forbidden test infrastructure in production payload: $relativeEntryPath"
            }

            if ($entry.Name -match '^(?i:IptvSuite\.NativePlaybackCompatibilitySpike(?:\..*)?)$') {
                throw "Forbidden test infrastructure in production payload: $relativeEntryPath"
            }
        }

        $payloadFiles = @($payloadEntries | Where-Object { -not $_.PSIsContainer })
        foreach ($file in $payloadFiles) {
            $relativePath = $file.FullName.Substring($inspectionRoot.Length).TrimStart('\', '/')
            if ($file.Name -match '(?i)(?:Tests|Testing)(?:\.[A-Za-z0-9_-]+)*\.(?:dll|exe|pdb|json)$' -or
                $file.Name -match '^(?i:MSTest(?:\..+)?|testhost(?:\..+)?|Microsoft\.VisualStudio\.TestPlatform\..+|Microsoft\.TestPlatform\..+|Microsoft\.Testing\..+)$' -or
                $file.Name -match '^(?i:fixture-manifest(?:\.schema)?\.json)$') {
                throw "Forbidden test infrastructure in production payload: $relativePath"
            }

            foreach ($pattern in $markerPatterns) {
                if (Test-FileContainsByteSequence -Path $file.FullName -Sequence $pattern.Bytes) {
                    throw "Test canary marker detected in production payload: $relativePath ($($pattern.Name))."
                }
            }
        }
    }
    finally {
        if (Test-Path -LiteralPath $inspectionRoot) {
            $cleanupPath = [System.IO.Path]::GetFullPath($inspectionRoot)
            if (-not $cleanupPath.Equals($inspectionRoot, [System.StringComparison]::OrdinalIgnoreCase) -or
                [System.IO.Path]::GetFileName($cleanupPath) -ne $inspectionLeaf -or
                ([System.IO.File]::GetAttributes($cleanupPath) -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw "Refusing unsafe MSIX inspection-directory cleanup."
            }

            Remove-Item -LiteralPath $cleanupPath -Recurse -Force
        }
    }
}

function Invoke-CleanupStep {
    param(
        [Parameter(Mandatory)]
        [AllowEmptyCollection()]
        [System.Collections.Generic.List[string]]$Failures,

        [Parameter(Mandatory)]
        [string]$Name,

        [Parameter(Mandatory)]
        [scriptblock]$Action
    )

    try {
        & $Action
    }
    catch {
        $message = $_.Exception.Message -replace '[\r\n]+', ' '
        $Failures.Add(("{0}: {1}" -f $Name, $message)) | Out-Null
    }
}

function Remove-ExactPackageOutput {
    if (-not [System.Text.RegularExpressions.Regex]::IsMatch($runId, '\A[0-9a-f]{32}\z')) {
        throw "Refusing package-output cleanup because the run id is invalid."
    }

    $resolvedArtifactRoot = [System.IO.Path]::GetFullPath($artifactRoot)
    $resolvedPackagesRoot = [System.IO.Path]::GetFullPath(
        [System.IO.Path]::Combine($resolvedArtifactRoot, 'packages'))
    $resolvedPackageOutput = [System.IO.Path]::GetFullPath($packageOutput)
    $expectedPackageOutput = [System.IO.Path]::GetFullPath(
        [System.IO.Path]::Combine($resolvedPackagesRoot, $runId))
    $packageOutputParent = [System.IO.Directory]::GetParent($resolvedPackageOutput)

    if (-not $resolvedPackageOutput.Equals(
            $expectedPackageOutput,
            [System.StringComparison]::OrdinalIgnoreCase) -or
        $null -eq $packageOutputParent -or
        -not $packageOutputParent.FullName.Equals(
            $resolvedPackagesRoot,
            [System.StringComparison]::OrdinalIgnoreCase) -or
        [System.IO.Path]::GetFileName($resolvedPackageOutput) -ne $runId) {
        throw "Refusing cleanup of an unexpected package-output directory."
    }

    if (-not (Test-Path -LiteralPath $resolvedPackageOutput)) {
        return
    }

    foreach ($protectedPath in @($resolvedArtifactRoot, $resolvedPackagesRoot, $resolvedPackageOutput)) {
        if (-not (Test-Path -LiteralPath $protectedPath -PathType Container)) {
            throw "Refusing package-output cleanup because an expected directory is missing."
        }

        if (([System.IO.File]::GetAttributes($protectedPath) -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "Refusing package-output cleanup through a reparse point."
        }
    }

    Remove-Item -LiteralPath $resolvedPackageOutput -Recurse -Force -ErrorAction Stop
}

function Remove-ExactPlaybackControlDirectory {
    if (-not [System.Text.RegularExpressions.Regex]::IsMatch($runId, '\A[0-9a-f]{32}\z')) {
        throw "Refusing playback-control cleanup because the run id is invalid."
    }

    $resolvedArtifactRoot = [System.IO.Path]::GetFullPath($artifactRoot)
    $resolvedControlRoot = [System.IO.Path]::GetFullPath($playbackControlRoot)
    $resolvedControlDirectory = [System.IO.Path]::GetFullPath($playbackControlDirectory)
    $expectedControlRoot = [System.IO.Path]::GetFullPath(
        [System.IO.Path]::Combine($resolvedArtifactRoot, 'playback-ui'))
    $expectedControlDirectory = [System.IO.Path]::GetFullPath(
        [System.IO.Path]::Combine($expectedControlRoot, $runId))
    $controlParent = [System.IO.Directory]::GetParent($resolvedControlDirectory)
    if (-not $resolvedControlRoot.Equals(
            $expectedControlRoot,
            [System.StringComparison]::OrdinalIgnoreCase) -or
        -not $resolvedControlDirectory.Equals(
            $expectedControlDirectory,
            [System.StringComparison]::OrdinalIgnoreCase) -or
        $null -eq $controlParent -or
        -not $controlParent.FullName.Equals(
            $resolvedControlRoot,
            [System.StringComparison]::OrdinalIgnoreCase) -or
        [System.IO.Path]::GetFileName($resolvedControlDirectory) -ne $runId) {
        throw "Refusing cleanup of an unexpected playback-control directory."
    }

    if (-not (Test-Path -LiteralPath $resolvedControlDirectory)) {
        return
    }

    foreach ($protectedPath in @(
            $resolvedArtifactRoot,
            $resolvedControlRoot,
            $resolvedControlDirectory)) {
        if (-not (Test-Path -LiteralPath $protectedPath -PathType Container) -or
            ([System.IO.File]::GetAttributes($protectedPath) -band
                [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "Refusing playback-control cleanup through an unsafe directory."
        }
    }

    Remove-Item -LiteralPath $resolvedControlDirectory -Recurse -Force -ErrorAction Stop
}

function Assert-ExactPlaybackControlEntries {
    param(
        [Parameter(Mandatory)]
        [AllowEmptyCollection()]
        [string[]]$AllowedNames
    )

    if (-not (Test-Path -LiteralPath $playbackControlDirectory -PathType Container) -or
        ([System.IO.File]::GetAttributes($playbackControlDirectory) -band
            [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "The playback acceptance control directory is invalid."
    }

    $entries = @(Get-ChildItem -LiteralPath $playbackControlDirectory -Force)
    if ($entries.Count -ne $AllowedNames.Count) {
        throw "The playback acceptance control directory has an invalid schema."
    }

    foreach ($entry in $entries) {
        if ($entry.PSIsContainer -or
            ($entry.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0 -or
            $AllowedNames -cnotcontains $entry.Name) {
            throw "The playback acceptance control directory has an invalid schema."
        }
    }
}

function Read-StrictPlaybackJsonTicket {
    param(
        [Parameter(Mandatory)]
        [string]$Path,

        [Parameter(Mandatory)]
        [string[]]$AllowedProperties
    )

    $resolvedPath = [System.IO.Path]::GetFullPath($Path)
    $allowedPaths = @(
        [System.IO.Path]::GetFullPath($playbackReadyPath),
        [System.IO.Path]::GetFullPath($playbackResultPath)
    )
    if ($allowedPaths -notcontains $resolvedPath -or
        -not [System.IO.Directory]::GetParent($resolvedPath).FullName.Equals(
            [System.IO.Path]::GetFullPath($playbackControlDirectory),
            [System.StringComparison]::OrdinalIgnoreCase) -or
        -not (Test-Path -LiteralPath $resolvedPath -PathType Leaf)) {
        throw "The playback acceptance ticket path is invalid."
    }

    $ticketFile = Get-Item -LiteralPath $resolvedPath -Force
    if (($ticketFile.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0 -or
        $ticketFile.Length -le 0 -or
        $ticketFile.Length -gt 4096) {
        throw "The playback acceptance ticket is invalid."
    }

    try {
        $ticket = [System.IO.File]::ReadAllText(
            $resolvedPath,
            [System.Text.Encoding]::UTF8) | ConvertFrom-Json -ErrorAction Stop
    }
    catch {
        throw "The playback acceptance ticket is not valid JSON."
    }

    if ($ticket -isnot [pscustomobject]) {
        throw "The playback acceptance ticket root is invalid."
    }

    $properties = @($ticket.PSObject.Properties)
    if ($properties.Count -ne $AllowedProperties.Count) {
        throw "The playback acceptance ticket schema is invalid."
    }

    foreach ($property in $properties) {
        if ($AllowedProperties -cnotcontains $property.Name) {
            throw "The playback acceptance ticket schema is invalid."
        }
    }
    foreach ($allowedProperty in $AllowedProperties) {
        if (@($properties.Name) -cnotcontains $allowedProperty) {
            throw "The playback acceptance ticket schema is invalid."
        }
    }

    return $ticket
}

function Write-JsonAtomically {
    param(
        [Parameter(Mandatory)]
        [object]$Value,

        [Parameter(Mandatory)]
        [string]$DestinationPath
    )

    $resolvedArtifactRoot = [System.IO.Path]::GetFullPath($artifactRoot)
    $resolvedDestination = [System.IO.Path]::GetFullPath($DestinationPath)
    $allowedDestinations = @(
        [System.IO.Path]::GetFullPath($evidencePath),
        [System.IO.Path]::GetFullPath($failureEvidencePath)
    )
    if ($allowedDestinations -notcontains $resolvedDestination -or
        -not [System.IO.Directory]::GetParent($resolvedDestination).FullName.Equals(
            $resolvedArtifactRoot,
            [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to write evidence outside the exact artifact root."
    }

    if (Test-Path -LiteralPath $resolvedDestination) {
        throw "Refusing to overwrite an existing evidence file."
    }

    $temporaryPath = "$resolvedDestination.$runId.tmp"
    if (-not [System.IO.Directory]::GetParent($temporaryPath).FullName.Equals(
            $resolvedArtifactRoot,
            [System.StringComparison]::OrdinalIgnoreCase) -or
        [System.IO.Path]::GetFileName($temporaryPath) -ne
            "$([System.IO.Path]::GetFileName($resolvedDestination)).$runId.tmp") {
        throw "Refusing to use an unexpected evidence temporary path."
    }

    if (Test-Path -LiteralPath $temporaryPath) {
        throw "The evidence temporary path already exists."
    }

    try {
        $Value | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $temporaryPath -Encoding UTF8
        [System.IO.File]::Move($temporaryPath, $resolvedDestination)
    }
    finally {
        if (Test-Path -LiteralPath $temporaryPath) {
            Remove-Item -LiteralPath $temporaryPath -Force -ErrorAction Stop
        }
    }
}

function Remove-ExactDevelopmentPackage {
    $packages = @(
        Get-AppxPackage -Name $expectedName -ErrorAction SilentlyContinue |
            Where-Object { $_.Publisher -eq $expectedPublisher }
    )

    if ($packages.Count -gt 1) {
        throw "Refusing cleanup: more than one exact development package is registered."
    }

    if ($packages.Count -eq 1) {
        Remove-AppxPackage -Package $packages[0].PackageFullName
    }
}

function Get-RuntimeDependencyPackages {
    return @(Get-AppxPackage -Name $script:expectedRuntimeDependencyName -ErrorAction Stop |
        Where-Object {
            [string]::Equals(
                $_.Name,
                $script:expectedRuntimeDependencyName,
                [System.StringComparison]::OrdinalIgnoreCase) -and
            [string]::Equals(
                $_.Publisher,
                $script:expectedRuntimeDependencyPublisher,
                [System.StringComparison]::Ordinal)
        })
}

function Get-RequiredAutomationElement {
    param(
        [Parameter(Mandatory)]
        [System.Windows.Automation.AutomationElement]$Root,

        [Parameter(Mandatory)]
        [string]$AutomationId,

        [Parameter(Mandatory)]
        [System.Windows.Automation.ControlType]$ControlType,

        [Parameter(Mandatory)]
        [string]$AccessibleName
    )

    $condition = [System.Windows.Automation.PropertyCondition]::new(
        [System.Windows.Automation.AutomationElement]::AutomationIdProperty,
        $AutomationId)
    $element = $Root.FindFirst(
        [System.Windows.Automation.TreeScope]::Descendants,
        $condition)
    if ($null -eq $element -or
        $element.Current.ControlType -ne $ControlType -or
        $element.Current.Name -ne $AccessibleName) {
        throw "The packaged catalog UI Automation contract is invalid."
    }

    return $element
}

function Get-AutomationElementById {
    param(
        [Parameter(Mandatory)]
        [System.Windows.Automation.AutomationElement]$Root,

        [Parameter(Mandatory)]
        [string]$AutomationId
    )

    $condition = [System.Windows.Automation.PropertyCondition]::new(
        [System.Windows.Automation.AutomationElement]::AutomationIdProperty,
        $AutomationId)
    return $Root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)
}

function Get-Percentile95 {
    param(
        [Parameter(Mandatory)]
        [double[]]$Samples
    )

    if ($Samples.Count -eq 0) {
        throw "At least one UI response sample is required."
    }
    $ordered = @($Samples | Sort-Object)
    $index = [Math]::Ceiling(0.95 * $ordered.Count) - 1
    return [double]$ordered[[Math]::Max(0, [int]$index)]
}

function Assert-FocusedAutomationElement {
    param(
        [Parameter(Mandatory)]
        [System.Windows.Automation.AutomationElement]$ExpectedElement,

        [Parameter(Mandatory)]
        [string]$ExpectedAutomationId,

        [switch]$RequestFocus
    )

    $deadline = (Get-Date).AddSeconds(5)
    $observedFocusTarget = "None"
    do {
        if ($RequestFocus) {
            try {
                $ExpectedElement.SetFocus()
            }
            catch {
                # UI Automation focus transfer can race foreground activation;
                # the bounded identity assertion below remains fail-closed.
            }
        }

        $focused = [System.Windows.Automation.AutomationElement]::FocusedElement
        $observedFocusTarget = if ($null -eq $focused) { "None" } else { "Other" }
        for ($depth = 0; $null -ne $focused -and $depth -lt 32; $depth++) {
            $focusedAutomationId = $focused.Current.AutomationId
            if ($focusedAutomationId -in @(
                    "CatalogSourceSelector",
                    "CatalogCategorySelector",
                    "CatalogSearchBox",
                    "CatalogChannelList")) {
                $observedFocusTarget = $focusedAutomationId
            }
            if ($focusedAutomationId -eq "PlaybackFullscreenButton") {
                $observedFocusTarget = $focusedAutomationId
            }
            if ([System.Windows.Automation.Automation]::Compare($focused, $ExpectedElement) -or
                $focusedAutomationId -eq $ExpectedAutomationId) {
                return
            }

            $focused = [System.Windows.Automation.TreeWalker]::RawViewWalker.GetParent($focused)
        }

        Start-Sleep -Milliseconds 50
    } while ((Get-Date) -lt $deadline)

    throw "The packaged catalog keyboard focus order is invalid at $ExpectedAutomationId (Observed$observedFocusTarget)."
}

function Assert-PackagedWindowForeground {
    param(
        [Parameter(Mandatory)]
        [IntPtr]$WindowHandle,

        [Parameter(Mandatory)]
        [uint32]$ExpectedProcessId
    )

    [void][IptvSuite.PackageSmoke.WindowInspector]::SetForegroundWindow($WindowHandle)
    $deadline = (Get-Date).AddSeconds(5)
    do {
        $foreground = [IptvSuite.PackageSmoke.WindowInspector]::GetForegroundWindow()
        if ($foreground -ne [IntPtr]::Zero) {
            [uint32]$ownerProcessId = 0
            [void][IptvSuite.PackageSmoke.WindowInspector]::GetWindowThreadProcessId(
                $foreground,
                [ref]$ownerProcessId)
            if ($ownerProcessId -eq $ExpectedProcessId) {
                return
            }
        }

        Start-Sleep -Milliseconds 50
    } while ((Get-Date) -lt $deadline)

    throw "The packaged catalog window did not own foreground keyboard input."
}

function Assert-PackagedProcessAlive {
    param(
        [Parameter(Mandatory)]
        [System.Diagnostics.Process]$Process
    )

    try {
        $Process.Refresh()
        if ($Process.HasExited) {
            throw "The packaged playback application exited before acceptance completed."
        }
    }
    catch {
        throw "The packaged playback application exited before acceptance completed."
    }
}

function Wait-PackagedPlaybackStatus {
    param(
        [Parameter(Mandatory)]
        [System.Diagnostics.Process]$Process,

        [Parameter(Mandatory)]
        [System.Windows.Automation.AutomationElement]$StatusElement,

        [Parameter(Mandatory)]
        [string]$ExpectedStatus,

        [int]$TimeoutSeconds = 30
    )

    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    do {
        Assert-PackagedProcessAlive -Process $Process
        if ($StatusElement.Current.Name -ceq $ExpectedStatus) {
            return
        }

        Start-Sleep -Milliseconds 100
    } while ((Get-Date) -lt $deadline)

    throw "The packaged playback UI did not reach the expected safe state."
}

function Wait-PackagedPlaybackSelection {
    param(
        [Parameter(Mandatory)]
        [System.Diagnostics.Process]$Process,

        [Parameter(Mandatory)]
        [System.Windows.Automation.AutomationElement]$StatusElement,

        [Parameter(Mandatory)]
        [System.Windows.Automation.AutomationElement]$ChannelElement,

        [Parameter(Mandatory)]
        [string]$ExpectedChannelName,

        [int]$TimeoutMilliseconds = 5000
    )

    $deadline = [DateTime]::UtcNow.AddMilliseconds($TimeoutMilliseconds)
    do {
        Assert-PackagedProcessAlive -Process $Process
        if ($StatusElement.Current.Name -ceq "Channel is playing." -and
            $ChannelElement.Current.Name -ceq $ExpectedChannelName) {
            return
        }

        Start-Sleep -Milliseconds 50
    } while ([DateTime]::UtcNow -lt $deadline)

    throw "The packaged playback switch did not reach the expected channel-bound state."
}

function Invoke-PackagedPlaybackChannelItem {
    param(
        [Parameter(Mandatory)]
        [System.Diagnostics.Process]$Process,

        [Parameter(Mandatory)]
        [System.Windows.Automation.AutomationElement]$ChannelItem,

        [Parameter(Mandatory)]
        [IntPtr]$WindowHandle,

        [Parameter(Mandatory)]
        [uint32]$ExpectedProcessId
    )

    Assert-PackagedProcessAlive -Process $Process
    $invokePatternObject = $null
    if ($ChannelItem.TryGetCurrentPattern(
            [System.Windows.Automation.InvokePattern]::Pattern,
            [ref]$invokePatternObject)) {
        ([System.Windows.Automation.InvokePattern]$invokePatternObject).Invoke()
        return
    }

    Assert-PackagedWindowForeground -WindowHandle $WindowHandle -ExpectedProcessId $ExpectedProcessId
    Assert-FocusedAutomationElement $ChannelItem "CatalogChannelList" -RequestFocus
    [IptvSuite.PackageSmoke.KeyboardInspector]::PressEnter()
}

function Wait-PackagedAutomationName {
    param(
        [Parameter(Mandatory)]
        [System.Diagnostics.Process]$Process,

        [Parameter(Mandatory)]
        [System.Windows.Automation.AutomationElement]$Element,

        [Parameter(Mandatory)]
        [string]$ExpectedName,

        [int]$TimeoutSeconds = 10
    )

    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    do {
        Assert-PackagedProcessAlive -Process $Process
        if ($Element.Current.Name -ceq $ExpectedName) {
            return
        }

        Start-Sleep -Milliseconds 100
    } while ((Get-Date) -lt $deadline)

    throw "A packaged playback control did not reach the expected safe state."
}

function Wait-PackagedAutomationElementByName {
    param(
        [Parameter(Mandatory)]
        [System.Diagnostics.Process]$Process,

        [Parameter(Mandatory)]
        [System.Windows.Automation.AutomationElement]$Root,

        [Parameter(Mandatory)]
        [string]$AutomationId,

        [Parameter(Mandatory)]
        [System.Windows.Automation.ControlType]$ControlType,

        [Parameter(Mandatory)]
        [string]$ExpectedName,

        [int]$TimeoutSeconds = 10
    )

    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    do {
        Assert-PackagedProcessAlive -Process $Process
        try {
            $element = Get-AutomationElementById `
                -Root $Root `
                -AutomationId $AutomationId
            if ($null -ne $element -and
                $element.Current.ControlType -eq $ControlType -and
                $element.Current.Name -ceq $ExpectedName) {
                return $element
            }
        }
        catch [System.Windows.Automation.ElementNotAvailableException] {
        }

        Start-Sleep -Milliseconds 100
    } while ((Get-Date) -lt $deadline)

    throw "A packaged playback automation element did not reach the expected safe state."
}

function Invoke-PackagedPlaybackControlButton {
    param(
        [Parameter(Mandatory)]
        [System.Diagnostics.Process]$Process,

        [Parameter(Mandatory)]
        [System.Windows.Automation.AutomationElement]$ButtonElement
    )

    Assert-PackagedProcessAlive -Process $Process
    if (-not $ButtonElement.Current.IsEnabled -or
        $ButtonElement.Current.ControlType -ne
            [System.Windows.Automation.ControlType]::Button) {
        throw "A packaged playback command is unavailable."
    }

    $invokePatternObject = $null
    if (-not $ButtonElement.TryGetCurrentPattern(
            [System.Windows.Automation.InvokePattern]::Pattern,
            [ref]$invokePatternObject)) {
        throw "A packaged playback command has no InvokePattern."
    }

    ([System.Windows.Automation.InvokePattern]$invokePatternObject).Invoke()
}

function Invoke-PackagedPlaybackButton {
    param(
        [Parameter(Mandatory)]
        [System.Diagnostics.Process]$Process,

        [Parameter(Mandatory)]
        [System.Windows.Automation.AutomationElement]$ButtonElement,

        [Parameter(Mandatory)]
        [System.Windows.Automation.AutomationElement]$StatusElement,

        [Parameter(Mandatory)]
        [string]$ExpectedStatus
    )

    Invoke-PackagedPlaybackControlButton `
        -Process $Process `
        -ButtonElement $ButtonElement
    Wait-PackagedPlaybackStatus `
        -Process $Process `
        -StatusElement $StatusElement `
        -ExpectedStatus $ExpectedStatus
}

try {
    foreach ($staleEvidencePath in @($evidencePath, $failureEvidencePath)) {
        if (Test-Path -LiteralPath $staleEvidencePath) {
            Remove-Item -LiteralPath $staleEvidencePath -Force -ErrorAction Stop
        }
    }

    $currentIdentity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $currentPrincipal = [Security.Principal.WindowsPrincipal]::new($currentIdentity)
    if (-not $currentPrincipal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw "Run this smoke test from an elevated PowerShell session so the temporary public certificate can be trusted and removed."
    }

    $enableLua = Get-ItemPropertyValue `
        -LiteralPath "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System" `
        -Name "EnableLUA" `
        -ErrorAction Stop
    if ([int]$enableLua -ne 1) {
        throw "Package activation requires the Windows app-model UAC service to be enabled."
    }

    $expectedSdk = (Get-Content -Raw (Join-Path $repositoryRoot "global.json") | ConvertFrom-Json).sdk.version
    $actualSdk = (& $DotNetPath --version).Trim()
    if ($LASTEXITCODE -ne 0 -or $actualSdk -ne $expectedSdk) {
        throw "Expected .NET SDK $expectedSdk, received '$actualSdk'."
    }

    [xml]$sourceManifest = Get-Content -Raw $sourceManifestPath
    Assert-ManifestPolicy -Manifest $sourceManifest

    if (Get-ChildItem -Path (Join-Path $repositoryRoot "apps") -Filter "Package.StoreAssociation.xml" -Recurse -File) {
        throw "Package.StoreAssociation.xml is forbidden for the disposable M1 identity."
    }

    New-Item -ItemType Directory -Path $artifactRoot -Force | Out-Null
    New-Item -ItemType Directory -Path $packageOutput -Force | Out-Null

    $certificate = New-SelfSignedCertificate `
        -Type Custom `
        -Subject $expectedPublisher `
        -CertStoreLocation "Cert:\CurrentUser\My" `
        -KeyAlgorithm RSA `
        -KeyLength 2048 `
        -HashAlgorithm SHA256 `
        -KeyExportPolicy NonExportable `
        -KeyUsage DigitalSignature `
        -NotAfter (Get-Date).AddDays(7) `
        -TextExtension @(
            "2.5.29.37={text}1.3.6.1.5.5.7.3.3",
            "2.5.29.19={text}"
        )

    if (-not $certificate.HasPrivateKey -or $certificate.Subject -ne $expectedPublisher) {
        throw "The local signing certificate does not match the manifest publisher."
    }

    $enhancedKeyUsageExtension = $certificate.Extensions |
        Where-Object { $_.Oid.Value -eq "2.5.29.37" } |
        Select-Object -First 1
    $enhancedKeyUsage = [System.Security.Cryptography.X509Certificates.X509EnhancedKeyUsageExtension]$enhancedKeyUsageExtension
    $codeSigningUsage = @($enhancedKeyUsage.EnhancedKeyUsages | ForEach-Object { $_.Value })
    if ($codeSigningUsage -notcontains "1.3.6.1.5.5.7.3.3") {
        throw "The local signing certificate is missing the code-signing EKU."
    }

    Export-Certificate -Cert $certificate -FilePath $publicCertificatePath | Out-Null
    Import-Certificate -FilePath $publicCertificatePath -CertStoreLocation "Cert:\LocalMachine\TrustedPeople" | Out-Null

    $msBuildEnvironment.PackageCertificateThumbprint = $certificate.Thumbprint
    foreach ($entry in $msBuildEnvironment.GetEnumerator()) {
        $environmentBackup[$entry.Key] = [Environment]::GetEnvironmentVariable($entry.Key, "Process")
        [Environment]::SetEnvironmentVariable($entry.Key, $entry.Value, "Process")
    }

    & $DotNetPath build $projectPath -c $Configuration -p:Platform=x64 --no-restore
    if ($LASTEXITCODE -ne 0) {
        throw "Signed MSIX build failed."
    }

    & $DotNetPath build $catalogUiHarnessProjectPath -c $Configuration -p:Platform=x64 `
        --no-restore --nologo -m:1 -nr:false
    if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $catalogUiHarnessAssemblyPath -PathType Leaf)) {
        throw "The catalog UI acceptance harness build failed."
    }

    & $DotNetPath build $playbackUiHarnessProjectPath -c $Configuration -p:Platform=x64 `
        --no-restore --nologo -m:1 -nr:false
    if ($LASTEXITCODE -ne 0 -or
        -not (Test-Path -LiteralPath $playbackUiHarnessAssemblyPath -PathType Leaf)) {
        throw "The playback UI acceptance harness build failed."
    }

    $packages = @(
        Get-ChildItem -Path $packageOutput -Filter "IptvSuite.Windows_*.msix" -Recurse -File |
            Where-Object { $_.FullName -notmatch "[\\/]Dependencies[\\/]" }
    )
    if ($packages.Count -ne 1) {
        throw "Expected exactly one x64 MSIX, found $($packages.Count)."
    }

    $runtimeDependencies = @(
        Get-ChildItem -Path $packageOutput -Filter "Microsoft.WindowsAppRuntime.2.msix" -Recurse -File |
            Where-Object { $_.Directory.Name -eq "x64" }
    )
    if ($runtimeDependencies.Count -ne 1) {
        throw "Expected exactly one x64 Windows App Runtime dependency package."
    }

    $signature = Get-AuthenticodeSignature -FilePath $packages[0].FullName
    if ($null -eq $signature.SignerCertificate -or $signature.SignerCertificate.Thumbprint -ne $certificate.Thumbprint) {
        throw "The generated MSIX signer does not match the ephemeral certificate."
    }

    if ($signature.Status -in @("HashMismatch", "NotSigned")) {
        throw "The generated MSIX signature failed integrity validation: $($signature.Status)."
    }

    $packageSha256 = (Get-FileHash -LiteralPath $packages[0].FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    Assert-ProductionPackagePayload -PackagePath $packages[0].FullName

    $runtimeDependencyPackagesBefore = @(Get-RuntimeDependencyPackages)
    $compatibleRuntimeDependencyRegistered = @($runtimeDependencyPackagesBefore |
        Where-Object {
            -not [string]::IsNullOrWhiteSpace([string]$_.PackageFullName) -and
            [string]::Equals(
                [string]$_.PackageFamilyName,
                "$($expectedRuntimeDependencyName)_$expectedRuntimeDependencyPublisherId",
                [System.StringComparison]::OrdinalIgnoreCase) -and
            [string]::Equals(
                [string]$_.Architecture,
                "X64",
                [System.StringComparison]::Ordinal) -and
            $_.IsFramework -eq $true -and
            [version]$_.Version -ge [version]$expectedRuntimeDependencyVersion
        }).Count -gt 0

    Remove-ExactDevelopmentPackage
    $installAttempted = $true
    if ($compatibleRuntimeDependencyRegistered) {
        Write-Host "Compatible Windows App Runtime dependency is already registered; package install will reuse it."
        Add-AppxPackage -Path $packages[0].FullName
        $windowsAppRuntimeDisposition = "ReusedRegisteredFramework"
    }
    else {
        Add-AppxPackage -Path $packages[0].FullName -DependencyPath $runtimeDependencies[0].FullName
        $windowsAppRuntimeDisposition = "InstalledLockedDependency"
    }

    $installedPackages = @(
        Get-AppxPackage -Name $expectedName |
            Where-Object { $_.Publisher -eq $expectedPublisher }
    )
    if ($installedPackages.Count -ne 1) {
        throw "Expected exactly one installed development package."
    }

    $installedPackage = $installedPackages[0]
    if ($installedPackage.Architecture -ne "X64") {
        throw "Expected an x64 package, received $($installedPackage.Architecture)."
    }

    $packageFamilyName = $installedPackage.PackageFamilyName
    $catalogDatabasePath = Join-Path $env:LOCALAPPDATA `
        "Packages\$packageFamilyName\LocalCache\Catalog\v2\catalog.db"
    & $DotNetPath $catalogUiHarnessAssemblyPath seed $catalogDatabasePath 50000
    if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $catalogDatabasePath -PathType Leaf)) {
        throw "The disposable 50k packaged catalog seed failed."
    }
    $catalog50kSeedVerified = $true

    $installedManifest = $installedPackage | Get-AppxPackageManifest
    [xml]$installedManifestXml = $installedManifest.Package.OuterXml
    Assert-BuiltManifestPolicy -Manifest $installedManifestXml

    $existingProcesses = @(Get-Process -Name "IptvSuite.Windows" -ErrorAction SilentlyContinue)
    if ($existingProcesses.Count -ne 0) {
        throw "IptvSuite.Windows is already running; refusing an ambiguous launch smoke."
    }

    $aumid = "$($installedPackage.PackageFamilyName)!$expectedApplicationId"
    $activationProcessId = [IptvSuite.PackageSmoke.PackagedApplicationActivator]::Activate($aumid)
    $launchedProcess = Get-Process -Id $activationProcessId -ErrorAction SilentlyContinue
    if ($null -eq $launchedProcess) {
        throw "The packaged application exited before its process could be observed."
    }
    try {
        # Cache the native handle while the PID is live. Windows PowerShell 5.1
        # otherwise exposes ExitCode as null after an attached process exits.
        $null = $launchedProcess.Handle
    }
    catch {
        throw "The packaged application exited before its process handle could be retained."
    }
    $launchedProcess.Refresh()
    if ($launchedProcess.HasExited) {
        throw ("The packaged application exited during activation (exit code 0x{0:X8})." -f [int]$launchedProcess.ExitCode)
    }
    if ($launchedProcess.ProcessName -ne "IptvSuite.Windows") {
        throw "Package activation returned an unexpected process."
    }

    $launchDeadline = (Get-Date).AddSeconds(30)
    $visibleWindow = $false
    do {
        $launchedProcess.Refresh()
        if ($launchedProcess.HasExited) {
            throw ("The packaged application exited before creating a visible window (exit code 0x{0:X8})." -f [int]$launchedProcess.ExitCode)
        }

        $windowHandle = $launchedProcess.MainWindowHandle
        if ($windowHandle -ne [IntPtr]::Zero -and
            [IptvSuite.PackageSmoke.WindowInspector]::IsWindowVisible($windowHandle)) {
            [uint32]$windowOwnerProcessId = 0
            [void][IptvSuite.PackageSmoke.WindowInspector]::GetWindowThreadProcessId(
                $windowHandle,
                [ref]$windowOwnerProcessId)
            if ($windowOwnerProcessId -eq [uint32]$activationProcessId) {
                $visibleWindow = $true
                break
            }
        }

        Start-Sleep -Milliseconds 250
    } while ((Get-Date) -lt $launchDeadline)

    if (-not $visibleWindow) {
        throw "The packaged application remained running but did not create a visible window before the launch deadline."
    }

    $automationRoot = [System.Windows.Automation.AutomationElement]::FromHandle($windowHandle)
    if ($null -eq $automationRoot) {
        throw "The packaged application window has no UI Automation root."
    }
    $sourceElement = Get-RequiredAutomationElement $automationRoot "CatalogSourceSelector" `
        ([System.Windows.Automation.ControlType]::ComboBox) "Playlist source"
    $categoryElement = Get-RequiredAutomationElement $automationRoot "CatalogCategorySelector" `
        ([System.Windows.Automation.ControlType]::ComboBox) "Channel category"
    $searchElement = Get-RequiredAutomationElement $automationRoot "CatalogSearchBox" `
        ([System.Windows.Automation.ControlType]::Group) "Search channels"
    $channelListElement = Get-RequiredAutomationElement $automationRoot "CatalogChannelList" `
        ([System.Windows.Automation.ControlType]::List) "Channels"
    $statusElement = Get-AutomationElementById $automationRoot "CatalogStatusText"
    if ($null -eq $statusElement) {
        throw "The packaged catalog status automation element is missing."
    }
    $catalogUiaContractVerified = $true

    $expectedCatalogStatus = "Showing 1$([char]0x2013)200 of 50000 channels."
    $catalogReadyDeadline = (Get-Date).AddSeconds(30)
    while ($statusElement.Current.Name -ne $expectedCatalogStatus -and
        (Get-Date) -lt $catalogReadyDeadline) {
        Start-Sleep -Milliseconds 100
    }
    if ($statusElement.Current.Name -ne $expectedCatalogStatus) {
        throw "The packaged application did not expose the seeded 50k catalog page."
    }

    $listItemCondition = [System.Windows.Automation.PropertyCondition]::new(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
        [System.Windows.Automation.ControlType]::ListItem)
    $realizedDeadline = (Get-Date).AddSeconds(10)
    do {
        $realizedItems = $channelListElement.FindAll(
            [System.Windows.Automation.TreeScope]::Descendants,
            $listItemCondition)
        $catalogRealizedContainerCount = $realizedItems.Count
        if ($catalogRealizedContainerCount -gt 0) { break }
        Start-Sleep -Milliseconds 100
    } while ((Get-Date) -lt $realizedDeadline)
    if ($catalogRealizedContainerCount -lt 1 -or $catalogRealizedContainerCount -gt 300) {
        throw "The packaged catalog realized-container bound failed."
    }
    $catalogRealizedContainerBoundVerified = $true

    $editCondition = [System.Windows.Automation.PropertyCondition]::new(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
        [System.Windows.Automation.ControlType]::Edit)
    $searchEdit = $searchElement.FindFirst(
        [System.Windows.Automation.TreeScope]::Descendants,
        $editCondition)
    $valuePatternObject = $null
    if ($null -eq $searchEdit -or
        -not $searchEdit.TryGetCurrentPattern(
            [System.Windows.Automation.ValuePattern]::Pattern,
            [ref]$valuePatternObject)) {
        throw "The packaged catalog search value pattern is unavailable."
    }
    $valuePattern = [System.Windows.Automation.ValuePattern]$valuePatternObject
    $inputSamples = [System.Collections.Generic.List[double]]::new()
    for ($sample = 0; $sample -lt 20; $sample++) {
        $value = if (($sample % 2) -eq 0) { "Synthetic" } else { "channel" }
        $watch = [System.Diagnostics.Stopwatch]::StartNew()
        $valuePattern.SetValue($value)
        $watch.Stop()
        $inputSamples.Add($watch.Elapsed.TotalMilliseconds)
    }
    $valuePattern.SetValue("")
    $catalogInputResponseP95Milliseconds = Get-Percentile95 $inputSamples.ToArray()
    if ($catalogInputResponseP95Milliseconds -gt 100.0) {
        throw "The packaged catalog input response budget failed."
    }

    $catalogRestoredDeadline = (Get-Date).AddSeconds(10)
    while ($statusElement.Current.Name -ne $expectedCatalogStatus -and
        (Get-Date) -lt $catalogRestoredDeadline) {
        Start-Sleep -Milliseconds 100
    }
    if ($statusElement.Current.Name -ne $expectedCatalogStatus) {
        throw "The packaged catalog did not settle after the input-response probe."
    }

    $focusReadyWatch = [System.Diagnostics.Stopwatch]::StartNew()
    $focusStableWatch = $null
    do {
        $focusControlsReady =
            $statusElement.Current.Name -eq $expectedCatalogStatus -and
            $sourceElement.Current.IsEnabled -and
            $sourceElement.Current.IsKeyboardFocusable -and
            $categoryElement.Current.IsEnabled -and
            $categoryElement.Current.IsKeyboardFocusable
        if ($focusControlsReady) {
            if ($null -eq $focusStableWatch) {
                $focusStableWatch = [System.Diagnostics.Stopwatch]::StartNew()
            }
            if ($focusStableWatch.ElapsedMilliseconds -ge 750) {
                break
            }
        }
        else {
            $focusStableWatch = $null
        }

        Start-Sleep -Milliseconds 50
    } while ($focusReadyWatch.Elapsed -lt [TimeSpan]::FromSeconds(15))
    if ($null -eq $focusStableWatch -or $focusStableWatch.ElapsedMilliseconds -lt 750) {
        throw "The packaged catalog controls did not remain keyboard-focusable after the input-response probe."
    }
    Assert-PackagedWindowForeground $windowHandle ([uint32]$activationProcessId)
    Assert-FocusedAutomationElement $sourceElement "CatalogSourceSelector" -RequestFocus
    [IptvSuite.PackageSmoke.KeyboardInspector]::PressTab()
    Start-Sleep -Milliseconds 150
    Assert-FocusedAutomationElement $categoryElement "CatalogCategorySelector"
    [IptvSuite.PackageSmoke.KeyboardInspector]::PressTab()
    Start-Sleep -Milliseconds 150
    Assert-FocusedAutomationElement $searchElement "CatalogSearchBox"
    $catalogKeyboardFocusOrderVerified = $true
    $scrollFocusItem = $channelListElement.FindFirst(
        [System.Windows.Automation.TreeScope]::Descendants,
        $listItemCondition)
    if ($null -eq $scrollFocusItem) {
        throw "The packaged catalog has no realized item for the scroll probe."
    }
    Assert-FocusedAutomationElement $scrollFocusItem "CatalogChannelList" -RequestFocus
    [IptvSuite.PackageSmoke.DwmFrameSampler]::Start()
    $frameResult = $null
    try {
        for ($frameInput = 0; $frameInput -lt 240; $frameInput++) {
            if (($frameInput % 2) -eq 0) {
                [IptvSuite.PackageSmoke.KeyboardInspector]::PressPageDown()
            }
            else {
                [IptvSuite.PackageSmoke.KeyboardInspector]::PressPageUp()
            }
            Start-Sleep -Milliseconds 16
        }
    }
    finally {
        $frameResult = [IptvSuite.PackageSmoke.DwmFrameSampler]::Stop()
    }
    $catalogFrameP95Milliseconds = $frameResult.P95Milliseconds
    $catalogFrameMaximumMilliseconds = $frameResult.MaximumMilliseconds
    $catalogDroppedFramePercent = $frameResult.DroppedPercent
    $catalogFrameIntervalCount = $frameResult.IntervalCount
    if ($catalogFrameP95Milliseconds -gt 33.3 -or
        $catalogDroppedFramePercent -ge 1.0 -or
        $catalogFrameMaximumMilliseconds -gt 200.0) {
        throw "The packaged catalog DWM frame budget failed."
    }

    Start-Sleep -Seconds 2
    $launchedProcess.Refresh()
    if ($launchedProcess.HasExited) {
        throw ("The packaged application exited during the launch stability interval (exit code 0x{0:X8})." -f [int]$launchedProcess.ExitCode)
    }

    $protectedStoreDirectoryExists = $false
    $protectedStoreAttributes = [System.IO.FileAttributes]0
    try {
        $protectedStorePath = Join-Path `
            $env:LOCALAPPDATA `
            "Packages\$packageFamilyName\LocalCache\ProtectedStore\v2"
        $protectedStoreDirectoryExists = Test-Path `
            -LiteralPath $protectedStorePath `
            -PathType Container
        if ($protectedStoreDirectoryExists) {
            $protectedStoreAttributes = [System.IO.File]::GetAttributes($protectedStorePath)
        }
    }
    catch {
        throw "The packaged protected-store directory could not be inspected safely."
    }
    if (-not $protectedStoreDirectoryExists) {
        throw "The packaged application did not initialize its protected-store directory."
    }
    if (($protectedStoreAttributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "The packaged protected-store directory must not be a reparse point."
    }
    $protectedStoreDirectoryInitialized = $true

    if (-not $launchedProcess.CloseMainWindow()) {
        throw "The application rejected a normal window-close request."
    }

    if (-not $launchedProcess.WaitForExit(10000)) {
        throw "The application did not exit after a normal window-close request."
    }
    $launchedProcess.Refresh()
    $exitCode = $launchedProcess.ExitCode
    if ($null -eq $exitCode) {
        throw "The application exit code could not be read after the normal window-close request."
    }
    if ([int]$exitCode -ne 0) {
        throw ("The application returned a non-zero exit code after the normal window-close request (exit code 0x{0:X8})." -f [int]$exitCode)
    }

    $launchedProcess.Dispose()
    $launchedProcess = $null

    if (-not (Test-Path -LiteralPath $playbackFixtureRoot -PathType Container) -or
        ([System.IO.File]::GetAttributes($playbackFixtureRoot) -band
            [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "The committed playback acceptance fixture root is invalid."
    }
    if (Test-Path -LiteralPath $playbackControlDirectory) {
        throw "The playback acceptance control directory already exists."
    }

    New-Item -ItemType Directory -Path $playbackControlRoot -Force | Out-Null
    if (([System.IO.File]::GetAttributes($artifactRoot) -band
            [System.IO.FileAttributes]::ReparsePoint) -ne 0 -or
        ([System.IO.File]::GetAttributes($playbackControlRoot) -band
            [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "The playback acceptance control root is invalid."
    }
    New-Item -ItemType Directory -Path $playbackControlDirectory | Out-Null
    Assert-ExactPlaybackControlEntries -AllowedNames @()

    $playbackHarnessArgumentValues = @(
        $playbackUiHarnessAssemblyPath,
        "serve-and-seed",
        $catalogDatabasePath,
        $protectedStorePath,
        $playbackFixtureRoot,
        $playbackControlDirectory
    )
    foreach ($argumentValue in $playbackHarnessArgumentValues) {
        if ([string]::IsNullOrWhiteSpace($argumentValue) -or $argumentValue.Contains('"')) {
            throw "A playback acceptance harness argument is invalid."
        }
    }
    $playbackHarnessArguments = ($playbackHarnessArgumentValues |
        ForEach-Object { '"' + $_ + '"' }) -join ' '
    try {
        $playbackHarnessProcess = Start-Process `
            -FilePath $DotNetPath `
            -ArgumentList $playbackHarnessArguments `
            -WorkingDirectory $repositoryRoot `
            -WindowStyle Hidden `
            -PassThru
    }
    catch {
        throw "The playback acceptance harness could not be started."
    }
    try {
        $null = $playbackHarnessProcess.Handle
    }
    catch {
        throw "The playback acceptance harness exited before its process handle could be retained."
    }

    $playbackReadyDeadline = (Get-Date).AddSeconds(60)
    do {
        $playbackHarnessProcess.Refresh()
        if ($playbackHarnessProcess.HasExited) {
            throw "The playback acceptance harness exited before publishing readiness."
        }
        if ((Test-Path -LiteralPath $playbackReadyPath -PathType Leaf) -and
            (Test-Path -LiteralPath $playbackPublicCertificatePath -PathType Leaf)) {
            $playbackHarnessReady = $true
            break
        }

        Start-Sleep -Milliseconds 100
    } while ((Get-Date) -lt $playbackReadyDeadline)
    if (-not $playbackHarnessReady) {
        throw "The playback acceptance harness did not publish readiness before the deadline."
    }

    Assert-ExactPlaybackControlEntries -AllowedNames @("loopback.cer", "ready.json")
    $readyTicket = Read-StrictPlaybackJsonTicket `
        -Path $playbackReadyPath `
        -AllowedProperties @("IsReady", "SeedCompleted", "CertificateThumbprint")
    if ($readyTicket.IsReady -isnot [bool] -or
        $readyTicket.SeedCompleted -isnot [bool] -or
        $readyTicket.CertificateThumbprint -isnot [string] -or
        -not $readyTicket.IsReady -or
        -not $readyTicket.SeedCompleted -or
        -not [System.Text.RegularExpressions.Regex]::IsMatch(
            $readyTicket.CertificateThumbprint,
            '\A[0-9A-F]{40}\z')) {
        throw "The playback acceptance readiness ticket is invalid."
    }

    $playbackLoopbackCertificateThumbprint = $readyTicket.CertificateThumbprint
    try {
        $playbackLoopbackCertificate = [System.Security.Cryptography.X509Certificates.X509Certificate2]::new(
            $playbackPublicCertificatePath)
    }
    catch {
        throw "The playback acceptance public certificate is invalid."
    }
    $now = Get-Date
    if ($playbackLoopbackCertificate.HasPrivateKey -or
        $playbackLoopbackCertificate.Subject -cne $expectedPlaybackCertificateSubject -or
        $playbackLoopbackCertificate.Issuer -cne $expectedPlaybackCertificateSubject -or
        $playbackLoopbackCertificate.Thumbprint -cne $playbackLoopbackCertificateThumbprint -or
        $playbackLoopbackCertificate.NotBefore -gt $now -or
        $playbackLoopbackCertificate.NotAfter -le $now) {
        throw "The playback acceptance public certificate does not match readiness."
    }

    $serverAuthenticationExtensions = @(
        $playbackLoopbackCertificate.Extensions |
            Where-Object { $_.Oid.Value -eq "2.5.29.37" }
    )
    if ($serverAuthenticationExtensions.Count -ne 1) {
        throw "The playback acceptance public certificate usage is invalid."
    }
    $serverAuthenticationExtension =
        [System.Security.Cryptography.X509Certificates.X509EnhancedKeyUsageExtension]$serverAuthenticationExtensions[0]
    $serverAuthenticationUsages = @(
        $serverAuthenticationExtension.EnhancedKeyUsages |
            ForEach-Object { $_.Value }
    )
    if (-not $serverAuthenticationExtension.Critical -or
        $serverAuthenticationUsages.Count -ne 1 -or
        $serverAuthenticationUsages[0] -cne "1.3.6.1.5.5.7.3.1") {
        throw "The playback acceptance public certificate usage is invalid."
    }

    $basicConstraintExtensions = @(
        $playbackLoopbackCertificate.Extensions |
            Where-Object { $_.Oid.Value -eq "2.5.29.19" }
    )
    $keyUsageExtensions = @(
        $playbackLoopbackCertificate.Extensions |
            Where-Object { $_.Oid.Value -eq "2.5.29.15" }
    )
    if ($basicConstraintExtensions.Count -ne 1 -or
        $keyUsageExtensions.Count -ne 1) {
        throw "The playback acceptance public certificate constraints are invalid."
    }
    $basicConstraintExtension =
        [System.Security.Cryptography.X509Certificates.X509BasicConstraintsExtension]$basicConstraintExtensions[0]
    $keyUsageExtension =
        [System.Security.Cryptography.X509Certificates.X509KeyUsageExtension]$keyUsageExtensions[0]
    if (-not $basicConstraintExtension.Critical -or
        $basicConstraintExtension.CertificateAuthority -or
        -not $keyUsageExtension.Critical -or
        $keyUsageExtension.KeyUsages -ne
            [System.Security.Cryptography.X509Certificates.X509KeyUsageFlags]::DigitalSignature) {
        throw "The playback acceptance public certificate constraints are invalid."
    }

    $playbackRootCertificatePath =
        "Cert:\LocalMachine\Root\$playbackLoopbackCertificateThumbprint"
    if (Test-Path -LiteralPath $playbackRootCertificatePath) {
        throw "The exact playback acceptance certificate is already trusted."
    }
    $playbackLoopbackCertificateImported = $true
    try {
        $importedPlaybackCertificates = @(
            Import-Certificate `
                -FilePath $playbackPublicCertificatePath `
                -CertStoreLocation "Cert:\LocalMachine\Root"
        )
    }
    catch {
        throw "The playback acceptance public certificate could not be trusted."
    }
    if ($importedPlaybackCertificates.Count -ne 1 -or
        $importedPlaybackCertificates[0].Thumbprint -cne
            $playbackLoopbackCertificateThumbprint) {
        throw "The playback acceptance public certificate import is invalid."
    }

    $existingProcesses = @(Get-Process -Name "IptvSuite.Windows" -ErrorAction SilentlyContinue)
    if ($existingProcesses.Count -ne 0) {
        throw "IptvSuite.Windows is already running; refusing an ambiguous playback launch."
    }

    $playbackActivationProcessId =
        [IptvSuite.PackageSmoke.PackagedApplicationActivator]::Activate($aumid)
    $launchedProcess = Get-Process -Id $playbackActivationProcessId -ErrorAction SilentlyContinue
    if ($null -eq $launchedProcess) {
        throw "The packaged playback application exited before its process could be observed."
    }
    try {
        $null = $launchedProcess.Handle
    }
    catch {
        throw "The packaged playback application exited before its process handle could be retained."
    }
    Assert-PackagedProcessAlive -Process $launchedProcess
    if ($launchedProcess.ProcessName -ne "IptvSuite.Windows") {
        throw "Playback package activation returned an unexpected process."
    }

    $playbackLaunchDeadline = (Get-Date).AddSeconds(30)
    $playbackWindowVisible = $false
    do {
        Assert-PackagedProcessAlive -Process $launchedProcess
        $playbackWindowHandle = $launchedProcess.MainWindowHandle
        if ($playbackWindowHandle -ne [IntPtr]::Zero -and
            [IptvSuite.PackageSmoke.WindowInspector]::IsWindowVisible($playbackWindowHandle)) {
            [uint32]$playbackWindowOwnerProcessId = 0
            [void][IptvSuite.PackageSmoke.WindowInspector]::GetWindowThreadProcessId(
                $playbackWindowHandle,
                [ref]$playbackWindowOwnerProcessId)
            if ($playbackWindowOwnerProcessId -eq [uint32]$playbackActivationProcessId) {
                $playbackWindowVisible = $true
                break
            }
        }

        Start-Sleep -Milliseconds 250
    } while ((Get-Date) -lt $playbackLaunchDeadline)
    if (-not $playbackWindowVisible) {
        throw "The packaged playback application did not create a visible window."
    }

    $playbackAutomationRoot =
        [System.Windows.Automation.AutomationElement]::FromHandle($playbackWindowHandle)
    if ($null -eq $playbackAutomationRoot) {
        throw "The packaged playback application has no UI Automation root."
    }
    $playbackSourceElement = Get-RequiredAutomationElement `
        $playbackAutomationRoot `
        "CatalogSourceSelector" `
        ([System.Windows.Automation.ControlType]::ComboBox) `
        "Playlist source"
    $playbackChannelListElement = Get-RequiredAutomationElement `
        $playbackAutomationRoot `
        "CatalogChannelList" `
        ([System.Windows.Automation.ControlType]::List) `
        "Channels"
    $playbackStatusElement = Get-AutomationElementById `
        $playbackAutomationRoot `
        "PlaybackStatusText"
    if ($null -eq $playbackStatusElement -or
        $playbackStatusElement.Current.ControlType -ne
            [System.Windows.Automation.ControlType]::Text) {
        throw "The packaged playback status automation element is invalid."
    }
    $playbackCurrentChannelElement = Get-AutomationElementById `
        $playbackAutomationRoot `
        "PlaybackChannelText"
    if ($null -eq $playbackCurrentChannelElement -or
        $playbackCurrentChannelElement.Current.ControlType -ne
            [System.Windows.Automation.ControlType]::Text -or
        $playbackCurrentChannelElement.Current.Name -cne "No channel selected.") {
        throw "The packaged current playback channel automation element is invalid."
    }
    $playButtonElement = Get-RequiredAutomationElement `
        $playbackAutomationRoot `
        "PlaybackPlayButton" `
        ([System.Windows.Automation.ControlType]::Button) `
        "Play channel"
    $pauseButtonElement = Get-RequiredAutomationElement `
        $playbackAutomationRoot `
        "PlaybackPauseButton" `
        ([System.Windows.Automation.ControlType]::Button) `
        "Pause channel"
    $stopButtonElement = Get-RequiredAutomationElement `
        $playbackAutomationRoot `
        "PlaybackStopButton" `
        ([System.Windows.Automation.ControlType]::Button) `
        "Stop channel"
    $volumeDownButtonElement = Get-RequiredAutomationElement `
        $playbackAutomationRoot `
        "PlaybackVolumeDownButton" `
        ([System.Windows.Automation.ControlType]::Button) `
        "Decrease playback volume"
    $volumeUpButtonElement = Get-RequiredAutomationElement `
        $playbackAutomationRoot `
        "PlaybackVolumeUpButton" `
        ([System.Windows.Automation.ControlType]::Button) `
        "Increase playback volume"
    $muteButtonElement = Get-RequiredAutomationElement `
        $playbackAutomationRoot `
        "PlaybackMuteButton" `
        ([System.Windows.Automation.ControlType]::Button) `
        "Mute playback"
    $aspectModeButtonElement = Get-RequiredAutomationElement `
        $playbackAutomationRoot `
        "PlaybackAspectModeButton" `
        ([System.Windows.Automation.ControlType]::Button) `
        "Use fill aspect mode"
    $fullscreenButtonElement = Get-RequiredAutomationElement `
        $playbackAutomationRoot `
        "PlaybackFullscreenButton" `
        ([System.Windows.Automation.ControlType]::Button) `
        "Enter fullscreen"
    $volumeTextElement = Get-AutomationElementById `
        $playbackAutomationRoot `
        "PlaybackVolumeText"
    if ($null -eq $volumeTextElement -or
        $volumeTextElement.Current.ControlType -ne
            [System.Windows.Automation.ControlType]::Text -or
        $volumeTextElement.Current.Name -cne "Volume 100%") {
        throw "The packaged playback volume status automation element is invalid."
    }

    $sourceSelectionPatternObject = $null
    if (-not $playbackSourceElement.TryGetCurrentPattern(
            [System.Windows.Automation.SelectionPattern]::Pattern,
            [ref]$sourceSelectionPatternObject)) {
        throw "The packaged playback source selector has no SelectionPattern."
    }
    $sourceSelectionPattern =
        [System.Windows.Automation.SelectionPattern]$sourceSelectionPatternObject
    $sourceExpandPatternObject = $null
    if (-not $playbackSourceElement.TryGetCurrentPattern(
            [System.Windows.Automation.ExpandCollapsePattern]::Pattern,
            [ref]$sourceExpandPatternObject)) {
        throw "The packaged playback source selector has no ExpandCollapsePattern."
    }
    $sourceExpandPattern =
        [System.Windows.Automation.ExpandCollapsePattern]$sourceExpandPatternObject
    $sourceExpandPattern.Expand()

    $playbackListItemCondition = [System.Windows.Automation.PropertyCondition]::new(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
        [System.Windows.Automation.ControlType]::ListItem)
    $sourceItemsDeadline = (Get-Date).AddSeconds(10)
    do {
        Assert-PackagedProcessAlive -Process $launchedProcess
        $sourceItems = $playbackSourceElement.FindAll(
            [System.Windows.Automation.TreeScope]::Descendants,
            $playbackListItemCondition)
        if ($sourceItems.Count -eq 2) {
            break
        }

        Start-Sleep -Milliseconds 100
    } while ((Get-Date) -lt $sourceItemsDeadline)
    if ($sourceItems.Count -ne 2) {
        throw "The packaged playback catalog did not expose exactly two sources."
    }

    $playbackSourceItem = $null
    $catalogSourceItem = $null
    for ($sourceItemIndex = 0; $sourceItemIndex -lt $sourceItems.Count; $sourceItemIndex++) {
        $sourceItem = $sourceItems[$sourceItemIndex]
        if ($sourceItem.Current.Name -ceq $expectedPlaybackSourceName) {
            if ($null -ne $playbackSourceItem) {
                throw "The packaged playback source list contains a duplicate acceptance source."
            }
            $playbackSourceItem = $sourceItem
        }
        elseif ($sourceItem.Current.Name -ceq $expectedCatalogSourceName) {
            if ($null -ne $catalogSourceItem) {
                throw "The packaged playback source list contains a duplicate catalog source."
            }
            $catalogSourceItem = $sourceItem
        }
        else {
            throw "The packaged playback source list contains an unexpected source."
        }
    }
    if ($null -eq $playbackSourceItem -or $null -eq $catalogSourceItem) {
        throw "The packaged playback source list is incomplete."
    }

    $selectedSources = @($sourceSelectionPattern.Current.GetSelection())
    $playbackSourceSelected =
        $selectedSources.Count -eq 1 -and
        $selectedSources[0].Current.Name -ceq $expectedPlaybackSourceName
    if (-not $playbackSourceSelected) {
        $sourceSelectionItemPatternObject = $null
        $selectedWithUia = $false
        try {
            if ($playbackSourceItem.TryGetCurrentPattern(
                    [System.Windows.Automation.SelectionItemPattern]::Pattern,
                    [ref]$sourceSelectionItemPatternObject)) {
                ([System.Windows.Automation.SelectionItemPattern]$sourceSelectionItemPatternObject).Select()
                $selectedWithUia = $true
            }
        }
        catch {
            $selectedWithUia = $false
        }

        if (-not $selectedWithUia) {
            Assert-PackagedWindowForeground `
                $playbackWindowHandle `
                ([uint32]$playbackActivationProcessId)
            Assert-FocusedAutomationElement `
                $playbackSourceElement `
                "CatalogSourceSelector" `
                -RequestFocus
            [IptvSuite.PackageSmoke.KeyboardInspector]::PressHome()
            [IptvSuite.PackageSmoke.KeyboardInspector]::PressEnter()
        }
    }
    if ($sourceExpandPattern.Current.ExpandCollapseState -eq
        [System.Windows.Automation.ExpandCollapseState]::Expanded) {
        $sourceExpandPattern.Collapse()
    }

    $expectedPlaybackCatalogStatus =
        "Showing 1$([char]0x2013)2 of 2 channels."
    $catalogStatusElement = $null
    $playbackCatalogDeadline = (Get-Date).AddSeconds(15)
    $playbackCatalogReady = $false
    do {
        Assert-PackagedProcessAlive -Process $launchedProcess
        if ($null -eq $catalogStatusElement) {
            $catalogStatusElement = Get-AutomationElementById `
                $playbackAutomationRoot `
                "CatalogStatusText"
        }
        $selectedSources = @($sourceSelectionPattern.Current.GetSelection())
        if ($selectedSources.Count -eq 1 -and
            $selectedSources[0].Current.Name -ceq $expectedPlaybackSourceName -and
            $null -ne $catalogStatusElement -and
            $catalogStatusElement.Current.Name -ceq $expectedPlaybackCatalogStatus) {
            $playbackCatalogReady = $true
            break
        }

        Start-Sleep -Milliseconds 100
    } while ((Get-Date) -lt $playbackCatalogDeadline)
    if (-not $playbackCatalogReady) {
        if ($null -eq $catalogStatusElement) {
            throw "The packaged playback catalog status automation element is missing."
        }
        throw "The packaged playback catalog did not expose the seeded acceptance channel."
    }

    $playbackChannelDeadline = (Get-Date).AddSeconds(10)
    do {
        Assert-PackagedProcessAlive -Process $launchedProcess
        $playbackChannelItems = $playbackChannelListElement.FindAll(
            [System.Windows.Automation.TreeScope]::Descendants,
            $playbackListItemCondition)
        if ($playbackChannelItems.Count -eq 2) {
            break
        }

        Start-Sleep -Milliseconds 100
    } while ((Get-Date) -lt $playbackChannelDeadline)
    if ($playbackChannelItems.Count -ne 2) {
        throw "The packaged playback catalog did not expose exactly two channel items."
    }

    $playbackChannelItemA = $null
    $playbackChannelItemB = $null
    for ($channelItemIndex = 0;
        $channelItemIndex -lt $playbackChannelItems.Count;
        $channelItemIndex++) {
        $channelItem = $playbackChannelItems[$channelItemIndex]
        if ($channelItem.Current.Name -ceq $expectedPlaybackChannelAName) {
            if ($null -ne $playbackChannelItemA) {
                throw "The packaged playback channel list contains a duplicate acceptance channel."
            }
            $playbackChannelItemA = $channelItem
        }
        elseif ($channelItem.Current.Name -ceq $expectedPlaybackChannelBName) {
            if ($null -ne $playbackChannelItemB) {
                throw "The packaged playback channel list contains a duplicate acceptance channel."
            }
            $playbackChannelItemB = $channelItem
        }
        else {
            throw "The packaged playback channel list contains an unexpected channel."
        }
    }
    if ($null -eq $playbackChannelItemA -or $null -eq $playbackChannelItemB) {
        throw "The packaged playback channel list is incomplete."
    }

    Assert-PackagedWindowForeground `
        $playbackWindowHandle `
        ([uint32]$playbackActivationProcessId)
    Invoke-PackagedPlaybackChannelItem `
        -Process $launchedProcess `
        -ChannelItem $playbackChannelItemA `
        -WindowHandle $playbackWindowHandle `
        -ExpectedProcessId ([uint32]$playbackActivationProcessId)
    Wait-PackagedPlaybackSelection `
        -Process $launchedProcess `
        -StatusElement $playbackStatusElement `
        -ChannelElement $playbackCurrentChannelElement `
        -ExpectedChannelName $expectedPlaybackChannelAName

    Invoke-PackagedPlaybackControlButton `
        -Process $launchedProcess `
        -ButtonElement $volumeDownButtonElement
    Wait-PackagedAutomationName `
        -Process $launchedProcess `
        -Element $volumeTextElement `
        -ExpectedName "Volume 95%"
    Invoke-PackagedPlaybackControlButton `
        -Process $launchedProcess `
        -ButtonElement $volumeUpButtonElement
    Wait-PackagedAutomationName `
        -Process $launchedProcess `
        -Element $volumeTextElement `
        -ExpectedName "Volume 100%"
    $playbackVolumeControlVerified = $true

    Invoke-PackagedPlaybackControlButton `
        -Process $launchedProcess `
        -ButtonElement $muteButtonElement
    Wait-PackagedAutomationName `
        -Process $launchedProcess `
        -Element $muteButtonElement `
        -ExpectedName "Unmute playback"
    $playbackMuteControlVerified = $true

    Invoke-PackagedPlaybackControlButton `
        -Process $launchedProcess `
        -ButtonElement $aspectModeButtonElement
    Wait-PackagedAutomationName `
        -Process $launchedProcess `
        -Element $aspectModeButtonElement `
        -ExpectedName "Use fit aspect mode"
    $playbackAspectControlVerified = $true

    Assert-FocusedAutomationElement `
        $fullscreenButtonElement `
        "PlaybackFullscreenButton" `
        -RequestFocus
    Invoke-PackagedPlaybackControlButton `
        -Process $launchedProcess `
        -ButtonElement $fullscreenButtonElement
    $fullscreenButtonElement = Wait-PackagedAutomationElementByName `
        -Process $launchedProcess `
        -Root $playbackAutomationRoot `
        -AutomationId "PlaybackFullscreenButton" `
        -ControlType ([System.Windows.Automation.ControlType]::Button) `
        -ExpectedName "Exit fullscreen"
    Wait-PackagedPlaybackStatus `
        -Process $launchedProcess `
        -StatusElement $playbackStatusElement `
        -ExpectedStatus "Channel is playing."
    $playbackFullscreenEnterVerified = $true
    Invoke-PackagedPlaybackControlButton `
        -Process $launchedProcess `
        -ButtonElement $fullscreenButtonElement
    $fullscreenButtonElement = Wait-PackagedAutomationElementByName `
        -Process $launchedProcess `
        -Root $playbackAutomationRoot `
        -AutomationId "PlaybackFullscreenButton" `
        -ControlType ([System.Windows.Automation.ControlType]::Button) `
        -ExpectedName "Enter fullscreen"
    Assert-FocusedAutomationElement `
        $fullscreenButtonElement `
        "PlaybackFullscreenButton"
    Wait-PackagedPlaybackStatus `
        -Process $launchedProcess `
        -StatusElement $playbackStatusElement `
        -ExpectedStatus "Channel is playing."
    $playbackFullscreenExitVerified = $true
    $playbackFullscreenFocusRestored = $true

    Invoke-PackagedPlaybackButton `
        -Process $launchedProcess `
        -ButtonElement $pauseButtonElement `
        -StatusElement $playbackStatusElement `
        -ExpectedStatus "Playback paused."
    Invoke-PackagedPlaybackButton `
        -Process $launchedProcess `
        -ButtonElement $playButtonElement `
        -StatusElement $playbackStatusElement `
        -ExpectedStatus "Channel is playing."

    $rapidSwitchSamples = [System.Collections.Generic.List[double]]::new(25)
    for ($switchOrdinal = 1; $switchOrdinal -le 25; $switchOrdinal++) {
        $targetChannelItem = if (($switchOrdinal % 2) -eq 1) {
            $playbackChannelItemB
        }
        else {
            $playbackChannelItemA
        }
        $targetChannelName = if (($switchOrdinal % 2) -eq 1) {
            $expectedPlaybackChannelBName
        }
        else {
            $expectedPlaybackChannelAName
        }

        $switchTimer = [System.Diagnostics.Stopwatch]::StartNew()
        Invoke-PackagedPlaybackChannelItem `
            -Process $launchedProcess `
            -ChannelItem $targetChannelItem `
            -WindowHandle $playbackWindowHandle `
            -ExpectedProcessId ([uint32]$playbackActivationProcessId)
        Wait-PackagedPlaybackSelection `
            -Process $launchedProcess `
            -StatusElement $playbackStatusElement `
            -ChannelElement $playbackCurrentChannelElement `
            -ExpectedChannelName $targetChannelName
        $switchTimer.Stop()
        $rapidSwitchSamples.Add($switchTimer.Elapsed.TotalMilliseconds)
    }

    $playbackRapidSwitchP95Milliseconds = Get-Percentile95 $rapidSwitchSamples.ToArray()
    $playbackRapidSwitchMaximumMilliseconds = [double](
        ($rapidSwitchSamples | Measure-Object -Maximum).Maximum)
    if ($playbackRapidSwitchP95Milliseconds -gt 3000.0) {
        throw "The packaged playback rapid-switch p95 budget was exceeded."
    }
    $playbackRapidSwitchCount = $rapidSwitchSamples.Count
    $playbackRapidSwitchVerified = $playbackRapidSwitchCount -eq 25

    Invoke-PackagedPlaybackButton `
        -Process $launchedProcess `
        -ButtonElement $stopButtonElement `
        -StatusElement $playbackStatusElement `
        -ExpectedStatus "Playback stopped."
    Wait-PackagedAutomationName `
        -Process $launchedProcess `
        -Element $playbackCurrentChannelElement `
        -ExpectedName "No channel selected."

    Invoke-PackagedPlaybackChannelItem `
        -Process $launchedProcess `
        -ChannelItem $playbackChannelItemA `
        -WindowHandle $playbackWindowHandle `
        -ExpectedProcessId ([uint32]$playbackActivationProcessId)
    Wait-PackagedPlaybackSelection `
        -Process $launchedProcess `
        -StatusElement $playbackStatusElement `
        -ChannelElement $playbackCurrentChannelElement `
        -ExpectedChannelName $expectedPlaybackChannelAName

    Assert-PackagedProcessAlive -Process $launchedProcess
    if (-not $launchedProcess.CloseMainWindow()) {
        throw "The packaged playback application rejected a normal window-close request."
    }
    if (-not $launchedProcess.WaitForExit(10000)) {
        throw "The packaged playback application did not exit after a normal close request."
    }
    $launchedProcess.Refresh()
    $playbackExitCode = $launchedProcess.ExitCode
    if ($null -eq $playbackExitCode -or [int]$playbackExitCode -ne 0) {
        throw "The packaged playback application did not return a successful normal-close result."
    }
    $playbackActiveCloseVerified = $true

    $stopSignalStream = [System.IO.File]::Open(
        $playbackStopSignalPath,
        [System.IO.FileMode]::CreateNew,
        [System.IO.FileAccess]::Write,
        [System.IO.FileShare]::None)
    $stopSignalStream.Dispose()
    $playbackStopSignalCreated = $true
    if (-not $playbackHarnessProcess.WaitForExit(15000)) {
        throw "The playback acceptance harness did not stop before the deadline."
    }
    $playbackHarnessProcess.Refresh()
    if ([int]$playbackHarnessProcess.ExitCode -ne 0) {
        throw "The playback acceptance harness returned a failure result."
    }

    Assert-ExactPlaybackControlEntries `
        -AllowedNames @("loopback.cer", "ready.json", "result.json", "stop.signal")
    $resultTicket = Read-StrictPlaybackJsonTicket `
        -Path $playbackResultPath `
        -AllowedProperties @(
            "ReadyPublished",
            "SeedCompleted",
            "StopObserved",
            "StoppedGracefully",
            "CertificateThumbprint",
            "RequestCount",
            "CompletedResponseCount",
            "CompletedBodyBytes",
            "FailureCount",
            "ChannelARequestCount",
            "ChannelBRequestCount")
    if ($resultTicket.ReadyPublished -isnot [bool] -or
        $resultTicket.SeedCompleted -isnot [bool] -or
        $resultTicket.StopObserved -isnot [bool] -or
        $resultTicket.StoppedGracefully -isnot [bool] -or
        $resultTicket.CertificateThumbprint -isnot [string] -or
        $resultTicket.RequestCount -isnot [int] -or
        $resultTicket.CompletedResponseCount -isnot [int] -or
        ($resultTicket.CompletedBodyBytes -isnot [int] -and
            $resultTicket.CompletedBodyBytes -isnot [long]) -or
        $resultTicket.FailureCount -isnot [int] -or
        $resultTicket.ChannelARequestCount -isnot [int] -or
        $resultTicket.ChannelBRequestCount -isnot [int] -or
        -not $resultTicket.ReadyPublished -or
        -not $resultTicket.SeedCompleted -or
        -not $resultTicket.StopObserved -or
        -not $resultTicket.StoppedGracefully -or
        $resultTicket.CertificateThumbprint -cne
            $playbackLoopbackCertificateThumbprint -or
        [int]$resultTicket.RequestCount -lt 27 -or
        [int]$resultTicket.CompletedResponseCount -ne
            [int]$resultTicket.RequestCount -or
        [long]$resultTicket.CompletedBodyBytes -le 0 -or
        [int]$resultTicket.FailureCount -ne 0 -or
        [int]$resultTicket.ChannelARequestCount -le 0 -or
        [int]$resultTicket.ChannelBRequestCount -le 0 -or
        ([int]$resultTicket.ChannelARequestCount +
            [int]$resultTicket.ChannelBRequestCount) -ne
            [int]$resultTicket.RequestCount) {
        throw "The playback acceptance result ticket is invalid."
    }

    $playbackUiRequestCount = [int]$resultTicket.RequestCount
    $playbackUiCompletedResponseCount = [int]$resultTicket.CompletedResponseCount
    $playbackUiCompletedBodyBytes = [long]$resultTicket.CompletedBodyBytes
    $playbackChannelARequestCount = [int]$resultTicket.ChannelARequestCount
    $playbackChannelBRequestCount = [int]$resultTicket.ChannelBRequestCount
    $playbackUiAcceptanceVerified = $true

    $packageFileName = $packages[0].Name
    Remove-ExactDevelopmentPackage

    if (Get-AppxPackage -Name $expectedName -ErrorAction SilentlyContinue) {
        throw "The development package is still registered after uninstall."
    }
    $installedPackage = $null
    $installAttempted = $false

    $appDataPath = Join-Path $env:LOCALAPPDATA "Packages\$packageFamilyName"
    $cleanupDeadline = (Get-Date).AddSeconds(10)
    while ((Test-Path -LiteralPath $appDataPath) -and (Get-Date) -lt $cleanupDeadline) {
        Start-Sleep -Milliseconds 250
    }
    if (Test-Path -LiteralPath $appDataPath) {
        throw "Package app-data remains after uninstall."
    }
    if ($windowsAppRuntimeDisposition -notin @("ReusedRegisteredFramework", "InstalledLockedDependency")) {
        throw "The Windows App Runtime installation disposition is invalid."
    }

    $successEvidence = [ordered]@{
        RunId             = $runId
        CompletedAt       = (Get-Date).ToUniversalTime().ToString("O")
        Configuration     = $Configuration
        DotNetSdk         = $actualSdk
        PackageFile       = $packageFileName
        PackageSha256     = $packageSha256
        PackageName       = $expectedName
        PackagePublisher  = $expectedPublisher
        PackageFamilyName = $packageFamilyName
        Architecture      = "x64"
        Capabilities      = @("runFullTrust")
        SignatureStatus   = $signature.Status.ToString()
        WindowsAppRuntimeDisposition = $windowsAppRuntimeDisposition
        PayloadLeakGate   = $true
        ProtectedStoreDirectoryInitialized = $protectedStoreDirectoryInitialized
        CatalogUiaContractVerified = $catalogUiaContractVerified
        CatalogKeyboardFocusOrderVerified = $catalogKeyboardFocusOrderVerified
        Catalog50kSeedVerified = $catalog50kSeedVerified
        CatalogRealizedContainerBoundVerified = $catalogRealizedContainerBoundVerified
        CatalogRealizedContainerCount = $catalogRealizedContainerCount
        CatalogInputResponseP95Milliseconds = [Math]::Round($catalogInputResponseP95Milliseconds, 3)
        CatalogDwmFrameP95Milliseconds = [Math]::Round($catalogFrameP95Milliseconds, 3)
        CatalogDwmFrameMaximumMilliseconds = [Math]::Round($catalogFrameMaximumMilliseconds, 3)
        CatalogDwmDroppedFramePercent = [Math]::Round($catalogDroppedFramePercent, 3)
        CatalogDwmFrameIntervalCount = $catalogFrameIntervalCount
        PlaybackUiAcceptanceVerified = $playbackUiAcceptanceVerified
        PlaybackVolumeControlVerified = $playbackVolumeControlVerified
        PlaybackMuteControlVerified = $playbackMuteControlVerified
        PlaybackAspectControlVerified = $playbackAspectControlVerified
        PlaybackFullscreenEnterVerified = $playbackFullscreenEnterVerified
        PlaybackFullscreenExitVerified = $playbackFullscreenExitVerified
        PlaybackFullscreenFocusRestored = $playbackFullscreenFocusRestored
        PlaybackRapidSwitchVerified = $playbackRapidSwitchVerified
        PlaybackRapidSwitchCount = $playbackRapidSwitchCount
        PlaybackRapidSwitchP95Milliseconds = [Math]::Round(
            $playbackRapidSwitchP95Milliseconds,
            3)
        PlaybackRapidSwitchMaximumMilliseconds = [Math]::Round(
            $playbackRapidSwitchMaximumMilliseconds,
            3)
        PlaybackActiveCloseVerified = $playbackActiveCloseVerified
        PlaybackUiRequestCount = $playbackUiRequestCount
        PlaybackUiCompletedResponseCount = $playbackUiCompletedResponseCount
        PlaybackUiCompletedBodyBytes = $playbackUiCompletedBodyBytes
        PlaybackChannelARequestCount = $playbackChannelARequestCount
        PlaybackChannelBRequestCount = $playbackChannelBRequestCount
        NormalClose       = $true
        PackageRemoved    = $true
    }
    $githubSha = [Environment]::GetEnvironmentVariable("GITHUB_SHA", "Process")
    if (-not [string]::IsNullOrWhiteSpace($githubSha) -and
        [System.Text.RegularExpressions.Regex]::IsMatch($githubSha, '\A[0-9a-fA-F]{40}\z')) {
        $successEvidence.CommitSha = $githubSha.ToLowerInvariant()
    }

    $successMessage = "MSIX smoke passed: signed, installed, launched, closed, and uninstalled $packageFileName."
}
catch {
    $primaryFailure = $_
}
finally {
    Invoke-CleanupStep -Failures $cleanupFailures -Name "Stop launched application" -Action {
        if ($null -ne $launchedProcess) {
            try {
                $launchedProcess.Refresh()
                if (-not $launchedProcess.HasExited) {
                    $launchedProcess.Kill()
                    if (-not $launchedProcess.WaitForExit(10000)) {
                        throw "The exact packaged application process did not stop during cleanup."
                    }
                }
            }
            finally {
                $launchedProcess.Dispose()
            }
        }
    }

    Invoke-CleanupStep -Failures $cleanupFailures -Name "Stop playback acceptance harness" -Action {
        if ($null -ne $playbackHarnessProcess) {
            try {
                $playbackHarnessProcess.Refresh()
                if (-not $playbackHarnessProcess.HasExited) {
                    if ($playbackHarnessReady -and -not $playbackStopSignalCreated) {
                        if (Test-Path -LiteralPath $playbackStopSignalPath) {
                            $existingStopSignal = Get-Item -LiteralPath $playbackStopSignalPath -Force
                            if ($existingStopSignal.PSIsContainer -or
                                ($existingStopSignal.Attributes -band
                                    [System.IO.FileAttributes]::ReparsePoint) -ne 0 -or
                                $existingStopSignal.Length -ne 0) {
                                throw "The playback acceptance stop signal is invalid during cleanup."
                            }
                        }
                        else {
                            $cleanupStopSignalStream = [System.IO.File]::Open(
                                $playbackStopSignalPath,
                                [System.IO.FileMode]::CreateNew,
                                [System.IO.FileAccess]::Write,
                                [System.IO.FileShare]::None)
                            $cleanupStopSignalStream.Dispose()
                        }
                        $playbackStopSignalCreated = $true
                    }

                    if (-not $playbackHarnessReady -or
                        -not $playbackHarnessProcess.WaitForExit(10000)) {
                        $playbackHarnessProcess.Kill()
                        if (-not $playbackHarnessProcess.WaitForExit(10000)) {
                            throw "The exact playback acceptance harness process did not stop during cleanup."
                        }
                    }
                }
            }
            finally {
                $playbackHarnessProcess.Dispose()
            }
        }
    }

    Invoke-CleanupStep -Failures $cleanupFailures -Name "Remove exact development package" -Action {
        if ($installAttempted -or $null -ne $installedPackage) {
            Remove-ExactDevelopmentPackage
        }
    }

    foreach ($environmentEntry in @($environmentBackup.GetEnumerator())) {
        $environmentName = [string]$environmentEntry.Key
        $previousEnvironmentValue = $environmentEntry.Value
        Invoke-CleanupStep -Failures $cleanupFailures -Name "Restore process environment '$environmentName'" -Action {
            [Environment]::SetEnvironmentVariable(
                $environmentName,
                $previousEnvironmentValue,
                "Process")
        }
    }

    Invoke-CleanupStep -Failures $cleanupFailures -Name "Remove exact playback acceptance certificate" -Action {
        if ($playbackLoopbackCertificateImported) {
            if ([string]::IsNullOrWhiteSpace($playbackLoopbackCertificateThumbprint) -or
                -not [System.Text.RegularExpressions.Regex]::IsMatch(
                    $playbackLoopbackCertificateThumbprint,
                    '\A[0-9A-F]{40}\z')) {
                throw "Refusing playback certificate cleanup because the thumbprint is invalid."
            }

            $playbackCertificatePath =
                "Cert:\LocalMachine\Root\$playbackLoopbackCertificateThumbprint"
            $playbackCertificateCandidate = Get-Item `
                -LiteralPath $playbackCertificatePath `
                -ErrorAction SilentlyContinue
            if ($null -ne $playbackCertificateCandidate) {
                if ($playbackCertificateCandidate.Subject -cne
                        $expectedPlaybackCertificateSubject -or
                    $playbackCertificateCandidate.Thumbprint -cne
                        $playbackLoopbackCertificateThumbprint) {
                    throw "Refusing playback certificate cleanup because its identity does not match."
                }

                Remove-Item -LiteralPath $playbackCertificatePath -Force -ErrorAction Stop
            }
        }
    }

    Invoke-CleanupStep -Failures $cleanupFailures -Name "Dispose playback acceptance certificate" -Action {
        if ($null -ne $playbackLoopbackCertificate) {
            $playbackLoopbackCertificate.Dispose()
        }
    }

    if ($null -ne $certificate) {
        foreach ($certificateStore in @("Cert:\LocalMachine\TrustedPeople", "Cert:\CurrentUser\My")) {
            $store = $certificateStore
            Invoke-CleanupStep -Failures $cleanupFailures -Name "Remove exact certificate from '$store'" -Action {
                $certificatePath = "$store\$($certificate.Thumbprint)"
                $candidate = Get-Item -LiteralPath $certificatePath -ErrorAction SilentlyContinue
                if ($null -ne $candidate) {
                    if ($candidate.Subject -ne $expectedPublisher) {
                        throw "Refusing certificate cleanup because the subject does not match."
                    }

                    Remove-Item -LiteralPath $certificatePath -Force -ErrorAction Stop
                }
            }
        }
    }

    Invoke-CleanupStep -Failures $cleanupFailures -Name "Remove exported public certificate" -Action {
        if (Test-Path -LiteralPath $publicCertificatePath) {
            Remove-Item -LiteralPath $publicCertificatePath -Force -ErrorAction Stop
        }
    }

    Invoke-CleanupStep -Failures $cleanupFailures -Name "Remove exact playback-control directory" -Action {
        Remove-ExactPlaybackControlDirectory
    }

    Invoke-CleanupStep -Failures $cleanupFailures -Name "Remove exact package-output directory" -Action {
        Remove-ExactPackageOutput
    }
}

if ($null -ne $primaryFailure -or $cleanupFailures.Count -ne 0) {
    $failureMessage = if ($null -ne $primaryFailure) {
        $primaryFailure.Exception.Message
    }
    else {
        "Cleanup failed after an otherwise successful MSIX smoke."
    }
    $failureEvidence = [ordered]@{
        RunId         = $runId
        FailedAt      = (Get-Date).ToUniversalTime().ToString("O")
        Configuration = $Configuration
        Error         = $failureMessage
    }
    if ($cleanupFailures.Count -ne 0) {
        $failureEvidence.CleanupFailures = @($cleanupFailures)
    }
    Write-JsonAtomically -Value $failureEvidence -DestinationPath $failureEvidencePath

    if ($cleanupFailures.Count -ne 0) {
        $aggregateMessage = "Cleanup failures: $($cleanupFailures -join ' | ')"
        if ($null -ne $primaryFailure) {
            throw [System.InvalidOperationException]::new(
                "MSIX smoke failed: $failureMessage. $aggregateMessage",
                $primaryFailure.Exception)
        }

        throw [System.InvalidOperationException]::new($aggregateMessage)
    }

    throw $primaryFailure
}

try {
    Write-JsonAtomically -Value $successEvidence -DestinationPath $evidencePath
}
catch {
    $successEvidenceFailure = $_
    $failureEvidence = [ordered]@{
        RunId         = $runId
        FailedAt      = (Get-Date).ToUniversalTime().ToString("O")
        Configuration = $Configuration
        Error         = "Atomic success-evidence write failed: $($_.Exception.Message)"
    }
    try {
        Write-JsonAtomically -Value $failureEvidence -DestinationPath $failureEvidencePath
    }
    catch {
        throw [System.InvalidOperationException]::new(
            "Success evidence and failure evidence could not be written atomically.",
            $successEvidenceFailure.Exception)
    }

    throw $successEvidenceFailure
}

Write-Host $successMessage
