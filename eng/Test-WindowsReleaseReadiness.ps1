[CmdletBinding()]
param(
    [switch]$AllowBlockedInventory,

    [string]$RepositoryRoot,

    [string]$EvidencePath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$script:maximumEvidenceBytes = 1MB
$script:maximumSourceFileBytes = 2MB
$script:technicalStage = "Initialization"
$script:utf8Strict = New-Object System.Text.UTF8Encoding($false, $true)
$script:utf8NoBom = New-Object System.Text.UTF8Encoding($false)

function Fail-TechnicalInvariant {
    param(
        [Parameter(Mandatory = $true)]
        [ValidatePattern('^[A-Za-z][A-Za-z0-9]+$')]
        [string]$Code
    )

    throw "M15TechnicalInvariant:$Code"
}

function Assert-Condition {
    param(
        [Parameter(Mandatory = $true)]
        [bool]$Condition,

        [Parameter(Mandatory = $true)]
        [string]$Code
    )

    if (-not $Condition) {
        Fail-TechnicalInvariant -Code $Code
    }
}

function Test-PathContainedByRoot {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path,

        [Parameter(Mandatory = $true)]
        [string]$Root
    )

    $rootWithSeparator = $Root.TrimEnd(
        [System.IO.Path]::DirectorySeparatorChar,
        [System.IO.Path]::AltDirectorySeparatorChar) +
        [System.IO.Path]::DirectorySeparatorChar

    return $Path.StartsWith(
        $rootWithSeparator,
        [System.StringComparison]::OrdinalIgnoreCase)
}

function Resolve-RegularRepositoryFile {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Root,

        [Parameter(Mandatory = $true)]
        [string]$RelativePath,

        [long]$MaximumBytes = $script:maximumSourceFileBytes,

        [string]$Code = "RepositoryFileInvalid"
    )

    Assert-Condition `
        (-not [System.IO.Path]::IsPathRooted($RelativePath)) `
        $Code

    $candidate = [System.IO.Path]::GetFullPath((Join-Path $Root $RelativePath))
    Assert-Condition (Test-PathContainedByRoot -Path $candidate -Root $Root) $Code
    Assert-NoReparseDirectoryChain `
        -Root $Root `
        -DirectoryPath ([System.IO.Path]::GetDirectoryName($candidate))
    Assert-Condition (Test-Path -LiteralPath $candidate -PathType Leaf) $Code

    $item = Get-Item -LiteralPath $candidate -Force
    Assert-Condition `
        (($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -eq 0) `
        $Code
    Assert-Condition ($item.Length -gt 0 -and $item.Length -le $MaximumBytes) $Code
    return $item
}

function Read-StrictUtf8Text {
    param(
        [Parameter(Mandatory = $true)]
        [System.IO.FileInfo]$File,

        [string]$Code = "RepositoryTextInvalid"
    )

    try {
        $stream = [System.IO.File]::Open(
            $File.FullName,
            [System.IO.FileMode]::Open,
            [System.IO.FileAccess]::Read,
            [System.IO.FileShare]::Read)
        try {
            $reader = New-Object System.IO.StreamReader(
                $stream,
                $script:utf8Strict,
                $false,
                4096,
                $true)
            try {
                return $reader.ReadToEnd()
            }
            finally {
                $reader.Dispose()
            }
        }
        finally {
            $stream.Dispose()
        }
    }
    catch {
        Fail-TechnicalInvariant -Code $Code
    }
}

function Read-SafeXml {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Text,

        [string]$Code = "XmlInvalid"
    )

    try {
        $settings = New-Object System.Xml.XmlReaderSettings
        $settings.DtdProcessing = [System.Xml.DtdProcessing]::Prohibit
        $settings.XmlResolver = $null
        $settings.MaxCharactersInDocument = $script:maximumSourceFileBytes

        $stringReader = New-Object System.IO.StringReader($Text)
        try {
            $reader = [System.Xml.XmlReader]::Create($stringReader, $settings)
            try {
                $document = New-Object System.Xml.XmlDocument
                $document.XmlResolver = $null
                $document.Load($reader)
                return $document
            }
            finally {
                $reader.Dispose()
            }
        }
        finally {
            $stringReader.Dispose()
        }
    }
    catch {
        Fail-TechnicalInvariant -Code $Code
    }
}

function Get-SingleNode {
    param(
        [Parameter(Mandatory = $true)]
        [System.Xml.XmlNode]$Node,

        [Parameter(Mandatory = $true)]
        [string]$XPath,

        [Parameter(Mandatory = $true)]
        [string]$Code
    )

    $matches = @($Node.SelectNodes($XPath))
    Assert-Condition ($matches.Count -eq 1) $Code
    return $matches[0]
}

function Get-SingleProjectProperty {
    param(
        [Parameter(Mandatory = $true)]
        [System.Xml.XmlDocument]$Project,

        [Parameter(Mandatory = $true)]
        [string]$Name,

        [Parameter(Mandatory = $true)]
        [string]$Code
    )

    $matches = @($Project.SelectNodes(
        "/*[local-name()='Project']/*[local-name()='PropertyGroup']/*[local-name()='$Name']"))
    Assert-Condition ($matches.Count -eq 1) $Code
    return $matches[0].InnerText.Trim()
}

