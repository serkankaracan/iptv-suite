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
$script:maximumFinalArtifactAcceptanceBytes = 32KB
$script:maximumProducerContractSourceBytes = 2MB
$script:maximumJsonDepth = 16
$script:maximumObjectPropertyCount = 1024
$script:maximumArrayLength = 4096
$script:maximumStringLength = 4096
$script:maximumJsonNodeCount = 65536
$script:technicalStage = "Initialization"
$script:utf8Strict = New-Object System.Text.UTF8Encoding($false, $true)
$script:utf8NoBom = New-Object System.Text.UTF8Encoding($false)
$script:finalArtifactAcceptanceRelativePath =
    "eng/windows-m16-final-artifact-acceptance.json"
$script:finalArtifactAcceptanceSha256 =
    "d0da8a15ff410886c7f9450a8a0ec4c1fe0e463a951b665c2797d178da4db91a"
$script:finalArtifactProducerContractSourceCount = 39
$script:finalArtifactProducerContractSourceSetSha256 =
    "18b20bf208943c6ac9cc1ac4075f3df3f7668765bdf3833b03de664134bae6ae"
$script:packageProducingSnapshotFileCount = 115
$script:packageProducingSnapshotSha256 =
    "5568fb8fc87f614392762501cb2a4b3be1a13487bb8cfab037ccaec579756810"
$script:syntheticJourneyAcceptanceRelativePath =
    "eng/windows-m16-synthetic-journey-acceptance.json"
$script:syntheticJourneyAcceptanceSha256 =
    "50d867fd845e96bb4ad9207fc356bce891e36801d250e8f2d5e1f04e968a8480"
$script:syntheticJourneyProducerContractSourceCount = 132
$script:syntheticJourneyProducerContractSourceSetSha256 =
    "08ef66d9ce752f91721cfcf9a3b848cfb69eb45fd454dfd674342e29a4a961ca"
$script:securityArchitectureAcceptanceRelativePath =
    "eng/windows-m16-security-architecture-acceptance.json"
$script:securityArchitectureAcceptanceSha256 =
    "f8707e534c7b31a4d9d6e88cf143256d2bd549dc1475cffb9185cfdf7df8d864"
$script:securityArchitectureProducerContractSourceCount = 329
$script:securityArchitectureProducerContractCanonicalByteLength = 7162233
$script:securityArchitectureProducerContractSourceSetSha256 =
    "580bbf89b427828db09485310f0d2284e2b4f24fda947d6e2cc9d721c78b2265"

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
        $current = [System.IO.Path]::Combine($current, $part)
        $directory = [System.IO.DirectoryInfo]::new($current)
        $directory.Refresh()
        if ($directory.Exists) {
            Assert-Condition `
                (($directory.Attributes -band [System.IO.FileAttributes]::Directory) -ne 0) `
                $Code
            Assert-Condition `
                (($directory.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -eq 0) `
                $Code
        }
        else {
            Assert-Condition (-not [System.IO.File]::Exists($current)) $Code
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
        $item = [System.IO.FileInfo]::new($fullPath)
        $item.Refresh()
        Assert-Condition $item.Exists $Code
        Assert-Condition `
            (($item.Attributes -band [System.IO.FileAttributes]::Directory) -eq 0) `
            $Code
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

function Get-FinalArtifactProducerContractBinding {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Root
    )

    $code = "FinalArtifactAcceptanceInvalid"
    $directoryContracts = @(
        [pscustomobject]@{
            RelativeRoot = "apps/windows/tests/IptvSuite.Testing"
            IgnoreBuildDirectories = $true
            FileNames = [string[]]@(
                "ArtifactCanaryScanner.cs",
                "FakePlayer.cs",
                "InMemorySecretStore.cs",
                "IptvSuite.Testing.csproj",
                "LocalHttpFixtureServer.cs",
                "M14CatalogCorpusGenerator.cs",
                "NativePlaybackEvidenceValidator.cs",
                "packages.lock.json",
                "Program.cs",
                "ScriptedTransport.cs",
                "SyntheticFixtureGenerator.cs",
                "TemporaryDirectory.cs",
                "TestCanary.cs",
                "TestTime.cs",
                "TimeoutGuard.cs")
        },
        [pscustomobject]@{
            RelativeRoot = "apps/windows/tests/IptvSuite.CatalogUiAcceptanceHarness"
            IgnoreBuildDirectories = $true
            FileNames = [string[]]@(
                "IptvSuite.CatalogUiAcceptanceHarness.csproj",
                "packages.lock.json",
                "Program.cs")
        },
        [pscustomobject]@{
            RelativeRoot = "apps/windows/tests/IptvSuite.PlaybackUiAcceptanceHarness"
            IgnoreBuildDirectories = $true
            FileNames = [string[]]@(
                "IptvSuite.PlaybackUiAcceptanceHarness.csproj",
                "packages.lock.json",
                "Program.cs")
        },
        [pscustomobject]@{
            RelativeRoot = "apps/windows/tests/fixtures/playback/tier-a"
            IgnoreBuildDirectories = $false
            FileNames = [string[]]@(
                "direct-h264-aac.ts",
                "fixture-manifest.json",
                "hls.m3u8",
                "hls-000.ts",
                "hls-001.ts",
                "hls-002.ts",
                "hls-003.ts")
        })
    $relativePaths = [string[]]@(
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
        "eng/windows-package-sbom-tool.json")
    foreach ($directoryContract in $directoryContracts) {
        $relativeRoot = [string]$directoryContract.RelativeRoot
        $relativePaths += [string[]]@($directoryContract.FileNames | ForEach-Object {
                "$relativeRoot/$_"
            })
    }
    Assert-Condition `
        ($relativePaths.Count -eq $script:finalArtifactProducerContractSourceCount) `
        $code

    $inventoryCurrent = $true
    foreach ($directoryContract in $directoryContracts) {
        $contractRoot = [System.IO.Path]::GetFullPath(
            (Join-Path $Root ([string]$directoryContract.RelativeRoot)))
        Assert-Condition `
            (Test-PathContainedByRoot -Path $contractRoot -Root $Root) `
            $code
        Assert-NoReparseDirectoryChain `
            -Root $Root `
            -DirectoryPath $contractRoot `
            -Code $code
        if (-not (Test-Path -LiteralPath $contractRoot -PathType Container)) {
            $inventoryCurrent = $false
            continue
        }
        $actualFileNames = [System.Collections.Generic.List[string]]::new()
        foreach ($entry in @(Get-ChildItem -LiteralPath $contractRoot -Force)) {
            if ($entry.PSIsContainer) {
                Assert-Condition `
                    (($entry.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -eq 0) `
                    $code
                if (-not ($directoryContract.IgnoreBuildDirectories -is [bool] -and
                          $directoryContract.IgnoreBuildDirectories -and
                          ($entry.Name -ceq "bin" -or $entry.Name -ceq "obj"))) {
                    $inventoryCurrent = $false
                }
                continue
            }
            Assert-Condition `
                (($entry.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -eq 0) `
                $code
            $actualFileNames.Add([string]$entry.Name)
        }
        $actualNames = [string[]]$actualFileNames.ToArray()
        $expectedNames = [string[]]@($directoryContract.FileNames)
        [System.Array]::Sort($actualNames, [System.StringComparer]::Ordinal)
        [System.Array]::Sort($expectedNames, [System.StringComparer]::Ordinal)
        if ($actualNames.Count -ne $expectedNames.Count) {
            $inventoryCurrent = $false
        }
        else {
            for ($index = 0; $index -lt $expectedNames.Count; $index++) {
                if ($actualNames[$index] -cne $expectedNames[$index]) {
                    $inventoryCurrent = $false
                }
            }
        }
    }

    foreach ($relativePath in $relativePaths) {
        if (-not (Test-Path `
                -LiteralPath (Join-Path $Root $relativePath) `
                -PathType Leaf)) {
            $inventoryCurrent = $false
        }
    }
    if (-not $inventoryCurrent) {
        return [pscustomobject]@{
            SourceCount = -1
            SourceSetSha256 = ("0" * 64)
        }
    }

    $records = [System.Collections.Generic.List[string]]::new()
    [long]$aggregateBytes = 0
    foreach ($relativePath in $relativePaths) {
        Assert-Condition `
            ($relativePath -cmatch '\A[A-Za-z0-9._/-]+\z' -and
             -not [System.IO.Path]::IsPathRooted($relativePath) -and
             $relativePath -notmatch '(?:^|/)\.\.(?:/|$)') `
            $code
        [byte[]]$sourceBytes = Read-RegularFileBytes `
            -Path (Join-Path $Root $relativePath) `
            -MaximumBytes $script:maximumProducerContractSourceBytes `
            -Root $Root `
            -Code $code
        $isBinary = $relativePath.EndsWith(
            ".ts",
            [System.StringComparison]::OrdinalIgnoreCase)
        if ($isBinary) {
            [byte[]]$canonicalBytes = $sourceBytes
            $kind = "binary"
        }
        else {
            Assert-Condition `
                (-not ($sourceBytes.Length -ge 3 -and
                       $sourceBytes[0] -eq 0xef -and
                       $sourceBytes[1] -eq 0xbb -and
                       $sourceBytes[2] -eq 0xbf)) `
                $code
            try {
                $sourceText = $script:utf8Strict.GetString($sourceBytes)
            }
            catch {
                Fail-TechnicalInvariant -Code $code
            }
            $normalizedText =
                $sourceText.Replace("`r`n", "`n").Replace("`r", "`n")
            [byte[]]$canonicalBytes = $script:utf8NoBom.GetBytes($normalizedText)
            $kind = "text-lf"
        }
        Assert-Condition ($canonicalBytes.Length -gt 0) $code
        $aggregateBytes += $canonicalBytes.Length
        Assert-Condition ($aggregateBytes -le 32MB) $code
        $records.Add(
            "$relativePath`0$kind`0$($canonicalBytes.Length)`0" +
            (Get-LowerSha256Bytes -Bytes $canonicalBytes))
    }

    [byte[]]$bindingBytes = $script:utf8NoBom.GetBytes(
        ([string[]]$records.ToArray() -join "`n"))
    return [pscustomobject]@{
        SourceCount = [int]$records.Count
        SourceSetSha256 = Get-LowerSha256Bytes -Bytes $bindingBytes
    }
}

function Get-SyntheticJourneyProducerContractBinding {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Root
    )

    $code = "SyntheticJourneyAcceptanceInvalid"
    $relativePaths = [System.Collections.Generic.List[string]]::new()
    foreach ($relativePath in @(
            ".github/workflows/windows-quality.yml",
            "eng/Invoke-WindowsQualityGate.ps1",
            "global.json",
            "NuGet.config",
            "Directory.Build.props",
            "Directory.Packages.props",
            "Directory.Solution.props",
            "apps/windows/IptvSuite.Windows.sln")) {
        $relativePaths.Add($relativePath)
    }

    $inventoryCurrent = $true
    foreach ($relativePath in [string[]]$relativePaths.ToArray()) {
        if (-not (Test-Path `
                -LiteralPath (Join-Path $Root $relativePath) `
                -PathType Leaf)) {
            $inventoryCurrent = $false
        }
    }

    $sourceRoots = @(
        "apps/windows/src/IptvSuite.Domain",
        "apps/windows/src/IptvSuite.Application",
        "apps/windows/src/IptvSuite.Infrastructure",
        "apps/windows/tests/IptvSuite.Testing",
        "apps/windows/tests/IptvSuite.IntegrationTests")
    $rootPrefix = $Root.TrimEnd(
        [System.IO.Path]::DirectorySeparatorChar,
        [System.IO.Path]::AltDirectorySeparatorChar) +
        [System.IO.Path]::DirectorySeparatorChar
    foreach ($relativeSourceRoot in $sourceRoots) {
        $sourceRoot = [System.IO.Path]::GetFullPath(
            (Join-Path $Root $relativeSourceRoot))
        Assert-Condition (Test-PathContainedByRoot -Path $sourceRoot -Root $Root) $code
        Assert-NoReparseDirectoryChain `
            -Root $Root `
            -DirectoryPath $sourceRoot `
            -Code $code
        if (-not (Test-Path -LiteralPath $sourceRoot -PathType Container)) {
            $inventoryCurrent = $false
            continue
        }

        $pending = [System.Collections.Generic.Stack[string]]::new()
        $pending.Push($sourceRoot)
        while ($pending.Count -gt 0) {
            $directory = $pending.Pop()
            foreach ($entry in @(Get-ChildItem -LiteralPath $directory -Force)) {
                Assert-Condition `
                    (($entry.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -eq 0) `
                    $code
                if ($entry.PSIsContainer) {
                    if ($entry.Name -ceq "bin" -or $entry.Name -ceq "obj") {
                        continue
                    }
                    $pending.Push($entry.FullName)
                    continue
                }

                if (-not ($entry.Extension -ceq ".cs" -or
                          $entry.Extension -ceq ".csproj" -or
                          $entry.Name -ceq "packages.lock.json")) {
                    $inventoryCurrent = $false
                    continue
                }
                Assert-Condition `
                    ($entry.FullName.StartsWith(
                        $rootPrefix,
                        [System.StringComparison]::OrdinalIgnoreCase)) `
                    $code
                $relativePaths.Add(
                    $entry.FullName.Substring($rootPrefix.Length).Replace('\', '/'))
            }
        }
    }

    if (-not $inventoryCurrent) {
        return [pscustomobject]@{
            SourceCount = -1
            SourceSetSha256 = ("0" * 64)
        }
    }

    $sortedPaths = [string[]]$relativePaths.ToArray()
    [System.Array]::Sort($sortedPaths, [System.StringComparer]::Ordinal)
    for ($index = 1; $index -lt $sortedPaths.Count; $index++) {
        Assert-Condition ($sortedPaths[$index] -cne $sortedPaths[$index - 1]) $code
    }

    $records = [System.Collections.Generic.List[string]]::new()
    [long]$aggregateBytes = 0
    foreach ($relativePath in $sortedPaths) {
        Assert-Condition `
            ($relativePath -cmatch '\A[A-Za-z0-9._/-]+\z' -and
             -not [System.IO.Path]::IsPathRooted($relativePath) -and
             $relativePath -notmatch '(?:^|/)\.\.(?:/|$)') `
            $code
        [byte[]]$sourceBytes = Read-RegularFileBytes `
            -Path (Join-Path $Root $relativePath) `
            -MaximumBytes $script:maximumProducerContractSourceBytes `
            -Root $Root `
            -Code $code
        if ($relativePath.EndsWith(
                ".sln",
                [System.StringComparison]::OrdinalIgnoreCase)) {
            [byte[]]$canonicalBytes = $sourceBytes
            $kind = "binary"
        }
        else {
            Assert-Condition `
                (-not ($sourceBytes.Length -ge 3 -and
                       $sourceBytes[0] -eq 0xef -and
                       $sourceBytes[1] -eq 0xbb -and
                       $sourceBytes[2] -eq 0xbf)) `
                $code
            try {
                $sourceText = $script:utf8Strict.GetString($sourceBytes)
            }
            catch {
                Fail-TechnicalInvariant -Code $code
            }
            $normalizedText =
                $sourceText.Replace("`r`n", "`n").Replace("`r", "`n")
            [byte[]]$canonicalBytes = $script:utf8NoBom.GetBytes($normalizedText)
            $kind = "text-lf"
        }

        Assert-Condition ($canonicalBytes.Length -gt 0) $code
        $aggregateBytes += $canonicalBytes.Length
        Assert-Condition ($aggregateBytes -le 32MB) $code
        $records.Add(
            "$relativePath`0$kind`0$($canonicalBytes.Length)`0" +
            (Get-LowerSha256Bytes -Bytes $canonicalBytes))
    }

    [byte[]]$bindingBytes = $script:utf8NoBom.GetBytes(
        ([string[]]$records.ToArray() -join "`n"))
    return [pscustomobject]@{
        SourceCount = [int]$records.Count
        SourceSetSha256 = Get-LowerSha256Bytes -Bytes $bindingBytes
    }
}

