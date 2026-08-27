[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$script:utf8NoBom = New-Object System.Text.UTF8Encoding($false)
$script:repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot "..\..\..\.."))
$script:validatorPath = Join-Path $script:repositoryRoot "eng\Test-WindowsReleaseCandidateReadiness.ps1"
$script:runId = [Guid]::NewGuid().ToString("N")
$script:fixtureRoot = Join-Path `
    ([System.IO.Path]::GetTempPath()) `
    "IptvSuite-M16-ReleaseCandidate-$($script:runId)"
$script:inputRoot = Join-Path $script:fixtureRoot ".artifacts\m16-release-candidate\inputs"
$script:evidenceRoot = Join-Path $script:fixtureRoot ".artifacts\m16-release-candidate\self-test"
$script:originalCommit = $null
$script:productionPackageSha256 = ("a" * 64)
$script:nativePackageSha256 = ("b" * 64)
$script:m16FinalArtifactAcceptanceRelativePath =
    "eng/windows-m16-final-artifact-acceptance.json"
$script:m16FinalArtifactAcceptanceSha256 =
    "d0da8a15ff410886c7f9450a8a0ec4c1fe0e463a951b665c2797d178da4db91a"
$script:m16SyntheticJourneyAcceptanceRelativePath =
    "eng/windows-m16-synthetic-journey-acceptance.json"
$script:m16SyntheticJourneyAcceptanceSha256 =
    "8cdfaed7356984d724105b2c04a9f0a66852c9f8f3480cbbf194e7d16f204092"
$script:m16SecurityArchitectureAcceptanceRelativePath =
    "eng/windows-m16-security-architecture-acceptance.json"
$script:m16SecurityArchitectureAcceptanceSha256 =
    "ec86fa5b92afbe4b3c30c4b19e07c39954358d6fb1f2948e373f2cdf66550007"
$script:m16SyntheticJourneyProducerStaticPaths = @(
    ".github/workflows/windows-quality.yml",
    "eng/Invoke-WindowsQualityGate.ps1",
    "global.json",
    "NuGet.config",
    "Directory.Build.props",
    "Directory.Packages.props",
    "Directory.Solution.props",
    "apps/windows/IptvSuite.Windows.sln")
$script:m16SyntheticJourneyProducerSourceRoots = @(
    "apps/windows/src/IptvSuite.Domain",
    "apps/windows/src/IptvSuite.Application",
    "apps/windows/src/IptvSuite.Infrastructure",
    "apps/windows/tests/IptvSuite.Testing",
    "apps/windows/tests/IptvSuite.IntegrationTests")
$script:m16ProducerContractPaths = @(
    ".github/workflows/windows-quality.yml",
    ".config/dotnet-tools.json",
    "eng/Invoke-WindowsFinalArtifactCanaryScan.ps1",
    "eng/Invoke-WindowsPackageSmoke.ps1",
    "eng/WindowsM16FinalArtifactEvidence.ps1",
    "eng/WindowsBoundedProcess.ps1",
    "eng/WindowsPackageInstallRootAudit.ps1",
    "eng/WindowsWack.ps1",
    "eng/Invoke-WindowsPackageSbom.ps1",
    "eng/WindowsPackageSbom.ps1",
    "eng/windows-package-sbom-tool.json",
    "apps/windows/tests/IptvSuite.Testing/ArtifactCanaryScanner.cs",
    "apps/windows/tests/IptvSuite.Testing/FakePlayer.cs",
    "apps/windows/tests/IptvSuite.Testing/InMemorySecretStore.cs",
    "apps/windows/tests/IptvSuite.Testing/IptvSuite.Testing.csproj",
    "apps/windows/tests/IptvSuite.Testing/LocalHttpFixtureServer.cs",
    "apps/windows/tests/IptvSuite.Testing/M14CatalogCorpusGenerator.cs",
    "apps/windows/tests/IptvSuite.Testing/NativePlaybackEvidenceValidator.cs",
    "apps/windows/tests/IptvSuite.Testing/packages.lock.json",
    "apps/windows/tests/IptvSuite.Testing/Program.cs",
    "apps/windows/tests/IptvSuite.Testing/ScriptedTransport.cs",
    "apps/windows/tests/IptvSuite.Testing/SyntheticFixtureGenerator.cs",
    "apps/windows/tests/IptvSuite.Testing/TemporaryDirectory.cs",
    "apps/windows/tests/IptvSuite.Testing/TestCanary.cs",
    "apps/windows/tests/IptvSuite.Testing/TestTime.cs",
    "apps/windows/tests/IptvSuite.Testing/TimeoutGuard.cs",
    "apps/windows/tests/IptvSuite.CatalogUiAcceptanceHarness/IptvSuite.CatalogUiAcceptanceHarness.csproj",
    "apps/windows/tests/IptvSuite.CatalogUiAcceptanceHarness/packages.lock.json",
    "apps/windows/tests/IptvSuite.CatalogUiAcceptanceHarness/Program.cs",
    "apps/windows/tests/IptvSuite.PlaybackUiAcceptanceHarness/IptvSuite.PlaybackUiAcceptanceHarness.csproj",
    "apps/windows/tests/IptvSuite.PlaybackUiAcceptanceHarness/packages.lock.json",
    "apps/windows/tests/IptvSuite.PlaybackUiAcceptanceHarness/Program.cs",
    "apps/windows/tests/fixtures/playback/tier-a/direct-h264-aac.ts",
    "apps/windows/tests/fixtures/playback/tier-a/fixture-manifest.json",
    "apps/windows/tests/fixtures/playback/tier-a/hls.m3u8",
    "apps/windows/tests/fixtures/playback/tier-a/hls-000.ts",
    "apps/windows/tests/fixtures/playback/tier-a/hls-001.ts",
    "apps/windows/tests/fixtures/playback/tier-a/hls-002.ts",
    "apps/windows/tests/fixtures/playback/tier-a/hls-003.ts")
$script:m15Blockers = @(
    "CodecIpLegalReviewPending",
    "LicenseFilePending",
    "NoticeFilePending",
    "PartnerCenterPrivateFlightPending",
    "PrivacyPolicyPending",
    "ProductionIdentityMigrationPending",
    "ProductionLifecycleMatrixPending",
    "ReleaseSigningPending",
    "ReviewerServiceAndRehearsalPending",
    "SbomPending",
    "StoreListingPending",
    "SupportUrlPending",
    "WackPending")
$script:m16Blockers = @(
    "M16FeatureFreezeDecisionPending",
    "M16PhysicalDeviceAccessibilityMatrixPending",
    "M16ReleaseOperationsPlanPending",
    "M16TwentyFourHourSoakPending")

function Assert-TestCondition {
    param(
        [Parameter(Mandatory = $true)]
        [bool]$Condition,

        [Parameter(Mandatory = $true)]
        [string]$Message
    )

    if (-not $Condition) {
        throw "M16 release-candidate self-test failed: $Message"
    }
}

function Write-TestText {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path,

        [Parameter(Mandatory = $true)]
        [string]$Value
    )

    $parent = [System.IO.Path]::GetDirectoryName($Path)
    if (-not [string]::IsNullOrWhiteSpace($parent)) {
        [System.IO.Directory]::CreateDirectory($parent) | Out-Null
    }
    [System.IO.File]::WriteAllText($Path, $Value, $script:utf8NoBom)
}

function Write-TestJson {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path,

        [Parameter(Mandatory = $true)]
        $Value
    )

    Write-TestText -Path $Path -Value ($Value | ConvertTo-Json -Depth 20)
}

