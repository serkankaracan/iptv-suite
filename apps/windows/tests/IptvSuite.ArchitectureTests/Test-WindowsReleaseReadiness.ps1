[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$script:utf8NoBom = New-Object System.Text.UTF8Encoding($false)
$script:repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot "..\..\..\.."))
$script:validatorPath = Join-Path $script:repositoryRoot "eng\Test-WindowsReleaseReadiness.ps1"
$script:runId = [Guid]::NewGuid().ToString("N")
$script:actualEvidenceRoot = Join-Path `
    $script:repositoryRoot `
    ".artifacts\m15-release-readiness\self-test-$($script:runId)"
$script:fixtureRoot = Join-Path `
    ([System.IO.Path]::GetTempPath()) `
    "IptvSuite-M15-ReleaseReadiness-$($script:runId)"

function Assert-TestCondition {
    param(
        [Parameter(Mandatory = $true)]
        [bool]$Condition,

        [Parameter(Mandatory = $true)]
        [string]$Message
    )

    if (-not $Condition) {
        throw "M15 release-readiness self-test failed: $Message"
    }
}

function Write-TestText {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path,

        [Parameter(Mandatory = $true)]
        [string]$Value
    )

    [System.IO.Directory]::CreateDirectory([System.IO.Path]::GetDirectoryName($Path)) | Out-Null
    [System.IO.File]::WriteAllText($Path, $Value, $script:utf8NoBom)
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
        [System.IO.FileShare]::None)
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

function Copy-TestFile {
    param(
        [Parameter(Mandatory = $true)]
        [string]$RelativePath
    )

    $source = Join-Path $script:repositoryRoot $RelativePath
    $destination = Join-Path $script:fixtureRoot $RelativePath
    Assert-TestCondition (Test-Path -LiteralPath $source -PathType Leaf) "fixture source is missing: $RelativePath"
    [System.IO.Directory]::CreateDirectory([System.IO.Path]::GetDirectoryName($destination)) | Out-Null
    Copy-Item -LiteralPath $source -Destination $destination -Force
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

function Invoke-AllowedAudit {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Root,

        [Parameter(Mandatory = $true)]
        [string]$EvidencePath,

        [string]$ValidatorPath = $script:validatorPath
    )

    & $ValidatorPath `
        -RepositoryRoot $Root `
        -EvidencePath $EvidencePath `
        -AllowBlockedInventory | Out-Null
}

function Assert-AuditFailure {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Root,

        [Parameter(Mandatory = $true)]
        [string]$EvidencePath,

        [Parameter(Mandatory = $true)]
        [string]$ExpectedMessage,

        [string]$ValidatorPath = $script:validatorPath,

        [switch]$AllowBlockedInventory
    )

    $actualMessage = $null
    try {
        if ($AllowBlockedInventory) {
            & $ValidatorPath `
                -RepositoryRoot $Root `
                -EvidencePath $EvidencePath `
                -AllowBlockedInventory | Out-Null
        }
        else {
            & $ValidatorPath `
                -RepositoryRoot $Root `
                -EvidencePath $EvidencePath | Out-Null
        }
    }
    catch {
        $actualMessage = $_.Exception.Message
    }

    Assert-TestCondition `
        ($actualMessage -ceq $ExpectedMessage) `
        "expected '$ExpectedMessage', received '$actualMessage'."
}

function Assert-NoIdentityLeakFields {
    param(
        [AllowNull()]
        [object]$Value
    )

    if ($null -eq $Value -or $Value -is [string] -or $Value.GetType().IsPrimitive) {
        return
    }

    if ($Value -is [System.Array]) {
        foreach ($entry in $Value) {
            Assert-NoIdentityLeakFields -Value $entry
        }
        return
    }

    foreach ($property in @($Value.PSObject.Properties)) {
        Assert-TestCondition `
            ($property.Name -notmatch '(?i)^(user(name)?|host(name)?|machine(name)?|computer(name)?|sid|pfn|packagefamily(name)?|packagefullname|certificate(thumbprint)?|thumbprint|repositoryroot|evidencepath|absolutepath)$') `
            "environment identity field '$($property.Name)' leaked into evidence."
        Assert-NoIdentityLeakFields -Value $property.Value
    }
}