function Get-OrdinalSortedStrings {
    param(
        [AllowEmptyCollection()]
        [string[]]$Values
    )

    if ($null -eq $Values -or $Values.Count -eq 0) {
        return @()
    }

    $result = [string[]]@($Values)
    [System.Array]::Sort($result, [System.StringComparer]::Ordinal)
    return $result
}

function Assert-ExactStringSet {
    param(
        [AllowEmptyCollection()]
        [string[]]$Actual,

        [AllowEmptyCollection()]
        [string[]]$Expected,

        [Parameter(Mandatory = $true)]
        [string]$Code
    )

    $actualSorted = @(Get-OrdinalSortedStrings -Values $Actual)
    $expectedSorted = @(Get-OrdinalSortedStrings -Values $Expected)
    Assert-Condition ($actualSorted.Count -eq $expectedSorted.Count) $Code

    for ($index = 0; $index -lt $expectedSorted.Count; $index++) {
        Assert-Condition ($actualSorted[$index] -ceq $expectedSorted[$index]) $Code
    }
}

function Get-RelativeEvidencePath {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Root,

        [Parameter(Mandatory = $true)]
        [string]$FullPath
    )

    Assert-Condition (Test-PathContainedByRoot -Path $FullPath -Root $Root) "RelativePathInvalid"
    $rootWithSeparator = $Root.TrimEnd(
        [System.IO.Path]::DirectorySeparatorChar,
        [System.IO.Path]::AltDirectorySeparatorChar) +
        [System.IO.Path]::DirectorySeparatorChar
    return $FullPath.Substring($rootWithSeparator.Length).Replace('\', '/')
}

