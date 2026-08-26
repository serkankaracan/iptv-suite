[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repositoryRoot = [System.IO.Path]::GetFullPath(
    (Join-Path $PSScriptRoot "..\..\..\.."))
$controllerPath = Join-Path `
    $repositoryRoot `
    "eng\Invoke-WindowsFinalArtifactCanaryScan.ps1"

function Assert-Condition {
    param(
        [Parameter(Mandatory = $true)][bool]$Condition,
        [Parameter(Mandatory = $true)][string]$Message
    )

    if (-not $Condition) {
        throw $Message
    }
}

function Assert-StableFailure {
    param(
        [Parameter(Mandatory = $true)][scriptblock]$Action,
        [Parameter(Mandatory = $true)][string]$Code
    )

    try {
        & $Action
        throw "Expected stable failure '$Code'."
    }
    catch {
        $expected = "WindowsFinalArtifactCanaryScan:$Code"
        if ($_.Exception.Message -cne $expected) {
            throw "Expected '$expected'; received '$($_.Exception.Message)'."
        }
    }
}

function Assert-BoundedProcessFailure {
    param(
        [Parameter(Mandatory = $true)][scriptblock]$Action,
        [Parameter(Mandatory = $true)][string]$Code
    )

    try {
        & $Action
        throw "Expected bounded-process failure '$Code'."
    }
    catch {
        $expected = "WindowsBoundedProcess:$Code"
        if ($_.Exception.Message -cne $expected) {
            throw "Expected '$expected'; received '$($_.Exception.Message)'."
        }
    }
}

$tokens = $null
$parseErrors = $null
$ast = [System.Management.Automation.Language.Parser]::ParseFile(
    $controllerPath,
    [ref]$tokens,
    [ref]$parseErrors)
Assert-Condition `
    ($parseErrors.Count -eq 0) `
    "The M16 final-artifact controller must parse under Windows PowerShell."
Assert-Condition `
    ($null -ne $ast.ParamBlock -and $ast.ParamBlock.Parameters.Count -eq 0) `
    "The M16 final-artifact controller must not expose caller-controlled parameters."

$source = [System.IO.File]::ReadAllText($controllerPath)
foreach ($requiredSource in @(
        '$script:windowsFinalMaximumLogBytes = 20MB',
        '$script:windowsFinalMaximumCleanupBytes = 64MB',
        '$script:windowsFinalPackageTimeoutMilliseconds = 2700000',
        '$script:windowsFinalScannerTimeoutMilliseconds = 600000',
        '"-EmitM16FinalArtifactSurfaces"',
        '"scan-release-artifacts"',
        '"M16"',
        '"FINAL_ARTIFACTS"',
        'New-WindowsM16FinalArtifactEvidence',
        'Write-WindowsM16FinalArtifactEvidenceAtomically',
        'WindowsBoundedProcess.ps1',
        'Enter-WindowsFinalRunMutex',
        'Get-WindowsFinalPackageBinding',
        'Initialize-WindowsFinalPackageOwnership',
        'Remove-WindowsFinalPackageSideState',
        'Stop-WindowsFinalExactPackageProcesses',
        '"Microsoft.PowerShell.Security\Certificate::CurrentUser\My"',
        '"Microsoft.PowerShell.Security\Certificate::LocalMachine\TrustedPeople"',
        '"Microsoft.PowerShell.Security\Certificate::LocalMachine\Root"',
        '"-M16RunToken"',
        '"m16-final-artifact-surfaces.json"',
        '"last-success.json"',
        'Get-WindowsFinalCleanRepositoryCommit',
        'Assert-WindowsFinalRepositoryStable')) {
    Assert-Condition `
        ($source.Contains($requiredSource)) `
        "The controller is missing required fixed contract '$requiredSource'."
}
foreach ($forbiddenSource in @(
        'IPTVSUITE_TEST_ONLY_CANARY_V1',
        'Cert:\',
        '-SoakMinutes',
        '-ContinueOnError',
        'Write-Host')) {
    Assert-Condition `
        (-not $source.Contains($forbiddenSource)) `
        "The controller contains forbidden surface '$forbiddenSource'."
}

. $controllerPath

$fixedPaths = Get-WindowsFinalPaths
. $fixedPaths.BoundedProcessHelperPath
if ($null -eq ('IptvSuite.WindowsBoundedProcessTest.InheritableFile' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace IptvSuite.WindowsBoundedProcessTest
{
    public static class InheritableFile
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct SecurityAttributes
        {
            internal int Length;
            internal IntPtr SecurityDescriptor;
            internal int InheritHandle;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateFile(
            string fileName,
            uint desiredAccess,
            uint shareMode,
            ref SecurityAttributes securityAttributes,
            uint creationDisposition,
            uint flagsAndAttributes,
            IntPtr templateFile);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr handle);

        public static IntPtr Create(string path)
        {
            SecurityAttributes attributes = new SecurityAttributes();
            attributes.Length = Marshal.SizeOf(typeof(SecurityAttributes));
            attributes.InheritHandle = 1;
            IntPtr handle = CreateFile(
                path,
                0x40000000,
                0x00000001 | 0x00000002 | 0x00000004,
                ref attributes,
                1,
                0x00000080,
                IntPtr.Zero);
            if (handle == new IntPtr(-1))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }
            return handle;
        }

        public static void Close(IntPtr handle)
        {
            if (handle != IntPtr.Zero && handle != new IntPtr(-1) &&
                !CloseHandle(handle))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }
        }
    }
}
'@ -ErrorAction Stop
}
Assert-Condition `
    ($fixedPaths.RepositoryRoot.Equals(
        $repositoryRoot,
        [System.StringComparison]::OrdinalIgnoreCase)) `
    "The controller did not resolve its repository root from its own location."
