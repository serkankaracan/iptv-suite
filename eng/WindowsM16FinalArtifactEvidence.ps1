Set-StrictMode -Version Latest

$script:m16FinalMaximumInputBytes = 128KB
$script:m16FinalMaximumAggregateInputBytes = 256KB
$script:m16FinalMaximumOutputBytes = 64KB
$script:m16FinalMaximumJsonDepth = 8
$script:m16FinalMaximumJsonNodeCount = 512
$script:m16FinalMaximumObjectPropertyCount = 32
$script:m16FinalMaximumArrayLength = 4
$script:m16FinalMaximumStringLength = 256
$script:m16FinalMaximumSurfaceFileCount = 25000
$script:m16FinalMaximumSurfaceDirectoryCount = 25000
$script:m16FinalMaximumSurfaceFileBytes = 8GB
$script:m16FinalMaximumAggregateFileBytes = 32GB
$script:m16FinalUtf8Strict = New-Object System.Text.UTF8Encoding($false, $true)
$script:m16FinalUtf8NoBom = New-Object System.Text.UTF8Encoding($false)

function Fail-WindowsM16FinalArtifactEvidence {
    param(
        [Parameter(Mandatory = $true)]
        [ValidatePattern('\A[A-Za-z][A-Za-z0-9]+\z')]
        [string]$Code
    )

    throw "M16FinalArtifactEvidence:$Code"
}

function Assert-WindowsM16FinalCondition {
    param(
        [Parameter(Mandatory = $true)]
        [bool]$Condition,

        [Parameter(Mandatory = $true)]
        [string]$Code
    )

    if (-not $Condition) {
        Fail-WindowsM16FinalArtifactEvidence -Code $Code
    }
}

function Get-WindowsM16FinalLowerSha256 {
    param(
        [Parameter(Mandatory = $true)]
        [byte[]]$Bytes
    )

    $sha256 = [System.Security.Cryptography.SHA256]::Create()
    try {
        return ([System.BitConverter]::ToString(
                $sha256.ComputeHash($Bytes))).Replace("-", "").ToLowerInvariant()
    }
    finally {
        $sha256.Dispose()
    }
}

function Test-WindowsM16FinalPathContainedByRoot {
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

function Assert-WindowsM16FinalNoAlternateDataStream {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path,

        [Parameter(Mandatory = $true)]
        [string]$Code
    )

    try {
        $root = [System.IO.Path]::GetPathRoot($Path)
        Assert-WindowsM16FinalCondition `
            (-not [string]::IsNullOrWhiteSpace($root)) `
            $Code
        Assert-WindowsM16FinalCondition `
            ($Path.Substring($root.Length).IndexOf(':') -lt 0) `
            $Code
    }
    catch {
        if ($_.Exception.Message -cmatch '\AM16FinalArtifactEvidence:[A-Za-z][A-Za-z0-9]+\z') {
            throw $_.Exception.Message
        }
        Fail-WindowsM16FinalArtifactEvidence -Code $Code
    }
}

function Assert-WindowsM16FinalNtfsVolume {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path,

        [Parameter(Mandatory = $true)]
        [string]$Code
    )

    try {
        $fullPath = [System.IO.Path]::GetFullPath($Path)
        $pathRoot = [System.IO.Path]::GetPathRoot($fullPath)
        Assert-WindowsM16FinalCondition `
            (-not [string]::IsNullOrWhiteSpace($pathRoot)) `
            $Code
        $drive = New-Object System.IO.DriveInfo($pathRoot)
        Assert-WindowsM16FinalCondition `
            ($drive.IsReady -and $drive.DriveFormat -ceq "NTFS") `
            $Code
    }
    catch {
        if ($_.Exception.Message -cmatch '\AM16FinalArtifactEvidence:[A-Za-z][A-Za-z0-9]+\z') {
            throw $_.Exception.Message
        }
        Fail-WindowsM16FinalArtifactEvidence -Code $Code
    }
}

function Assert-WindowsM16FinalNoNamedStreams {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path,

        [Parameter(Mandatory = $true)]
        [string]$Code
    )

    try {
        $fullPath = [System.IO.Path]::GetFullPath($Path)
        Assert-WindowsM16FinalNtfsVolume -Path $fullPath -Code $Code
        Assert-WindowsM16FinalCondition `
            (Test-Path -LiteralPath $fullPath -PathType Leaf) `
            $Code
        $file = Get-Item -LiteralPath $fullPath -Force -ErrorAction Stop
        $streams = @(Get-Item -LiteralPath $fullPath -Stream * -ErrorAction Stop)
        Assert-WindowsM16FinalCondition ($streams.Count -eq 1) $Code
        $stream = $streams[0]
        Assert-WindowsM16FinalCondition `
            ($stream.Stream -is [string] -and
             $stream.Stream -ceq ':$DATA' -and
             ($stream.Length -is [int64] -or $stream.Length -is [int32]) -and
             [long]$stream.Length -eq [long]$file.Length) `
            $Code
    }
    catch {
        if ($_.Exception.Message -cmatch '\AM16FinalArtifactEvidence:[A-Za-z][A-Za-z0-9]+\z') {
            throw $_.Exception.Message
        }
        Fail-WindowsM16FinalArtifactEvidence -Code $Code
    }
}

