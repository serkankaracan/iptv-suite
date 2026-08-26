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
    "StoreListingPending",
    "SupportUrlPending",
    "WackPending")
$script:m16Blockers = @(
    "M16FeatureFreezeDecisionPending",
    "M16FinalArtifactCanaryScanPending",
    "M16FinalSecurityArchitectureScanPending",
    "M16PhysicalDeviceAccessibilityMatrixPending",
    "M16ReleaseOperationsPlanPending",
    "M16SyntheticEndToEndJourneyPending",
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
if ($env:M16_SELF_TEST_M15_MODE -ceq "ExtraBlocker") {
    $blockers += "UnexpectedPending"
}
elseif ($env:M16_SELF_TEST_M15_MODE -ceq "MissingBlocker") {
    $blockers = @($blockers | Where-Object { $_ -cne "WackPending" })
}
$summary = [ordered]@{
    schemaVersion = 6
    result = "blocked"
    technicalBaselinePassed = $true
    releaseReady = $false
    commitSha = $commit
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
        -Path (Join-Path $script:fixtureRoot "apps\windows\src\fixture.marker") `
        -Value "bounded M16 fixture"
    Write-TestText `
        -Path (Join-Path $script:fixtureRoot "eng\Test-WindowsReleaseReadiness.ps1") `
        -Value (New-M15StubText)
    Write-TestText `
        -Path (Join-Path $script:fixtureRoot "eng\Invoke-WindowsNativePlaybackSmoke.ps1") `
        -Value "# Synthetic controller marker used only by the bounded M16 self-test.`n"

    & git init --quiet $script:fixtureRoot 2>&1 | Out-Null
    Assert-TestCondition ($LASTEXITCODE -eq 0) "temporary git repository initialization failed."
    Invoke-TestGit -Arguments @("config", "user.name", "IPTV Suite M16 Self Test") | Out-Null
    Invoke-TestGit -Arguments @("config", "user.email", "m16-self-test@example.invalid") | Out-Null
    Invoke-TestGit -Arguments @("config", "core.autocrlf", "false") | Out-Null
    Invoke-TestGit -Arguments @("add", "--", ".gitignore", "global.json", "apps", "eng") | Out-Null
    Invoke-TestGit -Arguments @("commit", "--quiet", "-m", "M16 self-test fixture") | Out-Null
    $script:originalCommit = Invoke-TestGit -Arguments @("rev-parse", "--verify", "HEAD")
    Assert-TestCondition ($script:originalCommit -cmatch '^[0-9a-f]{40}$') `
        "fixture HEAD is not an exact 40-character SHA-1."
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
        schemaVersion = 1
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
            iterations = 20
        }
        cancellation = [ordered]@{
            recordCount = 50000
            iterations = 20
            expectedErrorCode = "OperationCancelled"
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
        [string]$Path
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
         $evidence.m1ToM15AutomatedGateSetPassed) `
        "M1-M15 automated gate set must be exact Boolean true."
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
    Assert-TestCondition ($evidence.blockerCounts.total -eq 19) `
        "the exact 19-blocker baseline changed."
    Assert-TestCondition ($evidence.blockerCounts.m15 -eq 12) `
        "the exact 12 M15 blockers changed."
    Assert-TestCondition ($evidence.blockerCounts.m16 -eq 7) `
        "the exact seven M16 blockers changed."
    Assert-ExactStringSet `
        -Actual @($evidence.blockers | ForEach-Object { [string]$_.code }) `
        -Expected @($script:m15Blockers + $script:m16Blockers) `
        -Message "the exact 19 blocker codes changed."
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

Assert-TestCondition (Test-Path -LiteralPath $script:validatorPath -PathType Leaf) `
    "M16 release-candidate validator is missing."
$validatorText = [System.IO.File]::ReadAllText($script:validatorPath)
Assert-TestCondition `
    ($validatorText.Contains('schemaVersionOneCandidateReadyAllowed = $false') -and
     -not $validatorText.Contains('candidateReady = $true') -and
     $validatorText.Contains('SoakMinutes") -Expected 0') -and
     $validatorText.Contains('PackageSbomApplicationPackageSha256')) `
    "schema-v1 blocked-only, short native-profile, or package binding contract changed."

try {
    Initialize-TestRepository
    Write-ValidInputs

    $allowedPath = Join-Path $script:evidenceRoot "allowed.json"
    Invoke-AllowedCandidate -EvidencePath $allowedPath
    Read-AndAssertBlockedEvidence -Path $allowedPath | Out-Null

    $defaultPath = Join-Path $script:evidenceRoot "default.json"
    Assert-CandidateFailure `
        -EvidencePath $defaultPath `
        -ExpectedMessage "M16ReleaseCandidateBlocked: candidateReady=false; evidence was published."
    Read-AndAssertBlockedEvidence -Path $defaultPath | Out-Null

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
    $benchmark.budgetEvaluation.allPassed = $false
    Write-TestJson -Path $benchmarkPath -Value $benchmark
    $regressionPath = Join-Path $script:inputRoot "catalog-regression-summary.json"
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

    Write-ValidInputs
    $finalPath = Join-Path $script:evidenceRoot "final-restored.json"
    Invoke-AllowedCandidate -EvidencePath $finalPath
    Read-AndAssertBlockedEvidence -Path $finalPath | Out-Null
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
