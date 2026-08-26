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
$script:maximumPackageSbomAcceptanceBytes = 16KB
$script:maximumPackageVulnerabilityAcceptanceBytes = 16KB
$script:maximumAssetProvenanceBytes = 32KB
$script:maximumPackageProducingSnapshotFiles = 256
$script:maximumPackageProducingSnapshotDirectories = 128
$script:maximumPackageProducingSnapshotBytes = 64MB
$script:technicalStage = "Initialization"
$script:utf8Strict = New-Object System.Text.UTF8Encoding($false, $true)
$script:utf8NoBom = New-Object System.Text.UTF8Encoding($false)
$script:packageSbomAcceptanceRelativePath = "eng/windows-package-sbom-acceptance.json"
$script:packageSbomAcceptanceSha256 = "2377d35b7ec9d270bd49c63bce30d3a12d67b2570fafa5c28d0fe7998646813a"
$script:packageVulnerabilityAcceptanceRelativePath =
    "eng/windows-package-vulnerability-acceptance.json"
$script:packageVulnerabilityAcceptanceSha256 =
    "a7f5e50f37337442d770b8d9a026dc5a9cd843d833c03af13b0689a0b69099e5"
$script:packageVulnerabilityContractSourceCount = 16
$script:packageVulnerabilityContractSourceSetSha256 =
    "6b09978b5ee3ffc4d14e09458724a3d18fd1d23c5ec9ab3134dd25bfc7e91ff3"
$script:packageVulnerabilityHelperSourceSha256 =
    "3321951caee1745d644e6333ab0a7b8546f905ce2a54b92e686e7b7f104db057"
# Technical acceptance remains reusable for seven days, while a release
# decision requires a review no older than 24 hours.
$script:packageVulnerabilityMaximumAgeDays = 7
$script:packageVulnerabilityFinalReleaseMaximumAgeHours = 24
$script:packageSbomContractSourceCount = 7
$script:packageSbomContractSourceSetSha256 = "72c195557451beed09a43740036f186ff4c0091d14148024a995e3f90d20342d"
$script:packageSbomProductionInputSetSha256 = "293481fe2194c6f1fde3f667cf45872f4790e0b5955e17ac88c2d16a885b81df"
$script:packageProducingSnapshotFileCount = 113
$script:packageProducingSnapshotSha256 = "9a6313a187e7a34ea17163745dfcbe3d330f4acddbac2e2054d610edd4e49493"
$script:assetProvenanceRelativePath = "eng/windows-production-asset-provenance.json"
$script:assetProvenanceSha256 = "8006c56170202457815f3768dfcff56236b661a4dbb57aa7b7bf3a5acdcc6412"
$script:assetGeneratorSha256 = "4ac099e8da587b5df61817ab92071235e4e91408d891f5cafa3037599d7f603b"
$script:assetCanonicalSetSha256 = "6338f26af851a45eb4c7da593430ef1eab5a34afa6013365c2621fbfa0957777"

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

function Read-BoundedRegularFileBytes {
    param(
        [Parameter(Mandatory = $true)]
        [System.IO.FileInfo]$File,

        [long]$MaximumBytes = $script:maximumSourceFileBytes,

        [string]$Code = "RepositoryTextInvalid"
    )

    try {
        $stream = [System.IO.File]::Open(
            $File.FullName,
            [System.IO.FileMode]::Open,
            [System.IO.FileAccess]::Read,
            [System.IO.FileShare]::None)
        try {
            Assert-Condition `
                ($MaximumBytes -gt 0 -and
                 $MaximumBytes -le [int]::MaxValue -and
                 $stream.Length -gt 0 -and
                 $stream.Length -le $MaximumBytes) `
                $Code
            $bytes = New-Object byte[] ([int]$stream.Length)
            $offset = 0
            while ($offset -lt $bytes.Length) {
                $read = $stream.Read($bytes, $offset, $bytes.Length - $offset)
                Assert-Condition ($read -gt 0) $Code
                $offset += $read
            }
            Assert-Condition ($stream.ReadByte() -eq -1) $Code
            return ,$bytes
        }
        finally {
            $stream.Dispose()
        }
    }
    catch {
        if ($_.Exception.Message -match '^M15TechnicalInvariant:[A-Za-z][A-Za-z0-9]+$') {
            throw $_.Exception.Message
        }

        Fail-TechnicalInvariant -Code $Code
    }
}

function Read-StrictUtf8Text {
    param(
        [Parameter(Mandatory = $true)]
        [System.IO.FileInfo]$File,

        [long]$MaximumBytes = $script:maximumSourceFileBytes,

        [string]$Code = "RepositoryTextInvalid"
    )

    [byte[]]$bytes = Read-BoundedRegularFileBytes `
        -File $File `
        -MaximumBytes $MaximumBytes `
        -Code $Code
    $offset = 0
    if ($bytes.Length -ge 3 -and
        $bytes[0] -eq 0xef -and
        $bytes[1] -eq 0xbb -and
        $bytes[2] -eq 0xbf) {
        $offset = 3
    }

    try {
        return $script:utf8Strict.GetString($bytes, $offset, $bytes.Length - $offset)
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
    $propertyNode = $matches[0]
    Assert-Condition ($propertyNode.Attributes.Count -eq 0) $Code
    Assert-Condition `
        ($propertyNode.ParentNode.LocalName -ceq "PropertyGroup" -and
         $propertyNode.ParentNode.Attributes.Count -eq 0 -and
         $propertyNode.ParentNode.ParentNode.LocalName -ceq "Project") `
        $Code
    Assert-Condition `
        ($propertyNode.ChildNodes.Count -eq 1 -and
         $propertyNode.ChildNodes[0].NodeType -eq [System.Xml.XmlNodeType]::Text) `
        $Code
    return $propertyNode.InnerText.Trim()
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

function Get-LowerSha256ForBytes {
    param(
        [Parameter(Mandatory = $true)]
        [byte[]]$Bytes
    )

    $sha256 = [System.Security.Cryptography.SHA256]::Create()
    try {
        return ([System.BitConverter]::ToString($sha256.ComputeHash($Bytes))).Replace(
            "-",
            "").ToLowerInvariant()
    }
    finally {
        $sha256.Dispose()
    }
}

function Get-CanonicalTextSourceSetSha256 {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Root,

        [Parameter(Mandatory = $true)]
        [string[]]$RelativePaths,

        [string]$CapturedRelativePath,

        [string]$CapturedText
    )

    Assert-Condition ($RelativePaths.Count -gt 0 -and $RelativePaths.Count -le 32) `
        "PackageSbomAcceptanceInvalid"
    $hasCapturedRelativePath =
        $PSBoundParameters.ContainsKey("CapturedRelativePath")
    $hasCapturedText = $PSBoundParameters.ContainsKey("CapturedText")
    Assert-Condition ($hasCapturedRelativePath -eq $hasCapturedText) `
        "PackageSbomAcceptanceInvalid"
    if ($hasCapturedRelativePath) {
        Assert-Condition `
            (-not [string]::IsNullOrWhiteSpace($CapturedRelativePath) -and
             $null -ne $CapturedText -and
             $RelativePaths -ccontains $CapturedRelativePath) `
            "PackageSbomAcceptanceInvalid"
    }
    $records = @()
    foreach ($relativePath in $RelativePaths) {
        Assert-Condition `
            ($relativePath -cmatch '\A[A-Za-z0-9._/-]+\z' -and
             -not [System.IO.Path]::IsPathRooted($relativePath) -and
             $relativePath -notmatch '(?:^|/)\.\.(?:/|$)') `
            "PackageSbomAcceptanceInvalid"
        $text = if ($hasCapturedRelativePath -and
            $relativePath -ceq $CapturedRelativePath) {
            $CapturedText
        }
        else {
            $file = Resolve-RegularRepositoryFile `
                -Root $Root `
                -RelativePath ($relativePath.Replace('/', '\')) `
                -MaximumBytes $script:maximumSourceFileBytes `
                -Code "PackageSbomAcceptanceInvalid"
            Read-StrictUtf8Text `
                -File $file `
                -Code "PackageSbomAcceptanceInvalid"
        }
        $normalized = $text.Replace("`r`n", "`n").Replace("`r", "`n")
        $normalizedBytes = $script:utf8NoBom.GetBytes($normalized)
        Assert-Condition ($normalizedBytes.Length -gt 0) "PackageSbomAcceptanceInvalid"
        $records += (
            "$relativePath`0$($normalizedBytes.Length)`0" +
            (Get-LowerSha256ForBytes -Bytes $normalizedBytes))
    }

    $bindingBytes = $script:utf8NoBom.GetBytes(($records -join "`n"))
    return Get-LowerSha256ForBytes -Bytes $bindingBytes
}

function Get-CanonicalPackageSnapshotRecord {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Root,

        [Parameter(Mandatory = $true)]
        [string]$RelativePath
    )

    Assert-Condition `
        ($RelativePath -cmatch '\A[A-Za-z0-9._/-]+\z' -and
         -not [System.IO.Path]::IsPathRooted($RelativePath) -and
         $RelativePath -notmatch '(?:^|/)\.\.(?:/|$)') `
        "PackageSbomAcceptanceInvalid"
    $file = Resolve-RegularRepositoryFile `
        -Root $Root `
        -RelativePath ($RelativePath.Replace('/', '\')) `
        -MaximumBytes $script:maximumSourceFileBytes `
        -Code "PackageSbomAcceptanceInvalid"
    $extension = [System.IO.Path]::GetExtension($file.Name)
    $isBinary =
        $extension.Equals('.ico', [System.StringComparison]::OrdinalIgnoreCase) -or
        $extension.Equals('.png', [System.StringComparison]::OrdinalIgnoreCase)
    if ($isBinary) {
        [byte[]]$canonicalBytes = Read-BoundedRegularFileBytes `
            -File $file `
            -MaximumBytes $script:maximumSourceFileBytes `
            -Code "PackageSbomAcceptanceInvalid"
        $canonicalLength = [long]$canonicalBytes.Length
        $canonicalSha256 = Get-LowerSha256ForBytes -Bytes $canonicalBytes
        $kind = 'binary'
    }
    else {
        $text = Read-StrictUtf8Text `
            -File $file `
            -Code "PackageSbomAcceptanceInvalid"
        $normalized = $text.Replace("`r`n", "`n").Replace("`r", "`n")
        $canonicalBytes = $script:utf8NoBom.GetBytes($normalized)
        Assert-Condition ($canonicalBytes.Length -gt 0) "PackageSbomAcceptanceInvalid"
        $canonicalLength = [long]$canonicalBytes.Length
        $canonicalSha256 = Get-LowerSha256ForBytes -Bytes $canonicalBytes
        $kind = 'text-lf'
    }

    return [pscustomobject]@{
        Record = "$RelativePath`0$kind`0$canonicalLength`0$canonicalSha256"
        CanonicalLength = $canonicalLength
    }
}

function Assert-NoNearestPackageVersionOverrides {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Root
    )

    $forbiddenRelativePaths = @(
        "apps/Directory.Packages.props",
        "apps/windows/Directory.Packages.props",
        "apps/windows/src/Directory.Packages.props",
        "apps/windows/src/IptvSuite.Application/Directory.Packages.props",
        "apps/windows/src/IptvSuite.Domain/Directory.Packages.props",
        "apps/windows/src/IptvSuite.Infrastructure/Directory.Packages.props",
        "apps/windows/src/IptvSuite.Windows/Directory.Packages.props")
    foreach ($relativePath in $forbiddenRelativePaths) {
        $candidate = [System.IO.Path]::GetFullPath((Join-Path $Root $relativePath))
        Assert-Condition (Test-PathContainedByRoot -Path $candidate -Root $Root) `
            "PackageSbomAcceptanceInvalid"
        Assert-NoReparseDirectoryChain `
            -Root $Root `
            -DirectoryPath ([System.IO.Path]::GetDirectoryName($candidate))
        Assert-Condition (-not (Test-Path -LiteralPath $candidate)) `
            "PackageSbomAcceptanceInvalid"
    }
}