function Assert-WindowsM16FinalNoReparseDirectoryChain {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Root,

        [Parameter(Mandatory = $true)]
        [string]$DirectoryPath,

        [Parameter(Mandatory = $true)]
        [string]$Code
    )

    $isRoot = $DirectoryPath.Equals(
        $Root,
        [System.StringComparison]::OrdinalIgnoreCase)
    Assert-WindowsM16FinalCondition `
        ($isRoot -or (Test-WindowsM16FinalPathContainedByRoot -Path $DirectoryPath -Root $Root)) `
        $Code

    $rootItem = Get-Item -LiteralPath $Root -Force -ErrorAction Stop
    Assert-WindowsM16FinalCondition `
        ($rootItem.PSIsContainer -and
         (($rootItem.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -eq 0)) `
        $Code

    $rootWithSeparator = $Root.TrimEnd(
        [System.IO.Path]::DirectorySeparatorChar,
        [System.IO.Path]::AltDirectorySeparatorChar) +
        [System.IO.Path]::DirectorySeparatorChar
    $relative = if ($isRoot) { "" } else { $DirectoryPath.Substring($rootWithSeparator.Length) }
    $current = $Root
    foreach ($part in @($relative.Split(
                @('\', '/'),
                [System.StringSplitOptions]::RemoveEmptyEntries))) {
        $current = Join-Path $current $part
        if (Test-Path -LiteralPath $current) {
            $item = Get-Item -LiteralPath $current -Force -ErrorAction Stop
            Assert-WindowsM16FinalCondition `
                ($item.PSIsContainer -and
                 (($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -eq 0)) `
                $Code
        }
    }
}

function Read-WindowsM16FinalRegularFileBytes {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path,

        [Parameter(Mandatory = $true)]
        [string]$Root,

        [Parameter(Mandatory = $true)]
        [long]$MaximumBytes,

        [Parameter(Mandatory = $true)]
        [string]$Code
    )

    try {
        $fullRoot = [System.IO.Path]::GetFullPath($Root)
        $fullPath = [System.IO.Path]::GetFullPath($Path)
        Assert-WindowsM16FinalNoAlternateDataStream -Path $fullRoot -Code $Code
        Assert-WindowsM16FinalNoAlternateDataStream -Path $fullPath -Code $Code
        Assert-WindowsM16FinalCondition `
            (Test-WindowsM16FinalPathContainedByRoot -Path $fullPath -Root $fullRoot) `
            $Code
        Assert-WindowsM16FinalCondition `
            (Test-Path -LiteralPath $fullRoot -PathType Container) `
            $Code
        Assert-WindowsM16FinalNoReparseDirectoryChain `
            -Root ([System.IO.Path]::GetPathRoot($fullRoot)) `
            -DirectoryPath $fullRoot `
            -Code $Code
        Assert-WindowsM16FinalNoReparseDirectoryChain `
            -Root $fullRoot `
            -DirectoryPath ([System.IO.Path]::GetDirectoryName($fullPath)) `
            -Code $Code
        Assert-WindowsM16FinalCondition `
            (Test-Path -LiteralPath $fullPath -PathType Leaf) `
            $Code
        $item = Get-Item -LiteralPath $fullPath -Force -ErrorAction Stop
        Assert-WindowsM16FinalCondition `
            (-not $item.PSIsContainer -and
             (($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -eq 0)) `
            $Code
        Assert-WindowsM16FinalNoNamedStreams -Path $fullPath -Code $Code

        $stream = [System.IO.File]::Open(
            $fullPath,
            [System.IO.FileMode]::Open,
            [System.IO.FileAccess]::Read,
            [System.IO.FileShare]::None)
        try {
            Assert-WindowsM16FinalCondition `
                ($MaximumBytes -gt 0 -and
                 $MaximumBytes -le [int]::MaxValue -and
                 $stream.Length -gt 0 -and
                 $stream.Length -le $MaximumBytes -and
                 $stream.Length -eq $item.Length) `
                $Code
            $bytes = New-Object byte[] ([int]$stream.Length)
            $offset = 0
            while ($offset -lt $bytes.Length) {
                $read = $stream.Read($bytes, $offset, $bytes.Length - $offset)
                Assert-WindowsM16FinalCondition ($read -gt 0) $Code
                $offset += $read
            }
            Assert-WindowsM16FinalCondition ($stream.ReadByte() -eq -1) $Code
            return ,$bytes
        }
        finally {
            $stream.Dispose()
        }
    }
    catch {
        if ($_.Exception.Message -cmatch '\AM16FinalArtifactEvidence:[A-Za-z][A-Za-z0-9]+\z') {
            throw $_.Exception.Message
        }
        Fail-WindowsM16FinalArtifactEvidence -Code $Code
    }
}

