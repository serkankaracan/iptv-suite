#requires -Version 5.1

[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$PackagePath,

    [Parameter(Mandatory)]
    [string]$RuntimePackagePath,

    [string]$DotNetPath = "dotnet",

    [string]$RepositoryRoot = (Split-Path -Parent $PSScriptRoot)
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

. (Join-Path $PSScriptRoot "WindowsPackageSbom.ps1")

$applicationArtifactSpdxId = "SPDXRef-IptvSuiteWindowsMsixArtifact"
$runtimeArtifactSpdxId = "SPDXRef-WindowsAppRuntimeMsixArtifact"
$expectedApplicationName = "IptvSuite.LocalDev.6f0d9a64"
$expectedApplicationPublisher = "CN=IptvSuite Local Development"
$expectedRuntimeName = "Microsoft.WindowsAppRuntime.2"
$expectedRuntimePublisher = "CN=Microsoft Corporation, O=Microsoft Corporation, L=Redmond, S=Washington, C=US"
$expectedRuntimeVersion = "2.4.0.0"
$expectedArchitecture = "x64"

function Assert-ExactChildPath {
    param(
        [Parameter(Mandatory)]
        [string]$Parent,

        [Parameter(Mandatory)]
        [string]$Child,

        [Parameter(Mandatory)]
        [string]$Code
    )

    $parentFullPath = [System.IO.Path]::GetFullPath($Parent).TrimEnd('\')
    $childFullPath = [System.IO.Path]::GetFullPath($Child)
    $prefix = $parentFullPath + '\'
    Assert-WindowsPackageSbomCondition `
        ($childFullPath.StartsWith($prefix, [System.StringComparison]::OrdinalIgnoreCase)) `
        $Code
    return $childFullPath
}

function Get-StringSha256 {
    param(
        [Parameter(Mandatory)]
        [string]$Value
    )

    $algorithm = [System.Security.Cryptography.SHA256]::Create()
    try {
        $bytes = (New-Object System.Text.UTF8Encoding($false, $true)).GetBytes($Value)
        return ([BitConverter]::ToString($algorithm.ComputeHash($bytes))).Replace('-', '').ToLowerInvariant()
    }
    finally {
        $algorithm.Dispose()
    }
}

function Get-RepositorySnapshot {
    param(
        [Parameter(Mandatory)]
        [string]$Root
    )

    $topLevel = @(& git -C $Root rev-parse --show-toplevel 2>$null)
    if ($LASTEXITCODE -ne 0 -or $topLevel.Count -ne 1) {
        Fail-WindowsPackageSbom -Code 'RepositoryUnavailable'
    }
    Assert-WindowsPackageSbomCondition `
        ([System.IO.Path]::GetFullPath($topLevel[0]).TrimEnd('\') -ceq
            [System.IO.Path]::GetFullPath($Root).TrimEnd('\')) `
        'RepositoryMismatch'

    $commit = @(& git -C $Root rev-parse HEAD 2>$null)
    if ($LASTEXITCODE -ne 0 -or $commit.Count -ne 1 -or
        [string]$commit[0] -cnotmatch '\A[0-9a-f]{40}\z') {
        Fail-WindowsPackageSbom -Code 'CommitInvalid'
    }
    $status = @(& git -C $Root status --porcelain=v1 --untracked-files=all 2>$null)
    if ($LASTEXITCODE -ne 0) {
        Fail-WindowsPackageSbom -Code 'RepositoryStatusFailed'
    }
    Assert-WindowsPackageSbomCondition ($status.Count -eq 0) 'RepositoryDirty'

    $commitDateText = @(& git -C $Root show -s --format=%cI HEAD 2>$null)
    if ($LASTEXITCODE -ne 0 -or $commitDateText.Count -ne 1) {
        Fail-WindowsPackageSbom -Code 'CommitTimestampInvalid'
    }
    try {
        $commitDate = [DateTimeOffset]::Parse(
            [string]$commitDateText[0],
            [Globalization.CultureInfo]::InvariantCulture,
            [Globalization.DateTimeStyles]::RoundtripKind)
    }
    catch {
        Fail-WindowsPackageSbom -Code 'CommitTimestampInvalid'
    }

    return [pscustomobject]@{
        CommitSha = [string]$commit[0]
        GenerationTimestamp = $commitDate.UtcDateTime.ToString(
            'yyyy-MM-ddTHH:mm:ssZ',
            [Globalization.CultureInfo]::InvariantCulture)
    }
}

function Get-ProductionInputBinding {
    param(
        [Parameter(Mandatory)]
        [object]$Configuration,

        [Parameter(Mandatory)]
        [string]$Root
    )

    $records = @()
    foreach ($relativePathValue in @($Configuration.productionInputs)) {
        $relativePath = [string]$relativePathValue
        Assert-WindowsPackageSbomCondition `
            ($relativePath -cmatch '\A[A-Za-z0-9._/-]+\z' -and
             -not [System.IO.Path]::IsPathRooted($relativePath) -and
             $relativePath -notmatch '(?:^|/)\.\.(?:/|$)') `
            'ProductionInputPathInvalid'
        $fullPath = Assert-ExactChildPath `
            -Parent $Root `
            -Child (Join-Path $Root ($relativePath.Replace('/', '\'))) `
            -Code 'ProductionInputPathInvalid'
        $item = Resolve-WindowsPackageSbomRegularFile `
            -Path $fullPath `
            -MaximumBytes ([long]$Configuration.limits.maximumRepositoryInputBytes) `
            -Code 'ProductionInputInvalid'
        $records += [pscustomobject]@{
            Path = $relativePath
            Length = [long]$item.Length
            Sha256 = Get-WindowsPackageSbomSha256 $item.FullName
        }
    }
    Assert-WindowsPackageSbomCondition `
        ($records.Count -eq @($Configuration.productionInputs).Count -and $records.Count -gt 0) `
        'ProductionInputSetInvalid'
    Assert-WindowsPackageSbomNoCaseCollision `
        -Values @($records | ForEach-Object { [string]$_.Path }) `
        -Code 'ProductionInputSetInvalid'

    $bindingText = @($records | ForEach-Object {
        "$($_.Path)`0$($_.Length)`0$($_.Sha256)"
    }) -join "`n"
    return [pscustomobject]@{
        Records = $records
        Sha256 = Get-StringSha256 $bindingText
    }
}

function Invoke-ExactSbomTool {
    param(
        [Parameter(Mandatory)]
        [System.IO.FileInfo]$Tool,

        [Parameter(Mandatory)]
        [string[]]$Arguments,

        [Parameter(Mandatory)]
        [string]$WorkingDirectory,

        [Parameter(Mandatory)]
        [int]$TimeoutSeconds,

        [Parameter(Mandatory)]
        [string]$Code,

        [switch]$CaptureStandardOutput
    )

    foreach ($argument in $Arguments) {
        Assert-WindowsPackageSbomCondition `
            ($null -ne $argument -and $argument.IndexOf([char]0) -lt 0 -and
             $argument.IndexOf('"') -lt 0 -and
             $argument.IndexOf("`r") -lt 0 -and
             $argument.IndexOf("`n") -lt 0 -and
             -not $argument.EndsWith('\', [System.StringComparison]::Ordinal)) `
            'ToolArgumentInvalid'
    }

    $stdoutPath = Join-Path $WorkingDirectory "$Code.stdout.txt"
    $stderrPath = Join-Path $WorkingDirectory "$Code.stderr.txt"
    $quotedArguments = @($Arguments | ForEach-Object { '"' + $_ + '"' })
    $previousRollForward = [Environment]::GetEnvironmentVariable('DOTNET_ROLL_FORWARD', 'Process')
    try {
        [Environment]::SetEnvironmentVariable('DOTNET_ROLL_FORWARD', 'Major', 'Process')
        $process = Start-Process `
            -FilePath $Tool.FullName `
            -ArgumentList $quotedArguments `
            -WorkingDirectory $WorkingDirectory `
            -RedirectStandardOutput $stdoutPath `
            -RedirectStandardError $stderrPath `
            -WindowStyle Hidden `
            -PassThru
        # Windows PowerShell 5.1 must materialize the native handle before the
        # timed wait or ExitCode can remain unavailable after process exit.
        [void]$process.Handle
        if (-not $process.WaitForExit($TimeoutSeconds * 1000)) {
            try { $process.Kill() } catch { }
            Fail-WindowsPackageSbom -Code 'ToolTimedOut'
        }
        $process.WaitForExit()
        $exitCode = $process.ExitCode
    }
    catch {
        if ($_.Exception.Message -like 'WindowsPackageSbom:*') { throw }
        Fail-WindowsPackageSbom -Code $Code
    }
    finally {
        [Environment]::SetEnvironmentVariable('DOTNET_ROLL_FORWARD', $previousRollForward, 'Process')
    }

    foreach ($outputPath in @($stdoutPath, $stderrPath)) {
        if (Test-Path -LiteralPath $outputPath) {
            $output = Get-Item -LiteralPath $outputPath -Force
            Assert-WindowsPackageSbomCondition `
                (-not $output.PSIsContainer -and $output.Length -le 1MB) `
                'ToolOutputInvalid'
        }
    }
    if ($exitCode -ne 0) {
        Fail-WindowsPackageSbom -Code $Code
    }

    $stdout = if ($CaptureStandardOutput -and (Test-Path -LiteralPath $stdoutPath)) {
        [System.IO.File]::ReadAllText($stdoutPath, (New-Object System.Text.UTF8Encoding($false, $true)))
    }
    else { '' }
    return [pscustomobject]@{
        ExitCode = $exitCode
        StandardOutput = $stdout.Trim()
    }
}

function Assert-ExactSbomToolPayload {
    param(
        [Parameter(Mandatory)]
        [System.IO.FileInfo]$Package,

        [Parameter(Mandatory)]
        [string]$ExpectedPackageSha256
    )

    $payloadPrefix = 'tools/net8.0/any/'
    $maximumArchiveEntries = 512
    $maximumFileSystemEntries = 512
    $maximumPayloadFiles = 256
    $maximumPayloadFileBytes = 16MB
    $maximumPayloadBytes = 64MB
    $archive = $null
    $packageStream = $null

    try {
        Assert-WindowsPackageSbomCondition `
            ($ExpectedPackageSha256 -cmatch '\A[0-9a-f]{64}\z') `
            'ToolPayloadMismatch'
        $resolvedPackage = Resolve-WindowsPackageSbomRegularFile `
            -Path $Package.FullName `
            -MaximumBytes 32MB `
            -Code 'ToolPayloadMismatch'
        $payloadRoot = [System.IO.Path]::GetFullPath(
            (Join-Path $resolvedPackage.Directory.FullName 'tools\net8.0\any')).TrimEnd('\')
        Assert-WindowsPackageSbomPathHasNoReparsePoint `
            -Path $payloadRoot `
            -Code 'ToolPayloadMismatch'
        $payloadRootItem = Get-Item -LiteralPath $payloadRoot -Force -ErrorAction Stop
        Assert-WindowsPackageSbomCondition `
            ($payloadRootItem.PSIsContainer -and
             ($payloadRootItem.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -eq 0) `
            'ToolPayloadMismatch'

        # The package pin and archive entries must be read from the same exclusive
        # handle so the nupkg cannot be swapped between verification and use.
        $packageStream = [System.IO.File]::Open(
            $resolvedPackage.FullName,
            [System.IO.FileMode]::Open,
            [System.IO.FileAccess]::Read,
            [System.IO.FileShare]::None)
        $packageHashAlgorithm = [System.Security.Cryptography.SHA256]::Create()
        try {
            $actualPackageSha256 = ([BitConverter]::ToString(
                    $packageHashAlgorithm.ComputeHash($packageStream))).Replace('-', '').ToLowerInvariant()
        }
        finally {
            $packageHashAlgorithm.Dispose()
        }
        Assert-WindowsPackageSbomCondition `
            ($actualPackageSha256 -ceq $ExpectedPackageSha256) `
            'ToolPayloadMismatch'
        $packageStream.Position = 0

        $archive = New-Object System.IO.Compression.ZipArchive(
            $packageStream,
            [System.IO.Compression.ZipArchiveMode]::Read,
            $true)
        Assert-WindowsPackageSbomCondition `
            ($archive.Entries.Count -gt 0 -and $archive.Entries.Count -le $maximumArchiveEntries) `
            'ToolPayloadMismatch'

        $expectedFiles = New-Object `
            'System.Collections.Generic.Dictionary[string,object]' `
            ([System.StringComparer]::Ordinal)
        $expectedCaseInsensitive = New-Object `
            'System.Collections.Generic.HashSet[string]' `
            ([System.StringComparer]::OrdinalIgnoreCase)
        [long]$expectedPayloadBytes = 0
        foreach ($entry in $archive.Entries) {
            $entryName = [string]$entry.FullName
            if ($entryName.StartsWith($payloadPrefix, [System.StringComparison]::OrdinalIgnoreCase) -and
                -not $entryName.StartsWith($payloadPrefix, [System.StringComparison]::Ordinal)) {
                Fail-WindowsPackageSbom -Code 'ToolPayloadMismatch'
            }
            if (-not $entryName.StartsWith($payloadPrefix, [System.StringComparison]::Ordinal)) {
                continue
            }

            $relativePath = $entryName.Substring($payloadPrefix.Length)
            if ($relativePath.Length -eq 0 -or $relativePath.EndsWith('/', [System.StringComparison]::Ordinal)) {
                continue
            }
            $segments = @($relativePath.Split('/'))
            Assert-WindowsPackageSbomCondition `
                ($relativePath -cmatch '\A[A-Za-z0-9._/-]+\z' -and
                 -not $relativePath.Contains('//') -and
                 @($segments | Where-Object { $_ -ceq '.' -or $_ -ceq '..' }).Count -eq 0 -and
                 -not $expectedFiles.ContainsKey($relativePath) -and
                 $expectedCaseInsensitive.Add($relativePath) -and
                 [long]$entry.Length -ge 0 -and
                 [long]$entry.Length -le $maximumPayloadFileBytes) `
                'ToolPayloadMismatch'
            $expectedPayloadBytes += [long]$entry.Length
            Assert-WindowsPackageSbomCondition `
                ($expectedPayloadBytes -le $maximumPayloadBytes) `
                'ToolPayloadMismatch'

            $entryStream = $entry.Open()
            $entryHashAlgorithm = [System.Security.Cryptography.SHA256]::Create()
            try {
                $buffer = New-Object byte[] 81920
                [long]$streamedLength = 0
                while (($read = $entryStream.Read($buffer, 0, $buffer.Length)) -gt 0) {
                    $streamedLength += $read
                    Assert-WindowsPackageSbomCondition `
                        ($streamedLength -le [long]$entry.Length -and
                         $streamedLength -le $maximumPayloadFileBytes) `
                        'ToolPayloadMismatch'
                    [void]$entryHashAlgorithm.TransformBlock($buffer, 0, $read, $buffer, 0)
                }
                [void]$entryHashAlgorithm.TransformFinalBlock((New-Object byte[] 0), 0, 0)
                Assert-WindowsPackageSbomCondition `
                    ($streamedLength -eq [long]$entry.Length) `
                    'ToolPayloadMismatch'
                $entrySha256 = ([BitConverter]::ToString($entryHashAlgorithm.Hash)).Replace(
                    '-', '').ToLowerInvariant()
            }
            finally {
                $entryHashAlgorithm.Dispose()
                $entryStream.Dispose()
            }
            $expectedFiles.Add($relativePath, [pscustomobject]@{
                    Length = [long]$entry.Length
                    Sha256 = $entrySha256
                })
            Assert-WindowsPackageSbomCondition `
                ($expectedFiles.Count -le $maximumPayloadFiles) `
                'ToolPayloadMismatch'
        }
        Assert-WindowsPackageSbomCondition `
            ($expectedFiles.Count -gt 0 -and
             $expectedFiles.ContainsKey('Microsoft.Sbom.DotNetTool.dll') -and
             $expectedFiles.ContainsKey('Microsoft.Sbom.DotNetTool.deps.json') -and
             $expectedFiles.ContainsKey('Microsoft.Sbom.DotNetTool.runtimeconfig.json')) `
            'ToolPayloadMismatch'

        $actualFiles = New-Object `
            'System.Collections.Generic.Dictionary[string,object]' `
            ([System.StringComparer]::Ordinal)
        $actualCaseInsensitive = New-Object `
            'System.Collections.Generic.HashSet[string]' `
            ([System.StringComparer]::OrdinalIgnoreCase)
        $directories = New-Object 'System.Collections.Generic.Stack[string]'
        $directories.Push($payloadRoot)
        $fileSystemEntryCount = 0
        [long]$actualPayloadBytes = 0
        while ($directories.Count -gt 0) {
            $directory = $directories.Pop()
            foreach ($childPath in [System.IO.Directory]::EnumerateFileSystemEntries($directory)) {
                $fileSystemEntryCount++
                Assert-WindowsPackageSbomCondition `
                    ($fileSystemEntryCount -le $maximumFileSystemEntries) `
                    'ToolPayloadMismatch'
                $attributes = [System.IO.File]::GetAttributes($childPath)
                Assert-WindowsPackageSbomCondition `
                    (($attributes -band [System.IO.FileAttributes]::ReparsePoint) -eq 0) `
                    'ToolPayloadMismatch'
                if (($attributes -band [System.IO.FileAttributes]::Directory) -ne 0) {
                    $directories.Push($childPath)
                    continue
                }

                $childFullPath = [System.IO.Path]::GetFullPath($childPath)
                Assert-WindowsPackageSbomCondition `
                    ($childFullPath.StartsWith(
                        $payloadRoot + '\',
                        [System.StringComparison]::OrdinalIgnoreCase)) `
                    'ToolPayloadMismatch'
                $relativePath = $childFullPath.Substring($payloadRoot.Length + 1).Replace('\', '/')
                $segments = @($relativePath.Split('/'))
                Assert-WindowsPackageSbomCondition `
                    ($relativePath -cmatch '\A[A-Za-z0-9._/-]+\z' -and
                     -not $relativePath.Contains('//') -and
                     @($segments | Where-Object { $_ -ceq '.' -or $_ -ceq '..' }).Count -eq 0 -and
                     -not $actualFiles.ContainsKey($relativePath) -and
                     $actualCaseInsensitive.Add($relativePath)) `
                    'ToolPayloadMismatch'
                $actualFile = Resolve-WindowsPackageSbomRegularFile `
                    -Path $childFullPath `
                    -MaximumBytes $maximumPayloadFileBytes `
                    -Code 'ToolPayloadMismatch'
                $actualPayloadBytes += [long]$actualFile.Length
                Assert-WindowsPackageSbomCondition `
                    ($actualPayloadBytes -le $maximumPayloadBytes) `
                    'ToolPayloadMismatch'
                $actualFiles.Add($relativePath, [pscustomobject]@{
                        Length = [long]$actualFile.Length
                        Sha256 = Get-WindowsPackageSbomSha256 $actualFile.FullName
                    })
                Assert-WindowsPackageSbomCondition `
                    ($actualFiles.Count -le $maximumPayloadFiles) `
                    'ToolPayloadMismatch'
            }
        }

        Assert-WindowsPackageSbomCondition `
            ($actualFiles.Count -eq $expectedFiles.Count -and
             $actualPayloadBytes -eq $expectedPayloadBytes) `
            'ToolPayloadMismatch'
        foreach ($relativePath in $expectedFiles.Keys) {
            Assert-WindowsPackageSbomCondition `
                ($actualFiles.ContainsKey($relativePath) -and
                 [long]$actualFiles[$relativePath].Length -eq [long]$expectedFiles[$relativePath].Length -and
                 [string]$actualFiles[$relativePath].Sha256 -ceq
                    [string]$expectedFiles[$relativePath].Sha256) `
                'ToolPayloadMismatch'
        }

        return [pscustomobject]@{
            FileCount = $actualFiles.Count
            TotalBytes = $actualPayloadBytes
        }
    }
    catch {
        if ($_.Exception.Message -ceq 'WindowsPackageSbom:ToolPayloadMismatch') {
            throw
        }
        Fail-WindowsPackageSbom -Code 'ToolPayloadMismatch'
    }
    finally {
        if ($null -ne $archive) {
            $archive.Dispose()
        }
        if ($null -ne $packageStream) {
            $packageStream.Dispose()
        }
    }
}

function Add-SbomProperty {
    param(
        [Parameter(Mandatory)]
        [object]$InputObject,

        [Parameter(Mandatory)]
        [string]$Name,

        [Parameter(Mandatory)]
        [object]$Value
    )

    $InputObject | Add-Member -MemberType NoteProperty -Name $Name -Value $Value -Force
}

$root = [System.IO.Path]::GetFullPath($RepositoryRoot).TrimEnd('\')
$configurationPath = Join-Path $PSScriptRoot 'windows-package-sbom-tool.json'
$configurationFile = Resolve-WindowsPackageSbomRegularFile `
    -Path $configurationPath `
    -MaximumBytes 1MB `
    -Code 'ConfigurationInvalid'
$configuration = (Read-WindowsPackageSbomJson `
    -File $configurationFile `
    -Code 'ConfigurationInvalid').Value
Assert-WindowsPackageSbomConfiguration -Configuration $configuration

Assert-WindowsPackageSbomCondition `
    ($configuration.schemaVersion -eq 1 -and
     [string]$configuration.packageId -ceq 'microsoft.sbom.dotnettool' -and
     [string]$configuration.version -ceq '4.1.5' -and
     [string]$configuration.command -ceq 'sbom-tool' -and
     [string]$configuration.manifestInfo -ceq 'SPDX:2.2' -and
     [string]$configuration.packageSupplier -ceq 'NOASSERTION' -and
     [string]$configuration.componentPath -ceq 'apps/windows/src' -and
     [string]$configuration.nupkgSha256 -cmatch '\A[0-9a-f]{64}\z' -and
     [string]$configuration.shimSha256 -cmatch '\A[0-9a-f]{64}\z') `
    'ConfigurationContractInvalid'

$artifactRoot = Join-Path $root '.artifacts\msix-smoke'
[System.IO.Directory]::CreateDirectory($artifactRoot) | Out-Null
$artifactRootItem = Get-Item -LiteralPath $artifactRoot -Force
Assert-WindowsPackageSbomCondition `
    (($artifactRootItem.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -eq 0) `
    'ArtifactRootInvalid'
$sbomPath = Join-Path $artifactRoot 'package-sbom.spdx.json'
$summaryPath = Join-Path $artifactRoot 'package-sbom-summary.json'
$workParent = Join-Path $artifactRoot 'sbom-work'

foreach ($stalePath in @($sbomPath, $summaryPath)) {
    if (Test-Path -LiteralPath $stalePath) {
        $staleItem = Get-Item -LiteralPath $stalePath -Force
        Assert-WindowsPackageSbomCondition `
            (-not $staleItem.PSIsContainer -and
             ($staleItem.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -eq 0) `
            'StaleEvidenceInvalid'
        Remove-Item -LiteralPath $staleItem.FullName -Force
    }
}

$workRoot = Join-Path $workParent ([Guid]::NewGuid().ToString('N'))
$dropRoot = Join-Path $workRoot 'drop'
$manifestRoot = Join-Path $workRoot 'manifest'
$validationPath = Join-Path $workRoot 'official-validation.json'
[System.IO.Directory]::CreateDirectory($dropRoot) | Out-Null
[System.IO.Directory]::CreateDirectory($manifestRoot) | Out-Null

try {
    $repositoryBefore = Get-RepositorySnapshot -Root $root
    $sourceBindingBefore = Get-ProductionInputBinding -Configuration $configuration -Root $root

    $expectedSdk = [string]((Get-Content -LiteralPath (Join-Path $root 'global.json') -Raw |
        ConvertFrom-Json -ErrorAction Stop).sdk.version)
    $actualSdkOutput = @(& $DotNetPath --version 2>$null)
    Assert-WindowsPackageSbomCondition `
        ($LASTEXITCODE -eq 0 -and $actualSdkOutput.Count -eq 1 -and
         [string]$actualSdkOutput[0] -ceq $expectedSdk -and $expectedSdk -ceq '10.0.302') `
        'SdkVersionInvalid'

    $applicationPackage = Resolve-WindowsPackageSbomRegularFile `
        -Path $PackagePath `
        -MaximumBytes ([long]$configuration.limits.maximumPackageBytes) `
        -Code 'ApplicationPackageInvalid'
    $runtimePackage = Resolve-WindowsPackageSbomRegularFile `
        -Path $RuntimePackagePath `
        -MaximumBytes ([long]$configuration.limits.maximumPackageBytes) `
        -Code 'RuntimePackageInvalid'
    Assert-WindowsPackageSbomCondition `
        (-not [string]::Equals(
            $applicationPackage.FullName,
            $runtimePackage.FullName,
            [System.StringComparison]::OrdinalIgnoreCase) -and
         $applicationPackage.Extension -ceq '.msix' -and
         $runtimePackage.Extension -ceq '.msix') `
        'PackageSetInvalid'

    $applicationManifest = Get-WindowsPackageSbomArchiveManifest `
        -Package $applicationPackage `
        -Code 'ApplicationManifestInvalid'
    $runtimeManifest = Get-WindowsPackageSbomArchiveManifest `
        -Package $runtimePackage `
        -Code 'RuntimeManifestInvalid'
    Assert-WindowsPackageSbomCondition `
        ($applicationManifest.Name -ceq $expectedApplicationName -and
         $applicationManifest.Publisher -ceq $expectedApplicationPublisher -and
         $applicationManifest.Version -ceq '0.1.0.0' -and
         $applicationManifest.Architecture -ceq $expectedArchitecture) `
        'ApplicationManifestInvalid'
    Assert-WindowsPackageSbomCondition `
        ($runtimeManifest.Name -ceq $expectedRuntimeName -and
         $runtimeManifest.Publisher -ceq $expectedRuntimePublisher -and
         $runtimeManifest.Version -ceq $expectedRuntimeVersion -and
         $runtimeManifest.Architecture -ceq $expectedArchitecture) `
        'RuntimeManifestInvalid'

    $runtimeDependencyNodes = @($applicationManifest.Document.SelectNodes(
        "/*[local-name()='Package']/*[local-name()='Dependencies']/*[local-name()='PackageDependency']"))
    Assert-WindowsPackageSbomCondition `
        ($runtimeDependencyNodes.Count -eq 1 -and
         $runtimeDependencyNodes[0].GetAttribute('Name') -ceq $expectedRuntimeName -and
         $runtimeDependencyNodes[0].GetAttribute('Publisher') -ceq $expectedRuntimePublisher -and
         $runtimeDependencyNodes[0].GetAttribute('MinVersion') -ceq $expectedRuntimeVersion) `
        'RuntimeDependencyBindingInvalid'

    $applicationSha256 = Get-WindowsPackageSbomSha256 $applicationPackage.FullName
    $runtimeSha256 = Get-WindowsPackageSbomSha256 $runtimePackage.FullName
    $applicationSha1 = Get-WindowsPackageSbomSha1 $applicationPackage.FullName
    $runtimeSha1 = Get-WindowsPackageSbomSha1 $runtimePackage.FullName

    $toolPath = Assert-ExactChildPath `
        -Parent $root `
        -Child (Join-Path $root ([string]$configuration.toolExecutableRelativePath).Replace('/', '\')) `
        -Code 'ToolPathInvalid'
    $tool = Resolve-WindowsPackageSbomRegularFile -Path $toolPath -MaximumBytes 16MB -Code 'ToolInvalid'
    Assert-WindowsPackageSbomCondition `
        ((Get-WindowsPackageSbomSha256 $tool.FullName) -ceq [string]$configuration.shimSha256) `
        'ToolShimHashMismatch'

    $toolRoot = $tool.Directory.FullName
    $nupkgPath = Join-Path $toolRoot (
        ".store\$($configuration.packageId)\$($configuration.version)\" +
        "$($configuration.packageId)\$($configuration.version)\" +
        "$($configuration.packageId).$($configuration.version).nupkg")
    $nupkg = Resolve-WindowsPackageSbomRegularFile -Path $nupkgPath -MaximumBytes 32MB -Code 'ToolPackageInvalid'
    Assert-WindowsPackageSbomCondition `
        ((Get-WindowsPackageSbomSha256 $nupkg.FullName) -ceq [string]$configuration.nupkgSha256) `
        'ToolPackageHashMismatch'
    $toolPayload = Assert-ExactSbomToolPayload `
        -Package $nupkg `
        -ExpectedPackageSha256 ([string]$configuration.nupkgSha256)
    Assert-WindowsPackageSbomCondition `
        ($toolPayload.FileCount -gt 0 -and $toolPayload.TotalBytes -gt 0) `
        'ToolPayloadMismatch'
    $toolVersionResult = Invoke-ExactSbomTool `
        -Tool $tool `
        -Arguments @('Version') `
        -WorkingDirectory $workRoot `
        -TimeoutSeconds 30 `
        -Code 'ToolVersionFailed' `
        -CaptureStandardOutput
    Assert-WindowsPackageSbomCondition `
        ($toolVersionResult.StandardOutput -ceq [string]$configuration.version) `
        'ToolVersionMismatch'

    $applicationDropPath = Join-Path $dropRoot $applicationPackage.Name
    $runtimeDropPath = Join-Path $dropRoot $runtimePackage.Name
    [System.IO.File]::Copy($applicationPackage.FullName, $applicationDropPath, $false)
    [System.IO.File]::Copy($runtimePackage.FullName, $runtimeDropPath, $false)
    Assert-WindowsPackageSbomCondition `
        ((Get-WindowsPackageSbomSha256 $applicationDropPath) -ceq $applicationSha256 -and
         (Get-WindowsPackageSbomSha256 $runtimeDropPath) -ceq $runtimeSha256) `
        'PackageCopyHashMismatch'

    $componentRoot = Assert-ExactChildPath `
        -Parent $root `
        -Child (Join-Path $root ([string]$configuration.componentPath).Replace('/', '\')) `
        -Code 'ComponentPathInvalid'
    $componentRootItem = Get-Item -LiteralPath $componentRoot -Force
    Assert-WindowsPackageSbomCondition $componentRootItem.PSIsContainer 'ComponentPathInvalid'

    $namespaceUniquePart = "$($repositoryBefore.CommitSha)-$applicationSha256-$runtimeSha256"
    $expectedNamespace =
        "$($configuration.namespaceBase)/$($configuration.packageName)/$($applicationManifest.Version)/$namespaceUniquePart"
    $generateArguments = @(
        'generate',
        '-b', $dropRoot,
        '-bc', $componentRoot,
        '-m', $manifestRoot,
        '-pn', [string]$configuration.packageName,
        '-pv', $applicationManifest.Version,
        '-ps', [string]$configuration.packageSupplier,
        '-nsb', [string]$configuration.namespaceBase,
        '-nsu', $namespaceUniquePart,
        '-gt', $repositoryBefore.GenerationTimestamp,
        '-D', 'true',
        '-li', 'false',
        '-pm', 'false',
        '-F', 'false',
        '-P', [string]$configuration.parallelism,
        '-mi', [string]$configuration.manifestInfo,
        '-V', 'Error')
    [void](Invoke-ExactSbomTool `
        -Tool $tool `
        -Arguments $generateArguments `
        -WorkingDirectory $workRoot `
        -TimeoutSeconds ([int]$configuration.limits.toolTimeoutSeconds) `
        -Code 'ToolGenerateFailed')

    $generatedSbomPath = Join-Path $manifestRoot '_manifest\spdx_2.2\manifest.spdx.json'
    $generatedSbomFile = Resolve-WindowsPackageSbomRegularFile `
        -Path $generatedSbomPath `
        -MaximumBytes ([long]$configuration.limits.maximumSbomBytes) `
        -Code 'GeneratedDocumentInvalid'

    # Validate the untouched Microsoft-generated document first. The companion
    # artifact packages and release-set relationships added below are then
    # checked by the stricter repository-owned validator.
    [void](Invoke-ExactSbomTool `
        -Tool $tool `
        -Arguments @(
            'validate',
            '-b', $dropRoot,
            '-m', (Join-Path $manifestRoot '_manifest'),
            '-o', $validationPath,
            '-n', 'true',
            '-F', 'false',
            '-P', [string]$configuration.parallelism,
            '-mi', [string]$configuration.manifestInfo,
            '-V', 'Error') `
        -WorkingDirectory $workRoot `
        -TimeoutSeconds ([int]$configuration.limits.toolTimeoutSeconds) `
        -Code 'ToolValidateFailed')
    $validationFile = Resolve-WindowsPackageSbomRegularFile `
        -Path $validationPath `
        -MaximumBytes 2MB `
        -Code 'OfficialValidationInvalid'
    $validation = (Read-WindowsPackageSbomJson `
        -File $validationFile `
        -Code 'OfficialValidationInvalid').Value
    Assert-WindowsPackageSbomCondition `
        ([string]$validation.Result -ceq 'Success' -and
         [int]$validation.ValidationErrors.Count -eq 0 -and
         @($validation.ValidationErrors.Errors).Count -eq 0) `
        'OfficialValidationFailed'

    $generated = Read-WindowsPackageSbomJson -File $generatedSbomFile -Code 'GeneratedDocumentInvalid'
    $document = $generated.Value
    $rootPackages = @($document.packages | Where-Object { [string]$_.SPDXID -ceq 'SPDXRef-RootPackage' })
    Assert-WindowsPackageSbomCondition `
        ($rootPackages.Count -eq 1 -and @($document.files).Count -eq 2) `
        'GeneratedDocumentContractInvalid'
    Add-SbomProperty -InputObject $rootPackages[0] -Name 'packageFileName' -Value './release-set'

    $applicationArtifact = [ordered]@{
        name = 'IptvSuite.Windows.MsixArtifact'
        SPDXID = $applicationArtifactSpdxId
        versionInfo = $applicationManifest.Version
        downloadLocation = 'NOASSERTION'
        filesAnalyzed = $false
        checksums = @([ordered]@{ algorithm = 'SHA256'; checksumValue = $applicationSha256 })
        packageFileName = $applicationPackage.Name
        licenseConcluded = 'NOASSERTION'
        licenseDeclared = 'NOASSERTION'
        copyrightText = 'NOASSERTION'
    }
    $runtimeArtifact = [ordered]@{
        name = 'Microsoft.WindowsAppRuntime.2.MsixArtifact'
        SPDXID = $runtimeArtifactSpdxId
        versionInfo = $runtimeManifest.Version
        downloadLocation = 'NOASSERTION'
        filesAnalyzed = $false
        checksums = @([ordered]@{ algorithm = 'SHA256'; checksumValue = $runtimeSha256 })
        packageFileName = $runtimePackage.Name
        licenseConcluded = 'NOASSERTION'
        licenseDeclared = 'NOASSERTION'
        copyrightText = 'NOASSERTION'
    }
    Add-SbomProperty `
        -InputObject $document `
        -Name 'packages' `
        -Value (@($document.packages) + @($applicationArtifact, $runtimeArtifact))
    $requiredRelationships = @(
        [ordered]@{
            relationshipType = 'CONTAINS'
            relatedSpdxElement = $applicationArtifactSpdxId
            spdxElementId = 'SPDXRef-RootPackage'
        },
        [ordered]@{
            relationshipType = 'CONTAINS'
            relatedSpdxElement = $runtimeArtifactSpdxId
            spdxElementId = 'SPDXRef-RootPackage'
        },
        [ordered]@{
            relationshipType = 'DEPENDS_ON'
            relatedSpdxElement = $runtimeArtifactSpdxId
            spdxElementId = $applicationArtifactSpdxId
        })
    Add-SbomProperty `
        -InputObject $document `
        -Name 'relationships' `
        -Value (@($document.relationships) + $requiredRelationships)
    Write-WindowsPackageSbomJsonAtomically `
        -Value $document `
        -DestinationPath $generatedSbomFile.FullName `
        -MaximumBytes ([long]$configuration.limits.maximumSbomBytes)
    $generatedSbomFile = Get-Item -LiteralPath $generatedSbomFile.FullName -Force
    $checksumPath = "$($generatedSbomFile.FullName).sha256"
    [System.IO.File]::WriteAllText(
        $checksumPath,
        (Get-WindowsPackageSbomSha256 $generatedSbomFile.FullName),
        [System.Text.Encoding]::ASCII)

    $strictResult = Assert-WindowsPackageSbomDocument `
        -SbomFile $generatedSbomFile `
        -Configuration $configuration `
        -ExpectedNamespace $expectedNamespace `
        -ExpectedVersion $applicationManifest.Version `
        -ApplicationPackage $applicationPackage `
        -RuntimePackage $runtimePackage `
        -ApplicationArtifactSpdxId $applicationArtifactSpdxId `
        -RuntimeArtifactSpdxId $runtimeArtifactSpdxId

    Assert-WindowsPackageSbomCondition `
        ((Get-WindowsPackageSbomSha256 $applicationPackage.FullName) -ceq $applicationSha256 -and
         (Get-WindowsPackageSbomSha256 $runtimePackage.FullName) -ceq $runtimeSha256) `
        'PackageChangedDuringGeneration'
    $sourceBindingAfter = Get-ProductionInputBinding -Configuration $configuration -Root $root
    $repositoryAfter = Get-RepositorySnapshot -Root $root
    Assert-WindowsPackageSbomCondition `
        ($repositoryAfter.CommitSha -ceq $repositoryBefore.CommitSha -and
         $sourceBindingAfter.Sha256 -ceq $sourceBindingBefore.Sha256) `
        'RepositoryChangedDuringGeneration'

    Write-WindowsPackageSbomJsonAtomically `
        -Value $document `
        -DestinationPath $sbomPath `
        -MaximumBytes ([long]$configuration.limits.maximumSbomBytes)
    $publishedSbom = Resolve-WindowsPackageSbomRegularFile `
        -Path $sbomPath `
        -MaximumBytes ([long]$configuration.limits.maximumSbomBytes) `
        -Code 'PublishedDocumentInvalid'
    $publishedStrictResult = Assert-WindowsPackageSbomDocument `
        -SbomFile $publishedSbom `
        -Configuration $configuration `
        -ExpectedNamespace $expectedNamespace `
        -ExpectedVersion $applicationManifest.Version `
        -ApplicationPackage $applicationPackage `
        -RuntimePackage $runtimePackage `
        -ApplicationArtifactSpdxId $applicationArtifactSpdxId `
        -RuntimeArtifactSpdxId $runtimeArtifactSpdxId
    Assert-WindowsPackageSbomCondition `
        ($publishedStrictResult.FileCount -eq $strictResult.FileCount -and
         $publishedStrictResult.ComponentCount -eq $strictResult.ComponentCount -and
         $publishedStrictResult.PackageCount -eq $strictResult.PackageCount -and
         $publishedStrictResult.RelationshipCount -eq $strictResult.RelationshipCount) `
        'PublishedDocumentMismatch'

    $summary = [ordered]@{
        SchemaVersion = 1
        Stage = 'WindowsPackageSbom'
        Result = 'Pass'
        CommitSha = $repositoryBefore.CommitSha
        DotNetSdk = $expectedSdk
        SbomFormat = 'SPDX-2.2'
        SbomFile = $publishedSbom.Name
        SbomLength = [long]$publishedSbom.Length
        SbomSha256 = Get-WindowsPackageSbomSha256 $publishedSbom.FullName
        DocumentNamespace = $expectedNamespace
        ToolPackageId = [string]$configuration.packageId
        ToolVersion = [string]$configuration.version
        ToolPackageSha256 = [string]$configuration.nupkgSha256
        ToolShimSha256 = [string]$configuration.shimSha256
        OfficialValidationPassed = $true
        StrictValidationPassed = $true
        ProductionInputCount = @($sourceBindingBefore.Records).Count
        ProductionInputSetSha256 = $sourceBindingBefore.Sha256
        ApplicationPackageFile = $applicationPackage.Name
        ApplicationPackageLength = [long]$applicationPackage.Length
        ApplicationPackageSha256 = $applicationSha256
        ApplicationIdentityName = $applicationManifest.Name
        ApplicationVersion = $applicationManifest.Version
        RuntimePackageFile = $runtimePackage.Name
        RuntimePackageLength = [long]$runtimePackage.Length
        RuntimePackageSha256 = $runtimeSha256
        RuntimeIdentityName = $runtimeManifest.Name
        RuntimeVersion = $runtimeManifest.Version
        Architecture = $expectedArchitecture
        FileCount = $strictResult.FileCount
        ComponentCount = $strictResult.ComponentCount
        PackageCount = $strictResult.PackageCount
        RelationshipCount = $strictResult.RelationshipCount
        BlockerDisposition = 'HostedAcceptancePending'
        SbomPending = $true
    }
    Write-WindowsPackageSbomJsonAtomically `
        -Value $summary `
        -DestinationPath $summaryPath `
        -MaximumBytes 1MB

    Write-Host "Package-bound SPDX 2.2 SBOM generated and validated for the exact signed x64 release set."
    return [pscustomobject]$summary
}
catch {
    foreach ($outputPath in @($sbomPath, $summaryPath)) {
        if (Test-Path -LiteralPath $outputPath) {
            Remove-Item -LiteralPath $outputPath -Force -ErrorAction SilentlyContinue
        }
    }
    throw
}
finally {
    if (Test-Path -LiteralPath $workRoot) {
        $resolvedWorkParent = [System.IO.Path]::GetFullPath($workParent).TrimEnd('\')
        $resolvedWorkRoot = [System.IO.Path]::GetFullPath($workRoot)
        if ($resolvedWorkRoot.StartsWith(
                $resolvedWorkParent + '\',
                [System.StringComparison]::OrdinalIgnoreCase) -and
            [System.IO.Path]::GetFileName($resolvedWorkRoot) -cmatch '\A[0-9a-f]{32}\z') {
            Remove-Item -LiteralPath $resolvedWorkRoot -Recurse -Force -ErrorAction SilentlyContinue
        }
    }
}