function Read-AndAssertEvidence {
    param(
        [Parameter(Mandatory = $true)]
        [string]$EvidencePath,

        [Parameter(Mandatory = $true)]
        [string]$ForbiddenRoot,

        [Nullable[bool]]$ExpectedFinalReleaseFreshAtEvaluation
    )

    Assert-TestCondition (Test-Path -LiteralPath $EvidencePath -PathType Leaf) "evidence was not published."
    $evidenceFile = Get-Item -LiteralPath $EvidencePath -Force
    Assert-TestCondition `
        ($evidenceFile.Length -gt 0 -and $evidenceFile.Length -le 1MB) `
        "evidence is empty or exceeds the 1 MiB bound."
    Assert-TestCondition `
        (($evidenceFile.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -eq 0) `
        "evidence is a reparse point."

    $raw = [System.IO.File]::ReadAllText($EvidencePath)
    try {
        $evidence = $raw | ConvertFrom-Json
    }
    catch {
        throw "M15 release-readiness self-test failed: evidence JSON is invalid."
    }

    Assert-ExactStringSet `
        -Actual @($evidence.PSObject.Properties.Name) `
        -Expected @(
            "schemaVersion",
            "result",
            "technicalBaselinePassed",
            "releaseReady",
            "commitSha",
            "manifest",
            "packaging",
            "storage",
            "assets",
            "assetProvenance",
            "lockfiles",
            "packageInventory",
            "packageInventoryPolicy",
            "packageSbomAcceptance",
            "packageVulnerabilityAcceptance",
            "blockers") `
        -Message "evidence root schema changed."
    Assert-TestCondition ($evidence.schemaVersion -eq 6) "schemaVersion must be 6."
    Assert-TestCondition ($evidence.result -ceq "blocked") "result must remain blocked."
    Assert-TestCondition `
        ($evidence.technicalBaselinePassed -is [bool] -and $evidence.technicalBaselinePassed) `
        "technicalBaselinePassed must be exact Boolean true."
    Assert-TestCondition `
        ($evidence.releaseReady -is [bool] -and -not $evidence.releaseReady) `
        "releaseReady must be exact Boolean false."
    Assert-TestCondition (@($evidence.assets).Count -eq 8) "the exact eight production assets were not inventoried."
    $assetProvenance = $evidence.assetProvenance
    Assert-ExactStringSet `
        -Actual @($assetProvenance.PSObject.Properties.Name) `
        -Expected @(
            "ledgerSha256",
            "decision",
            "scope",
            "provenanceKind",
            "generatorPath",
            "generatorVersion",
            "generatorSha256",
            "algorithmVersion",
            "canonicalAssetSetSha256",
            "assetCount",
            "deterministicRecipeVerified",
            "sourceAssetDependencyCount",
            "thirdPartyAssetInputCount",
            "fontInputCount",
            "textInputCount",
            "trademarkInputCount",
            "developmentPlaceholderOnly",
            "productionBrandApproved",
            "copyrightOwnershipDetermined",
            "redistributionDecisionComplete",
            "legalReviewComplete") `
        -Message "the asset provenance evidence schema changed."
    Assert-TestCondition `
        ($assetProvenance.ledgerSha256 -ceq
            "8006c56170202457815f3768dfcff56236b661a4dbb57aa7b7bf3a5acdcc6412" -and
         $assetProvenance.decision -ceq "AcceptGeneratedAssetProvenance" -and
         $assetProvenance.scope -ceq "ExactWindowsPackageAssetOriginOnly" -and
         $assetProvenance.provenanceKind -ceq
            "GeneratedBySourceControlledDeterministicRecipe" -and
         $assetProvenance.generatorPath -ceq
            "eng/New-WindowsProductionAssets.ps1" -and
         $assetProvenance.generatorVersion -ceq "1.0.0" -and
         $assetProvenance.generatorSha256 -ceq
            "4ac099e8da587b5df61817ab92071235e4e91408d891f5cafa3037599d7f603b" -and
         $assetProvenance.algorithmVersion -ceq
            "WindowsProductionAssets-Rgba8Filter0FixedHuffmanLz77-PngFrameIco-v1" -and
         $assetProvenance.canonicalAssetSetSha256 -ceq
            "6338f26af851a45eb4c7da593430ef1eab5a34afa6013365c2621fbfa0957777" -and
         $assetProvenance.assetCount -eq 8 -and
         $assetProvenance.deterministicRecipeVerified -is [bool] -and
         $assetProvenance.deterministicRecipeVerified -and
         $assetProvenance.sourceAssetDependencyCount -eq 0 -and
         $assetProvenance.thirdPartyAssetInputCount -eq 0 -and
         $assetProvenance.fontInputCount -eq 0 -and
         $assetProvenance.textInputCount -eq 0 -and
         $assetProvenance.trademarkInputCount -eq 0 -and
         $assetProvenance.developmentPlaceholderOnly -is [bool] -and
         $assetProvenance.developmentPlaceholderOnly -and
         $assetProvenance.productionBrandApproved -is [bool] -and
         -not $assetProvenance.productionBrandApproved -and
         $assetProvenance.copyrightOwnershipDetermined -is [bool] -and
         -not $assetProvenance.copyrightOwnershipDetermined -and
         $assetProvenance.redistributionDecisionComplete -is [bool] -and
         -not $assetProvenance.redistributionDecisionComplete -and
         $assetProvenance.legalReviewComplete -is [bool] -and
         -not $assetProvenance.legalReviewComplete) `
        "the exact generated asset provenance disposition changed."
    Assert-TestCondition (@($evidence.lockfiles).Count -eq 4) "the exact four production lockfiles were not inventoried."
    Assert-TestCondition (@($evidence.packageInventory).Count -eq 23) "the exact production package inventory changed."
    Assert-ExactStringSet `
        -Actual @($evidence.packaging.PSObject.Properties.Name) `
        -Expected @(
            "architecture",
            "runtimeIdentifier",
            "releaseArchitectures",
            "arm64Disposition",
            "architectureImportSurfaceAuditVersion",
            "sourceControlledArchitectureImportSurfacePassed",
            "selfContained",
            "windowsAppSdkSelfContained",
            "appxBundle",
            "executionLevel",
            "uiAccess",
            "dpiAwareness") `
        -Message "the packaging evidence schema changed."
    Assert-ExactStringSet `
        -Actual @($evidence.packaging.releaseArchitectures | ForEach-Object { [string]$_ }) `
        -Expected @("x64") `
        -Message "the Windows MVP release architecture set changed."
    Assert-TestCondition `
        ($evidence.packaging.architecture -ceq "x64" -and
         $evidence.packaging.runtimeIdentifier -ceq "win-x64" -and
         $evidence.packaging.arm64Disposition -ceq "DeferredUntilNativeArm64ChainAccepted" -and
         $evidence.packaging.architectureImportSurfaceAuditVersion -eq 1 -and
         $evidence.packaging.sourceControlledArchitectureImportSurfacePassed -is [bool] -and
         $evidence.packaging.sourceControlledArchitectureImportSurfacePassed -and
         $evidence.packaging.appxBundle -ceq "Never") `
        "the x64-only Windows MVP release disposition changed."
    Assert-ExactStringSet `
        -Actual @($evidence.storage.PSObject.Properties.Name) `
        -Expected @(
            "catalogRoot",
            "protectedStoreRoot",
            "baseDirectoryUse",
            "knownInstallRootDiscoveryPatternScanPassed",
            "installRootDiscoveryDenylistVersion") `
        -Message "the storage/install-root evidence schema changed."
    Assert-TestCondition `
        ($evidence.storage.knownInstallRootDiscoveryPatternScanPassed -is [bool] -and
         $evidence.storage.knownInstallRootDiscoveryPatternScanPassed -and
         $evidence.storage.installRootDiscoveryDenylistVersion -eq 1) `
        "the install-root discovery scan contract changed."
    Assert-TestCondition `
        ($evidence.packageInventoryPolicy.mode -ceq "exact-current-production-package-names" -and
         $evidence.packageInventoryPolicy.expectedPackageCount -eq 23 -and
         $evidence.packageInventoryPolicy.exactPackageNamesLocked -is [bool] -and
         $evidence.packageInventoryPolicy.exactPackageNamesLocked -and
         $evidence.packageInventoryPolicy.legalSbomComplete -is [bool] -and
         -not $evidence.packageInventoryPolicy.legalSbomComplete) `
        "the exact package-inventory policy changed."

    $packageSbomAcceptance = $evidence.packageSbomAcceptance
    Assert-ExactStringSet `
        -Actual @($packageSbomAcceptance.PSObject.Properties.Name) `
        -Expected @(
            "decision",
            "scope",
            "runCompletedAtUtc",
            "repository",
            "workflowPath",
            "workflowName",
            "runId",
            "runNumber",
            "runAttempt",
            "runEvent",
            "runBranch",
            "runHeadSha",
            "runConclusion",
            "packageJobId",
            "packageJobName",
            "packageJobConclusion",
            "artifactId",
            "artifactName",
            "artifactSizeBytes",
            "artifactDigestSha256",
            "lastSuccessMemberName",
            "lastSuccessMemberLength",
            "lastSuccessMemberSha256",
            "sbomSummaryMemberName",
            "sbomSummaryMemberLength",
            "sbomSummaryMemberSha256",
            "sbomMemberName",
            "sbomMemberLength",
            "sbomMemberSha256",
            "configuration",
            "dotNetSdk",
            "sbomFormat",
            "toolPackageId",
            "toolVersion",
            "toolNupkgSha256",
            "toolShimSha256",
            "officialValidationPassed",
            "strictValidationPassed",
            "productionInputCount",
            "productionInputSetCanonicalSha256",
            "contractSourceCount",
            "contractSourceSetCanonicalSha256",
            "packageProducingSnapshotFileCount",
            "packageProducingSnapshotSha256",
            "applicationPackageFile",
            "applicationPackageLength",
            "applicationPackageSha256",
            "applicationIdentityName",
            "applicationVersion",
            "applicationSignatureStatus",
            "runtimePackageFile",
            "runtimePackageLength",
            "runtimePackageSha256",
            "runtimeIdentityName",
            "runtimeVersion",
            "runtimeSignatureStatus",
            "architecture",
            "fileCount",
            "componentCount",
            "packageCount",
            "relationshipCount",
            "producerBlockerDisposition",
            "producerSbomPending",
            "closedBlocker",
            "legalSbomComplete") `
        -Message "the package SBOM acceptance evidence schema changed."
    Assert-TestCondition `
        ($packageSbomAcceptance.decision -ceq "AcceptTechnicalPackageBoundSbom" -and
         $packageSbomAcceptance.scope -ceq "TechnicalPackageBoundSbomOnly" -and
         $packageSbomAcceptance.runCompletedAtUtc -ceq "2026-08-26T14:18:56Z" -and
         $packageSbomAcceptance.repository -ceq "serkankaracan/iptv-suite" -and
         $packageSbomAcceptance.workflowPath -ceq ".github/workflows/windows-package-sbom.yml" -and
         $packageSbomAcceptance.workflowName -ceq "Windows package SBOM producer" -and
         $packageSbomAcceptance.runId -eq 32978788187 -and
         $packageSbomAcceptance.runNumber -eq 5 -and
         $packageSbomAcceptance.runAttempt -eq 1 -and
         $packageSbomAcceptance.runEvent -ceq "workflow_dispatch" -and
         $packageSbomAcceptance.runBranch -ceq "main" -and
         $packageSbomAcceptance.runHeadSha -ceq "62b601e871ca41a6d2100dfb2375b683bbd8e0ca" -and
         $packageSbomAcceptance.runConclusion -ceq "success" -and
         $packageSbomAcceptance.packageJobId -eq 98209973083 -and
         $packageSbomAcceptance.packageJobName -ceq "Package-bound SBOM producer gate" -and
         $packageSbomAcceptance.packageJobConclusion -ceq "success" -and
         $packageSbomAcceptance.artifactId -eq 9610820189 -and
         $packageSbomAcceptance.artifactName -ceq "windows-msix-smoke-evidence" -and
         $packageSbomAcceptance.artifactSizeBytes -eq 7740 -and
         $packageSbomAcceptance.artifactDigestSha256 -ceq "79786ab5bbabde942d6f45cb9e47bbee814980be11a7817b208259d88ca03926") `
        "the hosted package SBOM workflow evidence changed."
    Assert-TestCondition `
        ($packageSbomAcceptance.lastSuccessMemberName -ceq "last-success.json" -and
         $packageSbomAcceptance.lastSuccessMemberLength -eq 18711 -and
         $packageSbomAcceptance.lastSuccessMemberSha256 -ceq "39ff344dc33ecd3b943c37ec70f9d73d296726b57b3c5bb10503ab4d143895ca" -and
         $packageSbomAcceptance.sbomSummaryMemberName -ceq "package-sbom-summary.json" -and
         $packageSbomAcceptance.sbomSummaryMemberLength -eq 1985 -and
         $packageSbomAcceptance.sbomSummaryMemberSha256 -ceq "7553492ee17022d73d5801ee75fee5be1230d1b85fa3c6f8071aecdd9be0cfc2" -and
         $packageSbomAcceptance.sbomMemberName -ceq "package-sbom.spdx.json" -and
         $packageSbomAcceptance.sbomMemberLength -eq 50566 -and
         $packageSbomAcceptance.sbomMemberSha256 -ceq "03c29c18da6b0323c88149805e6eeef6f43d35ec329c08d2b93fc5247b04a903") `
        "the hosted package SBOM artifact member evidence changed."
    Assert-TestCondition `
        ($packageSbomAcceptance.configuration -ceq "Release" -and
         $packageSbomAcceptance.dotNetSdk -ceq "10.0.302" -and
         $packageSbomAcceptance.sbomFormat -ceq "SPDX-2.2" -and
         $packageSbomAcceptance.toolPackageId -ceq "microsoft.sbom.dotnettool" -and
         $packageSbomAcceptance.toolVersion -ceq "4.1.5" -and
         $packageSbomAcceptance.toolNupkgSha256 -ceq "00e1fb81c01f4e9ad7a9d00f365bb3f3776cde6fecdd15cc3adbbce1f83d14bb" -and
         $packageSbomAcceptance.toolShimSha256 -ceq "c8e151612c03db7a5b8d680cd5ccdfd1d9676f36d43c33cec2a4397fb19ada55" -and
         $packageSbomAcceptance.officialValidationPassed -is [bool] -and
         $packageSbomAcceptance.officialValidationPassed -and
         $packageSbomAcceptance.strictValidationPassed -is [bool] -and
         $packageSbomAcceptance.strictValidationPassed -and
         $packageSbomAcceptance.productionInputCount -eq 10 -and
         $packageSbomAcceptance.productionInputSetCanonicalSha256 -ceq "293481fe2194c6f1fde3f667cf45872f4790e0b5955e17ac88c2d16a885b81df" -and
         $packageSbomAcceptance.contractSourceCount -eq 7 -and
         $packageSbomAcceptance.contractSourceSetCanonicalSha256 -ceq "72c195557451beed09a43740036f186ff4c0091d14148024a995e3f90d20342d" -and
         $packageSbomAcceptance.packageProducingSnapshotFileCount -eq 113 -and
         $packageSbomAcceptance.packageProducingSnapshotSha256 -ceq "9a6313a187e7a34ea17163745dfcbe3d330f4acddbac2e2054d610edd4e49493") `
        "the package SBOM tool or source binding evidence changed."
    Assert-TestCondition `
        ($packageSbomAcceptance.applicationPackageFile -ceq "IptvSuite.Windows_0.1.0.0_x64.msix" -and
         $packageSbomAcceptance.applicationPackageLength -eq 29852385 -and
         $packageSbomAcceptance.applicationPackageSha256 -ceq "2fcfbd3cd59501e605596a6e77d567979993e78d9986566964cb21a0f2229a3a" -and
         $packageSbomAcceptance.applicationIdentityName -ceq "IptvSuite.LocalDev.6f0d9a64" -and
         $packageSbomAcceptance.applicationVersion -ceq "0.1.0.0" -and
         $packageSbomAcceptance.applicationSignatureStatus -ceq "Valid" -and
         $packageSbomAcceptance.runtimePackageFile -ceq "Microsoft.WindowsAppRuntime.2.msix" -and
         $packageSbomAcceptance.runtimePackageLength -eq 46787781 -and
         $packageSbomAcceptance.runtimePackageSha256 -ceq "a3ce5b76713133dfd3b378e81c43a89954c664fcd70fd0c070e409ed3de03ebf" -and
         $packageSbomAcceptance.runtimeIdentityName -ceq "Microsoft.WindowsAppRuntime.2" -and
         $packageSbomAcceptance.runtimeVersion -ceq "2.4.0.0" -and
         $packageSbomAcceptance.runtimeSignatureStatus -ceq "Valid" -and
         $packageSbomAcceptance.architecture -ceq "x64" -and
         $packageSbomAcceptance.fileCount -eq 2 -and
         $packageSbomAcceptance.componentCount -eq 24 -and
         $packageSbomAcceptance.packageCount -eq 27 -and
         $packageSbomAcceptance.relationshipCount -eq 43) `
        "the accepted signed release-set evidence changed."
    Assert-TestCondition `
        ($packageSbomAcceptance.producerBlockerDisposition -ceq "HostedAcceptancePending" -and
         $packageSbomAcceptance.producerSbomPending -is [bool] -and
         $packageSbomAcceptance.producerSbomPending -and
         $packageSbomAcceptance.closedBlocker -ceq "SbomPending" -and
         $packageSbomAcceptance.legalSbomComplete -is [bool] -and
         -not $packageSbomAcceptance.legalSbomComplete) `
        "the bounded package SBOM acceptance disposition changed."

    $packageVulnerabilityAcceptance = $evidence.packageVulnerabilityAcceptance
    Assert-ExactStringSet `
        -Actual @($packageVulnerabilityAcceptance.PSObject.Properties.Name) `
        -Expected @(
            "ledgerSha256",
            "decision",
            "scope",
            "runCompletedAtUtc",
            "freshThroughUtc",
            "freshnessPolicy",
            "maximumAgeDays",
            "freshAtEvaluation",
            "finalReleaseMaximumAgeHours",
            "finalReleaseFreshAtEvaluation",
            "repository",
            "workflowPath",
            "workflowName",
            "workflowId",
            "runId",
            "runNumber",
            "runAttempt",
            "runHeadSha",
            "runConclusion",
            "jobId",
            "jobName",
            "jobConclusion",
            "artifactId",
            "artifactName",
            "artifactDigestSha256",
            "lastSuccessMemberLength",
            "lastSuccessMemberSha256",
            "packageSbomAcceptanceSha256",
            "observedAtUtc",
            "producerRepositoryCommitSha",
            "dotNetSdk",
            "projectPath",
            "targetFramework",
            "auditSourceId",
            "auditSourceConfigSha256",
            "restoreProjectCount",
            "restoreSkippedCount",
            "restoreProjectsAuditedCount",
            "productionProjectCount",
            "productionLockfileCount",
            "productionPackageCount",
            "topLevelPackageCount",
            "transitivePackageCount",
            "contractSnapshotSha256",
            "productionPackageGraphSha256",
            "knownDirectVulnerabilityCount",
            "knownTransitiveVulnerabilityCount",
            "knownVulnerabilityCount",
            "officialOutputValidationPassed",
            "strictValidationPassed",
            "producerCheckpointOnly",
            "producerCveReviewPending",
            "effectiveClosedBlocker",
            "cveFreeClaim",
            "legalReviewComplete") `
        -Message "the package vulnerability acceptance evidence schema changed."
    Assert-TestCondition `
        ($packageVulnerabilityAcceptance.ledgerSha256 -ceq
            "a7f5e50f37337442d770b8d9a026dc5a9cd843d833c03af13b0689a0b69099e5" -and
         $packageVulnerabilityAcceptance.decision -ceq
            "AcceptTechnicalKnownVulnerabilityReview" -and
         $packageVulnerabilityAcceptance.scope -ceq
            "ProductionWindowsLeafKnownVulnerabilityReviewOnly" -and
         $packageVulnerabilityAcceptance.runCompletedAtUtc -ceq
            "2026-08-26T04:17:16Z" -and
         $packageVulnerabilityAcceptance.freshThroughUtc -ceq
            "2026-09-02T04:17:16Z" -and
         $packageVulnerabilityAcceptance.freshnessPolicy -ceq
            "RunCompletionPlus7Days" -and
         $packageVulnerabilityAcceptance.maximumAgeDays -eq 7 -and
         $packageVulnerabilityAcceptance.freshAtEvaluation -is [bool] -and
         $packageVulnerabilityAcceptance.finalReleaseMaximumAgeHours -eq 24 -and
         $packageVulnerabilityAcceptance.finalReleaseFreshAtEvaluation -is [bool]) `
        "the bounded package vulnerability freshness disposition changed."
    $expectedVulnerabilityFresh =
        [bool]$packageVulnerabilityAcceptance.freshAtEvaluation
    if ($null -eq $ExpectedFinalReleaseFreshAtEvaluation) {
        $expectedFinalReleaseFresh =
            [bool]$packageVulnerabilityAcceptance.finalReleaseFreshAtEvaluation
    }
    else {
        $expectedFinalReleaseFresh =
            [bool]$ExpectedFinalReleaseFreshAtEvaluation
        Assert-TestCondition `
            ($packageVulnerabilityAcceptance.finalReleaseFreshAtEvaluation -eq
                $expectedFinalReleaseFresh) `
            "the final-release freshness evidence changed."
    }
    Assert-TestCondition `
        (-not $expectedFinalReleaseFresh -or $expectedVulnerabilityFresh) `
        "final-release freshness cannot outlive technical freshness."
    Assert-TestCondition `
        ($packageVulnerabilityAcceptance.repository -ceq "serkankaracan/iptv-suite" -and
         $packageVulnerabilityAcceptance.workflowPath -ceq
            ".github/workflows/windows-cve-review.yml" -and
         $packageVulnerabilityAcceptance.workflowName -ceq
            "Windows known-vulnerability producer" -and
         $packageVulnerabilityAcceptance.workflowId -eq 342499403 -and
         $packageVulnerabilityAcceptance.runId -eq 32929529931 -and
         $packageVulnerabilityAcceptance.runNumber -eq 18 -and
         $packageVulnerabilityAcceptance.runAttempt -eq 1 -and
         $packageVulnerabilityAcceptance.runHeadSha -ceq
            "ef876d103223165bf546fb60fddef102e74c2c08" -and
         $packageVulnerabilityAcceptance.runConclusion -ceq "success" -and
         $packageVulnerabilityAcceptance.jobId -eq 98058958334 -and
         $packageVulnerabilityAcceptance.jobName -ceq
            "Known-vulnerability producer gate" -and
         $packageVulnerabilityAcceptance.jobConclusion -ceq "success" -and
         $packageVulnerabilityAcceptance.artifactId -eq 9592732443 -and
         $packageVulnerabilityAcceptance.artifactName -ceq
            "windows-cve-review-evidence" -and
         $packageVulnerabilityAcceptance.artifactDigestSha256 -ceq
            "dd3425616f584993578c422123130f8737155b4c5012e477c9639a3125fb87fb" -and
         $packageVulnerabilityAcceptance.lastSuccessMemberLength -eq 2403 -and
         $packageVulnerabilityAcceptance.lastSuccessMemberSha256 -ceq
            "6890351195a17c207169a86fed60b4d46d6afd2851e4fe7567e4c704d43d6bb9" -and
         $packageVulnerabilityAcceptance.packageSbomAcceptanceSha256 -ceq
            "69bfd62dc8145ba280c1aa45c92dde15173440d4378cb568df68beef3f814c80" -and
         $packageVulnerabilityAcceptance.observedAtUtc -ceq
            "2026-08-26T04:17:03.5506767Z" -and
         $packageVulnerabilityAcceptance.producerRepositoryCommitSha -ceq
            "ef876d103223165bf546fb60fddef102e74c2c08") `
        "the hosted package vulnerability workflow or artifact evidence changed."
    Assert-TestCondition `
        ($packageVulnerabilityAcceptance.restoreProjectCount -eq 4 -and
         $packageVulnerabilityAcceptance.auditSourceId -ceq
            "nuget.org-audit-vulnerabilityinfo" -and
         $packageVulnerabilityAcceptance.restoreSkippedCount -eq 0 -and
         $packageVulnerabilityAcceptance.restoreProjectsAuditedCount -eq 4 -and
         $packageVulnerabilityAcceptance.productionProjectCount -eq 4 -and
         $packageVulnerabilityAcceptance.productionLockfileCount -eq 4 -and
         $packageVulnerabilityAcceptance.productionPackageCount -eq 23 -and
         $packageVulnerabilityAcceptance.topLevelPackageCount -eq 2 -and
         $packageVulnerabilityAcceptance.transitivePackageCount -eq 21 -and
         $packageVulnerabilityAcceptance.contractSnapshotSha256 -ceq
            "6b09978b5ee3ffc4d14e09458724a3d18fd1d23c5ec9ab3134dd25bfc7e91ff3" -and
         $packageVulnerabilityAcceptance.productionPackageGraphSha256 -ceq
            "760562b81e0097913e1daf4ec88c67596337dd6636ed6d88c8f645424dc50b6e" -and
         $packageVulnerabilityAcceptance.knownDirectVulnerabilityCount -eq 0 -and
         $packageVulnerabilityAcceptance.knownTransitiveVulnerabilityCount -eq 0 -and
         $packageVulnerabilityAcceptance.knownVulnerabilityCount -eq 0 -and
         $packageVulnerabilityAcceptance.officialOutputValidationPassed -is [bool] -and
         $packageVulnerabilityAcceptance.officialOutputValidationPassed -and
         $packageVulnerabilityAcceptance.strictValidationPassed -is [bool] -and
         $packageVulnerabilityAcceptance.strictValidationPassed -and
         $packageVulnerabilityAcceptance.producerCheckpointOnly -is [bool] -and
         $packageVulnerabilityAcceptance.producerCheckpointOnly -and
         $packageVulnerabilityAcceptance.producerCveReviewPending -is [bool] -and
         $packageVulnerabilityAcceptance.producerCveReviewPending -and
         $packageVulnerabilityAcceptance.effectiveClosedBlocker -ceq
            $(if ($expectedFinalReleaseFresh) { "CveReviewPending" } else { "None" }) -and
         $packageVulnerabilityAcceptance.cveFreeClaim -is [bool] -and
         -not $packageVulnerabilityAcceptance.cveFreeClaim -and
         $packageVulnerabilityAcceptance.legalReviewComplete -is [bool] -and
         -not $packageVulnerabilityAcceptance.legalReviewComplete) `
        "the exact package vulnerability disposition changed."

    $expectedBlockers = @(
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
    if (-not $expectedFinalReleaseFresh) {
        $expectedBlockers += "CveReviewPending"
    }
    Assert-ExactStringSet `
        -Actual @($evidence.blockers | ForEach-Object { [string]$_ }) `
        -Expected $expectedBlockers `
        -Message "the explicit release blocker set changed."

    Assert-TestCondition `
        ($raw.IndexOf($ForbiddenRoot, [System.StringComparison]::OrdinalIgnoreCase) -lt 0) `
        "an absolute repository root leaked into evidence."
    Assert-TestCondition ($raw -notmatch '(?i)[a-z]:[\\/]') "a drive-qualified path leaked into evidence."
    Assert-TestCondition ($raw -notmatch '\\\\[^\\]') "a UNC path leaked into evidence."
    foreach ($environmentIdentity in @($env:USERNAME, $env:COMPUTERNAME)) {
        if (-not [string]::IsNullOrWhiteSpace($environmentIdentity) -and $environmentIdentity.Length -ge 4) {
            Assert-TestCondition `
                ($raw.IndexOf($environmentIdentity, [System.StringComparison]::OrdinalIgnoreCase) -lt 0) `
                "a user or machine identity value leaked into evidence."
        }
    }

    foreach ($pathRecord in @(@($evidence.assets) + @($evidence.lockfiles))) {
        $relativePath = [string]$pathRecord.path
        Assert-TestCondition `
            (-not [string]::IsNullOrWhiteSpace($relativePath) -and
             -not [System.IO.Path]::IsPathRooted($relativePath) -and
             $relativePath -notmatch '(^|/)\.\.(/|$)' -and
             $relativePath.StartsWith("apps/windows/src/", [System.StringComparison]::Ordinal)) `
            "an inventory path is not a safe repository-relative production path."
    }

    Assert-NoIdentityLeakFields -Value $evidence
    return $evidence
}

