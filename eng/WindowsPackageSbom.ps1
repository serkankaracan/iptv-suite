#requires -Version 5.1

Set-StrictMode -Version Latest

$script:packageSbomUtf8 = New-Object System.Text.UTF8Encoding($false, $true)
$script:packageSbomUtf8NoThrow = New-Object System.Text.UTF8Encoding($false)
$script:packageSbomMaximumArchiveManifestBytes = 2MB
$script:packageSbomMaximumDocumentBytes = 16MB

Add-Type -AssemblyName System.IO.Compression

function Fail-WindowsPackageSbom {
    param(
        [Parameter(Mandatory)]
        [ValidatePattern('^[A-Za-z][A-Za-z0-9]+$')]
        [string]$Code
    )

    throw "WindowsPackageSbom:$Code"
}

function Assert-WindowsPackageSbomCondition {
    param(
        [Parameter(Mandatory)]
        [bool]$Condition,

        [Parameter(Mandatory)]
        [string]$Code
    )

    if (-not $Condition) {
        Fail-WindowsPackageSbom -Code $Code
    }
}

function Get-WindowsPackageSbomSha256 {
    param(
        [Parameter(Mandatory)]
        [string]$Path
    )

    $stream = [System.IO.File]::Open(
        $Path,
        [System.IO.FileMode]::Open,
        [System.IO.FileAccess]::Read,
        [System.IO.FileShare]::None)
    $algorithm = [System.Security.Cryptography.SHA256]::Create()
    try {
        return ([BitConverter]::ToString($algorithm.ComputeHash($stream))).Replace('-', '').ToLowerInvariant()
    }
    finally {
        $algorithm.Dispose()
        $stream.Dispose()
    }
}

function Get-WindowsPackageSbomSha1 {
    param(
        [Parameter(Mandatory)]
        [string]$Path
    )

    $stream = [System.IO.File]::Open(
        $Path,
        [System.IO.FileMode]::Open,
        [System.IO.FileAccess]::Read,
        [System.IO.FileShare]::None)
    $algorithm = [System.Security.Cryptography.SHA1]::Create()
    try {
        return ([BitConverter]::ToString($algorithm.ComputeHash($stream))).Replace('-', '').ToLowerInvariant()
    }
    finally {
        $algorithm.Dispose()
        $stream.Dispose()
    }
}

function Get-WindowsPackageSbomVerificationCode {
    param(
        [Parameter(Mandatory)]
        [ValidateCount(1, 25000)]
        [string[]]$FileSha1Values
    )

    foreach ($value in $FileSha1Values) {
        Assert-WindowsPackageSbomCondition `
            ($value -cmatch '\A[0-9a-f]{40}\z') `
            'RootPackageInvalid'
    }
    # Microsoft.SBOMTool 4.1.5 normalizes the file SHA-1 values to upper case
    # before sorting and hashing the package-verification-code input.
    $concatenated = @(
        $FileSha1Values |
            ForEach-Object { $_.ToUpperInvariant() } |
            Sort-Object -CaseSensitive) -join ''
    $bytes = [System.Text.Encoding]::ASCII.GetBytes($concatenated)
    $algorithm = [System.Security.Cryptography.SHA1]::Create()
    try {
        return ([BitConverter]::ToString($algorithm.ComputeHash($bytes))).Replace('-', '').ToLowerInvariant()
    }
    finally {
        $algorithm.Dispose()
    }
}

function Assert-WindowsPackageSbomPathHasNoReparsePoint {
    param(
        [Parameter(Mandatory)]
        [string]$Path,

        [Parameter(Mandatory)]
        [string]$Code
    )

    $current = [System.IO.Path]::GetFullPath($Path).TrimEnd(
        [System.IO.Path]::DirectorySeparatorChar,
        [System.IO.Path]::AltDirectorySeparatorChar)
    while (-not [string]::IsNullOrWhiteSpace($current)) {
        if ([System.IO.File]::Exists($current) -or [System.IO.Directory]::Exists($current)) {
            try {
                $item = Get-Item -LiteralPath $current -Force -ErrorAction Stop
            }
            catch {
                Fail-WindowsPackageSbom -Code $Code
            }
            Assert-WindowsPackageSbomCondition `
                (($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -eq 0) `
                $Code
        }

        $parent = [System.IO.Path]::GetDirectoryName($current)
        if ([string]::IsNullOrWhiteSpace($parent) -or
            [string]::Equals($parent, $current, [System.StringComparison]::OrdinalIgnoreCase)) {
            break
        }
        $current = $parent.TrimEnd(
            [System.IO.Path]::DirectorySeparatorChar,
            [System.IO.Path]::AltDirectorySeparatorChar)
    }
}

function Resolve-WindowsPackageSbomRegularFile {
    param(
        [Parameter(Mandatory)]
        [string]$Path,

        [Parameter(Mandatory)]
        [long]$MaximumBytes,

        [Parameter(Mandatory)]
        [string]$Code
    )

    Assert-WindowsPackageSbomCondition ($MaximumBytes -gt 0) $Code
    $fullPath = [System.IO.Path]::GetFullPath($Path)
    Assert-WindowsPackageSbomPathHasNoReparsePoint -Path $fullPath -Code $Code
    try {
        $item = Get-Item -LiteralPath $fullPath -Force -ErrorAction Stop
    }
    catch {
        Fail-WindowsPackageSbom -Code $Code
    }

    Assert-WindowsPackageSbomCondition (-not $item.PSIsContainer) $Code
    Assert-WindowsPackageSbomCondition `
        (($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -eq 0) `
        $Code
    Assert-WindowsPackageSbomCondition `
        ($item.Length -gt 0 -and $item.Length -le $MaximumBytes) `
        $Code
    return $item
}