function Get-LowerSha256 {
    param(
        [Parameter(Mandatory = $true)]
        [System.IO.FileInfo]$File
    )

    $maximumAttempts = 3
    for ($attempt = 1; $attempt -le $maximumAttempts; $attempt++) {
        try {
            $File.Refresh()
            if (-not $File.Exists -or
                ($File.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0 -or
                $File.Length -le 0) {
                Fail-TechnicalInvariant -Code "RepositoryFileHashInvalid"
            }

            $beforeLength = [long]$File.Length
            $beforeLastWriteTicks = [long]$File.LastWriteTimeUtc.Ticks
            $stream = [System.IO.File]::Open(
                $File.FullName,
                [System.IO.FileMode]::Open,
                [System.IO.FileAccess]::Read,
                [System.IO.FileShare]::Read)
            try {
                $sha256 = [System.Security.Cryptography.SHA256]::Create()
                try {
                    $hash = $sha256.ComputeHash($stream)
                }
                finally {
                    $sha256.Dispose()
                }
            }
            finally {
                $stream.Dispose()
            }

            $File.Refresh()
            if (-not $File.Exists -or
                ($File.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0 -or
                $File.Length -ne $beforeLength -or
                $File.LastWriteTimeUtc.Ticks -ne $beforeLastWriteTicks) {
                Fail-TechnicalInvariant -Code "RepositoryFileChangedDuringHash"
            }

            return ([System.BitConverter]::ToString($hash)).Replace("-", "").ToLowerInvariant()
        }
        catch {
            if ($_.Exception.Message -match '^M15TechnicalInvariant:[A-Za-z][A-Za-z0-9]+$') {
                throw $_.Exception.Message
            }

            $nativeCode = $_.Exception.HResult -band 0xffff
            $isTransientFileLock =
                $_.Exception -is [System.IO.IOException] -and
                ($nativeCode -eq 32 -or $nativeCode -eq 33)
            if ($isTransientFileLock -and $attempt -lt $maximumAttempts) {
                [System.Threading.Thread]::Sleep(50 * $attempt)
                continue
            }

            Fail-TechnicalInvariant -Code "RepositoryFileHashInvalid"
        }
    }

    Fail-TechnicalInvariant -Code "RepositoryFileHashInvalid"
}

function Assert-NoReparseDirectoryChain {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Root,

        [Parameter(Mandatory = $true)]
        [string]$DirectoryPath
    )

    Assert-Condition (Test-PathContainedByRoot -Path $DirectoryPath -Root $Root) "EvidencePathOutsideRepository"
    $rootWithSeparator = $Root.TrimEnd(
        [System.IO.Path]::DirectorySeparatorChar,
        [System.IO.Path]::AltDirectorySeparatorChar) +
        [System.IO.Path]::DirectorySeparatorChar
    $relative = $DirectoryPath.Substring($rootWithSeparator.Length)
    $current = $Root
    foreach ($part in @($relative.Split(@('\', '/'), [System.StringSplitOptions]::RemoveEmptyEntries))) {
        $current = Join-Path $current $part
        if (Test-Path -LiteralPath $current) {
            $item = Get-Item -LiteralPath $current -Force
            Assert-Condition $item.PSIsContainer "EvidenceDirectoryInvalid"
            Assert-Condition `
                (($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -eq 0) `
                "EvidenceDirectoryReparsePoint"
        }
    }
}

function Publish-BoundedEvidence {
    param(
        [Parameter(Mandatory = $true)]
        [System.Collections.IDictionary]$Value,

        [Parameter(Mandatory = $true)]
        [string]$DestinationPath,

        [Parameter(Mandatory = $true)]
        [string]$Root
    )

    $json = $Value | ConvertTo-Json -Depth 12
    $bytes = $script:utf8NoBom.GetBytes($json)
    Assert-Condition `
        ($bytes.Length -gt 0 -and $bytes.Length -le $script:maximumEvidenceBytes) `
        "EvidenceSizeInvalid"

    $parent = [System.IO.Path]::GetDirectoryName($DestinationPath)
    Assert-Condition (-not [string]::IsNullOrWhiteSpace($parent)) "EvidenceDirectoryInvalid"
    Assert-NoReparseDirectoryChain -Root $Root -DirectoryPath $parent
    [System.IO.Directory]::CreateDirectory($parent) | Out-Null
    Assert-NoReparseDirectoryChain -Root $Root -DirectoryPath $parent

    if (Test-Path -LiteralPath $DestinationPath) {
        $existing = Get-Item -LiteralPath $DestinationPath -Force
        Assert-Condition (-not $existing.PSIsContainer) "EvidenceDestinationInvalid"
        Assert-Condition `
                (($existing.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -eq 0) `
                "EvidenceDestinationReparsePoint"
        Assert-Condition `
            ($existing.Length -gt 0 -and $existing.Length -le $script:maximumEvidenceBytes) `
            "EvidenceDestinationSizeInvalid"
    }

    $temporaryPath = Join-Path $parent (".readiness-summary.{0}.tmp" -f [Guid]::NewGuid().ToString("N"))
    $backupPath = Join-Path $parent (".readiness-summary.{0}.backup" -f [Guid]::NewGuid().ToString("N"))
    try {
        $stream = New-Object System.IO.FileStream(
            $temporaryPath,
            [System.IO.FileMode]::CreateNew,
            [System.IO.FileAccess]::Write,
            [System.IO.FileShare]::None)
        try {
            $stream.Write($bytes, 0, $bytes.Length)
            $stream.Flush($true)
        }
        finally {
            $stream.Dispose()
        }

        if (Test-Path -LiteralPath $DestinationPath) {
            [System.IO.File]::Replace(
                $temporaryPath,
                $DestinationPath,
                $backupPath,
                $true)
            [System.IO.File]::Delete($backupPath)
        }
        else {
            [System.IO.File]::Move($temporaryPath, $DestinationPath)
        }
    }
    finally {
        if (Test-Path -LiteralPath $temporaryPath) {
            Remove-Item -LiteralPath $temporaryPath -Force -ErrorAction SilentlyContinue
        }
        if (Test-Path -LiteralPath $backupPath) {
            Remove-Item -LiteralPath $backupPath -Force -ErrorAction SilentlyContinue
        }
    }
}

function Get-CleanRepositoryCommit {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Root
    )

    try {
        $topLevel = (& git -C $Root rev-parse --show-toplevel 2>$null | Out-String).Trim()
        if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($topLevel)) {
            return $null
        }

        $resolvedTopLevel = [System.IO.Path]::GetFullPath($topLevel)
        if (-not $resolvedTopLevel.Equals($Root, [System.StringComparison]::OrdinalIgnoreCase)) {
            return $null
        }

        $status = (& git -C $Root status --porcelain=v1 --untracked-files=normal 2>$null | Out-String)
        if ($LASTEXITCODE -ne 0 -or -not [string]::IsNullOrWhiteSpace($status)) {
            return $null
        }

        $commit = (& git -C $Root rev-parse HEAD 2>$null | Out-String).Trim().ToLowerInvariant()
        if ($LASTEXITCODE -ne 0 -or $commit -notmatch '^[0-9a-f]{40}$') {
            return $null
        }

        return $commit
    }
    catch {
        return $null
    }
}

try {
    $script:technicalStage = "RepositoryBinding"
    if ([string]::IsNullOrWhiteSpace($RepositoryRoot)) {
        $RepositoryRoot = Join-Path $PSScriptRoot ".."
    }

    $resolvedRepositoryRoot = [System.IO.Path]::GetFullPath($RepositoryRoot)
    Assert-Condition (Test-Path -LiteralPath $resolvedRepositoryRoot -PathType Container) "RepositoryRootInvalid"
    $repositoryItem = Get-Item -LiteralPath $resolvedRepositoryRoot -Force
    Assert-Condition `
        (($repositoryItem.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -eq 0) `
        "RepositoryRootReparsePoint"
    foreach ($marker in @("global.json", "apps\windows\src", "eng")) {
        Assert-Condition (Test-Path -LiteralPath (Join-Path $resolvedRepositoryRoot $marker)) "RepositoryLayoutInvalid"
    }

    if ([string]::IsNullOrWhiteSpace($EvidencePath)) {
        $EvidencePath = Join-Path $resolvedRepositoryRoot ".artifacts\m15-release-readiness\readiness-summary.json"
    }
    elseif (-not [System.IO.Path]::IsPathRooted($EvidencePath)) {
        $EvidencePath = Join-Path $resolvedRepositoryRoot $EvidencePath
    }

    $evidencePathRoot = [System.IO.Path]::GetPathRoot($EvidencePath)
    $evidencePathTail = $EvidencePath.Substring($evidencePathRoot.Length)
    Assert-Condition `
        ($evidencePathTail.IndexOf(':') -lt 0) `
        "EvidencePathAlternateDataStream"
    $resolvedEvidencePath = [System.IO.Path]::GetFullPath($EvidencePath)
    $releaseReadinessArtifactRoot = [System.IO.Path]::GetFullPath(
        (Join-Path $resolvedRepositoryRoot ".artifacts"))
    Assert-Condition `
        (Test-PathContainedByRoot `
            -Path $resolvedEvidencePath `
            -Root $releaseReadinessArtifactRoot) `
        "EvidencePathOutsideRepository"
    $artifactRootWithSeparator = $releaseReadinessArtifactRoot.TrimEnd(
        [System.IO.Path]::DirectorySeparatorChar,
        [System.IO.Path]::AltDirectorySeparatorChar) +
        [System.IO.Path]::DirectorySeparatorChar
    $relativeEvidencePath = $resolvedEvidencePath.Substring($artifactRootWithSeparator.Length)
    Assert-Condition ($relativeEvidencePath.IndexOf(':') -lt 0) "EvidencePathAlternateDataStream"

    $script:technicalStage = "PackageManifest"
    $manifestFile = Resolve-RegularRepositoryFile `
        -Root $resolvedRepositoryRoot `
        -RelativePath "apps\windows\src\IptvSuite.Windows\Package.appxmanifest" `
        -Code "PackageManifestFileInvalid"
    $manifest = Read-SafeXml `
        -Text (Read-StrictUtf8Text -File $manifestFile -Code "PackageManifestEncodingInvalid") `
        -Code "PackageManifestXmlInvalid"

    $identity = Get-SingleNode `
        -Node $manifest `
        -XPath "/*[local-name()='Package']/*[local-name()='Identity']" `
        -Code "PackageIdentityInvalid"
    Assert-Condition ($identity.Attributes.Count -eq 3) "PackageIdentityInvalid"
    Assert-Condition ($identity.GetAttribute("Name") -ceq "IptvSuite.LocalDev.6f0d9a64") "PackageIdentityInvalid"
    Assert-Condition ($identity.GetAttribute("Publisher") -ceq "CN=IptvSuite Local Development") "PackagePublisherInvalid"
    Assert-Condition ($identity.GetAttribute("Version") -ceq "0.1.0.0") "PackageVersionInvalid"

    $targetFamily = Get-SingleNode `
        -Node $manifest `
        -XPath "/*[local-name()='Package']/*[local-name()='Dependencies']/*[local-name()='TargetDeviceFamily']" `
        -Code "TargetDeviceFamilyInvalid"
    Assert-Condition ($targetFamily.Attributes.Count -eq 3) "TargetDeviceFamilyInvalid"
    Assert-Condition ($targetFamily.GetAttribute("Name") -ceq "Windows.Desktop") "TargetDeviceFamilyInvalid"
    Assert-Condition ($targetFamily.GetAttribute("MinVersion") -ceq "10.0.26100.0") "TargetDeviceFamilyInvalid"
    Assert-Condition ($targetFamily.GetAttribute("MaxVersionTested") -ceq "10.0.26100.0") "TargetDeviceFamilyInvalid"

    $capabilities = @($manifest.SelectNodes(
        "/*[local-name()='Package']/*[local-name()='Capabilities']/*"))
    Assert-Condition ($capabilities.Count -eq 1) "PackageCapabilitiesInvalid"
    Assert-Condition ($capabilities[0].LocalName -ceq "Capability") "PackageCapabilitiesInvalid"
    Assert-Condition `
        ($capabilities[0].NamespaceURI -ceq
         "http://schemas.microsoft.com/appx/manifest/foundation/windows10/restrictedcapabilities") `
        "PackageCapabilitiesInvalid"
    Assert-Condition ($capabilities[0].Attributes.Count -eq 1) "PackageCapabilitiesInvalid"
    Assert-Condition ($capabilities[0].GetAttribute("Name") -ceq "runFullTrust") "PackageCapabilitiesInvalid"

    $propertyLogo = Get-SingleNode `
        -Node $manifest `
        -XPath "/*[local-name()='Package']/*[local-name()='Properties']/*[local-name()='Logo']" `
        -Code "ManifestAssetBindingInvalid"
    $visualElements = Get-SingleNode `
        -Node $manifest `
        -XPath "/*[local-name()='Package']/*[local-name()='Applications']/*[local-name()='Application']/*[local-name()='VisualElements']" `
        -Code "ManifestAssetBindingInvalid"
    $defaultTile = Get-SingleNode `
        -Node $visualElements `
        -XPath "./*[local-name()='DefaultTile']" `
        -Code "ManifestAssetBindingInvalid"
    $splash = Get-SingleNode `
        -Node $visualElements `
        -XPath "./*[local-name()='SplashScreen']" `
        -Code "ManifestAssetBindingInvalid"
    Assert-Condition ($propertyLogo.InnerText.Trim() -ceq "Assets\StoreLogo.png") "ManifestAssetBindingInvalid"
    Assert-Condition ($visualElements.GetAttribute("Square150x150Logo") -ceq "Assets\Square150x150Logo.png") "ManifestAssetBindingInvalid"
    Assert-Condition ($visualElements.GetAttribute("Square44x44Logo") -ceq "Assets\Square44x44Logo.png") "ManifestAssetBindingInvalid"
    Assert-Condition ($defaultTile.GetAttribute("Wide310x150Logo") -ceq "Assets\Wide310x150Logo.png") "ManifestAssetBindingInvalid"
    Assert-Condition ($splash.GetAttribute("Image") -ceq "Assets\SplashScreen.png") "ManifestAssetBindingInvalid"

    $storeAssociations = @(Get-ChildItem `
        -LiteralPath (Join-Path $resolvedRepositoryRoot "apps") `
        -Filter "Package.StoreAssociation.xml" `
        -Recurse `
        -Force `
        -File |
        Where-Object {
            $_.FullName -notmatch '[\\/](?:bin|obj)[\\/]'
        })
    Assert-Condition ($storeAssociations.Count -eq 0) "StoreAssociationUnexpected"

    $script:technicalStage = "ProjectContracts"
    $projectContracts = @(
        [ordered]@{
            path = "apps\windows\src\IptvSuite.Domain\IptvSuite.Domain.csproj"
            references = @()
            packages = @()
        },
        [ordered]@{
            path = "apps\windows\src\IptvSuite.Application\IptvSuite.Application.csproj"
            references = @("..\IptvSuite.Domain\IptvSuite.Domain.csproj")
            packages = @()
        },
        [ordered]@{
            path = "apps\windows\src\IptvSuite.Infrastructure\IptvSuite.Infrastructure.csproj"
            references = @("..\IptvSuite.Application\IptvSuite.Application.csproj")
            packages = @("Microsoft.Data.Sqlite", "System.Security.Cryptography.ProtectedData")
        },
        [ordered]@{
            path = "apps\windows\src\IptvSuite.Windows\IptvSuite.Windows.csproj"
            references = @(
                "..\IptvSuite.Application\IptvSuite.Application.csproj",
                "..\IptvSuite.Infrastructure\IptvSuite.Infrastructure.csproj")
            packages = @("Microsoft.Windows.SDK.BuildTools", "Microsoft.WindowsAppSDK")
        }
    )

    $script:technicalStage = "ProductionProjectGraph"
    $projects = @{}
    foreach ($contract in $projectContracts) {
        $projectFile = Resolve-RegularRepositoryFile `
            -Root $resolvedRepositoryRoot `
            -RelativePath $contract.path `
            -Code "ProductionProjectFileInvalid"
        $project = Read-SafeXml `
            -Text (Read-StrictUtf8Text -File $projectFile -Code "ProductionProjectEncodingInvalid") `
            -Code "ProductionProjectXmlInvalid"
        $projects[$contract.path] = $project

        $actualReferences = @(
            $project.SelectNodes("/*[local-name()='Project']/*[local-name()='ItemGroup']/*[local-name()='ProjectReference']") |
                ForEach-Object { $_.GetAttribute("Include").Replace('/', '\') })
        Assert-ExactStringSet `
            -Actual $actualReferences `
            -Expected $contract.references `
            -Code "ProductionProjectReferencesInvalid"

        $actualPackages = @(
            $project.SelectNodes("/*[local-name()='Project']/*[local-name()='ItemGroup']/*[local-name()='PackageReference']") |
                ForEach-Object { $_.GetAttribute("Include") })
        Assert-ExactStringSet `
            -Actual $actualPackages `
            -Expected $contract.packages `
            -Code "ProductionPackageReferencesInvalid"
    }

    $script:technicalStage = "WindowsProjectContract"
    $windowsProject = $projects["apps\windows\src\IptvSuite.Windows\IptvSuite.Windows.csproj"]
    $expectedWindowsProperties = [ordered]@{
        TargetFramework = "net10.0-windows10.0.26100.0"
        TargetPlatformMinVersion = "10.0.26100.0"
        Platforms = "x64"
        PlatformTarget = "x64"
        RuntimeIdentifier = "win-x64"
        WindowsAppSDKSelfContained = "false"
        SelfContained = "false"
        AppxBundle = "Never"
        Version = "0.1.0"
        AssemblyVersion = "0.1.0.0"
        FileVersion = "0.1.0.0"
    }
    foreach ($property in $expectedWindowsProperties.GetEnumerator()) {
        $actual = Get-SingleProjectProperty `
            -Project $windowsProject `
            -Name $property.Key `
            -Code "WindowsProjectPropertyInvalid"
        Assert-Condition ($actual -ceq $property.Value) "WindowsProjectPropertyInvalid"
    }
    Assert-Condition `
        ($identity.GetAttribute("Version") -ceq ($expectedWindowsProperties.Version + ".0")) `
        "ProjectManifestVersionMismatch"

    $script:technicalStage = "ProductionAssetInventory"
    $expectedAssetIncludes = @(
        "Assets\AppIcon.ico",
        "Assets\SplashScreen.scale-200.png",
        "Assets\Square150x150Logo.scale-200.png",
        "Assets\Square44x44Logo.scale-200.png",
        "Assets\Square44x44Logo.targetsize-24_altform-unplated.png",
        "Assets\Square44x44Logo.targetsize-48_altform-lightunplated.png",
        "Assets\StoreLogo.png",
        "Assets\Wide310x150Logo.scale-200.png"
    )
    $actualAssetIncludes = @(
        $windowsProject.SelectNodes("/*[local-name()='Project']/*[local-name()='ItemGroup']/*[local-name()='Content']") |
            ForEach-Object { $_.GetAttribute("Include").Replace('/', '\') })
    Assert-ExactStringSet `
        -Actual $actualAssetIncludes `
        -Expected $expectedAssetIncludes `
        -Code "ProjectAssetInventoryInvalid"

    $assets = @()
    foreach ($assetInclude in (Get-OrdinalSortedStrings -Values $expectedAssetIncludes)) {
        $assetRelativePath = "apps\windows\src\IptvSuite.Windows\$assetInclude"
        $asset = Resolve-RegularRepositoryFile `
            -Root $resolvedRepositoryRoot `
            -RelativePath $assetRelativePath `
            -MaximumBytes 10MB `
            -Code "AssetFileInvalid"
        $assets += [ordered]@{
            path = (Get-RelativeEvidencePath -Root $resolvedRepositoryRoot -FullPath $asset.FullName)
            length = [long]$asset.Length
            sha256 = Get-LowerSha256 -File $asset
        }
    }

    $script:technicalStage = "ApplicationManifest"
    $applicationManifestFile = Resolve-RegularRepositoryFile `
        -Root $resolvedRepositoryRoot `
        -RelativePath "apps\windows\src\IptvSuite.Windows\app.manifest" `
        -Code "ApplicationManifestFileInvalid"
    $applicationManifest = Read-SafeXml `
        -Text (Read-StrictUtf8Text -File $applicationManifestFile -Code "ApplicationManifestEncodingInvalid") `
        -Code "ApplicationManifestXmlInvalid"
    $executionLevel = Get-SingleNode `
        -Node $applicationManifest `
        -XPath "/*[local-name()='assembly']/*[local-name()='trustInfo']/*[local-name()='security']/*[local-name()='requestedPrivileges']/*[local-name()='requestedExecutionLevel']" `
        -Code "ExecutionLevelInvalid"
    Assert-Condition ($executionLevel.GetAttribute("level") -ceq "asInvoker") "ExecutionLevelInvalid"
    Assert-Condition ($executionLevel.GetAttribute("uiAccess") -ceq "false") "ExecutionLevelInvalid"
    $dpiAwareness = Get-SingleNode `
        -Node $applicationManifest `
        -XPath "/*[local-name()='assembly']/*[local-name()='application']/*[local-name()='windowsSettings']/*[local-name()='dpiAwareness']" `
        -Code "DpiAwarenessInvalid"
    Assert-Condition ($dpiAwareness.InnerText.Trim() -ceq "PerMonitorV2") "DpiAwarenessInvalid"

    $script:technicalStage = "ProductionLockInventory"
    $expectedLockfiles = @(
        "apps\windows\src\IptvSuite.Application\packages.lock.json",
        "apps\windows\src\IptvSuite.Domain\packages.lock.json",
        "apps\windows\src\IptvSuite.Infrastructure\packages.lock.json",
        "apps\windows\src\IptvSuite.Windows\packages.lock.json"
    )
    $actualProductionLockfiles = @(
        Get-ChildItem `
            -LiteralPath (Join-Path $resolvedRepositoryRoot "apps\windows\src") `
            -Filter "packages.lock.json" `
            -Recurse `
            -Force `
            -File |
            ForEach-Object {
                Get-RelativeEvidencePath -Root $resolvedRepositoryRoot -FullPath $_.FullName
            })
    Assert-ExactStringSet `
        -Actual $actualProductionLockfiles `
        -Expected @($expectedLockfiles | ForEach-Object { $_.Replace('\', '/') }) `
        -Code "ProductionLockfileSetInvalid"

    $lockfileEvidence = @()
    $packageMap = @{}
    foreach ($lockRelativePath in (Get-OrdinalSortedStrings -Values $expectedLockfiles)) {
        $lockfile = Resolve-RegularRepositoryFile `
            -Root $resolvedRepositoryRoot `
            -RelativePath $lockRelativePath `
            -Code "ProductionLockfileInvalid"
        try {
            $lock = Read-StrictUtf8Text -File $lockfile -Code "ProductionLockfileEncodingInvalid" |
                ConvertFrom-Json
        }
        catch {
            Fail-TechnicalInvariant -Code "ProductionLockfileJsonInvalid"
        }
        Assert-Condition ($lock.version -eq 2) "ProductionLockfileVersionInvalid"
        Assert-Condition ($null -ne $lock.dependencies) "ProductionLockfileTargetsInvalid"

        $lockfileEvidence += [ordered]@{
            path = (Get-RelativeEvidencePath -Root $resolvedRepositoryRoot -FullPath $lockfile.FullName)
            length = [long]$lockfile.Length
            sha256 = Get-LowerSha256 -File $lockfile
            version = 2
        }

        foreach ($target in $lock.dependencies.PSObject.Properties) {
            Assert-Condition (-not [string]::IsNullOrWhiteSpace($target.Name)) "ProductionLockfileTargetsInvalid"
            foreach ($dependency in $target.Value.PSObject.Properties) {
                $dependencyName = [string]$dependency.Name
                $dependencyType = [string]$dependency.Value.type
                Assert-Condition `
                    (-not [string]::IsNullOrWhiteSpace($dependencyName) -and
                     -not [string]::IsNullOrWhiteSpace($dependencyType)) `
                    "ProductionLockDependencyInvalid"
                if ($dependencyType -ceq "Project") {
                    continue
                }

                $resolvedVersion = [string]$dependency.Value.resolved
                $contentHash = [string]$dependency.Value.contentHash
                Assert-Condition `
                    (-not [string]::IsNullOrWhiteSpace($resolvedVersion) -and
                     -not [string]::IsNullOrWhiteSpace($contentHash)) `
                    "ProductionLockDependencyInvalid"
                try {
                    $decodedHash = [Convert]::FromBase64String($contentHash)
                }
                catch {
                    Fail-TechnicalInvariant -Code "ProductionLockContentHashInvalid"
                }
                Assert-Condition ($decodedHash.Length -eq 64) "ProductionLockContentHashInvalid"

                if ($packageMap.ContainsKey($dependencyName)) {
                    $existingPackage = $packageMap[$dependencyName]
                    Assert-Condition `
                        ($existingPackage.version -ceq $resolvedVersion -and
                         $existingPackage.contentHash -ceq $contentHash) `
                        "ProductionPackageConflict"
                    $existingPackage.types[$dependencyType] = $true
                    $existingPackage.occurrences++
                }
                else {
                    $types = @{}
                    $types[$dependencyType] = $true
                    $packageMap[$dependencyName] = [ordered]@{
                        name = $dependencyName
                        version = $resolvedVersion
                        contentHash = $contentHash
                        types = $types
                        occurrences = 1
                    }
                }
            }
        }
    }

    $packageInventory = @()
    $packageNames = [string[]]@($packageMap.Keys)
    [System.Array]::Sort($packageNames, [System.StringComparer]::Ordinal)
    $expectedProductionPackageNames = @(
        "Microsoft.Data.Sqlite",
        "Microsoft.Data.Sqlite.Core",
        "Microsoft.Web.WebView2",
        "Microsoft.Windows.AI.MachineLearning",
        "Microsoft.Windows.SDK.BuildTools",
        "Microsoft.Windows.SDK.BuildTools.MSIX",
        "Microsoft.WindowsAppSDK",
        "Microsoft.WindowsAppSDK.AI",
        "Microsoft.WindowsAppSDK.Base",
        "Microsoft.WindowsAppSDK.DWrite",
        "Microsoft.WindowsAppSDK.Foundation",
        "Microsoft.WindowsAppSDK.InteractiveExperiences",
        "Microsoft.WindowsAppSDK.ML",
        "Microsoft.WindowsAppSDK.Runtime",
        "Microsoft.WindowsAppSDK.Search",
        "Microsoft.WindowsAppSDK.Widgets",
        "Microsoft.WindowsAppSDK.WinUI",
        "SQLitePCLRaw.bundle_e_sqlite3",
        "SQLitePCLRaw.core",
        "SQLitePCLRaw.lib.e_sqlite3",
        "SQLitePCLRaw.provider.e_sqlite3",
        "System.Numerics.Tensors",
        "System.Security.Cryptography.ProtectedData"
    )
    Assert-ExactStringSet `
        -Actual $packageNames `
        -Expected $expectedProductionPackageNames `
        -Code "ProductionPackageNameInventoryChanged"
    foreach ($packageName in $packageNames) {
        $record = $packageMap[$packageName]
        $packageTypes = [string[]]@($record.types.Keys)
        [System.Array]::Sort($packageTypes, [System.StringComparer]::Ordinal)
        $packageInventory += [ordered]@{
            name = $record.name
            version = $record.version
            contentHash = $record.contentHash
            types = @($packageTypes)
            occurrences = [int]$record.occurrences
        }
    }

    $script:technicalStage = "StorageAndInstallRoot"
    $secretFactoryFile = Resolve-RegularRepositoryFile `
        -Root $resolvedRepositoryRoot `
        -RelativePath "apps\windows\src\IptvSuite.Windows\WindowsSecretStoreFactory.cs" `
        -Code "SecretStoreFactoryInvalid"
    $secretFactory = Read-StrictUtf8Text -File $secretFactoryFile -Code "SecretStoreFactoryEncodingInvalid"
    Assert-Condition `
        ([regex]::Matches($secretFactory, 'ApplicationData\.GetDefault\(\)\.LocalCachePath').Count -eq 1) `
        "SecretStoreRootBindingInvalid"
    Assert-Condition `
        ($secretFactory -match 'Path\.Combine\s*\(\s*localCachePath\s*,\s*"ProtectedStore"\s*,\s*"v2"\s*\)') `
        "SecretStoreRootBindingInvalid"

    $catalogFactoryFile = Resolve-RegularRepositoryFile `
        -Root $resolvedRepositoryRoot `
        -RelativePath "apps\windows\src\IptvSuite.Windows\WindowsCatalogBrowserFactory.cs" `
        -Code "CatalogFactoryInvalid"
    $catalogFactory = Read-StrictUtf8Text -File $catalogFactoryFile -Code "CatalogFactoryEncodingInvalid"
    Assert-Condition `
        ([regex]::Matches($catalogFactory, 'ApplicationData\.GetDefault\(\)\.LocalCachePath').Count -eq 1) `
        "CatalogRootBindingInvalid"
    Assert-Condition `
        ($catalogFactory -match 'Path\.Combine\s*\(\s*ApplicationData\.GetDefault\(\)\.LocalCachePath\s*,\s*"Catalog"\s*,\s*"v2"\s*\)') `
        "CatalogRootBindingInvalid"

    $productionSourceRoot = Join-Path $resolvedRepositoryRoot "apps\windows\src"
    $baseDirectoryOccurrences = @()
    $productionSourceFiles = @(
        Get-ChildItem -LiteralPath $productionSourceRoot -Filter "*.cs" -Recurse -Force -File |
            Where-Object {
                $_.FullName -notmatch '[\\/](?:bin|obj)[\\/]'
            })
    $forbiddenInstallRootDiscoveryPattern =
        'AppDomain\.CurrentDomain\.(?:BaseDirectory|SetupInformation|RelativeSearchPath)|' +
        'Environment\.(?:CurrentDirectory|ProcessPath|GetCommandLineArgs)\b|' +
        'Directory\.(?:SetCurrentDirectory|GetCurrentDirectory)\s*\(|' +
        '(?:Assembly\.)?(?:GetEntryAssembly|GetExecutingAssembly|GetCallingAssembly)\s*\(|' +
        '\.Assembly\.Location\b|\.MainModule(?:\.FileName)?\b|' +
        'Process\.GetCurrentProcess\s*\(|' +
        'Package\.Current\.(?:InstalledLocation|EffectiveLocation)|' +
        'InstalledLocation\.Path|WindowsApps'
    foreach ($sourceFile in $productionSourceFiles) {
        Assert-NoReparseDirectoryChain `
            -Root $resolvedRepositoryRoot `
            -DirectoryPath $sourceFile.DirectoryName
        Assert-Condition `
            (($sourceFile.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -eq 0) `
            "ProductionSourceReparsePoint"
        Assert-Condition `
            ($sourceFile.Length -gt 0 -and $sourceFile.Length -le $script:maximumSourceFileBytes) `
            "ProductionSourceFileInvalid"
        $sourceText = Read-StrictUtf8Text -File $sourceFile -Code "ProductionSourceEncodingInvalid"
        $occurrenceCount = [regex]::Matches($sourceText, 'AppContext\.BaseDirectory').Count
        if ($occurrenceCount -gt 0) {
            $baseDirectoryOccurrences += [ordered]@{
                path = Get-RelativeEvidencePath -Root $resolvedRepositoryRoot -FullPath $sourceFile.FullName
                count = $occurrenceCount
                text = $sourceText
            }
        }

        Assert-Condition `
            ($sourceText -notmatch $forbiddenInstallRootDiscoveryPattern) `
            "InstallRootDiscoveryPatternDetected"
    }

    Assert-Condition ($baseDirectoryOccurrences.Count -eq 1) "BaseDirectoryUsageInvalid"
    Assert-Condition `
        ($baseDirectoryOccurrences[0].path -ceq "apps/windows/src/IptvSuite.Windows/MainWindow.xaml.cs" -and
         $baseDirectoryOccurrences[0].count -eq 1) `
        "BaseDirectoryUsageInvalid"
    $allowedBaseDirectoryPattern =
        'AppWindow\.SetIcon\s*\(\s*Path\.Combine\s*\(\s*' +
        'AppContext\.BaseDirectory\s*,\s*"Assets"\s*,\s*"AppIcon\.ico"\s*\)\s*\)'
    Assert-Condition `
        ([regex]::Matches(
            $baseDirectoryOccurrences[0].text,
            $allowedBaseDirectoryPattern).Count -eq 1) `
        "BaseDirectoryIconReadInvalid"

    $script:technicalStage = "EvidenceComposition"
    $blockers = Get-OrdinalSortedStrings -Values @(
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
        "WackPending"
    )

    $summary = [ordered]@{
        schemaVersion = 1
        result = "blocked"
        technicalBaselinePassed = $true
        releaseReady = $false
        commitSha = Get-CleanRepositoryCommit -Root $resolvedRepositoryRoot
        manifest = [ordered]@{
            identity = $identity.GetAttribute("Name")
            publisher = $identity.GetAttribute("Publisher")
            version = $identity.GetAttribute("Version")
            targetDeviceFamily = $targetFamily.GetAttribute("Name")
            minimumVersion = $targetFamily.GetAttribute("MinVersion")
            maximumVersionTested = $targetFamily.GetAttribute("MaxVersionTested")
            capabilities = @("runFullTrust")
            storeAssociationPresent = $false
        }
        packaging = [ordered]@{
            architecture = "x64"
            runtimeIdentifier = "win-x64"
            selfContained = $false
            windowsAppSdkSelfContained = $false
            appxBundle = "Never"
            executionLevel = "asInvoker"
            uiAccess = $false
            dpiAwareness = "PerMonitorV2"
        }
        storage = [ordered]@{
            catalogRoot = "LocalCache/Catalog/v2"
            protectedStoreRoot = "LocalCache/ProtectedStore/v2"
            baseDirectoryUse = "Assets/AppIcon.ico:read-only"
            knownInstallRootDiscoveryPatternScanPassed = $true
            installRootDiscoveryDenylistVersion = 1
        }
        assets = @($assets)
        lockfiles = @($lockfileEvidence)
        packageInventory = @($packageInventory)
        packageInventoryPolicy = [ordered]@{
            mode = "exact-current-production-package-names"
            expectedPackageCount = 23
            exactPackageNamesLocked = $true
            legalSbomComplete = $false
        }
        blockers = @($blockers)
    }

    $script:technicalStage = "EvidencePublication"
    Publish-BoundedEvidence `
        -Value $summary `
        -DestinationPath $resolvedEvidencePath `
        -Root $resolvedRepositoryRoot
}
catch {
    if ($_.Exception.Message -match '^M15TechnicalInvariant:[A-Za-z][A-Za-z0-9]+$') {
        throw $_.Exception.Message
    }

    throw "M15TechnicalInvariant:$($script:technicalStage)Failed"
}

if (-not $AllowBlockedInventory) {
    throw "M15ReleaseReadinessBlocked: releaseReady=false; evidence was published."
}

$summary