Assert-Condition `
    ($script:windowsFinalMaximumLogBytes -eq 20MB) `
    "The combined package log budget changed."
Assert-Condition `
    ($script:windowsFinalPackageTimeoutMilliseconds -eq 2700000) `
    "The package timeout changed."
Assert-Condition `
    ($script:windowsFinalScannerTimeoutMilliseconds -eq 600000) `
    "The scanner timeout changed."

$root = [System.IO.Path]::GetFullPath("C:\fixed\root")
Assert-Condition `
    (Test-WindowsFinalPathContainedByRoot `
        -Path ([System.IO.Path]::Combine($root, "child", "file.txt")) `
        -Root $root) `
    "A contained path was rejected."
Assert-Condition `
    (-not (Test-WindowsFinalPathContainedByRoot `
            -Path "C:\fixed\root-sibling\file.txt" `
            -Root $root)) `
    "A sibling-prefix path was admitted."
Assert-StableFailure `
    -Code "AdversarialPathRejected" `
    -Action {
        Assert-WindowsFinalNoAlternateDataStream `
            -Path "C:\fixed\root\file.txt:payload" `
            -Code "AdversarialPathRejected"
    }
Assert-StableFailure `
    -Code "ProcessArgumentInvalid" `
    -Action {
        [void](Join-WindowsFinalProcessArguments `
                -Values @('fixed', 'unsafe"argument'))
    }

$temporaryParent = [System.IO.Path]::GetFullPath(
    [System.IO.Path]::GetTempPath()).TrimEnd(
        [System.IO.Path]::DirectorySeparatorChar,
        [System.IO.Path]::AltDirectorySeparatorChar)
$temporaryRoot = [System.IO.Path]::Combine(
    $temporaryParent,
    "iptvsuite-m16-final-orchestrator-$([Guid]::NewGuid().ToString('N'))")
[System.IO.Directory]::CreateDirectory($temporaryRoot) | Out-Null
try {
    $exactCleanup = [System.IO.Path]::Combine($temporaryRoot, "exact-cleanup")
    $nested = [System.IO.Path]::Combine($exactCleanup, "nested")
    [System.IO.Directory]::CreateDirectory($nested) | Out-Null
    [System.IO.File]::WriteAllBytes(
        [System.IO.Path]::Combine($nested, "bounded.bin"),
        [byte[]](1, 2, 3, 4))
    Remove-WindowsFinalExactDirectory `
        -Path $exactCleanup `
        -ExpectedPath $exactCleanup `
        -ParentRoot $temporaryRoot
    Assert-Condition `
        (-not (Test-Path -LiteralPath $exactCleanup)) `
        "Exact cleanup did not remove its bounded target."

    $largeCleanup = [System.IO.Path]::Combine(
        $temporaryRoot,
        "large-exact-cleanup")
    [System.IO.Directory]::CreateDirectory($largeCleanup) | Out-Null
    $largeCleanupStream = [System.IO.File]::Open(
        [System.IO.Path]::Combine($largeCleanup, "bounded.bin"),
        [System.IO.FileMode]::CreateNew,
        [System.IO.FileAccess]::Write,
        [System.IO.FileShare]::None)
    try {
        $largeCleanupStream.SetLength(40MB)
    }
    finally {
        $largeCleanupStream.Dispose()
    }
    Remove-WindowsFinalExactDirectory `
        -Path $largeCleanup `
        -ExpectedPath $largeCleanup `
        -ParentRoot $temporaryRoot
    Assert-Condition `
        (-not (Test-Path -LiteralPath $largeCleanup)) `
        "Exact cleanup rejected a target within its fixed 64 MiB bound."

    $alternateStreamFile = [System.IO.Path]::Combine(
        $temporaryRoot,
        "alternate-stream.bin")
    [System.IO.File]::WriteAllBytes($alternateStreamFile, [byte[]](1, 2, 3))
    Set-Content `
        -LiteralPath $alternateStreamFile `
        -Stream "m16-selftest" `
        -Value "owned" `
        -NoNewline
    Assert-StableFailure `
        -Code "CleanupRefused" `
        -Action {
            Remove-WindowsFinalExactFile `
                -Path $alternateStreamFile `
                -ExpectedPath $alternateStreamFile `
                -ParentRoot $temporaryRoot `
                -MaximumBytes 64KB
        }
    Remove-Item `
        -LiteralPath $alternateStreamFile `
        -Stream "m16-selftest" `
        -Force `
        -ErrorAction Stop
    Remove-WindowsFinalExactFile `
        -Path $alternateStreamFile `
        -ExpectedPath $alternateStreamFile `
        -ParentRoot $temporaryRoot `
        -MaximumBytes 64KB

    $packageArtifactRoot = [System.IO.Path]::Combine(
        $temporaryRoot,
        "msix-smoke")
    [System.IO.Directory]::CreateDirectory($packageArtifactRoot) | Out-Null
    $packagePaths = [pscustomobject]@{
        PackageArtifactRoot = $packageArtifactRoot
        PackageCaptureParent = [System.IO.Path]::Combine(
            $packageArtifactRoot,
            "m16-final-artifact-capture")
        PackageOwnershipParent = [System.IO.Path]::Combine(
            $packageArtifactRoot,
            "m16-final-artifact-ownership")
        PackageOutputParent = [System.IO.Path]::Combine(
            $packageArtifactRoot,
            "packages")
        PlaybackControlParent = [System.IO.Path]::Combine(
            $packageArtifactRoot,
            "playback-ui")
        OnboardingControlParent = [System.IO.Path]::Combine(
            $packageArtifactRoot,
            "onboarding-ui")
    }
    $testRunToken = "a" * 32
    $packageRunPaths = Get-WindowsFinalPackageRunPaths `
        -Paths $packagePaths `
        -RunToken $testRunToken
    Initialize-WindowsFinalPackageOwnership `
        -Paths $packagePaths `
        -RunPaths $packageRunPaths
    Assert-Condition `
        ((Test-Path -LiteralPath $packageRunPaths.OwnershipRoot -PathType Container) -and
         [System.IO.Directory]::GetParent(
            $packageRunPaths.OwnershipRoot).FullName.Equals(
                $packagePaths.PackageOwnershipParent,
                [System.StringComparison]::OrdinalIgnoreCase)) `
        "The exact package cleanup ownership root was not initialized safely."
    $expectedPackageFullName =
        "IptvSuite.LocalDev.6f0d9a64_0.1.0.0_x64__6f0d9a64local"
    $registrationIntentJson =
        '{"SchemaVersion":1,"RunToken":"' + $testRunToken +
        '","ExpectedPackageFullName":"' + $expectedPackageFullName + '"}'
    [System.IO.File]::WriteAllText(
        $packageRunPaths.PackageRegistrationIntent,
        $registrationIntentJson,
        (New-Object System.Text.UTF8Encoding($false, $true)))
    $registrationIntent = Get-WindowsFinalPackageRegistrationIntent `
        -Path $packageRunPaths.PackageRegistrationIntent `
        -OwnershipRoot $packageRunPaths.OwnershipRoot `
        -ExpectedRunToken $testRunToken
    Assert-Condition `
        ($registrationIntent.RunToken -ceq $testRunToken -and
         $registrationIntent.ExpectedPackageFullName -ceq
            $expectedPackageFullName) `
        "The strict package registration intent was not parsed exactly."
    [System.IO.File]::Delete($packageRunPaths.PackageRegistrationIntent)
    $invalidOwnershipPath = $packageRunPaths.SigningThumbprint
    [System.IO.File]::WriteAllText(
        $invalidOwnershipPath,
        (("A" * 40) + "`n"),
        (New-Object System.Text.UTF8Encoding($false, $true)))
    Assert-StableFailure `
        -Code "PackageCleanupStateInvalid" `
        -Action {
            [void](Get-WindowsFinalOwnershipValue `
                    -Path $invalidOwnershipPath `
                    -OwnershipRoot $packageRunPaths.OwnershipRoot `
                    -Pattern '\A[0-9A-F]{40}\z')
        }
    [System.IO.File]::Delete($invalidOwnershipPath)
    Remove-WindowsFinalExactDirectory `
        -Path $packageRunPaths.OwnershipRoot `
        -ExpectedPath $packageRunPaths.OwnershipRoot `
        -ParentRoot $packagePaths.PackageOwnershipParent `
        -MaximumEntries 16 `
        -MaximumBytes 4KB
    Remove-WindowsFinalExactEmptyDirectory `
        -Path $packagePaths.PackageOwnershipParent `
        -ExpectedPath $packagePaths.PackageOwnershipParent `
        -ParentRoot $packageArtifactRoot

    $staleRunToken = "b" * 32
    $staleRunPaths = Get-WindowsFinalPackageRunPaths `
        -Paths $packagePaths `
        -RunToken $staleRunToken
    Initialize-WindowsFinalPackageOwnership `
        -Paths $packagePaths `
        -RunPaths $staleRunPaths
    [System.IO.Directory]::CreateDirectory($staleRunPaths.CaptureRoot) | Out-Null
    [System.IO.File]::WriteAllBytes(
        [System.IO.Path]::Combine($staleRunPaths.CaptureRoot, "stale.bin"),
        [byte[]](1, 2, 3, 4))
    $certificateDrive = Get-PSDrive -Name "Cert" -ErrorAction SilentlyContinue
    if ($null -ne $certificateDrive) {
        Remove-PSDrive -Name "Cert" -Force -ErrorAction Stop
    }
    Assert-Condition `
        ($null -eq (Get-PSDrive -Name "Cert" -ErrorAction SilentlyContinue)) `
        "The certificate-drive-independent cleanup precondition was not established."
    Remove-WindowsFinalStalePackageOwnership -Paths $packagePaths
    Assert-Condition `
        (-not (Test-Path -LiteralPath $staleRunPaths.OwnershipRoot) -and
         -not (Test-Path -LiteralPath $staleRunPaths.CaptureRoot)) `
        "Bounded stale-token recovery did not remove exact owned state."

    [System.IO.Directory]::CreateDirectory(
        $packagePaths.PackageOwnershipParent) | Out-Null
    foreach ($invalidStaleToken in @(("c" * 32), ("d" * 32))) {
        [System.IO.Directory]::CreateDirectory(
            [System.IO.Path]::Combine(
                $packagePaths.PackageOwnershipParent,
                $invalidStaleToken)) | Out-Null
    }
    Assert-StableFailure `
        -Code "PackageCleanupStateInvalid" `
        -Action {
            Remove-WindowsFinalStalePackageOwnership -Paths $packagePaths
        }
    foreach ($invalidStaleToken in @(("c" * 32), ("d" * 32))) {
        Remove-WindowsFinalExactDirectory `
            -Path ([System.IO.Path]::Combine(
                $packagePaths.PackageOwnershipParent,
                $invalidStaleToken)) `
            -ExpectedPath ([System.IO.Path]::Combine(
                $packagePaths.PackageOwnershipParent,
                $invalidStaleToken)) `
            -ParentRoot $packagePaths.PackageOwnershipParent `
            -MaximumEntries 16 `
            -MaximumBytes 4KB
    }
    Remove-WindowsFinalExactEmptyDirectory `
        -Path $packagePaths.PackageOwnershipParent `
        -ExpectedPath $packagePaths.PackageOwnershipParent `
        -ParentRoot $packageArtifactRoot

    $unexpected = [System.IO.Path]::Combine($temporaryRoot, "unexpected")
    Assert-StableFailure `
        -Code "CleanupRefused" `
        -Action {
            Remove-WindowsFinalExactDirectory `
                -Path $unexpected `
                -ExpectedPath $exactCleanup `
                -ParentRoot $temporaryRoot
        }

    $boundedRoot = [System.IO.Path]::Combine($temporaryRoot, "bounded-log")
    $boundedOutput = [System.IO.Path]::Combine($boundedRoot, "io")
    $boundedDestinationRoot = [System.IO.Path]::Combine($boundedRoot, "combined")
    [System.IO.Directory]::CreateDirectory($boundedOutput) | Out-Null
    [System.IO.Directory]::CreateDirectory($boundedDestinationRoot) | Out-Null
    $smallStdout = [System.IO.Path]::Combine($boundedOutput, "small.stdout")
    $smallStderr = [System.IO.Path]::Combine($boundedOutput, "small.stderr")
    [System.IO.File]::WriteAllBytes(
        $smallStdout,
        ([System.Text.Encoding]::UTF8.GetBytes("alpha")))
    [System.IO.File]::WriteAllBytes(
        $smallStderr,
        ([System.Text.Encoding]::UTF8.GetBytes("beta")))
    $smallResult = [pscustomobject]@{
        StandardOutputPath = $smallStdout
        StandardErrorPath = $smallStderr
    }
    $combined = [System.IO.Path]::Combine(
        $boundedDestinationRoot,
        "combined.log")
    New-WindowsFinalCombinedLog `
        -ProcessResult $smallResult `
        -DestinationPath $combined `
        -AllowedRoot $boundedOutput
    $combinedItem = Get-Item -LiteralPath $combined -Force
    Assert-Condition `
        ($combinedItem.Length -gt 0 -and $combinedItem.Length -le 20MB) `
        "A bounded combined log was not produced."

    $largeStdout = [System.IO.Path]::Combine($boundedOutput, "large.stdout")
    $largeStderr = [System.IO.Path]::Combine($boundedOutput, "large.stderr")
    $largeStream = [System.IO.File]::Open(
        $largeStdout,
        [System.IO.FileMode]::CreateNew,
        [System.IO.FileAccess]::Write,
        [System.IO.FileShare]::None)
    try {
        $largeStream.SetLength(20MB)
    }
    finally {
        $largeStream.Dispose()
    }
    [System.IO.File]::WriteAllBytes($largeStderr, [byte[]](1))
    $largeResult = [pscustomobject]@{
        StandardOutputPath = $largeStdout
        StandardErrorPath = $largeStderr
    }
    Assert-StableFailure `
        -Code "ProcessOutputLimitExceeded" `
        -Action {
            New-WindowsFinalCombinedLog `
                -ProcessResult $largeResult `
                -DestinationPath ([System.IO.Path]::Combine(
                    $boundedDestinationRoot,
                    "too-large.log")) `
                -AllowedRoot $boundedOutput
        }
    Assert-Condition `
        (-not (Test-Path -LiteralPath ([System.IO.Path]::Combine(
                    $boundedDestinationRoot,
                    "too-large.log")))) `
        "The rejected oversized combined log left a partial artifact."

    $processRoot = [System.IO.Path]::Combine($temporaryRoot, "bounded-process")
    [System.IO.Directory]::CreateDirectory($processRoot) | Out-Null
    $windowsPowerShell = [System.IO.Path]::Combine(
        $env:SystemRoot,
        "System32",
        "WindowsPowerShell",
        "v1.0",
        "powershell.exe")
    $processResult = Invoke-WindowsBoundedProcess `
        -FilePath $windowsPowerShell `
        -ArgumentString (Join-WindowsFinalProcessArguments -Values @(
                "-NoProfile",
                "-NonInteractive",
                "-Command",
                "[Console]::Out.Write('ok'); [Console]::Error.Write('e'); exit 7")) `
        -WorkingDirectory $temporaryRoot `
        -StandardOutputPath ([System.IO.Path]::Combine($processRoot, "normal.stdout")) `
        -StandardErrorPath ([System.IO.Path]::Combine($processRoot, "normal.stderr")) `
        -TimeoutMilliseconds 10000 `
        -MaximumOutputBytes 1000
    Assert-Condition `
        ($processResult.ExitCode -eq 7 -and
         $processResult.StandardOutputLength -eq 2 -and
         $processResult.StandardErrorLength -eq 1) `
        "The bounded process did not preserve exact exit/output evidence."

    $ambientSentinelPath = [System.IO.Path]::Combine(
        $processRoot,
        "ambient-handle-sentinel.bin")
    $ambientHandle = [IptvSuite.WindowsBoundedProcessTest.InheritableFile]::Create(
        $ambientSentinelPath)
    try {
        $ambientCommand =
            '$handle=[IntPtr]::new(' + $ambientHandle.ToInt64() + ');' +
            'try{$safe=[Microsoft.Win32.SafeHandles.SafeFileHandle]::new(' +
            '$handle,$false);$stream=[System.IO.FileStream]::new(' +
            '$safe,[System.IO.FileAccess]::Write);' +
            '$stream.Write([byte[]](1,2,3,4),0,4);' +
            '$stream.Flush();$stream.Dispose()}catch{};exit 0'
        $ambientResult = Invoke-WindowsBoundedProcess `
            -FilePath $windowsPowerShell `
            -ArgumentString (Join-WindowsFinalProcessArguments -Values @(
                    "-NoProfile",
                    "-NonInteractive",
                    "-Command",
                    $ambientCommand)) `
            -WorkingDirectory $temporaryRoot `
            -StandardOutputPath ([System.IO.Path]::Combine(
                $processRoot,
                "ambient.stdout")) `
            -StandardErrorPath ([System.IO.Path]::Combine(
                $processRoot,
                "ambient.stderr")) `
            -TimeoutMilliseconds 10000 `
            -MaximumOutputBytes 1000
    }
    finally {
        [IptvSuite.WindowsBoundedProcessTest.InheritableFile]::Close(
            $ambientHandle)
    }
    Assert-Condition `
        ($ambientResult.ExitCode -eq 0 -and
         (Get-Item -LiteralPath $ambientSentinelPath -Force).Length -eq 0) `
        "The bounded child inherited an ambient parent handle."

    Assert-BoundedProcessFailure `
        -Code "OutputLimitExceeded" `
        -Action {
            Invoke-WindowsBoundedProcess `
                -FilePath $windowsPowerShell `
                -ArgumentString (Join-WindowsFinalProcessArguments -Values @(
                        "-NoProfile",
                        "-NonInteractive",
                        "-Command",
                        "[Console]::Out.Write(('x' * 1001))")) `
                -WorkingDirectory $temporaryRoot `
                -StandardOutputPath ([System.IO.Path]::Combine(
                    $processRoot,
                    "limit.stdout")) `
                -StandardErrorPath ([System.IO.Path]::Combine(
                    $processRoot,
                    "limit.stderr")) `
                -TimeoutMilliseconds 10000 `
                -MaximumOutputBytes 1000
        }
    $limitedBytes = @(
        (Get-Item -LiteralPath ([System.IO.Path]::Combine(
                    $processRoot,
                    "limit.stdout"))).Length,
        (Get-Item -LiteralPath ([System.IO.Path]::Combine(
                    $processRoot,
                    "limit.stderr"))).Length) |
        Measure-Object -Sum
    Assert-Condition `
        ([long]$limitedBytes.Sum -le 1000) `
        "The bounded process exceeded its aggregate output byte cap."

    $originalBoundedProcessFunction = ${function:Invoke-WindowsBoundedProcess}
    $faultHelperPath = [System.IO.Path]::Combine(
        $processRoot,
        "WindowsBoundedProcess.FlushFault.ps1")
    $faultHelperText = [System.IO.File]::ReadAllText(
            $fixedPaths.BoundedProcessHelperPath).Replace("`r`n", "`n")
    $faultHelperText = $faultHelperText.Replace(
        "IptvSuite.WindowsBoundedProcess",
        "IptvSuite.WindowsBoundedProcessFlushFault")
    $flushNeedle = @'
                    if (read == 0)
                    {
                        destinationOperation = true;
                        destination.Flush();
                        destinationOperation = false;
                        return;
                    }
'@
    $flushFault = @'
                    if (read == 0)
                    {
                        destinationOperation = true;
                        Thread.Sleep(250);
                        throw new IOException("Injected destination flush failure.");
                    }
'@
    $firstFlushNeedle = $faultHelperText.IndexOf(
        $flushNeedle,
        [System.StringComparison]::Ordinal)
    Assert-Condition `
        ($firstFlushNeedle -ge 0 -and
         $faultHelperText.IndexOf(
            $flushNeedle,
            $firstFlushNeedle + $flushNeedle.Length,
            [System.StringComparison]::Ordinal) -lt 0) `
        "The bounded-process destination fault injection anchor is not exact."
    $faultHelperText = $faultHelperText.Replace($flushNeedle, $flushFault)
    [System.IO.File]::WriteAllText(
        $faultHelperPath,
        $faultHelperText,
        (New-Object System.Text.UTF8Encoding($false)))
    try {
        . $faultHelperPath
        Assert-BoundedProcessFailure `
            -Code "OutputCaptureFailed" `
            -Action {
                Invoke-WindowsBoundedProcess `
                    -FilePath $windowsPowerShell `
                    -ArgumentString (Join-WindowsFinalProcessArguments -Values @(
                            "-NoProfile",
                            "-NonInteractive",
                            "-Command",
                            "exit 0")) `
                    -WorkingDirectory $temporaryRoot `
                    -StandardOutputPath ([System.IO.Path]::Combine(
                        $processRoot,
                        "flush-fault.stdout")) `
                    -StandardErrorPath ([System.IO.Path]::Combine(
                        $processRoot,
                        "flush-fault.stderr")) `
                    -TimeoutMilliseconds 10000 `
                    -MaximumOutputBytes 1000
            }
    }
    finally {
        ${function:Invoke-WindowsBoundedProcess} = $originalBoundedProcessFunction
    }
}
finally {
    Remove-WindowsFinalExactDirectory `
        -Path $temporaryRoot `
        -ExpectedPath $temporaryRoot `
        -ParentRoot $temporaryParent
}

$exactDotNetPath = Join-Path $repositoryRoot ".artifacts\dotnet\dotnet.exe"
$testingAssemblyPath = Join-Path `
    $repositoryRoot `
    "apps\windows\tests\IptvSuite.Testing\bin\x64\Release\net10.0\IptvSuite.Testing.dll"
if ((Test-Path -LiteralPath $exactDotNetPath -PathType Leaf) -and
    (Test-Path -LiteralPath $testingAssemblyPath -PathType Leaf)) {
    . (Join-Path $repositoryRoot "eng\WindowsM16FinalArtifactEvidence.ps1")
    $artifactUmbrella = Join-Path $repositoryRoot ".artifacts"
    $scannerTestRoot = Join-Path `
        $artifactUmbrella `
        "m16-final-orchestrator-selftest-$([Guid]::NewGuid().ToString('N'))"
    $fullLogRoot = Join-Path $scannerTestRoot "full-log"
    $scannerIoRoot = Join-Path $scannerTestRoot "scanner-io"
    [System.IO.Directory]::CreateDirectory($fullLogRoot) | Out-Null
    [System.IO.Directory]::CreateDirectory($scannerIoRoot) | Out-Null
    try {
        [System.IO.File]::WriteAllBytes(
            (Join-Path $fullLogRoot "synthetic.log"),
            ([System.Text.Encoding]::UTF8.GetBytes(
                "Synthetic bounded final-artifact log.")))
        $scannerPaths = [pscustomobject]@{
            TestingAssemblyPath = $testingAssemblyPath
            RepositoryRoot = $repositoryRoot
            DotNetPath = $exactDotNetPath
            FullLogRoot = $fullLogRoot
            ScannerIoRoot = $scannerIoRoot
            ArtifactUmbrella = $artifactUmbrella
            PackageBindingPath = [System.IO.Path]::Combine(
                $scannerTestRoot,
                "package-binding.json")
        }
        $normalizedPath = New-WindowsFinalFullLogScannerReport `
            -Paths $scannerPaths
        $normalizedRecord = Read-WindowsM16FinalStrictJson `
            -Path $normalizedPath `
            -InputRoot $artifactUmbrella
        $normalized = Test-WindowsM16FinalScannerSurface `
            -Value $normalizedRecord.Value `
            -ExpectedSurfaceId "full-log"
        Assert-Condition `
            ($normalized.Result -ceq "clean" -and
             $normalized.FileCount -eq 1 -and
             $normalized.FindingCount -eq 0) `
            "The real scanner path did not produce a clean normalized full-log report."

        $retainedCaptureRoot = [System.IO.Path]::Combine(
            $scannerTestRoot,
            "retained-capture")
        $exactPackageRoot = [System.IO.Path]::Combine(
            $retainedCaptureRoot,
            "exact-package")
        $expandedRoot = [System.IO.Path]::Combine(
            $exactPackageRoot,
            "expanded")
        [System.IO.Directory]::CreateDirectory($expandedRoot) | Out-Null
        [System.IO.File]::WriteAllBytes(
            [System.IO.Path]::Combine($exactPackageRoot, "package.msix"),
            [byte[]](11, 12, 13, 14, 15, 16))
        [System.IO.File]::WriteAllBytes(
            [System.IO.Path]::Combine($expandedRoot, "payload.bin"),
            [byte[]](21, 22, 23, 24))
        $outerExpectation = Get-WindowsFinalOuterPackageExpectation `
            -Paths $scannerPaths `
            -RunPaths ([pscustomobject]@{ CaptureRoot = $retainedCaptureRoot })
        Assert-Condition `
            ($outerExpectation.PackageSha256 -cmatch '\A[0-9a-f]{64}\z' -and
             $outerExpectation.PackageSbomApplicationPackageSha256 -ceq
                $outerExpectation.PackageSha256 -and
             $outerExpectation.ExactPackageInventorySha256 -cmatch
                '\A[0-9a-f]{64}\z') `
            "The outer controller did not independently derive package expectations."

        $bindingRunId = "e" * 32
        $bindingCommit = "f" * 40
        $wrongPackageHash = "0" * 64
        Write-WindowsM16FinalArtifactEvidenceAtomically `
            -Value ([ordered]@{
                SchemaVersion = 1
                EvidenceKind = "PackageBoundFinalArtifactBinding"
                RunId = $bindingRunId
                CommitSha = $bindingCommit
                PackageSha256 = $wrongPackageHash
                PackageSbomApplicationPackageSha256 = $wrongPackageHash
                ExactPackageInventorySha256 =
                    $outerExpectation.ExactPackageInventorySha256
                PostScanPackageRehashPassed = $true
            }) `
            -DestinationPath $scannerPaths.PackageBindingPath
        Assert-StableFailure `
            -Code "PackageBindingMismatch" `
            -Action {
                [void](Get-WindowsFinalPackageBinding `
                        -Paths $scannerPaths `
                        -ExpectedRunId $bindingRunId `
                        -ExpectedCommit $bindingCommit `
                        -ExpectedPackageSha256 $outerExpectation.PackageSha256 `
                        -ExpectedExactPackageInventorySha256 `
                            $outerExpectation.ExactPackageInventorySha256)
            }
        Remove-WindowsFinalExactFile `
            -Path $scannerPaths.PackageBindingPath `
            -ExpectedPath $scannerPaths.PackageBindingPath `
            -ParentRoot $scannerTestRoot `
            -MaximumBytes 64KB
        Write-WindowsM16FinalArtifactEvidenceAtomically `
            -Value ([ordered]@{
                SchemaVersion = 1
                EvidenceKind = "PackageBoundFinalArtifactBinding"
                RunId = $bindingRunId
                CommitSha = $bindingCommit
                PackageSha256 = $outerExpectation.PackageSha256
                PackageSbomApplicationPackageSha256 =
                    $outerExpectation.PackageSha256
                ExactPackageInventorySha256 =
                    $outerExpectation.ExactPackageInventorySha256
                PostScanPackageRehashPassed = $true
            }) `
            -DestinationPath $scannerPaths.PackageBindingPath
        Assert-Condition `
            (Get-WindowsFinalPackageBinding `
                -Paths $scannerPaths `
                -ExpectedRunId $bindingRunId `
                -ExpectedCommit $bindingCommit `
                -ExpectedPackageSha256 $outerExpectation.PackageSha256 `
                -ExpectedExactPackageInventorySha256 `
                    $outerExpectation.ExactPackageInventorySha256) `
            "The strict package binding rejected outer-owned expectations."
    }
    finally {
        Remove-WindowsFinalExactDirectory `
            -Path $scannerTestRoot `
            -ExpectedPath $scannerTestRoot `
            -ParentRoot $artifactUmbrella
    }
}

Write-Output "Windows M16 final artifact canary controller self-test passed."