function Get-PackageProducingSnapshot {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Root
    )

    $fixedRootBuildInputs = @(
        "global.json",
        "NuGet.config",
        "Directory.Build.props",
        "Directory.Packages.props",
        "Directory.Solution.props",
        "apps/windows/IptvSuite.Windows.sln")
    $records = [System.Collections.Generic.List[string]]::new()
    $seenPaths = New-Object 'System.Collections.Generic.HashSet[string]' `
        ([System.StringComparer]::OrdinalIgnoreCase)
    [long]$totalCanonicalBytes = 0

    foreach ($relativePath in $fixedRootBuildInputs) {
        Assert-Condition ($seenPaths.Add($relativePath)) "PackageSbomAcceptanceInvalid"
        $record = Get-CanonicalPackageSnapshotRecord `
            -Root $Root `
            -RelativePath $relativePath
        $records.Add([string]$record.Record)
        $totalCanonicalBytes += [long]$record.CanonicalLength
        Assert-Condition `
            ($records.Count -le $script:maximumPackageProducingSnapshotFiles -and
             $totalCanonicalBytes -le $script:maximumPackageProducingSnapshotBytes) `
            "PackageSbomAcceptanceInvalid"
    }

    $sourceRoot = [System.IO.Path]::GetFullPath((Join-Path $Root "apps\windows\src"))
    Assert-Condition (Test-PathContainedByRoot -Path $sourceRoot -Root $Root) `
        "PackageSbomAcceptanceInvalid"
    Assert-NoReparseDirectoryChain -Root $Root -DirectoryPath $sourceRoot
    $sourceRootItem = Get-Item -LiteralPath $sourceRoot -Force
    Assert-Condition `
        ($sourceRootItem.PSIsContainer -and
         ($sourceRootItem.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -eq 0) `
        "PackageSbomAcceptanceInvalid"

    $pendingDirectories = [System.Collections.Generic.Queue[System.IO.DirectoryInfo]]::new()
    $pendingDirectories.Enqueue($sourceRootItem)
    $seenDirectories = New-Object 'System.Collections.Generic.HashSet[string]' `
        ([System.StringComparer]::OrdinalIgnoreCase)
    Assert-Condition ($seenDirectories.Add($sourceRootItem.FullName)) `
        "PackageSbomAcceptanceInvalid"
    while ($pendingDirectories.Count -gt 0) {
        $directory = $pendingDirectories.Dequeue()
        $entryEnumerator = [System.IO.Directory]::EnumerateFileSystemEntries(
            $directory.FullName).GetEnumerator()
        try {
            while ($entryEnumerator.MoveNext()) {
                $entryPath = [System.IO.Path]::GetFullPath([string]$entryEnumerator.Current)
                Assert-Condition (Test-PathContainedByRoot -Path $entryPath -Root $Root) `
                    "PackageSbomAcceptanceInvalid"
                $entry = Get-Item -LiteralPath $entryPath -Force
                if ($entry.PSIsContainer) {
                    if ($entry.Name -ieq 'bin' -or $entry.Name -ieq 'obj') {
                        continue
                    }

                    Assert-Condition `
                        (($entry.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -eq 0) `
                        "PackageSbomAcceptanceInvalid"
                    Assert-Condition ($seenDirectories.Add($entry.FullName)) `
                        "PackageSbomAcceptanceInvalid"
                    Assert-Condition `
                        ($seenDirectories.Count -le $script:maximumPackageProducingSnapshotDirectories) `
                        "PackageSbomAcceptanceInvalid"
                    $pendingDirectories.Enqueue($entry)
                    continue
                }

                Assert-Condition `
                    ($records.Count -lt $script:maximumPackageProducingSnapshotFiles) `
                    "PackageSbomAcceptanceInvalid"
                $relativePath = Get-RelativeEvidencePath `
                    -Root $Root `
                    -FullPath $entry.FullName
                Assert-Condition `
                    ($relativePath.StartsWith(
                        "apps/windows/src/",
                        [System.StringComparison]::Ordinal) -and
                     $seenPaths.Add($relativePath)) `
                    "PackageSbomAcceptanceInvalid"
                $record = Get-CanonicalPackageSnapshotRecord `
                    -Root $Root `
                    -RelativePath $relativePath
                $records.Add([string]$record.Record)
                $totalCanonicalBytes += [long]$record.CanonicalLength
                Assert-Condition `
                    ($totalCanonicalBytes -le $script:maximumPackageProducingSnapshotBytes) `
                    "PackageSbomAcceptanceInvalid"
            }
        }
        finally {
            if ($entryEnumerator -is [System.IDisposable]) {
                $entryEnumerator.Dispose()
            }
        }
    }

    Assert-Condition ($records.Count -gt $fixedRootBuildInputs.Count) `
        "PackageSbomAcceptanceInvalid"
    $orderedRecords = [string[]]$records.ToArray()
    [System.Array]::Sort($orderedRecords, [System.StringComparer]::Ordinal)
    $bindingBytes = $script:utf8NoBom.GetBytes(($orderedRecords -join "`n"))
    return [pscustomobject]@{
        FileCount = [int]$orderedRecords.Count
        CanonicalBytes = $totalCanonicalBytes
        Sha256 = Get-LowerSha256ForBytes -Bytes $bindingBytes
    }
}

function Get-UInt16LittleEndian {
    param(
        [Parameter(Mandatory = $true)]
        [byte[]]$Bytes,

        [Parameter(Mandatory = $true)]
        [int]$Offset
    )

    Assert-Condition `
        ($Offset -ge 0 -and $Offset -le ($Bytes.Length - 2)) `
        "AssetProvenanceInvalid"
    return [int]([System.BitConverter]::ToUInt16($Bytes, $Offset))
}

function Get-UInt32LittleEndian {
    param(
        [Parameter(Mandatory = $true)]
        [byte[]]$Bytes,

        [Parameter(Mandatory = $true)]
        [int]$Offset
    )

    Assert-Condition `
        ($Offset -ge 0 -and $Offset -le ($Bytes.Length - 4)) `
        "AssetProvenanceInvalid"
    return [long]([System.BitConverter]::ToUInt32($Bytes, $Offset))
}

function Get-UInt32BigEndian {
    param(
        [Parameter(Mandatory = $true)]
        [byte[]]$Bytes,

        [Parameter(Mandatory = $true)]
        [int]$Offset
    )

    Assert-Condition `
        ($Offset -ge 0 -and $Offset -le ($Bytes.Length - 4)) `
        "AssetProvenanceInvalid"
    return [long]$Bytes[$Offset] * 16777216L +
        [long]$Bytes[$Offset + 1] * 65536L +
        [long]$Bytes[$Offset + 2] * 256L +
        [long]$Bytes[$Offset + 3]
}

function Get-PngAssetShape {
    param(
        [Parameter(Mandatory = $true)]
        [byte[]]$Bytes,

        [int]$Offset = 0,

        [int]$Length = $Bytes.Length
    )

    Assert-Condition `
        ($Offset -ge 0 -and
         $Length -ge 45 -and
         [long]$Offset + [long]$Length -le $Bytes.Length) `
        "AssetProvenanceInvalid"
    $signature = [byte[]]@(137, 80, 78, 71, 13, 10, 26, 10)
    for ($index = 0; $index -lt $signature.Length; $index++) {
        Assert-Condition `
            ($Bytes[$Offset + $index] -eq $signature[$index]) `
            "AssetProvenanceInvalid"
    }

    Assert-Condition `
        ((Get-UInt32BigEndian -Bytes $Bytes -Offset ($Offset + 8)) -eq 13 -and
         $Bytes[$Offset + 12] -eq 73 -and
         $Bytes[$Offset + 13] -eq 72 -and
         $Bytes[$Offset + 14] -eq 68 -and
         $Bytes[$Offset + 15] -eq 82) `
        "AssetProvenanceInvalid"
    $width = Get-UInt32BigEndian -Bytes $Bytes -Offset ($Offset + 16)
    $height = Get-UInt32BigEndian -Bytes $Bytes -Offset ($Offset + 20)
    Assert-Condition `
        ($width -gt 0 -and $width -le 4096 -and
         $height -gt 0 -and $height -le 4096 -and
         $Bytes[$Offset + 24] -eq 8 -and
         $Bytes[$Offset + 25] -eq 6 -and
         $Bytes[$Offset + 26] -eq 0 -and
         $Bytes[$Offset + 27] -eq 0 -and
         $Bytes[$Offset + 28] -eq 0) `
        "AssetProvenanceInvalid"

    $endOffset = $Offset + $Length - 12
    Assert-Condition `
        ((Get-UInt32BigEndian -Bytes $Bytes -Offset $endOffset) -eq 0 -and
         $Bytes[$endOffset + 4] -eq 73 -and
         $Bytes[$endOffset + 5] -eq 69 -and
         $Bytes[$endOffset + 6] -eq 78 -and
         $Bytes[$endOffset + 7] -eq 68) `
        "AssetProvenanceInvalid"

    return [pscustomobject]@{
        Width = [int]$width
        Height = [int]$height
    }
}

function Get-ProductionAssetShape {
    param(
        [Parameter(Mandatory = $true)]
        [System.IO.FileInfo]$File,

        [Parameter(Mandatory = $true)]
        [string]$MediaType
    )

    [byte[]]$bytes = Read-BoundedRegularFileBytes `
        -File $File `
        -MaximumBytes $script:maximumSourceFileBytes `
        -Code "AssetProvenanceInvalid"
    if ($MediaType -ceq "image/png") {
        $png = Get-PngAssetShape -Bytes $bytes
        return [pscustomobject]@{
            Width = $png.Width
            Height = $png.Height
            FrameSizes = [int[]]@()
        }
    }

    Assert-Condition ($MediaType -ceq "image/vnd.microsoft.icon") `
        "AssetProvenanceInvalid"
    Assert-Condition `
        ($bytes.Length -ge 22 -and
         (Get-UInt16LittleEndian -Bytes $bytes -Offset 0) -eq 0 -and
         (Get-UInt16LittleEndian -Bytes $bytes -Offset 2) -eq 1) `
        "AssetProvenanceInvalid"
    $frameCount = Get-UInt16LittleEndian -Bytes $bytes -Offset 4
    Assert-Condition ($frameCount -gt 0 -and $frameCount -le 32) `
        "AssetProvenanceInvalid"
    $directoryLength = 6 + (16 * $frameCount)
    Assert-Condition ($directoryLength -lt $bytes.Length) "AssetProvenanceInvalid"

    $frameSizes = [System.Collections.Generic.List[int]]::new()
    [long]$expectedImageOffset = $directoryLength
    for ($frameIndex = 0; $frameIndex -lt $frameCount; $frameIndex++) {
        $entryOffset = 6 + (16 * $frameIndex)
        $width = if ($bytes[$entryOffset] -eq 0) { 256 } else { [int]$bytes[$entryOffset] }
        $height = if ($bytes[$entryOffset + 1] -eq 0) { 256 } else { [int]$bytes[$entryOffset + 1] }
        $imageLength = Get-UInt32LittleEndian -Bytes $bytes -Offset ($entryOffset + 8)
        $imageOffset = Get-UInt32LittleEndian -Bytes $bytes -Offset ($entryOffset + 12)
        Assert-Condition `
            ($width -eq $height -and
             $bytes[$entryOffset + 2] -eq 0 -and
             $bytes[$entryOffset + 3] -eq 0 -and
             (Get-UInt16LittleEndian -Bytes $bytes -Offset ($entryOffset + 4)) -eq 1 -and
             (Get-UInt16LittleEndian -Bytes $bytes -Offset ($entryOffset + 6)) -eq 32 -and
             $imageLength -ge 45 -and
             $imageOffset -eq $expectedImageOffset -and
             $imageOffset + $imageLength -le $bytes.Length) `
            "AssetProvenanceInvalid"
        $png = Get-PngAssetShape `
            -Bytes $bytes `
            -Offset ([int]$imageOffset) `
            -Length ([int]$imageLength)
        Assert-Condition ($png.Width -eq $width -and $png.Height -eq $height) `
            "AssetProvenanceInvalid"
        $frameSizes.Add($width)
        $expectedImageOffset += $imageLength
    }
    Assert-Condition ($expectedImageOffset -eq $bytes.Length) `
        "AssetProvenanceInvalid"

    return [pscustomobject]@{
        Width = $null
        Height = $null
        FrameSizes = [int[]]$frameSizes.ToArray()
    }
}

