[CmdletBinding()]
param(
    [switch]$AllowBlockedCandidate,

    [string]$RepositoryRoot,

    [string]$EvidencePath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$script:maximumInputBytes = 1MB
$script:maximumAggregateInputBytes = 4MB
$script:maximumOutputBytes = 256KB
$script:maximumJsonDepth = 16
$script:maximumObjectPropertyCount = 1024
$script:maximumArrayLength = 4096
$script:maximumStringLength = 4096
$script:maximumJsonNodeCount = 65536
$script:technicalStage = "Initialization"
$script:utf8Strict = New-Object System.Text.UTF8Encoding($false, $true)
$script:utf8NoBom = New-Object System.Text.UTF8Encoding($false)

function Fail-TechnicalInvariant {
    param(
        [Parameter(Mandatory = $true)]
        [ValidatePattern('^[A-Za-z][A-Za-z0-9]+$')]
        [string]$Code
    )

    throw "M16TechnicalInvariant:$Code"
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

function Assert-NoAlternateDataStreamPath {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path,

        [Parameter(Mandatory = $true)]
        [string]$Code
    )

    try {
        $pathRoot = [System.IO.Path]::GetPathRoot($Path)
        Assert-Condition (-not [string]::IsNullOrWhiteSpace($pathRoot)) $Code
        $tail = $Path.Substring($pathRoot.Length)
        Assert-Condition ($tail.IndexOf(':') -lt 0) $Code
    }
    catch {
        if ($_.Exception.Message -match '^M16TechnicalInvariant:[A-Za-z][A-Za-z0-9]+$') {
            throw $_.Exception.Message
        }

        Fail-TechnicalInvariant -Code $Code
    }
}

function Assert-NoReparseDirectoryChain {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Root,

        [Parameter(Mandatory = $true)]
        [string]$DirectoryPath,

        [string]$Code = "DirectoryReparsePoint"
    )

    $isRoot = $DirectoryPath.Equals(
        $Root,
        [System.StringComparison]::OrdinalIgnoreCase)
    Assert-Condition `
        ($isRoot -or (Test-PathContainedByRoot -Path $DirectoryPath -Root $Root)) `
        $Code

    $rootWithSeparator = $Root.TrimEnd(
        [System.IO.Path]::DirectorySeparatorChar,
        [System.IO.Path]::AltDirectorySeparatorChar) +
        [System.IO.Path]::DirectorySeparatorChar
    $relative = if ($isRoot) {
        ""
    }
    else {
        $DirectoryPath.Substring($rootWithSeparator.Length)
    }

    $current = $Root
    foreach ($part in @($relative.Split(
                @('\', '/'),
                [System.StringSplitOptions]::RemoveEmptyEntries))) {
        $current = Join-Path $current $part
        if (Test-Path -LiteralPath $current) {
            $item = Get-Item -LiteralPath $current -Force
            Assert-Condition $item.PSIsContainer $Code
            Assert-Condition `
                (($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -eq 0) `
                $Code
        }
    }
}

function Get-LowerSha256Bytes {
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

function Read-RegularFileBytes {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path,

        [Parameter(Mandatory = $true)]
        [long]$MaximumBytes,

        [Parameter(Mandatory = $true)]
        [string]$Root,

        [Parameter(Mandatory = $true)]
        [string]$Code
    )

    try {
        $fullPath = [System.IO.Path]::GetFullPath($Path)
        Assert-NoAlternateDataStreamPath -Path $fullPath -Code $Code
        Assert-Condition (Test-PathContainedByRoot -Path $fullPath -Root $Root) $Code
        Assert-NoReparseDirectoryChain `
            -Root $Root `
            -DirectoryPath ([System.IO.Path]::GetDirectoryName($fullPath)) `
            -Code $Code
        Assert-Condition (Test-Path -LiteralPath $fullPath -PathType Leaf) $Code
        $item = Get-Item -LiteralPath $fullPath -Force
        Assert-Condition (-not $item.PSIsContainer) $Code
        Assert-Condition `
            (($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -eq 0) `
            $Code

        $stream = [System.IO.File]::Open(
            $fullPath,
            [System.IO.FileMode]::Open,
            [System.IO.FileAccess]::Read,
            [System.IO.FileShare]::None)
        try {
            Assert-Condition `
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
        if ($_.Exception.Message -match '^M16TechnicalInvariant:[A-Za-z][A-Za-z0-9]+$') {
            throw $_.Exception.Message
        }

        Fail-TechnicalInvariant -Code $Code
    }
}

function Assert-NoDuplicateJsonProperties {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Text,

        [Parameter(Mandatory = $true)]
        [string]$Code
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
            Assert-Condition ($objectPropertySets.Count -gt 0) $Code
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
                        Assert-Condition (($index + 4) -lt $Text.Length) $Code
                        $hex = $Text.Substring($index + 1, 4)
                        Assert-Condition ($hex -cmatch '^[0-9A-Fa-f]{4}$') $Code
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

            Assert-Condition ([int]$stringCharacter -ge 0x20) $Code
            [void]$builder.Append($stringCharacter)
            $index++
        }

        Assert-Condition $closed $Code
        $lookAhead = $index
        while ($lookAhead -lt $Text.Length -and [char]::IsWhiteSpace($Text[$lookAhead])) {
            $lookAhead++
        }
        if ($lookAhead -lt $Text.Length -and $Text[$lookAhead] -eq [char]0x3a) {
            Assert-Condition ($objectPropertySets.Count -gt 0) $Code
            if (-not $objectPropertySets.Peek().Add($builder.ToString())) {
                Fail-TechnicalInvariant -Code $Code
            }
        }
    }

    Assert-Condition ($objectPropertySets.Count -eq 0) $Code
}

function Assert-JsonBounds {
    param(
        $Value,

        [int]$Depth = 1,

        [Parameter(Mandatory = $true)]
        [ref]$NodeCount,

        [Parameter(Mandatory = $true)]
        [string]$Code
    )

    Assert-Condition ($Depth -le $script:maximumJsonDepth) $Code
    $NodeCount.Value++
    Assert-Condition ($NodeCount.Value -le $script:maximumJsonNodeCount) $Code

    if ($null -eq $Value) {
        return
    }
    if ($Value -is [string]) {
        Assert-Condition ($Value.Length -le $script:maximumStringLength) $Code
        return
    }
    if ($Value -is [bool] -or
        $Value -is [byte] -or
        $Value -is [sbyte] -or
        $Value -is [int16] -or
        $Value -is [uint16] -or
        $Value -is [int32] -or
        $Value -is [uint32] -or
        $Value -is [int64] -or
        $Value -is [uint64] -or
        $Value -is [single] -or
        $Value -is [double] -or
        $Value -is [decimal]) {
        return
    }
    if ($Value -is [System.Collections.IDictionary]) {
        Assert-Condition ($Value.Count -le $script:maximumObjectPropertyCount) $Code
        foreach ($key in $Value.Keys) {
            Assert-Condition ($key -is [string]) $Code
            Assert-Condition ($key.Length -le $script:maximumStringLength) $Code
            Assert-JsonBounds `
                -Value $Value[$key] `
                -Depth ($Depth + 1) `
                -NodeCount $NodeCount `
                -Code $Code
        }
        return
    }
    if ($Value -is [System.Array] -or $Value -is [System.Collections.IList]) {
        $values = @($Value)
        Assert-Condition ($values.Count -le $script:maximumArrayLength) $Code
        foreach ($item in $values) {
            Assert-JsonBounds `
                -Value $item `
                -Depth ($Depth + 1) `
                -NodeCount $NodeCount `
                -Code $Code
        }
        return
    }
    if ($Value -is [pscustomobject]) {
        $properties = @($Value.PSObject.Properties)
        Assert-Condition ($properties.Count -le $script:maximumObjectPropertyCount) $Code
        foreach ($property in $properties) {
            Assert-Condition ($property.Name.Length -le $script:maximumStringLength) $Code
            Assert-JsonBounds `
                -Value $property.Value `
                -Depth ($Depth + 1) `
                -NodeCount $NodeCount `
                -Code $Code
        }
        return
    }

    Fail-TechnicalInvariant -Code $Code
}

