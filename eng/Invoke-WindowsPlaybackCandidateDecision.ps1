[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$projectPath = Join-Path $repositoryRoot 'apps\windows\tests\IptvSuite.PlaybackCompatibilitySpike\IptvSuite.PlaybackCompatibilitySpike.csproj'
$lockPath = Join-Path (Split-Path -Parent $projectPath) 'packages.lock.json'
$artifactRoot = Join-Path $repositoryRoot '.artifacts\m10-playback-candidate'
$evidenceRoot = Join-Path $artifactRoot 'evidence'
$evidencePath = Join-Path $evidenceRoot 'decision-summary.json'
$expectedPackageVersion = '3.0.23.1'
$expectedPackageId = 'videolan.libvlc.windows'
$expectedBlocker = 'build/x64/plugins/codec/libx26410b_plugin.dll'

function Get-RelativePackagePath {
    param([string] $FullName, [string] $PackageRoot)
    return $FullName.Substring($PackageRoot.Length + 1).Replace('\', '/')
}

$dotnet = Get-Command dotnet -ErrorAction Stop
$actualSdk = (& $dotnet.Source --version).Trim()
if ($LASTEXITCODE -ne 0 -or $actualSdk -ne '10.0.302') {
    throw "Expected .NET SDK 10.0.302, received '$actualSdk'."
}

& $dotnet.Source restore $projectPath --locked-mode -p:Platform=x64
if ($LASTEXITCODE -ne 0) {
    throw 'Locked restore failed for the M10 playback candidate.'
}

$lock = Get-Content -LiteralPath $lockPath -Raw -Encoding utf8 | ConvertFrom-Json
$resolvedVersion = $lock.dependencies.'net10.0-windows10.0.26100'.'VideoLAN.LibVLC.Windows'.resolved
if ($resolvedVersion -ne $expectedPackageVersion) {
    throw "Expected VideoLAN.LibVLC.Windows $expectedPackageVersion, received '$resolvedVersion'."
}

$packageCache = if ([string]::IsNullOrWhiteSpace($env:NUGET_PACKAGES)) {
    Join-Path ([Environment]::GetFolderPath('UserProfile')) '.nuget\packages'
} else {
    [System.IO.Path]::GetFullPath($env:NUGET_PACKAGES)
}
$packageRoot = Join-Path $packageCache "$expectedPackageId\$expectedPackageVersion"
$nativeRoot = Join-Path $packageRoot 'build\x64'
$nupkgPath = Join-Path $packageRoot "$expectedPackageId.$expectedPackageVersion.nupkg"
$nuspecPath = Join-Path $packageRoot 'videolan.libvlc.windows.nuspec'
foreach ($requiredPath in @($nativeRoot, $nupkgPath, $nuspecPath)) {
    if (-not (Test-Path -LiteralPath $requiredPath)) {
        throw "Required restored package path is missing: $requiredPath"
    }
}

[xml] $nuspec = Get-Content -LiteralPath $nuspecPath -Raw -Encoding utf8
$licenseExpression = [string]$nuspec.package.metadata.license.'#text'
if ($licenseExpression -ne 'LGPL-2.1-or-later') {
    throw "Unexpected package-level license expression '$licenseExpression'."
}

$riskPatterns = @(
    '(?i)^build/x64/plugins/codec/libx264(?:10b)?_plugin\.dll$',
    '(?i)^build/x64/plugins/codec/libx265_plugin\.dll$',
    '(?i)^build/x64/plugins/codec/liba52_plugin\.dll$',
    '(?i)^build/x64/plugins/codec/libdca_plugin\.dll$',
    '(?i)^build/x64/plugins/codec/libfaad_plugin\.dll$',
    '(?i)^build/x64/plugins/codec/libmad_plugin\.dll$',
    '(?i)^build/x64/plugins/codec/liblibmpeg2_plugin\.dll$'
)
$pluginFiles = @(Get-ChildItem -LiteralPath (Join-Path $nativeRoot 'plugins') -Recurse -File -Filter '*.dll')
$riskFiles = @($pluginFiles | Where-Object {
    $relativePath = Get-RelativePackagePath -FullName $_.FullName -PackageRoot $packageRoot
    $riskPatterns.Where({ $relativePath -match $_ }, 'First').Count -gt 0
} | ForEach-Object {
    [ordered]@{
        Path = Get-RelativePackagePath -FullName $_.FullName -PackageRoot $packageRoot
        Sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        SizeBytes = $_.Length
    }
})
if ($riskFiles.Path -notcontains $expectedBlocker) {
    throw "The exact expected GPL-risk blocker '$expectedBlocker' was not found; review the package manually."
}

Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive = [System.IO.Compression.ZipFile]::OpenRead($nupkgPath)
try {
    $noticeEntries = @($archive.Entries | Where-Object {
        $_.FullName -match '(?i)(^|/)(copying|license|notice)(\.|/|$)'
    } | ForEach-Object { $_.FullName })
} finally {
    $archive.Dispose()
}

$decision = [ordered]@{
    SchemaVersion = 1
    Decision = 'NO-GO'
    ReasonCode = 'ExactBinaryLicenseBoundaryUnresolved'
    PackageId = 'VideoLAN.LibVLC.Windows'
    PackageVersion = $resolvedVersion
    PackageLicenseExpression = $licenseExpression
    PackageSha256 = (Get-FileHash -LiteralPath $nupkgPath -Algorithm SHA256).Hash.ToLowerInvariant()
    X64PluginCount = $pluginFiles.Count
    GplRiskFiles = $riskFiles
    EmbeddedLicenseOrNoticeEntries = $noticeEntries
    Invariant = 'A package-level LGPL expression cannot override unresolved exact binary/plugin license and source obligations.'
    FollowUp = 'Do not ship this candidate; validate the Windows native Tier A fallback in a separate ADR.'
}

if (Test-Path -LiteralPath $artifactRoot) {
    Remove-Item -LiteralPath $artifactRoot -Recurse -Force
}
New-Item -ItemType Directory -Path $evidenceRoot -Force | Out-Null
$json = $decision | ConvertTo-Json -Depth 8
[System.IO.File]::WriteAllText($evidencePath, $json, [System.Text.UTF8Encoding]::new($false))

Write-Host "M10 playback candidate decision: NO-GO ($($decision.ReasonCode))."
Write-Host "Evidence: $evidencePath"
