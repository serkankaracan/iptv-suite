[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$DotNetPath,
    [switch]$AllowBenchmark,
    [switch]$ReferenceMode,
    [string]$CacheCondition,
    [string]$PowerCondition,
    [string]$ThermalCondition,
    [string]$BackgroundCondition
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

if (-not $AllowBenchmark) {
    throw 'M14 catalog benchmark requires explicit -AllowBenchmark acknowledgement.'
}

$referenceDeclarations = @(
    [pscustomobject]@{ Parameter = 'CacheCondition'; Value = $CacheCondition; Expected = 'Warm' },
    [pscustomobject]@{ Parameter = 'PowerCondition'; Value = $PowerCondition; Expected = 'AcStable' },
    [pscustomobject]@{ Parameter = 'ThermalCondition'; Value = $ThermalCondition; Expected = 'Nominal' },
    [pscustomobject]@{ Parameter = 'BackgroundCondition'; Value = $BackgroundCondition; Expected = 'Controlled' }
)
foreach ($declaration in $referenceDeclarations) {
    $isBound = $PSBoundParameters.ContainsKey([string]$declaration.Parameter)
    if ($ReferenceMode -and
        (-not $isBound -or [string]$declaration.Value -cne [string]$declaration.Expected)) {
        throw "M14 reference mode requires exact -$($declaration.Parameter) $($declaration.Expected) declaration."
    }

    if (-not $ReferenceMode -and $isBound) {
        throw "-$($declaration.Parameter) is valid only with explicit -ReferenceMode."
    }
}

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$globalJsonPath = Join-Path $repositoryRoot 'global.json'
$project = Join-Path $repositoryRoot 'apps\windows\tests\IptvSuite.IntegrationTests\IptvSuite.IntegrationTests.csproj'
$evidenceRoot = Join-Path $repositoryRoot '.artifacts\m14-catalog-benchmark\evidence'
$evidencePath = Join-Path $evidenceRoot 'benchmark-summary.json'
$temporaryEvidencePath = $evidencePath + '.tmp'
$legacyManifestPath = Join-Path $evidenceRoot 'corpus-manifest.json'
$legacyTemporaryManifestPath = $legacyManifestPath + '.tmp'
$dotnet = (Resolve-Path -LiteralPath $DotNetPath).Path

function Get-RepositoryHead {
    $output = @(& git -C $repositoryRoot rev-parse --verify HEAD)
    $exitCode = $LASTEXITCODE
    $head = ($output -join '').Trim().ToLowerInvariant()
    if ($exitCode -ne 0 -or $head -notmatch '^[0-9a-f]{40}$') {
        throw 'Unable to bind the M14 catalog benchmark to the repository HEAD.'
    }

    return $head
}

function Get-RepositoryStatus {
    $output = @(& git -C $repositoryRoot status --porcelain=v1 --untracked-files=normal)
    if ($LASTEXITCODE -ne 0) {
        throw 'Unable to validate the repository worktree for the M14 catalog benchmark.'
    }

    return $output
}

$globalJson = Get-Content -LiteralPath $globalJsonPath -Raw | ConvertFrom-Json
$expectedSdk = [string]$globalJson.sdk.version
if ($expectedSdk -notmatch '^\d+\.\d+\.\d+$' -or
    [string]$globalJson.sdk.rollForward -ne 'disable' -or
    [bool]$globalJson.sdk.allowPrerelease) {
    throw 'The repository exact .NET SDK contract is invalid.'
}

$actualSdkOutput = @(& $dotnet --version 2>$null)
$actualSdkExitCode = $LASTEXITCODE
$actualSdk = ($actualSdkOutput -join '').Trim()
if ($actualSdkExitCode -ne 0 -or $actualSdk -ne $expectedSdk) {
    throw "The M14 catalog benchmark requires exact .NET SDK $expectedSdk."
}

$initialHead = Get-RepositoryHead
$initialStatus = @(Get-RepositoryStatus)
if ($initialStatus.Count -ne 0) {
    throw 'M14 catalog benchmark requires a clean repository.'
}

New-Item -ItemType Directory -Force -Path $evidenceRoot | Out-Null
Remove-Item -LiteralPath $evidencePath -Force -ErrorAction SilentlyContinue
Remove-Item -LiteralPath $temporaryEvidencePath -Force -ErrorAction SilentlyContinue
Remove-Item -LiteralPath $legacyManifestPath -Force -ErrorAction SilentlyContinue
Remove-Item -LiteralPath $legacyTemporaryManifestPath -Force -ErrorAction SilentlyContinue

$environmentNames = @(
    'DOTNET_CLI_USE_MSBUILD_SERVER',
    'MSBUILDDISABLENODEREUSE',
    'IPTVSUITE_M14_CATALOG_BENCHMARK',
    'IPTVSUITE_M14_CATALOG_EVIDENCE_ROOT',
    'IPTVSUITE_M14_CATALOG_COMMIT',
    'IPTVSUITE_M14_CATALOG_VALIDATED_SDK',
    'IPTVSUITE_M14_CATALOG_REFERENCE_MODE',
    'IPTVSUITE_M14_CATALOG_CACHE_CONDITION',
    'IPTVSUITE_M14_CATALOG_POWER_CONDITION',
    'IPTVSUITE_M14_CATALOG_THERMAL_CONDITION',
    'IPTVSUITE_M14_CATALOG_BACKGROUND_CONDITION'
)
$previousEnvironment = @{}
foreach ($name in $environmentNames) {
    $previousEnvironment[$name] = [Environment]::GetEnvironmentVariable($name, 'Process')
}

try {
    $env:DOTNET_CLI_USE_MSBUILD_SERVER = '0'
    $env:MSBUILDDISABLENODEREUSE = '1'
    $env:IPTVSUITE_M14_CATALOG_BENCHMARK = '1'
    $env:IPTVSUITE_M14_CATALOG_EVIDENCE_ROOT = [System.IO.Path]::GetFullPath($evidenceRoot)
    $env:IPTVSUITE_M14_CATALOG_COMMIT = $initialHead
    $env:IPTVSUITE_M14_CATALOG_VALIDATED_SDK = $expectedSdk
    $env:IPTVSUITE_M14_CATALOG_REFERENCE_MODE = '0'
    [Environment]::SetEnvironmentVariable('IPTVSUITE_M14_CATALOG_CACHE_CONDITION', $null, 'Process')
    [Environment]::SetEnvironmentVariable('IPTVSUITE_M14_CATALOG_POWER_CONDITION', $null, 'Process')
    [Environment]::SetEnvironmentVariable('IPTVSUITE_M14_CATALOG_THERMAL_CONDITION', $null, 'Process')
    [Environment]::SetEnvironmentVariable('IPTVSUITE_M14_CATALOG_BACKGROUND_CONDITION', $null, 'Process')
    if ($ReferenceMode) {
        $env:IPTVSUITE_M14_CATALOG_REFERENCE_MODE = '1'
        $env:IPTVSUITE_M14_CATALOG_CACHE_CONDITION = $CacheCondition
        $env:IPTVSUITE_M14_CATALOG_POWER_CONDITION = $PowerCondition
        $env:IPTVSUITE_M14_CATALOG_THERMAL_CONDITION = $ThermalCondition
        $env:IPTVSUITE_M14_CATALOG_BACKGROUND_CONDITION = $BackgroundCondition
    }

    & $dotnet test $project -c Release -p:Platform=x64 --no-restore `
        --filter 'FullyQualifiedName=IptvSuite.IntegrationTests.M14CatalogPerformanceBenchmarkTests.MeasureM14CatalogBenchmarkMatrix' `
        --logger 'console;verbosity=minimal' -m:1
    if ($LASTEXITCODE -ne 0) {
        throw 'M14 catalog benchmark test failed.'
    }
}
finally {
    foreach ($name in $environmentNames) {
        [Environment]::SetEnvironmentVariable($name, $previousEnvironment[$name], 'Process')
    }
}

$finalHead = Get-RepositoryHead
$finalStatus = @(Get-RepositoryStatus)
if ($finalHead -ne $initialHead -or $finalStatus.Count -ne 0) {
    throw 'Repository binding changed during the M14 catalog benchmark.'
}

if (-not (Test-Path -LiteralPath $evidencePath -PathType Leaf) -or
    (Test-Path -LiteralPath $temporaryEvidencePath)) {
    throw 'M14 catalog benchmark evidence was not published atomically.'
}

$evidenceText = Get-Content -LiteralPath $evidencePath -Raw
try {
    $evidence = $evidenceText | ConvertFrom-Json
}
catch {
    throw 'M14 catalog benchmark evidence is not valid JSON.'
}
if ($evidenceText.IndexOf($initialHead, [StringComparison]::OrdinalIgnoreCase) -lt 0) {
    throw 'M14 catalog benchmark evidence is not bound to the initial repository commit.'
}
if ($ReferenceMode) {
    if ([string]$evidence.result -ne 'passed' -or
        -not ([bool]$evidence.referenceModeRequested) -or
        -not ([bool]$evidence.measurementIntegrityVerified) -or
        -not ([bool]$evidence.conditionDeclarationsComplete) -or
        -not ([bool]$evidence.referenceEligible) -or
        [string]$evidence.conditions.cache.verification -cne 'Declared' -or
        [string]$evidence.conditions.cache.value -cne 'Warm' -or
        [string]$evidence.conditions.power.verification -cne 'Declared' -or
        [string]$evidence.conditions.power.value -cne 'AcStable' -or
        [string]$evidence.conditions.thermal.verification -cne 'Declared' -or
        [string]$evidence.conditions.thermal.value -cne 'Nominal' -or
        [string]$evidence.conditions.background.verification -cne 'Declared' -or
        [string]$evidence.conditions.background.value -cne 'Controlled') {
        throw 'M14 catalog benchmark reference evidence is not eligible.'
    }
}
elseif ([string]$evidence.result -ne 'passed' -or
    [bool]$evidence.referenceModeRequested -or
    [bool]$evidence.referenceEligible) {
    throw 'M14 catalog benchmark evidence result or foundation eligibility is invalid.'
}
if ((Test-Path -LiteralPath $legacyManifestPath) -or
    (Test-Path -LiteralPath $legacyTemporaryManifestPath)) {
    throw 'M14 catalog benchmark retained a legacy transient corpus manifest.'
}

$mode = if ($ReferenceMode) { 'reference' } else { 'foundation' }
Write-Host "M14 catalog benchmark $mode mode passed. Evidence: $evidencePath"