function Assert-WindowsM16FinalNoDuplicateJsonProperties {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Text
    )

    $sets = New-Object System.Collections.Stack
    $index = 0
    while ($index -lt $Text.Length) {
        $character = $Text[$index]
        if ($character -eq [char]0x7b) {
            $properties = New-Object 'System.Collections.Generic.HashSet[string]' `
                ([System.StringComparer]::OrdinalIgnoreCase)
            $sets.Push($properties)
            $index++
            continue
        }
        if ($character -eq [char]0x7d) {
            Assert-WindowsM16FinalCondition ($sets.Count -gt 0) "InputDuplicateProperty"
            [void]$sets.Pop()
            $index++
            continue
        }
        if ($character -ne [char]0x22) {
            $index++
            continue
        }

        $index++
        $builder = New-Object System.Text.StringBuilder
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
                Assert-WindowsM16FinalCondition ($index -lt $Text.Length) "InputDuplicateProperty"
                $escape = $Text[$index]
                switch ($escape) {
                    '"' { [void]$builder.Append([char]0x22) }
                    '\' { [void]$builder.Append([char]0x5c) }
                    '/' { [void]$builder.Append([char]0x2f) }
                    'b' { [void]$builder.Append([char]0x08) }
                    'f' { [void]$builder.Append([char]0x0c) }
                    'n' { [void]$builder.Append([char]0x0a) }
                    'r' { [void]$builder.Append([char]0x0d) }
                    't' { [void]$builder.Append([char]0x09) }
                    'u' {
                        Assert-WindowsM16FinalCondition (($index + 4) -lt $Text.Length) "InputDuplicateProperty"
                        $hex = $Text.Substring($index + 1, 4)
                        Assert-WindowsM16FinalCondition ($hex -cmatch '\A[0-9A-Fa-f]{4}\z') "InputDuplicateProperty"
                        [void]$builder.Append([char][Convert]::ToInt32($hex, 16))
                        $index += 4
                    }
                    default { Fail-WindowsM16FinalArtifactEvidence -Code "InputDuplicateProperty" }
                }
                $index++
                continue
            }

            Assert-WindowsM16FinalCondition ([int]$stringCharacter -ge 0x20) "InputDuplicateProperty"
            [void]$builder.Append($stringCharacter)
            $index++
        }

        Assert-WindowsM16FinalCondition $closed "InputDuplicateProperty"
        $lookAhead = $index
        while ($lookAhead -lt $Text.Length -and [char]::IsWhiteSpace($Text[$lookAhead])) {
            $lookAhead++
        }
        if ($lookAhead -lt $Text.Length -and $Text[$lookAhead] -eq [char]0x3a) {
            Assert-WindowsM16FinalCondition ($sets.Count -gt 0) "InputDuplicateProperty"
            if (-not $sets.Peek().Add($builder.ToString())) {
                Fail-WindowsM16FinalArtifactEvidence -Code "InputDuplicateProperty"
            }
        }
    }
    Assert-WindowsM16FinalCondition ($sets.Count -eq 0) "InputDuplicateProperty"
}

function Assert-WindowsM16FinalJsonBounds {
    param(
        $Value,
        [int]$Depth = 1,
        [Parameter(Mandatory = $true)][ref]$NodeCount
    )

    Assert-WindowsM16FinalCondition ($Depth -le $script:m16FinalMaximumJsonDepth) "InputBoundsInvalid"
    $NodeCount.Value++
    Assert-WindowsM16FinalCondition `
        ($NodeCount.Value -le $script:m16FinalMaximumJsonNodeCount) `
        "InputBoundsInvalid"
    if ($null -eq $Value) { return }
    if ($Value -is [string]) {
        Assert-WindowsM16FinalCondition `
            ($Value.Length -le $script:m16FinalMaximumStringLength) `
            "InputBoundsInvalid"
        return
    }
    if ($Value -is [bool] -or
        $Value -is [int32] -or
        $Value -is [int64] -or
        $Value -is [double] -or
        $Value -is [decimal]) { return }
    if ($Value -is [System.Array] -or $Value -is [System.Collections.IList]) {
        $values = @($Value)
        Assert-WindowsM16FinalCondition `
            ($values.Count -le $script:m16FinalMaximumArrayLength) `
            "InputBoundsInvalid"
        foreach ($item in $values) {
            Assert-WindowsM16FinalJsonBounds `
                -Value $item `
                -Depth ($Depth + 1) `
                -NodeCount $NodeCount
        }
        return
    }
    if ($Value -is [pscustomobject]) {
        $properties = @($Value.PSObject.Properties)
        Assert-WindowsM16FinalCondition `
            ($properties.Count -le $script:m16FinalMaximumObjectPropertyCount) `
            "InputBoundsInvalid"
        foreach ($property in $properties) {
            Assert-WindowsM16FinalJsonBounds `
                -Value $property.Value `
                -Depth ($Depth + 1) `
                -NodeCount $NodeCount
        }
        return
    }
    Fail-WindowsM16FinalArtifactEvidence -Code "InputBoundsInvalid"
}

function Read-WindowsM16FinalStrictJson {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$InputRoot
    )

    [byte[]]$first = Read-WindowsM16FinalRegularFileBytes `
        -Path $Path -Root $InputRoot `
        -MaximumBytes $script:m16FinalMaximumInputBytes -Code "InputFileInvalid"
    [byte[]]$second = Read-WindowsM16FinalRegularFileBytes `
        -Path $Path -Root $InputRoot `
        -MaximumBytes $script:m16FinalMaximumInputBytes -Code "InputFileInvalid"
    $firstHash = Get-WindowsM16FinalLowerSha256 -Bytes $first
    Assert-WindowsM16FinalCondition `
        ($first.Length -eq $second.Length -and
         $firstHash -ceq (Get-WindowsM16FinalLowerSha256 -Bytes $second)) `
        "InputChanged"
    Assert-WindowsM16FinalCondition `
        (-not ($first.Length -ge 3 -and
               $first[0] -eq 0xef -and $first[1] -eq 0xbb -and $first[2] -eq 0xbf)) `
        "InputEncodingInvalid"
    try { $text = $script:m16FinalUtf8Strict.GetString($first) }
    catch { Fail-WindowsM16FinalArtifactEvidence -Code "InputEncodingInvalid" }
    Assert-WindowsM16FinalNoDuplicateJsonProperties -Text $text
    try { $value = $text | ConvertFrom-Json -ErrorAction Stop }
    catch { Fail-WindowsM16FinalArtifactEvidence -Code "InputJsonInvalid" }
    Assert-WindowsM16FinalCondition `
        ($null -ne $value -and $value -is [pscustomobject]) `
        "InputJsonInvalid"
    $nodeCount = 0
    Assert-WindowsM16FinalJsonBounds -Value $value -NodeCount ([ref]$nodeCount)
    return [pscustomobject]@{
        Value = $value
        ByteLength = [long]$first.Length
        Sha256 = $firstHash
    }
}

function Assert-WindowsM16FinalExactPropertySet {
    param(
        [Parameter(Mandatory = $true)][pscustomobject]$Value,
        [Parameter(Mandatory = $true)][string[]]$Expected
    )

    $actual = New-Object 'System.Collections.Generic.HashSet[string]' `
        ([System.StringComparer]::Ordinal)
    foreach ($property in @($Value.PSObject.Properties)) {
        Assert-WindowsM16FinalCondition ($actual.Add($property.Name)) "InputContractInvalid"
    }
    Assert-WindowsM16FinalCondition ($actual.Count -eq $Expected.Count) "InputContractInvalid"
    foreach ($name in $Expected) {
        Assert-WindowsM16FinalCondition ($actual.Contains($name)) "InputContractInvalid"
    }
}

function Get-WindowsM16FinalExactProperty {
    param(
        [Parameter(Mandatory = $true)][pscustomobject]$Value,
        [Parameter(Mandatory = $true)][string]$Name
    )

    $matches = @($Value.PSObject.Properties | Where-Object { $_.Name -ceq $Name })
    Assert-WindowsM16FinalCondition ($matches.Count -eq 1) "InputContractInvalid"
    return $matches[0].Value
}

function Assert-WindowsM16FinalExactString {
    param($Value, [Parameter(Mandatory = $true)][string]$Expected)
    Assert-WindowsM16FinalCondition `
        ($Value -is [string] -and $Value -ceq $Expected) `
        "InputContractInvalid"
}

function Assert-WindowsM16FinalPatternString {
    param($Value, [Parameter(Mandatory = $true)][string]$Pattern)

    $matchesExactly = $false
    if ($Value -is [string]) {
        try {
            $match = [System.Text.RegularExpressions.Regex]::Match(
                $Value,
                $Pattern,
                [System.Text.RegularExpressions.RegexOptions]::CultureInvariant)
            $matchesExactly =
                $match.Success -and $match.Index -eq 0 -and $match.Length -eq $Value.Length
        }
        catch {
            $matchesExactly = $false
        }
    }
    Assert-WindowsM16FinalCondition `
        $matchesExactly `
        "InputContractInvalid"
}

function Assert-WindowsM16FinalExactInteger {
    param($Value, [Parameter(Mandatory = $true)][long]$Expected)
    Assert-WindowsM16FinalCondition `
        (($Value -is [int32] -or $Value -is [int64]) -and [long]$Value -eq $Expected) `
        "InputContractInvalid"
}

function Assert-WindowsM16FinalIntegerRange {
    param($Value, [long]$Minimum, [long]$Maximum)
    Assert-WindowsM16FinalCondition `
        (($Value -is [int32] -or $Value -is [int64]) -and
         [long]$Value -ge $Minimum -and [long]$Value -le $Maximum) `
        "InputContractInvalid"
}

function Test-WindowsM16FinalScannerSurface {
    param(
        [Parameter(Mandatory = $true)][pscustomobject]$Value,
        [Parameter(Mandatory = $true)][string]$ExpectedSurfaceId
    )

    Assert-WindowsM16FinalExactPropertySet -Value $Value -Expected @(
        "SurfaceId", "SchemaVersion", "Profile", "Result", "FileCount",
        "DirectoryCount", "TotalFileBytes", "InventorySha256", "FindingCount")
    Assert-WindowsM16FinalExactString `
        (Get-WindowsM16FinalExactProperty $Value "SurfaceId") $ExpectedSurfaceId
    Assert-WindowsM16FinalExactInteger `
        (Get-WindowsM16FinalExactProperty $Value "SchemaVersion") 1
    Assert-WindowsM16FinalExactString `
        (Get-WindowsM16FinalExactProperty $Value "Profile") "M16ReleaseCandidate"
    Assert-WindowsM16FinalExactString `
        (Get-WindowsM16FinalExactProperty $Value "Result") "clean"
    Assert-WindowsM16FinalIntegerRange `
        (Get-WindowsM16FinalExactProperty $Value "FileCount") `
        1 $script:m16FinalMaximumSurfaceFileCount
    Assert-WindowsM16FinalIntegerRange `
        (Get-WindowsM16FinalExactProperty $Value "DirectoryCount") `
        0 $script:m16FinalMaximumSurfaceDirectoryCount
    Assert-WindowsM16FinalIntegerRange `
        (Get-WindowsM16FinalExactProperty $Value "TotalFileBytes") `
        1 $script:m16FinalMaximumSurfaceFileBytes
    Assert-WindowsM16FinalPatternString `
        (Get-WindowsM16FinalExactProperty $Value "InventorySha256") `
        '\A[0-9a-f]{64}\z'
    Assert-WindowsM16FinalExactInteger `
        (Get-WindowsM16FinalExactProperty $Value "FindingCount") 0

    return [ordered]@{
        SurfaceId = $ExpectedSurfaceId
        SchemaVersion = 1
        Profile = "M16ReleaseCandidate"
        Result = "clean"
        FileCount = [long](Get-WindowsM16FinalExactProperty $Value "FileCount")
        DirectoryCount = [long](Get-WindowsM16FinalExactProperty $Value "DirectoryCount")
        TotalFileBytes = [long](Get-WindowsM16FinalExactProperty $Value "TotalFileBytes")
        InventorySha256 = [string](Get-WindowsM16FinalExactProperty $Value "InventorySha256")
        FindingCount = 0
    }
}

function New-WindowsM16FinalArtifactEvidence {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][string]$PackageIntermediatePath,
        [Parameter(Mandatory = $true)][string]$FullLogScannerReportPath,
        [Parameter(Mandatory = $true)][string]$InputRoot,
        [Parameter(Mandatory = $true)][string]$ExpectedRunId,
        [Parameter(Mandatory = $true)][string]$ExpectedCommitSha,
        [Parameter(Mandatory = $true)][string]$ExpectedPackageSha256,
        [Parameter(Mandatory = $true)][string]$ExpectedPackageSbomApplicationPackageSha256,
        [Parameter(Mandatory = $true)][string]$ExpectedExactPackageInventorySha256
    )

    Assert-WindowsM16FinalPatternString $ExpectedRunId '\A[0-9a-f]{32}\z'
    Assert-WindowsM16FinalPatternString $ExpectedCommitSha '\A[0-9a-f]{40}\z'
    Assert-WindowsM16FinalPatternString $ExpectedPackageSha256 '\A[0-9a-f]{64}\z'
    Assert-WindowsM16FinalPatternString `
        $ExpectedPackageSbomApplicationPackageSha256 '\A[0-9a-f]{64}\z'
    Assert-WindowsM16FinalPatternString `
        $ExpectedExactPackageInventorySha256 '\A[0-9a-f]{64}\z'
    Assert-WindowsM16FinalCondition `
        ($ExpectedPackageSha256 -ceq $ExpectedPackageSbomApplicationPackageSha256) `
        "BindingMismatch"

    $packageRecord = Read-WindowsM16FinalStrictJson `
        -Path $PackageIntermediatePath -InputRoot $InputRoot
    $fullLogRecord = Read-WindowsM16FinalStrictJson `
        -Path $FullLogScannerReportPath -InputRoot $InputRoot
    Assert-WindowsM16FinalCondition `
        (($packageRecord.ByteLength + $fullLogRecord.ByteLength) -le
            $script:m16FinalMaximumAggregateInputBytes) `
        "InputBoundsInvalid"

    $package = $packageRecord.Value
    Assert-WindowsM16FinalExactPropertySet -Value $package -Expected @(
        "SchemaVersion", "Milestone", "EvidenceKind", "Result", "RunId",
        "CommitSha", "PackageSha256", "PackageSbomApplicationPackageSha256",
        "ScannerProfile", "Surfaces", "SameBuildBindingPassed",
        "RepositoryStable", "RawSurfacesUploaded", "SupportArtifactScope")
    Assert-WindowsM16FinalExactInteger `
        (Get-WindowsM16FinalExactProperty $package "SchemaVersion") 1
    Assert-WindowsM16FinalExactString `
        (Get-WindowsM16FinalExactProperty $package "Milestone") "M16"
    Assert-WindowsM16FinalExactString `
        (Get-WindowsM16FinalExactProperty $package "EvidenceKind") `
        "PackageBoundFinalArtifactSurfaces"
    Assert-WindowsM16FinalExactString `
        (Get-WindowsM16FinalExactProperty $package "Result") "passed"
    Assert-WindowsM16FinalExactString `
        (Get-WindowsM16FinalExactProperty $package "RunId") $ExpectedRunId
    Assert-WindowsM16FinalExactString `
        (Get-WindowsM16FinalExactProperty $package "CommitSha") $ExpectedCommitSha
    Assert-WindowsM16FinalExactString `
        (Get-WindowsM16FinalExactProperty $package "PackageSha256") `
        $ExpectedPackageSha256
    Assert-WindowsM16FinalExactString `
        (Get-WindowsM16FinalExactProperty $package "PackageSbomApplicationPackageSha256") `
        $ExpectedPackageSbomApplicationPackageSha256
    Assert-WindowsM16FinalExactString `
        (Get-WindowsM16FinalExactProperty $package "ScannerProfile") `
        "M16ReleaseCandidate"
    foreach ($propertyName in @("SameBuildBindingPassed", "RepositoryStable")) {
        $propertyValue = Get-WindowsM16FinalExactProperty $package $propertyName
        Assert-WindowsM16FinalCondition `
            ($propertyValue -is [bool] -and $propertyValue) `
            "InputContractInvalid"
    }
    $rawUploaded = Get-WindowsM16FinalExactProperty $package "RawSurfacesUploaded"
    Assert-WindowsM16FinalCondition `
        ($rawUploaded -is [bool] -and -not $rawUploaded) `
        "InputContractInvalid"
    Assert-WindowsM16FinalExactString `
        (Get-WindowsM16FinalExactProperty $package "SupportArtifactScope") `
        "ReleaseAcceptanceOnly"

    $surfaceValues = @(Get-WindowsM16FinalExactProperty $package "Surfaces")
    Assert-WindowsM16FinalCondition ($surfaceValues.Count -eq 3) "InputContractInvalid"
    $surfaceIds = @("owned-app-data", "exact-package", "support-artifact")
    $sanitizedSurfaces = New-Object System.Collections.Generic.List[object]
    for ($index = 0; $index -lt $surfaceIds.Count; $index++) {
        Assert-WindowsM16FinalCondition `
            ($surfaceValues[$index] -is [pscustomobject]) `
            "InputContractInvalid"
        $sanitizedSurfaces.Add((Test-WindowsM16FinalScannerSurface `
                -Value $surfaceValues[$index] `
                -ExpectedSurfaceId $surfaceIds[$index]))
    }
    Assert-WindowsM16FinalCondition `
        ($sanitizedSurfaces[1].InventorySha256 -ceq
            $ExpectedExactPackageInventorySha256) `
        "BindingMismatch"

    $fullLog = $fullLogRecord.Value
    $sanitizedSurfaces.Add((Test-WindowsM16FinalScannerSurface `
            -Value $fullLog `
            -ExpectedSurfaceId "full-log"))

    [long]$totalFileCount = 0
    [long]$totalDirectoryCount = 0
    [long]$totalFileBytes = 0
    foreach ($surface in $sanitizedSurfaces) {
        Assert-WindowsM16FinalCondition `
            ($surface.FileCount -le
                (($script:m16FinalMaximumSurfaceFileCount * 4L) - $totalFileCount)) `
            "AggregateBoundsInvalid"
        Assert-WindowsM16FinalCondition `
            ($surface.DirectoryCount -le
                (($script:m16FinalMaximumSurfaceDirectoryCount * 4L) - $totalDirectoryCount)) `
            "AggregateBoundsInvalid"
        Assert-WindowsM16FinalCondition `
            ($surface.TotalFileBytes -le
                ($script:m16FinalMaximumAggregateFileBytes - $totalFileBytes)) `
            "AggregateBoundsInvalid"
        $totalFileCount += $surface.FileCount
        $totalDirectoryCount += $surface.DirectoryCount
        $totalFileBytes += $surface.TotalFileBytes
    }

    return [pscustomobject][ordered]@{
        SchemaVersion = 1
        Milestone = "M16"
        EvidenceKind = "FinalArtifactCanaryScan"
        Result = "passed"
        RunId = $ExpectedRunId
        CommitSha = $ExpectedCommitSha
        PackageSha256 = $ExpectedPackageSha256
        PackageSbomApplicationPackageSha256 =
            $ExpectedPackageSbomApplicationPackageSha256
        ScannerProfile = "M16ReleaseCandidate"
        SurfaceCount = 4
        TotalFileCount = $totalFileCount
        TotalDirectoryCount = $totalDirectoryCount
        TotalFileBytes = $totalFileBytes
        PackageIntermediateSha256 = $packageRecord.Sha256
        FullLogScannerReportSha256 = $fullLogRecord.Sha256
        Surfaces = $sanitizedSurfaces.ToArray()
        SameBuildBindingPassed = $true
        RepositoryStable = $true
        RawSurfacesUploaded = $false
        SupportArtifactScope = "ReleaseAcceptanceOnly"
    }
}

function Write-WindowsM16FinalArtifactEvidenceAtomically {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]$Value,
        [Parameter(Mandatory = $true)][string]$DestinationPath
    )

    try {
        $destination = [System.IO.Path]::GetFullPath($DestinationPath)
        Assert-WindowsM16FinalNoAlternateDataStream `
            -Path $destination -Code "OutputPathInvalid"
        Assert-WindowsM16FinalNtfsVolume `
            -Path $destination -Code "OutputPathInvalid"
        $parent = [System.IO.Path]::GetDirectoryName($destination)
        Assert-WindowsM16FinalCondition `
            (-not [string]::IsNullOrWhiteSpace($parent)) `
            "OutputPathInvalid"
        $existingAncestor = $parent
        while (-not (Test-Path -LiteralPath $existingAncestor)) {
            $nextAncestor = [System.IO.Path]::GetDirectoryName($existingAncestor)
            Assert-WindowsM16FinalCondition `
                (-not [string]::IsNullOrWhiteSpace($nextAncestor) -and
                 -not $nextAncestor.Equals(
                    $existingAncestor,
                    [System.StringComparison]::OrdinalIgnoreCase)) `
                "OutputPathInvalid"
            $existingAncestor = $nextAncestor
        }
        Assert-WindowsM16FinalNoReparseDirectoryChain `
            -Root ([System.IO.Path]::GetPathRoot($destination)) `
            -DirectoryPath $existingAncestor `
            -Code "OutputPathInvalid"
        [System.IO.Directory]::CreateDirectory($parent) | Out-Null
        Assert-WindowsM16FinalNoReparseDirectoryChain `
            -Root ([System.IO.Path]::GetPathRoot($destination)) `
            -DirectoryPath $parent `
            -Code "OutputPathInvalid"
        if (Test-Path -LiteralPath $destination) {
            $destinationItem = Get-Item -LiteralPath $destination -Force -ErrorAction Stop
            Assert-WindowsM16FinalCondition `
                (-not $destinationItem.PSIsContainer -and
                 (($destinationItem.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -eq 0)) `
                "OutputPathInvalid"
            Assert-WindowsM16FinalNoNamedStreams `
                -Path $destination -Code "OutputPathInvalid"
        }

        $json = $Value | ConvertTo-Json -Depth 8
        $bytes = $script:m16FinalUtf8NoBom.GetBytes($json + [Environment]::NewLine)
        Assert-WindowsM16FinalCondition `
            ($bytes.Length -gt 0 -and
             $bytes.Length -le $script:m16FinalMaximumOutputBytes) `
            "OutputBoundsInvalid"
        $temporary = "$destination.$([Guid]::NewGuid().ToString('N')).tmp"
        $backup = "$destination.$([Guid]::NewGuid().ToString('N')).bak"
        $rollbackDiscard = "$destination.$([Guid]::NewGuid().ToString('N')).rollback"
        $destinationExisted = Test-Path -LiteralPath $destination -PathType Leaf
        $published = $false
        $committed = $false
        $preserveBackup = $false
        try {
            try {
                $stream = [System.IO.File]::Open(
                    $temporary,
                    [System.IO.FileMode]::CreateNew,
                    [System.IO.FileAccess]::Write,
                    [System.IO.FileShare]::None)
                try {
                    $stream.Write($bytes, 0, $bytes.Length)
                    $stream.Flush($true)
                }
                finally { $stream.Dispose() }
                Assert-WindowsM16FinalNoNamedStreams `
                    -Path $temporary -Code "OutputPathInvalid"
                if ($destinationExisted) {
                    [System.IO.File]::Replace($temporary, $destination, $backup, $true)
                }
                else {
                    [System.IO.File]::Move($temporary, $destination)
                }
                $published = $true
                Assert-WindowsM16FinalNoNamedStreams `
                    -Path $destination -Code "OutputPathInvalid"
                if ($destinationExisted) {
                    Remove-Item -LiteralPath $backup -Force -ErrorAction Stop
                }
                $committed = $true
            }
            catch {
                $publicationFailure = $_
                if ($published -and -not $committed) {
                    try {
                        if ($destinationExisted) {
                            Assert-WindowsM16FinalCondition `
                                (Test-Path -LiteralPath $backup -PathType Leaf) `
                                "OutputRollbackFailed"
                            $backupItem = Get-Item -LiteralPath $backup -Force -ErrorAction Stop
                            Assert-WindowsM16FinalCondition `
                                (-not $backupItem.PSIsContainer -and
                                 (($backupItem.Attributes -band
                                    [System.IO.FileAttributes]::ReparsePoint) -eq 0)) `
                                "OutputRollbackFailed"
                            Assert-WindowsM16FinalNoNamedStreams `
                                -Path $backup -Code "OutputRollbackFailed"
                            if (Test-Path -LiteralPath $destination -PathType Leaf) {
                                [System.IO.File]::Replace(
                                    $backup,
                                    $destination,
                                    $rollbackDiscard,
                                    $true)
                            }
                            else {
                                [System.IO.File]::Move($backup, $destination)
                            }
                            Assert-WindowsM16FinalNoNamedStreams `
                                -Path $destination -Code "OutputRollbackFailed"
                        }
                        elseif (Test-Path -LiteralPath $destination) {
                            $destinationItem = Get-Item `
                                -LiteralPath $destination `
                                -Force `
                                -ErrorAction Stop
                            Assert-WindowsM16FinalCondition `
                                (-not $destinationItem.PSIsContainer -and
                                 (($destinationItem.Attributes -band
                                    [System.IO.FileAttributes]::ReparsePoint) -eq 0)) `
                                "OutputRollbackFailed"
                            Remove-Item `
                                -LiteralPath $destination `
                                -Force `
                                -ErrorAction Stop
                        }
                    }
                    catch {
                        $preserveBackup =
                            $destinationExisted -and
                            (Test-Path -LiteralPath $backup -PathType Leaf)
                        try {
                            if (Test-Path -LiteralPath $destination -PathType Leaf) {
                                $failedDestination = Get-Item `
                                    -LiteralPath $destination `
                                    -Force `
                                    -ErrorAction Stop
                                if (($failedDestination.Attributes -band
                                        [System.IO.FileAttributes]::ReparsePoint) -eq 0) {
                                    Remove-Item `
                                        -LiteralPath $destination `
                                        -Force `
                                        -ErrorAction Stop
                                }
                            }
                        }
                        catch {
                        }
                        Fail-WindowsM16FinalArtifactEvidence -Code "OutputRollbackFailed"
                    }
                }
                throw $publicationFailure
            }
        }
        finally {
            if (Test-Path -LiteralPath $temporary) {
                Remove-Item -LiteralPath $temporary -Force -ErrorAction Stop
            }
            if ((Test-Path -LiteralPath $backup) -and -not $preserveBackup) {
                Remove-Item -LiteralPath $backup -Force -ErrorAction Stop
            }
            if (Test-Path -LiteralPath $rollbackDiscard) {
                Remove-Item -LiteralPath $rollbackDiscard -Force -ErrorAction Stop
            }
        }
    }
    catch {
        if ($_.Exception.Message -cmatch '\AM16FinalArtifactEvidence:[A-Za-z][A-Za-z0-9]+\z') {
            throw $_.Exception.Message
        }
        Fail-WindowsM16FinalArtifactEvidence -Code "OutputWriteFailed"
    }
}
