[CmdletBinding()]
param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",

    [string]$DotNetPath = "dotnet"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$activationInterop = @'
using System;
using System.Runtime.InteropServices;

namespace IptvSuite.PackageLifecycleSmoke
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

        public static int Activate(string appUserModelId, string arguments)
        {
            if (String.IsNullOrWhiteSpace(appUserModelId))
            {
                throw new ArgumentException("An application user model ID is required.", "appUserModelId");
            }

            if (String.IsNullOrWhiteSpace(arguments))
            {
                throw new ArgumentException("Activation arguments are required.", "arguments");
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
                throw new COMException("Packaged activation service creation failed.", creationResult);
            }

            try
            {
                uint processId;
                int result = ((IApplicationActivationManager)activationManager).ActivateApplication(
                    appUserModelId,
                    arguments,
                    ActivateOptions.NoErrorUi,
                    out processId);
                if (result < 0)
                {
                    throw new COMException("Packaged application activation failed.", result);
                }

                if (processId == 0 || processId > Int32.MaxValue)
                {
                    throw new InvalidOperationException("Package activation returned an invalid process identifier.");
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
}
'@
Add-Type -TypeDefinition $activationInterop -Language CSharp -ErrorAction Stop

$expectedName = "ProtectedStore.PackageLifecycleTest.Local.5d8c7a91"
$expectedPublisher = "CN=Protected Store Package Lifecycle Local Test"
$baselineVersion = "0.0.1.0"
$updatedVersion = "0.0.2.0"
$expectedApplicationId = "Harness"
$expectedProcessName = "IptvSuite.PackageLifecycleHarness"
$baselinePackageFileName = "IptvSuite.PackageLifecycleHarness_0.0.1.0_x64.msix"
$updatedPackageFileName = "IptvSuite.PackageLifecycleHarness_0.0.2.0_x64.msix"
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$projectPath = Join-Path $repositoryRoot "apps\windows\tests\IptvSuite.PackageLifecycleHarness\IptvSuite.PackageLifecycleHarness.csproj"
$sourceManifestPath = Join-Path $repositoryRoot "apps\windows\tests\IptvSuite.PackageLifecycleHarness\Package.appxmanifest"
$sourceUpdateManifestPath = Join-Path $repositoryRoot "apps\windows\tests\IptvSuite.PackageLifecycleHarness\Package.Update.appxmanifest"
$testingProjectPath = Join-Path $repositoryRoot "apps\windows\tests\IptvSuite.Testing\IptvSuite.Testing.csproj"
$testingToolPath = Join-Path $repositoryRoot "apps\windows\tests\IptvSuite.Testing\bin\Release\net10.0\IptvSuite.Testing.dll"
$artifactsRoot = Join-Path $repositoryRoot ".artifacts"
$artifactRoot = Join-Path $artifactsRoot "package-lifecycle"
$runId = [Guid]::NewGuid().ToString("N")
$packageOutputRoot = Join-Path $artifactRoot "packages"
$packageOutput = Join-Path $packageOutputRoot $runId
$baselinePackageOutput = Join-Path $packageOutput "baseline"
$updatedPackageOutput = Join-Path $packageOutput "updated"
$publicCertificatePath = Join-Path $artifactRoot "$runId.cer"
$successEvidencePath = Join-Path $artifactRoot "last-success.json"
$failureEvidencePath = Join-Path $artifactRoot "last-failure.json"

$certificate = $null
$installedPackage = $null
$activeProcess = $null
$packageFamilyName = $null
$appDataPath = $null
$installAttempted = $false
$environmentBackup = @{}
$cleanupFailures = [System.Collections.Generic.List[string]]::new()
$primaryFailure = $null
$failureStage = "Bootstrap"
$failureCode = "UnexpectedFailure"
$actualSdk = $null
$baselineArtifacts = $null
$updatedArtifacts = $null
$baselinePackageSha256 = $null
$updatedPackageSha256 = $null
$baselinePackageFullName = $null
$updatedPackageFullName = $null
$baselineSignatureStatus = $null
$updatedSignatureStatus = $null
$successEvidence = $null
$msBuildEnvironment = @{
    AppxBundle                    = "Never"
    AppxPackageSigningEnabled     = "true"
    AppxSymbolPackageEnabled      = "false"
    DebugSymbols                  = "false"
    DebugType                     = "None"
    GenerateAppxPackageOnBuild    = "true"
    UapAppxPackageBuildMode       = "SideloadOnly"
}

function Set-FailurePoint {
    param(
        [Parameter(Mandatory)]
        [ValidatePattern('\A[A-Za-z][A-Za-z0-9]+\z')]
        [string]$Stage,

        [Parameter(Mandatory)]
        [ValidatePattern('\A[A-Za-z][A-Za-z0-9]+\z')]
        [string]$Code
    )

    $script:failureStage = $Stage
    $script:failureCode = $Code
}

function Assert-RegularFile {
    param(
        [Parameter(Mandatory)]
        [string]$Path
    )

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "A required regular file is unavailable."
    }

    $attributes = [System.IO.File]::GetAttributes([System.IO.Path]::GetFullPath($Path))
    if (($attributes -band ([System.IO.FileAttributes]::Directory -bor [System.IO.FileAttributes]::ReparsePoint)) -ne 0) {
        throw "A required file is unsafe."
    }
}

function Assert-SafeDirectory {
    param(
        [Parameter(Mandatory)]
        [string]$Path
    )

    if (-not (Test-Path -LiteralPath $Path -PathType Container)) {
        throw "A required directory is unavailable."
    }

    $attributes = [System.IO.File]::GetAttributes([System.IO.Path]::GetFullPath($Path))
    if (($attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "A required directory is unsafe."
    }
}

function Assert-ManifestPolicy {
    param(
        [Parameter(Mandatory)]
        [xml]$Manifest,

        [Parameter(Mandatory)]
        [ValidateSet("0.0.1.0", "0.0.2.0")]
        [string]$ExpectedVersion,

        [switch]$Built
    )

    $identity = $Manifest.SelectSingleNode("/*[local-name()='Package']/*[local-name()='Identity']")
    if ($null -eq $identity -or
        $identity.GetAttribute("Name") -ne $expectedName -or
        $identity.GetAttribute("Publisher") -ne $expectedPublisher -or
        $identity.GetAttribute("Version") -ne $ExpectedVersion) {
        throw "The package identity is outside lifecycle-smoke policy."
    }

    if ($Built -and $identity.GetAttribute("ProcessorArchitecture") -ne "x64") {
        throw "The built package is not x64."
    }

    $applications = @($Manifest.SelectNodes("/*[local-name()='Package']/*[local-name()='Applications']/*[local-name()='Application']"))
    if ($applications.Count -ne 1 -or $applications[0].GetAttribute("Id") -ne $expectedApplicationId) {
        throw "The package must contain exactly the lifecycle harness application."
    }

    if ($Built) {
        if ($applications[0].GetAttribute("Executable") -ne "IptvSuite.PackageLifecycleHarness.exe" -or
            $applications[0].GetAttribute("EntryPoint") -ne "Windows.FullTrustApplication") {
            throw "The built lifecycle application entry point is outside policy."
        }
    }
    elseif ($applications[0].GetAttribute("Executable") -ne '$targetnametoken$.exe' -or
        $applications[0].GetAttribute("EntryPoint") -ne '$targetentrypoint$') {
        throw "The source lifecycle application placeholders are outside policy."
    }

    $visualElements = @($applications[0].SelectNodes("./*[local-name()='VisualElements']"))
    if ($visualElements.Count -ne 1 -or $visualElements[0].GetAttribute("AppListEntry") -ne "none") {
        throw "The lifecycle harness must remain hidden from the app list."
    }

    $capabilities = @(
        $Manifest.SelectNodes("/*[local-name()='Package']/*[local-name()='Capabilities']/*") |
            ForEach-Object { $_.GetAttribute("Name") }
    )
    if ($capabilities.Count -ne 1 -or $capabilities[0] -ne "runFullTrust") {
        throw "The lifecycle harness capability set is outside policy."
    }

    $targetFamilies = @($Manifest.SelectNodes("/*[local-name()='Package']/*[local-name()='Dependencies']/*[local-name()='TargetDeviceFamily']"))
    if ($targetFamilies.Count -ne 1 -or
        $targetFamilies[0].GetAttribute("Name") -ne "Windows.Desktop" -or
        $targetFamilies[0].GetAttribute("MinVersion") -ne "10.0.26100.0" -or
        $targetFamilies[0].GetAttribute("MaxVersionTested") -ne "10.0.26100.0") {
        throw "The lifecycle harness Windows baseline is outside policy."
    }

    if ($Built) {
        $frameworkDependencies = @($Manifest.SelectNodes("/*[local-name()='Package']/*[local-name()='Dependencies']/*[local-name()='PackageDependency']"))
        if ($frameworkDependencies.Count -ne 1 -or
            $frameworkDependencies[0].GetAttribute("Name") -ne "Microsoft.WindowsAppRuntime.2" -or
            $frameworkDependencies[0].GetAttribute("MinVersion") -ne "2.3.1.0") {
            throw "The lifecycle MSIX must use the exact Windows App Runtime dependency."
        }
    }
}

function Get-BuiltPackageArtifacts {
    param(
        [Parameter(Mandatory)]
        [string]$PackageOutput,

        [Parameter(Mandatory)]
        [string]$ExpectedPackageFileName
    )

    Assert-SafeDirectory -Path $PackageOutput
    $packages = @(
        Get-ChildItem -LiteralPath $PackageOutput -Filter "*.msix" -Recurse -File |
            Where-Object { $_.FullName -notmatch "[\\/]Dependencies[\\/]" }
    )
    if ($packages.Count -ne 1 -or $packages[0].Name -ne $ExpectedPackageFileName) {
        throw "The lifecycle build did not produce the exact x64 MSIX."
    }

    $runtimeDependencies = @(
        Get-ChildItem -LiteralPath $PackageOutput -Filter "Microsoft.WindowsAppRuntime.2.msix" -Recurse -File |
            Where-Object { $_.Directory.Name -eq "x64" }
    )
    if ($runtimeDependencies.Count -ne 1) {
        throw "The lifecycle build did not produce the exact x64 runtime dependency."
    }

    Assert-RegularFile -Path $packages[0].FullName
    Assert-RegularFile -Path $runtimeDependencies[0].FullName
    return [pscustomobject]@{
        Package = $packages[0]
        RuntimeDependency = $runtimeDependencies[0]
    }
}

function Get-ValidatedPackageSignature {
    param(
        [Parameter(Mandatory)]
        [System.IO.FileInfo]$Package,

        [Parameter(Mandatory)]
        [System.Security.Cryptography.X509Certificates.X509Certificate2]$Certificate
    )

    $signature = Get-AuthenticodeSignature -FilePath $Package.FullName
    if ($null -eq $signature.SignerCertificate -or
        $signature.SignerCertificate.Thumbprint -ne $Certificate.Thumbprint -or
        $signature.Status -ne [System.Management.Automation.SignatureStatus]::Valid) {
        throw "The lifecycle MSIX signature is invalid."
    }

    return $signature.Status.ToString()
}

function Assert-HarnessResult {
    param(
        [Parameter(Mandatory)]
        [object]$Result,

        [Parameter(Mandatory)]
        [ValidateSet("Create", "DuplicateCreate", "VerifyDelete")]
        [string]$Kind
    )

    $expectedProperties = @(
        "SchemaVersion",
        "Phase",
        "Succeeded",
        "Failure",
        "CreateCommitted",
        "DuplicateCreateSuppressed",
        "InitialReadVerified",
        "WrongOwnerReadRejected",
        "WrongOwnerDeleteIdempotent",
        "CorrectRecordSurvivedWrongOwnerDelete",
        "UpdateCommitted",
        "UpdatedReadVerified",
        "DeleteCommitted",
        "PostDeleteUnavailable",
        "TicketRemoved"
    )
    $actualProperties = @($Result.PSObject.Properties | ForEach-Object { $_.Name })
    $propertyDifference = @(Compare-Object -ReferenceObject $expectedProperties -DifferenceObject $actualProperties)
    if ($propertyDifference.Count -ne 0) {
        throw "The harness result schema contains an unexpected property set."
    }

    foreach ($numericProperty in @("SchemaVersion", "Phase", "Failure")) {
        if ($Result.$numericProperty -isnot [int] -and $Result.$numericProperty -isnot [long]) {
            throw "The harness result contains a non-numeric enum or schema field."
        }
    }
    foreach ($booleanProperty in $expectedProperties | Where-Object { $_ -notin @("SchemaVersion", "Phase", "Failure") }) {
        if ($Result.$booleanProperty -isnot [bool]) {
            throw "The harness result contains a non-Boolean outcome field."
        }
    }

    if ([int]$Result.SchemaVersion -ne 1) {
        throw "The harness result schema version is unsupported."
    }

    $expected = switch ($Kind) {
        "Create" {
            [ordered]@{
                Phase = 1; Succeeded = $true; Failure = 0; CreateCommitted = $true;
                DuplicateCreateSuppressed = $false; InitialReadVerified = $false;
                WrongOwnerReadRejected = $false; WrongOwnerDeleteIdempotent = $false;
                CorrectRecordSurvivedWrongOwnerDelete = $false; UpdateCommitted = $false;
                UpdatedReadVerified = $false; DeleteCommitted = $false;
                PostDeleteUnavailable = $false; TicketRemoved = $false
            }
        }
        "DuplicateCreate" {
            [ordered]@{
                Phase = 1; Succeeded = $false; Failure = 5; CreateCommitted = $false;
                DuplicateCreateSuppressed = $true; InitialReadVerified = $false;
                WrongOwnerReadRejected = $false; WrongOwnerDeleteIdempotent = $false;
                CorrectRecordSurvivedWrongOwnerDelete = $false; UpdateCommitted = $false;
                UpdatedReadVerified = $false; DeleteCommitted = $false;
                PostDeleteUnavailable = $false; TicketRemoved = $false
            }
        }
        "VerifyDelete" {
            [ordered]@{
                Phase = 2; Succeeded = $true; Failure = 0; CreateCommitted = $false;
                DuplicateCreateSuppressed = $false; InitialReadVerified = $true;
                WrongOwnerReadRejected = $true; WrongOwnerDeleteIdempotent = $true;
                CorrectRecordSurvivedWrongOwnerDelete = $true; UpdateCommitted = $true;
                UpdatedReadVerified = $true; DeleteCommitted = $true;
                PostDeleteUnavailable = $true; TicketRemoved = $true
            }
        }
    }

    foreach ($entry in $expected.GetEnumerator()) {
        if ($Result.($entry.Key) -ne $entry.Value) {
            throw "The harness result does not match the expected lifecycle outcome."
        }
    }
}

function Read-HarnessResult {
    param(
        [Parameter(Mandatory)]
        [string]$Path
    )

    Assert-RegularFile -Path $Path
    $fileInfo = Get-Item -LiteralPath $Path -Force
    if ($fileInfo.Length -le 0 -or $fileInfo.Length -gt 2048) {
        throw "The harness result is outside its size bound."
    }

    $json = [System.IO.File]::ReadAllText($fileInfo.FullName, [System.Text.Encoding]::UTF8)
    try {
        return $json | ConvertFrom-Json -ErrorAction Stop
    }
    finally {
        $json = $null
    }
}

function New-EmptyReleaseMarker {
    param(
        [Parameter(Mandatory)]
        [string]$Path
    )

    $parent = [System.IO.Path]::GetDirectoryName([System.IO.Path]::GetFullPath($Path))
    Assert-SafeDirectory -Path $parent
    if (Test-Path -LiteralPath $Path) {
        throw "The release marker already exists."
    }

    $stream = $null
    try {
        $stream = [System.IO.File]::Open(
            $Path,
            [System.IO.FileMode]::CreateNew,
            [System.IO.FileAccess]::Write,
            [System.IO.FileShare]::None)
        $stream.Flush()
    }
    finally {
        if ($null -ne $stream) {
            $stream.Dispose()
        }
    }
}

function Remove-ExactPhaseFiles {
    param(
        [Parameter(Mandatory)]
        [ValidateSet("create", "verify-delete")]
        [string]$Phase
    )

    $resultLeaf = if ($Phase -eq "create") { "result-create.json" } else { "result-verify-delete.json" }
    $releaseLeaf = if ($Phase -eq "create") { "release-create.ready" } else { "release-verify-delete.ready" }
    foreach ($path in @((Join-Path $script:runDirectory $resultLeaf), (Join-Path $script:runDirectory $releaseLeaf))) {
        if (Test-Path -LiteralPath $path) {
            Assert-RegularFile -Path $path
            if ([System.IO.Path]::GetFileName($path) -eq $releaseLeaf -and
                (Get-Item -LiteralPath $path -Force).Length -ne 0) {
                throw "The release marker was modified."
            }
            Remove-Item -LiteralPath $path -Force -ErrorAction Stop
        }
    }
}

function Invoke-HarnessPhase {
    param(
        [Parameter(Mandatory)]
        [ValidateSet("create", "verify-delete")]
        [string]$Phase,

        [Parameter(Mandatory)]
        [ValidateSet("Create", "DuplicateCreate", "VerifyDelete")]
        [string]$ExpectedResult,

        [Parameter(Mandatory)]
        [int]$ExpectedExitCode
    )

    $resultLeaf = if ($Phase -eq "create") { "result-create.json" } else { "result-verify-delete.json" }
    $releaseLeaf = if ($Phase -eq "create") { "release-create.ready" } else { "release-verify-delete.ready" }
    $resultPath = Join-Path $script:runDirectory $resultLeaf
    $releasePath = Join-Path $script:runDirectory $releaseLeaf
    if ((Test-Path -LiteralPath $resultPath) -or (Test-Path -LiteralPath $releasePath)) {
        throw "A phase handshake file already exists."
    }

    $existingProcesses = @(Get-Process -Name $expectedProcessName -ErrorAction SilentlyContinue)
    if ($existingProcesses.Count -ne 0) {
        throw "The lifecycle harness process is already running."
    }

    $arguments = "--phase $Phase --run-id $runId"
    $activationProcessId = [IptvSuite.PackageLifecycleSmoke.PackagedApplicationActivator]::Activate(
        $script:aumid,
        $arguments)
    $script:activeProcess = Get-Process -Id $activationProcessId -ErrorAction SilentlyContinue
    if ($null -eq $script:activeProcess) {
        throw "The lifecycle harness exited before its process could be observed."
    }

    try {
        $null = $script:activeProcess.Handle
    }
    catch {
        throw "The lifecycle harness exited before its process handle could be retained."
    }

    $script:activeProcess.Refresh()
    if ($script:activeProcess.HasExited -or $script:activeProcess.ProcessName -ne $expectedProcessName) {
        throw "Package activation returned an unexpected lifecycle process."
    }

    $resultDeadline = (Get-Date).AddSeconds(20)
    while (-not (Test-Path -LiteralPath $resultPath -PathType Leaf) -and (Get-Date) -lt $resultDeadline) {
        $script:activeProcess.Refresh()
        if ($script:activeProcess.HasExited) {
            throw "The lifecycle harness exited before publishing its result."
        }

        Start-Sleep -Milliseconds 100
    }
    if (-not (Test-Path -LiteralPath $resultPath -PathType Leaf)) {
        throw "The lifecycle harness did not publish a bounded result."
    }

    $result = Read-HarnessResult -Path $resultPath
    Assert-HarnessResult -Result $result -Kind $ExpectedResult
    New-EmptyReleaseMarker -Path $releasePath

    if (-not $script:activeProcess.WaitForExit(10000)) {
        throw "The lifecycle harness did not exit after release."
    }
    $script:activeProcess.Refresh()
    $exitCode = $script:activeProcess.ExitCode
    if ($null -eq $exitCode -or [int]$exitCode -ne $ExpectedExitCode) {
        throw "The lifecycle harness returned an unexpected fixed exit code."
    }

    $script:activeProcess.Dispose()
    $script:activeProcess = $null
    return $result
}

function Get-ExactProtectedRecordLeaf {
    Assert-SafeDirectory -Path $script:protectedStorePath
    $entries = @(Get-ChildItem -LiteralPath $script:protectedStorePath -Force)
    foreach ($entry in $entries) {
        if ($entry.PSIsContainer -or ($entry.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "The protected store contains an unsafe entry."
        }
    }

    $records = @($entries | Where-Object { $_.Name -cmatch '\Arecord-v2-[0-9A-F]{64}\.dpapi\z' })
    $temporary = @($entries | Where-Object { $_.Name -match '\Atemporary-v2-' })
    if ($records.Count -ne 1 -or $temporary.Count -ne 0 -or $entries.Count -ne 1) {
        throw "The protected store does not contain exactly one committed v2 record."
    }

    Assert-RegularFile -Path $records[0].FullName
    return [string]$records[0].Name
}

function Assert-ProtectedStoreCreated {
    $null = Get-ExactProtectedRecordLeaf
}

function Assert-ProtectedStoreClean {
    Assert-SafeDirectory -Path $script:protectedStorePath
    $entries = @(Get-ChildItem -LiteralPath $script:protectedStorePath -Force)
    if ($entries.Count -ne 0) {
        throw "The lifecycle operation left an unexpected protected-store entry."
    }

    if (Test-Path -LiteralPath $script:ticketPath) {
        throw "The lifecycle operation left its control ticket."
    }
}

function Assert-OwnedLifecycleStateAbsent {
    $deadline = (Get-Date).AddSeconds(15)
    while (((Test-Path -LiteralPath $script:protectedStorePath) -or
            (Test-Path -LiteralPath $script:runDirectory)) -and
        (Get-Date) -lt $deadline) {
        Start-Sleep -Milliseconds 250
    }

    if ((Test-Path -LiteralPath $script:protectedStorePath) -or
        (Test-Path -LiteralPath $script:runDirectory)) {
        throw "The package lifecycle operation retained app-owned state."
    }
}

function Assert-ExactAppDataAbsent {
    if ([string]::IsNullOrWhiteSpace($script:appDataPath) -or
        [string]::IsNullOrWhiteSpace($script:packageFamilyName)) {
        throw "The exact package app-data identity is unavailable."
    }

    $packagesRoot = [System.IO.Path]::GetFullPath((Join-Path $env:LOCALAPPDATA "Packages"))
    $resolvedPath = [System.IO.Path]::GetFullPath($script:appDataPath)
    $expectedPath = [System.IO.Path]::GetFullPath((Join-Path $packagesRoot $script:packageFamilyName))
    $parent = [System.IO.Directory]::GetParent($resolvedPath)
    if (-not $resolvedPath.Equals($expectedPath, [System.StringComparison]::OrdinalIgnoreCase) -or
        $null -eq $parent -or
        -not $parent.FullName.Equals($packagesRoot, [System.StringComparison]::OrdinalIgnoreCase) -or
        [System.IO.Path]::GetFileName($resolvedPath) -ne $script:packageFamilyName) {
        throw "Refusing to inspect an unexpected package app-data directory."
    }

    $deadline = (Get-Date).AddSeconds(15)
    while ((Test-Path -LiteralPath $resolvedPath) -and (Get-Date) -lt $deadline) {
        Start-Sleep -Milliseconds 250
    }

    if (Test-Path -LiteralPath $resolvedPath) {
        throw "The package deployment operation retained app data."
    }
}

function Invoke-OwnedCanaryScan {
    foreach ($scanRoot in @($script:protectedStorePath, $script:runDirectory)) {
        Assert-SafeDirectory -Path $scanRoot
        & $DotNetPath $testingToolPath scan-artifacts $scanRoot M4 PACKAGE_LIFECYCLE_CREATE *> $null
        $scannerExitCode = $LASTEXITCODE
        switch ($scannerExitCode) {
            0 { }
            1 {
                $script:failureCode = "CanaryScannerOperationalFailure"
                throw "The packaged lifecycle canary scanner could not complete."
            }
            2 {
                $script:failureCode = "CanaryArtifactDetected"
                throw "A packaged lifecycle write surface contains a test-canary artifact."
            }
            default {
                $script:failureCode = "CanaryScannerContractFailure"
                throw "The packaged lifecycle canary scanner returned an unsupported result."
            }
        }
    }
}

function Invoke-CleanupStep {
    param(
        [Parameter(Mandatory)]
        [string]$Code,

        [Parameter(Mandatory)]
        [scriptblock]$Action
    )

    try {
        & $Action
    }
    catch {
        $script:cleanupFailures.Add($Code) | Out-Null
    }
}

function Remove-ExactLifecyclePackage {
    param(
        [string]$ExpectedPackageFullName
    )

    $packages = @(
        Get-AppxPackage -Name $expectedName -ErrorAction SilentlyContinue |
            Where-Object { $_.Name -eq $expectedName -and $_.Publisher -eq $expectedPublisher }
    )
    if ($packages.Count -gt 1 -or
        (-not [string]::IsNullOrWhiteSpace($ExpectedPackageFullName) -and $packages.Count -ne 1)) {
        throw "The exact lifecycle package registration count is outside policy."
    }

    if ($packages.Count -eq 1) {
        if (-not [string]::IsNullOrWhiteSpace($ExpectedPackageFullName) -and
            $packages[0].PackageFullName -ne $ExpectedPackageFullName) {
            throw "The registered lifecycle package does not match the expected package full name."
        }

        if ($null -eq $script:packageFamilyName) {
            $script:packageFamilyName = $packages[0].PackageFamilyName
            $script:appDataPath = Join-Path $env:LOCALAPPDATA "Packages\$($script:packageFamilyName)"
        }
        Remove-AppxPackage -Package $packages[0].PackageFullName -ErrorAction Stop
    }
}

function Remove-ExactAppData {
    if ([string]::IsNullOrWhiteSpace($script:appDataPath) -or
        [string]::IsNullOrWhiteSpace($script:packageFamilyName)) {
        return
    }

    $packagesRoot = [System.IO.Path]::GetFullPath((Join-Path $env:LOCALAPPDATA "Packages"))
    $resolvedPath = [System.IO.Path]::GetFullPath($script:appDataPath)
    $expectedPath = [System.IO.Path]::GetFullPath((Join-Path $packagesRoot $script:packageFamilyName))
    $parent = [System.IO.Directory]::GetParent($resolvedPath)
    if (-not $resolvedPath.Equals($expectedPath, [System.StringComparison]::OrdinalIgnoreCase) -or
        $null -eq $parent -or
        -not $parent.FullName.Equals($packagesRoot, [System.StringComparison]::OrdinalIgnoreCase) -or
        [System.IO.Path]::GetFileName($resolvedPath) -ne $script:packageFamilyName) {
        throw "Refusing cleanup of an unexpected package app-data directory."
    }

    $deadline = (Get-Date).AddSeconds(10)
    while ((Test-Path -LiteralPath $resolvedPath) -and (Get-Date) -lt $deadline) {
        Start-Sleep -Milliseconds 250
    }
    if (-not (Test-Path -LiteralPath $resolvedPath)) {
        return
    }

    Assert-SafeDirectory -Path $packagesRoot
    Assert-SafeDirectory -Path $resolvedPath
    $pending = [System.Collections.Generic.Queue[System.IO.DirectoryInfo]]::new()
    $pending.Enqueue((Get-Item -LiteralPath $resolvedPath -Force))
    while ($pending.Count -gt 0) {
        $directory = $pending.Dequeue()
        foreach ($entry in $directory.GetFileSystemInfos()) {
            if (($entry.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw "Refusing app-data cleanup through a reparse point."
            }
            if ($entry -is [System.IO.DirectoryInfo]) {
                $pending.Enqueue($entry)
            }
        }
    }

    Remove-Item -LiteralPath $resolvedPath -Recurse -Force -ErrorAction Stop
    if (Test-Path -LiteralPath $resolvedPath) {
        throw "The exact package app-data directory remains after cleanup."
    }
}

function Remove-ExactPackageOutput {
    if (-not [System.Text.RegularExpressions.Regex]::IsMatch($runId, '\A[0-9a-f]{32}\z')) {
        throw "Refusing package-output cleanup because the run identifier is invalid."
    }

    $resolvedArtifactRoot = [System.IO.Path]::GetFullPath($artifactRoot)
    $packagesRoot = [System.IO.Path]::GetFullPath((Join-Path $resolvedArtifactRoot "packages"))
    $resolvedOutput = [System.IO.Path]::GetFullPath($packageOutput)
    $expectedOutput = [System.IO.Path]::GetFullPath((Join-Path $packagesRoot $runId))
    $parent = [System.IO.Directory]::GetParent($resolvedOutput)
    if (-not $resolvedOutput.Equals($expectedOutput, [System.StringComparison]::OrdinalIgnoreCase) -or
        $null -eq $parent -or
        -not $parent.FullName.Equals($packagesRoot, [System.StringComparison]::OrdinalIgnoreCase) -or
        [System.IO.Path]::GetFileName($resolvedOutput) -ne $runId) {
        throw "Refusing cleanup of an unexpected package-output directory."
    }

    if (-not (Test-Path -LiteralPath $resolvedOutput)) {
        return
    }

    foreach ($directoryPath in @($resolvedArtifactRoot, $packagesRoot, $resolvedOutput)) {
        Assert-SafeDirectory -Path $directoryPath
    }
    $pending = [System.Collections.Generic.Queue[System.IO.DirectoryInfo]]::new()
    $pending.Enqueue((Get-Item -LiteralPath $resolvedOutput -Force))
    while ($pending.Count -gt 0) {
        $directory = $pending.Dequeue()
        foreach ($entry in $directory.GetFileSystemInfos()) {
            if (($entry.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw "Refusing package-output cleanup through a reparse point."
            }
            if ($entry -is [System.IO.DirectoryInfo]) {
                $pending.Enqueue($entry)
            }
        }
    }

    Remove-Item -LiteralPath $resolvedOutput -Recurse -Force -ErrorAction Stop
}

function Write-JsonAtomically {
    param(
        [Parameter(Mandatory)]
        [object]$Value,

        [Parameter(Mandatory)]
        [string]$DestinationPath
    )

    $resolvedRoot = [System.IO.Path]::GetFullPath($artifactRoot)
    $resolvedDestination = [System.IO.Path]::GetFullPath($DestinationPath)
    $allowed = @(
        [System.IO.Path]::GetFullPath($successEvidencePath),
        [System.IO.Path]::GetFullPath($failureEvidencePath)
    )
    if ($resolvedDestination -notin $allowed -or
        -not [System.IO.Directory]::GetParent($resolvedDestination).FullName.Equals(
            $resolvedRoot,
            [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to write evidence outside the exact artifact root."
    }
    Assert-SafeDirectory -Path $resolvedRoot

    if (Test-Path -LiteralPath $resolvedDestination) {
        throw "Refusing to overwrite existing lifecycle evidence."
    }

    $temporaryPath = "$resolvedDestination.$runId.tmp"
    if (Test-Path -LiteralPath $temporaryPath) {
        throw "The lifecycle evidence temporary file already exists."
    }

    try {
        $Value | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $temporaryPath -Encoding UTF8
        Assert-RegularFile -Path $temporaryPath
        [System.IO.File]::Move($temporaryPath, $resolvedDestination)
    }
    finally {
        if (Test-Path -LiteralPath $temporaryPath) {
            Assert-RegularFile -Path $temporaryPath
            Remove-Item -LiteralPath $temporaryPath -Force -ErrorAction Stop
        }
    }
}

try {
    Set-FailurePoint -Stage "ArtifactPreparation" -Code "ArtifactRootInvalid"
    $resolvedRepositoryRoot = [System.IO.Path]::GetFullPath($repositoryRoot)
    $resolvedArtifactsRoot = [System.IO.Path]::GetFullPath($artifactsRoot)
    $resolvedArtifactRoot = [System.IO.Path]::GetFullPath($artifactRoot)
    if (-not [System.IO.Directory]::GetParent($resolvedArtifactsRoot).FullName.Equals(
            $resolvedRepositoryRoot,
            [System.StringComparison]::OrdinalIgnoreCase) -or
        -not [System.IO.Directory]::GetParent($resolvedArtifactRoot).FullName.Equals(
            $resolvedArtifactsRoot,
            [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "The lifecycle artifact path is outside the repository artifact root."
    }
    Assert-SafeDirectory -Path $resolvedRepositoryRoot
    if (-not (Test-Path -LiteralPath $artifactsRoot)) {
        New-Item -ItemType Directory -Path $artifactsRoot | Out-Null
    }
    Assert-SafeDirectory -Path $artifactsRoot
    if (-not (Test-Path -LiteralPath $artifactRoot)) {
        New-Item -ItemType Directory -Path $artifactRoot | Out-Null
    }
    Assert-SafeDirectory -Path $artifactRoot
    foreach ($staleEvidence in @($successEvidencePath, $failureEvidencePath)) {
        if (Test-Path -LiteralPath $staleEvidence) {
            Assert-RegularFile -Path $staleEvidence
            Remove-Item -LiteralPath $staleEvidence -Force -ErrorAction Stop
        }
    }

    Set-FailurePoint -Stage "HostValidation" -Code "ElevationRequired"
    $currentIdentity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $currentPrincipal = [Security.Principal.WindowsPrincipal]::new($currentIdentity)
    if (-not $currentPrincipal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw "The lifecycle smoke requires an elevated Windows host."
    }

    Set-FailurePoint -Stage "HostValidation" -Code "EnableLuaRequired"
    $enableLua = Get-ItemPropertyValue `
        -LiteralPath "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System" `
        -Name "EnableLUA" `
        -ErrorAction Stop
    if ([int]$enableLua -ne 1) {
        throw "The Windows app-model activation service is disabled."
    }

    Set-FailurePoint -Stage "SdkValidation" -Code "SdkMismatch"
    $expectedSdk = (Get-Content -Raw (Join-Path $repositoryRoot "global.json") | ConvertFrom-Json).sdk.version
    $actualSdk = (& $DotNetPath --version).Trim()
    if ($LASTEXITCODE -ne 0 -or $actualSdk -ne $expectedSdk) {
        throw "The exact .NET SDK is unavailable."
    }

    Set-FailurePoint -Stage "ManifestValidation" -Code "ManifestPolicyFailed"
    [xml]$sourceManifest = Get-Content -Raw -LiteralPath $sourceManifestPath
    [xml]$sourceUpdateManifest = Get-Content -Raw -LiteralPath $sourceUpdateManifestPath
    Assert-ManifestPolicy -Manifest $sourceManifest -ExpectedVersion $baselineVersion
    Assert-ManifestPolicy -Manifest $sourceUpdateManifest -ExpectedVersion $updatedVersion
    $storeAssociations = @(Get-ChildItem -Path (Join-Path $repositoryRoot "apps") -Filter "Package.StoreAssociation.xml" -Recurse -File)
    if ($storeAssociations.Count -ne 0) {
        throw "Store association is forbidden for the disposable lifecycle identity."
    }

    Set-FailurePoint -Stage "PackagePreparation" -Code "StalePackageCleanupFailed"
    Remove-ExactLifecyclePackage
    Remove-ExactAppData
    $script:packageFamilyName = $null
    $script:appDataPath = $null

    if (-not (Test-Path -LiteralPath $packageOutputRoot)) {
        New-Item -ItemType Directory -Path $packageOutputRoot | Out-Null
    }
    Assert-SafeDirectory -Path $packageOutputRoot
    if (Test-Path -LiteralPath $packageOutput) {
        throw "The exact package-output directory already exists."
    }
    New-Item -ItemType Directory -Path $packageOutput | Out-Null
    Assert-SafeDirectory -Path $packageOutput

    Set-FailurePoint -Stage "CertificateCreation" -Code "CertificateCreationFailed"
    $certificate = New-SelfSignedCertificate `
        -Type Custom `
        -Subject $expectedPublisher `
        -CertStoreLocation "Cert:\CurrentUser\My" `
        -KeyAlgorithm RSA `
        -KeyLength 2048 `
        -HashAlgorithm SHA256 `
        -KeyExportPolicy NonExportable `
        -KeyUsage DigitalSignature `
        -NotAfter (Get-Date).AddDays(1) `
        -TextExtension @(
            "2.5.29.37={text}1.3.6.1.5.5.7.3.3",
            "2.5.29.19={text}"
        )
    if (-not $certificate.HasPrivateKey -or $certificate.Subject -ne $expectedPublisher) {
        throw "The ephemeral signing certificate is outside policy."
    }

    $ekuExtension = $certificate.Extensions |
        Where-Object { $_.Oid.Value -eq "2.5.29.37" } |
        Select-Object -First 1
    if ($null -eq $ekuExtension) {
        throw "The ephemeral signing certificate has no EKU."
    }
    $enhancedKeyUsage = [System.Security.Cryptography.X509Certificates.X509EnhancedKeyUsageExtension]$ekuExtension
    $codeSigningUsage = @($enhancedKeyUsage.EnhancedKeyUsages | ForEach-Object { $_.Value })
    if ($codeSigningUsage -notcontains "1.3.6.1.5.5.7.3.3") {
        throw "The ephemeral signing certificate is not code-signing-only."
    }

    Export-Certificate -Cert $certificate -FilePath $publicCertificatePath | Out-Null
    Import-Certificate -FilePath $publicCertificatePath -CertStoreLocation "Cert:\LocalMachine\TrustedPeople" | Out-Null

    Set-FailurePoint -Stage "UpdatedPackageBuild" -Code "UpdatedSignedBuildFailed"
    $msBuildEnvironment.PackageCertificateThumbprint = $certificate.Thumbprint
    foreach ($entry in $msBuildEnvironment.GetEnumerator()) {
        $environmentBackup[$entry.Key] = [Environment]::GetEnvironmentVariable($entry.Key, "Process")
        [Environment]::SetEnvironmentVariable($entry.Key, $entry.Value, "Process")
    }
    & $DotNetPath build $projectPath -c $Configuration -p:Platform=x64 `
        "-p:AppxPackageDir=$updatedPackageOutput/" `
        -p:LifecyclePackageFlavor=Update -t:Rebuild --no-restore --nologo
    if ($LASTEXITCODE -ne 0) {
        throw "The signed updated lifecycle MSIX build failed."
    }

    Set-FailurePoint -Stage "BaselinePackageBuild" -Code "BaselineSignedBuildFailed"
    & $DotNetPath build $projectPath -c $Configuration -p:Platform=x64 `
        "-p:AppxPackageDir=$baselinePackageOutput/" `
        -p:LifecyclePackageFlavor=Baseline -t:Rebuild --no-restore --nologo
    if ($LASTEXITCODE -ne 0) {
        throw "The signed baseline lifecycle MSIX build failed."
    }

    Set-FailurePoint -Stage "ScannerBuild" -Code "ScannerBuildFailed"
    & $DotNetPath build $testingProjectPath -c Release --no-restore --nologo
    if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $testingToolPath -PathType Leaf)) {
        throw "The test-only artifact scanner build failed."
    }

    Set-FailurePoint -Stage "PackageInspection" -Code "PackageOutputInvalid"
    $baselineArtifacts = Get-BuiltPackageArtifacts `
        -PackageOutput $baselinePackageOutput `
        -ExpectedPackageFileName $baselinePackageFileName
    $updatedArtifacts = Get-BuiltPackageArtifacts `
        -PackageOutput $updatedPackageOutput `
        -ExpectedPackageFileName $updatedPackageFileName
    $baselineSignatureStatus = Get-ValidatedPackageSignature `
        -Package $baselineArtifacts.Package `
        -Certificate $certificate
    $updatedSignatureStatus = Get-ValidatedPackageSignature `
        -Package $updatedArtifacts.Package `
        -Certificate $certificate
    $baselinePackageSha256 = (Get-FileHash `
        -LiteralPath $baselineArtifacts.Package.FullName `
        -Algorithm SHA256).Hash.ToLowerInvariant()
    $updatedPackageSha256 = (Get-FileHash `
        -LiteralPath $updatedArtifacts.Package.FullName `
        -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($baselinePackageSha256 -eq $updatedPackageSha256) {
        throw "The baseline and updated lifecycle packages must have distinct content."
    }

    Set-FailurePoint -Stage "BaselinePackageInstall" -Code "BaselinePackageInstallFailed"
    $installAttempted = $true
    Add-AppxPackage `
        -Path $baselineArtifacts.Package.FullName `
        -DependencyPath $baselineArtifacts.RuntimeDependency.FullName `
        -ErrorAction Stop
    $installedPackages = @(
        Get-AppxPackage -Name $expectedName -ErrorAction Stop |
            Where-Object { $_.Name -eq $expectedName -and $_.Publisher -eq $expectedPublisher }
    )
    if ($installedPackages.Count -ne 1) {
        throw "The exact baseline lifecycle package was not installed."
    }
    $installedPackage = $installedPackages[0]
    if ($installedPackage.Architecture -ne "X64" -or
        $installedPackage.Version.ToString() -ne $baselineVersion) {
        throw "The installed baseline lifecycle package is outside architecture or version policy."
    }
    [xml]$installedManifest = ($installedPackage | Get-AppxPackageManifest).Package.OuterXml
    Assert-ManifestPolicy -Manifest $installedManifest -ExpectedVersion $baselineVersion -Built

    $packageFamilyName = $installedPackage.PackageFamilyName
    if ([string]::IsNullOrWhiteSpace($packageFamilyName) -or
        $packageFamilyName.IndexOfAny([System.IO.Path]::GetInvalidFileNameChars()) -ge 0) {
        throw "The installed package family identifier is unsafe."
    }
    $baselinePackageFullName = $installedPackage.PackageFullName
    if ([string]::IsNullOrWhiteSpace($baselinePackageFullName)) {
        throw "The installed baseline package full name is unavailable."
    }
    $appDataPath = Join-Path $env:LOCALAPPDATA "Packages\$packageFamilyName"
    $localCachePath = Join-Path $appDataPath "LocalCache"
    $protectedStorePath = Join-Path $localCachePath "ProtectedStore\v2"
    $runDirectory = Join-Path $localCachePath "LifecycleHarness\v1\runs\$runId"
    $ticketPath = Join-Path $runDirectory "control-ticket.dpapi"
    $aumid = "$packageFamilyName!$expectedApplicationId"

    Set-FailurePoint -Stage "CreateLaunch" -Code "CreatePhaseFailed"
    $createResult = Invoke-HarnessPhase -Phase "create" -ExpectedResult "Create" -ExpectedExitCode 0
    Assert-ProtectedStoreCreated
    Assert-RegularFile -Path $ticketPath

    Set-FailurePoint -Stage "CreateScan" -Code "CreateCanaryScanFailed"
    Invoke-OwnedCanaryScan
    Remove-ExactPhaseFiles -Phase "create"

    Set-FailurePoint -Stage "DuplicateCreateLaunch" -Code "DuplicateCreateGateFailed"
    $duplicateResult = Invoke-HarnessPhase -Phase "create" -ExpectedResult "DuplicateCreate" -ExpectedExitCode 65
    Assert-ProtectedStoreCreated
    Assert-RegularFile -Path $ticketPath
    Remove-ExactPhaseFiles -Phase "create"

    Set-FailurePoint -Stage "PackageUpdate" -Code "PackageUpdateFailed"
    Add-AppxPackage `
        -Path $updatedArtifacts.Package.FullName `
        -DependencyPath $updatedArtifacts.RuntimeDependency.FullName `
        -ErrorAction Stop
    $updatedInstalledPackages = @(
        Get-AppxPackage -Name $expectedName -ErrorAction Stop |
            Where-Object { $_.Name -eq $expectedName -and $_.Publisher -eq $expectedPublisher }
    )
    if ($updatedInstalledPackages.Count -ne 1) {
        throw "The exact updated lifecycle package was not installed."
    }
    $installedPackage = $updatedInstalledPackages[0]
    if ($installedPackage.Architecture -ne "X64" -or
        $installedPackage.Version.ToString() -ne $updatedVersion -or
        $installedPackage.PackageFamilyName -ne $packageFamilyName -or
        [string]::IsNullOrWhiteSpace($installedPackage.PackageFullName) -or
        $installedPackage.PackageFullName -eq $baselinePackageFullName) {
        throw "The lifecycle package update did not preserve the family and advance the package identity."
    }
    $updatedPackageFullName = $installedPackage.PackageFullName
    [xml]$updatedInstalledManifest = ($installedPackage | Get-AppxPackageManifest).Package.OuterXml
    Assert-ManifestPolicy -Manifest $updatedInstalledManifest -ExpectedVersion $updatedVersion -Built
    Assert-ProtectedStoreCreated
    Assert-RegularFile -Path $ticketPath

    Set-FailurePoint -Stage "PackageUpdateScan" -Code "PackageUpdateCanaryScanFailed"
    Invoke-OwnedCanaryScan

    Set-FailurePoint -Stage "VerifyDeleteLaunch" -Code "VerifyDeletePhaseFailed"
    $verifyResult = Invoke-HarnessPhase -Phase "verify-delete" -ExpectedResult "VerifyDelete" -ExpectedExitCode 0

    Set-FailurePoint -Stage "LifecycleCleanupValidation" -Code "LifecycleResidueDetected"
    Assert-ProtectedStoreClean

    Set-FailurePoint -Stage "PostUpdateDeleteScan" -Code "PostUpdateDeleteCanaryScanFailed"
    Invoke-OwnedCanaryScan
    Remove-ExactPhaseFiles -Phase "verify-delete"

    Set-FailurePoint -Stage "ResetSeedLaunch" -Code "ResetSeedCreateFailed"
    $preResetResult = Invoke-HarnessPhase -Phase "create" -ExpectedResult "Create" -ExpectedExitCode 0
    $preResetRecordLeaf = Get-ExactProtectedRecordLeaf
    Assert-RegularFile -Path $ticketPath

    Set-FailurePoint -Stage "ResetSeedScan" -Code "ResetSeedCanaryScanFailed"
    Invoke-OwnedCanaryScan

    Set-FailurePoint -Stage "PackageReset" -Code "PackageResetFailed"
    Reset-AppxPackage -Package $installedPackage.PackageFullName -ErrorAction Stop
    $resetInstalledPackages = @(
        Get-AppxPackage -Name $expectedName -ErrorAction Stop |
            Where-Object { $_.Name -eq $expectedName -and $_.Publisher -eq $expectedPublisher }
    )
    if ($resetInstalledPackages.Count -ne 1) {
        throw "The exact lifecycle package registration changed during reset."
    }
    $installedPackage = $resetInstalledPackages[0]
    if ($installedPackage.Architecture -ne "X64" -or
        $installedPackage.Version.ToString() -ne $updatedVersion -or
        $installedPackage.PackageFamilyName -ne $packageFamilyName -or
        $installedPackage.PackageFullName -ne $updatedPackageFullName) {
        throw "The lifecycle package reset changed the installed package identity."
    }
    [xml]$resetInstalledManifest = ($installedPackage | Get-AppxPackageManifest).Package.OuterXml
    Assert-ManifestPolicy -Manifest $resetInstalledManifest -ExpectedVersion $updatedVersion -Built

    Set-FailurePoint -Stage "ResetStateValidation" -Code "ResetOwnedStateRetained"
    Assert-OwnedLifecycleStateAbsent

    Set-FailurePoint -Stage "PostResetCreateLaunch" -Code "PostResetCreateFailed"
    $postResetResult = Invoke-HarnessPhase -Phase "create" -ExpectedResult "Create" -ExpectedExitCode 0
    $postResetRecordLeaf = Get-ExactProtectedRecordLeaf
    Assert-RegularFile -Path $ticketPath
    if ($postResetRecordLeaf -eq $preResetRecordLeaf) {
        throw "Package reset reused the previous protected-record identity."
    }

    Set-FailurePoint -Stage "PostResetCreateScan" -Code "PostResetCanaryScanFailed"
    Invoke-OwnedCanaryScan

    Set-FailurePoint -Stage "LiveStatePackageRemoval" -Code "LiveStatePackageRemovalFailed"
    Remove-ExactLifecyclePackage -ExpectedPackageFullName $updatedPackageFullName
    $remainingPackages = @(
        Get-AppxPackage -Name $expectedName -ErrorAction SilentlyContinue |
            Where-Object { $_.Name -eq $expectedName -and $_.Publisher -eq $expectedPublisher }
    )
    if ($remainingPackages.Count -ne 0) {
        throw "The exact lifecycle package remains registered."
    }
    $installedPackage = $null
    $installAttempted = $false

    Set-FailurePoint -Stage "LiveStateUninstallValidation" -Code "LiveStateAppDataRetained"
    Assert-ExactAppDataAbsent

    Set-FailurePoint -Stage "PackageReinstall" -Code "PackageReinstallFailed"
    $installAttempted = $true
    Add-AppxPackage `
        -Path $updatedArtifacts.Package.FullName `
        -DependencyPath $updatedArtifacts.RuntimeDependency.FullName `
        -ErrorAction Stop
    $reinstalledPackages = @(
        Get-AppxPackage -Name $expectedName -ErrorAction Stop |
            Where-Object { $_.Name -eq $expectedName -and $_.Publisher -eq $expectedPublisher }
    )
    if ($reinstalledPackages.Count -ne 1) {
        throw "The exact lifecycle package was not reinstalled."
    }
    $installedPackage = $reinstalledPackages[0]
    if ($installedPackage.Architecture -ne "X64" -or
        $installedPackage.Version.ToString() -ne $updatedVersion -or
        $installedPackage.PackageFamilyName -ne $packageFamilyName -or
        $installedPackage.PackageFullName -ne $updatedPackageFullName) {
        throw "The reinstalled lifecycle package identity is outside policy."
    }
    [xml]$reinstalledManifest = ($installedPackage | Get-AppxPackageManifest).Package.OuterXml
    Assert-ManifestPolicy -Manifest $reinstalledManifest -ExpectedVersion $updatedVersion -Built

    Set-FailurePoint -Stage "ReinstallStateValidation" -Code "ReinstallOwnedStateRetained"
    Assert-OwnedLifecycleStateAbsent

    Set-FailurePoint -Stage "PostReinstallCreateLaunch" -Code "PostReinstallCreateFailed"
    $postReinstallResult = Invoke-HarnessPhase -Phase "create" -ExpectedResult "Create" -ExpectedExitCode 0
    $postReinstallRecordLeaf = Get-ExactProtectedRecordLeaf
    Assert-RegularFile -Path $ticketPath
    if ($postReinstallRecordLeaf -eq $postResetRecordLeaf) {
        throw "Package reinstall reused the previous protected-record identity."
    }

    Set-FailurePoint -Stage "PostReinstallCreateScan" -Code "PostReinstallCanaryScanFailed"
    Invoke-OwnedCanaryScan
    Remove-ExactPhaseFiles -Phase "create"

    Set-FailurePoint -Stage "FreshVerifyDeleteLaunch" -Code "FreshVerifyDeletePhaseFailed"
    $freshVerifyResult = Invoke-HarnessPhase `
        -Phase "verify-delete" `
        -ExpectedResult "VerifyDelete" `
        -ExpectedExitCode 0

    Set-FailurePoint -Stage "FreshLifecycleCleanupValidation" -Code "FreshLifecycleResidueDetected"
    Assert-ProtectedStoreClean

    Set-FailurePoint -Stage "FinalScan" -Code "FinalCanaryScanFailed"
    Invoke-OwnedCanaryScan
    Remove-ExactPhaseFiles -Phase "verify-delete"

    Set-FailurePoint -Stage "PackageRemoval" -Code "PackageRemovalFailed"
    Remove-ExactLifecyclePackage -ExpectedPackageFullName $updatedPackageFullName
    $remainingPackages = @(
        Get-AppxPackage -Name $expectedName -ErrorAction SilentlyContinue |
            Where-Object { $_.Name -eq $expectedName -and $_.Publisher -eq $expectedPublisher }
    )
    if ($remainingPackages.Count -ne 0) {
        throw "The exact lifecycle package remains registered."
    }
    $installedPackage = $null
    $installAttempted = $false

    Set-FailurePoint -Stage "AppDataRemovalValidation" -Code "AppDataRemovalFailed"
    Assert-ExactAppDataAbsent

    $successEvidence = [ordered]@{
        SchemaVersion = 3
        CompletedAt = (Get-Date).ToUniversalTime().ToString("O")
        Configuration = $Configuration
        DotNetSdk = $actualSdk
        BaselinePackageFile = $baselinePackageFileName
        BaselinePackageSha256 = $baselinePackageSha256
        BaselinePackageVersion = $baselineVersion
        UpdatedPackageFile = $updatedPackageFileName
        UpdatedPackageSha256 = $updatedPackageSha256
        UpdatedPackageVersion = $updatedVersion
        PackageName = $expectedName
        PackagePublisher = $expectedPublisher
        Architecture = "x64"
        Capabilities = @("runFullTrust")
        BaselineSignatureStatus = $baselineSignatureStatus
        UpdatedSignatureStatus = $updatedSignatureStatus
        SameSigner = $true
        SamePackageFamily = $true
        PackageFullNameChanged = $true
        UpdateInstalled = $true
        ProtectedRecordReadAfterPackageUpdate = [bool]$verifyResult.InitialReadVerified
        PostUpdateOwnedSurfaceCanaryScanPassed = $true
        PackageReset = $true
        PackageIdentityPreservedAfterReset = $true
        ResetOwnedStateRemoved = $true
        FreshCreateAfterReset = [bool]$postResetResult.CreateCommitted
        ResetRecordIdentityChanged = $true
        PackageUninstalledWithOwnedState = $true
        UninstallAppDataRemoved = $true
        PackageReinstalled = $true
        PackageIdentityPreservedAfterReinstall = $true
        FreshCreateAfterReinstall = [bool]$postReinstallResult.CreateCommitted
        ReinstallRecordIdentityChanged = $true
        ProtectedStoreVersion = "v2"
        DataProtectionScope = "CurrentUser"
        CreatePersistedAcrossProcessRestart = $true
        DuplicateCreateSuppressed = [bool]$duplicateResult.DuplicateCreateSuppressed
        InitialReadVerified = [bool]$verifyResult.InitialReadVerified
        WrongOwnerReadRejected = [bool]$verifyResult.WrongOwnerReadRejected
        WrongOwnerDeleteIdempotent = [bool]$verifyResult.WrongOwnerDeleteIdempotent
        CorrectRecordSurvivedWrongOwnerDelete = [bool]$verifyResult.CorrectRecordSurvivedWrongOwnerDelete
        UpdateCommitted = [bool]$verifyResult.UpdateCommitted
        UpdatedReadVerified = [bool]$verifyResult.UpdatedReadVerified
        DeleteCommitted = [bool]$verifyResult.DeleteCommitted
        PostDeleteUnavailable = [bool]$verifyResult.PostDeleteUnavailable
        InitialOwnedSurfaceCanaryScanPassed = $true
        FinalOwnedSurfaceCanaryScanPassed = $true
        RecordCleanupPassed = $true
        TicketCleanupPassed = [bool]($verifyResult.TicketRemoved -and $freshVerifyResult.TicketRemoved)
        PackageRemoved = $true
        AppDataRemoved = $true
        CertificateRemoved = $false
        PackageOutputRemoved = $false
    }
    $githubSha = [Environment]::GetEnvironmentVariable("GITHUB_SHA", "Process")
    if (-not [string]::IsNullOrWhiteSpace($githubSha) -and
        [System.Text.RegularExpressions.Regex]::IsMatch($githubSha, '\A[0-9a-fA-F]{40}\z')) {
        $successEvidence.CommitSha = $githubSha.ToLowerInvariant()
    }
}
catch {
    $primaryFailure = $_
}
finally {
    Invoke-CleanupStep -Code "ProcessCleanupFailed" -Action {
        if ($null -ne $script:activeProcess) {
            try {
                $script:activeProcess.Refresh()
                if (-not $script:activeProcess.HasExited) {
                    $script:activeProcess.Kill()
                    if (-not $script:activeProcess.WaitForExit(10000)) {
                        throw "The exact lifecycle process did not stop."
                    }
                }
            }
            finally {
                $script:activeProcess.Dispose()
                $script:activeProcess = $null
            }
        }
    }

    Invoke-CleanupStep -Code "PackageCleanupFailed" -Action {
        if ($installAttempted -or $null -ne $installedPackage) {
            Remove-ExactLifecyclePackage
        }
    }

    Invoke-CleanupStep -Code "AppDataCleanupFailed" -Action {
        Remove-ExactAppData
    }

    foreach ($environmentEntry in @($environmentBackup.GetEnumerator())) {
        $environmentName = [string]$environmentEntry.Key
        $previousValue = $environmentEntry.Value
        Invoke-CleanupStep -Code "EnvironmentCleanupFailed" -Action {
            [Environment]::SetEnvironmentVariable($environmentName, $previousValue, "Process")
        }
    }

    if ($null -ne $certificate) {
        foreach ($certificateStore in @("Cert:\LocalMachine\TrustedPeople", "Cert:\CurrentUser\My")) {
            $store = $certificateStore
            Invoke-CleanupStep -Code "CertificateCleanupFailed" -Action {
                $certificatePath = "$store\$($certificate.Thumbprint)"
                $candidate = Get-Item -LiteralPath $certificatePath -ErrorAction SilentlyContinue
                if ($null -ne $candidate) {
                    if ($candidate.Subject -ne $expectedPublisher) {
                        throw "The exact certificate subject does not match."
                    }
                    Remove-Item -LiteralPath $certificatePath -Force -ErrorAction Stop
                }
            }
        }
    }

    Invoke-CleanupStep -Code "CertificateFileCleanupFailed" -Action {
        if (Test-Path -LiteralPath $publicCertificatePath) {
            Assert-RegularFile -Path $publicCertificatePath
            Remove-Item -LiteralPath $publicCertificatePath -Force -ErrorAction Stop
        }
    }

    Invoke-CleanupStep -Code "PackageOutputCleanupFailed" -Action {
        Remove-ExactPackageOutput
    }

    Invoke-CleanupStep -Code "ExactCleanupVerificationFailed" -Action {
        $remainingPackages = @(
            Get-AppxPackage -Name $expectedName -ErrorAction SilentlyContinue |
                Where-Object { $_.Name -eq $expectedName -and $_.Publisher -eq $expectedPublisher }
        )
        if ($remainingPackages.Count -ne 0) {
            throw "The exact lifecycle package remains after cleanup."
        }
        if (-not [string]::IsNullOrWhiteSpace($script:appDataPath) -and
            (Test-Path -LiteralPath $script:appDataPath)) {
            throw "The exact lifecycle app-data directory remains after cleanup."
        }
        if ($null -ne $certificate) {
            foreach ($certificateStore in @("Cert:\LocalMachine\TrustedPeople", "Cert:\CurrentUser\My")) {
                if (Test-Path -LiteralPath "$certificateStore\$($certificate.Thumbprint)") {
                    throw "The exact lifecycle certificate remains after cleanup."
                }
            }
        }
        if ((Test-Path -LiteralPath $publicCertificatePath) -or
            (Test-Path -LiteralPath $packageOutput)) {
            throw "The exact lifecycle build residue remains after cleanup."
        }
    }
}

if ($null -ne $primaryFailure -or $cleanupFailures.Count -ne 0) {
    if ($cleanupFailures.Count -ne 0) {
        $failureStage = "Cleanup"
        $failureCode = "CleanupFailed"
    }
    $failureEvidence = [ordered]@{
        Stage = $failureStage
        Code = $failureCode
    }
    try {
        Write-JsonAtomically -Value $failureEvidence -DestinationPath $failureEvidencePath
    }
    catch {
        throw "Package lifecycle smoke failed and its stable failure evidence could not be written."
    }

    throw "Package lifecycle smoke failed at $failureStage ($failureCode)."
}

$successEvidence.CertificateRemoved = $true
$successEvidence.PackageOutputRemoved = $true
$successEvidence.CompletedAt = (Get-Date).ToUniversalTime().ToString("O")
Set-FailurePoint -Stage "EvidenceWrite" -Code "SuccessEvidenceWriteFailed"
try {
    Write-JsonAtomically -Value $successEvidence -DestinationPath $successEvidencePath
}
catch {
    $failureEvidence = [ordered]@{
        Stage = $failureStage
        Code = $failureCode
    }
    try {
        Write-JsonAtomically -Value $failureEvidence -DestinationPath $failureEvidencePath
    }
    catch {
        throw "Package lifecycle success and failure evidence could not be written."
    }
    throw "Package lifecycle smoke passed but success evidence could not be written."
}

Write-Host "Packaged protected-store lifecycle smoke passed."