function Read-StrictJsonRecord {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path,

        [Parameter(Mandatory = $true)]
        [string]$Root,

        [Parameter(Mandatory = $true)]
        [string]$Name,

        [long]$MaximumBytes = $script:maximumInputBytes
    )

    $code = "InputInvalid"
    [byte[]]$firstBytes = Read-RegularFileBytes `
        -Path $Path `
        -MaximumBytes $MaximumBytes `
        -Root $Root `
        -Code $code
    [byte[]]$secondBytes = Read-RegularFileBytes `
        -Path $Path `
        -MaximumBytes $MaximumBytes `
        -Root $Root `
        -Code $code
    $firstHash = Get-LowerSha256Bytes -Bytes $firstBytes
    $secondHash = Get-LowerSha256Bytes -Bytes $secondBytes
    Assert-Condition `
        ($firstBytes.Length -eq $secondBytes.Length -and $firstHash -ceq $secondHash) `
        "InputChanged"
    Assert-Condition `
        (-not ($firstBytes.Length -ge 3 -and
               $firstBytes[0] -eq 0xef -and
               $firstBytes[1] -eq 0xbb -and
               $firstBytes[2] -eq 0xbf)) `
        "InputEncodingInvalid"

    try {
        $text = $script:utf8Strict.GetString($firstBytes)
    }
    catch {
        Fail-TechnicalInvariant -Code "InputEncodingInvalid"
    }
    Assert-Condition ($text.Length -gt 0) "InputInvalid"
    Assert-NoDuplicateJsonProperties -Text $text -Code "InputDuplicateProperty"
    try {
        $value = $text | ConvertFrom-Json
    }
    catch {
        Fail-TechnicalInvariant -Code "InputJsonInvalid"
    }
    Assert-Condition ($null -ne $value -and $value -is [pscustomobject]) "InputJsonInvalid"
    $nodeCount = 0
    Assert-JsonBounds `
        -Value $value `
        -NodeCount ([ref]$nodeCount) `
        -Code "InputBoundsInvalid"

    return [pscustomobject]@{
        Name = $Name
        Path = [System.IO.Path]::GetFullPath($Path)
        ByteLength = [long]$firstBytes.Length
        Sha256 = $firstHash
        Value = $value
    }
}

function Get-ExactProperty {
    param(
        [Parameter(Mandatory = $true)]
        [pscustomobject]$Value,

        [Parameter(Mandatory = $true)]
        [string]$Name,

        [string]$Code = "InputContractInvalid"
    )

    $matches = @($Value.PSObject.Properties | Where-Object { $_.Name -ceq $Name })
    Assert-Condition ($matches.Count -eq 1) $Code
    return $matches[0].Value
}

function Assert-ExactString {
    param(
        $Value,

        [Parameter(Mandatory = $true)]
        [string]$Expected,

        [string]$Code = "InputContractInvalid"
    )

    Assert-Condition ($Value -is [string] -and $Value -ceq $Expected) $Code
}

function Assert-True {
    param(
        $Value,

        [string]$Code = "InputContractInvalid"
    )

    Assert-Condition ($Value -is [bool] -and $Value) $Code
}

function Assert-False {
    param(
        $Value,

        [string]$Code = "InputContractInvalid"
    )

    Assert-Condition ($Value -is [bool] -and -not $Value) $Code
}

function Assert-Boolean {
    param(
        $Value,

        [string]$Code = "InputContractInvalid"
    )

    Assert-Condition ($Value -is [bool]) $Code
}

function Assert-ExactInteger {
    param(
        $Value,

        [Parameter(Mandatory = $true)]
        [long]$Expected,

        [string]$Code = "InputContractInvalid"
    )

    Assert-Condition `
        (($Value -is [int32] -or $Value -is [int64]) -and [long]$Value -eq $Expected) `
        $Code
}

function Assert-IntegerRange {
    param(
        $Value,

        [Parameter(Mandatory = $true)]
        [long]$Minimum,

        [Parameter(Mandatory = $true)]
        [long]$Maximum,

        [string]$Code = "InputContractInvalid"
    )

    Assert-Condition `
        (($Value -is [int32] -or $Value -is [int64]) -and
         [long]$Value -ge $Minimum -and
         [long]$Value -le $Maximum) `
        $Code
}

function Assert-NumberRange {
    param(
        $Value,

        [Parameter(Mandatory = $true)]
        [double]$Minimum,

        [Parameter(Mandatory = $true)]
        [double]$Maximum,

        [string]$Code = "InputContractInvalid"
    )

    $isNumber =
        $Value -is [int32] -or
        $Value -is [int64] -or
        $Value -is [single] -or
        $Value -is [double] -or
        $Value -is [decimal]
    if (-not $isNumber) {
        Fail-TechnicalInvariant -Code $Code
    }
    $number = [double]$Value
    Assert-Condition `
        (-not [double]::IsNaN($number) -and
         -not [double]::IsInfinity($number) -and
         $number -ge $Minimum -and
         $number -le $Maximum) `
        $Code
}

function Assert-ExactStringArray {
    param(
        $Value,

        [Parameter(Mandatory = $true)]
        [string[]]$Expected,

        [string]$Code = "InputContractInvalid"
    )

    $values = @($Value)
    Assert-Condition ($values.Count -eq $Expected.Count) $Code
    for ($index = 0; $index -lt $Expected.Count; $index++) {
        Assert-ExactString -Value $values[$index] -Expected $Expected[$index] -Code $Code
    }
}

function Assert-CommitBinding {
    param(
        [Parameter(Mandatory = $true)]
        [pscustomobject]$Value,

        [Parameter(Mandatory = $true)]
        [string]$PropertyName,

        [Parameter(Mandatory = $true)]
        [string]$CommitSha
    )

    $boundCommit = Get-ExactProperty -Value $Value -Name $PropertyName
    Assert-ExactString -Value $boundCommit -Expected $CommitSha
}

function Assert-LowerSha256 {
    param(
        $Value,

        [string]$Code = "InputContractInvalid"
    )

    Assert-Condition `
        ($Value -is [string] -and $Value -cmatch '^[0-9a-f]{64}$') `
        $Code
}

function Assert-UtcTimestamp {
    param(
        $Value,

        [string]$Code = "InputContractInvalid"
    )

    Assert-Condition `
        ($Value -is [string] -and
         $Value -cmatch '^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d{1,7})?Z$') `
        $Code
    $parsed = [DateTimeOffset]::MinValue
    Assert-Condition `
        ([DateTimeOffset]::TryParse(
            $Value,
            [System.Globalization.CultureInfo]::InvariantCulture,
            [System.Globalization.DateTimeStyles]::AssumeUniversal,
            [ref]$parsed) -and
         $parsed.Offset -eq [TimeSpan]::Zero) `
        $Code
}

function Get-CleanRepositoryCommit {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Root,

        [string]$Code = "RepositoryBindingInvalid"
    )

    try {
        $topLevel = (& git -C $Root rev-parse --show-toplevel 2>$null | Out-String).Trim()
        Assert-Condition ($LASTEXITCODE -eq 0) $Code
        $resolvedTopLevel = [System.IO.Path]::GetFullPath($topLevel)
        Assert-Condition `
            ($resolvedTopLevel.Equals($Root, [System.StringComparison]::OrdinalIgnoreCase)) `
            $Code

        $status = (& git -C $Root status --porcelain=v1 --untracked-files=normal 2>$null |
                Out-String)
        Assert-Condition ($LASTEXITCODE -eq 0 -and [string]::IsNullOrWhiteSpace($status)) `
            "RepositoryDirty"

        $commit = (& git -C $Root rev-parse --verify HEAD 2>$null | Out-String).
            Trim().ToLowerInvariant()
        Assert-Condition `
            ($LASTEXITCODE -eq 0 -and $commit -cmatch '^[0-9a-f]{40}$') `
            $Code
        return $commit
    }
    catch {
        if ($_.Exception.Message -match '^M16TechnicalInvariant:[A-Za-z][A-Za-z0-9]+$') {
            throw $_.Exception.Message
        }

        Fail-TechnicalInvariant -Code $Code
    }
}

