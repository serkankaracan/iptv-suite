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
    Assert-TestCondition ($evidence.schemaVersion -eq 2) "schemaVersion must be 2."
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

    $expectedBlockers = @(
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
        "Directory.Build.props",
        "Directory.Packages.props",
        "Directory.Solution.props",
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
