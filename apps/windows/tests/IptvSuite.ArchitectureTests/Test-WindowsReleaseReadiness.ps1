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
        [string]$EvidencePath
    )

    & $script:validatorPath `
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

        [switch]$AllowBlockedInventory
    )

    $actualMessage = $null
    try {
        if ($AllowBlockedInventory) {
            & $script:validatorPath `
                -RepositoryRoot $Root `
                -EvidencePath $EvidencePath `
                -AllowBlockedInventory | Out-Null
        }
        else {
            & $script:validatorPath `
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
        [string]$ForbiddenRoot
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
            "lockfiles",
            "packageInventory",
            "packageInventoryPolicy",
            "blockers") `
        -Message "evidence root schema changed."
    Assert-TestCondition ($evidence.schemaVersion -eq 1) "schemaVersion must be 1."
    Assert-TestCondition ($evidence.result -ceq "blocked") "result must remain blocked."
    Assert-TestCondition `
        ($evidence.technicalBaselinePassed -is [bool] -and $evidence.technicalBaselinePassed) `
        "technicalBaselinePassed must be exact Boolean true."
    Assert-TestCondition `
        ($evidence.releaseReady -is [bool] -and -not $evidence.releaseReady) `
        "releaseReady must be exact Boolean false."
    Assert-TestCondition (@($evidence.assets).Count -eq 8) "the exact eight production assets were not inventoried."
    Assert-TestCondition (@($evidence.lockfiles).Count -eq 4) "the exact four production lockfiles were not inventoried."
    Assert-TestCondition (@($evidence.packageInventory).Count -eq 23) "the exact production package inventory changed."
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

    $expectedBlockers = @(
        "Arm64ReleaseDecisionPending",
        "AssetProvenancePending",
        "CodecIpLegalReviewPending",
        "CveReviewPending",
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
        "global.json",
        "apps\windows\src\IptvSuite.Domain\IptvSuite.Domain.csproj",
        "apps\windows\src\IptvSuite.Domain\packages.lock.json",
        "apps\windows\src\IptvSuite.Application\IptvSuite.Application.csproj",
        "apps\windows\src\IptvSuite.Application\packages.lock.json",
        "apps\windows\src\IptvSuite.Infrastructure\IptvSuite.Infrastructure.csproj",
        "apps\windows\src\IptvSuite.Infrastructure\packages.lock.json",
        "apps\windows\src\IptvSuite.Windows\IptvSuite.Windows.csproj",
        "apps\windows\src\IptvSuite.Windows\packages.lock.json",
        "apps\windows\src\IptvSuite.Windows\Package.appxmanifest",
        "apps\windows\src\IptvSuite.Windows\app.manifest",
        "apps\windows\src\IptvSuite.Windows\WindowsSecretStoreFactory.cs",
        "apps\windows\src\IptvSuite.Windows\WindowsCatalogBrowserFactory.cs",
        "apps\windows\src\IptvSuite.Windows\MainWindow.xaml.cs",
        "apps\windows\src\IptvSuite.Windows\Assets\AppIcon.ico",
        "apps\windows\src\IptvSuite.Windows\Assets\SplashScreen.scale-200.png",
        "apps\windows\src\IptvSuite.Windows\Assets\Square150x150Logo.scale-200.png",
        "apps\windows\src\IptvSuite.Windows\Assets\Square44x44Logo.scale-200.png",
        "apps\windows\src\IptvSuite.Windows\Assets\Square44x44Logo.targetsize-24_altform-unplated.png",
        "apps\windows\src\IptvSuite.Windows\Assets\Square44x44Logo.targetsize-48_altform-lightunplated.png",
        "apps\windows\src\IptvSuite.Windows\Assets\StoreLogo.png",
        "apps\windows\src\IptvSuite.Windows\Assets\Wide310x150Logo.scale-200.png")
    foreach ($relativePath in $requiredFiles) {
        Copy-TestFile -RelativePath $relativePath
    }
}

Assert-TestCondition (Test-Path -LiteralPath $script:validatorPath -PathType Leaf) "validator script is missing."

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
    Write-TestText -Path $windowsProjectPath -Value $originalWindowsProject

    $assetRelativePath = "apps\windows\src\IptvSuite.Windows\Assets\StoreLogo.png"
    $assetPath = Join-Path $script:fixtureRoot $assetRelativePath
    Remove-Item -LiteralPath $assetPath -Force
    Assert-AuditFailure `
        -Root $script:fixtureRoot `
        -EvidencePath (Join-Path $fixtureEvidenceRoot "missing-asset.json") `
        -ExpectedMessage "M15TechnicalInvariant:AssetFileInvalid" `
        -AllowBlockedInventory
    Copy-TestFile -RelativePath $assetRelativePath

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