function Copy-TestFixtureFile {
    param(
        [Parameter(Mandatory = $true)]
        [string]$RelativePath
    )

    $normalizedPath = $RelativePath.Replace('/', '\')
    $sourcePath = Join-Path $script:repositoryRoot $normalizedPath
    $destinationPath = Join-Path $script:fixtureRoot $normalizedPath
    Assert-TestCondition (Test-Path -LiteralPath $sourcePath -PathType Leaf) `
        "fixture source is missing: $RelativePath"
    $parent = [System.IO.Path]::GetDirectoryName($destinationPath)
    [System.IO.Directory]::CreateDirectory($parent) | Out-Null
    [System.IO.File]::WriteAllBytes(
        $destinationPath,
        [System.IO.File]::ReadAllBytes($sourcePath))
}

function Get-M16SecurityArchitectureProducerPaths {
    $excludedPaths = [string[]]@(
        "eng/Test-WindowsReleaseCandidateReadiness.ps1",
        "apps/windows/tests/IptvSuite.ArchitectureTests/Test-WindowsReleaseCandidateReadiness.ps1",
        "eng/Test-WindowsReleaseReadiness.ps1",
        "apps/windows/tests/IptvSuite.ArchitectureTests/Test-WindowsReleaseReadiness.ps1",
        "eng/windows-package-sbom-acceptance.json",
        "eng/windows-package-vulnerability-acceptance.json",
        "eng/windows-m16-final-artifact-acceptance.json",
        "eng/windows-m16-synthetic-journey-acceptance.json",
        "eng/windows-m16-security-architecture-acceptance.json")
    $staticPaths = [string[]]@(
        ".config/dotnet-tools.json",
        "global.json",
        "NuGet.config",
        "Directory.Build.props",
        "Directory.Packages.props",
        "Directory.Solution.props",
        "apps/windows/IptvSuite.Windows.sln")
    $trackedPaths = @(& git -C $script:repositoryRoot ls-files --)
    Assert-TestCondition ($LASTEXITCODE -eq 0) `
        "security-architecture fixture inventory could not be read."
    $paths = [string[]]@($trackedPaths | Where-Object {
            ($_ -cmatch '\A\.github/workflows/[^/]+\.yml\z' -or
             $_ -cmatch '\Aeng/[^/]+\z' -or
             $_ -cmatch '\Aapps/windows/(?:src|tests|testdata)/' -or
             $staticPaths -ccontains $_) -and
            $excludedPaths -cnotcontains $_
        })
    [System.Array]::Sort($paths, [System.StringComparer]::Ordinal)
    Assert-TestCondition ($paths.Count -eq 329) `
        "security-architecture fixture inventory count changed."
    return $paths
}

function Get-TestFileSha256 {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path
    )

    $stream = [System.IO.File]::Open(
        $Path,
        [System.IO.FileMode]::Open,
        [System.IO.FileAccess]::Read,
        [System.IO.FileShare]::Read)
    $sha256 = [System.Security.Cryptography.SHA256]::Create()
    try {
        return [System.BitConverter]::ToString(
            $sha256.ComputeHash($stream)).Replace("-", "").ToLowerInvariant()
    }
    finally {
        $sha256.Dispose()
        $stream.Dispose()
    }
}

function Invoke-TestGit {
    param(
        [Parameter(Mandatory = $true)]
        [string[]]$Arguments
    )

    $previousErrorActionPreference = $ErrorActionPreference
    try {
        $ErrorActionPreference = "Continue"
        $output = (& git -C $script:fixtureRoot @Arguments 2>&1 | Out-String).Trim()
        $exitCode = $LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $previousErrorActionPreference
    }
    Assert-TestCondition ($exitCode -eq 0) `
        "git command failed: git $($Arguments -join ' '); $output"
    return $output
}

function Commit-TestRepositoryState {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Message
    )

    Invoke-TestGit -Arguments @("add", "--all") | Out-Null
    Invoke-TestGit -Arguments @("commit", "--quiet", "-m", $Message) | Out-Null
    $script:originalCommit = Invoke-TestGit -Arguments @("rev-parse", "--verify", "HEAD")
    Assert-TestCondition ($script:originalCommit -cmatch '^[0-9a-f]{40}$') `
        "fixture HEAD is not an exact 40-character SHA-1."
}

function Get-TruePropertyMap {
    param(
        [Parameter(Mandatory = $true)]
        [string[]]$Names
    )

    $map = [ordered]@{}
    foreach ($name in $Names) {
        $map[$name] = $true
    }
    return $map
}

function Merge-TestMap {
    param(
        [Parameter(Mandatory = $true)]
        [System.Collections.IDictionary]$Head,

        [Parameter(Mandatory = $true)]
        [System.Collections.IDictionary]$Tail
    )

    $result = [ordered]@{}
    foreach ($entry in $Head.GetEnumerator()) {
        $result[$entry.Key] = $entry.Value
    }
    foreach ($entry in $Tail.GetEnumerator()) {
        $result[$entry.Key] = $entry.Value
    }
    return $result
}

function New-M15StubText {
    return @'
[CmdletBinding()]
param(
    [switch]$AllowBlockedInventory,
    [string]$RepositoryRoot,
    [string]$EvidencePath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
$utf8NoBom = New-Object System.Text.UTF8Encoding($false)
$commit = (& git -C $RepositoryRoot rev-parse --verify HEAD 2>$null | Out-String).Trim().ToLowerInvariant()
if ($LASTEXITCODE -ne 0 -or $commit -notmatch '^[0-9a-f]{40}$') {
    throw "M15TechnicalInvariant:RepositoryBindingInvalid"
}
$sbomCurrentAtEvaluation =
    $env:M16_SELF_TEST_M15_MODE -ceq "CurrentSbom"
$cveFinalReleaseFreshAtEvaluation =
    $env:M16_SELF_TEST_M15_MODE -cne "ExpiredCve"
$blockers = @(
    "CodecIpLegalReviewPending",
    "LicenseFilePending",
    "NoticeFilePending",
    "PartnerCenterPrivateFlightPending",
    "PrivacyPolicyPending",
    "ProductionIdentityMigrationPending",
    "ProductionLifecycleMatrixPending",
    "ReleaseSigningPending",
    "ReviewerServiceAndRehearsalPending",
    "StoreListingPending",
    "SupportUrlPending",
    "WackPending")
if (-not $sbomCurrentAtEvaluation) {
    $blockers += "SbomPending"
}
if (-not $cveFinalReleaseFreshAtEvaluation) {
    $blockers += "CveReviewPending"
}
[System.Array]::Sort($blockers, [System.StringComparer]::Ordinal)
if ($env:M16_SELF_TEST_M15_MODE -ceq "ExtraBlocker") {
    $blockers += "UnexpectedPending"
}
elseif ($env:M16_SELF_TEST_M15_MODE -ceq "MissingBlocker") {
    $blockers = @($blockers | Where-Object { $_ -cne "WackPending" })
}
$summary = [ordered]@{
    schemaVersion = 7
    result = "blocked"
    technicalBaselinePassed = $sbomCurrentAtEvaluation
    releaseReady = $false
    commitSha = $commit
    packageSbomAcceptance = [ordered]@{
        result = if ($sbomCurrentAtEvaluation) { "accepted-current" } else { "stale-reopen" }
        currentAtEvaluation = $sbomCurrentAtEvaluation
        effectiveClosedBlocker = if ($sbomCurrentAtEvaluation) { "SbomPending" } else { "None" }
        packageProducingSnapshotFileCount = 115
        packageProducingSnapshotSha256 = "5568fb8fc87f614392762501cb2a4b3be1a13487bb8cfab037ccaec579756810"
        currentPackageProducingSnapshotFileCount = if ($sbomCurrentAtEvaluation) { 115 } else { 116 }
        currentPackageProducingSnapshotSha256 = if ($sbomCurrentAtEvaluation) {
            "5568fb8fc87f614392762501cb2a4b3be1a13487bb8cfab037ccaec579756810"
        }
        else {
            ("c" * 64)
        }
        currentProductionInputSetCanonicalSha256 = "293481fe2194c6f1fde3f667cf45872f4790e0b5955e17ac88c2d16a885b81df"
    }
    packageVulnerabilityAcceptance = [ordered]@{
        finalReleaseFreshAtEvaluation = $cveFinalReleaseFreshAtEvaluation
        effectiveClosedBlocker = if ($cveFinalReleaseFreshAtEvaluation) {
            "CveReviewPending"
        }
        else {
            "None"
        }
    }
    blockers = @($blockers)
}
[System.IO.Directory]::CreateDirectory([System.IO.Path]::GetDirectoryName($EvidencePath)) | Out-Null
[System.IO.File]::WriteAllText(
    $EvidencePath,
    ($summary | ConvertTo-Json -Depth 10),
    $utf8NoBom)

if ($env:M16_SELF_TEST_MUTATE_INPUT -ceq "1") {
    $qualityPath = Join-Path $RepositoryRoot ".artifacts\m16-release-candidate\inputs\quality-summary.json"
    [System.IO.File]::AppendAllText($qualityPath, " ", $utf8NoBom)
}
if ($env:M16_SELF_TEST_ADVANCE_HEAD -ceq "1") {
    & git -C $RepositoryRoot commit --allow-empty --quiet -m "self-test head advance" 2>$null
    if ($LASTEXITCODE -ne 0) {
        throw "M15TechnicalInvariant:FixtureHeadAdvanceFailed"
    }
}

if (-not $AllowBlockedInventory) {
    throw "M15ReleaseReadinessBlocked: releaseReady=false; evidence was published."
}
$summary
'@
}

function Initialize-TestRepository {
    [System.IO.Directory]::CreateDirectory($script:fixtureRoot) | Out-Null
    [System.IO.Directory]::CreateDirectory((Join-Path $script:fixtureRoot "apps\windows\src")) | Out-Null
    [System.IO.Directory]::CreateDirectory((Join-Path $script:fixtureRoot "eng")) | Out-Null

    Write-TestText `
        -Path (Join-Path $script:fixtureRoot ".gitignore") `
        -Value ".artifacts/`n"
    Write-TestJson `
        -Path (Join-Path $script:fixtureRoot "global.json") `
        -Value ([ordered]@{
            sdk = [ordered]@{
                version = "10.0.302"
                rollForward = "disable"
                allowPrerelease = $false
            }
        })
    Write-TestText `
        -Path (Join-Path $script:fixtureRoot "fixture.marker") `
        -Value "bounded M16 fixture"
    Write-TestText `
        -Path (Join-Path $script:fixtureRoot "eng\Test-WindowsReleaseReadiness.ps1") `
        -Value (New-M15StubText)
    Write-TestText `
        -Path (Join-Path $script:fixtureRoot "eng\Invoke-WindowsNativePlaybackSmoke.ps1") `
        -Value "# Synthetic controller marker used only by the bounded M16 self-test.`n"
    $fixturePathSet = @{}
    $fixturePathSet[$script:m16FinalArtifactAcceptanceRelativePath] = $true
    foreach ($relativePath in $script:m16ProducerContractPaths) {
        $fixturePathSet[$relativePath] = $true
    }
    $fixturePathSet[$script:m16SyntheticJourneyAcceptanceRelativePath] = $true
    $fixturePathSet[$script:m16SecurityArchitectureAcceptanceRelativePath] = $true
    foreach ($relativePath in $script:m16SyntheticJourneyProducerStaticPaths) {
        $fixturePathSet[$relativePath] = $true
    }
    foreach ($relativeRoot in $script:m16SyntheticJourneyProducerSourceRoots) {
        $sourceRoot = Join-Path $script:repositoryRoot $relativeRoot.Replace('/', '\')
        foreach ($sourceFile in @(Get-ChildItem -LiteralPath $sourceRoot -Recurse -File | Where-Object {
                    $_.FullName -notmatch '\\(bin|obj)\\'
                })) {
            $relativePath = $sourceFile.FullName.Substring(
                $script:repositoryRoot.TrimEnd('\').Length + 1).Replace('\', '/')
            $fixturePathSet[$relativePath] = $true
        }
    }
    foreach ($relativePath in @(Get-M16SecurityArchitectureProducerPaths)) {
        $fixturePathSet[$relativePath] = $true
    }
    $fixturePaths = [string[]]@($fixturePathSet.Keys)
    [System.Array]::Sort($fixturePaths, [System.StringComparer]::Ordinal)
    foreach ($relativePath in $fixturePaths) {
        Copy-TestFixtureFile -RelativePath $relativePath
    }

    & git init --quiet $script:fixtureRoot 2>&1 | Out-Null
    Assert-TestCondition ($LASTEXITCODE -eq 0) "temporary git repository initialization failed."
    Invoke-TestGit -Arguments @("config", "user.name", "IPTV Suite M16 Self Test") | Out-Null
    Invoke-TestGit -Arguments @("config", "user.email", "m16-self-test@example.invalid") | Out-Null
    Invoke-TestGit -Arguments @("config", "core.autocrlf", "false") | Out-Null
    Commit-TestRepositoryState -Message "M16 self-test fixture"
}

function Write-ValidInputs {
    if (Test-Path -LiteralPath $script:inputRoot) {
        Remove-Item -LiteralPath $script:inputRoot -Recurse -Force
    }
    [System.IO.Directory]::CreateDirectory($script:inputRoot) | Out-Null

    $quality = [ordered]@{
        schemaVersion = 1
        commitSha = $script:originalCommit
        milestone = "M4-foundation"
        sdkVersion = "10.0.302"
        configuration = "Debug+Release"
        platform = "x64"
        cleanRunCount = 2
        qualityGateSentinel = "armed-failed-and-disarmed-passed"
        scannerCliSelfTest = "contaminated-exit-2-and-clean-exit-0"
        artifactCanaryScan = "artifact-files-only-passed"
        testCountPerRun = 1
        testResults = @("FixtureContract|Passed")
        fixture = [ordered]@{
            provenance = "synthetic"
            recordsSha256 = ("1" * 64)
            manifestSha256 = ("2" * 64)
        }
    }
    Write-TestJson -Path (Join-Path $script:inputRoot "quality-summary.json") -Value $quality

    $packageSmoke = Merge-TestMap `
        -Head ([ordered]@{
            CommitSha = $script:originalCommit
            CompletedAt = "2026-08-26T12:00:00Z"
            Configuration = "Release"
            DotNetSdk = "10.0.302"
            Architecture = "x64"
            Capabilities = @("runFullTrust")
            SignatureStatus = "Valid"
            RuntimeDependencySignatureStatus = "Valid"
            PackageSha256 = $script:productionPackageSha256
            PackageSbomApplicationPackageSha256 = $script:productionPackageSha256
            PackageSbomSchemaVersion = 1
            PackageSbomFormat = "SPDX-2.2"
            PackageSbomSha256 = ("3" * 64)
            PackageSbomProductionInputSetSha256 = ("4" * 64)
            PackageSbomRuntimePackageSha256 = ("5" * 64)
            PackageInstallRootPreResetBaselineManifestSha256 = ("6" * 64)
            PackageInstallRootPreResetFinalManifestSha256 = ("6" * 64)
            PackageInstallRootBaselineManifestSha256 = ("7" * 64)
            PackageInstallRootFinalManifestSha256 = ("7" * 64)
            PackageInstallRootAuditSegmentCount = 2
            PackageInstallRootPreResetMutationEventCount = 0
            PackageInstallRootMutationEventCount = 0
            PackageInstallRootAuditSchemaVersion = 1
            PackageInstallRootAuditScope = "ExactRegisteredProductPackageInstallLocation"
            PackageInstallRootPreResetWatcherOverflow = $false
            PackageInstallRootWatcherOverflow = $false
            PlaybackRapidSwitchCount = 25
            PlaybackRapidSwitchP95Milliseconds = 250.0
            PlaybackRapidSwitchMaximumMilliseconds = 500.0
            CatalogUiThreadResponsivenessProxyTimeoutCount = 0
            CatalogUiThreadResponsivenessProxyOverBudgetCount = 0
            CatalogInputResponseP95Milliseconds = 5.0
            CatalogDwmFrameP95Milliseconds = 16.0
            CatalogDwmFrameMaximumMilliseconds = 32.0
            CatalogDwmDroppedFramePercent = 0.0
            CleanInstallOnboardingRequestCount = 1
            CatalogRealizedContainerCount = 8
            CatalogPlayerOffSteadyWorkingSetBudgetBytes = 1048576
            CatalogPlayerOffSteadyWorkingSetMaximumBytes = 524288
            PlaybackPrivateBytesDelta = 0
            PlaybackWorkingSetBytesDelta = 0
            PlaybackHandleCountDelta = 0
            PlaybackThreadCountDelta = 0
            PlaybackReconnectCancelBudgetMilliseconds = 1000.0
            PlaybackReconnectCancelElapsedMilliseconds = 10.0
            PlaybackReconnectNoLaterOpenRequestCountAtReady = 10
            PlaybackReconnectNoLaterOpenRequestCountAfterObservation = 10
            NormalStreamCapacityRejectCount = 0
            NormalStreamUnexpectedFailureCount = 0
            FaultStreamCapacityRejectCount = 0
            FaultStreamUnexpectedFailureCount = 0
            FaultStreamHolding = $true
        }) `
        -Tail (Get-TruePropertyMap @(
            "PayloadLeakGate",
            "PackageSbomOfficialValidationPassed",
            "PackageSbomStrictValidationPassed",
            "PackageInstallRootResetBoundaryInventoryEquivalent",
            "PackageInstallRootPreResetInventoryEquivalent",
            "PackageInstallRootPreResetAuditPassed",
            "PackageInstallRootPrePostInventoryEquivalent",
            "PackageInstallRootAuditPassed",
            "ProtectedStoreDirectoryInitialized",
            "CatalogUiaContractVerified",
            "CatalogKeyboardFocusOrderVerified",
            "Catalog50kSeedVerified",
            "CleanInstallOnboardingVerified",
            "CleanInstallOnboardingAuthorizationVerified",
            "CleanInstallOnboardingSourceVerified",
            "CleanInstallOnboardingChannelsVerified",
            "CleanInstallOnboardingResetVerified",
            "CatalogRealizedContainerBoundVerified",
            "CatalogUiThreadResponsivenessProxyVerified",
            "CatalogPlayerOffStateVerified",
            "CatalogPlayerOffSteadyWorkingSetVerified",
            "CatalogPlayerOffSteadyWorkingSetProcessAliveVerified",
            "PlaybackUiAcceptanceVerified",
            "PlaybackVolumeControlVerified",
            "PlaybackMuteControlVerified",
            "PlaybackAspectControlVerified",
            "PlaybackFullscreenEnterVerified",
            "PlaybackFullscreenExitVerified",
            "PlaybackFullscreenFocusRestored",
            "PlaybackRapidSwitchVerified",
            "PlaybackSurfaceBoundsVerified",
            "PlaybackWindowResizeVerified",
            "PlaybackWindowMinimizeVerified",
            "PlaybackWindowRestoreVerified",
            "PlaybackWindowStatePreserved",
            "PlaybackResourceWarmupVerified",
            "PlaybackResourceSnapshotVerified",
            "PlaybackResourceBudgetVerified",
            "PlaybackActiveCloseVerified",
            "PlaybackReconnectRecoveryVerified",
            "PlaybackReconnectCancelVerified",
            "PlaybackReconnectNoLaterOpenVerified",
            "SourceDeletionCancelNoMutationVerified",
            "SourceDeletionDialogCloseNoMutationVerified",
            "SourceDeletionPendingFailureVerified",
            "SourceDeletionPendingRestartAdmissionBlockedVerified",
            "SourceDeletionPendingCatalogPreserved",
            "SourceDeletionPendingConfigurationRecordPreserved",
            "SourceDeletionPendingTombstoneBindingVerified",
            "SourceDeletionPendingSiblingCatalogRetained",
            "SourceDeletionFaultReleased",
            "SourceDeletionManualRetryVerified",
            "SourceDeletionActivePlaybackDrainVerified",
            "SourceDeletionRestartNonAdmissionVerified",
            "SourceDeletionTargetCatalogDeleted",
            "SourceDeletionProtectedRecordsDeleted",
            "SourceDeletionTombstoneBindingCompleted",
            "SourceDeletionSiblingCatalogRetained",
            "NormalClose",
            "PackageRemoved"))
    Write-TestJson `
        -Path (Join-Path $script:inputRoot "package-smoke-success.json") `
        -Value $packageSmoke

    $packageLifecycle = Merge-TestMap `
        -Head ([ordered]@{
            SchemaVersion = 3
            CommitSha = $script:originalCommit
            CompletedAt = "2026-08-26T12:01:00Z"
            Configuration = "Release"
            DotNetSdk = "10.0.302"
            Architecture = "x64"
            Capabilities = @("runFullTrust")
            BaselinePackageSha256 = ("8" * 64)
            UpdatedPackageSha256 = ("9" * 64)
            BaselineSignatureStatus = "Valid"
            UpdatedSignatureStatus = "Valid"
            DataProtectionScope = "CurrentUser"
            ProtectedStoreVersion = "v2"
        }) `
        -Tail (Get-TruePropertyMap @(
            "SameSigner",
            "SamePackageFamily",
            "PackageFullNameChanged",
            "UpdateInstalled",
            "ProtectedRecordReadAfterPackageUpdate",
            "PostUpdateOwnedSurfaceCanaryScanPassed",
            "PackageReset",
            "PackageIdentityPreservedAfterReset",
            "ResetOwnedStateRemoved",
            "FreshCreateAfterReset",
            "ResetRecordIdentityChanged",
            "PackageUninstalledWithOwnedState",
            "UninstallAppDataRemoved",
            "PackageReinstalled",
            "PackageIdentityPreservedAfterReinstall",
            "FreshCreateAfterReinstall",
            "ReinstallRecordIdentityChanged",
            "CreatePersistedAcrossProcessRestart",
            "DuplicateCreateSuppressed",
            "InitialReadVerified",
            "WrongOwnerReadRejected",
            "WrongOwnerDeleteIdempotent",
            "CorrectRecordSurvivedWrongOwnerDelete",
            "UpdateCommitted",
            "UpdatedReadVerified",
            "DeleteCommitted",
            "PostDeleteUnavailable",
            "InitialOwnedSurfaceCanaryScanPassed",
            "FinalOwnedSurfaceCanaryScanPassed",
            "RecordCleanupPassed",
            "TicketCleanupPassed",
            "PackageRemoved",
            "AppDataRemoved",
            "CertificateRemoved",
            "PackageOutputRemoved"))
    Write-TestJson `
        -Path (Join-Path $script:inputRoot "package-lifecycle-success.json") `
        -Value $packageLifecycle

    $dpapi = Merge-TestMap `
        -Head ([ordered]@{
            SchemaVersion = 1
            CommitSha = $script:originalCommit
            Milestone = "M4"
            EvidenceKind = "dpapi-current-user-boundary"
            Configuration = "Release"
            Platform = "x64"
            DataProtectionScope = "CurrentUser"
            DotNetSdk = "10.0.302"
            ControllerScriptSha256 = ("a" * 64)
            HarnessAssemblySha256 = ("b" * 64)
        }) `
        -Tail (Get-TruePropertyMap @(
            "ExactSdkVerified",
            "CleanHeadBound",
            "DistinctWindowsAccountVerified",
            "StandardUsersMembershipVerified",
            "SecondaryTokenNonAdministrator",
            "NumericSidAclApplied",
            "LogonWithProfileUsed",
            "NetCredentialsOnlyForbidden",
            "CreateNoWindowUsed",
            "ProbeProcessOwnerVerified",
            "ProbeProcessStartVerified",
            "ProfileLoadedForProbe",
            "RawInputDigestMatched",
            "RecordInputDigestMatched",
            "SecondaryRawRoundTripPassed",
            "CreatorRawRejectedCryptographically",
            "SecondaryAdapterRoundTripPassed",
            "SecondaryStoreClean",
            "CreatorRecordUnavailable",
            "CreatorRecordLeaseAbsent",
            "CreatorRecordImmutable",
            "OwnedDataCanaryScanPassed",
            "PrimaryVerificationPassed",
            "ProbeExitedSuccessfully",
            "ProcessCleanupPassed",
            "StandardUsersMembershipRemoved",
            "LocalAccountRemoved",
            "ProfileRemoved",
            "RunWorkspaceRemoved",
            "ToolWorkspaceRemoved",
            "RepositoryCleanAfterRun",
            "EvidenceCanaryScanPassed"))
    Write-TestJson `
        -Path (Join-Path $script:inputRoot "dpapi-user-boundary-success.json") `
        -Value $dpapi

    $native = Merge-TestMap `
        -Head ([ordered]@{
            SchemaVersion = 10
            CommitSha = $script:originalCommit
            Stage = "M10NativeTierAPlayback"
            Result = "Passed"
            RunId = ("1" * 32)
            CompletedAtUtc = "2026-08-26T12:02:00Z"
            Configuration = "Release"
            Platform = "x64"
            DotNetSdk = "10.0.302"
            ProbeEnvelopeSchemaVersion = 8
            SwitchCount = 100
            SoakMinutes = 0
            ResourceSampleCount = 0
            WarmupPrivateBytes = 0
            MemoryNetGrowthBytes = 0
            MemoryNetGrowthPercent = 0.0
            MemoryMonotonicIncrease = $false
            WarmupHandleCount = 0
            HandleNetGrowth = 0
            SurfaceTransitionCount = 6
            CleanHeadBound = $true
            FixtureCorpusVerified = $true
            ProbeRunIdBound = $true
            PackageSignatureStatus = "Valid"
            PackageSha256 = $script:nativePackageSha256
            ControllerScriptSha256 = Get-TestFileSha256 -Path (Join-Path `
                $script:fixtureRoot `
                "eng\Invoke-WindowsNativePlaybackSmoke.ps1")
            HarnessAssemblySha256 = ("d" * 64)
            FixtureManifestSha256 = ("e" * 64)
            RuntimeDependencyPackageSha256 = ("f" * 64)
            RuntimeDependencyPackageSignatureStatus = "Valid"
            ResolvedWindowsAppRuntimeArchitecture = "x64"
            ResolvedWindowsAppRuntimeIsFramework = $true
            ResolvedWindowsAppRuntimeName = "Microsoft.WindowsAppRuntime.2"
            ResolvedWindowsAppRuntimeVersion = "8000.1.2.3"
            ResolvedWindowsAppRuntimePublisherId = "8wekyb3d8bbwe"
            Transport = "Tls12LoopbackAllowlist"
            Fixtures = @("DirectH264AacMpegTs", "HlsH264AacMpegTs")
            StartupP95Milliseconds = 250.0
            StartupMaximumMilliseconds = 500.0
            HlsStartupP95Milliseconds = 300.0
            DirectStartupP95Milliseconds = 200.0
            SourceDetachP95Milliseconds = 10.0
            SourceDetachMaximumMilliseconds = 20.0
            NetworkInterruptionCount = 1
            NetworkRecoveryCount = 1
            LastInjectedRequestOrdinal = 10
            LastRecoveryRequestOrdinal = 11
            PlaybackRetryCount = 0
            CancellationProbeCount = 1
            CancellationObservedCount = 1
            CancellationSourceDetachCount = 1
            CancellationRecoveryCount = 1
            CancellationRecoverySourceDetachCount = 1
            DetachedSourceCount = 102
            CancellationLatencyMilliseconds = 50.0
            CancellationQuiescenceMilliseconds = 100.0
            CancellationObservationMilliseconds = 1100.0
            CancellationSourceDetachMilliseconds = 10.0
            CancellationRecoveryStartupMilliseconds = 10.0
            CancellationRecoveryAdvanceMilliseconds = 10.0
            CancellationRecoverySourceDetachMilliseconds = 10.0
            RuntimePackageGraphDisposition = "ExactRestored"
            RuntimePackageSharedAdditionCount = 0
            PackageAppDataEmptyRootCleanupUsed = $false
            InitialPrivateBytes = 1000000
            FinalPrivateBytes = 1000000
            InitialHandleCount = 100
            FinalHandleCount = 100
            LoopbackRequestCount = 100
            ForcedProcessTerminationUsed = $false
        }) `
        -Tail (Get-TruePropertyMap @(
            "CancellationSourceNullAfterObservation",
            "CancellationRecoveryUsedFreshSource",
            "CancellationNoAutomaticRestart",
            "H264DecoderRegistered",
            "AacDecoderRegistered",
            "NormalCloseVerified",
            "ProcessCleanupPassed",
            "TlsServerDisposed",
            "PackageRemoved",
            "PackageAppDataRemoved",
            "RuntimePackageBaselinePreserved",
            "EphemeralCertificatesRemoved",
            "ExportedCertificateFilesRemoved",
            "PackageOutputRemoved",
            "EnvironmentRestored",
            "RepositoryCleanAfterRun"))
    Write-TestJson `
        -Path (Join-Path $script:inputRoot "native-tier-a-success.json") `
        -Value $native

    $benchmark = [ordered]@{
        schemaVersion = 3
        commitSha = $script:originalCommit
        milestone = "M14"
        evidenceKind = "catalog-performance-benchmark"
        result = "passed"
        configuration = "Release"
        platform = "x64"
        sdkVersion = "10.0.302"
        iterations = 20
        authoritativeWarmIterations = 20
        minimumAuthoritativeWarmIterations = 20
        coldObservationsPerStage = 1
        runnerProfile = [ordered]@{
            verification = "Declared"
            value = "self-test-x64"
        }
        processor = [ordered]@{
            verification = "Observed"
            value = "Synthetic Processor"
        }
        logicalProcessorCount = 8
        measurementIntegrityVerified = $true
        authoritativeWarmSampleCountVerified = $true
        conditionDeclarationsComplete = $true
        referenceModeRequested = $true
        referenceEligible = $true
        referenceEligibilityRequirements = [ordered]@{
            exactConditionDeclarations = $true
            declaredRunnerProfile = $true
            measurementIntegrity = $true
            passingBenchmarkResult = $true
        }
        conditions = [ordered]@{
            cache = [ordered]@{ verification = "Declared"; value = "Warm" }
            power = [ordered]@{ verification = "Declared"; value = "AcStable" }
            thermal = [ordered]@{ verification = "Declared"; value = "Nominal" }
            background = [ordered]@{ verification = "Declared"; value = "Controlled" }
        }
        plaintextLocatorCanaryScan = "passed"
        budgetEvaluation = [ordered]@{
            normalizeProtectPersistIndexPassed = $true
            peakWorkingSetSamplingComplete = $true
            allPassed = $true
            parserP95Milliseconds = 1.0
            normalizeProtectPersistIndexConservativeUpperBoundP95Milliseconds = 2.0
            combinedImportP95Milliseconds = 3.0
            cancellationP95Milliseconds = 1.0
            firstPageP95Milliseconds = 1.0
            categoryPageP95Milliseconds = 1.0
            searchP95Milliseconds = 1.0
            reopenP95Milliseconds = 1.0
        }
        corpusManifest = [ordered]@{
            retained = $false
            sha256 = ("0" * 64)
        }
        query50k = [ordered]@{
            recordCount = 50000
            catalogSchemaVersion = 5
            warmupIterations = 5
            iterations = 100
            warmupSampleRole = "non-authoritative"
            authoritativeSampleRole = "authoritative-warm"
            percentileEstimator = "nearest-rank-ceiling"
            operationOrder = @(
                "FirstPage",
                "CategoryPage",
                "Search",
                "ReopenFirstVisible")
            rawSamples = @(1..100 | ForEach-Object {
                    [ordered]@{
                        iteration = $_
                        firstPageMilliseconds = 1.0
                        categoryPageMilliseconds = 1.0
                        searchMilliseconds = 1.0
                        reopenFirstVisibleMilliseconds = 1.0
                    }
                })
        }
        cancellation = [ordered]@{
            recordCount = 50000
            iterations = 20
            expectedErrorCode = "OperationCancelled"
            measurementBoundary = "CancellationRequestToLoaderCompletion"
        }
        entryLimitProbe = [ordered]@{
            recordCount = 100000
            expectedOutcome = "EntryLimitFailClosed"
            persistedRowsAfterFailure = 0
        }
    }
    $benchmarkPath = Join-Path $script:inputRoot "catalog-benchmark-summary.json"
    Write-TestJson -Path $benchmarkPath -Value $benchmark
    $benchmarkFile = Get-Item -LiteralPath $benchmarkPath
    $regression = [ordered]@{
        schemaVersion = 1
        milestone = "M14"
        evidenceKind = "catalog-performance-regression"
        result = "passed"
        allPassed = $true
        absoluteBudgetResult = "passed"
        candidate = [ordered]@{
            commitSha = $script:originalCommit
            sha256 = Get-TestFileSha256 -Path $benchmarkPath
            byteLength = $benchmarkFile.Length
        }
        binding = [ordered]@{
            physicalMachineIdentityVerified = $false
            baselineCommitAncestorOrSelf = $true
            baselineContentStable = $true
            exactEnvironmentMatch = $true
            exactWorkloadMatch = $true
            exactBudgetContractMatch = $true
            exactSchemaMatch = $true
            runnerProfile = [ordered]@{
                verification = "Declared"
                value = "self-test-x64"
            }
        }
        threshold = [ordered]@{
            metric = "p95"
            maximumIncreasePercent = 10.0
        }
        metrics = @(
            "parser-p95",
            "normalize-protect-persist-index-upper-bound-p95",
            "combined-import-p95",
            "cancellation-p95",
            "first-page-p95",
            "category-page-p95",
            "search-p95",
            "reopen-p95" | ForEach-Object {
                [ordered]@{ name = $_; unit = "milliseconds"; passed = $true }
            })
    }
    Write-TestJson `
        -Path (Join-Path $script:inputRoot "catalog-regression-summary.json") `
        -Value $regression
}

function Invoke-AllowedCandidate {
    param(
        [Parameter(Mandatory = $true)]
        [string]$EvidencePath
    )

    & $script:validatorPath `
        -AllowBlockedCandidate `
        -RepositoryRoot $script:fixtureRoot `
        -EvidencePath $EvidencePath | Out-Null
}

function Assert-CandidateFailure {
    param(
        [Parameter(Mandatory = $true)]
        [string]$ExpectedMessage,

        [string]$EvidencePath = (Join-Path $script:evidenceRoot "failure.json"),

        [switch]$AllowBlockedCandidate
    )

    $actualMessage = $null
    try {
        if ($AllowBlockedCandidate) {
            Invoke-AllowedCandidate -EvidencePath $EvidencePath
        }
        else {
            & $script:validatorPath `
                -RepositoryRoot $script:fixtureRoot `
                -EvidencePath $EvidencePath | Out-Null
        }
    }
    catch {
        $actualMessage = $_.Exception.Message
    }
    Assert-TestCondition ($actualMessage -ceq $ExpectedMessage) `
        "expected '$ExpectedMessage', received '$actualMessage'."
}

function Assert-ExactStringSet {
    param(
        [AllowEmptyCollection()]
        [string[]]$Actual,

        [AllowEmptyCollection()]
        [string[]]$Expected,

        [Parameter(Mandatory = $true)]
        [string]$Message
    )

    $actualValues = [string[]]@($Actual)
    $expectedValues = [string[]]@($Expected)
    [System.Array]::Sort($actualValues, [System.StringComparer]::Ordinal)
    [System.Array]::Sort($expectedValues, [System.StringComparer]::Ordinal)
    Assert-TestCondition ($actualValues.Count -eq $expectedValues.Count) $Message
    for ($index = 0; $index -lt $expectedValues.Count; $index++) {
        Assert-TestCondition ($actualValues[$index] -ceq $expectedValues[$index]) $Message
    }
}

function Read-AndAssertBlockedEvidence {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path,

        [bool]$ExpectedSbomCurrentAtEvaluation = $false,

        [bool]$ExpectedCveFinalReleaseFreshAtEvaluation = $true,

        [bool]$ExpectedFinalArtifactCurrent = $false,

        [bool]$ExpectedSecurityArchitectureCurrent = $false,

        [bool]$ExpectedSyntheticJourneyCurrent = $true
    )

    Assert-TestCondition (Test-Path -LiteralPath $Path -PathType Leaf) `
        "blocked evidence was not published."
    $file = Get-Item -LiteralPath $Path -Force
    Assert-TestCondition ($file.Length -gt 0 -and $file.Length -le 256KB) `
        "blocked evidence is empty or exceeds 256 KiB."
    Assert-TestCondition `
        (($file.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -eq 0) `
        "blocked evidence is a reparse point."
    $raw = [System.IO.File]::ReadAllText($Path)
    $evidence = $raw | ConvertFrom-Json
    Assert-ExactStringSet `
        -Actual @($evidence.PSObject.Properties.Name) `
        -Expected @(
            "schemaVersion",
            "milestone",
            "evidenceKind",
            "result",
            "aggregationIntegrityPassed",
            "m1ToM15AutomatedGateSetPassed",
            "m16TechnicalGateSetPassed",
            "candidateReady",
            "commitSha",
            "evaluatedAtUtc",
            "releasePackageSha256",
            "policy",
            "inputs",
            "finalArtifactCanaryAcceptance",
            "finalSecurityArchitectureAcceptance",
            "syntheticEndToEndJourneyAcceptance",
            "gates",
            "blockerCounts",
            "blockers",
            "nonClaims") `
        -Message "evidence root schema changed."
    Assert-TestCondition ($evidence.schemaVersion -eq 1) "schemaVersion must be 1."
    Assert-TestCondition ($evidence.milestone -ceq "M16") "milestone must be M16."
    Assert-TestCondition `
        ($evidence.evidenceKind -ceq "WindowsMvpReleaseCandidateGate") `
        "evidence kind changed."
    Assert-TestCondition ($evidence.result -ceq "blocked") "result must remain blocked."
    Assert-TestCondition `
        ($evidence.aggregationIntegrityPassed -is [bool] -and
         $evidence.aggregationIntegrityPassed) `
        "aggregation integrity must be exact Boolean true."
    Assert-TestCondition `
        ($evidence.m1ToM15AutomatedGateSetPassed -is [bool] -and
         $evidence.m1ToM15AutomatedGateSetPassed -eq
            ($ExpectedSbomCurrentAtEvaluation -and
             $ExpectedCveFinalReleaseFreshAtEvaluation)) `
        "the M1-M15 aggregate did not match SBOM/CVE currentness."
    Assert-TestCondition `
        ($evidence.m16TechnicalGateSetPassed -is [bool] -and
         -not $evidence.m16TechnicalGateSetPassed) `
        "M16 technical gate set must be exact Boolean false."
    Assert-TestCondition `
        ($evidence.candidateReady -is [bool] -and -not $evidence.candidateReady) `
        "schema-v1 candidateReady must be exact Boolean false."
    Assert-TestCondition ($evidence.commitSha -ceq $script:originalCommit) `
        "evidence commit binding changed."
    Assert-TestCondition `
        ($evidence.releasePackageSha256 -ceq $script:productionPackageSha256) `
        "release package binding changed."
    $finalArtifactAcceptance = $evidence.finalArtifactCanaryAcceptance
    Assert-TestCondition ($finalArtifactAcceptance -is [pscustomobject]) `
        "final-artifact acceptance summary is not an object."
    Assert-ExactStringSet `
        -Actual @($finalArtifactAcceptance.PSObject.Properties.Name) `
        -Expected @(
            "result",
            "current",
            "ledgerSha256",
            "decision",
            "scope",
            "runCompletedAtUtc",
            "runId",
            "runNumber",
            "runAttempt",
            "runHeadSha",
            "producerJobId",
            "artifactId",
            "artifactName",
            "artifactDigestSha256",
            "memberLength",
            "memberSha256",
            "packageSha256",
            "producerContractSourceCount",
            "producerContractSourceSetSha256",
            "packageProducingSnapshotFileCount",
            "packageProducingSnapshotSha256",
            "closedBlocker",
            "effectiveClosedBlocker") `
        -Message "final-artifact acceptance summary schema changed."
    $expectedFinalArtifactResult = if ($ExpectedFinalArtifactCurrent) {
        "accepted-current"
    }
    else {
        "stale-reopen"
    }
    $expectedFinalArtifactEffectiveBlocker = if ($ExpectedFinalArtifactCurrent) {
        "M16FinalArtifactCanaryScanPending"
    }
    else {
        "None"
    }
    Assert-TestCondition `
        ($finalArtifactAcceptance.result -ceq $expectedFinalArtifactResult -and
         $finalArtifactAcceptance.current -is [bool] -and
         $finalArtifactAcceptance.current -eq $ExpectedFinalArtifactCurrent -and
         $finalArtifactAcceptance.ledgerSha256 -ceq
            $script:m16FinalArtifactAcceptanceSha256 -and
         $finalArtifactAcceptance.decision -ceq
            "AcceptHostedM16FinalArtifactCanaryScan" -and
         $finalArtifactAcceptance.scope -ceq "M16FinalArtifactCanaryScanOnly" -and
         $finalArtifactAcceptance.runCompletedAtUtc -ceq "2026-08-27T10:13:40Z" -and
         $finalArtifactAcceptance.runId -eq 33060587316 -and
         $finalArtifactAcceptance.runNumber -eq 299 -and
         $finalArtifactAcceptance.runAttempt -eq 1 -and
         $finalArtifactAcceptance.runHeadSha -ceq
            "be52ab67687cc44a9ca820ec1907c1b92bf1d24a" -and
         $finalArtifactAcceptance.producerJobId -eq 98480943428 -and
         $finalArtifactAcceptance.artifactId -eq 9642123749 -and
         $finalArtifactAcceptance.artifactName -ceq
            "windows-m16-final-artifact-evidence" -and
         $finalArtifactAcceptance.artifactDigestSha256 -ceq
            "b40f8742681546c74f1c9d4b6d345ecc699addd2b1bca0830f647b380076f32f" -and
         $finalArtifactAcceptance.memberLength -eq 3281 -and
         $finalArtifactAcceptance.memberSha256 -ceq
            "fe27278d17391e2946642758c185f4f389e59d81f35e74482452ccdf1867fb11" -and
         $finalArtifactAcceptance.packageSha256 -ceq
            "0ceb0e95967c1ede0db1e034d958f0f7a4e7e9da00f65d66010b95f58da86333" -and
         $finalArtifactAcceptance.producerContractSourceCount -eq 39 -and
         $finalArtifactAcceptance.producerContractSourceSetSha256 -ceq
            "18b20bf208943c6ac9cc1ac4075f3df3f7668765bdf3833b03de664134bae6ae" -and
         $finalArtifactAcceptance.packageProducingSnapshotFileCount -eq 115 -and
         $finalArtifactAcceptance.packageProducingSnapshotSha256 -ceq
            "5568fb8fc87f614392762501cb2a4b3be1a13487bb8cfab037ccaec579756810" -and
         $finalArtifactAcceptance.closedBlocker -ceq
            "M16FinalArtifactCanaryScanPending" -and
         $finalArtifactAcceptance.effectiveClosedBlocker -ceq
            $expectedFinalArtifactEffectiveBlocker) `
        "final-artifact acceptance binding changed."
    $securityArchitectureAcceptance = $evidence.finalSecurityArchitectureAcceptance
    Assert-TestCondition ($securityArchitectureAcceptance -is [pscustomobject]) `
        "security-architecture acceptance summary is not an object."
    Assert-ExactStringSet `
        -Actual @($securityArchitectureAcceptance.PSObject.Properties.Name) `
        -Expected @(
            "result",
            "current",
            "ledgerSha256",
            "decision",
            "scope",
            "runCompletedAtUtc",
            "runId",
            "runNumber",
            "runAttempt",
            "runHeadSha",
            "producerJobId",
            "requiredGateJobId",
            "artifactId",
            "artifactName",
            "artifactDigestSha256",
            "qualitySummaryMemberLength",
            "qualitySummaryMemberSha256",
            "cleanRunCount",
            "testCountPerRun",
            "fullTestResultSetSha256",
            "architectureTestCount",
            "architectureTestResultSetSha256",
            "producerContractSourceCount",
            "producerContractCanonicalByteLength",
            "producerContractSourceSetSha256",
            "closedBlocker",
            "effectiveClosedBlocker") `
        -Message "security-architecture acceptance summary schema changed."
    $expectedSecurityResult = if ($ExpectedSecurityArchitectureCurrent) {
        "accepted-current"
    }
    else {
        "stale-reopen"
    }
    $expectedSecurityEffectiveBlocker = if ($ExpectedSecurityArchitectureCurrent) {
        "M16FinalSecurityArchitectureScanPending"
    }
    else {
        "None"
    }
    Assert-TestCondition `
        ($securityArchitectureAcceptance.result -ceq $expectedSecurityResult -and
         $securityArchitectureAcceptance.current -is [bool] -and
         $securityArchitectureAcceptance.current -eq
            $ExpectedSecurityArchitectureCurrent -and
         $securityArchitectureAcceptance.ledgerSha256 -ceq
            $script:m16SecurityArchitectureAcceptanceSha256 -and
         $securityArchitectureAcceptance.decision -ceq
            "AcceptHostedM16FinalSecurityArchitectureScan" -and
         $securityArchitectureAcceptance.scope -ceq
            "M16FinalSecurityArchitectureScanOnly" -and
         $securityArchitectureAcceptance.runCompletedAtUtc -ceq
            "2026-08-27T19:16:38Z" -and
         $securityArchitectureAcceptance.runId -eq 33104019955 -and
         $securityArchitectureAcceptance.runNumber -eq 313 -and
         $securityArchitectureAcceptance.runAttempt -eq 2 -and
         $securityArchitectureAcceptance.runHeadSha -ceq
            "524d148bea0ca0dc359eaefb777091eefe1efe1f" -and
         $securityArchitectureAcceptance.producerJobId -eq 98635759984 -and
         $securityArchitectureAcceptance.requiredGateJobId -eq 98641720078 -and
         $securityArchitectureAcceptance.artifactId -eq 9660849258 -and
         $securityArchitectureAcceptance.artifactName -ceq
            "windows-quality-evidence" -and
         $securityArchitectureAcceptance.artifactDigestSha256 -ceq
            "e2eb353682b0c88a2a03f2a82306f0afb22b6dad9b4c4141c2692ce87f6c568d" -and
         $securityArchitectureAcceptance.qualitySummaryMemberLength -eq 47236 -and
         $securityArchitectureAcceptance.qualitySummaryMemberSha256 -ceq
            "4e1789703b7b57d65c32e546f6277eacf0b903473a3e99d33a1671d1b9789565" -and
         $securityArchitectureAcceptance.cleanRunCount -eq 2 -and
         $securityArchitectureAcceptance.testCountPerRun -eq 631 -and
         $securityArchitectureAcceptance.fullTestResultSetSha256 -ceq
            "66dab64fa75e52da441dd863490f8d0c5c32f54c5963a12b860ff8af19663ff2" -and
         $securityArchitectureAcceptance.architectureTestCount -eq 77 -and
         $securityArchitectureAcceptance.architectureTestResultSetSha256 -ceq
            "9d2e961e127593313f48365a9c7f700a6bf1e745c832c8947a94c90a0c4da778" -and
         $securityArchitectureAcceptance.producerContractSourceCount -eq 329 -and
         $securityArchitectureAcceptance.producerContractCanonicalByteLength -eq
            7179352 -and
         $securityArchitectureAcceptance.producerContractSourceSetSha256 -ceq
            "6c2fcae05643225a934734ceff680b906b813629770c32f10e045a5c294e16e1" -and
         $securityArchitectureAcceptance.closedBlocker -ceq
            "M16FinalSecurityArchitectureScanPending" -and
         $securityArchitectureAcceptance.effectiveClosedBlocker -ceq
            $expectedSecurityEffectiveBlocker) `
        "security-architecture acceptance binding changed."

    $syntheticJourneyAcceptance = $evidence.syntheticEndToEndJourneyAcceptance
    Assert-TestCondition ($syntheticJourneyAcceptance -is [pscustomobject]) `
        "synthetic-journey acceptance summary is not an object."
    Assert-ExactStringSet `
        -Actual @($syntheticJourneyAcceptance.PSObject.Properties.Name) `
        -Expected @(
            "result",
            "current",
            "ledgerSha256",
            "decision",
            "scope",
            "runCompletedAtUtc",
            "runId",
            "runNumber",
            "runAttempt",
            "runHeadSha",
            "producerJobId",
            "requiredGateJobId",
            "artifactId",
            "artifactName",
            "artifactDigestSha256",
            "qualitySummaryMemberLength",
            "qualitySummaryMemberSha256",
            "cleanRunCount",
            "testCountPerRun",
            "journeyTestResult",
            "producerContractSourceCount",
            "producerContractSourceSetSha256",
            "closedBlocker",
            "effectiveClosedBlocker") `
        -Message "synthetic-journey acceptance summary schema changed."
    $expectedJourneyResult = if ($ExpectedSyntheticJourneyCurrent) {
        "accepted-current"
    }
    else {
        "stale-reopen"
    }
    $expectedJourneyEffectiveBlocker = if ($ExpectedSyntheticJourneyCurrent) {
        "M16SyntheticEndToEndJourneyPending"
    }
    else {
        "None"
    }
    Assert-TestCondition `
        ($syntheticJourneyAcceptance.result -ceq $expectedJourneyResult -and
         $syntheticJourneyAcceptance.current -is [bool] -and
         $syntheticJourneyAcceptance.current -eq $ExpectedSyntheticJourneyCurrent -and
         $syntheticJourneyAcceptance.ledgerSha256 -ceq
            $script:m16SyntheticJourneyAcceptanceSha256 -and
         $syntheticJourneyAcceptance.decision -ceq
            "AcceptHostedM16SyntheticEndToEndJourney" -and
         $syntheticJourneyAcceptance.scope -ceq
            "M16SyntheticEndToEndJourneyOnly" -and
         $syntheticJourneyAcceptance.runCompletedAtUtc -ceq
            "2026-08-27T17:58:52Z" -and
         $syntheticJourneyAcceptance.runId -eq 33098975507 -and
         $syntheticJourneyAcceptance.runNumber -eq 312 -and
         $syntheticJourneyAcceptance.runAttempt -eq 1 -and
         $syntheticJourneyAcceptance.runHeadSha -ceq
            "28c6da84574fe96b5aad3778d776aa0c958c5df4" -and
         $syntheticJourneyAcceptance.producerJobId -eq 98611290588 -and
         $syntheticJourneyAcceptance.requiredGateJobId -eq 98618891179 -and
         $syntheticJourneyAcceptance.artifactId -eq 9658189086 -and
         $syntheticJourneyAcceptance.artifactName -ceq
            "windows-quality-evidence" -and
         $syntheticJourneyAcceptance.artifactDigestSha256 -ceq
            "a155cfc748b56d865e84ef776b6d02ab9bfeec5c212d8f8b57db200aeec1af28" -and
         $syntheticJourneyAcceptance.qualitySummaryMemberLength -eq 47236 -and
         $syntheticJourneyAcceptance.qualitySummaryMemberSha256 -ceq
            "b02a5e255c916edf8b4700cf4c3a3e6e9829f544407d33b8d8a7693c9a1b6056" -and
         $syntheticJourneyAcceptance.cleanRunCount -eq 2 -and
         $syntheticJourneyAcceptance.testCountPerRun -eq 631 -and
         $syntheticJourneyAcceptance.journeyTestResult -ceq
            "AuthorizedRemotePlaylistCompletesExactSyntheticReleaseCandidateJourney|Passed" -and
         $syntheticJourneyAcceptance.producerContractSourceCount -eq 132 -and
         $syntheticJourneyAcceptance.producerContractSourceSetSha256 -ceq
            "d0fbbaec1898edceb7915e9cb789bad9603cc6ebdffb6767f2f181c246b80f81" -and
         $syntheticJourneyAcceptance.closedBlocker -ceq
            "M16SyntheticEndToEndJourneyPending" -and
         $syntheticJourneyAcceptance.effectiveClosedBlocker -ceq
            $expectedJourneyEffectiveBlocker) `
        "synthetic-journey acceptance binding changed."
    Assert-TestCondition (@($evidence.inputs).Count -eq 8) `
        "the exact eight bounded evidence inputs were not summarized."
    Assert-ExactStringSet `
        -Actual @($evidence.inputs | ForEach-Object { [string]$_.name }) `
        -Expected @(
            "quality-summary.json",
            "package-smoke-success.json",
            "package-lifecycle-success.json",
            "dpapi-user-boundary-success.json",
            "native-tier-a-success.json",
            "catalog-benchmark-summary.json",
            "catalog-regression-summary.json",
            "m15-readiness.json") `
        -Message "the exact bounded input-name set changed."
    $expectedM15Blockers = @($script:m15Blockers)
    if ($ExpectedSbomCurrentAtEvaluation) {
        $expectedM15Blockers = @(
            $expectedM15Blockers | Where-Object { $_ -cne "SbomPending" })
    }
    if (-not $ExpectedCveFinalReleaseFreshAtEvaluation) {
        $expectedM15Blockers += "CveReviewPending"
    }
    $expectedM16Blockers = @($script:m16Blockers)
    if (-not $ExpectedFinalArtifactCurrent) {
        $expectedM16Blockers += "M16FinalArtifactCanaryScanPending"
    }
    if (-not $ExpectedSecurityArchitectureCurrent) {
        $expectedM16Blockers += "M16FinalSecurityArchitectureScanPending"
    }
    if (-not $ExpectedSyntheticJourneyCurrent) {
        $expectedM16Blockers += "M16SyntheticEndToEndJourneyPending"
    }
    Assert-TestCondition `
        ($evidence.blockerCounts.total -eq
            ($expectedM15Blockers.Count + $expectedM16Blockers.Count)) `
        "the total blocker baseline changed."
    Assert-TestCondition ($evidence.blockerCounts.m15 -eq $expectedM15Blockers.Count) `
        "the M15 blocker baseline changed."
    Assert-TestCondition `
        ($evidence.blockerCounts.m16 -eq $expectedM16Blockers.Count) `
        "the remaining M16 blocker count changed."
    Assert-ExactStringSet `
        -Actual @($evidence.blockers | ForEach-Object { [string]$_.code }) `
        -Expected @($expectedM15Blockers + $expectedM16Blockers) `
        -Message "the exact blocker codes changed."

    $m1ToM15Gate = @($evidence.gates | Where-Object {
            $_.code -ceq "M1ToM15AutomatedGateSet"
        })
    Assert-TestCondition `
        ($m1ToM15Gate.Count -eq 1 -and
         $m1ToM15Gate[0].result -ceq
            $(if ($ExpectedSbomCurrentAtEvaluation -and
                  $ExpectedCveFinalReleaseFreshAtEvaluation) {
                    "passed"
                }
                else {
                    "blocked"
                })) `
        "the M1-M15 gate summary did not match aggregate currentness."
    Assert-ExactStringSet `
        -Actual @($evidence.gates | ForEach-Object { [string]$_.code }) `
        -Expected @(
            "M1ToM15AutomatedGateSet",
            "M16FinalArtifactCanaryScan",
            "M16FinalSecurityArchitectureScan",
            "M16SyntheticEndToEndJourney",
            "M16TechnicalGateSet",
            "ReleaseCandidate") `
        -Message "the exact release-candidate gate set changed."
    $finalArtifactGate = @($evidence.gates | Where-Object {
            $_.code -ceq "M16FinalArtifactCanaryScan"
        })
    Assert-TestCondition `
        ($finalArtifactGate.Count -eq 1 -and
         $finalArtifactGate[0].result -ceq
            $(if ($ExpectedFinalArtifactCurrent) { "passed" } else { "blocked" })) `
        "the final-artifact gate did not match ledger currentness."
    $securityArchitectureGate = @($evidence.gates | Where-Object {
            $_.code -ceq "M16FinalSecurityArchitectureScan"
        })
    Assert-TestCondition `
        ($securityArchitectureGate.Count -eq 1 -and
         $securityArchitectureGate[0].result -ceq
            $(if ($ExpectedSecurityArchitectureCurrent) { "passed" } else { "blocked" })) `
        "the security-architecture gate did not match ledger currentness."
    $syntheticJourneyGate = @($evidence.gates | Where-Object {
            $_.code -ceq "M16SyntheticEndToEndJourney"
        })
    Assert-TestCondition `
        ($syntheticJourneyGate.Count -eq 1 -and
         $syntheticJourneyGate[0].result -ceq
            $(if ($ExpectedSyntheticJourneyCurrent) { "passed" } else { "blocked" })) `
        "the synthetic-journey gate did not match ledger currentness."
    $blockerCodes = [string[]]@(
        $evidence.blockers | ForEach-Object { [string]$_.code })
    $sortedBlockerCodes = [string[]]@($blockerCodes)
    [System.Array]::Sort($sortedBlockerCodes, [System.StringComparer]::Ordinal)
    for ($index = 0; $index -lt $blockerCodes.Count; $index++) {
        Assert-TestCondition ($blockerCodes[$index] -ceq $sortedBlockerCodes[$index]) `
            "blockers are not emitted in unique ordinal order."
    }
    foreach ($blocker in @($evidence.blockers)) {
        Assert-TestCondition `
            (@($blocker.PSObject.Properties.Name).Count -eq 4) `
            "a blocker is not a bounded four-field record."
    }
    Assert-TestCondition `
        ($evidence.policy.schemaVersionOneCandidateReadyAllowed -is [bool] -and
         -not $evidence.policy.schemaVersionOneCandidateReadyAllowed) `
        "schema-v1 unexpectedly permits a ready result."
    Assert-TestCondition `
        ($raw.IndexOf($script:fixtureRoot, [System.StringComparison]::OrdinalIgnoreCase) -lt 0) `
        "absolute fixture path leaked into evidence."
    return $evidence
}

function Read-TestJson {
    param([Parameter(Mandatory = $true)][string]$Path)
    return [System.IO.File]::ReadAllText($Path) | ConvertFrom-Json
}

function Update-TestCatalogRegressionBinding {
    $benchmarkPath = Join-Path $script:inputRoot "catalog-benchmark-summary.json"
    $regressionPath = Join-Path $script:inputRoot "catalog-regression-summary.json"
    $regression = Read-TestJson -Path $regressionPath
    $regression.candidate.sha256 = Get-TestFileSha256 -Path $benchmarkPath
    $regression.candidate.byteLength = (Get-Item -LiteralPath $benchmarkPath).Length
    Write-TestJson -Path $regressionPath -Value $regression
}

Assert-TestCondition (Test-Path -LiteralPath $script:validatorPath -PathType Leaf) `
    "M16 release-candidate validator is missing."
$validatorText = [System.IO.File]::ReadAllText($script:validatorPath)
Assert-TestCondition `
    ($validatorText.Contains('schemaVersionOneCandidateReadyAllowed = $false') -and
     -not $validatorText.Contains('candidateReady = $true') -and
     $validatorText.Contains('SoakMinutes") -Expected 0') -and
     $validatorText.Contains('PackageSbomApplicationPackageSha256') -and
     $validatorText.Contains('$M15Validation.PackageSbomCurrentAtEvaluation -and') -and
     $validatorText.Contains('result = if ($m15Validation.AutomatedGateSetPassed)')) `
    "schema-v1 blocked-only, short native-profile, or package binding contract changed."

try {
    Initialize-TestRepository
    Write-ValidInputs

    $defaultPath = Join-Path $script:evidenceRoot "default.json"
    Assert-CandidateFailure `
        -EvidencePath $defaultPath `
        -ExpectedMessage "M16ReleaseCandidateBlocked: candidateReady=false; evidence was published."
    Read-AndAssertBlockedEvidence -Path $defaultPath | Out-Null

    $acceptancePath = Join-Path `
        $script:fixtureRoot `
        ($script:m16FinalArtifactAcceptanceRelativePath.Replace('/', '\'))
    [byte[]]$acceptanceBytes = [System.IO.File]::ReadAllBytes($acceptancePath)
    Remove-Item -LiteralPath $acceptancePath -Force
    Commit-TestRepositoryState -Message "Remove M16 final-artifact acceptance ledger"
    Write-ValidInputs
    Assert-CandidateFailure `
        -ExpectedMessage "M16TechnicalInvariant:FinalArtifactAcceptanceInvalid" `
        -AllowBlockedCandidate
    [System.IO.File]::WriteAllBytes($acceptancePath, $acceptanceBytes)
    Commit-TestRepositoryState -Message "Restore M16 final-artifact acceptance ledger"

    [System.IO.File]::AppendAllText($acceptancePath, " ", $script:utf8NoBom)
    Commit-TestRepositoryState -Message "Tamper M16 final-artifact acceptance ledger"
    Write-ValidInputs
    Assert-CandidateFailure `
        -ExpectedMessage "M16TechnicalInvariant:FinalArtifactAcceptanceInvalid" `
        -AllowBlockedCandidate
    [System.IO.File]::WriteAllBytes($acceptancePath, $acceptanceBytes)
    Commit-TestRepositoryState -Message "Restore exact M16 final-artifact acceptance ledger"

    $producerSourcePath = Join-Path `
        $script:fixtureRoot `
        "eng\WindowsM16FinalArtifactEvidence.ps1"
    $producerSourceSha256 = Get-TestFileSha256 -Path $producerSourcePath
    [byte[]]$producerSourceBytes = [System.IO.File]::ReadAllBytes($producerSourcePath)
    [System.IO.File]::AppendAllText(
        $producerSourcePath,
        "`n# self-test producer drift",
        $script:utf8NoBom)
    Commit-TestRepositoryState -Message "Drift M16 final-artifact producer source"
    $env:M16_SELF_TEST_M15_MODE = "CurrentSbom"
    try {
        Write-ValidInputs
        $staleFinalArtifactPath = Join-Path `
            $script:evidenceRoot `
            "stale-final-artifact.json"
        Invoke-AllowedCandidate -EvidencePath $staleFinalArtifactPath
        $staleFinalArtifactEvidence = Read-AndAssertBlockedEvidence `
            -Path $staleFinalArtifactPath `
            -ExpectedSbomCurrentAtEvaluation $true `
            -ExpectedFinalArtifactCurrent $false `
            -ExpectedSecurityArchitectureCurrent $false
        Assert-TestCondition `
            (-not $staleFinalArtifactEvidence.finalArtifactCanaryAcceptance.current) `
            "final-artifact source drift did not reopen its blocker."
    }
    finally {
        Remove-Item Env:\M16_SELF_TEST_M15_MODE -ErrorAction SilentlyContinue
    }
    [System.IO.File]::WriteAllBytes($producerSourcePath, $producerSourceBytes)
    Commit-TestRepositoryState -Message "Restore M16 final-artifact producer source"

    $binaryProducerSourcePath = Join-Path `
        $script:fixtureRoot `
        "apps\windows\tests\fixtures\playback\tier-a\direct-h264-aac.ts"
    $binaryProducerSourceSha256 =
        Get-TestFileSha256 -Path $binaryProducerSourcePath
    [byte[]]$binaryProducerSourceBytes =
        [System.IO.File]::ReadAllBytes($binaryProducerSourcePath)
    [byte[]]$binaryDriftBytes = New-Object byte[] $binaryProducerSourceBytes.Length
    [System.Array]::Copy(
        $binaryProducerSourceBytes,
        $binaryDriftBytes,
        $binaryProducerSourceBytes.Length)
    $binaryDriftBytes[0] = [byte]($binaryDriftBytes[0] -bxor 1)
    [System.IO.File]::WriteAllBytes($binaryProducerSourcePath, $binaryDriftBytes)
    Commit-TestRepositoryState -Message "Drift M16 final-artifact binary fixture"
    $env:M16_SELF_TEST_M15_MODE = "CurrentSbom"
    try {
        Write-ValidInputs
        $staleBinaryArtifactPath = Join-Path `
            $script:evidenceRoot `
            "stale-final-artifact-binary.json"
        Invoke-AllowedCandidate -EvidencePath $staleBinaryArtifactPath
        $staleBinaryArtifactEvidence = Read-AndAssertBlockedEvidence `
            -Path $staleBinaryArtifactPath `
            -ExpectedSbomCurrentAtEvaluation $true `
            -ExpectedFinalArtifactCurrent $false `
            -ExpectedSecurityArchitectureCurrent $false
        Assert-TestCondition `
            (-not $staleBinaryArtifactEvidence.finalArtifactCanaryAcceptance.current) `
            "final-artifact binary drift did not reopen its blocker."
    }
    finally {
        Remove-Item Env:\M16_SELF_TEST_M15_MODE -ErrorAction SilentlyContinue
    }
    [System.IO.File]::WriteAllBytes(
        $binaryProducerSourcePath,
        $binaryProducerSourceBytes)
    Commit-TestRepositoryState -Message "Restore M16 final-artifact binary fixture"

    $securityAcceptancePath = Join-Path `
        $script:fixtureRoot `
        ($script:m16SecurityArchitectureAcceptanceRelativePath.Replace('/', '\'))
    [byte[]]$securityAcceptanceBytes =
        [System.IO.File]::ReadAllBytes($securityAcceptancePath)
    Remove-Item -LiteralPath $securityAcceptancePath -Force
    Commit-TestRepositoryState -Message "Remove M16 security-architecture acceptance ledger"
    Write-ValidInputs
    Assert-CandidateFailure `
        -ExpectedMessage "M16TechnicalInvariant:SecurityArchitectureAcceptanceInvalid" `
        -AllowBlockedCandidate
    [System.IO.File]::WriteAllBytes(
        $securityAcceptancePath,
        $securityAcceptanceBytes)
    Commit-TestRepositoryState -Message "Restore M16 security-architecture acceptance ledger"

    [System.IO.File]::AppendAllText(
        $securityAcceptancePath,
        " ",
        $script:utf8NoBom)
    Commit-TestRepositoryState -Message "Tamper M16 security-architecture acceptance ledger"
    Write-ValidInputs
    Assert-CandidateFailure `
        -ExpectedMessage "M16TechnicalInvariant:SecurityArchitectureAcceptanceInvalid" `
        -AllowBlockedCandidate
    [System.IO.File]::WriteAllBytes(
        $securityAcceptancePath,
        $securityAcceptanceBytes)
    Commit-TestRepositoryState -Message "Restore exact M16 security-architecture acceptance ledger"

    $securitySourcePath = Join-Path `
        $script:fixtureRoot `
        ".github\workflows\windows-cve-review.yml"
    $securitySourceSha256 = Get-TestFileSha256 -Path $securitySourcePath
    [byte[]]$securitySourceBytes = [System.IO.File]::ReadAllBytes($securitySourcePath)
    [System.IO.File]::AppendAllText(
        $securitySourcePath,
        "`n# self-test security-architecture source drift",
        $script:utf8NoBom)
    Commit-TestRepositoryState -Message "Drift M16 security-architecture producer source"
    Write-ValidInputs
    $staleSecurityPath = Join-Path `
        $script:evidenceRoot `
        "stale-security-architecture.json"
    Invoke-AllowedCandidate -EvidencePath $staleSecurityPath
    $staleSecurityEvidence = Read-AndAssertBlockedEvidence `
        -Path $staleSecurityPath `
        -ExpectedSecurityArchitectureCurrent $false
    Assert-TestCondition `
        (-not $staleSecurityEvidence.finalSecurityArchitectureAcceptance.current) `
        "security-architecture source drift did not reopen its blocker."
    [System.IO.File]::WriteAllBytes($securitySourcePath, $securitySourceBytes)
    Commit-TestRepositoryState -Message "Restore M16 security-architecture producer source"

    $journeyAcceptancePath = Join-Path `
        $script:fixtureRoot `
        ($script:m16SyntheticJourneyAcceptanceRelativePath.Replace('/', '\'))
    [byte[]]$journeyAcceptanceBytes =
        [System.IO.File]::ReadAllBytes($journeyAcceptancePath)
    Remove-Item -LiteralPath $journeyAcceptancePath -Force
    Commit-TestRepositoryState -Message "Remove M16 synthetic-journey acceptance ledger"
    Write-ValidInputs
    Assert-CandidateFailure `
        -ExpectedMessage "M16TechnicalInvariant:SyntheticJourneyAcceptanceInvalid" `
        -AllowBlockedCandidate
    [System.IO.File]::WriteAllBytes(
        $journeyAcceptancePath,
        $journeyAcceptanceBytes)
    Commit-TestRepositoryState -Message "Restore M16 synthetic-journey acceptance ledger"

    [System.IO.File]::AppendAllText(
        $journeyAcceptancePath,
        " ",
        $script:utf8NoBom)
    Commit-TestRepositoryState -Message "Tamper M16 synthetic-journey acceptance ledger"
    Write-ValidInputs
    Assert-CandidateFailure `
        -ExpectedMessage "M16TechnicalInvariant:SyntheticJourneyAcceptanceInvalid" `
        -AllowBlockedCandidate
    [System.IO.File]::WriteAllBytes(
        $journeyAcceptancePath,
        $journeyAcceptanceBytes)
    Commit-TestRepositoryState -Message "Restore exact M16 synthetic-journey acceptance ledger"

    $journeySourcePath = Join-Path `
        $script:fixtureRoot `
        "apps\windows\tests\IptvSuite.IntegrationTests\M16SyntheticEndToEndJourneyTests.cs"
    $journeySourceSha256 = Get-TestFileSha256 -Path $journeySourcePath
    [byte[]]$journeySourceBytes = [System.IO.File]::ReadAllBytes($journeySourcePath)
    [System.IO.File]::AppendAllText(
        $journeySourcePath,
        "`n// self-test journey producer drift",
        $script:utf8NoBom)
    Commit-TestRepositoryState -Message "Drift M16 synthetic-journey producer source"
    Write-ValidInputs
    $staleJourneyPath = Join-Path `
        $script:evidenceRoot `
        "stale-synthetic-journey.json"
    Invoke-AllowedCandidate -EvidencePath $staleJourneyPath
    $staleJourneyEvidence = Read-AndAssertBlockedEvidence `
        -Path $staleJourneyPath `
        -ExpectedSecurityArchitectureCurrent $false `
        -ExpectedSyntheticJourneyCurrent $false
    Assert-TestCondition `
        (-not $staleJourneyEvidence.syntheticEndToEndJourneyAcceptance.current) `
        "synthetic-journey source drift did not reopen its blocker."
    [System.IO.File]::WriteAllBytes($journeySourcePath, $journeySourceBytes)
    Commit-TestRepositoryState -Message "Restore M16 synthetic-journey producer source"

    Write-ValidInputs
    Remove-Item -LiteralPath (Join-Path $script:inputRoot "quality-summary.json") -Force
    Assert-CandidateFailure `
        -ExpectedMessage "M16TechnicalInvariant:InputDirectoryInvalid" `
        -AllowBlockedCandidate

    Write-ValidInputs
    $qualityPath = Join-Path $script:inputRoot "quality-summary.json"
    $qualityText = [System.IO.File]::ReadAllText($qualityPath)
    Write-TestText `
        -Path $qualityPath `
        -Value ($qualityText.Replace(
            '"schemaVersion":  1,',
            '"schemaVersion":  1, "schemaVersion": 1,'))
    Assert-CandidateFailure `
        -ExpectedMessage "M16TechnicalInvariant:InputDuplicateProperty" `
        -AllowBlockedCandidate

    Write-ValidInputs
    Write-TestText -Path $qualityPath -Value "not-json"
    Assert-CandidateFailure `
        -ExpectedMessage "M16TechnicalInvariant:InputJsonInvalid" `
        -AllowBlockedCandidate

    Write-ValidInputs
    [byte[]]$qualityBytes = [System.IO.File]::ReadAllBytes($qualityPath)
    [byte[]]$bomBytes = @(0xEF, 0xBB, 0xBF) + $qualityBytes
    [System.IO.File]::WriteAllBytes($qualityPath, $bomBytes)
    Assert-CandidateFailure `
        -ExpectedMessage "M16TechnicalInvariant:InputEncodingInvalid" `
        -AllowBlockedCandidate

    Write-ValidInputs
    Write-TestText -Path $qualityPath -Value ("x" * (1MB + 1))
    Assert-CandidateFailure `
        -ExpectedMessage "M16TechnicalInvariant:InputInvalid" `
        -AllowBlockedCandidate

    Write-ValidInputs
    $globalJsonPath = Join-Path $script:fixtureRoot "global.json"
    $globalJsonText = [System.IO.File]::ReadAllText($globalJsonPath)
    [System.IO.File]::AppendAllText($globalJsonPath, " ", $script:utf8NoBom)
    try {
        Assert-CandidateFailure `
            -ExpectedMessage "M16TechnicalInvariant:RepositoryDirty" `
            -AllowBlockedCandidate
    }
    finally {
        Write-TestText -Path $globalJsonPath -Value $globalJsonText
    }

    Write-ValidInputs
    $quality = Read-TestJson -Path $qualityPath
    $quality.commitSha = ("0" * 40)
    Write-TestJson -Path $qualityPath -Value $quality
    Assert-CandidateFailure `
        -ExpectedMessage "M16TechnicalInvariant:InputContractInvalid" `
        -AllowBlockedCandidate

    Write-ValidInputs
    $quality = Read-TestJson -Path $qualityPath
    $quality.schemaVersion = 2
    Write-TestJson -Path $qualityPath -Value $quality
    Assert-CandidateFailure `
        -ExpectedMessage "M16TechnicalInvariant:InputContractInvalid" `
        -AllowBlockedCandidate

    Write-ValidInputs
    $packagePath = Join-Path $script:inputRoot "package-smoke-success.json"
    $package = Read-TestJson -Path $packagePath
    $package.NormalClose = "true"
    Write-TestJson -Path $packagePath -Value $package
    Assert-CandidateFailure `
        -ExpectedMessage "M16TechnicalInvariant:InputContractInvalid" `
        -AllowBlockedCandidate

    Write-ValidInputs
    $package = Read-TestJson -Path $packagePath
    $package.PackageSbomApplicationPackageSha256 = ("c" * 64)
    Write-TestJson -Path $packagePath -Value $package
    Assert-CandidateFailure `
        -ExpectedMessage "M16TechnicalInvariant:InputContractInvalid" `
        -AllowBlockedCandidate

    Write-ValidInputs
    $package = Read-TestJson -Path $packagePath
    $package.PlaybackRapidSwitchP95Milliseconds = 3000.1
    Write-TestJson -Path $packagePath -Value $package
    Assert-CandidateFailure `
        -ExpectedMessage "M16TechnicalInvariant:InputContractInvalid" `
        -AllowBlockedCandidate

    Write-ValidInputs
    $lifecyclePath = Join-Path $script:inputRoot "package-lifecycle-success.json"
    $lifecycle = Read-TestJson -Path $lifecyclePath
    $lifecycle.RecordCleanupPassed = $false
    Write-TestJson -Path $lifecyclePath -Value $lifecycle
    Assert-CandidateFailure `
        -ExpectedMessage "M16TechnicalInvariant:InputContractInvalid" `
        -AllowBlockedCandidate

    Write-ValidInputs
    $dpapiPath = Join-Path $script:inputRoot "dpapi-user-boundary-success.json"
    $dpapi = Read-TestJson -Path $dpapiPath
    $dpapi.RepositoryCleanAfterRun = $false
    Write-TestJson -Path $dpapiPath -Value $dpapi
    Assert-CandidateFailure `
        -ExpectedMessage "M16TechnicalInvariant:InputContractInvalid" `
        -AllowBlockedCandidate

    Write-ValidInputs
    $nativePath = Join-Path $script:inputRoot "native-tier-a-success.json"
    $native = Read-TestJson -Path $nativePath
    $native.SoakMinutes = 480
    Write-TestJson -Path $nativePath -Value $native
    Assert-CandidateFailure `
        -ExpectedMessage "M16TechnicalInvariant:InputContractInvalid" `
        -AllowBlockedCandidate

    Write-ValidInputs
    $native = Read-TestJson -Path $nativePath
    $native.StartupP95Milliseconds = 3000.1
    Write-TestJson -Path $nativePath -Value $native
    Assert-CandidateFailure `
        -ExpectedMessage "M16TechnicalInvariant:InputContractInvalid" `
        -AllowBlockedCandidate

    Write-ValidInputs
    $native = Read-TestJson -Path $nativePath
    $native.NetworkRecoveryCount = 0
    Write-TestJson -Path $nativePath -Value $native
    Assert-CandidateFailure `
        -ExpectedMessage "M16TechnicalInvariant:InputContractInvalid" `
        -AllowBlockedCandidate

    Write-ValidInputs
    $benchmarkPath = Join-Path $script:inputRoot "catalog-benchmark-summary.json"
    $benchmark = Read-TestJson -Path $benchmarkPath
    $benchmark.schemaVersion = 2
    Write-TestJson -Path $benchmarkPath -Value $benchmark
    $regressionPath = Join-Path $script:inputRoot "catalog-regression-summary.json"
    Update-TestCatalogRegressionBinding
    Assert-CandidateFailure `
        -ExpectedMessage "M16TechnicalInvariant:InputContractInvalid" `
        -AllowBlockedCandidate

    Write-ValidInputs
    $benchmark = Read-TestJson -Path $benchmarkPath
    $benchmark.query50k.catalogSchemaVersion = 6
    Write-TestJson -Path $benchmarkPath -Value $benchmark
    Update-TestCatalogRegressionBinding
    Assert-CandidateFailure `
        -ExpectedMessage "M16TechnicalInvariant:InputContractInvalid" `
        -AllowBlockedCandidate

    Write-ValidInputs
    $benchmark = Read-TestJson -Path $benchmarkPath
    $benchmark.query50k.PSObject.Properties.Remove("warmupIterations")
    Write-TestJson -Path $benchmarkPath -Value $benchmark
    Update-TestCatalogRegressionBinding
    Assert-CandidateFailure `
        -ExpectedMessage "M16TechnicalInvariant:InputContractInvalid" `
        -AllowBlockedCandidate

    Write-ValidInputs
    $benchmark = Read-TestJson -Path $benchmarkPath
    $benchmark.query50k.percentileEstimator = "linear-interpolation"
    Write-TestJson -Path $benchmarkPath -Value $benchmark
    Update-TestCatalogRegressionBinding
    Assert-CandidateFailure `
        -ExpectedMessage "M16TechnicalInvariant:InputContractInvalid" `
        -AllowBlockedCandidate

    Write-ValidInputs
    $benchmark = Read-TestJson -Path $benchmarkPath
    $benchmark.query50k.iterations = 99
    Write-TestJson -Path $benchmarkPath -Value $benchmark
    Update-TestCatalogRegressionBinding
    Assert-CandidateFailure `
        -ExpectedMessage "M16TechnicalInvariant:InputContractInvalid" `
        -AllowBlockedCandidate

    Write-ValidInputs
    $benchmark = Read-TestJson -Path $benchmarkPath
    $benchmark.query50k.operationOrder = @(
        "CategoryPage",
        "FirstPage",
        "Search",
        "ReopenFirstVisible")
    Write-TestJson -Path $benchmarkPath -Value $benchmark
    Update-TestCatalogRegressionBinding
    Assert-CandidateFailure `
        -ExpectedMessage "M16TechnicalInvariant:InputContractInvalid" `
        -AllowBlockedCandidate

    Write-ValidInputs
    $benchmark = Read-TestJson -Path $benchmarkPath
    $benchmark.query50k.operationOrder = @("FirstPage", "CategoryPage", "Search")
    Write-TestJson -Path $benchmarkPath -Value $benchmark
    Update-TestCatalogRegressionBinding
    Assert-CandidateFailure `
        -ExpectedMessage "M16TechnicalInvariant:InputContractInvalid" `
        -AllowBlockedCandidate

    Write-ValidInputs
    $benchmark = Read-TestJson -Path $benchmarkPath
    $benchmark.query50k.rawSamples = @($benchmark.query50k.rawSamples | Select-Object -First 99)
    Write-TestJson -Path $benchmarkPath -Value $benchmark
    Update-TestCatalogRegressionBinding
    Assert-CandidateFailure `
        -ExpectedMessage "M16TechnicalInvariant:InputContractInvalid" `
        -AllowBlockedCandidate

    Write-ValidInputs
    $benchmark = Read-TestJson -Path $benchmarkPath
    $benchmark.budgetEvaluation.allPassed = $false
    Write-TestJson -Path $benchmarkPath -Value $benchmark
    $regression = Read-TestJson -Path $regressionPath
    $regression.candidate.sha256 = Get-TestFileSha256 -Path $benchmarkPath
    $regression.candidate.byteLength = (Get-Item -LiteralPath $benchmarkPath).Length
    Write-TestJson -Path $regressionPath -Value $regression
    Assert-CandidateFailure `
        -ExpectedMessage "M16TechnicalInvariant:InputContractInvalid" `
        -AllowBlockedCandidate

    Write-ValidInputs
    $benchmark = Read-TestJson -Path $benchmarkPath
    $benchmark.cancellation.PSObject.Properties.Remove("measurementBoundary")
    Write-TestJson -Path $benchmarkPath -Value $benchmark
    $regression = Read-TestJson -Path $regressionPath
    $regression.candidate.sha256 = Get-TestFileSha256 -Path $benchmarkPath
    $regression.candidate.byteLength = (Get-Item -LiteralPath $benchmarkPath).Length
    Write-TestJson -Path $regressionPath -Value $regression
    Assert-CandidateFailure `
        -ExpectedMessage "M16TechnicalInvariant:InputContractInvalid" `
        -AllowBlockedCandidate

    Write-ValidInputs
    $benchmark = Read-TestJson -Path $benchmarkPath
    $benchmark.cancellation.measurementBoundary = "LoaderStartToLoaderCompletion"
    Write-TestJson -Path $benchmarkPath -Value $benchmark
    $regression = Read-TestJson -Path $regressionPath
    $regression.candidate.sha256 = Get-TestFileSha256 -Path $benchmarkPath
    $regression.candidate.byteLength = (Get-Item -LiteralPath $benchmarkPath).Length
    Write-TestJson -Path $regressionPath -Value $regression
    Assert-CandidateFailure `
        -ExpectedMessage "M16TechnicalInvariant:InputContractInvalid" `
        -AllowBlockedCandidate

    Write-ValidInputs
    $regression = Read-TestJson -Path $regressionPath
    $regression.candidate.sha256 = ("d" * 64)
    Write-TestJson -Path $regressionPath -Value $regression
    Assert-CandidateFailure `
        -ExpectedMessage "M16TechnicalInvariant:InputContractInvalid" `
        -AllowBlockedCandidate

    Write-ValidInputs
    $regression = Read-TestJson -Path $regressionPath
    $regression.candidate.byteLength = [long]$regression.candidate.byteLength + 1
    Write-TestJson -Path $regressionPath -Value $regression
    Assert-CandidateFailure `
        -ExpectedMessage "M16TechnicalInvariant:InputContractInvalid" `
        -AllowBlockedCandidate

    Write-ValidInputs
    $env:M16_SELF_TEST_M15_MODE = "ExtraBlocker"
    try {
        Assert-CandidateFailure `
            -ExpectedMessage "M16TechnicalInvariant:M15BlockerSetInvalid" `
            -AllowBlockedCandidate
    }
    finally {
        Remove-Item Env:\M16_SELF_TEST_M15_MODE -ErrorAction SilentlyContinue
    }

    Write-ValidInputs
    $env:M16_SELF_TEST_M15_MODE = "ExpiredCve"
    try {
        $expiredCvePath = Join-Path $script:evidenceRoot "expired-cve.json"
        Invoke-AllowedCandidate -EvidencePath $expiredCvePath
        $expiredCveEvidence = Read-AndAssertBlockedEvidence `
            -Path $expiredCvePath `
            -ExpectedSbomCurrentAtEvaluation $false `
            -ExpectedCveFinalReleaseFreshAtEvaluation $false
        Assert-TestCondition `
            (@($expiredCveEvidence.blockers | ForEach-Object { [string]$_.code }) `
                -ccontains "CveReviewPending") `
            "expired CVE freshness did not reopen CveReviewPending."
    }
    finally {
        Remove-Item Env:\M16_SELF_TEST_M15_MODE -ErrorAction SilentlyContinue
    }

    Write-ValidInputs
    $env:M16_SELF_TEST_M15_MODE = "CurrentSbom"
    try {
        $currentSbomPath = Join-Path $script:evidenceRoot "current-sbom.json"
        Invoke-AllowedCandidate -EvidencePath $currentSbomPath
        $currentSbomEvidence = Read-AndAssertBlockedEvidence `
            -Path $currentSbomPath `
            -ExpectedSbomCurrentAtEvaluation $true `
            -ExpectedCveFinalReleaseFreshAtEvaluation $true `
            -ExpectedFinalArtifactCurrent $false
        Assert-TestCondition `
            ($currentSbomEvidence.m1ToM15AutomatedGateSetPassed -and
             -not $currentSbomEvidence.finalArtifactCanaryAcceptance.current) `
            "M15 aggregate or stale final-artifact acceptance changed."
    }
    finally {
        Remove-Item Env:\M16_SELF_TEST_M15_MODE -ErrorAction SilentlyContinue
    }

    Write-ValidInputs
    $env:M16_SELF_TEST_MUTATE_INPUT = "1"
    try {
        Assert-CandidateFailure `
            -ExpectedMessage "M16TechnicalInvariant:InputChanged" `
            -AllowBlockedCandidate
    }
    finally {
        Remove-Item Env:\M16_SELF_TEST_MUTATE_INPUT -ErrorAction SilentlyContinue
    }

    Write-ValidInputs
    $env:M16_SELF_TEST_ADVANCE_HEAD = "1"
    try {
        Assert-CandidateFailure `
            -ExpectedMessage "M16TechnicalInvariant:RepositoryChanged" `
            -AllowBlockedCandidate
    }
    finally {
        Remove-Item Env:\M16_SELF_TEST_ADVANCE_HEAD -ErrorAction SilentlyContinue
        Invoke-TestGit -Arguments @("update-ref", "HEAD", $script:originalCommit) | Out-Null
    }

    Write-ValidInputs
    Assert-CandidateFailure `
        -EvidencePath (Join-Path $script:fixtureRoot "outside-artifacts.json") `
        -ExpectedMessage "M16TechnicalInvariant:EvidencePathInvalid" `
        -AllowBlockedCandidate
    Assert-CandidateFailure `
        -EvidencePath (Join-Path $script:evidenceRoot "stream.json:alternate") `
        -ExpectedMessage "M16TechnicalInvariant:EvidencePathInvalid" `
        -AllowBlockedCandidate

    Write-ValidInputs
    $junctionTarget = Join-Path `
        ([System.IO.Path]::GetDirectoryName($script:inputRoot)) `
        "inputs-junction-target"
    Move-Item -LiteralPath $script:inputRoot -Destination $junctionTarget
    $junctionCreated = $false
    try {
        New-Item -ItemType Junction -Path $script:inputRoot -Target $junctionTarget | Out-Null
        $junctionCreated = $true
        Assert-CandidateFailure `
            -ExpectedMessage "M16TechnicalInvariant:DirectoryReparsePoint" `
            -AllowBlockedCandidate
    }
    finally {
        if ($junctionCreated -and (Test-Path -LiteralPath $script:inputRoot)) {
            [System.IO.Directory]::Delete($script:inputRoot)
        }
        if (Test-Path -LiteralPath $junctionTarget) {
            Move-Item -LiteralPath $junctionTarget -Destination $script:inputRoot
        }
    }

    $restoredFileBindings = @(
        [ordered]@{
            Path = $acceptancePath
            Sha256 = $script:m16FinalArtifactAcceptanceSha256
        },
        [ordered]@{
            Path = $producerSourcePath
            Sha256 = $producerSourceSha256
        },
        [ordered]@{
            Path = $binaryProducerSourcePath
            Sha256 = $binaryProducerSourceSha256
        },
        [ordered]@{
            Path = $securityAcceptancePath
            Sha256 = $script:m16SecurityArchitectureAcceptanceSha256
        },
        [ordered]@{
            Path = $securitySourcePath
            Sha256 = $securitySourceSha256
        },
        [ordered]@{
            Path = $journeyAcceptancePath
            Sha256 = $script:m16SyntheticJourneyAcceptanceSha256
        },
        [ordered]@{
            Path = $journeySourcePath
            Sha256 = $journeySourceSha256
        })
    foreach ($binding in $restoredFileBindings) {
        Assert-TestCondition `
            ((Get-TestFileSha256 -Path $binding.Path) -ceq $binding.Sha256) `
            "a mutated fixture file was not restored exactly."
    }
    Assert-TestCondition `
        ([string]::IsNullOrWhiteSpace((Invoke-TestGit -Arguments @(
                    "status", "--porcelain=v1", "--untracked-files=normal")))) `
        "fixture repository was not restored to a clean state."

    Write-Output "M16 Windows release-candidate readiness self-test passed."
}
finally {
    foreach ($name in @(
            "M16_SELF_TEST_M15_MODE",
            "M16_SELF_TEST_MUTATE_INPUT",
            "M16_SELF_TEST_ADVANCE_HEAD")) {
        Remove-Item "Env:\$name" -ErrorAction SilentlyContinue
    }
    if (Test-Path -LiteralPath $script:fixtureRoot) {
        Remove-Item -LiteralPath $script:fixtureRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
}