function Get-SecurityArchitectureProducerContractBinding {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Root
    )

    $code = "SecurityArchitectureAcceptanceInvalid"
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
    $binaryExtensions = [string[]]@(".ico", ".png", ".ts", ".sln")
    $textExtensions = [string[]]@(
        ".appxmanifest",
        ".config",
        ".cs",
        ".csproj",
        ".json",
        ".m3u8",
        ".manifest",
        ".md",
        ".props",
        ".ps1",
        ".resw",
        ".txt",
        ".xaml",
        ".yml")

    $startInfo = New-Object System.Diagnostics.ProcessStartInfo
    $startInfo.FileName = "git"
    $startInfo.Arguments = "ls-files -z --"
    $startInfo.WorkingDirectory = $Root
    $startInfo.UseShellExecute = $false
    $startInfo.RedirectStandardOutput = $true
    $startInfo.CreateNoWindow = $true
    $process = New-Object System.Diagnostics.Process
    $process.StartInfo = $startInfo
    $gitOutput = New-Object System.IO.MemoryStream
    try {
        Assert-Condition $process.Start() $code
        $buffer = New-Object byte[] 8192
        while (($read = $process.StandardOutput.BaseStream.Read(
                    $buffer,
                    0,
                    $buffer.Length)) -gt 0) {
            Assert-Condition (($gitOutput.Length + $read) -le 1MB) $code
            $gitOutput.Write($buffer, 0, $read)
        }
        $process.WaitForExit()
        Assert-Condition ($process.ExitCode -eq 0) $code
        [byte[]]$trackedBytes = $gitOutput.ToArray()
    }
    finally {
        $gitOutput.Dispose()
        $process.Dispose()
    }

    Assert-Condition ($trackedBytes.Length -gt 0) $code
    try {
        $trackedText = $script:utf8Strict.GetString($trackedBytes)
    }
    catch {
        Fail-TechnicalInvariant -Code $code
    }
    Assert-Condition ($trackedText[$trackedText.Length - 1] -eq [char]0) $code
    $trackedEntries = @($trackedText.Split([char]0))
    Assert-Condition ($trackedEntries[$trackedEntries.Count - 1] -ceq "") $code

    $allTrackedPaths = [System.Collections.Generic.List[string]]::new()
    $trackedPathSet = New-Object 'System.Collections.Generic.HashSet[string]' `
        ([System.StringComparer]::Ordinal)
    for ($index = 0; $index -lt ($trackedEntries.Count - 1); $index++) {
        $trackedPath = [string]$trackedEntries[$index]
        Assert-Condition `
            (-not [string]::IsNullOrWhiteSpace($trackedPath) -and
             $trackedPath.Length -le $script:maximumStringLength -and
             $trackedPath -cmatch '\A[A-Za-z0-9._/-]+\z' -and
             -not [System.IO.Path]::IsPathRooted($trackedPath) -and
             $trackedPath -notmatch '(?:^|/)\.\.(?:/|$)' -and
             $trackedPath.IndexOf('\') -lt 0 -and
             $trackedPathSet.Add($trackedPath)) `
            $code
        $allTrackedPaths.Add($trackedPath)
    }
    Assert-Condition ($allTrackedPaths.Count -le $script:maximumArrayLength) $code

    $relativePaths = [System.Collections.Generic.List[string]]::new()
    foreach ($relativePath in [string[]]$allTrackedPaths.ToArray()) {
        $included =
            $relativePath -cmatch '\A\.github/workflows/[^/]+\.yml\z' -or
            $relativePath -cmatch '\Aeng/[^/]+\z' -or
            $relativePath -cmatch '\Aapps/windows/(?:src|tests|testdata)/' -or
            $staticPaths -ccontains $relativePath
        if (-not $included -or $excludedPaths -ccontains $relativePath) {
            continue
        }

        $extension = [System.IO.Path]::GetExtension($relativePath).ToLowerInvariant()
        Assert-Condition `
            ($binaryExtensions -ccontains $extension -or
             $textExtensions -ccontains $extension) `
            $code
        $relativePaths.Add($relativePath)
    }

    $sortedPaths = [string[]]$relativePaths.ToArray()
    [System.Array]::Sort($sortedPaths, [System.StringComparer]::Ordinal)
    $records = [System.Collections.Generic.List[string]]::new()
    [long]$aggregateBytes = 0
    foreach ($relativePath in $sortedPaths) {
        [byte[]]$sourceBytes = Read-RegularFileBytes `
            -Path (Join-Path $Root $relativePath) `
            -MaximumBytes $script:maximumProducerContractSourceBytes `
            -Root $Root `
            -Code $code
        $extension = [System.IO.Path]::GetExtension($relativePath).ToLowerInvariant()
        if ($binaryExtensions -ccontains $extension) {
            [byte[]]$canonicalBytes = $sourceBytes
            $kind = "binary"
        }
        else {
            Assert-Condition `
                (-not ($sourceBytes.Length -ge 3 -and
                       $sourceBytes[0] -eq 0xef -and
                       $sourceBytes[1] -eq 0xbb -and
                       $sourceBytes[2] -eq 0xbf)) `
                $code
            try {
                $sourceText = $script:utf8Strict.GetString($sourceBytes)
            }
            catch {
                Fail-TechnicalInvariant -Code $code
            }
            $normalizedText =
                $sourceText.Replace("`r`n", "`n").Replace("`r", "`n")
            [byte[]]$canonicalBytes = $script:utf8NoBom.GetBytes($normalizedText)
            $kind = "text-lf"
        }

        Assert-Condition ($canonicalBytes.Length -gt 0) $code
        $aggregateBytes += $canonicalBytes.Length
        Assert-Condition ($aggregateBytes -le 16MB) $code
        $records.Add(
            "$relativePath`0$kind`0$($canonicalBytes.Length)`0" +
            (Get-LowerSha256Bytes -Bytes $canonicalBytes))
    }

    [byte[]]$bindingBytes = $script:utf8NoBom.GetBytes(
        ([string[]]$records.ToArray() -join "`n"))
    return [pscustomobject]@{
        SourceCount = [int]$records.Count
        CanonicalByteLength = [long]$aggregateBytes
        SourceSetSha256 = Get-LowerSha256Bytes -Bytes $bindingBytes
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

function Assert-ExactPropertyNames {
    param(
        [Parameter(Mandatory = $true)]
        [pscustomobject]$Value,

        [Parameter(Mandatory = $true)]
        [string[]]$Expected,

        [string]$Code = "InputContractInvalid"
    )

    $actual = [string[]]@($Value.PSObject.Properties.Name)
    Assert-Condition ($actual.Count -eq $Expected.Count) $Code
    for ($index = 0; $index -lt $Expected.Count; $index++) {
        Assert-Condition ($actual[$index] -ceq $Expected[$index]) $Code
    }
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
    Assert-ExactInteger -Value (Get-ExactProperty $value "schemaVersion") -Expected 7
    Assert-CommitBinding -Value $value -PropertyName "commitSha" -CommitSha $CommitSha
    Assert-ExactString -Value (Get-ExactProperty $value "result") -Expected "blocked"
    Assert-False -Value (Get-ExactProperty $value "releaseReady")

    $packageSbomAcceptance = Get-ExactProperty $value "packageSbomAcceptance"
    Assert-Condition ($packageSbomAcceptance -is [pscustomobject]) `
        "M15PackageSnapshotInvalid"
    $packageSbomCurrentAtEvaluation = Get-ExactProperty `
        $packageSbomAcceptance `
        "currentAtEvaluation" `
        "M15PackageSnapshotInvalid"
    Assert-Condition ($packageSbomCurrentAtEvaluation -is [bool]) `
        "M15PackageSnapshotInvalid"
    $technicalBaselinePassed = Get-ExactProperty $value "technicalBaselinePassed"
    Assert-Condition `
        ($technicalBaselinePassed -is [bool] -and
         $technicalBaselinePassed -eq $packageSbomCurrentAtEvaluation) `
        "M15PackageSnapshotInvalid"
    Assert-ExactString `
        -Value (Get-ExactProperty `
            $packageSbomAcceptance `
            "result" `
            "M15PackageSnapshotInvalid") `
        -Expected $(if ($packageSbomCurrentAtEvaluation) {
                "accepted-current"
            }
            else {
                "stale-reopen"
            }) `
        -Code "M15PackageSnapshotInvalid"

    $packageVulnerabilityAcceptance = Get-ExactProperty `
        $value `
        "packageVulnerabilityAcceptance" `
        "M15PackageVulnerabilityFreshnessInvalid"
    Assert-Condition ($packageVulnerabilityAcceptance -is [pscustomobject]) `
        "M15PackageVulnerabilityFreshnessInvalid"
    $finalReleaseFreshAtEvaluation = Get-ExactProperty `
        $packageVulnerabilityAcceptance `
        "finalReleaseFreshAtEvaluation" `
        "M15PackageVulnerabilityFreshnessInvalid"
    Assert-Condition ($finalReleaseFreshAtEvaluation -is [bool]) `
        "M15PackageVulnerabilityFreshnessInvalid"
    Assert-ExactString `
        -Value (Get-ExactProperty `
            $packageVulnerabilityAcceptance `
            "effectiveClosedBlocker" `
            "M15PackageVulnerabilityFreshnessInvalid") `
        -Expected $(if ($finalReleaseFreshAtEvaluation) {
                "CveReviewPending"
            }
            else {
                "None"
            }) `
        -Code "M15PackageVulnerabilityFreshnessInvalid"
    Assert-ExactString `
        -Value (Get-ExactProperty `
            $packageSbomAcceptance `
            "effectiveClosedBlocker" `
            "M15PackageSnapshotInvalid") `
        -Expected $(if ($packageSbomCurrentAtEvaluation) {
                "SbomPending"
            }
            else {
                "None"
            }) `
        -Code "M15PackageSnapshotInvalid"

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
    if (-not $packageSbomCurrentAtEvaluation) {
        $requiredBlockers += "SbomPending"
    }
    if (-not $finalReleaseFreshAtEvaluation) {
        $requiredBlockers += "CveReviewPending"
    }
    [System.Array]::Sort($requiredBlockers, [System.StringComparer]::Ordinal)
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

    Assert-ExactInteger `
        -Value (Get-ExactProperty `
            $packageSbomAcceptance `
            "packageProducingSnapshotFileCount" `
            "M15PackageSnapshotInvalid") `
        -Expected $script:packageProducingSnapshotFileCount `
        -Code "M15PackageSnapshotInvalid"
    Assert-ExactString `
        -Value (Get-ExactProperty `
            $packageSbomAcceptance `
            "packageProducingSnapshotSha256" `
            "M15PackageSnapshotInvalid") `
        -Expected $script:packageProducingSnapshotSha256 `
        -Code "M15PackageSnapshotInvalid"
    $currentSnapshotFileCount = Get-ExactProperty `
        $packageSbomAcceptance `
        "currentPackageProducingSnapshotFileCount" `
        "M15PackageSnapshotInvalid"
    Assert-Condition `
        ($currentSnapshotFileCount -is [int] -and
         $currentSnapshotFileCount -gt 0 -and
         $currentSnapshotFileCount -le 256) `
        "M15PackageSnapshotInvalid"
    Assert-LowerSha256 `
        -Value (Get-ExactProperty `
            $packageSbomAcceptance `
            "currentPackageProducingSnapshotSha256" `
            "M15PackageSnapshotInvalid") `
        -Code "M15PackageSnapshotInvalid"
    Assert-LowerSha256 `
        -Value (Get-ExactProperty `
            $packageSbomAcceptance `
            "currentProductionInputSetCanonicalSha256" `
            "M15PackageSnapshotInvalid") `
        -Code "M15PackageSnapshotInvalid"

    $currentPackageProducingSnapshotSha256 = Get-ExactProperty `
        $packageSbomAcceptance `
        "currentPackageProducingSnapshotSha256" `
        "M15PackageSnapshotInvalid"
    $currentProductionInputSetSha256 = Get-ExactProperty `
        $packageSbomAcceptance `
        "currentProductionInputSetCanonicalSha256" `
        "M15PackageSnapshotInvalid"

    return [pscustomobject]@{
        Blockers = [string[]]$blockers
        TechnicalBaselinePassed = [bool]$technicalBaselinePassed
        AutomatedGateSetPassed = [bool]($technicalBaselinePassed -and
            $finalReleaseFreshAtEvaluation)
        PackageSbomCurrentAtEvaluation = [bool]$packageSbomCurrentAtEvaluation
        PackageVulnerabilityFinalReleaseFreshAtEvaluation =
            [bool]$finalReleaseFreshAtEvaluation
        PackageProducingSnapshotFileCount =
            $script:packageProducingSnapshotFileCount
        PackageProducingSnapshotSha256 =
            $script:packageProducingSnapshotSha256
        CurrentPackageProducingSnapshotFileCount =
            [int]$currentSnapshotFileCount
        CurrentPackageProducingSnapshotSha256 =
            [string]$currentPackageProducingSnapshotSha256
        CurrentProductionInputSetCanonicalSha256 =
            [string]$currentProductionInputSetSha256
    }
}

function Read-M16FinalArtifactAcceptance {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Root,

        [Parameter(Mandatory = $true)]
        [pscustomobject]$M15Validation
    )

    $code = "FinalArtifactAcceptanceInvalid"
    try {
        $ledgerPath = Join-Path `
            $Root `
            ($script:finalArtifactAcceptanceRelativePath.Replace('/', '\'))
        $record = Read-StrictJsonRecord `
            -Path $ledgerPath `
            -Root $Root `
            -Name "windows-m16-final-artifact-acceptance.json" `
            -MaximumBytes $script:maximumFinalArtifactAcceptanceBytes
        Assert-Condition `
            ($record.Sha256 -ceq $script:finalArtifactAcceptanceSha256) `
            $code
        $acceptance = $record.Value
        Assert-ExactPropertyNames `
            -Value $acceptance `
            -Expected @(
                "schemaVersion",
                "decision",
                "scope",
                "runCompletedAtUtc",
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
                "producerJobId",
                "producerJobName",
                "producerJobConclusion",
                "artifactId",
                "artifactName",
                "artifactSizeBytes",
                "artifactDigestSha256",
                "memberName",
                "memberLength",
                "memberSha256",
                "sameRunPackageEvidence",
                "evidence",
                "producerContractSourceCount",
                "producerContractSourceSetSha256",
                "packageProducingSnapshotFileCount",
                "packageProducingSnapshotSha256",
                "closedBlocker",
                "remainingM16Blockers",
                "nonClaims") `
            -Code $code

        $expectedRootStrings = [ordered]@{
            decision = "AcceptHostedM16FinalArtifactCanaryScan"
            scope = "M16FinalArtifactCanaryScanOnly"
            runCompletedAtUtc = "2026-08-27T10:13:40Z"
            repository = "serkankaracan/iptv-suite"
            workflowPath = ".github/workflows/windows-quality.yml"
            workflowName = "Windows quality"
            runEvent = "workflow_dispatch"
            runBranch = "main"
            runHeadSha = "be52ab67687cc44a9ca820ec1907c1b92bf1d24a"
            runConclusion = "success"
            producerJobName = "Packaged install and launch smoke"
            producerJobConclusion = "success"
            artifactName = "windows-m16-final-artifact-evidence"
            artifactDigestSha256 = "b40f8742681546c74f1c9d4b6d345ecc699addd2b1bca0830f647b380076f32f"
            memberName = "last-success.json"
            memberSha256 = "fe27278d17391e2946642758c185f4f389e59d81f35e74482452ccdf1867fb11"
            producerContractSourceSetSha256 =
                $script:finalArtifactProducerContractSourceSetSha256
            packageProducingSnapshotSha256 = $script:packageProducingSnapshotSha256
            closedBlocker = "M16FinalArtifactCanaryScanPending"
        }
        foreach ($expected in $expectedRootStrings.GetEnumerator()) {
            Assert-ExactString `
                -Value (Get-ExactProperty $acceptance $expected.Key $code) `
                -Expected ([string]$expected.Value) `
                -Code $code
        }
        Assert-UtcTimestamp `
            -Value (Get-ExactProperty $acceptance "runCompletedAtUtc" $code) `
            -Code $code
        $expectedRootIntegers = [ordered]@{
            schemaVersion = 1
            repositoryId = 1328998460
            workflowId = 330610209
            runId = [long]33060587316
            runNumber = 299
            runAttempt = 1
            producerJobId = [long]98480943428
            artifactId = [long]9642123749
            artifactSizeBytes = 1000
            memberLength = 3281
            producerContractSourceCount =
                $script:finalArtifactProducerContractSourceCount
            packageProducingSnapshotFileCount = $script:packageProducingSnapshotFileCount
        }
        foreach ($expected in $expectedRootIntegers.GetEnumerator()) {
            Assert-ExactInteger `
                -Value (Get-ExactProperty $acceptance $expected.Key $code) `
                -Expected ([long]$expected.Value) `
                -Code $code
        }

        $packageProducingSnapshotCurrent =
            $acceptance.packageProducingSnapshotFileCount -eq
                $M15Validation.PackageProducingSnapshotFileCount -and
            $acceptance.packageProducingSnapshotSha256 -ceq
                $M15Validation.PackageProducingSnapshotSha256

        $packageEvidence = Get-ExactProperty `
            $acceptance `
            "sameRunPackageEvidence" `
            $code
        Assert-Condition ($packageEvidence -is [pscustomobject]) $code
        Assert-ExactPropertyNames `
            -Value $packageEvidence `
            -Expected @(
                "runId",
                "runHeadSha",
                "artifactId",
                "artifactName",
                "artifactSizeBytes",
                "artifactDigestSha256",
                "lastSuccessMemberName",
                "lastSuccessMemberLength",
                "lastSuccessMemberSha256",
                "lastSuccessCommitSha",
                "sbomSummaryMemberName",
                "sbomSummaryMemberLength",
                "sbomSummaryMemberSha256",
                "sbomSummaryCommitSha",
                "sbomMemberName",
                "sbomMemberLength",
                "sbomMemberSha256",
                "configuration",
                "dotNetSdk",
                "productionInputSetSha256",
                "applicationPackageFile",
                "applicationPackageLength",
                "applicationPackageSha256",
                "runtimePackageSha256",
                "officialSbomValidationPassed",
                "strictSbomValidationPassed") `
            -Code $code
        $expectedPackageStrings = [ordered]@{
            runHeadSha = "be52ab67687cc44a9ca820ec1907c1b92bf1d24a"
            artifactName = "windows-msix-smoke-evidence"
            artifactDigestSha256 = "0a0e64c7403bf61c458909ef80c0df7d4109639d07dd9469552e3dacd7e5e972"
            lastSuccessMemberName = "last-success.json"
            lastSuccessMemberSha256 = "1fb75723f4f0f545717daacf6f45e5c04e9470df90edf8fb6818fd1a7761c8ba"
            lastSuccessCommitSha = "be52ab67687cc44a9ca820ec1907c1b92bf1d24a"
            sbomSummaryMemberName = "package-sbom-summary.json"
            sbomSummaryMemberSha256 = "2f8a71d6737612b21303f4b39b45f4b8d244872e9ef661677b53de8b3b930eac"
            sbomSummaryCommitSha = "be52ab67687cc44a9ca820ec1907c1b92bf1d24a"
            sbomMemberName = "package-sbom.spdx.json"
            sbomMemberSha256 = "568d9d10fe6b54942bc394fa75c3e0d6f0fb5ae276f5579b9996c4bd5d974ba2"
            configuration = "Release"
            dotNetSdk = "10.0.302"
            productionInputSetSha256 = "293481fe2194c6f1fde3f667cf45872f4790e0b5955e17ac88c2d16a885b81df"
            applicationPackageFile = "IptvSuite.Windows_0.1.0.0_x64.msix"
            applicationPackageSha256 = "0ceb0e95967c1ede0db1e034d958f0f7a4e7e9da00f65d66010b95f58da86333"
            runtimePackageSha256 = "a3ce5b76713133dfd3b378e81c43a89954c664fcd70fd0c070e409ed3de03ebf"
        }
        foreach ($expected in $expectedPackageStrings.GetEnumerator()) {
            Assert-ExactString `
                -Value (Get-ExactProperty $packageEvidence $expected.Key $code) `
                -Expected ([string]$expected.Value) `
                -Code $code
        }
        $expectedPackageIntegers = [ordered]@{
            runId = [long]33060587316
            artifactId = [long]9642122977
            artifactSizeBytes = 7811
            lastSuccessMemberLength = 18716
            sbomSummaryMemberLength = 1985
            sbomMemberLength = 50566
            applicationPackageLength = 29857927
        }
        foreach ($expected in $expectedPackageIntegers.GetEnumerator()) {
            Assert-ExactInteger `
                -Value (Get-ExactProperty $packageEvidence $expected.Key $code) `
                -Expected ([long]$expected.Value) `
                -Code $code
        }
        Assert-True `
            -Value (Get-ExactProperty $packageEvidence "officialSbomValidationPassed" $code) `
            -Code $code
        Assert-True `
            -Value (Get-ExactProperty $packageEvidence "strictSbomValidationPassed" $code) `
            -Code $code

        $evidence = Get-ExactProperty $acceptance "evidence" $code
        Assert-Condition ($evidence -is [pscustomobject]) $code
        Assert-ExactPropertyNames `
            -Value $evidence `
            -Expected @(
                "schemaVersion",
                "milestone",
                "evidenceKind",
                "result",
                "runToken",
                "commitSha",
                "packageSha256",
                "packageSbomApplicationPackageSha256",
                "scannerProfile",
                "surfaceCount",
                "totalFileCount",
                "totalDirectoryCount",
                "totalFileBytes",
                "packageIntermediateSha256",
                "fullLogScannerReportSha256",
                "surfaces",
                "sameBuildBindingPassed",
                "repositoryStable",
                "rawSurfacesUploaded",
                "supportArtifactScope") `
            -Code $code
        $expectedEvidenceStrings = [ordered]@{
            milestone = "M16"
            evidenceKind = "FinalArtifactCanaryScan"
            result = "passed"
            runToken = "0fb0d6f98022462cbc928718318b18ba"
            commitSha = "be52ab67687cc44a9ca820ec1907c1b92bf1d24a"
            packageSha256 = "0ceb0e95967c1ede0db1e034d958f0f7a4e7e9da00f65d66010b95f58da86333"
            packageSbomApplicationPackageSha256 = "0ceb0e95967c1ede0db1e034d958f0f7a4e7e9da00f65d66010b95f58da86333"
            scannerProfile = "M16ReleaseCandidate"
            packageIntermediateSha256 = "30c1ead8c3a91583b2c1a8b70b5585da0ebe3e48c575194a0e593067df7f6f68"
            fullLogScannerReportSha256 = "4fefc069d4bb1d84cf3a3b78c5307e65f988f01903e4bd71d5565d5698aabb0d"
            supportArtifactScope = "ReleaseAcceptanceOnly"
        }
        foreach ($expected in $expectedEvidenceStrings.GetEnumerator()) {
            Assert-ExactString `
                -Value (Get-ExactProperty $evidence $expected.Key $code) `
                -Expected ([string]$expected.Value) `
                -Code $code
        }
        Assert-ExactInteger `
            -Value (Get-ExactProperty $evidence "schemaVersion" $code) `
            -Expected 1 `
            -Code $code
        Assert-ExactInteger `
            -Value (Get-ExactProperty $evidence "surfaceCount" $code) `
            -Expected 4 `
            -Code $code
        Assert-ExactInteger `
            -Value (Get-ExactProperty $evidence "totalFileCount" $code) `
            -Expected 86 `
            -Code $code
        Assert-ExactInteger `
            -Value (Get-ExactProperty $evidence "totalDirectoryCount" $code) `
            -Expected 26 `
            -Code $code
        Assert-ExactInteger `
            -Value (Get-ExactProperty $evidence "totalFileBytes" $code) `
            -Expected 154147379 `
            -Code $code
        Assert-True `
            -Value (Get-ExactProperty $evidence "sameBuildBindingPassed" $code) `
            -Code $code
        Assert-True `
            -Value (Get-ExactProperty $evidence "repositoryStable" $code) `
            -Code $code
        Assert-False `
            -Value (Get-ExactProperty $evidence "rawSurfacesUploaded" $code) `
            -Code $code
        Assert-Condition `
            ($packageEvidence.runId -eq $acceptance.runId -and
             $packageEvidence.runHeadSha -ceq $acceptance.runHeadSha -and
             $packageEvidence.lastSuccessCommitSha -ceq $acceptance.runHeadSha -and
             $packageEvidence.sbomSummaryCommitSha -ceq $acceptance.runHeadSha -and
             $evidence.commitSha -ceq $acceptance.runHeadSha -and
             $evidence.packageSha256 -ceq $packageEvidence.applicationPackageSha256 -and
             $evidence.packageSbomApplicationPackageSha256 -ceq
                $packageEvidence.applicationPackageSha256) `
            $code

        $expectedSurfaces = @(
            [pscustomobject]@{
                SurfaceId = "owned-app-data"; FileCount = 12; DirectoryCount = 23
                TotalFileBytes = 39096320
                InventorySha256 = "694cdc01beacc5632207dbb2d6874e6a80aa09177ca2a85938364ac86fd3b48f"
            },
            [pscustomobject]@{
                SurfaceId = "exact-package"; FileCount = 72; DirectoryCount = 3
                TotalFileBytes = 115046727
                InventorySha256 = "b45bda3b684511370bb8593005d84352389c5ec1375291858424922bcb817124"
            },
            [pscustomobject]@{
                SurfaceId = "support-artifact"; FileCount = 1; DirectoryCount = 0
                TotalFileBytes = 1139
                InventorySha256 = "ec499ffbeeace13e38ca5a8ffa0136c6187cec72e99bb457fe55be2a8920f1cb"
            },
            [pscustomobject]@{
                SurfaceId = "full-log"; FileCount = 1; DirectoryCount = 0
                TotalFileBytes = 3193
                InventorySha256 = "821e6f24d182b27ad2b93dd06c50288c32c10d8167bddddf8c535f11058c5023"
            })
        $surfaces = @(Get-ExactProperty $evidence "surfaces" $code)
        Assert-Condition ($surfaces.Count -eq $expectedSurfaces.Count) $code
        [long]$totalFiles = 0
        [long]$totalDirectories = 0
        [long]$totalBytes = 0
        for ($index = 0; $index -lt $expectedSurfaces.Count; $index++) {
            $surface = $surfaces[$index]
            $expected = $expectedSurfaces[$index]
            Assert-Condition ($surface -is [pscustomobject]) $code
            Assert-ExactPropertyNames `
                -Value $surface `
                -Expected @(
                    "surfaceId", "schemaVersion", "profile", "result",
                    "fileCount", "directoryCount", "totalFileBytes",
                    "inventorySha256", "findingCount") `
                -Code $code
            Assert-ExactString `
                -Value (Get-ExactProperty $surface "surfaceId" $code) `
                -Expected $expected.SurfaceId `
                -Code $code
            Assert-ExactInteger `
                -Value (Get-ExactProperty $surface "schemaVersion" $code) `
                -Expected 1 `
                -Code $code
            Assert-ExactString `
                -Value (Get-ExactProperty $surface "profile" $code) `
                -Expected "M16ReleaseCandidate" `
                -Code $code
            Assert-ExactString `
                -Value (Get-ExactProperty $surface "result" $code) `
                -Expected "clean" `
                -Code $code
            Assert-ExactInteger `
                -Value (Get-ExactProperty $surface "fileCount" $code) `
                -Expected $expected.FileCount `
                -Code $code
            Assert-ExactInteger `
                -Value (Get-ExactProperty $surface "directoryCount" $code) `
                -Expected $expected.DirectoryCount `
                -Code $code
            Assert-ExactInteger `
                -Value (Get-ExactProperty $surface "totalFileBytes" $code) `
                -Expected $expected.TotalFileBytes `
                -Code $code
            Assert-ExactString `
                -Value (Get-ExactProperty $surface "inventorySha256" $code) `
                -Expected $expected.InventorySha256 `
                -Code $code
            Assert-ExactInteger `
                -Value (Get-ExactProperty $surface "findingCount" $code) `
                -Expected 0 `
                -Code $code
            $totalFiles += [long]$surface.fileCount
            $totalDirectories += [long]$surface.directoryCount
            $totalBytes += [long]$surface.totalFileBytes
        }
        Assert-Condition `
            ($totalFiles -eq [long]$evidence.totalFileCount -and
             $totalDirectories -eq [long]$evidence.totalDirectoryCount -and
             $totalBytes -eq [long]$evidence.totalFileBytes) `
            $code

        Assert-ExactStringArray `
            -Value (Get-ExactProperty $acceptance "remainingM16Blockers" $code) `
            -Expected @(
                "M16FeatureFreezeDecisionPending",
                "M16FinalSecurityArchitectureScanPending",
                "M16PhysicalDeviceAccessibilityMatrixPending",
                "M16ReleaseOperationsPlanPending",
                "M16SyntheticEndToEndJourneyPending",
                "M16TwentyFourHourSoakPending") `
            -Code $code
        $nonClaims = Get-ExactProperty $acceptance "nonClaims" $code
        Assert-Condition ($nonClaims -is [pscustomobject]) $code
        Assert-ExactPropertyNames `
            -Value $nonClaims `
            -Expected @(
                "candidateReady",
                "finalSecurityArchitectureScanComplete",
                "twentyFourHourSoakComplete",
                "physicalDeviceAccessibilityMatrixComplete",
                "storeWackLegalOrSigningApproved") `
            -Code $code
        foreach ($propertyName in @($nonClaims.PSObject.Properties.Name)) {
            Assert-False `
                -Value (Get-ExactProperty $nonClaims $propertyName $code) `
                -Code $code
        }

        $contractBinding = Get-FinalArtifactProducerContractBinding -Root $Root
        return [pscustomobject]@{
            Record = $record
            Acceptance = $acceptance
            ContractBinding = $contractBinding
            IsCurrent =
                $M15Validation.PackageSbomCurrentAtEvaluation -and
                $contractBinding.SourceCount -eq
                    $acceptance.producerContractSourceCount -and
                $contractBinding.SourceSetSha256 -ceq
                    $acceptance.producerContractSourceSetSha256 -and
                $packageProducingSnapshotCurrent -and
                $acceptance.packageProducingSnapshotFileCount -eq
                    $M15Validation.CurrentPackageProducingSnapshotFileCount -and
                $acceptance.packageProducingSnapshotSha256 -ceq
                    $M15Validation.CurrentPackageProducingSnapshotSha256
        }
    }
    catch {
        if ($_.Exception.Message -ceq "M16TechnicalInvariant:$code") {
            throw $_.Exception.Message
        }
        Fail-TechnicalInvariant -Code $code
    }
}

function Read-M16SyntheticJourneyAcceptance {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Root
    )

    $code = "SyntheticJourneyAcceptanceInvalid"
    try {
        $ledgerPath = Join-Path `
            $Root `
            ($script:syntheticJourneyAcceptanceRelativePath.Replace('/', '\'))
        $record = Read-StrictJsonRecord `
            -Path $ledgerPath `
            -Root $Root `
            -Name "windows-m16-synthetic-journey-acceptance.json" `
            -MaximumBytes $script:maximumFinalArtifactAcceptanceBytes
        Assert-Condition `
            ($record.Sha256 -ceq $script:syntheticJourneyAcceptanceSha256) `
            $code
        $acceptance = $record.Value
        Assert-ExactPropertyNames `
            -Value $acceptance `
            -Expected @(
                "schemaVersion",
                "decision",
                "scope",
                "runCompletedAtUtc",
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
                "producerJobId",
                "producerJobName",
                "producerJobConclusion",
                "producerJobCompletedAtUtc",
                "requiredGateJobId",
                "requiredGateJobName",
                "requiredGateJobConclusion",
                "artifactId",
                "artifactName",
                "artifactSizeBytes",
                "artifactDigestSha256",
                "artifactMembers",
                "qualityEvidence",
                "producerContractSourceCount",
                "producerContractSourceSetSha256",
                "closedBlocker",
                "remainingM16Blockers",
                "nonClaims") `
            -Code $code

        $expectedRootStrings = [ordered]@{
            decision = "AcceptHostedM16SyntheticEndToEndJourney"
            scope = "M16SyntheticEndToEndJourneyOnly"
            runCompletedAtUtc = "2026-08-27T12:16:34Z"
            repository = "serkankaracan/iptv-suite"
            workflowPath = ".github/workflows/windows-quality.yml"
            workflowName = "Windows quality"
            runEvent = "push"
            runBranch = "main"
            runHeadSha = "ca63f5959fd0becf59411d6aa979ee350faed90f"
            runConclusion = "success"
            producerJobName = "Locked build and test gate"
            producerJobConclusion = "success"
            producerJobCompletedAtUtc = "2026-08-27T12:08:01Z"
            requiredGateJobName = "Required Windows gate"
            requiredGateJobConclusion = "success"
            artifactName = "windows-quality-evidence"
            artifactDigestSha256 =
                "8da92578e4f226a37255c8d618f240db3439df1b260b18403b83addf356bf658"
            producerContractSourceSetSha256 =
                $script:syntheticJourneyProducerContractSourceSetSha256
            closedBlocker = "M16SyntheticEndToEndJourneyPending"
        }
        foreach ($expected in $expectedRootStrings.GetEnumerator()) {
            Assert-ExactString `
                -Value (Get-ExactProperty $acceptance $expected.Key $code) `
                -Expected ([string]$expected.Value) `
                -Code $code
        }
        Assert-UtcTimestamp `
            -Value (Get-ExactProperty $acceptance "runCompletedAtUtc" $code) `
            -Code $code
        Assert-UtcTimestamp `
            -Value (Get-ExactProperty $acceptance "producerJobCompletedAtUtc" $code) `
            -Code $code

        $expectedRootIntegers = [ordered]@{
            schemaVersion = 1
            repositoryId = 1328998460
            workflowId = 330610209
            runId = [long]33069492771
            runNumber = 302
            runAttempt = 1
            producerJobId = [long]98507784764
            requiredGateJobId = [long]98513232942
            artifactId = [long]9645528070
            artifactSizeBytes = 14131
            producerContractSourceCount =
                $script:syntheticJourneyProducerContractSourceCount
        }
        foreach ($expected in $expectedRootIntegers.GetEnumerator()) {
            Assert-ExactInteger `
                -Value (Get-ExactProperty $acceptance $expected.Key $code) `
                -Expected ([long]$expected.Value) `
                -Code $code
        }

        $expectedMembers = @(
            [pscustomobject]@{
                Name = "evidence/quality-summary.json"
                Length = 47236
                Sha256 = "27013951798ba1b77b646d0a77ab39b1bd4c045a67cf0ecc039e0ec37db9b520"
            },
            [pscustomobject]@{
                Name = "fixtures/LICENSES/LicenseRef-IPTVSuite-Synthetic-Test-Only.txt"
                Length = 531
                Sha256 = "0ee38448ce47fb7c98e56984a84819138e0f7eec085b03d75607d3f5f1d0dba3"
            },
            [pscustomobject]@{
                Name = "fixtures/run-1/fixture-manifest.json"
                Length = 927
                Sha256 = "b1f1513e786f3176c7275af927c4c93c847d0476858fe56701d2054128818438"
            })
        $members = @(Get-ExactProperty $acceptance "artifactMembers" $code)
        Assert-Condition ($members.Count -eq $expectedMembers.Count) $code
        for ($index = 0; $index -lt $expectedMembers.Count; $index++) {
            $member = $members[$index]
            $expectedMember = $expectedMembers[$index]
            Assert-Condition ($member -is [pscustomobject]) $code
            Assert-ExactPropertyNames `
                -Value $member `
                -Expected @("name", "length", "sha256") `
                -Code $code
            Assert-ExactString `
                -Value (Get-ExactProperty $member "name" $code) `
                -Expected $expectedMember.Name `
                -Code $code
            Assert-ExactInteger `
                -Value (Get-ExactProperty $member "length" $code) `
                -Expected $expectedMember.Length `
                -Code $code
            Assert-ExactString `
                -Value (Get-ExactProperty $member "sha256" $code) `
                -Expected $expectedMember.Sha256 `
                -Code $code
        }

        $quality = Get-ExactProperty $acceptance "qualityEvidence" $code
        Assert-Condition ($quality -is [pscustomobject]) $code
        Assert-ExactPropertyNames `
            -Value $quality `
            -Expected @(
                "schemaVersion",
                "milestone",
                "commitSha",
                "sdkVersion",
                "configuration",
                "platform",
                "cleanRunCount",
                "testCountPerRun",
                "journeyTestResult",
                "journeyTestResultOccurrenceCount",
                "qualityGateSentinel",
                "scannerCliSelfTest",
                "artifactCanaryScan",
                "fixture") `
            -Code $code
        $expectedQualityStrings = [ordered]@{
            milestone = "M4-foundation"
            commitSha = "ca63f5959fd0becf59411d6aa979ee350faed90f"
            sdkVersion = "10.0.302"
            configuration = "Debug+Release"
            platform = "x64"
            journeyTestResult =
                "AuthorizedRemotePlaylistCompletesExactSyntheticReleaseCandidateJourney|Passed"
            qualityGateSentinel = "armed-failed-and-disarmed-passed"
            scannerCliSelfTest = "contaminated-exit-2-and-clean-exit-0"
            artifactCanaryScan = "artifact-files-only-passed"
        }
        foreach ($expected in $expectedQualityStrings.GetEnumerator()) {
            Assert-ExactString `
                -Value (Get-ExactProperty $quality $expected.Key $code) `
                -Expected ([string]$expected.Value) `
                -Code $code
        }
        foreach ($expected in ([ordered]@{
                    schemaVersion = 1
                    cleanRunCount = 2
                    testCountPerRun = 631
                    journeyTestResultOccurrenceCount = 1
                }).GetEnumerator()) {
            Assert-ExactInteger `
                -Value (Get-ExactProperty $quality $expected.Key $code) `
                -Expected ([long]$expected.Value) `
                -Code $code
        }
        Assert-Condition `
            ($quality.commitSha -ceq $acceptance.runHeadSha) `
            $code

        $fixture = Get-ExactProperty $quality "fixture" $code
        Assert-Condition ($fixture -is [pscustomobject]) $code
        Assert-ExactPropertyNames `
            -Value $fixture `
            -Expected @(
                "generatorName",
                "generatorVersion",
                "algorithmVersion",
                "seed",
                "provenance",
                "recordsSha256",
                "manifestSha256") `
            -Code $code
        foreach ($expected in ([ordered]@{
                    generatorName = "IptvSuite.Testing.SyntheticFixtureGenerator"
                    generatorVersion = "1.0.0"
                    provenance = "synthetic"
                    recordsSha256 =
                        "1da91c57da1f704076600aab29cdd938851d75f765679ac2b79dc9cb9e908020"
                    manifestSha256 =
                        "b1f1513e786f3176c7275af927c4c93c847d0476858fe56701d2054128818438"
                }).GetEnumerator()) {
            Assert-ExactString `
                -Value (Get-ExactProperty $fixture $expected.Key $code) `
                -Expected ([string]$expected.Value) `
                -Code $code
        }
        Assert-ExactInteger `
            -Value (Get-ExactProperty $fixture "algorithmVersion" $code) `
            -Expected 1 `
            -Code $code
        Assert-ExactInteger `
            -Value (Get-ExactProperty $fixture "seed" $code) `
            -Expected 20260809 `
            -Code $code
        Assert-Condition `
            ($fixture.manifestSha256 -ceq $members[2].sha256) `
            $code

        Assert-ExactStringArray `
            -Value (Get-ExactProperty $acceptance "remainingM16Blockers" $code) `
            -Expected @(
                "M16FeatureFreezeDecisionPending",
                "M16FinalArtifactCanaryScanPending",
                "M16FinalSecurityArchitectureScanPending",
                "M16PhysicalDeviceAccessibilityMatrixPending",
                "M16ReleaseOperationsPlanPending",
                "M16TwentyFourHourSoakPending") `
            -Code $code
        $nonClaims = Get-ExactProperty $acceptance "nonClaims" $code
        Assert-Condition ($nonClaims -is [pscustomobject]) $code
        Assert-ExactPropertyNames `
            -Value $nonClaims `
            -Expected @(
                "candidateReady",
                "realDpapiBoundaryProven",
                "nativeDecoderProven",
                "winUiOrPackagedJourneyProven",
                "twentyFourHourSoakComplete",
                "physicalDeviceAccessibilityMatrixComplete",
                "storeWackLegalOrSigningApproved") `
            -Code $code
        foreach ($propertyName in @($nonClaims.PSObject.Properties.Name)) {
            Assert-False `
                -Value (Get-ExactProperty $nonClaims $propertyName $code) `
                -Code $code
        }

        $contractBinding = Get-SyntheticJourneyProducerContractBinding -Root $Root
        return [pscustomobject]@{
            Record = $record
            Acceptance = $acceptance
            ContractBinding = $contractBinding
            IsCurrent =
                $contractBinding.SourceCount -eq
                    $acceptance.producerContractSourceCount -and
                $contractBinding.SourceSetSha256 -ceq
                    $acceptance.producerContractSourceSetSha256
        }
    }
    catch {
        if ($_.Exception.Message -ceq "M16TechnicalInvariant:$code") {
            throw $_.Exception.Message
        }
        Fail-TechnicalInvariant -Code $code
    }
}

function Read-M16SecurityArchitectureAcceptance {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Root
    )

    $code = "SecurityArchitectureAcceptanceInvalid"
    try {
        $ledgerPath = Join-Path `
            $Root `
            ($script:securityArchitectureAcceptanceRelativePath.Replace('/', '\'))
        $record = Read-StrictJsonRecord `
            -Path $ledgerPath `
            -Root $Root `
            -Name "windows-m16-security-architecture-acceptance.json" `
            -MaximumBytes $script:maximumFinalArtifactAcceptanceBytes
        Assert-Condition `
            ($record.Sha256 -ceq $script:securityArchitectureAcceptanceSha256) `
            $code
        $acceptance = $record.Value
        Assert-ExactPropertyNames `
            -Value $acceptance `
            -Expected @(
                "schemaVersion",
                "decision",
                "scope",
                "runCompletedAtUtc",
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
                "producerJobId",
                "producerJobName",
                "producerJobConclusion",
                "producerJobCompletedAtUtc",
                "requiredGateJobId",
                "requiredGateJobName",
                "requiredGateJobConclusion",
                "artifactId",
                "artifactName",
                "artifactSizeBytes",
                "artifactDigestSha256",
                "artifactMembers",
                "qualityEvidence",
                "producerContractSourceCount",
                "producerContractCanonicalByteLength",
                "producerContractSourceSetSha256",
                "closedBlocker",
                "remainingM16Blockers",
                "nonClaims") `
            -Code $code

        $expectedRootStrings = [ordered]@{
            decision = "AcceptHostedM16FinalSecurityArchitectureScan"
            scope = "M16FinalSecurityArchitectureScanOnly"
            runCompletedAtUtc = "2026-08-27T13:01:49Z"
            repository = "serkankaracan/iptv-suite"
            workflowPath = ".github/workflows/windows-quality.yml"
            workflowName = "Windows quality"
            runEvent = "push"
            runBranch = "main"
            runHeadSha = "cdcb4f64029df9b6490f5b7065f612914c9de6a9"
            runConclusion = "success"
            producerJobName = "Locked build and test gate"
            producerJobConclusion = "success"
            producerJobCompletedAtUtc = "2026-08-27T12:53:10Z"
            requiredGateJobName = "Required Windows gate"
            requiredGateJobConclusion = "success"
            artifactName = "windows-quality-evidence"
            artifactDigestSha256 =
                "0562ea042c76154a3749d8c9c284d269f7b4cb951edc4da398e91b7a21f7e2e5"
            producerContractSourceSetSha256 =
                $script:securityArchitectureProducerContractSourceSetSha256
            closedBlocker = "M16FinalSecurityArchitectureScanPending"
        }
        foreach ($expected in $expectedRootStrings.GetEnumerator()) {
            Assert-ExactString `
                -Value (Get-ExactProperty $acceptance $expected.Key $code) `
                -Expected ([string]$expected.Value) `
                -Code $code
        }
        Assert-UtcTimestamp `
            -Value (Get-ExactProperty $acceptance "runCompletedAtUtc" $code) `
            -Code $code
        Assert-UtcTimestamp `
            -Value (Get-ExactProperty $acceptance "producerJobCompletedAtUtc" $code) `
            -Code $code

        $expectedRootIntegers = [ordered]@{
            schemaVersion = 1
            repositoryId = 1328998460
            workflowId = 330610209
            runId = [long]33072949178
            runNumber = 303
            runAttempt = 1
            producerJobId = [long]98519637813
            requiredGateJobId = [long]98525833186
            artifactId = [long]9647026031
            artifactSizeBytes = 14136
            producerContractSourceCount =
                $script:securityArchitectureProducerContractSourceCount
            producerContractCanonicalByteLength =
                $script:securityArchitectureProducerContractCanonicalByteLength
        }
        foreach ($expected in $expectedRootIntegers.GetEnumerator()) {
            Assert-ExactInteger `
                -Value (Get-ExactProperty $acceptance $expected.Key $code) `
                -Expected ([long]$expected.Value) `
                -Code $code
        }

        $expectedMembers = @(
            [pscustomobject]@{
                Name = "evidence/quality-summary.json"
                Length = 47236
                Sha256 = "3631c20fbd7ae11fd2e4586babf1cc7c928ffe874e446920ed2a2e2eb6277549"
            },
            [pscustomobject]@{
                Name = "fixtures/LICENSES/LicenseRef-IPTVSuite-Synthetic-Test-Only.txt"
                Length = 531
                Sha256 = "0ee38448ce47fb7c98e56984a84819138e0f7eec085b03d75607d3f5f1d0dba3"
            },
            [pscustomobject]@{
                Name = "fixtures/run-1/fixture-manifest.json"
                Length = 927
                Sha256 = "b1f1513e786f3176c7275af927c4c93c847d0476858fe56701d2054128818438"
            })
        $members = @(Get-ExactProperty $acceptance "artifactMembers" $code)
        Assert-Condition ($members.Count -eq $expectedMembers.Count) $code
        for ($index = 0; $index -lt $expectedMembers.Count; $index++) {
            $member = $members[$index]
            $expectedMember = $expectedMembers[$index]
            Assert-Condition ($member -is [pscustomobject]) $code
            Assert-ExactPropertyNames `
                -Value $member `
                -Expected @("name", "length", "sha256") `
                -Code $code
            Assert-ExactString `
                -Value (Get-ExactProperty $member "name" $code) `
                -Expected $expectedMember.Name `
                -Code $code
            Assert-ExactInteger `
                -Value (Get-ExactProperty $member "length" $code) `
                -Expected $expectedMember.Length `
                -Code $code
            Assert-ExactString `
                -Value (Get-ExactProperty $member "sha256" $code) `
                -Expected $expectedMember.Sha256 `
                -Code $code
        }

        $quality = Get-ExactProperty $acceptance "qualityEvidence" $code
        Assert-Condition ($quality -is [pscustomobject]) $code
        Assert-ExactPropertyNames `
            -Value $quality `
            -Expected @(
                "schemaVersion",
                "milestone",
                "commitSha",
                "sdkVersion",
                "configuration",
                "platform",
                "cleanRunCount",
                "testCountPerRun",
                "fullTestResultSetSha256",
                "architectureTestCount",
                "architectureTestResultSetSha256",
                "architectureTestResultsPresentExactlyOnce",
                "architectureTestResultsAllPassed",
                "qualityGateSentinel",
                "scannerCliSelfTest",
                "artifactCanaryScan",
                "fixture") `
            -Code $code
        $expectedQualityStrings = [ordered]@{
            milestone = "M4-foundation"
            commitSha = "cdcb4f64029df9b6490f5b7065f612914c9de6a9"
            sdkVersion = "10.0.302"
            configuration = "Debug+Release"
            platform = "x64"
            fullTestResultSetSha256 =
                "66dab64fa75e52da441dd863490f8d0c5c32f54c5963a12b860ff8af19663ff2"
            architectureTestResultSetSha256 =
                "9d2e961e127593313f48365a9c7f700a6bf1e745c832c8947a94c90a0c4da778"
            qualityGateSentinel = "armed-failed-and-disarmed-passed"
            scannerCliSelfTest = "contaminated-exit-2-and-clean-exit-0"
            artifactCanaryScan = "artifact-files-only-passed"
        }
        foreach ($expected in $expectedQualityStrings.GetEnumerator()) {
            Assert-ExactString `
                -Value (Get-ExactProperty $quality $expected.Key $code) `
                -Expected ([string]$expected.Value) `
                -Code $code
        }
        foreach ($expected in ([ordered]@{
                    schemaVersion = 1
                    cleanRunCount = 2
                    testCountPerRun = 631
                    architectureTestCount = 77
                }).GetEnumerator()) {
            Assert-ExactInteger `
                -Value (Get-ExactProperty $quality $expected.Key $code) `
                -Expected ([long]$expected.Value) `
                -Code $code
        }
        Assert-True `
            -Value (Get-ExactProperty `
                $quality `
                "architectureTestResultsPresentExactlyOnce" `
                $code) `
            -Code $code
        Assert-True `
            -Value (Get-ExactProperty `
                $quality `
                "architectureTestResultsAllPassed" `
                $code) `
            -Code $code
        Assert-Condition ($quality.commitSha -ceq $acceptance.runHeadSha) $code

        $fixture = Get-ExactProperty $quality "fixture" $code
        Assert-Condition ($fixture -is [pscustomobject]) $code
        Assert-ExactPropertyNames `
            -Value $fixture `
            -Expected @(
                "generatorName",
                "generatorVersion",
                "algorithmVersion",
                "seed",
                "provenance",
                "recordsSha256",
                "manifestSha256") `
            -Code $code
        foreach ($expected in ([ordered]@{
                    generatorName = "IptvSuite.Testing.SyntheticFixtureGenerator"
                    generatorVersion = "1.0.0"
                    provenance = "synthetic"
                    recordsSha256 =
                        "1da91c57da1f704076600aab29cdd938851d75f765679ac2b79dc9cb9e908020"
                    manifestSha256 =
                        "b1f1513e786f3176c7275af927c4c93c847d0476858fe56701d2054128818438"
                }).GetEnumerator()) {
            Assert-ExactString `
                -Value (Get-ExactProperty $fixture $expected.Key $code) `
                -Expected ([string]$expected.Value) `
                -Code $code
        }
        Assert-ExactInteger `
            -Value (Get-ExactProperty $fixture "algorithmVersion" $code) `
            -Expected 1 `
            -Code $code
        Assert-ExactInteger `
            -Value (Get-ExactProperty $fixture "seed" $code) `
            -Expected 20260809 `
            -Code $code
        Assert-Condition ($fixture.manifestSha256 -ceq $members[2].sha256) $code

        Assert-ExactStringArray `
            -Value (Get-ExactProperty $acceptance "remainingM16Blockers" $code) `
            -Expected @(
                "M16FeatureFreezeDecisionPending",
                "M16FinalArtifactCanaryScanPending",
                "M16PhysicalDeviceAccessibilityMatrixPending",
                "M16ReleaseOperationsPlanPending",
                "M16SyntheticEndToEndJourneyPending",
                "M16TwentyFourHourSoakPending") `
            -Code $code
        $nonClaims = Get-ExactProperty $acceptance "nonClaims" $code
        Assert-Condition ($nonClaims -is [pscustomobject]) $code
        Assert-ExactPropertyNames `
            -Value $nonClaims `
            -Expected @(
                "candidateReady",
                "independentTrxArtifactProvenance",
                "penetrationOrSastComplete",
                "cveLicenseLegalPrivacyApproved",
                "storeIdentitySigningWackApproved",
                "physicalDeviceAccessibilityMatrixComplete",
                "finalArtifactCanaryScanCurrent",
                "twentyFourHourSoakComplete") `
            -Code $code
        foreach ($propertyName in @($nonClaims.PSObject.Properties.Name)) {
            Assert-False `
                -Value (Get-ExactProperty $nonClaims $propertyName $code) `
                -Code $code
        }

        $contractBinding = Get-SecurityArchitectureProducerContractBinding -Root $Root
        return [pscustomobject]@{
            Record = $record
            Acceptance = $acceptance
            ContractBinding = $contractBinding
            IsCurrent =
                $contractBinding.SourceCount -eq
                    $acceptance.producerContractSourceCount -and
                $contractBinding.CanonicalByteLength -eq
                    $acceptance.producerContractCanonicalByteLength -and
                $contractBinding.SourceSetSha256 -ceq
                    $acceptance.producerContractSourceSetSha256
        }
    }
    catch {
        if ($_.Exception.Message -ceq "M16TechnicalInvariant:$code") {
            throw $_.Exception.Message
        }
        Fail-TechnicalInvariant -Code $code
    }
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
    $m15Validation = Test-M15Input $m15Record $repositoryCommit
    $m15Blockers = @($m15Validation.Blockers)
    $inputRecords["m15-readiness"] = $m15Record

    $script:technicalStage = "FinalArtifactAcceptance"
    $finalArtifactAcceptanceValidation = Read-M16FinalArtifactAcceptance `
        -Root $resolvedRepositoryRoot `
        -M15Validation $m15Validation
    $finalArtifactAcceptance =
        $finalArtifactAcceptanceValidation.Acceptance

    $script:technicalStage = "SyntheticJourneyAcceptance"
    $syntheticJourneyAcceptanceValidation = Read-M16SyntheticJourneyAcceptance `
        -Root $resolvedRepositoryRoot
    $syntheticJourneyAcceptance =
        $syntheticJourneyAcceptanceValidation.Acceptance
    $script:technicalStage = "SecurityArchitectureAcceptance"
    $securityArchitectureAcceptanceValidation =
        Read-M16SecurityArchitectureAcceptance -Root $resolvedRepositoryRoot
    $securityArchitectureAcceptance =
        $securityArchitectureAcceptanceValidation.Acceptance
    $finalArtifactAcceptanceCurrent =
        [bool]$finalArtifactAcceptanceValidation.IsCurrent
    $syntheticJourneyAcceptanceCurrent =
        [bool]$syntheticJourneyAcceptanceValidation.IsCurrent
    $securityArchitectureAcceptanceCurrent =
        [bool]$securityArchitectureAcceptanceValidation.IsCurrent

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
        [ordered]@{ code = "M16TwentyFourHourSoakPending"; category = "Reliability"; origin = "M16"; closureMode = "OperatorEvidenceRequired" },
        [ordered]@{ code = "M16PhysicalDeviceAccessibilityMatrixPending"; category = "Accessibility"; origin = "M16"; closureMode = "OperatorEvidenceRequired" },
        [ordered]@{ code = "M16ReleaseOperationsPlanPending"; category = "Operations"; origin = "M16"; closureMode = "RecordedDecisionRequired" }
    )
    if (-not $finalArtifactAcceptanceCurrent) {
        $m16BlockerDefinitions += [ordered]@{
            code = "M16FinalArtifactCanaryScanPending"
            category = "Security"
            origin = "M16"
            closureMode = "AutomatedEvidenceRequired"
        }
    }
    if (-not $securityArchitectureAcceptanceCurrent) {
        $m16BlockerDefinitions += [ordered]@{
            code = "M16FinalSecurityArchitectureScanPending"
            category = "Security"
            origin = "M16"
            closureMode = "AutomatedEvidenceRequired"
        }
    }
    if (-not $syntheticJourneyAcceptanceCurrent) {
        $m16BlockerDefinitions += [ordered]@{
            code = "M16SyntheticEndToEndJourneyPending"
            category = "Technical"
            origin = "M16"
            closureMode = "AutomatedEvidenceRequired"
        }
    }
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
        m1ToM15AutomatedGateSetPassed =
            [bool]$m15Validation.AutomatedGateSetPassed
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
        finalArtifactCanaryAcceptance = [ordered]@{
            result = if ($finalArtifactAcceptanceCurrent) {
                "accepted-current"
            }
            else {
                "stale-reopen"
            }
            current = $finalArtifactAcceptanceCurrent
            ledgerSha256 = $finalArtifactAcceptanceValidation.Record.Sha256
            decision = $finalArtifactAcceptance.decision
            scope = $finalArtifactAcceptance.scope
            runCompletedAtUtc = $finalArtifactAcceptance.runCompletedAtUtc
            runId = $finalArtifactAcceptance.runId
            runNumber = $finalArtifactAcceptance.runNumber
            runAttempt = $finalArtifactAcceptance.runAttempt
            runHeadSha = $finalArtifactAcceptance.runHeadSha
            producerJobId = $finalArtifactAcceptance.producerJobId
            artifactId = $finalArtifactAcceptance.artifactId
            artifactName = $finalArtifactAcceptance.artifactName
            artifactDigestSha256 = $finalArtifactAcceptance.artifactDigestSha256
            memberLength = $finalArtifactAcceptance.memberLength
            memberSha256 = $finalArtifactAcceptance.memberSha256
            packageSha256 =
                $finalArtifactAcceptance.evidence.packageSha256
            producerContractSourceCount =
                $finalArtifactAcceptance.producerContractSourceCount
            producerContractSourceSetSha256 =
                $finalArtifactAcceptance.producerContractSourceSetSha256
            packageProducingSnapshotFileCount =
                $finalArtifactAcceptance.packageProducingSnapshotFileCount
            packageProducingSnapshotSha256 =
                $finalArtifactAcceptance.packageProducingSnapshotSha256
            closedBlocker = $finalArtifactAcceptance.closedBlocker
            effectiveClosedBlocker = if ($finalArtifactAcceptanceCurrent) {
                $finalArtifactAcceptance.closedBlocker
            }
            else {
                "None"
            }
        }
        finalSecurityArchitectureAcceptance = [ordered]@{
            result = if ($securityArchitectureAcceptanceCurrent) {
                "accepted-current"
            }
            else {
                "stale-reopen"
            }
            current = $securityArchitectureAcceptanceCurrent
            ledgerSha256 = $securityArchitectureAcceptanceValidation.Record.Sha256
            decision = $securityArchitectureAcceptance.decision
            scope = $securityArchitectureAcceptance.scope
            runCompletedAtUtc = $securityArchitectureAcceptance.runCompletedAtUtc
            runId = $securityArchitectureAcceptance.runId
            runNumber = $securityArchitectureAcceptance.runNumber
            runAttempt = $securityArchitectureAcceptance.runAttempt
            runHeadSha = $securityArchitectureAcceptance.runHeadSha
            producerJobId = $securityArchitectureAcceptance.producerJobId
            requiredGateJobId = $securityArchitectureAcceptance.requiredGateJobId
            artifactId = $securityArchitectureAcceptance.artifactId
            artifactName = $securityArchitectureAcceptance.artifactName
            artifactDigestSha256 =
                $securityArchitectureAcceptance.artifactDigestSha256
            qualitySummaryMemberLength =
                $securityArchitectureAcceptance.artifactMembers[0].length
            qualitySummaryMemberSha256 =
                $securityArchitectureAcceptance.artifactMembers[0].sha256
            cleanRunCount =
                $securityArchitectureAcceptance.qualityEvidence.cleanRunCount
            testCountPerRun =
                $securityArchitectureAcceptance.qualityEvidence.testCountPerRun
            fullTestResultSetSha256 =
                $securityArchitectureAcceptance.qualityEvidence.fullTestResultSetSha256
            architectureTestCount =
                $securityArchitectureAcceptance.qualityEvidence.architectureTestCount
            architectureTestResultSetSha256 =
                $securityArchitectureAcceptance.qualityEvidence.architectureTestResultSetSha256
            producerContractSourceCount =
                $securityArchitectureAcceptance.producerContractSourceCount
            producerContractCanonicalByteLength =
                $securityArchitectureAcceptance.producerContractCanonicalByteLength
            producerContractSourceSetSha256 =
                $securityArchitectureAcceptance.producerContractSourceSetSha256
            closedBlocker = $securityArchitectureAcceptance.closedBlocker
            effectiveClosedBlocker = if ($securityArchitectureAcceptanceCurrent) {
                $securityArchitectureAcceptance.closedBlocker
            }
            else {
                "None"
            }
        }
        syntheticEndToEndJourneyAcceptance = [ordered]@{
            result = if ($syntheticJourneyAcceptanceCurrent) {
                "accepted-current"
            }
            else {
                "stale-reopen"
            }
            current = $syntheticJourneyAcceptanceCurrent
            ledgerSha256 = $syntheticJourneyAcceptanceValidation.Record.Sha256
            decision = $syntheticJourneyAcceptance.decision
            scope = $syntheticJourneyAcceptance.scope
            runCompletedAtUtc = $syntheticJourneyAcceptance.runCompletedAtUtc
            runId = $syntheticJourneyAcceptance.runId
            runNumber = $syntheticJourneyAcceptance.runNumber
            runAttempt = $syntheticJourneyAcceptance.runAttempt
            runHeadSha = $syntheticJourneyAcceptance.runHeadSha
            producerJobId = $syntheticJourneyAcceptance.producerJobId
            requiredGateJobId = $syntheticJourneyAcceptance.requiredGateJobId
            artifactId = $syntheticJourneyAcceptance.artifactId
            artifactName = $syntheticJourneyAcceptance.artifactName
            artifactDigestSha256 = $syntheticJourneyAcceptance.artifactDigestSha256
            qualitySummaryMemberLength =
                $syntheticJourneyAcceptance.artifactMembers[0].length
            qualitySummaryMemberSha256 =
                $syntheticJourneyAcceptance.artifactMembers[0].sha256
            cleanRunCount =
                $syntheticJourneyAcceptance.qualityEvidence.cleanRunCount
            testCountPerRun =
                $syntheticJourneyAcceptance.qualityEvidence.testCountPerRun
            journeyTestResult =
                $syntheticJourneyAcceptance.qualityEvidence.journeyTestResult
            producerContractSourceCount =
                $syntheticJourneyAcceptance.producerContractSourceCount
            producerContractSourceSetSha256 =
                $syntheticJourneyAcceptance.producerContractSourceSetSha256
            closedBlocker = $syntheticJourneyAcceptance.closedBlocker
            effectiveClosedBlocker = if ($syntheticJourneyAcceptanceCurrent) {
                $syntheticJourneyAcceptance.closedBlocker
            }
            else {
                "None"
            }
        }
        gates = @(
            [ordered]@{
                code = "M1ToM15AutomatedGateSet"
                result = if ($m15Validation.AutomatedGateSetPassed) {
                    "passed"
                }
                else {
                    "blocked"
                }
                evidenceCount = 8
            },
            [ordered]@{
                code = "M16FinalArtifactCanaryScan"
                result = if ($finalArtifactAcceptanceCurrent) {
                    "passed"
                }
                else {
                    "blocked"
                }
                evidenceCount = 1
            },
            [ordered]@{
                code = "M16FinalSecurityArchitectureScan"
                result = if ($securityArchitectureAcceptanceCurrent) {
                    "passed"
                }
                else {
                    "blocked"
                }
                evidenceCount = 1
            },
            [ordered]@{
                code = "M16SyntheticEndToEndJourney"
                result = if ($syntheticJourneyAcceptanceCurrent) {
                    "passed"
                }
                else {
                    "blocked"
                }
                evidenceCount = 1
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
    $stableFinalArtifactAcceptance = Read-M16FinalArtifactAcceptance `
        -Root $resolvedRepositoryRoot `
        -M15Validation $m15Validation
    Assert-Condition `
        ($stableFinalArtifactAcceptance.Record.ByteLength -eq
            $finalArtifactAcceptanceValidation.Record.ByteLength -and
         $stableFinalArtifactAcceptance.Record.Sha256 -ceq
            $finalArtifactAcceptanceValidation.Record.Sha256 -and
         $stableFinalArtifactAcceptance.ContractBinding.SourceSetSha256 -ceq
            $finalArtifactAcceptanceValidation.ContractBinding.SourceSetSha256) `
        "FinalArtifactAcceptanceInvalid"
    $stableSyntheticJourneyAcceptance = Read-M16SyntheticJourneyAcceptance `
        -Root $resolvedRepositoryRoot
    Assert-Condition `
        ($stableSyntheticJourneyAcceptance.Record.ByteLength -eq
            $syntheticJourneyAcceptanceValidation.Record.ByteLength -and
         $stableSyntheticJourneyAcceptance.Record.Sha256 -ceq
            $syntheticJourneyAcceptanceValidation.Record.Sha256 -and
         $stableSyntheticJourneyAcceptance.ContractBinding.SourceSetSha256 -ceq
             $syntheticJourneyAcceptanceValidation.ContractBinding.SourceSetSha256) `
        "SyntheticJourneyAcceptanceInvalid"
    $stableSecurityArchitectureAcceptance =
        Read-M16SecurityArchitectureAcceptance -Root $resolvedRepositoryRoot
    Assert-Condition `
        ($stableSecurityArchitectureAcceptance.Record.ByteLength -eq
            $securityArchitectureAcceptanceValidation.Record.ByteLength -and
         $stableSecurityArchitectureAcceptance.Record.Sha256 -ceq
            $securityArchitectureAcceptanceValidation.Record.Sha256 -and
         $stableSecurityArchitectureAcceptance.ContractBinding.SourceCount -eq
            $securityArchitectureAcceptanceValidation.ContractBinding.SourceCount -and
         $stableSecurityArchitectureAcceptance.ContractBinding.CanonicalByteLength -eq
            $securityArchitectureAcceptanceValidation.ContractBinding.CanonicalByteLength -and
         $stableSecurityArchitectureAcceptance.ContractBinding.SourceSetSha256 -ceq
            $securityArchitectureAcceptanceValidation.ContractBinding.SourceSetSha256) `
        "SecurityArchitectureAcceptanceInvalid"
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