function Initialize-IsolatedFixture {
    [System.IO.Directory]::CreateDirectory($script:fixtureRoot) | Out-Null
    [System.IO.Directory]::CreateDirectory((Join-Path $script:fixtureRoot "eng")) | Out-Null

    $requiredFiles = @(
        ".config\dotnet-tools.json",
        ".github\workflows\windows-cve-review.yml",
        ".github\workflows\windows-package-sbom.yml",
        "global.json",
        "NuGet.config",
        "Directory.Build.props",
        "Directory.Packages.props",
        "Directory.Solution.props",
        "apps\windows\IptvSuite.Windows.sln",
        "eng\Invoke-WindowsPackageSbom.ps1",
        "eng\Invoke-WindowsPackageSmoke.ps1",
        "eng\Invoke-WindowsPackageVulnerabilityAudit.ps1",
        "eng\New-WindowsProductionAssets.ps1",
        "eng\WindowsPackageInstallRootAudit.ps1",
        "eng\WindowsPackageSbom.ps1",
        "eng\WindowsPackageVulnerabilityAudit.ps1",
        "eng\windows-package-vulnerability-audit.config",
        "eng\windows-package-vulnerability-acceptance.json",
        "eng\windows-production-asset-provenance.json",
        "eng\windows-package-sbom-acceptance.json",
        "eng\windows-package-sbom-tool.json")
    foreach ($relativePath in $requiredFiles) {
        Copy-TestFile -RelativePath $relativePath
    }

    $productionSourceRoot = Join-Path $script:repositoryRoot "apps\windows\src"
    $productionSourceFiles = @(
        Get-ChildItem -LiteralPath $productionSourceRoot -Recurse -Force -File |
            Where-Object {
                $_.FullName -notmatch '[\\/](?:bin|obj)[\\/]'
            })
    Assert-TestCondition ($productionSourceFiles.Count -eq 105) `
        "the exact package-producing source fixture count changed."
    foreach ($sourceFile in $productionSourceFiles) {
        $relativePath = $sourceFile.FullName.Substring(
            $script:repositoryRoot.Length + 1)
        Copy-TestFile -RelativePath $relativePath
    }
}

Assert-TestCondition (Test-Path -LiteralPath $script:validatorPath -PathType Leaf) "validator script is missing."
$validatorText = [System.IO.File]::ReadAllText($script:validatorPath)
Assert-TestCondition `
    ($validatorText.Contains(
        '$packageProducingSnapshot = Get-PackageProducingSnapshot -Root $Root') -and
     $validatorText.Contains(
        '$publicationPackageProducingSnapshot = Get-PackageProducingSnapshot') -and
     $validatorText.Contains(
        '$publicationPackageProducingSnapshot.CanonicalBytes -eq') -and
     $validatorText.Contains(
        '$validatedPackageProducingSnapshot.CanonicalBytes') -and
     $validatorText.Contains(
        '[System.IO.Directory]::EnumerateFileSystemEntries(')) `
    "the bounded two-pass package-producing snapshot contract changed."
Assert-TestCondition `
    ([regex]::Matches(
        $validatorText,
        'Read-PackageVulnerabilityAcceptance').Count -eq 3 -and
     $validatorText.Contains(
         '$publicationPackageVulnerabilityValidation.FreshAtEvaluation') -and
     $validatorText.Contains(
         '$publicationPackageVulnerabilityValidation.FinalReleaseFreshAtEvaluation') -and
     $validatorText.Contains(
         '$publicationPackageVulnerabilityValidation.ContractSourceSetSha256')) `
    "the bounded two-pass package vulnerability acceptance contract changed."
Assert-TestCondition `
    ($validatorText.Contains(
        '$helperScriptBlock = [ScriptBlock]::Create($normalizedHelperText)') -and
     $validatorText.Contains('-CapturedText $normalizedHelperText') -and
     -not $validatorText.Contains('. $helperFile.FullName')) `
    "the captured package vulnerability helper execution contract changed."
Assert-TestCondition `
    ([regex]::Matches(
        $validatorText,
        'Read-ProductionAssetProvenance').Count -eq 3 -and
     $validatorText.Contains(
        '& $generatorFile.FullName -VerifyRoot $Root 6>&1 | Out-Null') -and
     $validatorText.Contains(
        '$publicationAssetProvenance = Read-ProductionAssetProvenance')) `
    "the two-pass deterministic asset provenance contract changed."

try {
    [System.IO.Directory]::CreateDirectory($script:actualEvidenceRoot) | Out-Null
    $actualEvidencePath = Join-Path $script:actualEvidenceRoot "readiness-summary.json"
    Invoke-AllowedAudit -Root $script:repositoryRoot -EvidencePath $actualEvidencePath
    Read-AndAssertEvidence `
        -EvidencePath $actualEvidencePath `
        -ForbiddenRoot $script:repositoryRoot | Out-Null

    Assert-AuditFailure `
        -Root $script:repositoryRoot `
        -EvidencePath $actualEvidencePath `
        -ExpectedMessage "M15ReleaseReadinessBlocked: releaseReady=false; evidence was published."

    Initialize-IsolatedFixture
    $fixtureEvidenceRoot = Join-Path $script:fixtureRoot ".artifacts\m15-release-readiness\self-test"
    $fixtureEvidencePath = Join-Path $fixtureEvidenceRoot "valid.json"
    Invoke-AllowedAudit -Root $script:fixtureRoot -EvidencePath $fixtureEvidencePath
    Read-AndAssertEvidence `
        -EvidencePath $fixtureEvidencePath `
        -ForbiddenRoot $script:fixtureRoot | Out-Null

    $assetProvenanceRelativePath =
        "eng\windows-production-asset-provenance.json"
    $assetProvenancePath = Join-Path `
        $script:fixtureRoot `
        $assetProvenanceRelativePath
    $assetProvenanceText = [System.IO.File]::ReadAllText($assetProvenancePath)
    $acceptedAssetProvenanceSha256 =
        "8006c56170202457815f3768dfcff56236b661a4dbb57aa7b7bf3a5acdcc6412"

    Remove-Item -LiteralPath $assetProvenancePath -Force
    Assert-AuditFailure `
        -Root $script:fixtureRoot `
        -EvidencePath (Join-Path $fixtureEvidenceRoot "missing-asset-provenance.json") `
        -ExpectedMessage "M15TechnicalInvariant:AssetProvenanceInvalid" `
        -AllowBlockedInventory
    Copy-TestFile -RelativePath $assetProvenanceRelativePath

    $duplicateAssetProvenanceText = $assetProvenanceText.Replace(
        '  "decision": "AcceptGeneratedAssetProvenance",',
        ('  "duplicateProbe": { "value": 1, "value": 2 },' + "`n" +
         '  "decision": "AcceptGeneratedAssetProvenance",'))
    Assert-TestCondition `
        ($duplicateAssetProvenanceText -cne $assetProvenanceText) `
        "duplicate asset provenance mutation was not applied."
    Write-TestText -Path $assetProvenancePath -Value $duplicateAssetProvenanceText
    $duplicateAssetProvenanceSha256 = Get-TestFileSha256 `
        -Path $assetProvenancePath
    $duplicateAssetValidatorText = $validatorText.Replace(
        $acceptedAssetProvenanceSha256,
        $duplicateAssetProvenanceSha256)
    Assert-TestCondition ($duplicateAssetValidatorText -cne $validatorText) `
        "duplicate asset provenance validator mutation was not applied."
    $duplicateAssetValidatorPath = Join-Path `
        $script:fixtureRoot `
        "eng\Test-WindowsReleaseReadiness.asset-duplicate.ps1"
    Write-TestText `
        -Path $duplicateAssetValidatorPath `
        -Value $duplicateAssetValidatorText
    try {
        Assert-AuditFailure `
            -Root $script:fixtureRoot `
            -EvidencePath (Join-Path $fixtureEvidenceRoot "duplicate-asset-provenance.json") `
            -ExpectedMessage "M15TechnicalInvariant:AssetProvenanceDuplicateProperty" `
            -ValidatorPath $duplicateAssetValidatorPath `
            -AllowBlockedInventory
    }
    finally {
        Remove-Item `
            -LiteralPath $duplicateAssetValidatorPath `
            -Force `
            -ErrorAction SilentlyContinue
    }
    Copy-TestFile -RelativePath $assetProvenanceRelativePath

    $nonClaimAssetProvenanceText = $assetProvenanceText.Replace(
        '"productionBrandApproved": false',
        '"productionBrandApproved": true')
    Assert-TestCondition `
        ($nonClaimAssetProvenanceText -cne $assetProvenanceText) `
        "asset provenance non-claim mutation was not applied."
    Write-TestText -Path $assetProvenancePath -Value $nonClaimAssetProvenanceText
    $nonClaimAssetProvenanceSha256 = Get-TestFileSha256 `
        -Path $assetProvenancePath
    $nonClaimAssetValidatorText = $validatorText.Replace(
        $acceptedAssetProvenanceSha256,
        $nonClaimAssetProvenanceSha256)
    Assert-TestCondition ($nonClaimAssetValidatorText -cne $validatorText) `
        "asset provenance non-claim validator mutation was not applied."
    $nonClaimAssetValidatorPath = Join-Path `
        $script:fixtureRoot `
        "eng\Test-WindowsReleaseReadiness.asset-nonclaim.ps1"
    Write-TestText `
        -Path $nonClaimAssetValidatorPath `
        -Value $nonClaimAssetValidatorText
    try {
        Assert-AuditFailure `
            -Root $script:fixtureRoot `
            -EvidencePath (Join-Path $fixtureEvidenceRoot "asset-provenance-nonclaim.json") `
            -ExpectedMessage "M15TechnicalInvariant:AssetProvenanceInvalid" `
            -ValidatorPath $nonClaimAssetValidatorPath `
            -AllowBlockedInventory
    }
    finally {
        Remove-Item `
            -LiteralPath $nonClaimAssetValidatorPath `
            -Force `
            -ErrorAction SilentlyContinue
    }
    Copy-TestFile -RelativePath $assetProvenanceRelativePath

    $externalInputAssetProvenanceText = $assetProvenanceText.Replace(
        '"thirdPartyAssetInputs": []',
        '"thirdPartyAssetInputs": ["external-input"]')
    Assert-TestCondition `
        ($externalInputAssetProvenanceText -cne $assetProvenanceText) `
        "asset provenance external-input mutation was not applied."
    Write-TestText `
        -Path $assetProvenancePath `
        -Value $externalInputAssetProvenanceText
    $externalInputAssetProvenanceSha256 = Get-TestFileSha256 `
        -Path $assetProvenancePath
    $externalInputAssetValidatorText = $validatorText.Replace(
        $acceptedAssetProvenanceSha256,
        $externalInputAssetProvenanceSha256)
    Assert-TestCondition ($externalInputAssetValidatorText -cne $validatorText) `
        "asset provenance external-input validator mutation was not applied."
    $externalInputAssetValidatorPath = Join-Path `
        $script:fixtureRoot `
        "eng\Test-WindowsReleaseReadiness.asset-external-input.ps1"
    Write-TestText `
        -Path $externalInputAssetValidatorPath `
        -Value $externalInputAssetValidatorText
    try {
        Assert-AuditFailure `
            -Root $script:fixtureRoot `
            -EvidencePath (Join-Path $fixtureEvidenceRoot "asset-provenance-external-input.json") `
            -ExpectedMessage "M15TechnicalInvariant:AssetProvenanceInvalid" `
            -ValidatorPath $externalInputAssetValidatorPath `
            -AllowBlockedInventory
    }
    finally {
        Remove-Item `
            -LiteralPath $externalInputAssetValidatorPath `
            -Force `
            -ErrorAction SilentlyContinue
    }
    Copy-TestFile -RelativePath $assetProvenanceRelativePath

    $dimensionAssetProvenanceText = $assetProvenanceText.Replace(
        '"width": 50',
        '"width": 51')
    Assert-TestCondition `
        ($dimensionAssetProvenanceText -cne $assetProvenanceText) `
        "asset provenance dimension mutation was not applied."
    Write-TestText -Path $assetProvenancePath -Value $dimensionAssetProvenanceText
    $dimensionAssetProvenanceSha256 = Get-TestFileSha256 `
        -Path $assetProvenancePath
    $dimensionAssetValidatorText = $validatorText.Replace(
        $acceptedAssetProvenanceSha256,
        $dimensionAssetProvenanceSha256)
    Assert-TestCondition ($dimensionAssetValidatorText -cne $validatorText) `
        "asset provenance dimension validator mutation was not applied."
    $dimensionAssetValidatorPath = Join-Path `
        $script:fixtureRoot `
        "eng\Test-WindowsReleaseReadiness.asset-dimension.ps1"
    Write-TestText `
        -Path $dimensionAssetValidatorPath `
        -Value $dimensionAssetValidatorText
    try {
        Assert-AuditFailure `
            -Root $script:fixtureRoot `
            -EvidencePath (Join-Path $fixtureEvidenceRoot "asset-provenance-dimension.json") `
            -ExpectedMessage "M15TechnicalInvariant:AssetProvenanceInvalid" `
            -ValidatorPath $dimensionAssetValidatorPath `
            -AllowBlockedInventory
    }
    finally {
        Remove-Item `
            -LiteralPath $dimensionAssetValidatorPath `
            -Force `
            -ErrorAction SilentlyContinue
    }
    Copy-TestFile -RelativePath $assetProvenanceRelativePath

    $assetGeneratorRelativePath = "eng\New-WindowsProductionAssets.ps1"
    $assetGeneratorPath = Join-Path $script:fixtureRoot $assetGeneratorRelativePath
    $assetGeneratorText = [System.IO.File]::ReadAllText($assetGeneratorPath)
    Write-TestText `
        -Path $assetGeneratorPath `
        -Value ($assetGeneratorText + "`n# generator drift")
    Assert-AuditFailure `
        -Root $script:fixtureRoot `
        -EvidencePath (Join-Path $fixtureEvidenceRoot "asset-generator-drift.json") `
        -ExpectedMessage "M15TechnicalInvariant:AssetProvenanceInvalid" `
        -AllowBlockedInventory
    Copy-TestFile -RelativePath $assetGeneratorRelativePath

    $assetByteRelativePath =
        "apps\windows\src\IptvSuite.Windows\Assets\StoreLogo.png"
    $assetBytePath = Join-Path $script:fixtureRoot $assetByteRelativePath
    $assetBytes = [System.IO.File]::ReadAllBytes($assetBytePath)
    Assert-TestCondition ($assetBytes.Length -gt 64) `
        "asset byte mutation target is too small."
    $assetBytes[50] = $assetBytes[50] -bxor 1
    [System.IO.File]::WriteAllBytes($assetBytePath, $assetBytes)
    Assert-AuditFailure `
        -Root $script:fixtureRoot `
        -EvidencePath (Join-Path $fixtureEvidenceRoot "asset-byte-drift.json") `
        -ExpectedMessage "M15TechnicalInvariant:AssetProvenanceInvalid" `
        -AllowBlockedInventory
    Copy-TestFile -RelativePath $assetByteRelativePath

    $evaluationClockExpression =
        '$evaluationUtcNow = [DateTimeOffset]::UtcNow'
    Assert-TestCondition `
        ([regex]::Matches(
            $validatorText,
            [regex]::Escape($evaluationClockExpression)).Count -eq 1) `
        "the package vulnerability evaluation clock contract changed."
    $staleValidatorText = $validatorText.Replace(
        $evaluationClockExpression,
        '$evaluationUtcNow = [DateTimeOffset]::new(2026, 9, 3, 0, 0, 0, [TimeSpan]::Zero)')
    Assert-TestCondition ($staleValidatorText -cne $validatorText) `
        "the deterministic stale validator mutation was not applied."

    $finalReleaseBoundaryValidatorText = $validatorText.Replace(
        $evaluationClockExpression,
        '$evaluationUtcNow = [DateTimeOffset]::new(2026, 8, 27, 4, 17, 16, [TimeSpan]::Zero)')
    Assert-TestCondition `
        ($finalReleaseBoundaryValidatorText -cne $validatorText) `
        "the exact final-release freshness boundary mutation was not applied."
    $finalReleaseBoundaryValidatorPath =
        $script:fixtureRoot + "-final-release-boundary-validator.ps1"
    Write-TestText `
        -Path $finalReleaseBoundaryValidatorPath `
        -Value $finalReleaseBoundaryValidatorText
    try {
        $finalReleaseBoundaryEvidencePath = Join-Path `
            $fixtureEvidenceRoot `
            "final-release-boundary.json"
        Invoke-AllowedAudit `
            -Root $script:fixtureRoot `
            -EvidencePath $finalReleaseBoundaryEvidencePath `
            -ValidatorPath $finalReleaseBoundaryValidatorPath
        $finalReleaseBoundaryEvidence = Read-AndAssertEvidence `
            -EvidencePath $finalReleaseBoundaryEvidencePath `
            -ForbiddenRoot $script:fixtureRoot `
            -ExpectedFinalReleaseFreshAtEvaluation $true
        Assert-TestCondition `
            ($finalReleaseBoundaryEvidence.packageVulnerabilityAcceptance.freshAtEvaluation -and
             $finalReleaseBoundaryEvidence.packageVulnerabilityAcceptance.finalReleaseFreshAtEvaluation -and
             $finalReleaseBoundaryEvidence.packageVulnerabilityAcceptance.effectiveClosedBlocker -ceq
                "CveReviewPending" -and
             @($finalReleaseBoundaryEvidence.blockers).Count -eq 12 -and
             @($finalReleaseBoundaryEvidence.blockers) -cnotcontains "CveReviewPending") `
            "the exact 24-hour final-release boundary did not remain accepted."
    }
    finally {
        Remove-Item `
            -LiteralPath $finalReleaseBoundaryValidatorPath `
            -Force `
            -ErrorAction SilentlyContinue
    }

    $finalReleaseExpiredValidatorText = $validatorText.Replace(
        $evaluationClockExpression,
        '$evaluationUtcNow = [DateTimeOffset]::new(2026, 8, 27, 4, 17, 17, [TimeSpan]::Zero)')
    Assert-TestCondition `
        ($finalReleaseExpiredValidatorText -cne $validatorText) `
        "the final-release freshness plus-one-second mutation was not applied."
    $finalReleaseExpiredValidatorPath =
        $script:fixtureRoot + "-final-release-expired-validator.ps1"
    Write-TestText `
        -Path $finalReleaseExpiredValidatorPath `
        -Value $finalReleaseExpiredValidatorText
    try {
        $finalReleaseExpiredEvidencePath = Join-Path `
            $fixtureEvidenceRoot `
            "final-release-expired.json"
        Invoke-AllowedAudit `
            -Root $script:fixtureRoot `
            -EvidencePath $finalReleaseExpiredEvidencePath `
            -ValidatorPath $finalReleaseExpiredValidatorPath
        $finalReleaseExpiredEvidence = Read-AndAssertEvidence `
            -EvidencePath $finalReleaseExpiredEvidencePath `
            -ForbiddenRoot $script:fixtureRoot `
            -ExpectedFinalReleaseFreshAtEvaluation $false
        Assert-TestCondition `
            ($finalReleaseExpiredEvidence.packageVulnerabilityAcceptance.freshAtEvaluation -and
             -not $finalReleaseExpiredEvidence.packageVulnerabilityAcceptance.finalReleaseFreshAtEvaluation -and
             $finalReleaseExpiredEvidence.packageVulnerabilityAcceptance.effectiveClosedBlocker -ceq
                "None" -and
             @($finalReleaseExpiredEvidence.blockers).Count -eq 13 -and
             @($finalReleaseExpiredEvidence.blockers) -ccontains "CveReviewPending") `
            "the final-release blocker did not reopen at 24 hours plus one second."
    }
    finally {
        Remove-Item `
            -LiteralPath $finalReleaseExpiredValidatorPath `
            -Force `
            -ErrorAction SilentlyContinue
    }

    $staleValidatorPath = $script:fixtureRoot + "-stale-validator.ps1"
    Write-TestText -Path $staleValidatorPath -Value $staleValidatorText
    try {
        $staleEvidencePath = Join-Path $fixtureEvidenceRoot "stale.json"
        Invoke-AllowedAudit `
            -Root $script:fixtureRoot `
            -EvidencePath $staleEvidencePath `
            -ValidatorPath $staleValidatorPath
        $staleEvidence = Read-AndAssertEvidence `
            -EvidencePath $staleEvidencePath `
            -ForbiddenRoot $script:fixtureRoot `
            -ExpectedFinalReleaseFreshAtEvaluation $false
        Assert-TestCondition `
            (-not $staleEvidence.packageVulnerabilityAcceptance.freshAtEvaluation -and
             -not $staleEvidence.packageVulnerabilityAcceptance.finalReleaseFreshAtEvaluation -and
             $staleEvidence.packageVulnerabilityAcceptance.effectiveClosedBlocker -ceq "None" -and
             @($staleEvidence.blockers).Count -eq 13 -and
             @($staleEvidence.blockers) -ccontains "CveReviewPending") `
            "stale package vulnerability acceptance did not reopen only its blocker."
    }
    finally {
        Remove-Item -LiteralPath $staleValidatorPath -Force -ErrorAction SilentlyContinue
    }

    $acceptanceRelativePath = "eng\windows-package-sbom-acceptance.json"
    $acceptancePath = Join-Path $script:fixtureRoot $acceptanceRelativePath
    Remove-Item -LiteralPath $acceptancePath -Force
    Assert-AuditFailure `
        -Root $script:fixtureRoot `
        -EvidencePath (Join-Path $fixtureEvidenceRoot "missing-sbom-acceptance.json") `
        -ExpectedMessage "M15TechnicalInvariant:PackageSbomAcceptanceInvalid" `
        -AllowBlockedInventory
    Copy-TestFile -RelativePath $acceptanceRelativePath

    $acceptanceText = [System.IO.File]::ReadAllText($acceptancePath)
    $duplicateAcceptanceText = $acceptanceText.Replace(
        '  "decision": "AcceptTechnicalPackageBoundSbom",',
        ('  "duplicateProbe": { "value": 1, "value": 2 },' + "`n" +
         '  "decision": "AcceptTechnicalPackageBoundSbom",'))
    Assert-TestCondition `
        ($duplicateAcceptanceText -cne $acceptanceText) `
        "nested duplicate package SBOM acceptance mutation was not applied."
    Write-TestText -Path $acceptancePath -Value $duplicateAcceptanceText
    Assert-AuditFailure `
        -Root $script:fixtureRoot `
        -EvidencePath (Join-Path $fixtureEvidenceRoot "duplicate-sbom-acceptance.json") `
        -ExpectedMessage "M15TechnicalInvariant:PackageSbomAcceptanceDuplicateProperty" `
        -AllowBlockedInventory
    Copy-TestFile -RelativePath $acceptanceRelativePath

    $tamperedAcceptanceText = $acceptanceText.Replace(
        '"officialValidationPassed": true',
        '"officialValidationPassed": false')
    Assert-TestCondition `
        ($tamperedAcceptanceText -cne $acceptanceText) `
        "package SBOM acceptance Boolean mutation was not applied."
    Write-TestText -Path $acceptancePath -Value $tamperedAcceptanceText
    Assert-AuditFailure `
        -Root $script:fixtureRoot `
        -EvidencePath (Join-Path $fixtureEvidenceRoot "tampered-sbom-acceptance.json") `
        -ExpectedMessage "M15TechnicalInvariant:PackageSbomAcceptanceInvalid" `
        -AllowBlockedInventory
    Copy-TestFile -RelativePath $acceptanceRelativePath

    $contractSourceRelativePath = ".github\workflows\windows-package-sbom.yml"
    $contractSourcePath = Join-Path $script:fixtureRoot $contractSourceRelativePath
    $contractSourceText = [System.IO.File]::ReadAllText($contractSourcePath)
    Write-TestText `
        -Path $contractSourcePath `
        -Value ($contractSourceText + "`n# package SBOM acceptance mutation")
    Assert-AuditFailure `
        -Root $script:fixtureRoot `
        -EvidencePath (Join-Path $fixtureEvidenceRoot "tampered-sbom-source-scope.json") `
        -ExpectedMessage "M15TechnicalInvariant:PackageSbomAcceptanceInvalid" `
        -AllowBlockedInventory
    Copy-TestFile -RelativePath $contractSourceRelativePath

    $vulnerabilityAcceptanceRelativePath =
        "eng\windows-package-vulnerability-acceptance.json"
    $vulnerabilityAcceptancePath = Join-Path `
        $script:fixtureRoot `
        $vulnerabilityAcceptanceRelativePath
    Remove-Item -LiteralPath $vulnerabilityAcceptancePath -Force
    Assert-AuditFailure `
        -Root $script:fixtureRoot `
        -EvidencePath (Join-Path $fixtureEvidenceRoot "missing-cve-acceptance.json") `
        -ExpectedMessage "M15TechnicalInvariant:PackageVulnerabilityAcceptanceInvalid" `
        -AllowBlockedInventory
    Copy-TestFile -RelativePath $vulnerabilityAcceptanceRelativePath

    $vulnerabilityAcceptanceText =
        [System.IO.File]::ReadAllText($vulnerabilityAcceptancePath)
    $duplicateVulnerabilityAcceptanceText = $vulnerabilityAcceptanceText.Replace(
        '  "decision": "AcceptTechnicalKnownVulnerabilityReview",',
        ('  "duplicateProbe": { "value": 1, "value": 2 },' + "`n" +
         '  "decision": "AcceptTechnicalKnownVulnerabilityReview",'))
    Assert-TestCondition `
        ($duplicateVulnerabilityAcceptanceText -cne $vulnerabilityAcceptanceText) `
        "nested duplicate package vulnerability acceptance mutation was not applied."
    Write-TestText `
        -Path $vulnerabilityAcceptancePath `
        -Value $duplicateVulnerabilityAcceptanceText
    Assert-AuditFailure `
        -Root $script:fixtureRoot `
        -EvidencePath (Join-Path $fixtureEvidenceRoot "duplicate-cve-acceptance.json") `
        -ExpectedMessage "M15TechnicalInvariant:PackageVulnerabilityAcceptanceInvalid" `
        -AllowBlockedInventory
    Copy-TestFile -RelativePath $vulnerabilityAcceptanceRelativePath

    $tamperedVulnerabilityAcceptanceText = $vulnerabilityAcceptanceText.Replace(
        '"knownVulnerabilityCount": 0',
        '"knownVulnerabilityCount": 1')
    Assert-TestCondition `
        ($tamperedVulnerabilityAcceptanceText -cne $vulnerabilityAcceptanceText) `
        "package vulnerability count mutation was not applied."
    Write-TestText `
        -Path $vulnerabilityAcceptancePath `
        -Value $tamperedVulnerabilityAcceptanceText
    $tamperedVulnerabilityAcceptanceSha256 = Get-TestFileSha256 `
        -Path $vulnerabilityAcceptancePath
    $semanticValidatorText = $validatorText.Replace(
        "a7f5e50f37337442d770b8d9a026dc5a9cd843d833c03af13b0689a0b69099e5",
        $tamperedVulnerabilityAcceptanceSha256)
    Assert-TestCondition ($semanticValidatorText -cne $validatorText) `
        "package vulnerability semantic validator mutation was not applied."
    $semanticValidatorPath = Join-Path `
        $script:fixtureRoot `
        "eng\Test-WindowsReleaseReadiness.semantic.ps1"
    Write-TestText -Path $semanticValidatorPath -Value $semanticValidatorText
    try {
        Assert-AuditFailure `
            -Root $script:fixtureRoot `
            -EvidencePath (Join-Path $fixtureEvidenceRoot "tampered-cve-acceptance.json") `
            -ExpectedMessage "M15TechnicalInvariant:PackageVulnerabilityAcceptanceInvalid" `
            -ValidatorPath $semanticValidatorPath `
            -AllowBlockedInventory
    }
    finally {
        Remove-Item -LiteralPath $semanticValidatorPath -Force -ErrorAction SilentlyContinue
    }
    Copy-TestFile -RelativePath $vulnerabilityAcceptanceRelativePath

    $vulnerabilityContractRelativePath =
        ".github\workflows\windows-cve-review.yml"
    $vulnerabilityContractPath = Join-Path `
        $script:fixtureRoot `
        $vulnerabilityContractRelativePath
    $vulnerabilityContractText =
        [System.IO.File]::ReadAllText($vulnerabilityContractPath)
    Write-TestText `
        -Path $vulnerabilityContractPath `
        -Value ($vulnerabilityContractText + "`n# package vulnerability acceptance mutation")
    Assert-AuditFailure `
        -Root $script:fixtureRoot `
        -EvidencePath (Join-Path $fixtureEvidenceRoot "tampered-cve-contract.json") `
        -ExpectedMessage "M15TechnicalInvariant:PackageVulnerabilityAcceptanceInvalid" `
        -AllowBlockedInventory
    Copy-TestFile -RelativePath $vulnerabilityContractRelativePath

    $vulnerabilityHelperRelativePath =
        "eng\WindowsPackageVulnerabilityAudit.ps1"
    $vulnerabilityHelperPath = Join-Path `
        $script:fixtureRoot `
        $vulnerabilityHelperRelativePath
    $vulnerabilityHelperText =
        [System.IO.File]::ReadAllText($vulnerabilityHelperPath)
    Write-TestText `
        -Path $vulnerabilityHelperPath `
        -Value ($vulnerabilityHelperText + "`n# captured helper mutation")
    Assert-AuditFailure `
        -Root $script:fixtureRoot `
        -EvidencePath (Join-Path $fixtureEvidenceRoot "tampered-cve-helper.json") `
        -ExpectedMessage "M15TechnicalInvariant:PackageVulnerabilityAcceptanceInvalid" `
        -AllowBlockedInventory
    Copy-TestFile -RelativePath $vulnerabilityHelperRelativePath

    $packageSourceRelativePath = "apps\windows\src\IptvSuite.Domain\AssemblyMarker.cs"
    $packageSourcePath = Join-Path $script:fixtureRoot $packageSourceRelativePath
    $packageSourceText = [System.IO.File]::ReadAllText($packageSourcePath)
    Write-TestText `
        -Path $packageSourcePath `
        -Value ($packageSourceText + "`n// package-producing snapshot mutation")
    Assert-AuditFailure `
        -Root $script:fixtureRoot `
        -EvidencePath (Join-Path $fixtureEvidenceRoot "tampered-package-source-snapshot.json") `
        -ExpectedMessage "M15TechnicalInvariant:PackageSbomAcceptanceInvalid" `
        -AllowBlockedInventory
    Copy-TestFile -RelativePath $packageSourceRelativePath

    $addedPackageSourcePath = Join-Path `
        $script:fixtureRoot `
        "apps\windows\src\IptvSuite.Windows\UnexpectedAcceptanceInput.xaml"
    Write-TestText -Path $addedPackageSourcePath -Value '<Page />'
    Assert-AuditFailure `
        -Root $script:fixtureRoot `
        -EvidencePath (Join-Path $fixtureEvidenceRoot "added-package-source-snapshot.json") `
        -ExpectedMessage "M15TechnicalInvariant:PackageSbomAcceptanceInvalid" `
        -AllowBlockedInventory
    Remove-Item -LiteralPath $addedPackageSourcePath -Force

    $removedPackageSourceRelativePath = `
        "apps\windows\src\IptvSuite.Application\AssemblyMarker.cs"
    $removedPackageSourcePath = Join-Path `
        $script:fixtureRoot `
        $removedPackageSourceRelativePath
    Remove-Item -LiteralPath $removedPackageSourcePath -Force
    Assert-AuditFailure `
        -Root $script:fixtureRoot `
        -EvidencePath (Join-Path $fixtureEvidenceRoot "removed-package-source-snapshot.json") `
        -ExpectedMessage "M15TechnicalInvariant:PackageSbomAcceptanceInvalid" `
        -AllowBlockedInventory
    Copy-TestFile -RelativePath $removedPackageSourceRelativePath

    $forbiddenNearestPackageOverrides = @(
        "apps\Directory.Packages.props",
        "apps\windows\Directory.Packages.props",
        "apps\windows\src\Directory.Packages.props",
        "apps\windows\src\IptvSuite.Application\Directory.Packages.props",
        "apps\windows\src\IptvSuite.Domain\Directory.Packages.props",
        "apps\windows\src\IptvSuite.Infrastructure\Directory.Packages.props",
        "apps\windows\src\IptvSuite.Windows\Directory.Packages.props")
    for ($overrideIndex = 0; $overrideIndex -lt $forbiddenNearestPackageOverrides.Count; $overrideIndex++) {
        $overrideRelativePath = $forbiddenNearestPackageOverrides[$overrideIndex]
        $overridePath = Join-Path $script:fixtureRoot $overrideRelativePath
        Write-TestText -Path $overridePath -Value '<Project />'
        Assert-AuditFailure `
            -Root $script:fixtureRoot `
            -EvidencePath (Join-Path `
                $fixtureEvidenceRoot `
                ("nearest-package-override-{0}.json" -f $overrideIndex)) `
            -ExpectedMessage "M15TechnicalInvariant:PackageSbomAcceptanceInvalid" `
            -AllowBlockedInventory
        Remove-Item -LiteralPath $overridePath -Force
    }

    Assert-AuditFailure `
        -Root $script:fixtureRoot `
        -EvidencePath (Join-Path $script:fixtureRoot "outside-artifacts.json") `
        -ExpectedMessage "M15TechnicalInvariant:EvidencePathOutsideRepository" `
        -AllowBlockedInventory
    Assert-AuditFailure `
        -Root $script:fixtureRoot `
        -EvidencePath ($fixtureEvidencePath + ":alternate") `
        -ExpectedMessage "M15TechnicalInvariant:EvidencePathAlternateDataStream" `
        -AllowBlockedInventory

    $manifestPath = Join-Path $script:fixtureRoot "apps\windows\src\IptvSuite.Windows\Package.appxmanifest"
    $originalManifest = [System.IO.File]::ReadAllText($manifestPath)
    $mutatedManifest = $originalManifest.Replace(
        '<rescap:Capability Name="runFullTrust" />',
        '<rescap:Capability Name="runFullTrust" /><rescap:Capability Name="internetClient" />')
    Assert-TestCondition ($mutatedManifest -cne $originalManifest) "capability mutation was not applied."
    Write-TestText -Path $manifestPath -Value $mutatedManifest
    Assert-AuditFailure `
        -Root $script:fixtureRoot `
        -EvidencePath (Join-Path $fixtureEvidenceRoot "extra-capability.json") `
        -ExpectedMessage "M15TechnicalInvariant:PackageCapabilitiesInvalid" `
        -AllowBlockedInventory
    Write-TestText -Path $manifestPath -Value $originalManifest

    $wrongNamespaceManifest = $originalManifest.Replace(
        '<rescap:Capability Name="runFullTrust" />',
        '<Capability Name="runFullTrust" />')
    Assert-TestCondition ($wrongNamespaceManifest -cne $originalManifest) "capability namespace mutation was not applied."
    Write-TestText -Path $manifestPath -Value $wrongNamespaceManifest
    Assert-AuditFailure `
        -Root $script:fixtureRoot `
        -EvidencePath (Join-Path $fixtureEvidenceRoot "wrong-capability-namespace.json") `
        -ExpectedMessage "M15TechnicalInvariant:PackageCapabilitiesInvalid" `
        -AllowBlockedInventory
    Write-TestText -Path $manifestPath -Value $originalManifest

    $storeAssociationPath = Join-Path `
        $script:fixtureRoot `
        "apps\windows\src\IptvSuite.Windows\Package.StoreAssociation.xml"
    Write-TestText -Path $storeAssociationPath -Value '<StoreAssociation />'
    Assert-AuditFailure `
        -Root $script:fixtureRoot `
        -EvidencePath (Join-Path $fixtureEvidenceRoot "store-association.json") `
        -ExpectedMessage "M15TechnicalInvariant:StoreAssociationUnexpected" `
        -AllowBlockedInventory
    Remove-Item -LiteralPath $storeAssociationPath -Force

    $windowsProjectPath = Join-Path `
        $script:fixtureRoot `
        "apps\windows\src\IptvSuite.Windows\IptvSuite.Windows.csproj"
    $originalWindowsProject = [System.IO.File]::ReadAllText($windowsProjectPath)
    $selfContainedProject = $originalWindowsProject.Replace(
        '<SelfContained>false</SelfContained>',
        '<SelfContained>true</SelfContained>')
    Assert-TestCondition ($selfContainedProject -cne $originalWindowsProject) "self-contained mutation was not applied."
    Write-TestText -Path $windowsProjectPath -Value $selfContainedProject
    Assert-AuditFailure `
        -Root $script:fixtureRoot `
        -EvidencePath (Join-Path $fixtureEvidenceRoot "self-contained.json") `
        -ExpectedMessage "M15TechnicalInvariant:WindowsProjectPropertyInvalid" `
        -AllowBlockedInventory

    $arm64Project = $originalWindowsProject.Replace(
        '<RuntimeIdentifier>win-x64</RuntimeIdentifier>',
        '<RuntimeIdentifier>win-arm64</RuntimeIdentifier>')
    Assert-TestCondition ($arm64Project -cne $originalWindowsProject) "ARM64 RID mutation was not applied."
    Write-TestText -Path $windowsProjectPath -Value $arm64Project
    Assert-AuditFailure `
        -Root $script:fixtureRoot `
        -EvidencePath (Join-Path $fixtureEvidenceRoot "arm64-rid.json") `
        -ExpectedMessage "M15TechnicalInvariant:WindowsProjectPropertyInvalid" `
        -AllowBlockedInventory

    $conditionalRidProject = $originalWindowsProject.Replace(
        '<RuntimeIdentifier>win-x64</RuntimeIdentifier>',
        '<RuntimeIdentifier Condition="''1'' == ''0''">win-x64</RuntimeIdentifier>')
    Assert-TestCondition `
        ($conditionalRidProject -cne $originalWindowsProject) `
        "conditional RID mutation was not applied."
    Write-TestText -Path $windowsProjectPath -Value $conditionalRidProject
    Assert-AuditFailure `
        -Root $script:fixtureRoot `
        -EvidencePath (Join-Path $fixtureEvidenceRoot "conditional-rid.json") `
        -ExpectedMessage "M15TechnicalInvariant:WindowsProjectPropertyInvalid" `
        -AllowBlockedInventory

    $arm64PlatformsProject = $originalWindowsProject.Replace(
        '<Platforms>x64</Platforms>',
        '<Platforms>x64;ARM64</Platforms>')
    Assert-TestCondition `
        ($arm64PlatformsProject -cne $originalWindowsProject) `
        "ARM64 Platforms mutation was not applied."
    Write-TestText -Path $windowsProjectPath -Value $arm64PlatformsProject
    Assert-AuditFailure `
        -Root $script:fixtureRoot `
        -EvidencePath (Join-Path $fixtureEvidenceRoot "arm64-platforms.json") `
        -ExpectedMessage "M15TechnicalInvariant:WindowsProjectPropertyInvalid" `
        -AllowBlockedInventory

    $bundleProject = $originalWindowsProject.Replace(
        '<AppxBundle>Never</AppxBundle>',
        '<AppxBundle>Always</AppxBundle>')
    Assert-TestCondition ($bundleProject -cne $originalWindowsProject) "bundle mutation was not applied."
    Write-TestText -Path $windowsProjectPath -Value $bundleProject
    Assert-AuditFailure `
        -Root $script:fixtureRoot `
        -EvidencePath (Join-Path $fixtureEvidenceRoot "appx-bundle.json") `
        -ExpectedMessage "M15TechnicalInvariant:WindowsProjectPropertyInvalid" `
        -AllowBlockedInventory

    $pluralRidProject = $originalWindowsProject.Replace(
        '<RuntimeIdentifier>win-x64</RuntimeIdentifier>',
        "<RuntimeIdentifier>win-x64</RuntimeIdentifier>`r`n    <RuntimeIdentifiers>win-x64;win-arm64</RuntimeIdentifiers>")
    Assert-TestCondition `
        ($pluralRidProject -cne $originalWindowsProject) `
        "plural RID mutation was not applied."
    Write-TestText -Path $windowsProjectPath -Value $pluralRidProject
    Assert-AuditFailure `
        -Root $script:fixtureRoot `
        -EvidencePath (Join-Path $fixtureEvidenceRoot "plural-rid.json") `
        -ExpectedMessage "M15TechnicalInvariant:WindowsProjectPropertyInvalid" `
        -AllowBlockedInventory

    $bundlePlatformsProject = $originalWindowsProject.Replace(
        '<AppxBundle>Never</AppxBundle>',
        "<AppxBundle>Never</AppxBundle>`r`n    <AppxBundlePlatforms>x64|ARM64</AppxBundlePlatforms>")
    Assert-TestCondition `
        ($bundlePlatformsProject -cne $originalWindowsProject) `
        "bundle-platforms mutation was not applied."
    Write-TestText -Path $windowsProjectPath -Value $bundlePlatformsProject
    Assert-AuditFailure `
        -Root $script:fixtureRoot `
        -EvidencePath (Join-Path $fixtureEvidenceRoot "bundle-platforms.json") `
        -ExpectedMessage "M15TechnicalInvariant:WindowsProjectPropertyInvalid" `
        -AllowBlockedInventory

    $explicitImportProject = $originalWindowsProject.Replace(
        '<Project Sdk="Microsoft.NET.Sdk">',
        "<Project Sdk=`"Microsoft.NET.Sdk`">`r`n  <Import Project=`"architecture.targets`" />")
    Assert-TestCondition `
        ($explicitImportProject -cne $originalWindowsProject) `
        "explicit import mutation was not applied."
    Write-TestText -Path $windowsProjectPath -Value $explicitImportProject
    Assert-AuditFailure `
        -Root $script:fixtureRoot `
        -EvidencePath (Join-Path $fixtureEvidenceRoot "explicit-import.json") `
        -ExpectedMessage "M15TechnicalInvariant:MsBuildArchitectureImportSurfaceInvalid" `
        -AllowBlockedInventory
    Write-TestText -Path $windowsProjectPath -Value $originalWindowsProject

    $windowsProjectUserPath = $windowsProjectPath + ".user"
    Write-TestText `
        -Path $windowsProjectUserPath `
        -Value '<Project><PropertyGroup><RuntimeIdentifier>win-arm64</RuntimeIdentifier></PropertyGroup></Project>'
    Assert-AuditFailure `
        -Root $script:fixtureRoot `
        -EvidencePath (Join-Path $fixtureEvidenceRoot "project-user-import.json") `
        -ExpectedMessage "M15TechnicalInvariant:MsBuildArchitectureImportSurfaceInvalid" `
        -AllowBlockedInventory
    Remove-Item -LiteralPath $windowsProjectUserPath -Force

    $projectExtensionAttackPath = Join-Path `
        ([System.IO.Path]::GetDirectoryName($windowsProjectPath)) `
        "obj\IptvSuite.Windows.csproj.attack.targets"
    Write-TestText `
        -Path $projectExtensionAttackPath `
        -Value '<Project><PropertyGroup><RuntimeIdentifier>win-arm64</RuntimeIdentifier></PropertyGroup></Project>'
    Assert-AuditFailure `
        -Root $script:fixtureRoot `
        -EvidencePath (Join-Path $fixtureEvidenceRoot "project-extension-import.json") `
        -ExpectedMessage "M15TechnicalInvariant:MsBuildArchitectureImportSurfaceInvalid" `
        -AllowBlockedInventory
    Remove-Item -LiteralPath $projectExtensionAttackPath -Force

    $directoryBuildTargetsPath = Join-Path $script:fixtureRoot "Directory.Build.targets"
    Write-TestText `
        -Path $directoryBuildTargetsPath `
        -Value '<Project><PropertyGroup><RuntimeIdentifier>win-arm64</RuntimeIdentifier></PropertyGroup></Project>'
    Assert-AuditFailure `
        -Root $script:fixtureRoot `
        -EvidencePath (Join-Path $fixtureEvidenceRoot "directory-build-targets.json") `
        -ExpectedMessage "M15TechnicalInvariant:MsBuildArchitectureImportSurfaceInvalid" `
        -AllowBlockedInventory
    Remove-Item -LiteralPath $directoryBuildTargetsPath -Force

    $nestedDirectoryBuildPropsPath = Join-Path `
        $script:fixtureRoot `
        "apps\windows\src\IptvSuite.Windows\Directory.Build.props"
    Write-TestText -Path $nestedDirectoryBuildPropsPath -Value '<Project />'
    Assert-AuditFailure `
        -Root $script:fixtureRoot `
        -EvidencePath (Join-Path $fixtureEvidenceRoot "nested-directory-build-props.json") `
        -ExpectedMessage "M15TechnicalInvariant:MsBuildArchitectureImportSurfaceInvalid" `
        -AllowBlockedInventory
    Remove-Item -LiteralPath $nestedDirectoryBuildPropsPath -Force

    $directoryBuildResponsePath = Join-Path $script:fixtureRoot "Directory.Build.rsp"
    Write-TestText `
        -Path $directoryBuildResponsePath `
        -Value '-p:RuntimeIdentifier=win-arm64'
    Assert-AuditFailure `
        -Root $script:fixtureRoot `
        -EvidencePath (Join-Path $fixtureEvidenceRoot "directory-build-response.json") `
        -ExpectedMessage "M15TechnicalInvariant:MsBuildArchitectureImportSurfaceInvalid" `
        -AllowBlockedInventory
    Remove-Item -LiteralPath $directoryBuildResponsePath -Force

    $nestedDirectorySolutionPropsPath = Join-Path `
        $script:fixtureRoot `
        "apps\windows\Directory.Solution.props"
    Write-TestText `
        -Path $nestedDirectorySolutionPropsPath `
        -Value '<Project><PropertyGroup><Platform>ARM64</Platform></PropertyGroup></Project>'
    Assert-AuditFailure `
        -Root $script:fixtureRoot `
        -EvidencePath (Join-Path $fixtureEvidenceRoot "nested-directory-solution-props.json") `
        -ExpectedMessage "M15TechnicalInvariant:MsBuildArchitectureImportSurfaceInvalid" `
        -AllowBlockedInventory
    Remove-Item -LiteralPath $nestedDirectorySolutionPropsPath -Force

    $directoryBuildPropsPath = Join-Path $script:fixtureRoot "Directory.Build.props"
    $originalDirectoryBuildProps = [System.IO.File]::ReadAllText($directoryBuildPropsPath)
    $architectureOverrideProps = $originalDirectoryBuildProps.Replace(
        '<LangVersion>14.0</LangVersion>',
        "<LangVersion>14.0</LangVersion>`r`n    <AppxBundlePlatforms>x64|ARM64</AppxBundlePlatforms>")
    Assert-TestCondition `
        ($architectureOverrideProps -cne $originalDirectoryBuildProps) `
        "shared architecture override mutation was not applied."
    Write-TestText -Path $directoryBuildPropsPath -Value $architectureOverrideProps
    Assert-AuditFailure `
        -Root $script:fixtureRoot `
        -EvidencePath (Join-Path $fixtureEvidenceRoot "shared-architecture-override.json") `
        -ExpectedMessage "M15TechnicalInvariant:MsBuildArchitectureImportSurfaceInvalid" `
        -AllowBlockedInventory
    Write-TestText -Path $directoryBuildPropsPath -Value $originalDirectoryBuildProps

    $architectureHookTargetPath = Join-Path $script:fixtureRoot "architecture-hook.targets"
    Write-TestText `
        -Path $architectureHookTargetPath `
        -Value '<Project><PropertyGroup><RuntimeIdentifier>win-arm64</RuntimeIdentifier></PropertyGroup></Project>'
    $architectureHookProps = $originalDirectoryBuildProps.Replace(
        '<LangVersion>14.0</LangVersion>',
        "<LangVersion>14.0</LangVersion>`r`n    <CustomAfterDirectoryBuildTargets>architecture-hook.targets</CustomAfterDirectoryBuildTargets>")
    Assert-TestCondition `
        ($architectureHookProps -cne $originalDirectoryBuildProps) `
        "shared architecture hook mutation was not applied."
    Write-TestText -Path $directoryBuildPropsPath -Value $architectureHookProps
    Assert-AuditFailure `
        -Root $script:fixtureRoot `
        -EvidencePath (Join-Path $fixtureEvidenceRoot "shared-architecture-hook.json") `
        -ExpectedMessage "M15TechnicalInvariant:MsBuildArchitectureImportSurfaceInvalid" `
        -AllowBlockedInventory
    Write-TestText -Path $directoryBuildPropsPath -Value $originalDirectoryBuildProps
    Remove-Item -LiteralPath $architectureHookTargetPath -Force

    $redirectedExtensionPath = Join-Path `
        ([System.IO.Path]::GetDirectoryName($windowsProjectPath)) `
        "architecture-hooks\obj\IptvSuite.Windows.csproj.attack.targets"
    Write-TestText `
        -Path $redirectedExtensionPath `
        -Value '<Project><PropertyGroup><RuntimeIdentifier>win-arm64</RuntimeIdentifier></PropertyGroup></Project>'
    $redirectedExtensionProps = $originalDirectoryBuildProps.Replace(
        '<LangVersion>14.0</LangVersion>',
        "<LangVersion>14.0</LangVersion>`r`n    <ArtifactsPath>architecture-hooks</ArtifactsPath>`r`n    <UseArtifactsOutput>true</UseArtifactsOutput>")
    Assert-TestCondition `
        ($redirectedExtensionProps -cne $originalDirectoryBuildProps) `
        "redirected project-extension mutation was not applied."
    Write-TestText -Path $directoryBuildPropsPath -Value $redirectedExtensionProps
    Assert-AuditFailure `
        -Root $script:fixtureRoot `
        -EvidencePath (Join-Path $fixtureEvidenceRoot "redirected-project-extension.json") `
        -ExpectedMessage "M15TechnicalInvariant:MsBuildArchitectureImportSurfaceInvalid" `
        -AllowBlockedInventory
    Write-TestText -Path $directoryBuildPropsPath -Value $originalDirectoryBuildProps
    Remove-Item `
        -LiteralPath (Join-Path `
            ([System.IO.Path]::GetDirectoryName($windowsProjectPath)) `
            "architecture-hooks") `
        -Recurse `
        -Force

    $assetRelativePath = "apps\windows\src\IptvSuite.Windows\Assets\StoreLogo.png"
    $assetPath = Join-Path $script:fixtureRoot $assetRelativePath
    Remove-Item -LiteralPath $assetPath -Force
    Assert-AuditFailure `
        -Root $script:fixtureRoot `
        -EvidencePath (Join-Path $fixtureEvidenceRoot "missing-asset.json") `
        -ExpectedMessage "M15TechnicalInvariant:AssetFileInvalid" `
        -AllowBlockedInventory
    Copy-TestFile -RelativePath $assetRelativePath

    $lockedAsset = [System.IO.File]::Open(
        $assetPath,
        [System.IO.FileMode]::Open,
        [System.IO.FileAccess]::Read,
        [System.IO.FileShare]::None)
    try {
        Assert-AuditFailure `
            -Root $script:fixtureRoot `
            -EvidencePath (Join-Path $fixtureEvidenceRoot "locked-asset.json") `
            -ExpectedMessage "M15TechnicalInvariant:RepositoryFileHashInvalid" `
            -AllowBlockedInventory
    }
    finally {
        $lockedAsset.Dispose()
    }

    $lockPath = Join-Path `
        $script:fixtureRoot `
        "apps\windows\src\IptvSuite.Windows\packages.lock.json"
    $originalLock = [System.IO.File]::ReadAllText($lockPath)
    $lock = $originalLock | ConvertFrom-Json
    $target = @($lock.dependencies.PSObject.Properties)[0].Value
    $fakeContentHash = [Convert]::ToBase64String((New-Object byte[] 64))
    $target | Add-Member `
        -NotePropertyName "LibVLCSharp" `
        -NotePropertyValue ([pscustomobject]@{
            type = "Transitive"
            resolved = "3.10.0"
            contentHash = $fakeContentHash
        })
    Write-TestText -Path $lockPath -Value ($lock | ConvertTo-Json -Depth 20)
    Assert-AuditFailure `
        -Root $script:fixtureRoot `
        -EvidencePath (Join-Path $fixtureEvidenceRoot "libvlc-lock.json") `
        -ExpectedMessage "M15TechnicalInvariant:ProductionPackageNameInventoryChanged" `
        -AllowBlockedInventory
    Write-TestText -Path $lockPath -Value $originalLock

    $catalogFactoryPath = Join-Path `
        $script:fixtureRoot `
        "apps\windows\src\IptvSuite.Windows\WindowsCatalogBrowserFactory.cs"
    $originalCatalogFactory = [System.IO.File]::ReadAllText($catalogFactoryPath)
    Write-TestText `
        -Path $catalogFactoryPath `
        -Value ($originalCatalogFactory + [Environment]::NewLine +
            'internal static class InstallRootMutation { internal static void Write() => Directory.SetCurrentDirectory("mutation"); }')
    Assert-AuditFailure `
        -Root $script:fixtureRoot `
        -EvidencePath (Join-Path $fixtureEvidenceRoot "install-root-write.json") `
        -ExpectedMessage "M15TechnicalInvariant:InstallRootDiscoveryPatternDetected" `
        -AllowBlockedInventory
    Write-TestText -Path $catalogFactoryPath -Value $originalCatalogFactory

    Write-TestText `
        -Path $catalogFactoryPath `
        -Value ($originalCatalogFactory + [Environment]::NewLine +
            'internal static class AlternateInstallRootMutation { internal static void Write() { string root = Package.Current.InstalledLocation.Path; File.WriteAllText(Path.Combine(root, "mutation"), "x"); } }')
    Assert-AuditFailure `
        -Root $script:fixtureRoot `
        -EvidencePath (Join-Path $fixtureEvidenceRoot "alternate-install-root-write.json") `
        -ExpectedMessage "M15TechnicalInvariant:InstallRootDiscoveryPatternDetected" `
        -AllowBlockedInventory
    Write-TestText -Path $catalogFactoryPath -Value $originalCatalogFactory

    $restoredEvidencePath = Join-Path $fixtureEvidenceRoot "restored.json"
    Invoke-AllowedAudit -Root $script:fixtureRoot -EvidencePath $restoredEvidencePath
    Read-AndAssertEvidence `
        -EvidencePath $restoredEvidencePath `
        -ForbiddenRoot $script:fixtureRoot | Out-Null

    Write-Output "M15 Windows release-readiness self-test passed."
}
finally {
    if (Test-Path -LiteralPath $script:actualEvidenceRoot) {
        Remove-Item -LiteralPath $script:actualEvidenceRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
    if (Test-Path -LiteralPath $script:fixtureRoot) {
        Remove-Item -LiteralPath $script:fixtureRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
}