function Read-WindowsPackageSbomJson {
    param(
        [Parameter(Mandatory)]
        [System.IO.FileInfo]$File,

        [Parameter(Mandatory)]
        [string]$Code,

        [long]$MaximumBytes = $script:packageSbomMaximumDocumentBytes
    )

    $resolved = Resolve-WindowsPackageSbomRegularFile `
        -Path $File.FullName `
        -MaximumBytes $MaximumBytes `
        -Code $Code
    try {
        $stream = [System.IO.File]::Open(
            $resolved.FullName,
            [System.IO.FileMode]::Open,
            [System.IO.FileAccess]::Read,
            [System.IO.FileShare]::None)
        try {
            Assert-WindowsPackageSbomCondition ($stream.Length -le $MaximumBytes) $Code
            $bytes = New-Object byte[] ([int]$stream.Length)
            $offset = 0
            while ($offset -lt $bytes.Length) {
                $read = $stream.Read($bytes, $offset, $bytes.Length - $offset)
                Assert-WindowsPackageSbomCondition ($read -gt 0) $Code
                $offset += $read
            }
            $text = $script:packageSbomUtf8.GetString($bytes)
        }
        finally {
            $stream.Dispose()
        }
        $value = $text | ConvertFrom-Json -ErrorAction Stop
        if ([string]::Equals(
                $resolved.Name,
                'windows-package-sbom-tool.json',
                [System.StringComparison]::Ordinal)) {
            Assert-WindowsPackageSbomConfiguration -Configuration $value
        }
        return [pscustomobject]@{
            Text = $text
            Value = $value
        }
    }
    catch {
        Fail-WindowsPackageSbom -Code $Code
    }
}

function Get-WindowsPackageSbomArchiveManifest {
    param(
        [Parameter(Mandatory)]
        [System.IO.FileInfo]$Package,

        [Parameter(Mandatory)]
        [string]$Code
    )

    try {
        $stream = [System.IO.File]::Open(
            $Package.FullName,
            [System.IO.FileMode]::Open,
            [System.IO.FileAccess]::Read,
            [System.IO.FileShare]::None)
    }
    catch {
        Fail-WindowsPackageSbom -Code $Code
    }
    $archive = $null
    try {
        $archive = New-Object System.IO.Compression.ZipArchive(
            $stream,
            [System.IO.Compression.ZipArchiveMode]::Read,
            $false)
        Assert-WindowsPackageSbomCondition `
            ($archive.Entries.Count -gt 0 -and $archive.Entries.Count -le 25000) `
            $Code

        $manifestEntries = @($archive.Entries | Where-Object {
            [string]::Equals($_.FullName.Replace('\', '/'), 'AppxManifest.xml', [System.StringComparison]::OrdinalIgnoreCase)
        })
        Assert-WindowsPackageSbomCondition ($manifestEntries.Count -eq 1) $Code
        $entry = $manifestEntries[0]
        Assert-WindowsPackageSbomCondition `
            ([string]::Equals($entry.FullName.Replace('\', '/'), 'AppxManifest.xml', [System.StringComparison]::Ordinal)) `
            $Code
        Assert-WindowsPackageSbomCondition `
            ($entry.Length -gt 0 -and $entry.Length -le $script:packageSbomMaximumArchiveManifestBytes) `
            $Code

        $entryStream = $entry.Open()
        $memory = New-Object System.IO.MemoryStream([int]$entry.Length)
        try {
            $buffer = New-Object byte[] 81920
            while (($read = $entryStream.Read($buffer, 0, $buffer.Length)) -gt 0) {
                Assert-WindowsPackageSbomCondition `
                    (($memory.Length + $read) -le $script:packageSbomMaximumArchiveManifestBytes) `
                    $Code
                $memory.Write($buffer, 0, $read)
            }
            Assert-WindowsPackageSbomCondition ($memory.Length -eq $entry.Length) $Code
            $manifestBytes = $memory.ToArray()
            $utf8Offset = if ($manifestBytes.Length -ge 3 -and
                $manifestBytes[0] -eq 0xEF -and
                $manifestBytes[1] -eq 0xBB -and
                $manifestBytes[2] -eq 0xBF) { 3 } else { 0 }
            $text = $script:packageSbomUtf8.GetString(
                $manifestBytes,
                $utf8Offset,
                $manifestBytes.Length - $utf8Offset)
        }
        finally {
            $memory.Dispose()
            $entryStream.Dispose()
        }

        $settings = New-Object System.Xml.XmlReaderSettings
        $settings.DtdProcessing = [System.Xml.DtdProcessing]::Prohibit
        $settings.XmlResolver = $null
        $settings.MaxCharactersFromEntities = 0
        $settings.MaxCharactersInDocument = $script:packageSbomMaximumArchiveManifestBytes
        $stringReader = New-Object System.IO.StringReader($text)
        $reader = [System.Xml.XmlReader]::Create($stringReader, $settings)
        try {
            $document = New-Object System.Xml.XmlDocument
            $document.XmlResolver = $null
            $document.Load($reader)
        }
        catch {
            Fail-WindowsPackageSbom -Code $Code
        }
        finally {
            $reader.Dispose()
            $stringReader.Dispose()
        }

        $identityNodes = @($document.SelectNodes("/*[local-name()='Package']/*[local-name()='Identity']"))
        Assert-WindowsPackageSbomCondition ($identityNodes.Count -eq 1) $Code
        $identity = $identityNodes[0]
        $name = [string]$identity.GetAttribute('Name')
        $publisher = [string]$identity.GetAttribute('Publisher')
        $version = [string]$identity.GetAttribute('Version')
        $architecture = [string]$identity.GetAttribute('ProcessorArchitecture')
        Assert-WindowsPackageSbomCondition ($name -cmatch '\A[A-Za-z0-9][A-Za-z0-9.-]{0,199}\z') $Code
        Assert-WindowsPackageSbomCondition `
            ($publisher.Length -gt 0 -and $publisher.Length -le 512 -and $publisher -cnotmatch '[\x00-\x1f\x7f]') `
            $Code
        Assert-WindowsPackageSbomCondition `
            ($version -cmatch '\A(?:0|[1-9][0-9]{0,4})(?:\.(?:0|[1-9][0-9]{0,4})){3}\z') `
            $Code
        foreach ($part in $version.Split('.')) {
            Assert-WindowsPackageSbomCondition ([int]$part -le 65535) $Code
        }
        Assert-WindowsPackageSbomCondition `
            ($architecture -cin @('x86', 'x64', 'arm', 'arm64', 'neutral')) `
            $Code
        return [pscustomobject]@{
            Name = $name
            Publisher = $publisher
            Version = $version
            Architecture = $architecture
            Document = $document
        }
    }
    catch {
        Fail-WindowsPackageSbom -Code $Code
    }
    finally {
        if ($null -ne $archive) {
            $archive.Dispose()
        }
        else {
            $stream.Dispose()
        }
    }
}

function Get-WindowsPackageSbomChecksumValue {
    param(
        [Parameter(Mandatory)]
        [object]$Value,

        [Parameter(Mandatory)]
        [string]$Algorithm,

        [Parameter(Mandatory)]
        [string]$Code
    )

    $matches = @($Value.checksums | Where-Object {
        [string]::Equals([string]$_.algorithm, $Algorithm, [System.StringComparison]::Ordinal)
    })
    Assert-WindowsPackageSbomCondition ($matches.Count -eq 1) $Code
    $checksum = [string]$matches[0].checksumValue
    $expectedLength = if ($Algorithm -ceq 'SHA1') { 40 } else { 64 }
    Assert-WindowsPackageSbomCondition `
        ($checksum -cmatch "\A[0-9a-f]{$expectedLength}\z") `
        $Code
    return $checksum
}

function Assert-WindowsPackageSbomNoCaseCollision {
    param(
        [Parameter(Mandatory)]
        [AllowEmptyCollection()]
        [string[]]$Values,

        [Parameter(Mandatory)]
        [string]$Code
    )

    $ordinal = New-Object 'System.Collections.Generic.HashSet[string]' ([System.StringComparer]::Ordinal)
    $ignoreCase = New-Object 'System.Collections.Generic.HashSet[string]' ([System.StringComparer]::OrdinalIgnoreCase)
    foreach ($value in $Values) {
        Assert-WindowsPackageSbomCondition `
            (-not [string]::IsNullOrWhiteSpace($value) -and $ordinal.Add($value) -and $ignoreCase.Add($value)) `
            $Code
    }
}

function Assert-WindowsPackageSbomExactSet {
    param(
        [Parameter(Mandatory)]
        [AllowEmptyCollection()]
        [string[]]$Actual,

        [Parameter(Mandatory)]
        [AllowEmptyCollection()]
        [string[]]$Expected,

        [Parameter(Mandatory)]
        [string]$Code
    )

    Assert-WindowsPackageSbomNoCaseCollision -Values $Actual -Code $Code
    Assert-WindowsPackageSbomNoCaseCollision -Values $Expected -Code $Code
    $actualSorted = @($Actual | Sort-Object -CaseSensitive)
    $expectedSorted = @($Expected | Sort-Object -CaseSensitive)
    Assert-WindowsPackageSbomCondition ($actualSorted.Count -eq $expectedSorted.Count) $Code
    for ($index = 0; $index -lt $expectedSorted.Count; $index++) {
        Assert-WindowsPackageSbomCondition `
            ([string]::Equals($actualSorted[$index], $expectedSorted[$index], [System.StringComparison]::Ordinal)) `
            $Code
    }
}

function Assert-WindowsPackageSbomConfiguration {
    param(
        [Parameter(Mandatory)]
        [object]$Configuration
    )

    Assert-WindowsPackageSbomCondition ([int]$Configuration.schemaVersion -eq 1) 'ConfigurationInvalid'
    Assert-WindowsPackageSbomCondition `
        ([string]$Configuration.packageId -ceq 'microsoft.sbom.dotnettool' -and
         [string]$Configuration.version -ceq '4.1.5' -and
         [string]$Configuration.command -ceq 'sbom-tool' -and
         [int]$Configuration.parallelism -eq 2 -and
         [string]$Configuration.manifestInfo -ceq 'SPDX:2.2') `
        'ConfigurationInvalid'
    Assert-WindowsPackageSbomCondition `
        ([string]$Configuration.packageName -ceq 'IptvSuite.Windows.ReleaseSet' -and
         [string]$Configuration.packageSupplier -ceq 'NOASSERTION' -and
         [string]$Configuration.namespaceBase -ceq 'https://github.com/serkankaracan/iptv-suite/sbom') `
        'ConfigurationInvalid'
    Assert-WindowsPackageSbomCondition `
        ([string]$Configuration.nupkgSha256 -ceq '00e1fb81c01f4e9ad7a9d00f365bb3f3776cde6fecdd15cc3adbbce1f83d14bb' -and
         [string]$Configuration.shimSha256 -ceq 'c8e151612c03db7a5b8d680cd5ccdfd1d9676f36d43c33cec2a4397fb19ada55') `
        'ConfigurationInvalid'
    Assert-WindowsPackageSbomCondition `
        ([string]$Configuration.toolExecutableRelativePath -ceq '.artifacts/windows-package-sbom-tool/sbom-tool.exe' -and
         [string]$Configuration.componentPath -ceq 'apps/windows/src') `
        'ConfigurationInvalid'

    $productionInputs = @($Configuration.productionInputs | ForEach-Object { [string]$_ })
    Assert-WindowsPackageSbomCondition `
        ($productionInputs.Count -gt 0 -and $productionInputs.Count -le 32) `
        'ConfigurationInvalid'
    Assert-WindowsPackageSbomNoCaseCollision -Values $productionInputs -Code 'ConfigurationInvalid'
    foreach ($relativePath in $productionInputs) {
        Assert-WindowsPackageSbomCondition `
            ($relativePath -cmatch '\A(?!/)(?!.*(?:^|/)\.\.(?:/|$))[A-Za-z0-9._/-]+\z' -and
             $relativePath.IndexOf('\', [System.StringComparison]::Ordinal) -lt 0) `
            'ConfigurationInvalid'
    }

    $expectedComponents = @($Configuration.expectedComponents)
    Assert-WindowsPackageSbomCondition `
        ($expectedComponents.Count -gt 0 -and
         $expectedComponents.Count -le ([int]$Configuration.limits.maximumPackages - 3)) `
        'ConfigurationInvalid'
    $componentTuples = @()
    foreach ($component in $expectedComponents) {
        $name = [string]$component.name
        $version = [string]$component.version
        Assert-WindowsPackageSbomCondition `
            ($name -cmatch '\A[A-Za-z0-9][A-Za-z0-9._-]{0,199}\z' -and
             $version -cmatch '\A[0-9A-Za-z][0-9A-Za-z.+-]{0,99}\z') `
            'ConfigurationInvalid'
        $componentTuples += "$name@$version"
    }
    Assert-WindowsPackageSbomNoCaseCollision -Values $componentTuples -Code 'ConfigurationInvalid'

    Assert-WindowsPackageSbomCondition `
        ([long]$Configuration.limits.maximumPackageBytes -gt 0 -and
         [long]$Configuration.limits.maximumPackageBytes -le 2147483648 -and
         [long]$Configuration.limits.maximumRepositoryInputBytes -gt 0 -and
         [long]$Configuration.limits.maximumRepositoryInputBytes -le 4194304 -and
         [long]$Configuration.limits.maximumSbomBytes -gt 0 -and
         [long]$Configuration.limits.maximumSbomBytes -le $script:packageSbomMaximumDocumentBytes -and
         [int]$Configuration.limits.maximumPackages -ge ($expectedComponents.Count + 3) -and
         [int]$Configuration.limits.maximumPackages -le 256 -and
         [int]$Configuration.limits.maximumRelationships -gt 0 -and
         [int]$Configuration.limits.maximumRelationships -le 2048 -and
         [int]$Configuration.limits.toolTimeoutSeconds -ge 1 -and
         [int]$Configuration.limits.toolTimeoutSeconds -le 300) `
        'ConfigurationInvalid'
}

function Assert-WindowsPackageSbomDocument {
    param(
        [Parameter(Mandatory)]
        [System.IO.FileInfo]$SbomFile,

        [Parameter(Mandatory)]
        [object]$Configuration,

        [Parameter(Mandatory)]
        [string]$ExpectedNamespace,

        [Parameter(Mandatory)]
        [string]$ExpectedVersion,

        [Parameter(Mandatory)]
        [System.IO.FileInfo]$ApplicationPackage,

        [Parameter(Mandatory)]
        [System.IO.FileInfo]$RuntimePackage,

        [Parameter(Mandatory)]
        [string]$ApplicationArtifactSpdxId,

        [Parameter(Mandatory)]
        [string]$RuntimeArtifactSpdxId
    )

    Assert-WindowsPackageSbomConfiguration -Configuration $Configuration
    Assert-WindowsPackageSbomCondition `
        ($ExpectedVersion -cmatch '\A(?:0|[1-9][0-9]{0,4})(?:\.(?:0|[1-9][0-9]{0,4})){3}\z') `
        'DocumentContractInvalid'
    foreach ($versionPart in $ExpectedVersion.Split('.')) {
        Assert-WindowsPackageSbomCondition ([int]$versionPart -le 65535) 'DocumentContractInvalid'
    }
    Assert-WindowsPackageSbomCondition `
        ($ExpectedNamespace.StartsWith("$([string]$Configuration.namespaceBase)/", [System.StringComparison]::Ordinal) -and
         $ExpectedNamespace -cmatch '\Ahttps://github\.com/serkankaracan/iptv-suite/sbom/[A-Za-z0-9._~!$&''()*+,;=:@%/-]+\z') `
        'DocumentContractInvalid'
    Assert-WindowsPackageSbomCondition `
        ($ApplicationArtifactSpdxId -match '\ASPDXRef-[A-Za-z0-9.-]+\z' -and
         $RuntimeArtifactSpdxId -match '\ASPDXRef-[A-Za-z0-9.-]+\z') `
        'DocumentContractInvalid'

    $parsed = Read-WindowsPackageSbomJson `
        -File $SbomFile `
        -Code 'DocumentJsonInvalid' `
        -MaximumBytes ([long]$Configuration.limits.maximumSbomBytes)
    $document = $parsed.Value
    $text = $parsed.Text

    Assert-WindowsPackageSbomCondition ($text.IndexOf([char]0) -lt 0) 'DocumentContainsUnsafeText'
    Assert-WindowsPackageSbomCondition `
        ($text -notmatch '(?i)(?:(?<![A-Za-z0-9])[A-Z]:(?:\\\\|/)|file:/|\\\\\\\\(?:[?.]\\\\)?|/(?:home|Users|tmp|var|mnt)/)') `
        'DocumentContainsUnsafeText'
    foreach ($sensitiveValue in @($env:USERPROFILE)) {
        if (-not [string]::IsNullOrWhiteSpace($sensitiveValue)) {
            Assert-WindowsPackageSbomCondition `
                ($text.IndexOf($sensitiveValue, [System.StringComparison]::OrdinalIgnoreCase) -lt 0) `
                'DocumentContainsUnsafeText'
        }
    }

    Assert-WindowsPackageSbomCondition ([string]$document.spdxVersion -ceq 'SPDX-2.2') 'DocumentContractInvalid'
    Assert-WindowsPackageSbomCondition ([string]$document.dataLicense -ceq 'CC0-1.0') 'DocumentContractInvalid'
    Assert-WindowsPackageSbomCondition ([string]$document.SPDXID -ceq 'SPDXRef-DOCUMENT') 'DocumentContractInvalid'
    Assert-WindowsPackageSbomCondition ([string]$document.documentNamespace -ceq $ExpectedNamespace) 'DocumentContractInvalid'
    Assert-WindowsPackageSbomExactSet `
        -Actual @($document.documentDescribes | ForEach-Object { [string]$_ }) `
        -Expected @('SPDXRef-RootPackage') `
        -Code 'DocumentContractInvalid'

    $creationInfoCreators = @($document.creationInfo.creators | ForEach-Object { [string]$_ })
    Assert-WindowsPackageSbomNoCaseCollision `
        -Values $creationInfoCreators `
        -Code 'CreationInfoInvalid'
    $officialCreatorSignature = @(
        "Organization: $([string]$Configuration.packageSupplier)",
        "Tool: Microsoft.SBOMTool-$([string]$Configuration.version)") | Sort-Object -CaseSensitive
    $selfTestCreatorSignature = @('Tool: IptvSuite.WindowsPackageSbom.SelfTest-1.0')
    $actualCreatorSignature = @($creationInfoCreators | Sort-Object -CaseSensitive)
    $isOfficialDocument = (($actualCreatorSignature -join "`n") -ceq ($officialCreatorSignature -join "`n"))
    $isSyntheticSelfTestDocument =
        (($actualCreatorSignature -join "`n") -ceq ($selfTestCreatorSignature -join "`n") -and
         $ApplicationPackage.Name -ceq 'Synthetic.Application_0.1.0.0_x64.msix' -and
         $RuntimePackage.Name -ceq 'Microsoft.WindowsAppRuntime.2_2.4.0.0_x64.msix')
    Assert-WindowsPackageSbomCondition `
        ($isOfficialDocument -or $isSyntheticSelfTestDocument) `
        'CreationInfoInvalid'
    $expectedDocumentName = if ($isOfficialDocument) {
        "$([string]$Configuration.packageName) $ExpectedVersion"
    }
    else {
        [string]$Configuration.packageName
    }
    Assert-WindowsPackageSbomCondition `
        ([string]$document.name -ceq $expectedDocumentName) `
        'DocumentContractInvalid'
    Assert-WindowsPackageSbomCondition `
        ([string]$document.creationInfo.created -cmatch '\A[0-9]{4}-(?:0[1-9]|1[0-2])-(?:0[1-9]|[12][0-9]|3[01])T(?:[01][0-9]|2[0-3]):[0-5][0-9]:[0-5][0-9]Z\z') `
        'CreationInfoInvalid'

    $packages = @($document.packages)
    Assert-WindowsPackageSbomCondition `
        ($packages.Count -eq (@($Configuration.expectedComponents).Count + 3) -and
         $packages.Count -le [int]$Configuration.limits.maximumPackages) `
        'PackageSetInvalid'
    $packageIds = @($packages | ForEach-Object { [string]$_.SPDXID })
    Assert-WindowsPackageSbomNoCaseCollision -Values $packageIds -Code 'PackageSetInvalid'
    $packageNames = @($packages | ForEach-Object { [string]$_.name })
    Assert-WindowsPackageSbomNoCaseCollision -Values $packageNames -Code 'PackageSetInvalid'

    $rootPackages = @($packages | Where-Object { [string]$_.SPDXID -ceq 'SPDXRef-RootPackage' })
    Assert-WindowsPackageSbomCondition ($rootPackages.Count -eq 1) 'RootPackageInvalid'
    $rootPackage = $rootPackages[0]
    Assert-WindowsPackageSbomCondition ([string]$rootPackage.name -ceq [string]$Configuration.packageName) 'RootPackageInvalid'
    Assert-WindowsPackageSbomCondition ([string]$rootPackage.versionInfo -ceq $ExpectedVersion) 'RootPackageInvalid'
    Assert-WindowsPackageSbomCondition ($rootPackage.filesAnalyzed -is [bool] -and $rootPackage.filesAnalyzed) 'RootPackageInvalid'
    Assert-WindowsPackageSbomCondition ([string]$rootPackage.packageFileName -ceq './release-set') 'RootPackageInvalid'
    $expectedVerificationCode = Get-WindowsPackageSbomVerificationCode -FileSha1Values @(
        (Get-WindowsPackageSbomSha1 $ApplicationPackage.FullName),
        (Get-WindowsPackageSbomSha1 $RuntimePackage.FullName))
    Assert-WindowsPackageSbomCondition `
        ([string]$rootPackage.packageVerificationCode.packageVerificationCodeValue -ceq $expectedVerificationCode) `
        'RootPackageInvalid'
    if ($isOfficialDocument) {
        Assert-WindowsPackageSbomCondition `
            ([string]$rootPackage.supplier -ceq "Organization: $([string]$Configuration.packageSupplier)") `
            'RootPackageInvalid'
        $rootExternalRefs = @($rootPackage.externalRefs)
        $expectedRootPurlPattern =
            '\Apkg:swid/NOASSERTION/github\.com/IptvSuite\.Windows\.ReleaseSet@' +
            [regex]::Escape($ExpectedVersion) +
            '\?tag_id=[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}\z'
        Assert-WindowsPackageSbomCondition `
            ($rootExternalRefs.Count -eq 1 -and
             [string]$rootExternalRefs[0].referenceCategory -ceq 'PACKAGE-MANAGER' -and
             [string]$rootExternalRefs[0].referenceType -ceq 'purl' -and
             [string]$rootExternalRefs[0].referenceLocator -cmatch $expectedRootPurlPattern) `
            'RootPackageInvalid'
    }

    $applicationArtifacts = @($packages | Where-Object { [string]$_.SPDXID -ceq $ApplicationArtifactSpdxId })
    $runtimeArtifacts = @($packages | Where-Object { [string]$_.SPDXID -ceq $RuntimeArtifactSpdxId })
    Assert-WindowsPackageSbomCondition ($applicationArtifacts.Count -eq 1) 'ApplicationArtifactInvalid'
    Assert-WindowsPackageSbomCondition ($runtimeArtifacts.Count -eq 1) 'RuntimeArtifactInvalid'
    $applicationArtifact = $applicationArtifacts[0]
    $runtimeArtifact = $runtimeArtifacts[0]
    $applicationArchiveManifest = Get-WindowsPackageSbomArchiveManifest `
        -Package $ApplicationPackage `
        -Code 'ApplicationArtifactInvalid'
    $runtimeArchiveManifest = Get-WindowsPackageSbomArchiveManifest `
        -Package $RuntimePackage `
        -Code 'RuntimeArtifactInvalid'
    Assert-WindowsPackageSbomCondition `
        ($applicationArchiveManifest.Version -ceq $ExpectedVersion -and
         [string]$applicationArtifact.versionInfo -ceq $applicationArchiveManifest.Version -and
         [string]$runtimeArtifact.versionInfo -ceq $runtimeArchiveManifest.Version) `
        'ArtifactPackageInvalid'
    if ($isOfficialDocument) {
        Assert-WindowsPackageSbomCondition `
            ([string]$applicationArtifact.name -ceq 'IptvSuite.Windows.MsixArtifact' -and
             [string]$runtimeArtifact.name -ceq 'Microsoft.WindowsAppRuntime.2.MsixArtifact') `
            'ArtifactPackageInvalid'
    }
    else {
        Assert-WindowsPackageSbomCondition `
            ([string]$applicationArtifact.name -ceq 'Synthetic.Application.MSIX' -and
             [string]$runtimeArtifact.name -ceq 'Synthetic.WindowsAppRuntime.MSIX') `
            'ArtifactPackageInvalid'
    }
    foreach ($artifact in @($applicationArtifact, $runtimeArtifact)) {
        Assert-WindowsPackageSbomCondition `
            ($artifact.filesAnalyzed -is [bool] -and -not $artifact.filesAnalyzed -and
             @($artifact.checksums).Count -eq 1) `
            'ArtifactPackageInvalid'
    }
    Assert-WindowsPackageSbomCondition `
        ([string]$applicationArtifact.packageFileName -ceq $ApplicationPackage.Name -and
         (Get-WindowsPackageSbomChecksumValue $applicationArtifact 'SHA256' 'ApplicationArtifactInvalid') -ceq
            (Get-WindowsPackageSbomSha256 $ApplicationPackage.FullName)) `
        'ApplicationArtifactInvalid'
    Assert-WindowsPackageSbomCondition `
        ([string]$runtimeArtifact.packageFileName -ceq $RuntimePackage.Name -and
         (Get-WindowsPackageSbomChecksumValue $runtimeArtifact 'SHA256' 'RuntimeArtifactInvalid') -ceq
            (Get-WindowsPackageSbomSha256 $RuntimePackage.FullName)) `
        'RuntimeArtifactInvalid'

    $componentPackages = @($packages | Where-Object {
        [string]$_.SPDXID -notin @('SPDXRef-RootPackage', $ApplicationArtifactSpdxId, $RuntimeArtifactSpdxId)
    })
    $actualComponentTuples = @()
    foreach ($component in $componentPackages) {
        Assert-WindowsPackageSbomCondition `
            ($component.filesAnalyzed -is [bool] -and -not $component.filesAnalyzed) `
            'ComponentSetInvalid'
        $purlMatches = @($component.externalRefs | Where-Object {
            [string]$_.referenceCategory -ceq 'PACKAGE-MANAGER' -and
            [string]$_.referenceType -ceq 'purl'
        })
        Assert-WindowsPackageSbomCondition `
            ($purlMatches.Count -eq 1 -and @($component.externalRefs).Count -eq 1) `
            'ComponentSetInvalid'
        $expectedPurl = "pkg:nuget/$([string]$component.name)@$([string]$component.versionInfo)"
        Assert-WindowsPackageSbomCondition `
            ([string]$purlMatches[0].referenceLocator -ceq $expectedPurl) `
            'ComponentSetInvalid'
        $actualComponentTuples += "$([string]$component.name)@$([string]$component.versionInfo)"
    }
    $expectedComponentTuples = @($Configuration.expectedComponents | ForEach-Object {
        "$([string]$_.name)@$([string]$_.version)"
    })
    Assert-WindowsPackageSbomExactSet `
        -Actual $actualComponentTuples `
        -Expected $expectedComponentTuples `
        -Code 'ComponentSetInvalid'
    Assert-WindowsPackageSbomCondition `
        (@($componentPackages | Where-Object {
            [string]$_.name -match '(?i)(?:LibVLC|MSTest|Testing|TestHost)'
        }).Count -eq 0) `
        'ForbiddenComponentDetected'

    $files = @($document.files)
    Assert-WindowsPackageSbomCondition ($files.Count -eq 2) 'FileSetInvalid'
    $externalDocumentRefsProperty = $document.PSObject.Properties['externalDocumentRefs']
    Assert-WindowsPackageSbomCondition `
        ($null -eq $externalDocumentRefsProperty -or @($externalDocumentRefsProperty.Value).Count -eq 0) `
        'FileSetInvalid'
    $fileNames = @($files | ForEach-Object { [string]$_.fileName })
    Assert-WindowsPackageSbomExactSet `
        -Actual $fileNames `
        -Expected @("./$($ApplicationPackage.Name)", "./$($RuntimePackage.Name)") `
        -Code 'FileSetInvalid'
    $fileIds = @($files | ForEach-Object { [string]$_.SPDXID })
    Assert-WindowsPackageSbomNoCaseCollision -Values $fileIds -Code 'FileSetInvalid'
    foreach ($file in $files) {
        $expectedFile = if ([string]$file.fileName -ceq "./$($ApplicationPackage.Name)") {
            $ApplicationPackage
        }
        else {
            $RuntimePackage
        }
        Assert-WindowsPackageSbomCondition (@($file.checksums).Count -eq 2) 'FileSetInvalid'
        Assert-WindowsPackageSbomCondition `
            ((Get-WindowsPackageSbomChecksumValue $file 'SHA1' 'FileSetInvalid') -ceq
                (Get-WindowsPackageSbomSha1 $expectedFile.FullName) -and
             (Get-WindowsPackageSbomChecksumValue $file 'SHA256' 'FileSetInvalid') -ceq
                (Get-WindowsPackageSbomSha256 $expectedFile.FullName)) `
            'FileSetInvalid'
    }
    Assert-WindowsPackageSbomExactSet `
        -Actual @($rootPackage.hasFiles | ForEach-Object { [string]$_ }) `
        -Expected $fileIds `
        -Code 'RootPackageInvalid'

    $allIds = @('SPDXRef-DOCUMENT') + $packageIds + $fileIds
    Assert-WindowsPackageSbomNoCaseCollision -Values $allIds -Code 'SpdxIdCollision'
    foreach ($packageId in $packageIds) {
        Assert-WindowsPackageSbomCondition `
            ($packageId -cmatch '\ASPDXRef-[A-Za-z0-9.-]+\z') `
            'PackageSetInvalid'
    }
    foreach ($fileId in $fileIds) {
        Assert-WindowsPackageSbomCondition `
            ($fileId -cmatch '\ASPDXRef-File-[A-Za-z0-9.-]+\z') `
            'FileSetInvalid'
    }
    $knownIds = New-Object 'System.Collections.Generic.HashSet[string]' ([System.StringComparer]::Ordinal)
    foreach ($id in $allIds) { [void]$knownIds.Add($id) }
    $relationships = @($document.relationships)
    Assert-WindowsPackageSbomCondition `
        ($relationships.Count -gt 0 -and $relationships.Count -le [int]$Configuration.limits.maximumRelationships) `
        'RelationshipSetInvalid'
    $relationshipKeys = @()
    foreach ($relationship in $relationships) {
        $left = [string]$relationship.spdxElementId
        $kind = [string]$relationship.relationshipType
        $right = [string]$relationship.relatedSpdxElement
        Assert-WindowsPackageSbomCondition `
            ($knownIds.Contains($left) -and $knownIds.Contains($right) -and
             $kind -cin @('DESCRIBES', 'CONTAINS', 'DEPENDS_ON')) `
            'RelationshipSetInvalid'
        $relationshipKeys += "$left|$kind|$right"
    }
    Assert-WindowsPackageSbomNoCaseCollision -Values $relationshipKeys -Code 'RelationshipSetInvalid'
    foreach ($requiredRelationship in @(
        "SPDXRef-DOCUMENT|DESCRIBES|SPDXRef-RootPackage",
        "SPDXRef-RootPackage|CONTAINS|$ApplicationArtifactSpdxId",
        "SPDXRef-RootPackage|CONTAINS|$RuntimeArtifactSpdxId",
        "$ApplicationArtifactSpdxId|DEPENDS_ON|$RuntimeArtifactSpdxId")) {
        Assert-WindowsPackageSbomCondition ($relationshipKeys -ccontains $requiredRelationship) 'RelationshipSetInvalid'
    }
    $describesRelationships = @($relationshipKeys | Where-Object { $_ -cmatch '\|DESCRIBES\|' })
    $containsRelationships = @($relationshipKeys | Where-Object { $_ -cmatch '\|CONTAINS\|' })
    Assert-WindowsPackageSbomExactSet `
        -Actual $describesRelationships `
        -Expected @('SPDXRef-DOCUMENT|DESCRIBES|SPDXRef-RootPackage') `
        -Code 'RelationshipSetInvalid'
    Assert-WindowsPackageSbomExactSet `
        -Actual $containsRelationships `
        -Expected @(
            "SPDXRef-RootPackage|CONTAINS|$ApplicationArtifactSpdxId",
            "SPDXRef-RootPackage|CONTAINS|$RuntimeArtifactSpdxId") `
        -Code 'RelationshipSetInvalid'
    $artifactRelationships = @($relationshipKeys | Where-Object {
        $_.StartsWith("$ApplicationArtifactSpdxId|", [System.StringComparison]::Ordinal) -or
        $_.StartsWith("$RuntimeArtifactSpdxId|", [System.StringComparison]::Ordinal) -or
        $_.EndsWith("|$ApplicationArtifactSpdxId", [System.StringComparison]::Ordinal) -or
        $_.EndsWith("|$RuntimeArtifactSpdxId", [System.StringComparison]::Ordinal)
    })
    Assert-WindowsPackageSbomExactSet `
        -Actual $artifactRelationships `
        -Expected @(
            "SPDXRef-RootPackage|CONTAINS|$ApplicationArtifactSpdxId",
            "SPDXRef-RootPackage|CONTAINS|$RuntimeArtifactSpdxId",
            "$ApplicationArtifactSpdxId|DEPENDS_ON|$RuntimeArtifactSpdxId") `
        -Code 'RelationshipSetInvalid'

    return [pscustomobject]@{
        FileCount = $files.Count
        ComponentCount = $componentPackages.Count
        PackageCount = $packages.Count
        RelationshipCount = $relationships.Count
    }
}

function Write-WindowsPackageSbomJsonAtomically {
    param(
        [Parameter(Mandatory)]
        [object]$Value,

        [Parameter(Mandatory)]
        [string]$DestinationPath,

        [Parameter(Mandatory)]
        [long]$MaximumBytes
    )

    $destination = [System.IO.Path]::GetFullPath($DestinationPath)
    $parent = [System.IO.Path]::GetDirectoryName($destination)
    Assert-WindowsPackageSbomCondition `
        ($MaximumBytes -gt 0 -and $MaximumBytes -le $script:packageSbomMaximumDocumentBytes) `
        'EvidenceTooLarge'
    Assert-WindowsPackageSbomPathHasNoReparsePoint `
        -Path $destination `
        -Code 'EvidencePathInvalid'
    [System.IO.Directory]::CreateDirectory($parent) | Out-Null
    Assert-WindowsPackageSbomPathHasNoReparsePoint `
        -Path $destination `
        -Code 'EvidencePathInvalid'
    $temporary = "$destination.$([Guid]::NewGuid().ToString('N')).tmp"
    $backup = "$destination.$([Guid]::NewGuid().ToString('N')).bak"
    try {
        $json = $Value | ConvertTo-Json -Depth 12
        $bytes = $script:packageSbomUtf8NoThrow.GetBytes($json + [Environment]::NewLine)
        Assert-WindowsPackageSbomCondition `
            ($bytes.Length -gt 0 -and $bytes.Length -le $MaximumBytes) `
            'EvidenceTooLarge'
        $temporaryStream = [System.IO.File]::Open(
            $temporary,
            [System.IO.FileMode]::CreateNew,
            [System.IO.FileAccess]::Write,
            [System.IO.FileShare]::None)
        try {
            $temporaryStream.Write($bytes, 0, $bytes.Length)
            $temporaryStream.Flush($true)
        }
        finally {
            $temporaryStream.Dispose()
        }
        Assert-WindowsPackageSbomPathHasNoReparsePoint `
            -Path $temporary `
            -Code 'EvidencePathInvalid'
        if (Test-Path -LiteralPath $destination) {
            [System.IO.File]::Replace($temporary, $destination, $backup, $true)
            Remove-Item -LiteralPath $backup -Force
        }
        else {
            [System.IO.File]::Move($temporary, $destination)
        }
    }
    finally {
        if (Test-Path -LiteralPath $temporary) {
            Remove-Item -LiteralPath $temporary -Force
        }
        if (Test-Path -LiteralPath $backup) {
            Remove-Item -LiteralPath $backup -Force
        }
    }
}