function Assert-NoDuplicateJsonProperties {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Text,

        [string]$Code = "PackageSbomAcceptanceInvalid",

        [string]$DuplicateCode = "PackageSbomAcceptanceDuplicateProperty"
    )

    $objectPropertySets = [System.Collections.Stack]::new()
    $index = 0
    while ($index -lt $Text.Length) {
        $character = $Text[$index]
        if ($character -eq [char]0x7b) {
            $properties = New-Object 'System.Collections.Generic.HashSet[string]' `
                ([System.StringComparer]::OrdinalIgnoreCase)
            $objectPropertySets.Push($properties)
            $index++
            continue
        }
        if ($character -eq [char]0x7d) {
            Assert-Condition ($objectPropertySets.Count -gt 0) `
                $Code
            [void]$objectPropertySets.Pop()
            $index++
            continue
        }
        if ($character -ne [char]0x22) {
            $index++
            continue
        }

        $index++
        $builder = [System.Text.StringBuilder]::new()
        $closed = $false
        while ($index -lt $Text.Length) {
            $stringCharacter = $Text[$index]
            if ($stringCharacter -eq [char]0x22) {
                $closed = $true
                $index++
                break
            }
            if ($stringCharacter -eq [char]0x5c) {
                $index++
                Assert-Condition ($index -lt $Text.Length) $Code
                $escapeCharacter = $Text[$index]
                switch ($escapeCharacter) {
                    '"' { [void]$builder.Append([char]0x22) }
                    '\' { [void]$builder.Append([char]0x5c) }
                    '/' { [void]$builder.Append([char]0x2f) }
                    'b' { [void]$builder.Append([char]0x08) }
                    'f' { [void]$builder.Append([char]0x0c) }
                    'n' { [void]$builder.Append([char]0x0a) }
                    'r' { [void]$builder.Append([char]0x0d) }
                    't' { [void]$builder.Append([char]0x09) }
                    'u' {
                        Assert-Condition (($index + 4) -lt $Text.Length) `
                            $Code
                        $hex = $Text.Substring($index + 1, 4)
                        Assert-Condition ($hex -cmatch '\A[0-9A-Fa-f]{4}\z') `
                            $Code
                        [void]$builder.Append([char][Convert]::ToInt32($hex, 16))
                        $index += 4
                    }
                    default {
                        Fail-TechnicalInvariant -Code $Code
                    }
                }
                $index++
                continue
            }

            Assert-Condition ([int]$stringCharacter -ge 0x20) `
                $Code
            [void]$builder.Append($stringCharacter)
            $index++
        }

        Assert-Condition $closed $Code
        $lookAhead = $index
        while ($lookAhead -lt $Text.Length -and [char]::IsWhiteSpace($Text[$lookAhead])) {
            $lookAhead++
        }
        if ($lookAhead -lt $Text.Length -and $Text[$lookAhead] -eq [char]0x3a) {
            Assert-Condition ($objectPropertySets.Count -gt 0) `
                $Code
            $propertyName = $builder.ToString()
            if (-not $objectPropertySets.Peek().Add($propertyName)) {
                Fail-TechnicalInvariant -Code $DuplicateCode
            }
        }
    }

    Assert-Condition ($objectPropertySets.Count -eq 0) $Code
}