function Assert-ExactInputDirectoryInventory {
    param(
        [Parameter(Mandatory = $true)]
        [string]$InputRoot,

        [Parameter(Mandatory = $true)]
        [string[]]$ExpectedNames,

        [Parameter(Mandatory = $true)]
        [string]$Root
    )

    Assert-Condition (Test-Path -LiteralPath $InputRoot -PathType Container) `
        "InputDirectoryInvalid"
    Assert-NoReparseDirectoryChain -Root $Root -DirectoryPath $InputRoot
    $expected = New-Object 'System.Collections.Generic.HashSet[string]' `
        ([System.StringComparer]::Ordinal)
    foreach ($expectedName in $ExpectedNames) {
        Assert-Condition ($expected.Add($expectedName)) "InputDirectoryInvalid"
    }

    $observed = New-Object 'System.Collections.Generic.HashSet[string]' `
        ([System.StringComparer]::Ordinal)
    foreach ($item in @(Get-ChildItem -LiteralPath $InputRoot -Force)) {
        Assert-Condition (-not $item.PSIsContainer) "InputDirectoryInvalid"
        Assert-Condition `
            (($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -eq 0) `
            "InputDirectoryInvalid"
        Assert-Condition `
            ($expected.Contains($item.Name) -and $observed.Add($item.Name)) `
            "InputDirectoryInvalid"
    }
    Assert-Condition ($observed.Count -eq $expected.Count) "InputDirectoryInvalid"
}

function Test-QualityInput {
    param(
        [Parameter(Mandatory = $true)]
        [pscustomobject]$InputRecord,

        [Parameter(Mandatory = $true)]
        [string]$CommitSha,

        [Parameter(Mandatory = $true)]
        [string]$SdkVersion
    )

    $value = $InputRecord.Value
    Assert-ExactInteger -Value (Get-ExactProperty $value "schemaVersion") -Expected 1
    Assert-CommitBinding -Value $value -PropertyName "commitSha" -CommitSha $CommitSha
    Assert-ExactString -Value (Get-ExactProperty $value "milestone") -Expected "M4-foundation"
    Assert-ExactString -Value (Get-ExactProperty $value "sdkVersion") -Expected $SdkVersion
    Assert-ExactString -Value (Get-ExactProperty $value "configuration") -Expected "Debug+Release"
    Assert-ExactString -Value (Get-ExactProperty $value "platform") -Expected "x64"
    Assert-ExactInteger -Value (Get-ExactProperty $value "cleanRunCount") -Expected 2
    Assert-ExactString `
        -Value (Get-ExactProperty $value "qualityGateSentinel") `
        -Expected "armed-failed-and-disarmed-passed"
    Assert-ExactString `
        -Value (Get-ExactProperty $value "scannerCliSelfTest") `
        -Expected "contaminated-exit-2-and-clean-exit-0"
    Assert-ExactString `
        -Value (Get-ExactProperty $value "artifactCanaryScan") `
        -Expected "artifact-files-only-passed"

    $testResults = @(Get-ExactProperty $value "testResults")
    $testCount = Get-ExactProperty $value "testCountPerRun"
    Assert-Condition `
        (($testCount -is [int32] -or $testCount -is [int64]) -and
         [long]$testCount -gt 0 -and
         [long]$testCount -eq $testResults.Count) `
        "InputContractInvalid"
    foreach ($testResult in $testResults) {
        Assert-Condition `
            ($testResult -is [string] -and
             $testResult.Length -gt 7 -and
             $testResult.EndsWith(
                "|Passed",
                [System.StringComparison]::Ordinal)) `
            "InputContractInvalid"
    }

    $fixture = Get-ExactProperty $value "fixture"
    Assert-Condition ($fixture -is [pscustomobject]) "InputContractInvalid"
    Assert-ExactString -Value (Get-ExactProperty $fixture "provenance") -Expected "synthetic"
    Assert-LowerSha256 -Value (Get-ExactProperty $fixture "recordsSha256")
    Assert-LowerSha256 -Value (Get-ExactProperty $fixture "manifestSha256")
}

function Test-PackageSmokeInput {
    param(
        [Parameter(Mandatory = $true)]
        [pscustomobject]$InputRecord,

        [Parameter(Mandatory = $true)]
        [string]$CommitSha,

        [Parameter(Mandatory = $true)]
        [string]$SdkVersion
    )

    $value = $InputRecord.Value
    Assert-CommitBinding -Value $value -PropertyName "CommitSha" -CommitSha $CommitSha
    Assert-UtcTimestamp -Value (Get-ExactProperty $value "CompletedAt")
    Assert-ExactString -Value (Get-ExactProperty $value "Configuration") -Expected "Release"
    Assert-ExactString -Value (Get-ExactProperty $value "DotNetSdk") -Expected $SdkVersion
    Assert-ExactString -Value (Get-ExactProperty $value "Architecture") -Expected "x64"
    Assert-ExactStringArray `
        -Value (Get-ExactProperty $value "Capabilities") `
        -Expected @("runFullTrust")
    Assert-ExactString -Value (Get-ExactProperty $value "SignatureStatus") -Expected "Valid"
    Assert-ExactString `
        -Value (Get-ExactProperty $value "RuntimeDependencySignatureStatus") `
        -Expected "Valid"
    $packageSha256 = Get-ExactProperty $value "PackageSha256"
    $sbomApplicationPackageSha256 =
        Get-ExactProperty $value "PackageSbomApplicationPackageSha256"
    Assert-LowerSha256 -Value $packageSha256
    Assert-LowerSha256 -Value $sbomApplicationPackageSha256
    Assert-ExactString `
        -Value $sbomApplicationPackageSha256 `
        -Expected $packageSha256
    Assert-ExactInteger -Value (Get-ExactProperty $value "PackageSbomSchemaVersion") -Expected 1
    Assert-ExactString -Value (Get-ExactProperty $value "PackageSbomFormat") -Expected "SPDX-2.2"
    foreach ($hashProperty in @(
            "PackageSbomSha256",
            "PackageSbomProductionInputSetSha256",
            "PackageSbomRuntimePackageSha256",
            "PackageInstallRootPreResetBaselineManifestSha256",
            "PackageInstallRootPreResetFinalManifestSha256",
            "PackageInstallRootBaselineManifestSha256",
            "PackageInstallRootFinalManifestSha256")) {
        Assert-LowerSha256 -Value (Get-ExactProperty $value $hashProperty)
    }
    Assert-ExactString `
        -Value (Get-ExactProperty $value "PackageInstallRootPreResetFinalManifestSha256") `
        -Expected (Get-ExactProperty $value "PackageInstallRootPreResetBaselineManifestSha256")
    Assert-ExactString `
        -Value (Get-ExactProperty $value "PackageInstallRootFinalManifestSha256") `
        -Expected (Get-ExactProperty $value "PackageInstallRootBaselineManifestSha256")
    Assert-ExactInteger -Value (Get-ExactProperty $value "PackageInstallRootAuditSegmentCount") -Expected 2
    Assert-ExactInteger -Value (Get-ExactProperty $value "PackageInstallRootPreResetMutationEventCount") -Expected 0
    Assert-ExactInteger -Value (Get-ExactProperty $value "PackageInstallRootMutationEventCount") -Expected 0
    Assert-ExactInteger -Value (Get-ExactProperty $value "PackageInstallRootAuditSchemaVersion") -Expected 1
    Assert-ExactString `
        -Value (Get-ExactProperty $value "PackageInstallRootAuditScope") `
        -Expected "ExactRegisteredProductPackageInstallLocation"
    Assert-False -Value (Get-ExactProperty $value "PackageInstallRootPreResetWatcherOverflow")
    Assert-False -Value (Get-ExactProperty $value "PackageInstallRootWatcherOverflow")
    foreach ($propertyName in @(
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
            "PackageRemoved")) {
        Assert-True -Value (Get-ExactProperty $value $propertyName)
    }

    Assert-ExactInteger -Value (Get-ExactProperty $value "PlaybackRapidSwitchCount") -Expected 25
    $rapidSwitchP95 = Get-ExactProperty $value "PlaybackRapidSwitchP95Milliseconds"
    Assert-NumberRange `
        -Value $rapidSwitchP95 `
        -Minimum 0 `
        -Maximum 3000
    Assert-NumberRange `
        -Value (Get-ExactProperty $value "PlaybackRapidSwitchMaximumMilliseconds") `
        -Minimum ([double]$rapidSwitchP95) `
        -Maximum 30000
    Assert-ExactInteger -Value (Get-ExactProperty $value "CatalogUiThreadResponsivenessProxyTimeoutCount") -Expected 0
    Assert-ExactInteger -Value (Get-ExactProperty $value "CatalogUiThreadResponsivenessProxyOverBudgetCount") -Expected 0
    Assert-NumberRange `
        -Value (Get-ExactProperty $value "CatalogInputResponseP95Milliseconds") `
        -Minimum 0 `
        -Maximum 100
    Assert-NumberRange `
        -Value (Get-ExactProperty $value "CatalogDwmFrameP95Milliseconds") `
        -Minimum 0 `
        -Maximum 33.3
    Assert-NumberRange `
        -Value (Get-ExactProperty $value "CatalogDwmFrameMaximumMilliseconds") `
        -Minimum 0 `
        -Maximum 200
    Assert-NumberRange `
        -Value (Get-ExactProperty $value "CatalogDwmDroppedFramePercent") `
        -Minimum 0 `
        -Maximum 1
    Assert-Condition `
        ([double](Get-ExactProperty $value "CatalogDwmDroppedFramePercent") -lt 1.0) `
        "InputContractInvalid"
    Assert-IntegerRange `
        -Value (Get-ExactProperty $value "CleanInstallOnboardingRequestCount") `
        -Minimum 1 `
        -Maximum ([long]::MaxValue)
    Assert-IntegerRange `
        -Value (Get-ExactProperty $value "CatalogRealizedContainerCount") `
        -Minimum 1 `
        -Maximum 64
    $catalogWorkingSetBudget =
        Get-ExactProperty $value "CatalogPlayerOffSteadyWorkingSetBudgetBytes"
    $catalogWorkingSetMaximum =
        Get-ExactProperty $value "CatalogPlayerOffSteadyWorkingSetMaximumBytes"
    Assert-IntegerRange -Value $catalogWorkingSetBudget -Minimum 1 -Maximum ([long]::MaxValue)
    Assert-IntegerRange `
        -Value $catalogWorkingSetMaximum `
        -Minimum 1 `
        -Maximum ([long]$catalogWorkingSetBudget)
    Assert-IntegerRange `
        -Value (Get-ExactProperty $value "PlaybackPrivateBytesDelta") `
        -Minimum ([long]::MinValue) `
        -Maximum 8388608
    Assert-IntegerRange `
        -Value (Get-ExactProperty $value "PlaybackWorkingSetBytesDelta") `
        -Minimum ([long]::MinValue) `
        -Maximum 16777216
    Assert-IntegerRange `
        -Value (Get-ExactProperty $value "PlaybackHandleCountDelta") `
        -Minimum ([long]::MinValue) `
        -Maximum 64
    Assert-IntegerRange `
        -Value (Get-ExactProperty $value "PlaybackThreadCountDelta") `
        -Minimum ([long]::MinValue) `
        -Maximum 0
    $reconnectCancelBudget =
        Get-ExactProperty $value "PlaybackReconnectCancelBudgetMilliseconds"
    Assert-NumberRange -Value $reconnectCancelBudget -Minimum 1000 -Maximum 1000
    Assert-NumberRange `
        -Value (Get-ExactProperty $value "PlaybackReconnectCancelElapsedMilliseconds") `
        -Minimum 0 `
        -Maximum ([double]$reconnectCancelBudget)
    Assert-ExactInteger `
        -Value (Get-ExactProperty $value "PlaybackReconnectNoLaterOpenRequestCountAfterObservation") `
        -Expected ([long](Get-ExactProperty $value "PlaybackReconnectNoLaterOpenRequestCountAtReady"))
    foreach ($zeroCounter in @(
            "NormalStreamCapacityRejectCount",
            "NormalStreamUnexpectedFailureCount",
            "FaultStreamCapacityRejectCount",
            "FaultStreamUnexpectedFailureCount")) {
        Assert-ExactInteger -Value (Get-ExactProperty $value $zeroCounter) -Expected 0
    }
    Assert-True -Value (Get-ExactProperty $value "FaultStreamHolding")
}

function Test-PackageLifecycleInput {
    param(
        [Parameter(Mandatory = $true)]
        [pscustomobject]$InputRecord,

        [Parameter(Mandatory = $true)]
        [string]$CommitSha,

        [Parameter(Mandatory = $true)]
        [string]$SdkVersion
    )

    $value = $InputRecord.Value
    Assert-ExactInteger -Value (Get-ExactProperty $value "SchemaVersion") -Expected 3
    Assert-CommitBinding -Value $value -PropertyName "CommitSha" -CommitSha $CommitSha
    Assert-UtcTimestamp -Value (Get-ExactProperty $value "CompletedAt")
    Assert-ExactString -Value (Get-ExactProperty $value "Configuration") -Expected "Release"
    Assert-ExactString -Value (Get-ExactProperty $value "DotNetSdk") -Expected $SdkVersion
    Assert-ExactString -Value (Get-ExactProperty $value "Architecture") -Expected "x64"
    Assert-ExactStringArray `
        -Value (Get-ExactProperty $value "Capabilities") `
        -Expected @("runFullTrust")
    foreach ($hashProperty in @("BaselinePackageSha256", "UpdatedPackageSha256")) {
        Assert-LowerSha256 -Value (Get-ExactProperty $value $hashProperty)
    }
    foreach ($signatureProperty in @("BaselineSignatureStatus", "UpdatedSignatureStatus")) {
        Assert-ExactString -Value (Get-ExactProperty $value $signatureProperty) -Expected "Valid"
    }
    Assert-ExactString -Value (Get-ExactProperty $value "DataProtectionScope") -Expected "CurrentUser"
    Assert-ExactString -Value (Get-ExactProperty $value "ProtectedStoreVersion") -Expected "v2"
    foreach ($propertyName in @(
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
            "PackageOutputRemoved")) {
        Assert-True -Value (Get-ExactProperty $value $propertyName)
    }
}

function Test-DpapiInput {
    param(
        [Parameter(Mandatory = $true)]
        [pscustomobject]$InputRecord,

        [Parameter(Mandatory = $true)]
        [string]$CommitSha,

        [Parameter(Mandatory = $true)]
        [string]$SdkVersion
    )

    $value = $InputRecord.Value
    Assert-ExactInteger -Value (Get-ExactProperty $value "SchemaVersion") -Expected 1
    Assert-CommitBinding -Value $value -PropertyName "CommitSha" -CommitSha $CommitSha
    Assert-ExactString -Value (Get-ExactProperty $value "Milestone") -Expected "M4"
    Assert-ExactString `
        -Value (Get-ExactProperty $value "EvidenceKind") `
        -Expected "dpapi-current-user-boundary"
    Assert-ExactString -Value (Get-ExactProperty $value "Configuration") -Expected "Release"
    Assert-ExactString -Value (Get-ExactProperty $value "Platform") -Expected "x64"
    Assert-ExactString -Value (Get-ExactProperty $value "DataProtectionScope") -Expected "CurrentUser"
    Assert-ExactString -Value (Get-ExactProperty $value "DotNetSdk") -Expected $SdkVersion
    foreach ($propertyName in @(
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
            "EvidenceCanaryScanPassed")) {
        Assert-True -Value (Get-ExactProperty $value $propertyName)
    }
    Assert-LowerSha256 -Value (Get-ExactProperty $value "ControllerScriptSha256")
    Assert-LowerSha256 -Value (Get-ExactProperty $value "HarnessAssemblySha256")
}

function Test-NativeTierAInput {
    param(
        [Parameter(Mandatory = $true)]
        [pscustomobject]$InputRecord,

        [Parameter(Mandatory = $true)]
        [string]$CommitSha,

        [Parameter(Mandatory = $true)]
        [string]$SdkVersion,

        [Parameter(Mandatory = $true)]
        [string]$Root
    )

    $value = $InputRecord.Value
    Assert-ExactInteger -Value (Get-ExactProperty $value "SchemaVersion") -Expected 10
    Assert-CommitBinding -Value $value -PropertyName "CommitSha" -CommitSha $CommitSha
    Assert-ExactString -Value (Get-ExactProperty $value "Stage") -Expected "M10NativeTierAPlayback"
    Assert-ExactString -Value (Get-ExactProperty $value "Result") -Expected "Passed"
    $runId = Get-ExactProperty $value "RunId"
    Assert-Condition `
        ($runId -is [string] -and $runId -cmatch '^[0-9a-f]{32}$') `
        "InputContractInvalid"
    Assert-UtcTimestamp -Value (Get-ExactProperty $value "CompletedAtUtc")
    Assert-ExactString -Value (Get-ExactProperty $value "Configuration") -Expected "Release"
    Assert-ExactString -Value (Get-ExactProperty $value "Platform") -Expected "x64"
    Assert-ExactString -Value (Get-ExactProperty $value "DotNetSdk") -Expected $SdkVersion
    Assert-ExactInteger -Value (Get-ExactProperty $value "ProbeEnvelopeSchemaVersion") -Expected 8
    Assert-ExactInteger -Value (Get-ExactProperty $value "SwitchCount") -Expected 100
    Assert-ExactInteger -Value (Get-ExactProperty $value "SoakMinutes") -Expected 0
    Assert-ExactInteger -Value (Get-ExactProperty $value "ResourceSampleCount") -Expected 0
    Assert-ExactInteger -Value (Get-ExactProperty $value "WarmupPrivateBytes") -Expected 0
    Assert-ExactInteger -Value (Get-ExactProperty $value "MemoryNetGrowthBytes") -Expected 0
    Assert-NumberRange `
        -Value (Get-ExactProperty $value "MemoryNetGrowthPercent") `
        -Minimum 0 `
        -Maximum 0
    Assert-False -Value (Get-ExactProperty $value "MemoryMonotonicIncrease")
    Assert-ExactInteger -Value (Get-ExactProperty $value "WarmupHandleCount") -Expected 0
    Assert-ExactInteger -Value (Get-ExactProperty $value "HandleNetGrowth") -Expected 0
    Assert-ExactInteger -Value (Get-ExactProperty $value "SurfaceTransitionCount") -Expected 6
    Assert-True -Value (Get-ExactProperty $value "CleanHeadBound")
    Assert-True -Value (Get-ExactProperty $value "FixtureCorpusVerified")
    Assert-True -Value (Get-ExactProperty $value "ProbeRunIdBound")
    Assert-ExactString -Value (Get-ExactProperty $value "PackageSignatureStatus") -Expected "Valid"
    Assert-LowerSha256 -Value (Get-ExactProperty $value "PackageSha256")
    $controllerScriptSha256 = Get-ExactProperty $value "ControllerScriptSha256"
    Assert-LowerSha256 -Value $controllerScriptSha256
    [byte[]]$controllerBytes = Read-RegularFileBytes `
        -Path (Join-Path $Root "eng\Invoke-WindowsNativePlaybackSmoke.ps1") `
        -MaximumBytes $script:maximumInputBytes `
        -Root $Root `
        -Code "InputContractInvalid"
    Assert-ExactString `
        -Value $controllerScriptSha256 `
        -Expected (Get-LowerSha256Bytes $controllerBytes)
    Assert-LowerSha256 -Value (Get-ExactProperty $value "HarnessAssemblySha256")
    Assert-LowerSha256 -Value (Get-ExactProperty $value "FixtureManifestSha256")
    Assert-LowerSha256 -Value (Get-ExactProperty $value "RuntimeDependencyPackageSha256")
    Assert-ExactString `
        -Value (Get-ExactProperty $value "RuntimeDependencyPackageSignatureStatus") `
        -Expected "Valid"
    Assert-ExactString `
        -Value (Get-ExactProperty $value "ResolvedWindowsAppRuntimeArchitecture") `
        -Expected "x64"
    Assert-True -Value (Get-ExactProperty $value "ResolvedWindowsAppRuntimeIsFramework")
    $runtimeName = Get-ExactProperty $value "ResolvedWindowsAppRuntimeName"
    $runtimeVersion = Get-ExactProperty $value "ResolvedWindowsAppRuntimeVersion"
    $runtimePublisherId = Get-ExactProperty $value "ResolvedWindowsAppRuntimePublisherId"
    Assert-ExactString -Value $runtimeName -Expected "Microsoft.WindowsAppRuntime.2"
    Assert-Condition `
        ($runtimeVersion -is [string] -and
         $runtimeVersion -cmatch '^\d+\.\d+\.\d+\.\d+$') `
        "InputContractInvalid"
    Assert-ExactString -Value $runtimePublisherId -Expected "8wekyb3d8bbwe"
    Assert-ExactString -Value (Get-ExactProperty $value "Transport") -Expected "Tls12LoopbackAllowlist"
    Assert-ExactStringArray `
        -Value (Get-ExactProperty $value "Fixtures") `
        -Expected @("DirectH264AacMpegTs", "HlsH264AacMpegTs")

    $startupP95 = Get-ExactProperty $value "StartupP95Milliseconds"
    Assert-NumberRange -Value $startupP95 -Minimum 0 -Maximum 3000
    $startupMaximum = Get-ExactProperty $value "StartupMaximumMilliseconds"
    Assert-NumberRange -Value $startupMaximum -Minimum ([double]$startupP95) -Maximum 5000
    Assert-NumberRange `
        -Value (Get-ExactProperty $value "HlsStartupP95Milliseconds") `
        -Minimum 0 `
        -Maximum ([double]$startupMaximum)
    Assert-NumberRange `
        -Value (Get-ExactProperty $value "DirectStartupP95Milliseconds") `
        -Minimum 0 `
        -Maximum ([double]$startupMaximum)
    $sourceDetachP95 = Get-ExactProperty $value "SourceDetachP95Milliseconds"
    $sourceDetachMaximum = Get-ExactProperty $value "SourceDetachMaximumMilliseconds"
    Assert-NumberRange -Value $sourceDetachP95 -Minimum 0 -Maximum 3000
    Assert-NumberRange `
        -Value $sourceDetachMaximum `
        -Minimum ([double]$sourceDetachP95) `
        -Maximum 5000
    $interruptions = Get-ExactProperty $value "NetworkInterruptionCount"
    $recoveries = Get-ExactProperty $value "NetworkRecoveryCount"
    Assert-ExactInteger -Value $interruptions -Expected 1
    Assert-ExactInteger -Value $recoveries -Expected 1
    $lastInjectedOrdinal = Get-ExactProperty $value "LastInjectedRequestOrdinal"
    $lastRecoveryOrdinal = Get-ExactProperty $value "LastRecoveryRequestOrdinal"
    Assert-IntegerRange -Value $lastInjectedOrdinal -Minimum 1 -Maximum ([long]::MaxValue)
    Assert-IntegerRange -Value $lastRecoveryOrdinal -Minimum 1 -Maximum ([long]::MaxValue)
    Assert-Condition ([long]$lastRecoveryOrdinal -gt [long]$lastInjectedOrdinal) `
        "InputContractInvalid"
    $playbackRetryCount = Get-ExactProperty $value "PlaybackRetryCount"
    Assert-IntegerRange -Value $playbackRetryCount -Minimum 0 -Maximum 1
    Assert-ExactInteger -Value (Get-ExactProperty $value "CancellationProbeCount") -Expected 1
    Assert-ExactInteger -Value (Get-ExactProperty $value "CancellationObservedCount") -Expected 1
    Assert-ExactInteger -Value (Get-ExactProperty $value "CancellationSourceDetachCount") -Expected 1
    Assert-ExactInteger -Value (Get-ExactProperty $value "CancellationRecoveryCount") -Expected 1
    Assert-ExactInteger `
        -Value (Get-ExactProperty $value "CancellationRecoverySourceDetachCount") `
        -Expected 1
    $expectedDetachedSources = 102 + [long]$playbackRetryCount
    Assert-ExactInteger `
        -Value (Get-ExactProperty $value "DetachedSourceCount") `
        -Expected $expectedDetachedSources
    $cancellationLatency = Get-ExactProperty $value "CancellationLatencyMilliseconds"
    $cancellationQuiescence = Get-ExactProperty $value "CancellationQuiescenceMilliseconds"
    $cancellationObservation = Get-ExactProperty $value "CancellationObservationMilliseconds"
    $cancellationDetach = Get-ExactProperty $value "CancellationSourceDetachMilliseconds"
    $cancellationRecoveryStartup =
        Get-ExactProperty $value "CancellationRecoveryStartupMilliseconds"
    $cancellationRecoveryAdvance =
        Get-ExactProperty $value "CancellationRecoveryAdvanceMilliseconds"
    $cancellationRecoveryDetach =
        Get-ExactProperty $value "CancellationRecoverySourceDetachMilliseconds"
    Assert-NumberRange -Value $cancellationLatency -Minimum 0 -Maximum 1000
    Assert-NumberRange -Value $cancellationQuiescence -Minimum 0 -Maximum 1000
    Assert-NumberRange -Value $cancellationObservation -Minimum 1000 -Maximum 1500
    Assert-NumberRange -Value $cancellationDetach -Minimum 0 -Maximum 5000
    Assert-NumberRange -Value $cancellationRecoveryStartup -Minimum 0 -Maximum 5000
    Assert-NumberRange -Value $cancellationRecoveryAdvance -Minimum 0 -Maximum 3000
    Assert-NumberRange -Value $cancellationRecoveryDetach -Minimum 0 -Maximum 5000
    Assert-Condition `
        ([double]$cancellationRecoveryStartup -gt 0 -and
         [double]$cancellationRecoveryAdvance -gt 0 -and
         ([double]$cancellationLatency + [double]$cancellationDetach) -le
            ([double]$cancellationQuiescence + 0.002) -and
         ([double]$cancellationQuiescence + [double]$cancellationObservation) -ge
            999.998 -and
         [double]$cancellationDetach -le ([double]$sourceDetachMaximum + 0.002) -and
         [double]$cancellationRecoveryDetach -le ([double]$sourceDetachMaximum + 0.002)) `
        "InputContractInvalid"
    foreach ($propertyName in @(
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
            "RepositoryCleanAfterRun")) {
        Assert-True -Value (Get-ExactProperty $value $propertyName)
    }
    Assert-False -Value (Get-ExactProperty $value "ForcedProcessTerminationUsed")
    Assert-Boolean -Value (Get-ExactProperty $value "PackageAppDataEmptyRootCleanupUsed")
    foreach ($counterProperty in @(
            "InitialPrivateBytes",
            "FinalPrivateBytes",
            "InitialHandleCount",
            "FinalHandleCount")) {
        Assert-IntegerRange `
            -Value (Get-ExactProperty $value $counterProperty) `
            -Minimum 0 `
            -Maximum ([long]::MaxValue)
    }
    Assert-IntegerRange `
        -Value (Get-ExactProperty $value "LoopbackRequestCount") `
        -Minimum 100 `
        -Maximum ([long]::MaxValue)
    $runtimeDisposition = Get-ExactProperty $value "RuntimePackageGraphDisposition"
    $runtimeSharedAdditionCount = Get-ExactProperty $value "RuntimePackageSharedAdditionCount"
    Assert-IntegerRange -Value $runtimeSharedAdditionCount -Minimum 0 -Maximum 64
    Assert-Condition `
        (($runtimeDisposition -ceq "ExactRestored" -and
          [long]$runtimeSharedAdditionCount -eq 0) -or
         ($runtimeDisposition -ceq "SharedAdditionsPreserved" -and
          [long]$runtimeSharedAdditionCount -gt 0)) `
        "InputContractInvalid"
}

function Test-CatalogBenchmarkInput {
    param(
        [Parameter(Mandatory = $true)]
        [pscustomobject]$InputRecord,

        [Parameter(Mandatory = $true)]
        [string]$CommitSha,

        [Parameter(Mandatory = $true)]
        [string]$SdkVersion
    )

    $value = $InputRecord.Value
    Assert-ExactInteger -Value (Get-ExactProperty $value "schemaVersion") -Expected 1
    Assert-CommitBinding -Value $value -PropertyName "commitSha" -CommitSha $CommitSha
    Assert-ExactString -Value (Get-ExactProperty $value "milestone") -Expected "M14"
    Assert-ExactString `
        -Value (Get-ExactProperty $value "evidenceKind") `
        -Expected "catalog-performance-benchmark"
    Assert-ExactString -Value (Get-ExactProperty $value "result") -Expected "passed"
    Assert-ExactString -Value (Get-ExactProperty $value "configuration") -Expected "Release"
    Assert-ExactString -Value (Get-ExactProperty $value "platform") -Expected "x64"
    Assert-ExactString -Value (Get-ExactProperty $value "sdkVersion") -Expected $SdkVersion
    Assert-ExactInteger -Value (Get-ExactProperty $value "iterations") -Expected 20
    Assert-ExactInteger `
        -Value (Get-ExactProperty $value "authoritativeWarmIterations") `
        -Expected 20
    Assert-ExactInteger `
        -Value (Get-ExactProperty $value "minimumAuthoritativeWarmIterations") `
        -Expected 20
    Assert-ExactInteger -Value (Get-ExactProperty $value "coldObservationsPerStage") -Expected 1
    foreach ($propertyName in @(
            "measurementIntegrityVerified",
            "authoritativeWarmSampleCountVerified",
            "conditionDeclarationsComplete",
            "referenceModeRequested",
            "referenceEligible")) {
        Assert-True -Value (Get-ExactProperty $value $propertyName)
    }

    $runnerProfile = Get-ExactProperty $value "runnerProfile"
    Assert-Condition ($runnerProfile -is [pscustomobject]) "InputContractInvalid"
    Assert-ExactString `
        -Value (Get-ExactProperty $runnerProfile "verification") `
        -Expected "Declared"
    $runnerProfileValue = Get-ExactProperty $runnerProfile "value"
    Assert-Condition `
        ($runnerProfileValue -is [string] -and
         $runnerProfileValue -cmatch '^[a-z0-9][a-z0-9._-]{0,63}$') `
        "InputContractInvalid"
    $processor = Get-ExactProperty $value "processor"
    Assert-Condition ($processor -is [pscustomobject]) "InputContractInvalid"
    Assert-ExactString `
        -Value (Get-ExactProperty $processor "verification") `
        -Expected "Observed"
    $processorValue = Get-ExactProperty $processor "value"
    Assert-Condition `
        ($processorValue -is [string] -and
         -not [string]::IsNullOrWhiteSpace($processorValue) -and
         $processorValue.Length -le 128) `
        "InputContractInvalid"
    Assert-IntegerRange `
        -Value (Get-ExactProperty $value "logicalProcessorCount") `
        -Minimum 1 `
        -Maximum 1024

    $requirements = Get-ExactProperty $value "referenceEligibilityRequirements"
    Assert-Condition ($requirements -is [pscustomobject]) "InputContractInvalid"
    foreach ($propertyName in @(
            "exactConditionDeclarations",
            "declaredRunnerProfile",
            "measurementIntegrity",
            "passingBenchmarkResult")) {
        Assert-True -Value (Get-ExactProperty $requirements $propertyName)
    }
    $conditions = Get-ExactProperty $value "conditions"
    Assert-Condition ($conditions -is [pscustomobject]) "InputContractInvalid"
    $conditionValues = [ordered]@{
        cache = "Warm"
        power = "AcStable"
        thermal = "Nominal"
        background = "Controlled"
    }
    foreach ($conditionName in $conditionValues.Keys) {
        $condition = Get-ExactProperty $conditions $conditionName
        Assert-Condition ($condition -is [pscustomobject]) "InputContractInvalid"
        Assert-ExactString `
            -Value (Get-ExactProperty $condition "verification") `
            -Expected "Declared"
        Assert-ExactString `
            -Value (Get-ExactProperty $condition "value") `
            -Expected $conditionValues[$conditionName]
    }
    Assert-ExactString `
        -Value (Get-ExactProperty $value "plaintextLocatorCanaryScan") `
        -Expected "passed"
    $budgetEvaluation = Get-ExactProperty $value "budgetEvaluation"
    Assert-Condition ($budgetEvaluation -is [pscustomobject]) "InputContractInvalid"
    foreach ($propertyName in @(
            "normalizeProtectPersistIndexPassed",
            "peakWorkingSetSamplingComplete",
            "allPassed")) {
        Assert-True -Value (Get-ExactProperty $budgetEvaluation $propertyName)
    }
    foreach ($metricProperty in @(
            "parserP95Milliseconds",
            "normalizeProtectPersistIndexConservativeUpperBoundP95Milliseconds",
            "combinedImportP95Milliseconds",
            "cancellationP95Milliseconds",
            "firstPageP95Milliseconds",
            "categoryPageP95Milliseconds",
            "searchP95Milliseconds",
            "reopenP95Milliseconds")) {
        Assert-NumberRange `
            -Value (Get-ExactProperty $budgetEvaluation $metricProperty) `
            -Minimum ([double]::Epsilon) `
            -Maximum ([double]::MaxValue)
    }
    $corpusManifest = Get-ExactProperty $value "corpusManifest"
    Assert-Condition ($corpusManifest -is [pscustomobject]) "InputContractInvalid"
    Assert-False -Value (Get-ExactProperty $corpusManifest "retained")
    Assert-LowerSha256 -Value (Get-ExactProperty $corpusManifest "sha256")
    $query50k = Get-ExactProperty $value "query50k"
    Assert-Condition ($query50k -is [pscustomobject]) "InputContractInvalid"
    Assert-ExactInteger -Value (Get-ExactProperty $query50k "recordCount") -Expected 50000
    Assert-ExactInteger -Value (Get-ExactProperty $query50k "iterations") -Expected 20
    $cancellation = Get-ExactProperty $value "cancellation"
    Assert-Condition ($cancellation -is [pscustomobject]) "InputContractInvalid"
    Assert-ExactInteger -Value (Get-ExactProperty $cancellation "recordCount") -Expected 50000
    Assert-ExactInteger -Value (Get-ExactProperty $cancellation "iterations") -Expected 20
    Assert-ExactString `
        -Value (Get-ExactProperty $cancellation "expectedErrorCode") `
        -Expected "OperationCancelled"
    $entryLimitProbe = Get-ExactProperty $value "entryLimitProbe"
    Assert-Condition ($entryLimitProbe -is [pscustomobject]) "InputContractInvalid"
    Assert-ExactInteger -Value (Get-ExactProperty $entryLimitProbe "recordCount") -Expected 100000
    Assert-ExactString `
        -Value (Get-ExactProperty $entryLimitProbe "expectedOutcome") `
        -Expected "EntryLimitFailClosed"
    Assert-ExactInteger `
        -Value (Get-ExactProperty $entryLimitProbe "persistedRowsAfterFailure") `
        -Expected 0
}

function Test-CatalogRegressionInput {
    param(
        [Parameter(Mandatory = $true)]
        [pscustomobject]$InputRecord,

        [Parameter(Mandatory = $true)]
        [pscustomobject]$BenchmarkRecord,

        [Parameter(Mandatory = $true)]
        [string]$CommitSha
    )

    $value = $InputRecord.Value
    Assert-ExactInteger -Value (Get-ExactProperty $value "schemaVersion") -Expected 1
    Assert-ExactString -Value (Get-ExactProperty $value "milestone") -Expected "M14"
    Assert-ExactString `
        -Value (Get-ExactProperty $value "evidenceKind") `
        -Expected "catalog-performance-regression"
    Assert-ExactString -Value (Get-ExactProperty $value "result") -Expected "passed"
    Assert-True -Value (Get-ExactProperty $value "allPassed")
    Assert-ExactString -Value (Get-ExactProperty $value "absoluteBudgetResult") -Expected "passed"

    $candidate = Get-ExactProperty $value "candidate"
    Assert-Condition ($candidate -is [pscustomobject]) "InputContractInvalid"
    Assert-CommitBinding -Value $candidate -PropertyName "commitSha" -CommitSha $CommitSha
    Assert-ExactString `
        -Value (Get-ExactProperty $candidate "sha256") `
        -Expected $BenchmarkRecord.Sha256
    Assert-ExactInteger `
        -Value (Get-ExactProperty $candidate "byteLength") `
        -Expected $BenchmarkRecord.ByteLength

    $binding = Get-ExactProperty $value "binding"
    Assert-Condition ($binding -is [pscustomobject]) "InputContractInvalid"
    Assert-False -Value (Get-ExactProperty $binding "physicalMachineIdentityVerified")
    foreach ($propertyName in @(
            "baselineCommitAncestorOrSelf",
            "baselineContentStable",
            "exactEnvironmentMatch",
            "exactWorkloadMatch",
            "exactBudgetContractMatch",
            "exactSchemaMatch")) {
        Assert-True -Value (Get-ExactProperty $binding $propertyName)
    }
    $regressionRunnerProfile = Get-ExactProperty $binding "runnerProfile"
    $benchmarkRunnerProfile = Get-ExactProperty $BenchmarkRecord.Value "runnerProfile"
    Assert-Condition `
        ($regressionRunnerProfile -is [pscustomobject] -and
         $benchmarkRunnerProfile -is [pscustomobject]) `
        "InputContractInvalid"
    Assert-ExactString `
        -Value (Get-ExactProperty $regressionRunnerProfile "verification") `
        -Expected (Get-ExactProperty $benchmarkRunnerProfile "verification")
    Assert-ExactString `
        -Value (Get-ExactProperty $regressionRunnerProfile "value") `
        -Expected (Get-ExactProperty $benchmarkRunnerProfile "value")
    $threshold = Get-ExactProperty $value "threshold"
    Assert-Condition ($threshold -is [pscustomobject]) "InputContractInvalid"
    Assert-ExactString -Value (Get-ExactProperty $threshold "metric") -Expected "p95"
    Assert-NumberRange `
        -Value (Get-ExactProperty $threshold "maximumIncreasePercent") `
        -Minimum 10 `
        -Maximum 10
    $expectedMetricNames = @(
        "parser-p95",
        "normalize-protect-persist-index-upper-bound-p95",
        "combined-import-p95",
        "cancellation-p95",
        "first-page-p95",
        "category-page-p95",
        "search-p95",
        "reopen-p95"
    )
    $metrics = @(Get-ExactProperty $value "metrics")
    Assert-Condition ($metrics.Count -eq $expectedMetricNames.Count) "InputContractInvalid"
    for ($index = 0; $index -lt $expectedMetricNames.Count; $index++) {
        $metric = $metrics[$index]
        Assert-Condition ($metric -is [pscustomobject]) "InputContractInvalid"
        Assert-ExactString `
            -Value (Get-ExactProperty $metric "name") `
            -Expected $expectedMetricNames[$index]
        Assert-ExactString -Value (Get-ExactProperty $metric "unit") -Expected "milliseconds"
        Assert-True -Value (Get-ExactProperty $metric "passed")
    }
}

function Test-M15Input {
    param(
        [Parameter(Mandatory = $true)]
        [pscustomobject]$InputRecord,

        [Parameter(Mandatory = $true)]
        [string]$CommitSha
    )

    $value = $InputRecord.Value
    Assert-ExactInteger -Value (Get-ExactProperty $value "schemaVersion") -Expected 6
    Assert-CommitBinding -Value $value -PropertyName "commitSha" -CommitSha $CommitSha
    Assert-ExactString -Value (Get-ExactProperty $value "result") -Expected "blocked"
    Assert-True -Value (Get-ExactProperty $value "technicalBaselinePassed")
    Assert-False -Value (Get-ExactProperty $value "releaseReady")

    $requiredBlockers = @(
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
        "WackPending"
    )
    $blockers = @(Get-ExactProperty $value "blockers")
    Assert-Condition ($blockers.Count -eq $requiredBlockers.Count) `
        "M15BlockerSetInvalid"
    $seen = New-Object 'System.Collections.Generic.HashSet[string]' `
        ([System.StringComparer]::Ordinal)
    for ($index = 0; $index -lt $blockers.Count; $index++) {
        $blocker = $blockers[$index]
        Assert-Condition `
            ($blocker -is [string] -and
             $blocker -ceq $requiredBlockers[$index] -and
             $seen.Add($blocker)) `
            "M15BlockerSetInvalid"
    }
    foreach ($requiredBlocker in $requiredBlockers) {
        Assert-Condition ($seen.Contains($requiredBlocker)) "M15BlockerSetInvalid"
    }
    return [string[]]$blockers
}

function Get-InputEvidenceSummary {
    param(
        [Parameter(Mandatory = $true)]
        [pscustomobject]$InputRecord,

        [Parameter(Mandatory = $true)]
        [string]$CommitSha,

        [Parameter(Mandatory = $true)]
        [string]$Result
    )

    return [ordered]@{
        name = $InputRecord.Name
        byteLength = $InputRecord.ByteLength
        sha256 = $InputRecord.Sha256
        commitSha = $CommitSha
        result = $Result
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

    $json = $Value | ConvertTo-Json -Depth $script:maximumJsonDepth
    [byte[]]$bytes = $script:utf8NoBom.GetBytes($json)
    Assert-Condition `
        ($bytes.Length -gt 0 -and $bytes.Length -le $script:maximumOutputBytes) `
        "EvidenceSizeInvalid"

    $parent = [System.IO.Path]::GetDirectoryName($DestinationPath)
    Assert-Condition (-not [string]::IsNullOrWhiteSpace($parent)) "EvidencePathInvalid"
    Assert-NoReparseDirectoryChain -Root $Root -DirectoryPath $parent
    [System.IO.Directory]::CreateDirectory($parent) | Out-Null
    Assert-NoReparseDirectoryChain -Root $Root -DirectoryPath $parent

    if (Test-Path -LiteralPath $DestinationPath) {
        $existing = Get-Item -LiteralPath $DestinationPath -Force
        Assert-Condition (-not $existing.PSIsContainer) "EvidenceDestinationInvalid"
        Assert-Condition `
            (($existing.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -eq 0) `
            "EvidenceDestinationInvalid"
        Assert-Condition `
            ($existing.Length -gt 0 -and $existing.Length -le $script:maximumOutputBytes) `
            "EvidenceDestinationInvalid"
    }

    $temporaryPath = Join-Path $parent (".m16-rc.{0}.tmp" -f [Guid]::NewGuid().ToString("N"))
    $backupPath = Join-Path $parent (".m16-rc.{0}.backup" -f [Guid]::NewGuid().ToString("N"))
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
            [System.IO.File]::Replace($temporaryPath, $DestinationPath, $backupPath, $true)
            [System.IO.File]::Delete($backupPath)
        }
        else {
            [System.IO.File]::Move($temporaryPath, $DestinationPath)
        }

        [byte[]]$publishedBytes = Read-RegularFileBytes `
            -Path $DestinationPath `
            -MaximumBytes $script:maximumOutputBytes `
            -Root $Root `
            -Code "EvidencePublicationInvalid"
        Assert-Condition `
            ($publishedBytes.Length -eq $bytes.Length -and
             (Get-LowerSha256Bytes $publishedBytes) -ceq (Get-LowerSha256Bytes $bytes)) `
            "EvidencePublicationInvalid"
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

try {
    $script:technicalStage = "RepositoryBinding"
    if ([string]::IsNullOrWhiteSpace($RepositoryRoot)) {
        $RepositoryRoot = Join-Path $PSScriptRoot ".."
    }
    $resolvedRepositoryRoot = [System.IO.Path]::GetFullPath($RepositoryRoot)
    Assert-Condition `
        (Test-Path -LiteralPath $resolvedRepositoryRoot -PathType Container) `
        "RepositoryRootInvalid"
    $repositoryItem = Get-Item -LiteralPath $resolvedRepositoryRoot -Force
    Assert-Condition `
        (($repositoryItem.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -eq 0) `
        "RepositoryRootInvalid"
    foreach ($marker in @("global.json", "apps\windows\src", "eng")) {
        Assert-Condition `
            (Test-Path -LiteralPath (Join-Path $resolvedRepositoryRoot $marker)) `
            "RepositoryLayoutInvalid"
    }
    $repositoryCommit = Get-CleanRepositoryCommit -Root $resolvedRepositoryRoot

    $script:technicalStage = "EvidencePath"
    $artifactRoot = [System.IO.Path]::GetFullPath(
        (Join-Path $resolvedRepositoryRoot ".artifacts"))
    $inputRoot = [System.IO.Path]::GetFullPath(
        (Join-Path $artifactRoot "m16-release-candidate\inputs"))
    if ([string]::IsNullOrWhiteSpace($EvidencePath)) {
        $EvidencePath = Join-Path $artifactRoot "m16-release-candidate\rc-summary.json"
    }
    elseif (-not [System.IO.Path]::IsPathRooted($EvidencePath)) {
        $EvidencePath = Join-Path $resolvedRepositoryRoot $EvidencePath
    }
    Assert-NoAlternateDataStreamPath -Path $EvidencePath -Code "EvidencePathInvalid"
    $resolvedEvidencePath = [System.IO.Path]::GetFullPath($EvidencePath)
    Assert-Condition `
        (Test-PathContainedByRoot -Path $resolvedEvidencePath -Root $artifactRoot) `
        "EvidencePathInvalid"
    Assert-Condition `
        (-not $resolvedEvidencePath.Equals(
            $artifactRoot,
            [System.StringComparison]::OrdinalIgnoreCase) -and
         -not $resolvedEvidencePath.Equals(
            $inputRoot,
            [System.StringComparison]::OrdinalIgnoreCase) -and
         -not (Test-PathContainedByRoot -Path $resolvedEvidencePath -Root $inputRoot)) `
        "EvidencePathInvalid"
    Assert-NoReparseDirectoryChain `
        -Root $resolvedRepositoryRoot `
        -DirectoryPath ([System.IO.Path]::GetDirectoryName($resolvedEvidencePath))
    Assert-Condition (Test-Path -LiteralPath $inputRoot -PathType Container) "InputDirectoryInvalid"
    Assert-NoReparseDirectoryChain -Root $resolvedRepositoryRoot -DirectoryPath $inputRoot

    $script:technicalStage = "SdkBinding"
    $globalJsonRecord = Read-StrictJsonRecord `
        -Path (Join-Path $resolvedRepositoryRoot "global.json") `
        -Root $resolvedRepositoryRoot `
        -Name "global.json" `
        -MaximumBytes 32KB
    $sdk = Get-ExactProperty $globalJsonRecord.Value "sdk"
    Assert-Condition ($sdk -is [pscustomobject]) "SdkContractInvalid"
    $sdkVersion = Get-ExactProperty $sdk "version" "SdkContractInvalid"
    Assert-Condition `
        ($sdkVersion -is [string] -and $sdkVersion -cmatch '^\d+\.\d+\.\d+$') `
        "SdkContractInvalid"
    Assert-ExactString `
        -Value (Get-ExactProperty $sdk "rollForward" "SdkContractInvalid") `
        -Expected "disable" `
        -Code "SdkContractInvalid"
    Assert-False `
        -Value (Get-ExactProperty $sdk "allowPrerelease" "SdkContractInvalid") `
        -Code "SdkContractInvalid"

    $script:technicalStage = "InputValidation"
    $inputSpecifications = @(
        [pscustomobject]@{ Name = "quality-summary.json"; Kind = "quality" },
        [pscustomobject]@{ Name = "package-smoke-success.json"; Kind = "package-smoke" },
        [pscustomobject]@{ Name = "package-lifecycle-success.json"; Kind = "package-lifecycle" },
        [pscustomobject]@{ Name = "dpapi-user-boundary-success.json"; Kind = "dpapi-user-boundary" },
        [pscustomobject]@{ Name = "native-tier-a-success.json"; Kind = "native-tier-a" },
        [pscustomobject]@{ Name = "catalog-benchmark-summary.json"; Kind = "catalog-benchmark" },
        [pscustomobject]@{ Name = "catalog-regression-summary.json"; Kind = "catalog-regression" }
    )
    $requiredInputNames = [string[]]@($inputSpecifications | ForEach-Object { $_.Name })
    $preGenerationInputNames = [string[]]@($requiredInputNames)
    if (Test-Path -LiteralPath (Join-Path $inputRoot "m15-readiness.json") -PathType Leaf) {
        $preGenerationInputNames += "m15-readiness.json"
    }
    Assert-ExactInputDirectoryInventory `
        -InputRoot $inputRoot `
        -ExpectedNames $preGenerationInputNames `
        -Root $resolvedRepositoryRoot
    $inputRecords = [ordered]@{}
    [long]$aggregateInputBytes = 0
    foreach ($specification in $inputSpecifications) {
        $record = Read-StrictJsonRecord `
            -Path (Join-Path $inputRoot $specification.Name) `
            -Root $resolvedRepositoryRoot `
            -Name $specification.Name
        $aggregateInputBytes += $record.ByteLength
        Assert-Condition `
            ($aggregateInputBytes -le $script:maximumAggregateInputBytes) `
            "AggregateInputSizeInvalid"
        $inputRecords[$specification.Kind] = $record
    }

    Test-QualityInput $inputRecords["quality"] $repositoryCommit $sdkVersion
    Test-PackageSmokeInput $inputRecords["package-smoke"] $repositoryCommit $sdkVersion
    Test-PackageLifecycleInput $inputRecords["package-lifecycle"] $repositoryCommit $sdkVersion
    Test-DpapiInput $inputRecords["dpapi-user-boundary"] $repositoryCommit $sdkVersion
    Test-NativeTierAInput `
        $inputRecords["native-tier-a"] `
        $repositoryCommit `
        $sdkVersion `
        $resolvedRepositoryRoot
    Test-CatalogBenchmarkInput $inputRecords["catalog-benchmark"] $repositoryCommit $sdkVersion
    Test-CatalogRegressionInput `
        $inputRecords["catalog-regression"] `
        $inputRecords["catalog-benchmark"] `
        $repositoryCommit

    $script:technicalStage = "M15ReadinessGeneration"
    $m15ScriptPath = Join-Path $resolvedRepositoryRoot "eng\Test-WindowsReleaseReadiness.ps1"
    Assert-Condition (Test-Path -LiteralPath $m15ScriptPath -PathType Leaf) "M15ReadinessInvalid"
    $m15Path = Join-Path $inputRoot "m15-readiness.json"
    & $m15ScriptPath `
        -AllowBlockedInventory `
        -RepositoryRoot $resolvedRepositoryRoot `
        -EvidencePath $m15Path | Out-Null

    Assert-ExactInputDirectoryInventory `
        -InputRoot $inputRoot `
        -ExpectedNames ([string[]]@($requiredInputNames + "m15-readiness.json")) `
        -Root $resolvedRepositoryRoot

    $m15Record = Read-StrictJsonRecord `
        -Path $m15Path `
        -Root $resolvedRepositoryRoot `
        -Name "m15-readiness.json"
    $aggregateInputBytes += $m15Record.ByteLength
    Assert-Condition `
        ($aggregateInputBytes -le $script:maximumAggregateInputBytes) `
        "AggregateInputSizeInvalid"
    $m15Blockers = @(Test-M15Input $m15Record $repositoryCommit)
    $inputRecords["m15-readiness"] = $m15Record

    $script:technicalStage = "InputStability"
    foreach ($key in @($inputRecords.Keys)) {
        $originalRecord = $inputRecords[$key]
        $stableRecord = Read-StrictJsonRecord `
            -Path $originalRecord.Path `
            -Root $resolvedRepositoryRoot `
            -Name $originalRecord.Name
        Assert-Condition `
            ($stableRecord.ByteLength -eq $originalRecord.ByteLength -and
             $stableRecord.Sha256 -ceq $originalRecord.Sha256) `
            "InputChanged"
    }
    $publicationCommit = Get-CleanRepositoryCommit -Root $resolvedRepositoryRoot
    Assert-Condition ($publicationCommit -ceq $repositoryCommit) "RepositoryChanged"

    $script:technicalStage = "EvidenceComposition"
    $m16BlockerDefinitions = @(
        [ordered]@{ code = "M16FeatureFreezeDecisionPending"; category = "Governance"; origin = "M16"; closureMode = "RecordedDecisionRequired" },
        [ordered]@{ code = "M16SyntheticEndToEndJourneyPending"; category = "Technical"; origin = "M16"; closureMode = "AutomatedEvidenceRequired" },
        [ordered]@{ code = "M16FinalArtifactCanaryScanPending"; category = "Security"; origin = "M16"; closureMode = "AutomatedEvidenceRequired" },
        [ordered]@{ code = "M16FinalSecurityArchitectureScanPending"; category = "Security"; origin = "M16"; closureMode = "AutomatedEvidenceRequired" },
        [ordered]@{ code = "M16TwentyFourHourSoakPending"; category = "Reliability"; origin = "M16"; closureMode = "OperatorEvidenceRequired" },
        [ordered]@{ code = "M16PhysicalDeviceAccessibilityMatrixPending"; category = "Accessibility"; origin = "M16"; closureMode = "OperatorEvidenceRequired" },
        [ordered]@{ code = "M16ReleaseOperationsPlanPending"; category = "Operations"; origin = "M16"; closureMode = "RecordedDecisionRequired" }
    )
    $blockerDefinitions = @()
    foreach ($m15Blocker in $m15Blockers) {
        $blockerDefinitions += [ordered]@{
            code = $m15Blocker
            category = "External"
            origin = "M15"
            closureMode = "ExternalEvidenceRequired"
        }
    }
    $blockerDefinitions += $m16BlockerDefinitions
    $blockerDefinitions = @($blockerDefinitions | Sort-Object { $_.code })
    $blockerCodes = @($blockerDefinitions | ForEach-Object { $_.code })
    Assert-Condition `
        (@($blockerCodes | Sort-Object -Unique).Count -eq $blockerCodes.Count) `
        "BlockerSetInvalid"

    $inputEvidence = @(
        Get-InputEvidenceSummary $inputRecords["quality"] $repositoryCommit "passed"
        Get-InputEvidenceSummary $inputRecords["package-smoke"] $repositoryCommit "passed"
        Get-InputEvidenceSummary $inputRecords["package-lifecycle"] $repositoryCommit "passed"
        Get-InputEvidenceSummary $inputRecords["dpapi-user-boundary"] $repositoryCommit "passed"
        Get-InputEvidenceSummary $inputRecords["native-tier-a"] $repositoryCommit "passed"
        Get-InputEvidenceSummary $inputRecords["catalog-benchmark"] $repositoryCommit "passed"
        Get-InputEvidenceSummary $inputRecords["catalog-regression"] $repositoryCommit "passed"
        Get-InputEvidenceSummary $inputRecords["m15-readiness"] $repositoryCommit "blocked"
    )
    $releasePackageSha256 = Get-ExactProperty `
        $inputRecords["package-smoke"].Value `
        "PackageSha256"
    $summary = [ordered]@{
        schemaVersion = 1
        milestone = "M16"
        evidenceKind = "WindowsMvpReleaseCandidateGate"
        result = "blocked"
        aggregationIntegrityPassed = $true
        m1ToM15AutomatedGateSetPassed = $true
        m16TechnicalGateSetPassed = $false
        candidateReady = $false
        commitSha = $repositoryCommit
        evaluatedAtUtc = [DateTime]::UtcNow.ToString("O")
        releasePackageSha256 = $releasePackageSha256
        policy = [ordered]@{
            schemaVersion = 1
            schemaVersionOneCandidateReadyAllowed = $false
            inputDirectory = ".artifacts/m16-release-candidate/inputs"
            exactInputCount = 8
            maximumInputBytes = $script:maximumInputBytes
            maximumAggregateInputBytes = $script:maximumAggregateInputBytes
            maximumOutputBytes = $script:maximumOutputBytes
            maximumJsonDepth = $script:maximumJsonDepth
            maximumObjectPropertyCount = $script:maximumObjectPropertyCount
            maximumArrayLength = $script:maximumArrayLength
            maximumStringLength = $script:maximumStringLength
            repositoryMustBeClean = $true
            commitBindingRequired = $true
            blockedEvidencePublishedBeforeDefaultFailure = $true
        }
        inputs = @($inputEvidence)
        gates = @(
            [ordered]@{
                code = "M1ToM15AutomatedGateSet"
                result = "passed"
                evidenceCount = 8
            },
            [ordered]@{
                code = "M16TechnicalGateSet"
                result = "blocked"
                blockerCount = $m16BlockerDefinitions.Count
            },
            [ordered]@{
                code = "ReleaseCandidate"
                result = "blocked"
                candidateReady = $false
            }
        )
        blockerCounts = [ordered]@{
            total = $blockerDefinitions.Count
            m15 = $m15Blockers.Count
            m16 = $m16BlockerDefinitions.Count
            external = $m15Blockers.Count
            automatedOrRecordedM16 = @($m16BlockerDefinitions | Where-Object {
                    $_.closureMode -cne "OperatorEvidenceRequired"
                }).Count
            operatorM16 = @($m16BlockerDefinitions | Where-Object {
                    $_.closureMode -ceq "OperatorEvidenceRequired"
                }).Count
        }
        blockers = @($blockerDefinitions)
        nonClaims = @(
            "Schema version 1 cannot assert candidateReady=true.",
            "Blocked M15 inventory does not constitute Store, legal, signing, WACK, flight, or reviewer-service approval.",
            "The short native Tier A smoke is not the M16 twenty-four-hour soak.",
            "Synthetic and hosted evidence does not complete the physical-device accessibility matrix.",
            "This aggregator does not start a long-running soak, modify product architecture, or close external blockers."
        )
    }

    $script:technicalStage = "PrePublicationBinding"
    foreach ($key in @($inputRecords.Keys)) {
        $originalRecord = $inputRecords[$key]
        $stableRecord = Read-StrictJsonRecord `
            -Path $originalRecord.Path `
            -Root $resolvedRepositoryRoot `
            -Name $originalRecord.Name
        Assert-Condition `
            ($stableRecord.ByteLength -eq $originalRecord.ByteLength -and
             $stableRecord.Sha256 -ceq $originalRecord.Sha256) `
            "InputChanged"
    }
    $prePublicationCommit = Get-CleanRepositoryCommit -Root $resolvedRepositoryRoot
    Assert-Condition ($prePublicationCommit -ceq $repositoryCommit) "RepositoryChanged"

    $script:technicalStage = "EvidencePublication"
    Publish-BoundedEvidence `
        -Value $summary `
        -DestinationPath $resolvedEvidencePath `
        -Root $resolvedRepositoryRoot
}
catch {
    if ($_.Exception.Message -match '^M16TechnicalInvariant:[A-Za-z][A-Za-z0-9]+$') {
        throw $_.Exception.Message
    }

    throw "M16TechnicalInvariant:$($script:technicalStage)Failed"
}

if (-not $AllowBlockedCandidate) {
    throw "M16ReleaseCandidateBlocked: candidateReady=false; evidence was published."
}

$summary