function Read-ProductionAssetProvenance {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Root,

        [Parameter(Mandatory = $true)]
        [object[]]$AssetInventory
    )

    try {
        $ledgerFile = Resolve-RegularRepositoryFile `
            -Root $Root `
            -RelativePath ($script:assetProvenanceRelativePath.Replace('/', '\')) `
            -MaximumBytes $script:maximumAssetProvenanceBytes `
            -Code "AssetProvenanceInvalid"
        $ledgerSha256 = Get-LowerSha256 -File $ledgerFile
        Assert-Condition ($ledgerSha256 -ceq $script:assetProvenanceSha256) `
            "AssetProvenanceInvalid"
        $ledgerText = Read-StrictUtf8Text `
            -File $ledgerFile `
            -MaximumBytes $script:maximumAssetProvenanceBytes `
            -Code "AssetProvenanceInvalid"
        Assert-NoDuplicateJsonProperties `
            -Text $ledgerText `
            -Code "AssetProvenanceInvalid" `
            -DuplicateCode "AssetProvenanceDuplicateProperty"
        try {
            $ledger = $ledgerText | ConvertFrom-Json
        }
        catch {
            Fail-TechnicalInvariant -Code "AssetProvenanceInvalid"
        }

        Assert-ExactStringSet `
            -Actual @($ledger.PSObject.Properties.Name) `
            -Expected @(
                "schemaVersion",
                "decision",
                "scope",
                "provenanceKind",
                "generatorPath",
                "generatorVersion",
                "generatorSha256",
                "algorithmVersion",
                "canonicalAssetSetSha256",
                "assets",
                "sourceAssetDependencies",
                "thirdPartyAssetInputs",
                "fonts",
                "text",
                "trademarks",
                "developmentPlaceholderOnly",
                "productionBrandApproved",
                "copyrightOwnershipDetermined",
                "redistributionDecisionComplete",
                "legalReviewComplete") `
            -Code "AssetProvenanceInvalid"
        Assert-Condition `
            ($ledger.schemaVersion -is [int] -and
             $ledger.schemaVersion -eq 1 -and
             $ledger.decision -is [string] -and
             $ledger.decision -ceq "AcceptGeneratedAssetProvenance" -and
             $ledger.scope -is [string] -and
             $ledger.scope -ceq "ExactWindowsPackageAssetOriginOnly" -and
             $ledger.provenanceKind -is [string] -and
             $ledger.provenanceKind -ceq
                "GeneratedBySourceControlledDeterministicRecipe" -and
             $ledger.generatorPath -is [string] -and
             $ledger.generatorPath -ceq "eng/New-WindowsProductionAssets.ps1" -and
             $ledger.generatorVersion -is [string] -and
             $ledger.generatorVersion -ceq "1.0.0" -and
             $ledger.generatorSha256 -is [string] -and
             $ledger.generatorSha256 -ceq $script:assetGeneratorSha256 -and
             $ledger.algorithmVersion -is [string] -and
             $ledger.algorithmVersion -ceq
                "WindowsProductionAssets-Rgba8Filter0FixedHuffmanLz77-PngFrameIco-v1" -and
             $ledger.canonicalAssetSetSha256 -is [string] -and
             $ledger.canonicalAssetSetSha256 -ceq
                $script:assetCanonicalSetSha256) `
            "AssetProvenanceInvalid"

        foreach ($emptyArrayName in @(
            "sourceAssetDependencies",
            "thirdPartyAssetInputs",
            "fonts",
            "text",
            "trademarks")) {
            $emptyArray = $ledger.PSObject.Properties[$emptyArrayName].Value
            Assert-Condition `
                ($emptyArray -is [System.Array] -and @($emptyArray).Count -eq 0) `
                "AssetProvenanceInvalid"
        }
        Assert-Condition `
            ($ledger.developmentPlaceholderOnly -is [bool] -and
             $ledger.developmentPlaceholderOnly -and
             $ledger.productionBrandApproved -is [bool] -and
             -not $ledger.productionBrandApproved -and
             $ledger.copyrightOwnershipDetermined -is [bool] -and
             -not $ledger.copyrightOwnershipDetermined -and
             $ledger.redistributionDecisionComplete -is [bool] -and
             -not $ledger.redistributionDecisionComplete -and
             $ledger.legalReviewComplete -is [bool] -and
             -not $ledger.legalReviewComplete) `
            "AssetProvenanceInvalid"

        $expectedAssets = @(
            [pscustomobject]@{
                Path = "apps/windows/src/IptvSuite.Windows/Assets/AppIcon.ico"
                MediaType = "image/vnd.microsoft.icon"
                Width = $null
                Height = $null
                FrameSizes = [int[]]@(256, 128, 64, 48, 32, 16)
            },
            [pscustomobject]@{
                Path = "apps/windows/src/IptvSuite.Windows/Assets/SplashScreen.scale-200.png"
                MediaType = "image/png"
                Width = 1240
                Height = 600
                FrameSizes = [int[]]@()
            },
            [pscustomobject]@{
                Path = "apps/windows/src/IptvSuite.Windows/Assets/Square150x150Logo.scale-200.png"
                MediaType = "image/png"
                Width = 300
                Height = 300
                FrameSizes = [int[]]@()
            },
            [pscustomobject]@{
                Path = "apps/windows/src/IptvSuite.Windows/Assets/Square44x44Logo.scale-200.png"
                MediaType = "image/png"
                Width = 88
                Height = 88
                FrameSizes = [int[]]@()
            },
            [pscustomobject]@{
                Path = "apps/windows/src/IptvSuite.Windows/Assets/Square44x44Logo.targetsize-24_altform-unplated.png"
                MediaType = "image/png"
                Width = 24
                Height = 24
                FrameSizes = [int[]]@()
            },
            [pscustomobject]@{
                Path = "apps/windows/src/IptvSuite.Windows/Assets/Square44x44Logo.targetsize-48_altform-lightunplated.png"
                MediaType = "image/png"
                Width = 48
                Height = 48
                FrameSizes = [int[]]@()
            },
            [pscustomobject]@{
                Path = "apps/windows/src/IptvSuite.Windows/Assets/StoreLogo.png"
                MediaType = "image/png"
                Width = 50
                Height = 50
                FrameSizes = [int[]]@()
            },
            [pscustomobject]@{
                Path = "apps/windows/src/IptvSuite.Windows/Assets/Wide310x150Logo.scale-200.png"
                MediaType = "image/png"
                Width = 620
                Height = 300
                FrameSizes = [int[]]@()
            })
        Assert-Condition `
            ($ledger.assets -is [System.Array] -and
             @($ledger.assets).Count -eq $expectedAssets.Count -and
             $AssetInventory.Count -eq $expectedAssets.Count) `
            "AssetProvenanceInvalid"
        Assert-ExactStringSet `
            -Actual @($AssetInventory | ForEach-Object { [string]$_.path }) `
            -Expected @($expectedAssets | ForEach-Object { [string]$_.Path }) `
            -Code "AssetProvenanceInvalid"

        $canonicalLines = [System.Collections.Generic.List[string]]::new()
        for ($assetIndex = 0; $assetIndex -lt $expectedAssets.Count; $assetIndex++) {
            $expected = $expectedAssets[$assetIndex]
            $record = @($ledger.assets)[$assetIndex]
            $expectedProperties = if ($expected.MediaType -ceq "image/png") {
                @("path", "mediaType", "width", "height", "length", "sha256")
            }
            else {
                @("path", "mediaType", "frameSizes", "length", "sha256")
            }
            Assert-ExactStringSet `
                -Actual @($record.PSObject.Properties.Name) `
                -Expected $expectedProperties `
                -Code "AssetProvenanceInvalid"
            Assert-Condition `
                ($record.path -is [string] -and
                 $record.path -ceq $expected.Path -and
                 $record.mediaType -is [string] -and
                 $record.mediaType -ceq $expected.MediaType -and
                 $record.length -is [int] -and
                 $record.length -gt 0 -and
                 $record.length -le $script:maximumSourceFileBytes -and
                 $record.sha256 -is [string] -and
                 $record.sha256 -cmatch '\A[0-9a-f]{64}\z') `
                "AssetProvenanceInvalid"

            if ($expected.MediaType -ceq "image/png") {
                Assert-Condition `
                    ($record.width -is [int] -and
                     $record.width -eq $expected.Width -and
                     $record.height -is [int] -and
                     $record.height -eq $expected.Height) `
                    "AssetProvenanceInvalid"
            }
            else {
                Assert-Condition ($record.frameSizes -is [System.Array]) `
                    "AssetProvenanceInvalid"
                Assert-ExactStringSet `
                    -Actual @($record.frameSizes | ForEach-Object { $_.ToString() }) `
                    -Expected @($expected.FrameSizes | ForEach-Object { $_.ToString() }) `
                    -Code "AssetProvenanceInvalid"
                for ($frameIndex = 0; $frameIndex -lt $expected.FrameSizes.Count; $frameIndex++) {
                    Assert-Condition `
                        (@($record.frameSizes)[$frameIndex] -is [int] -and
                         @($record.frameSizes)[$frameIndex] -eq
                            $expected.FrameSizes[$frameIndex]) `
                        "AssetProvenanceInvalid"
                }
            }

            $assetFile = Resolve-RegularRepositoryFile `
                -Root $Root `
                -RelativePath ($expected.Path.Replace('/', '\')) `
                -MaximumBytes $script:maximumSourceFileBytes `
                -Code "AssetProvenanceInvalid"
            $assetSha256 = Get-LowerSha256 -File $assetFile
            Assert-Condition `
                ($assetFile.Length -eq $record.length -and
                 $assetSha256 -ceq $record.sha256) `
                "AssetProvenanceInvalid"
            $inventoryRecord = @($AssetInventory | Where-Object {
                $_.path -ceq $expected.Path
            })
            Assert-Condition `
                ($inventoryRecord.Count -eq 1 -and
                 $inventoryRecord[0].length -eq $record.length -and
                 $inventoryRecord[0].sha256 -ceq $record.sha256) `
                "AssetProvenanceInvalid"
            $shape = Get-ProductionAssetShape `
                -File $assetFile `
                -MediaType $expected.MediaType
            if ($expected.MediaType -ceq "image/png") {
                Assert-Condition `
                    ($shape.Width -eq $expected.Width -and
                     $shape.Height -eq $expected.Height -and
                     @($shape.FrameSizes).Count -eq 0) `
                    "AssetProvenanceInvalid"
                $shapeText = "size=$($expected.Width)x$($expected.Height)"
            }
            else {
                Assert-Condition `
                    (@($shape.FrameSizes).Count -eq $expected.FrameSizes.Count) `
                    "AssetProvenanceInvalid"
                for ($frameIndex = 0; $frameIndex -lt $expected.FrameSizes.Count; $frameIndex++) {
                    Assert-Condition `
                        ($shape.FrameSizes[$frameIndex] -eq
                            $expected.FrameSizes[$frameIndex]) `
                        "AssetProvenanceInvalid"
                }
                $shapeText = "frames=" + ($expected.FrameSizes -join ',')
            }
            $canonicalLines.Add(
                "$($expected.Path)|$($expected.MediaType)|$shapeText|$($record.length)|$($record.sha256)")
        }

        $canonicalText = ($canonicalLines.ToArray() -join "`n") + "`n"
        $canonicalSha256 = Get-LowerSha256ForBytes `
            -Bytes $script:utf8NoBom.GetBytes($canonicalText)
        Assert-Condition `
            ($canonicalSha256 -ceq $ledger.canonicalAssetSetSha256) `
            "AssetProvenanceInvalid"

        $generatorFile = Resolve-RegularRepositoryFile `
            -Root $Root `
            -RelativePath ($ledger.generatorPath.Replace('/', '\')) `
            -MaximumBytes $script:maximumSourceFileBytes `
            -Code "AssetProvenanceInvalid"
        [void](Read-StrictUtf8Text `
            -File $generatorFile `
            -MaximumBytes $script:maximumSourceFileBytes `
            -Code "AssetProvenanceInvalid")
        Assert-Condition `
            ((Get-LowerSha256 -File $generatorFile) -ceq
                $ledger.generatorSha256) `
            "AssetProvenanceInvalid"
        try {
            & $generatorFile.FullName -VerifyRoot $Root 6>&1 | Out-Null
        }
        catch {
            Fail-TechnicalInvariant -Code "AssetProvenanceVerificationInvalid"
        }

        return [pscustomobject]@{
            LedgerSha256 = $ledgerSha256
            Decision = $ledger.decision
            Scope = $ledger.scope
            ProvenanceKind = $ledger.provenanceKind
            GeneratorPath = $ledger.generatorPath
            GeneratorVersion = $ledger.generatorVersion
            GeneratorSha256 = $ledger.generatorSha256
            AlgorithmVersion = $ledger.algorithmVersion
            CanonicalAssetSetSha256 = $canonicalSha256
            AssetCount = [int]$expectedAssets.Count
            DeterministicRecipeVerified = $true
            DevelopmentPlaceholderOnly = $true
            ProductionBrandApproved = $false
            CopyrightOwnershipDetermined = $false
            RedistributionDecisionComplete = $false
            LegalReviewComplete = $false
        }
    }
    catch {
        if ($_.Exception.Message -match
            '^M15TechnicalInvariant:AssetProvenance(?:Invalid|DuplicateProperty|VerificationInvalid)$') {
            throw $_.Exception.Message
        }

        Fail-TechnicalInvariant -Code "AssetProvenanceInvalid"
    }
}

function Read-PackageSbomAcceptance {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Root
    )

    try {
        Assert-NoNearestPackageVersionOverrides -Root $Root
        $packageProducingSnapshot = Get-PackageProducingSnapshot -Root $Root
        Assert-Condition `
            ($packageProducingSnapshot.FileCount -eq
                $script:packageProducingSnapshotFileCount -and
             $packageProducingSnapshot.Sha256 -ceq
                $script:packageProducingSnapshotSha256) `
            "PackageSbomAcceptanceInvalid"

        $ledgerFile = Resolve-RegularRepositoryFile `
            -Root $Root `
            -RelativePath ($script:packageSbomAcceptanceRelativePath.Replace('/', '\')) `
            -MaximumBytes $script:maximumPackageSbomAcceptanceBytes `
            -Code "PackageSbomAcceptanceInvalid"
        $ledgerStream = [System.IO.File]::Open(
            $ledgerFile.FullName,
            [System.IO.FileMode]::Open,
            [System.IO.FileAccess]::Read,
            [System.IO.FileShare]::None)
        try {
            Assert-Condition `
                ($ledgerStream.Length -gt 0 -and
                 $ledgerStream.Length -le $script:maximumPackageSbomAcceptanceBytes) `
                "PackageSbomAcceptanceInvalid"
            $ledgerBytes = New-Object byte[] ([int]$ledgerStream.Length)
            $offset = 0
            while ($offset -lt $ledgerBytes.Length) {
                $read = $ledgerStream.Read(
                    $ledgerBytes,
                    $offset,
                    $ledgerBytes.Length - $offset)
                Assert-Condition ($read -gt 0) "PackageSbomAcceptanceInvalid"
                $offset += $read
            }
            Assert-Condition ($ledgerStream.ReadByte() -eq -1) `
                "PackageSbomAcceptanceInvalid"
        }
        finally {
            $ledgerStream.Dispose()
        }
        try {
            $ledgerText = $script:utf8Strict.GetString($ledgerBytes)
        }
        catch {
            Fail-TechnicalInvariant -Code "PackageSbomAcceptanceInvalid"
        }
        Assert-NoDuplicateJsonProperties -Text $ledgerText
        Assert-Condition `
            ((Get-LowerSha256ForBytes -Bytes $ledgerBytes) -ceq
             $script:packageSbomAcceptanceSha256) `
            "PackageSbomAcceptanceInvalid"
        try {
            $acceptance = $ledgerText | ConvertFrom-Json
        }
        catch {
            Fail-TechnicalInvariant -Code "PackageSbomAcceptanceInvalid"
        }

        Assert-Condition ($null -ne $acceptance) "PackageSbomAcceptanceInvalid"
        Assert-ExactStringSet `
            -Actual @($acceptance.PSObject.Properties.Name) `
            -Expected @(
                "schemaVersion",
                "decision",
                "scope",
                "runCompletedAtUtc",
                "repository",
                "repositoryId",
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
                "documentNamespace",
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
                "remainingBlockers",
                "legalSbomComplete") `
            -Code "PackageSbomAcceptanceInvalid"

        $expectedStrings = [ordered]@{
            decision = "AcceptTechnicalPackageBoundSbom"
            scope = "TechnicalPackageBoundSbomOnly"
            runCompletedAtUtc = "2026-08-26T14:18:56Z"
            repository = "serkankaracan/iptv-suite"
            workflowPath = ".github/workflows/windows-package-sbom.yml"
            workflowName = "Windows package SBOM producer"
            runEvent = "workflow_dispatch"
            runBranch = "main"
            runHeadSha = "62b601e871ca41a6d2100dfb2375b683bbd8e0ca"
            runConclusion = "success"
            packageJobName = "Package-bound SBOM producer gate"
            packageJobConclusion = "success"
            artifactName = "windows-msix-smoke-evidence"
            artifactDigestSha256 = "79786ab5bbabde942d6f45cb9e47bbee814980be11a7817b208259d88ca03926"
            lastSuccessMemberName = "last-success.json"
            lastSuccessMemberSha256 = "39ff344dc33ecd3b943c37ec70f9d73d296726b57b3c5bb10503ab4d143895ca"
            sbomSummaryMemberName = "package-sbom-summary.json"
            sbomSummaryMemberSha256 = "7553492ee17022d73d5801ee75fee5be1230d1b85fa3c6f8071aecdd9be0cfc2"
            sbomMemberName = "package-sbom.spdx.json"
            sbomMemberSha256 = "03c29c18da6b0323c88149805e6eeef6f43d35ec329c08d2b93fc5247b04a903"
            configuration = "Release"
            dotNetSdk = "10.0.302"
            sbomFormat = "SPDX-2.2"
            documentNamespace = "https://github.com/serkankaracan/iptv-suite/sbom/IptvSuite.Windows.ReleaseSet/0.1.0.0/62b601e871ca41a6d2100dfb2375b683bbd8e0ca-2fcfbd3cd59501e605596a6e77d567979993e78d9986566964cb21a0f2229a3a-a3ce5b76713133dfd3b378e81c43a89954c664fcd70fd0c070e409ed3de03ebf"
            toolPackageId = "microsoft.sbom.dotnettool"
            toolVersion = "4.1.5"
            toolNupkgSha256 = "00e1fb81c01f4e9ad7a9d00f365bb3f3776cde6fecdd15cc3adbbce1f83d14bb"
            toolShimSha256 = "c8e151612c03db7a5b8d680cd5ccdfd1d9676f36d43c33cec2a4397fb19ada55"
            productionInputSetCanonicalSha256 = $script:packageSbomProductionInputSetSha256
            contractSourceSetCanonicalSha256 = $script:packageSbomContractSourceSetSha256
            packageProducingSnapshotSha256 = $script:packageProducingSnapshotSha256
            applicationPackageFile = "IptvSuite.Windows_0.1.0.0_x64.msix"
            applicationPackageSha256 = "2fcfbd3cd59501e605596a6e77d567979993e78d9986566964cb21a0f2229a3a"
            applicationIdentityName = "IptvSuite.LocalDev.6f0d9a64"
            applicationVersion = "0.1.0.0"
            applicationSignatureStatus = "Valid"
            runtimePackageFile = "Microsoft.WindowsAppRuntime.2.msix"
            runtimePackageSha256 = "a3ce5b76713133dfd3b378e81c43a89954c664fcd70fd0c070e409ed3de03ebf"
            runtimeIdentityName = "Microsoft.WindowsAppRuntime.2"
            runtimeVersion = "2.4.0.0"
            runtimeSignatureStatus = "Valid"
            architecture = "x64"
            producerBlockerDisposition = "HostedAcceptancePending"
            closedBlocker = "SbomPending"
        }
        foreach ($expected in $expectedStrings.GetEnumerator()) {
            Assert-Condition `
                ($acceptance.PSObject.Properties[$expected.Key].Value -is [string] -and
                 [string]$acceptance.PSObject.Properties[$expected.Key].Value -ceq
                    [string]$expected.Value) `
                "PackageSbomAcceptanceInvalid"
        }

        $expectedInt32 = [ordered]@{
            schemaVersion = 1
            repositoryId = 1328998460
            runNumber = 5
            runAttempt = 1
            artifactSizeBytes = 7740
            lastSuccessMemberLength = 18711
            sbomSummaryMemberLength = 1985
            sbomMemberLength = 50566
            productionInputCount = 10
            contractSourceCount = $script:packageSbomContractSourceCount
            packageProducingSnapshotFileCount = $script:packageProducingSnapshotFileCount
            applicationPackageLength = 29852385
            runtimePackageLength = 46787781
            fileCount = 2
            componentCount = 24
            packageCount = 27
            relationshipCount = 43
        }
        foreach ($expected in $expectedInt32.GetEnumerator()) {
            Assert-Condition `
                ($acceptance.PSObject.Properties[$expected.Key].Value -is [int] -and
                 [int]$acceptance.PSObject.Properties[$expected.Key].Value -eq
                    [int]$expected.Value) `
                "PackageSbomAcceptanceInvalid"
        }

        $expectedInt64 = [ordered]@{
            runId = [long]32978788187
            packageJobId = [long]98209973083
            artifactId = [long]9610820189
        }
        foreach ($expected in $expectedInt64.GetEnumerator()) {
            Assert-Condition `
                ($acceptance.PSObject.Properties[$expected.Key].Value -is [long] -and
                 [long]$acceptance.PSObject.Properties[$expected.Key].Value -eq
                    [long]$expected.Value) `
                "PackageSbomAcceptanceInvalid"
        }

        Assert-Condition `
            ($acceptance.officialValidationPassed -is [bool] -and
             $acceptance.officialValidationPassed -and
             $acceptance.strictValidationPassed -is [bool] -and
             $acceptance.strictValidationPassed -and
             $acceptance.producerSbomPending -is [bool] -and
             $acceptance.producerSbomPending -and
             $acceptance.legalSbomComplete -is [bool] -and
             -not $acceptance.legalSbomComplete) `
            "PackageSbomAcceptanceInvalid"

        $expectedRemainingBlockers = @(
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
            "StoreListingPending",
            "SupportUrlPending",
            "WackPending")
        Assert-Condition ($acceptance.remainingBlockers -is [System.Array]) `
            "PackageSbomAcceptanceInvalid"
        Assert-ExactStringSet `
            -Actual @($acceptance.remainingBlockers | ForEach-Object { [string]$_ }) `
            -Expected $expectedRemainingBlockers `
            -Code "PackageSbomAcceptanceInvalid"

        $contractSourcePaths = @(
            ".config/dotnet-tools.json",
            ".github/workflows/windows-package-sbom.yml",
            "eng/Invoke-WindowsPackageSbom.ps1",
            "eng/Invoke-WindowsPackageSmoke.ps1",
            "eng/WindowsPackageInstallRootAudit.ps1",
            "eng/WindowsPackageSbom.ps1",
            "eng/windows-package-sbom-tool.json")
        $productionInputPaths = @(
            "Directory.Packages.props",
            "apps/windows/src/IptvSuite.Application/IptvSuite.Application.csproj",
            "apps/windows/src/IptvSuite.Application/packages.lock.json",
            "apps/windows/src/IptvSuite.Domain/IptvSuite.Domain.csproj",
            "apps/windows/src/IptvSuite.Domain/packages.lock.json",
            "apps/windows/src/IptvSuite.Infrastructure/IptvSuite.Infrastructure.csproj",
            "apps/windows/src/IptvSuite.Infrastructure/packages.lock.json",
            "apps/windows/src/IptvSuite.Windows/IptvSuite.Windows.csproj",
            "apps/windows/src/IptvSuite.Windows/packages.lock.json",
            "global.json")
        Assert-Condition `
            ($contractSourcePaths.Count -eq $script:packageSbomContractSourceCount) `
            "PackageSbomAcceptanceInvalid"
        Assert-Condition `
            ((Get-CanonicalTextSourceSetSha256 `
                -Root $Root `
                -RelativePaths $contractSourcePaths) -ceq
                $script:packageSbomContractSourceSetSha256) `
            "PackageSbomAcceptanceInvalid"
        Assert-Condition `
            ((Get-CanonicalTextSourceSetSha256 `
                -Root $Root `
                -RelativePaths $productionInputPaths) -ceq
                $script:packageSbomProductionInputSetSha256) `
            "PackageSbomAcceptanceInvalid"
        return [pscustomobject]@{
            Acceptance = $acceptance
            PackageProducingSnapshot = $packageProducingSnapshot
        }
    }
    catch {
        if ($_.Exception.Message -match
            '^M15TechnicalInvariant:PackageSbomAcceptance(?:Invalid|DuplicateProperty)$') {
            throw $_.Exception.Message
        }

        Fail-TechnicalInvariant -Code "PackageSbomAcceptanceInvalid"
    }
}

function Read-PackageVulnerabilityAcceptance {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Root
    )

    try {
        $ledgerFile = Resolve-RegularRepositoryFile `
            -Root $Root `
            -RelativePath ($script:packageVulnerabilityAcceptanceRelativePath.Replace('/', '\')) `
            -MaximumBytes $script:maximumPackageVulnerabilityAcceptanceBytes `
            -Code "PackageVulnerabilityAcceptanceInvalid"
        [byte[]]$ledgerBytes = Read-BoundedRegularFileBytes `
            -File $ledgerFile `
            -MaximumBytes $script:maximumPackageVulnerabilityAcceptanceBytes `
            -Code "PackageVulnerabilityAcceptanceInvalid"
        try {
            $ledgerText = $script:utf8Strict.GetString($ledgerBytes)
        }
        catch {
            Fail-TechnicalInvariant -Code "PackageVulnerabilityAcceptanceInvalid"
        }
        try {
            Assert-NoDuplicateJsonProperties -Text $ledgerText
        }
        catch {
            Fail-TechnicalInvariant -Code "PackageVulnerabilityAcceptanceInvalid"
        }
        Assert-Condition `
            ((Get-LowerSha256ForBytes -Bytes $ledgerBytes) -ceq
             $script:packageVulnerabilityAcceptanceSha256) `
            "PackageVulnerabilityAcceptanceInvalid"
        try {
            $acceptance = $ledgerText | ConvertFrom-Json
        }
        catch {
            Fail-TechnicalInvariant -Code "PackageVulnerabilityAcceptanceInvalid"
        }

        Assert-Condition ($null -ne $acceptance) "PackageVulnerabilityAcceptanceInvalid"
        Assert-ExactStringSet `
            -Actual @($acceptance.PSObject.Properties.Name) `
            -Expected @(
                "schemaVersion",
                "decision",
                "scope",
                "runCompletedAtUtc",
                "freshThroughUtc",
                "freshnessPolicy",
                "maximumAgeDays",
                "repository",
                "repositoryId",
                "workflowPath",
                "workflowName",
                "workflowId",
                "runId",
                "runNumber",
                "runAttempt",
                "runEvent",
                "runBranch",
                "runHeadSha",
                "runConclusion",
                "jobId",
                "jobName",
                "jobConclusion",
                "artifactId",
                "artifactName",
                "artifactSizeBytes",
                "artifactDigestSha256",
                "lastSuccessMemberName",
                "lastSuccessMemberLength",
                "lastSuccessMemberSha256",
                "packageSbomAcceptanceSha256",
                "observedAtUtc",
                "producerResult",
                "producerScope",
                "producerRepositoryCommitSha",
                "repositoryClean",
                "dotNetSdk",
                "projectPath",
                "targetFramework",
                "outputVersion",
                "auditSource",
                "auditSourceId",
                "auditSourceConfigSha256",
                "includeTransitive",
                "noRestoreDuringList",
                "lockedRestore",
                "nuGetAuditEnabled",
                "nuGetAuditMode",
                "nuGetAuditLevel",
                "httpCacheFreshAtStart",
                "restoreProjectCount",
                "restoreSkippedCount",
                "restoreProjectsAuditedCount",
                "productionProjectCount",
                "auditSuppressionCount",
                "auditBuildOverrideCount",
                "contractSnapshotFileCount",
                "contractSnapshotCanonicalBytes",
                "contractSnapshotSha256",
                "productionLockfileCount",
                "productionPackageCount",
                "windowsLeafPackageCount",
                "topLevelPackageCount",
                "transitivePackageCount",
                "productionPackageGraphSha256",
                "inventoryOutputLength",
                "inventoryOutputSha256",
                "rawOutputLength",
                "rawOutputSha256",
                "knownDirectVulnerabilityCount",
                "knownTransitiveVulnerabilityCount",
                "knownVulnerabilityCount",
                "officialOutputValidationPassed",
                "strictValidationPassed",
                "producerCheckpointOnly",
                "producerCveReviewPending",
                "closedBlocker",
                "remainingBlockers",
                "cveFreeClaim",
                "legalReviewComplete") `
            -Code "PackageVulnerabilityAcceptanceInvalid"

        $expectedStrings = [ordered]@{
            decision = "AcceptTechnicalKnownVulnerabilityReview"
            scope = "ProductionWindowsLeafKnownVulnerabilityReviewOnly"
            runCompletedAtUtc = "2026-08-26T04:17:16Z"
            freshThroughUtc = "2026-09-02T04:17:16Z"
            freshnessPolicy = "RunCompletionPlus7Days"
            repository = "serkankaracan/iptv-suite"
            workflowPath = ".github/workflows/windows-cve-review.yml"
            workflowName = "Windows known-vulnerability producer"
            runEvent = "workflow_dispatch"
            runBranch = "main"
            runHeadSha = "ef876d103223165bf546fb60fddef102e74c2c08"
            runConclusion = "success"
            jobName = "Known-vulnerability producer gate"
            jobConclusion = "success"
            artifactName = "windows-cve-review-evidence"
            artifactDigestSha256 = "dd3425616f584993578c422123130f8737155b4c5012e477c9639a3125fb87fb"
            lastSuccessMemberName = "last-success.json"
            lastSuccessMemberSha256 = "6890351195a17c207169a86fed60b4d46d6afd2851e4fe7567e4c704d43d6bb9"
            packageSbomAcceptanceSha256 = $script:packageSbomAcceptanceSha256
            observedAtUtc = "2026-08-26T04:17:03.5506767Z"
            producerResult = "passed"
            producerScope = "ProductionWindowsLeafKnownVulnerabilityProducer"
            producerRepositoryCommitSha = "ef876d103223165bf546fb60fddef102e74c2c08"
            dotNetSdk = "10.0.302"
            projectPath = "apps/windows/src/IptvSuite.Windows/IptvSuite.Windows.csproj"
            targetFramework = "net10.0-windows10.0.26100.0"
            auditSource = "https://data.nuget.org/v3/index.json"
            auditSourceId = "nuget.org-audit-vulnerabilityinfo"
            auditSourceConfigSha256 = "a66cc28824e3eee4d9e51844bcdd00bbcdccaa27566bc4cbb269abc8644334b3"
            nuGetAuditMode = "all"
            nuGetAuditLevel = "low"
            contractSnapshotSha256 = $script:packageVulnerabilityContractSourceSetSha256
            productionPackageGraphSha256 = "760562b81e0097913e1daf4ec88c67596337dd6636ed6d88c8f645424dc50b6e"
            inventoryOutputSha256 = "6c0f18b5dd94bf82754726a18c88f330a47c86dca8939e94e943d78b1e0506c9"
            rawOutputSha256 = "532fd239ddb450511d88bf3b83a2413a2e222a148a8302d232bdb9c7ec47cc28"
            closedBlocker = "CveReviewPending"
        }
        foreach ($expected in $expectedStrings.GetEnumerator()) {
            Assert-Condition `
                ($acceptance.PSObject.Properties[$expected.Key].Value -is [string] -and
                 [string]$acceptance.PSObject.Properties[$expected.Key].Value -ceq
                    [string]$expected.Value) `
                "PackageVulnerabilityAcceptanceInvalid"
        }

        $expectedInt32 = [ordered]@{
            schemaVersion = 1
            maximumAgeDays = $script:packageVulnerabilityMaximumAgeDays
            repositoryId = 1328998460
            workflowId = 342499403
            runNumber = 18
            runAttempt = 1
            artifactSizeBytes = 1121
            lastSuccessMemberLength = 2403
            outputVersion = 1
            restoreProjectCount = 4
            restoreSkippedCount = 0
            restoreProjectsAuditedCount = 4
            productionProjectCount = 4
            auditSuppressionCount = 0
            auditBuildOverrideCount = 0
            contractSnapshotFileCount = $script:packageVulnerabilityContractSourceCount
            contractSnapshotCanonicalBytes = 98075
            productionLockfileCount = 4
            productionPackageCount = 23
            windowsLeafPackageCount = 23
            topLevelPackageCount = 2
            transitivePackageCount = 21
            inventoryOutputLength = 3471
            rawOutputLength = 427
            knownDirectVulnerabilityCount = 0
            knownTransitiveVulnerabilityCount = 0
            knownVulnerabilityCount = 0
        }
        foreach ($expected in $expectedInt32.GetEnumerator()) {
            Assert-Condition `
                ($acceptance.PSObject.Properties[$expected.Key].Value -is [int] -and
                 [int]$acceptance.PSObject.Properties[$expected.Key].Value -eq
                    [int]$expected.Value) `
                "PackageVulnerabilityAcceptanceInvalid"
        }

        $expectedInt64 = [ordered]@{
            runId = [long]32929529931
            jobId = [long]98058958334
            artifactId = [long]9592732443
        }
        foreach ($expected in $expectedInt64.GetEnumerator()) {
            Assert-Condition `
                ($acceptance.PSObject.Properties[$expected.Key].Value -is [long] -and
                 [long]$acceptance.PSObject.Properties[$expected.Key].Value -eq
                    [long]$expected.Value) `
                "PackageVulnerabilityAcceptanceInvalid"
        }

        foreach ($propertyName in @(
                "repositoryClean",
                "includeTransitive",
                "noRestoreDuringList",
                "lockedRestore",
                "nuGetAuditEnabled",
                "httpCacheFreshAtStart",
                "officialOutputValidationPassed",
                "strictValidationPassed",
                "producerCheckpointOnly",
                "producerCveReviewPending")) {
            Assert-Condition `
                ($acceptance.PSObject.Properties[$propertyName].Value -is [bool] -and
                 [bool]$acceptance.PSObject.Properties[$propertyName].Value) `
                "PackageVulnerabilityAcceptanceInvalid"
        }
        foreach ($propertyName in @("cveFreeClaim", "legalReviewComplete")) {
            Assert-Condition `
                ($acceptance.PSObject.Properties[$propertyName].Value -is [bool] -and
                 -not [bool]$acceptance.PSObject.Properties[$propertyName].Value) `
                "PackageVulnerabilityAcceptanceInvalid"
        }

        $expectedRemainingBlockers = @(
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
        Assert-Condition ($acceptance.remainingBlockers -is [System.Array]) `
            "PackageVulnerabilityAcceptanceInvalid"
        Assert-ExactStringSet `
            -Actual @($acceptance.remainingBlockers | ForEach-Object { [string]$_ }) `
            -Expected $expectedRemainingBlockers `
            -Code "PackageVulnerabilityAcceptanceInvalid"

        $contractSourcePaths = @(
            "global.json",
            "Directory.Build.props",
            "Directory.Packages.props",
            "NuGet.config",
            "eng/windows-package-vulnerability-audit.config",
            "eng/WindowsPackageVulnerabilityAudit.ps1",
            "eng/Invoke-WindowsPackageVulnerabilityAudit.ps1",
            ".github/workflows/windows-cve-review.yml",
            "apps/windows/src/IptvSuite.Domain/IptvSuite.Domain.csproj",
            "apps/windows/src/IptvSuite.Domain/packages.lock.json",
            "apps/windows/src/IptvSuite.Application/IptvSuite.Application.csproj",
            "apps/windows/src/IptvSuite.Application/packages.lock.json",
            "apps/windows/src/IptvSuite.Infrastructure/IptvSuite.Infrastructure.csproj",
            "apps/windows/src/IptvSuite.Infrastructure/packages.lock.json",
            "apps/windows/src/IptvSuite.Windows/IptvSuite.Windows.csproj",
            "apps/windows/src/IptvSuite.Windows/packages.lock.json")
        Assert-Condition `
            ($contractSourcePaths.Count -eq $script:packageVulnerabilityContractSourceCount) `
            "PackageVulnerabilityAcceptanceInvalid"
        $helperRelativePath = "eng/WindowsPackageVulnerabilityAudit.ps1"
        $helperFile = Resolve-RegularRepositoryFile `
            -Root $Root `
            -RelativePath ($helperRelativePath.Replace('/', '\')) `
            -MaximumBytes $script:maximumSourceFileBytes `
            -Code "PackageVulnerabilityAcceptanceInvalid"
        $helperText = Read-StrictUtf8Text `
            -File $helperFile `
            -MaximumBytes $script:maximumSourceFileBytes `
            -Code "PackageVulnerabilityAcceptanceInvalid"
        $normalizedHelperText =
            $helperText.Replace("`r`n", "`n").Replace("`r", "`n")
        Assert-Condition `
            ((Get-LowerSha256ForBytes `
                -Bytes $script:utf8NoBom.GetBytes($normalizedHelperText)) -ceq
             $script:packageVulnerabilityHelperSourceSha256) `
            "PackageVulnerabilityAcceptanceInvalid"

        $contractSourceSetSha256 = Get-CanonicalTextSourceSetSha256 `
            -Root $Root `
            -RelativePaths $contractSourcePaths `
            -CapturedRelativePath $helperRelativePath `
            -CapturedText $normalizedHelperText
        Assert-Condition `
            ($contractSourceSetSha256 -ceq
             $script:packageVulnerabilityContractSourceSetSha256) `
            "PackageVulnerabilityAcceptanceInvalid"

        try {
            $helperScriptBlock = [ScriptBlock]::Create($normalizedHelperText)
            . $helperScriptBlock
        }
        catch {
            Fail-TechnicalInvariant -Code "PackageVulnerabilityAcceptanceInvalid"
        }
        $buildInputPolicy = Assert-WindowsPackageVulnerabilityBuildInputPolicy `
            -RepositoryRoot $Root
        $contractSnapshot = Get-WindowsPackageVulnerabilityContractSnapshot `
            -RepositoryRoot $Root
        $packageGraph = Get-WindowsProductionPackageGraph `
            -RepositoryRoot $Root
        Assert-Condition `
            ($buildInputPolicy.ProductionProjectCount -eq 4 -and
             $buildInputPolicy.SuppressionCount -eq 0 -and
             $buildInputPolicy.BuildOverrideCount -eq 0 -and
             $contractSnapshot.FileCount -eq $acceptance.contractSnapshotFileCount -and
             $contractSnapshot.CanonicalBytes -eq $acceptance.contractSnapshotCanonicalBytes -and
             $contractSnapshot.Sha256 -ceq $acceptance.contractSnapshotSha256 -and
             $packageGraph.LockfileCount -eq $acceptance.productionLockfileCount -and
             $packageGraph.PackageCount -eq $acceptance.productionPackageCount -and
             $packageGraph.WindowsLeafPackageCount -eq $acceptance.windowsLeafPackageCount -and
             $packageGraph.Sha256 -ceq $acceptance.productionPackageGraphSha256) `
            "PackageVulnerabilityAcceptanceInvalid"

        $styles = [System.Globalization.DateTimeStyles]::AssumeUniversal -bor
            [System.Globalization.DateTimeStyles]::AdjustToUniversal
        $evaluationUtcNow = [DateTimeOffset]::UtcNow
        [DateTimeOffset]$runCompleted = [DateTimeOffset]::MinValue
        [DateTimeOffset]$freshThrough = [DateTimeOffset]::MinValue
        [DateTimeOffset]$observedAt = [DateTimeOffset]::MinValue
        Assert-Condition `
            ([DateTimeOffset]::TryParseExact(
                [string]$acceptance.runCompletedAtUtc,
                "yyyy-MM-dd'T'HH:mm:ss'Z'",
                [System.Globalization.CultureInfo]::InvariantCulture,
                $styles,
                [ref]$runCompleted)) `
            "PackageVulnerabilityAcceptanceInvalid"
        Assert-Condition `
            ([DateTimeOffset]::TryParseExact(
                [string]$acceptance.freshThroughUtc,
                "yyyy-MM-dd'T'HH:mm:ss'Z'",
                [System.Globalization.CultureInfo]::InvariantCulture,
                $styles,
                [ref]$freshThrough)) `
            "PackageVulnerabilityAcceptanceInvalid"
        Assert-Condition `
            ([DateTimeOffset]::TryParseExact(
                [string]$acceptance.observedAtUtc,
                "yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'",
                [System.Globalization.CultureInfo]::InvariantCulture,
                $styles,
                [ref]$observedAt)) `
            "PackageVulnerabilityAcceptanceInvalid"
        Assert-Condition `
            ($freshThrough -eq
                $runCompleted.AddDays($script:packageVulnerabilityMaximumAgeDays) -and
             $observedAt -le $runCompleted -and
             ($runCompleted - $observedAt) -le [TimeSpan]::FromMinutes(15) -and
             $observedAt -le $evaluationUtcNow.AddMinutes(5)) `
            "PackageVulnerabilityAcceptanceInvalid"

        return [pscustomobject]@{
            Acceptance = $acceptance
            ContractSourceSetSha256 = $contractSourceSetSha256
            FreshAtEvaluation = ($evaluationUtcNow -le $freshThrough)
            FinalReleaseFreshAtEvaluation =
                ($evaluationUtcNow -le
                    $runCompleted.AddHours(
                        $script:packageVulnerabilityFinalReleaseMaximumAgeHours))
        }
    }
    catch {
        if ($_.Exception.Message -ceq
            "M15TechnicalInvariant:PackageVulnerabilityAcceptanceInvalid") {
            throw $_.Exception.Message
        }

        Fail-TechnicalInvariant -Code "PackageVulnerabilityAcceptanceInvalid"
    }
}

function Assert-NoReparseDirectoryChain {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Root,

        [Parameter(Mandatory = $true)]
        [string]$DirectoryPath
    )

    $isRepositoryRoot = $DirectoryPath.Equals(
        $Root,
        [System.StringComparison]::OrdinalIgnoreCase)
    Assert-Condition `
        ($isRepositoryRoot -or
         (Test-PathContainedByRoot -Path $DirectoryPath -Root $Root)) `
        "EvidencePathOutsideRepository"
    $rootWithSeparator = $Root.TrimEnd(
        [System.IO.Path]::DirectorySeparatorChar,
        [System.IO.Path]::AltDirectorySeparatorChar) +
        [System.IO.Path]::DirectorySeparatorChar
    $relative = if ($isRepositoryRoot) {
        ""
    }
    else {
        $DirectoryPath.Substring($rootWithSeparator.Length)
    }
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
    $architecturePropertyNames = @(
        "Platform",
        "Platforms",
        "PlatformTarget",
        "ProcessorArchitecture",
        "ProcessorArchitectureAsPlatform",
        "RuntimeIdentifier",
        "RuntimeIdentifiers",
        "RuntimeIdentifierGraphPath",
        "AppxBundle",
        "AppxBundlePlatforms",
        "AppxPackageArchitecture",
        "AppxPackagePlatforms")
    $importControlPropertyNames = @(
        "DirectoryBuildPropsPath",
        "DirectoryBuildTargetsPath",
        "ImportDirectoryBuildProps",
        "ImportDirectoryBuildTargets",
        "_DirectoryBuildPropsFile",
        "_DirectoryBuildPropsBasePath",
        "_DirectoryBuildTargetsFile",
        "_DirectoryBuildTargetsBasePath",
        "CustomBeforeDirectoryBuildProps",
        "CustomAfterDirectoryBuildProps",
        "CustomBeforeDirectoryBuildTargets",
        "CustomAfterDirectoryBuildTargets",
        "CustomBeforeMicrosoftCommonProps",
        "CustomAfterMicrosoftCommonProps",
        "CustomBeforeMicrosoftCommonTargets",
        "CustomAfterMicrosoftCommonTargets",
        "MSBuildExtensionsPath",
        "MSBuildExtensionsPath32",
        "MSBuildExtensionsPath64",
        "MSBuildUserExtensionsPath",
        "MSBuildSDKsPath",
        "MSBuildToolsPath",
        "MSBuildToolsVersion",
        "MSBuildProjectExtensionsPath",
        "BaseIntermediateOutputPath",
        "_InitialBaseIntermediateOutputPath",
        "_InitialMSBuildProjectExtensionsPath",
        "ArtifactsPath",
        "UseArtifactsOutput",
        "UseArtifactsIntermediateOutput",
        "ArtifactsProjectName",
        "IncludeProjectNameInArtifactsPaths",
        "_ArtifactsPathSetEarly",
        "ProjectToOverrideProjectExtensionsPath",
        "ProjectExtensionsPathForSpecifiedProject",
        "ImportProjectExtensionProps",
        "ImportProjectExtensionTargets",
        "ImportUserLocationsByWildcardBeforeMicrosoftCommonProps",
        "ImportByWildcardBeforeMicrosoftCommonProps",
        "ImportUserLocationsByWildcardAfterMicrosoftCommonProps",
        "ImportByWildcardAfterMicrosoftCommonProps",
        "ImportUserLocationsByWildcardBeforeMicrosoftCommonTargets",
        "ImportByWildcardBeforeMicrosoftCommonTargets",
        "ImportUserLocationsByWildcardAfterMicrosoftCommonTargets",
        "ImportByWildcardAfterMicrosoftCommonTargets")
    $importControlPropertyNamePattern = '(?i)(?:props|targets)(?:file|basepath|path)?$'
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

        $projectRoot = Get-SingleNode `
            -Node $project `
            -XPath "/*[local-name()='Project']" `
            -Code "MsBuildArchitectureImportSurfaceInvalid"
        Assert-Condition `
            ($projectRoot.Attributes.Count -eq 1 -and
             $projectRoot.GetAttribute("Sdk") -ceq "Microsoft.NET.Sdk") `
            "MsBuildArchitectureImportSurfaceInvalid"
        Assert-Condition `
            (@($project.SelectNodes("//*[local-name()='Import' or local-name()='Sdk' or local-name()='Target' or local-name()='UsingTask']")).Count -eq 0) `
            "MsBuildArchitectureImportSurfaceInvalid"
        Assert-Condition `
            (-not (Test-Path -LiteralPath ($projectFile.FullName + ".user"))) `
            "MsBuildArchitectureImportSurfaceInvalid"

        $projectExtensionsDirectory = Join-Path $projectFile.DirectoryName "obj"
        if (Test-Path -LiteralPath $projectExtensionsDirectory) {
            $projectExtensionsDirectoryItem = Get-Item `
                -LiteralPath $projectExtensionsDirectory `
                -Force
            Assert-Condition `
                ($projectExtensionsDirectoryItem.PSIsContainer -and
                 ($projectExtensionsDirectoryItem.Attributes -band
                  [System.IO.FileAttributes]::ReparsePoint) -eq 0) `
                "MsBuildArchitectureImportSurfaceInvalid"
            $allowedProjectExtensionNames = @(
                ($projectFile.Name + ".nuget.g.props")
                ($projectFile.Name + ".nuget.g.targets"))
            $projectExtensionFiles = @(Get-ChildItem `
                -LiteralPath $projectExtensionsDirectory `
                -File `
                -Force | Where-Object {
                    $_.Name.StartsWith(
                        $projectFile.Name + ".",
                        [System.StringComparison]::OrdinalIgnoreCase) -and
                    ($_.Name.EndsWith(
                        ".props",
                        [System.StringComparison]::OrdinalIgnoreCase) -or
                     $_.Name.EndsWith(
                        ".targets",
                        [System.StringComparison]::OrdinalIgnoreCase))
                })
            foreach ($projectExtensionFile in $projectExtensionFiles) {
                $isAllowedProjectExtension = $false
                foreach ($allowedProjectExtensionName in $allowedProjectExtensionNames) {
                    if ($projectExtensionFile.Name.Equals(
                        $allowedProjectExtensionName,
                        [System.StringComparison]::OrdinalIgnoreCase)) {
                        $isAllowedProjectExtension = $true
                        break
                    }
                }
                Assert-Condition `
                    ($isAllowedProjectExtension -and
                     ($projectExtensionFile.Attributes -band
                      [System.IO.FileAttributes]::ReparsePoint) -eq 0 -and
                     $projectExtensionFile.Length -gt 0 -and
                     $projectExtensionFile.Length -le $script:maximumSourceFileBytes) `
                    "MsBuildArchitectureImportSurfaceInvalid"
            }
        }

        if (Test-Path -LiteralPath (Join-Path $resolvedRepositoryRoot ".git")) {
            $projectDirectoryRelativePath = [System.IO.Path]::GetDirectoryName(
                $contract.path).Replace('\', '/')
            $trackedProjectExtensionPathspec = `
                ":(icase)" + $projectDirectoryRelativePath + "/obj/**"
            $trackedProjectExtensions = (& git `
                -C $resolvedRepositoryRoot `
                ls-files `
                -- `
                $trackedProjectExtensionPathspec 2>$null | Out-String)
            Assert-Condition `
                ($LASTEXITCODE -eq 0 -and
                 [string]::IsNullOrWhiteSpace($trackedProjectExtensions)) `
                "MsBuildArchitectureImportSurfaceInvalid"
        }

        foreach ($importControlPropertyName in $importControlPropertyNames) {
            $matchingNodes = @($project.SelectNodes("//*") | Where-Object {
                $_.LocalName.Equals(
                    $importControlPropertyName,
                    [System.StringComparison]::OrdinalIgnoreCase)
            })
            Assert-Condition `
                ($matchingNodes.Count -eq 0) `
                "MsBuildArchitectureImportSurfaceInvalid"
        }
        $patternImportControlNodes = @($project.SelectNodes("//*") | Where-Object {
            $_.LocalName -match $importControlPropertyNamePattern
        })
        Assert-Condition `
            ($patternImportControlNodes.Count -eq 0) `
            "MsBuildArchitectureImportSurfaceInvalid"

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
    foreach ($unsupportedMultiArchitectureProperty in @("RuntimeIdentifiers", "AppxBundlePlatforms")) {
        $unsupportedNodes = @($windowsProject.SelectNodes(
            "/*[local-name()='Project']/*[local-name()='PropertyGroup']/*[local-name()='$unsupportedMultiArchitectureProperty']"))
        Assert-Condition ($unsupportedNodes.Count -eq 0) "WindowsProjectPropertyInvalid"
    }

    foreach ($architecturePropertyName in $architecturePropertyNames) {
        $matchingNodes = @($windowsProject.SelectNodes("//*") | Where-Object {
            $_.LocalName.Equals(
                $architecturePropertyName,
                [System.StringComparison]::OrdinalIgnoreCase)
        })
        $expectedCount = if ($architecturePropertyName -in @(
            "Platforms",
            "PlatformTarget",
            "RuntimeIdentifier",
            "AppxBundle")) { 1 } else { 0 }
        Assert-Condition `
            ($matchingNodes.Count -eq $expectedCount) `
            "MsBuildArchitectureImportSurfaceInvalid"
    }
    $windowsProjectDirectory = [System.IO.Path]::GetDirectoryName(
        (Join-Path $resolvedRepositoryRoot "apps\windows\src\IptvSuite.Windows\IptvSuite.Windows.csproj"))
    $applicableDirectoryBuildProps = @()
    $applicableDirectoryBuildTargets = @()
    $applicableDirectoryBuildResponseFiles = @()
    $applicableMsBuildResponseFiles = @()
    $currentDirectory = Get-Item -LiteralPath $windowsProjectDirectory -Force
    while ($true) {
        foreach ($sharedBuildFileName in @(
            "Directory.Build.props",
            "Directory.Build.targets",
            "Directory.Build.rsp",
            "MSBuild.rsp")) {
            $sharedBuildCandidate = Join-Path $currentDirectory.FullName $sharedBuildFileName
            if (Test-Path -LiteralPath $sharedBuildCandidate) {
                $relativeSharedBuildPath = Get-RelativeEvidencePath `
                    -Root $resolvedRepositoryRoot `
                    -FullPath ([System.IO.Path]::GetFullPath($sharedBuildCandidate))
                $sharedBuildFile = Resolve-RegularRepositoryFile `
                    -Root $resolvedRepositoryRoot `
                    -RelativePath $relativeSharedBuildPath `
                    -Code "MsBuildArchitectureImportSurfaceInvalid"
                if ($sharedBuildFileName -ceq "Directory.Build.props") {
                    $applicableDirectoryBuildProps += $sharedBuildFile
                }
                elseif ($sharedBuildFileName -ceq "Directory.Build.targets") {
                    $applicableDirectoryBuildTargets += $sharedBuildFile
                }
                elseif ($sharedBuildFileName -ceq "Directory.Build.rsp") {
                    $applicableDirectoryBuildResponseFiles += $sharedBuildFile
                }
                else {
                    $applicableMsBuildResponseFiles += $sharedBuildFile
                }
            }
        }

        if ($currentDirectory.FullName.Equals(
            $resolvedRepositoryRoot,
            [System.StringComparison]::OrdinalIgnoreCase)) {
            break
        }

        Assert-Condition ($null -ne $currentDirectory.Parent) "MsBuildArchitectureImportSurfaceInvalid"
        $currentDirectory = $currentDirectory.Parent
        Assert-Condition `
            ($currentDirectory.FullName.Equals(
                $resolvedRepositoryRoot,
                [System.StringComparison]::OrdinalIgnoreCase) -or
             (Test-PathContainedByRoot `
                -Path $currentDirectory.FullName `
                -Root $resolvedRepositoryRoot)) `
            "MsBuildArchitectureImportSurfaceInvalid"
    }

    $rootDirectoryBuildProps = Resolve-RegularRepositoryFile `
        -Root $resolvedRepositoryRoot `
        -RelativePath "Directory.Build.props" `
        -Code "MsBuildArchitectureImportSurfaceInvalid"
    Assert-Condition `
        ($applicableDirectoryBuildProps.Count -eq 1 -and
         $applicableDirectoryBuildProps[0].FullName.Equals(
            $rootDirectoryBuildProps.FullName,
            [System.StringComparison]::OrdinalIgnoreCase)) `
        "MsBuildArchitectureImportSurfaceInvalid"
    Assert-Condition `
        ($applicableDirectoryBuildTargets.Count -eq 0) `
        "MsBuildArchitectureImportSurfaceInvalid"
    Assert-Condition `
        ($applicableDirectoryBuildResponseFiles.Count -eq 0 -and
         $applicableMsBuildResponseFiles.Count -eq 0) `
        "MsBuildArchitectureImportSurfaceInvalid"

    $solutionDirectory = Get-Item `
        -LiteralPath (Join-Path $resolvedRepositoryRoot "apps\windows") `
        -Force
    $applicableDirectorySolutionProps = @()
    $applicableDirectorySolutionTargets = @()
    $currentDirectory = $solutionDirectory
    while ($true) {
        foreach ($solutionBuildFileName in @(
            "Directory.Solution.props",
            "Directory.Solution.targets")) {
            $solutionBuildCandidate = Join-Path $currentDirectory.FullName $solutionBuildFileName
            if (Test-Path -LiteralPath $solutionBuildCandidate) {
                $relativeSolutionBuildPath = Get-RelativeEvidencePath `
                    -Root $resolvedRepositoryRoot `
                    -FullPath ([System.IO.Path]::GetFullPath($solutionBuildCandidate))
                $solutionBuildFile = Resolve-RegularRepositoryFile `
                    -Root $resolvedRepositoryRoot `
                    -RelativePath $relativeSolutionBuildPath `
                    -Code "MsBuildArchitectureImportSurfaceInvalid"
                if ($solutionBuildFileName -ceq "Directory.Solution.props") {
                    $applicableDirectorySolutionProps += $solutionBuildFile
                }
                else {
                    $applicableDirectorySolutionTargets += $solutionBuildFile
                }
            }
        }

        if ($currentDirectory.FullName.Equals(
            $resolvedRepositoryRoot,
            [System.StringComparison]::OrdinalIgnoreCase)) {
            break
        }

        Assert-Condition ($null -ne $currentDirectory.Parent) "MsBuildArchitectureImportSurfaceInvalid"
        $currentDirectory = $currentDirectory.Parent
        Assert-Condition `
            ($currentDirectory.FullName.Equals(
                $resolvedRepositoryRoot,
                [System.StringComparison]::OrdinalIgnoreCase) -or
             (Test-PathContainedByRoot `
                -Path $currentDirectory.FullName `
                -Root $resolvedRepositoryRoot)) `
            "MsBuildArchitectureImportSurfaceInvalid"
    }

    $rootDirectorySolutionProps = Resolve-RegularRepositoryFile `
        -Root $resolvedRepositoryRoot `
        -RelativePath "Directory.Solution.props" `
        -Code "MsBuildArchitectureImportSurfaceInvalid"
    Assert-Condition `
        ($applicableDirectorySolutionProps.Count -eq 1 -and
         $applicableDirectorySolutionProps[0].FullName.Equals(
            $rootDirectorySolutionProps.FullName,
            [System.StringComparison]::OrdinalIgnoreCase) -and
         $applicableDirectorySolutionTargets.Count -eq 0) `
        "MsBuildArchitectureImportSurfaceInvalid"

    foreach ($sharedBuildRelativePath in @(
        "Directory.Build.props",
        "Directory.Packages.props",
        "Directory.Solution.props")) {
        $sharedBuildFile = Resolve-RegularRepositoryFile `
            -Root $resolvedRepositoryRoot `
            -RelativePath $sharedBuildRelativePath `
            -Code "MsBuildArchitectureImportSurfaceInvalid"
        $sharedBuildProject = Read-SafeXml `
            -Text (Read-StrictUtf8Text `
                -File $sharedBuildFile `
                -Code "MsBuildArchitectureImportSurfaceInvalid") `
            -Code "MsBuildArchitectureImportSurfaceInvalid"
        $sharedBuildRoot = Get-SingleNode `
            -Node $sharedBuildProject `
            -XPath "/*[local-name()='Project']" `
            -Code "MsBuildArchitectureImportSurfaceInvalid"
        Assert-Condition `
            ($sharedBuildRoot.Attributes.Count -eq 0) `
            "MsBuildArchitectureImportSurfaceInvalid"
        Assert-Condition `
            (@($sharedBuildProject.SelectNodes("//*[local-name()='Import' or local-name()='Sdk' or local-name()='Target' or local-name()='UsingTask']")).Count -eq 0) `
            "MsBuildArchitectureImportSurfaceInvalid"

        foreach ($protectedPropertyName in @(
            $architecturePropertyNames + $importControlPropertyNames)) {
            $matchingNodes = @($sharedBuildProject.SelectNodes("//*") | Where-Object {
                $_.LocalName.Equals(
                    $protectedPropertyName,
                    [System.StringComparison]::OrdinalIgnoreCase)
            })
            Assert-Condition `
                ($matchingNodes.Count -eq 0) `
                "MsBuildArchitectureImportSurfaceInvalid"
        }
        $patternImportControlNodes = @($sharedBuildProject.SelectNodes("//*") | Where-Object {
            $_.LocalName -match $importControlPropertyNamePattern
        })
        Assert-Condition `
            ($patternImportControlNodes.Count -eq 0) `
            "MsBuildArchitectureImportSurfaceInvalid"
    }

    $sourceControlledArchitectureImportSurfacePassed = $true
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

    $script:technicalStage = "ProductionAssetProvenance"
    $assetProvenance = Read-ProductionAssetProvenance `
        -Root $resolvedRepositoryRoot `
        -AssetInventory $assets

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

    $script:technicalStage = "PackageSbomAcceptance"
    $packageSbomAcceptanceValidation = Read-PackageSbomAcceptance `
        -Root $resolvedRepositoryRoot
    $packageSbomAcceptance = $packageSbomAcceptanceValidation.Acceptance
    $validatedPackageProducingSnapshot =
        $packageSbomAcceptanceValidation.PackageProducingSnapshot

    $script:technicalStage = "PackageVulnerabilityAcceptance"
    $packageVulnerabilityAcceptanceValidation = Read-PackageVulnerabilityAcceptance `
        -Root $resolvedRepositoryRoot
    $packageVulnerabilityAcceptance =
        $packageVulnerabilityAcceptanceValidation.Acceptance
    $packageVulnerabilityFreshAtEvaluation =
        $packageVulnerabilityAcceptanceValidation.FreshAtEvaluation
    $packageVulnerabilityFinalReleaseFreshAtEvaluation =
        $packageVulnerabilityAcceptanceValidation.FinalReleaseFreshAtEvaluation
    Assert-Condition `
        (-not $packageVulnerabilityFinalReleaseFreshAtEvaluation -or
         $packageVulnerabilityFreshAtEvaluation) `
        "PackageVulnerabilityAcceptanceInvalid"

    $script:technicalStage = "EvidenceComposition"
    $baseBlockers = @(
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
        "StoreListingPending",
        "SupportUrlPending",
        "WackPending"
    )
    $effectiveBlockers = if ($packageVulnerabilityFinalReleaseFreshAtEvaluation) {
        @($baseBlockers | Where-Object { $_ -cne "CveReviewPending" })
    }
    else {
        @($baseBlockers)
    }
    $blockers = Get-OrdinalSortedStrings -Values $effectiveBlockers
    Assert-Condition `
        ($blockers.Count -eq $(if ($packageVulnerabilityFinalReleaseFreshAtEvaluation) { 12 } else { 13 })) `
        "PackageVulnerabilityAcceptanceInvalid"

    $summary = [ordered]@{
        schemaVersion = 6
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
            releaseArchitectures = @("x64")
            arm64Disposition = "DeferredUntilNativeArm64ChainAccepted"
            architectureImportSurfaceAuditVersion = 1
            sourceControlledArchitectureImportSurfacePassed = $sourceControlledArchitectureImportSurfacePassed
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
        assetProvenance = [ordered]@{
            ledgerSha256 = $assetProvenance.LedgerSha256
            decision = $assetProvenance.Decision
            scope = $assetProvenance.Scope
            provenanceKind = $assetProvenance.ProvenanceKind
            generatorPath = $assetProvenance.GeneratorPath
            generatorVersion = $assetProvenance.GeneratorVersion
            generatorSha256 = $assetProvenance.GeneratorSha256
            algorithmVersion = $assetProvenance.AlgorithmVersion
            canonicalAssetSetSha256 = $assetProvenance.CanonicalAssetSetSha256
            assetCount = $assetProvenance.AssetCount
            deterministicRecipeVerified =
                $assetProvenance.DeterministicRecipeVerified
            sourceAssetDependencyCount = 0
            thirdPartyAssetInputCount = 0
            fontInputCount = 0
            textInputCount = 0
            trademarkInputCount = 0
            developmentPlaceholderOnly =
                $assetProvenance.DevelopmentPlaceholderOnly
            productionBrandApproved =
                $assetProvenance.ProductionBrandApproved
            copyrightOwnershipDetermined =
                $assetProvenance.CopyrightOwnershipDetermined
            redistributionDecisionComplete =
                $assetProvenance.RedistributionDecisionComplete
            legalReviewComplete = $assetProvenance.LegalReviewComplete
        }
        lockfiles = @($lockfileEvidence)
        packageInventory = @($packageInventory)
        packageInventoryPolicy = [ordered]@{
            mode = "exact-current-production-package-names"
            expectedPackageCount = 23
            exactPackageNamesLocked = $true
            legalSbomComplete = $false
        }
        packageSbomAcceptance = [ordered]@{
            decision = $packageSbomAcceptance.decision
            scope = $packageSbomAcceptance.scope
            runCompletedAtUtc = $packageSbomAcceptance.runCompletedAtUtc
            repository = $packageSbomAcceptance.repository
            workflowPath = $packageSbomAcceptance.workflowPath
            workflowName = $packageSbomAcceptance.workflowName
            runId = $packageSbomAcceptance.runId
            runNumber = $packageSbomAcceptance.runNumber
            runAttempt = $packageSbomAcceptance.runAttempt
            runEvent = $packageSbomAcceptance.runEvent
            runBranch = $packageSbomAcceptance.runBranch
            runHeadSha = $packageSbomAcceptance.runHeadSha
            runConclusion = $packageSbomAcceptance.runConclusion
            packageJobId = $packageSbomAcceptance.packageJobId
            packageJobName = $packageSbomAcceptance.packageJobName
            packageJobConclusion = $packageSbomAcceptance.packageJobConclusion
            artifactId = $packageSbomAcceptance.artifactId
            artifactName = $packageSbomAcceptance.artifactName
            artifactSizeBytes = $packageSbomAcceptance.artifactSizeBytes
            artifactDigestSha256 = $packageSbomAcceptance.artifactDigestSha256
            lastSuccessMemberName = $packageSbomAcceptance.lastSuccessMemberName
            lastSuccessMemberLength = $packageSbomAcceptance.lastSuccessMemberLength
            lastSuccessMemberSha256 = $packageSbomAcceptance.lastSuccessMemberSha256
            sbomSummaryMemberName = $packageSbomAcceptance.sbomSummaryMemberName
            sbomSummaryMemberLength = $packageSbomAcceptance.sbomSummaryMemberLength
            sbomSummaryMemberSha256 = $packageSbomAcceptance.sbomSummaryMemberSha256
            sbomMemberName = $packageSbomAcceptance.sbomMemberName
            sbomMemberLength = $packageSbomAcceptance.sbomMemberLength
            sbomMemberSha256 = $packageSbomAcceptance.sbomMemberSha256
            configuration = $packageSbomAcceptance.configuration
            dotNetSdk = $packageSbomAcceptance.dotNetSdk
            sbomFormat = $packageSbomAcceptance.sbomFormat
            toolPackageId = $packageSbomAcceptance.toolPackageId
            toolVersion = $packageSbomAcceptance.toolVersion
            toolNupkgSha256 = $packageSbomAcceptance.toolNupkgSha256
            toolShimSha256 = $packageSbomAcceptance.toolShimSha256
            officialValidationPassed = $packageSbomAcceptance.officialValidationPassed
            strictValidationPassed = $packageSbomAcceptance.strictValidationPassed
            productionInputCount = $packageSbomAcceptance.productionInputCount
            productionInputSetCanonicalSha256 = $packageSbomAcceptance.productionInputSetCanonicalSha256
            contractSourceCount = $packageSbomAcceptance.contractSourceCount
            contractSourceSetCanonicalSha256 = $packageSbomAcceptance.contractSourceSetCanonicalSha256
            packageProducingSnapshotFileCount = $packageSbomAcceptance.packageProducingSnapshotFileCount
            packageProducingSnapshotSha256 = $packageSbomAcceptance.packageProducingSnapshotSha256
            applicationPackageFile = $packageSbomAcceptance.applicationPackageFile
            applicationPackageLength = $packageSbomAcceptance.applicationPackageLength
            applicationPackageSha256 = $packageSbomAcceptance.applicationPackageSha256
            applicationIdentityName = $packageSbomAcceptance.applicationIdentityName
            applicationVersion = $packageSbomAcceptance.applicationVersion
            applicationSignatureStatus = $packageSbomAcceptance.applicationSignatureStatus
            runtimePackageFile = $packageSbomAcceptance.runtimePackageFile
            runtimePackageLength = $packageSbomAcceptance.runtimePackageLength
            runtimePackageSha256 = $packageSbomAcceptance.runtimePackageSha256
            runtimeIdentityName = $packageSbomAcceptance.runtimeIdentityName
            runtimeVersion = $packageSbomAcceptance.runtimeVersion
            runtimeSignatureStatus = $packageSbomAcceptance.runtimeSignatureStatus
            architecture = $packageSbomAcceptance.architecture
            fileCount = $packageSbomAcceptance.fileCount
            componentCount = $packageSbomAcceptance.componentCount
            packageCount = $packageSbomAcceptance.packageCount
            relationshipCount = $packageSbomAcceptance.relationshipCount
            producerBlockerDisposition = $packageSbomAcceptance.producerBlockerDisposition
            producerSbomPending = $packageSbomAcceptance.producerSbomPending
            closedBlocker = "SbomPending"
            legalSbomComplete = $false
        }
        packageVulnerabilityAcceptance = [ordered]@{
            ledgerSha256 = $script:packageVulnerabilityAcceptanceSha256
            decision = $packageVulnerabilityAcceptance.decision
            scope = $packageVulnerabilityAcceptance.scope
            runCompletedAtUtc = $packageVulnerabilityAcceptance.runCompletedAtUtc
            freshThroughUtc = $packageVulnerabilityAcceptance.freshThroughUtc
            freshnessPolicy = $packageVulnerabilityAcceptance.freshnessPolicy
            maximumAgeDays = $packageVulnerabilityAcceptance.maximumAgeDays
            freshAtEvaluation = $packageVulnerabilityFreshAtEvaluation
            finalReleaseMaximumAgeHours =
                $script:packageVulnerabilityFinalReleaseMaximumAgeHours
            finalReleaseFreshAtEvaluation =
                $packageVulnerabilityFinalReleaseFreshAtEvaluation
            repository = $packageVulnerabilityAcceptance.repository
            workflowPath = $packageVulnerabilityAcceptance.workflowPath
            workflowName = $packageVulnerabilityAcceptance.workflowName
            workflowId = $packageVulnerabilityAcceptance.workflowId
            runId = $packageVulnerabilityAcceptance.runId
            runNumber = $packageVulnerabilityAcceptance.runNumber
            runAttempt = $packageVulnerabilityAcceptance.runAttempt
            runHeadSha = $packageVulnerabilityAcceptance.runHeadSha
            runConclusion = $packageVulnerabilityAcceptance.runConclusion
            jobId = $packageVulnerabilityAcceptance.jobId
            jobName = $packageVulnerabilityAcceptance.jobName
            jobConclusion = $packageVulnerabilityAcceptance.jobConclusion
            artifactId = $packageVulnerabilityAcceptance.artifactId
            artifactName = $packageVulnerabilityAcceptance.artifactName
            artifactDigestSha256 = $packageVulnerabilityAcceptance.artifactDigestSha256
            lastSuccessMemberLength = $packageVulnerabilityAcceptance.lastSuccessMemberLength
            lastSuccessMemberSha256 = $packageVulnerabilityAcceptance.lastSuccessMemberSha256
            packageSbomAcceptanceSha256 =
                $packageVulnerabilityAcceptance.packageSbomAcceptanceSha256
            observedAtUtc = $packageVulnerabilityAcceptance.observedAtUtc
            producerRepositoryCommitSha =
                $packageVulnerabilityAcceptance.producerRepositoryCommitSha
            dotNetSdk = $packageVulnerabilityAcceptance.dotNetSdk
            projectPath = $packageVulnerabilityAcceptance.projectPath
            targetFramework = $packageVulnerabilityAcceptance.targetFramework
            auditSourceId = $packageVulnerabilityAcceptance.auditSourceId
            auditSourceConfigSha256 =
                $packageVulnerabilityAcceptance.auditSourceConfigSha256
            restoreProjectCount = $packageVulnerabilityAcceptance.restoreProjectCount
            restoreSkippedCount = $packageVulnerabilityAcceptance.restoreSkippedCount
            restoreProjectsAuditedCount =
                $packageVulnerabilityAcceptance.restoreProjectsAuditedCount
            productionProjectCount = $packageVulnerabilityAcceptance.productionProjectCount
            productionLockfileCount = $packageVulnerabilityAcceptance.productionLockfileCount
            productionPackageCount = $packageVulnerabilityAcceptance.productionPackageCount
            topLevelPackageCount = $packageVulnerabilityAcceptance.topLevelPackageCount
            transitivePackageCount = $packageVulnerabilityAcceptance.transitivePackageCount
            contractSnapshotSha256 =
                $packageVulnerabilityAcceptance.contractSnapshotSha256
            productionPackageGraphSha256 =
                $packageVulnerabilityAcceptance.productionPackageGraphSha256
            knownDirectVulnerabilityCount =
                $packageVulnerabilityAcceptance.knownDirectVulnerabilityCount
            knownTransitiveVulnerabilityCount =
                $packageVulnerabilityAcceptance.knownTransitiveVulnerabilityCount
            knownVulnerabilityCount =
                $packageVulnerabilityAcceptance.knownVulnerabilityCount
            officialOutputValidationPassed =
                $packageVulnerabilityAcceptance.officialOutputValidationPassed
            strictValidationPassed =
                $packageVulnerabilityAcceptance.strictValidationPassed
            producerCheckpointOnly =
                $packageVulnerabilityAcceptance.producerCheckpointOnly
            producerCveReviewPending =
                $packageVulnerabilityAcceptance.producerCveReviewPending
            effectiveClosedBlocker = if ($packageVulnerabilityFinalReleaseFreshAtEvaluation) {
                "CveReviewPending"
            }
            else {
                "None"
            }
            cveFreeClaim = $false
            legalReviewComplete = $false
        }
        blockers = @($blockers)
    }

    $script:technicalStage = "ProductionAssetProvenanceStability"
    $publicationAssetProvenance = Read-ProductionAssetProvenance `
        -Root $resolvedRepositoryRoot `
        -AssetInventory $assets
    Assert-Condition `
        ($publicationAssetProvenance.LedgerSha256 -ceq
            $assetProvenance.LedgerSha256 -and
         $publicationAssetProvenance.GeneratorSha256 -ceq
            $assetProvenance.GeneratorSha256 -and
         $publicationAssetProvenance.CanonicalAssetSetSha256 -ceq
            $assetProvenance.CanonicalAssetSetSha256 -and
         $publicationAssetProvenance.AssetCount -eq
            $assetProvenance.AssetCount -and
         $publicationAssetProvenance.DeterministicRecipeVerified) `
        "AssetProvenanceInvalid"

    $script:technicalStage = "PackageSbomAcceptanceStability"
    Assert-NoNearestPackageVersionOverrides -Root $resolvedRepositoryRoot
    $publicationPackageProducingSnapshot = Get-PackageProducingSnapshot `
        -Root $resolvedRepositoryRoot
    Assert-Condition `
        ($publicationPackageProducingSnapshot.FileCount -eq
            $validatedPackageProducingSnapshot.FileCount -and
         $publicationPackageProducingSnapshot.CanonicalBytes -eq
            $validatedPackageProducingSnapshot.CanonicalBytes -and
         $publicationPackageProducingSnapshot.Sha256 -ceq
            $validatedPackageProducingSnapshot.Sha256) `
        "PackageSbomAcceptanceInvalid"

    $script:technicalStage = "PackageVulnerabilityAcceptanceStability"
    $publicationPackageVulnerabilityValidation = Read-PackageVulnerabilityAcceptance `
        -Root $resolvedRepositoryRoot
    Assert-Condition `
        ($publicationPackageVulnerabilityValidation.ContractSourceSetSha256 -ceq
            $packageVulnerabilityAcceptanceValidation.ContractSourceSetSha256 -and
         $publicationPackageVulnerabilityValidation.FreshAtEvaluation -eq
            $packageVulnerabilityFreshAtEvaluation -and
         $publicationPackageVulnerabilityValidation.FinalReleaseFreshAtEvaluation -eq
            $packageVulnerabilityFinalReleaseFreshAtEvaluation) `
        "PackageVulnerabilityAcceptanceInvalid"

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
