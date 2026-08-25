[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$DotNetPath,
    [switch]$AllowBenchmark,
    [switch]$ReferenceMode,
    [string]$CacheCondition,
    [string]$PowerCondition,
    [string]$ThermalCondition,
    [string]$BackgroundCondition,
    [string]$RunnerProfileId,
    [string]$BaselineEvidencePath
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

$runnerProfileBound = $PSBoundParameters.ContainsKey('RunnerProfileId')
if ($ReferenceMode -and
    (-not $runnerProfileBound -or
        [string]::IsNullOrWhiteSpace($RunnerProfileId) -or
        $RunnerProfileId -cnotmatch '^[a-z0-9][a-z0-9._-]{0,63}$')) {
    throw 'M14 reference mode requires -RunnerProfileId matching ^[a-z0-9][a-z0-9._-]{0,63}$.'
}
if (-not $ReferenceMode -and $runnerProfileBound) {
    throw '-RunnerProfileId is valid only with explicit -ReferenceMode.'
}

$baselineEvidenceBound = $PSBoundParameters.ContainsKey('BaselineEvidencePath')
if ($baselineEvidenceBound -and -not $ReferenceMode) {
    throw '-BaselineEvidencePath is valid only with explicit -ReferenceMode.'
}

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$globalJsonPath = Join-Path $repositoryRoot 'global.json'
$project = Join-Path $repositoryRoot 'apps\windows\tests\IptvSuite.IntegrationTests\IptvSuite.IntegrationTests.csproj'
$evidenceRoot = Join-Path $repositoryRoot '.artifacts\m14-catalog-benchmark\evidence'
$evidencePath = Join-Path $evidenceRoot 'benchmark-summary.json'
$temporaryEvidencePath = $evidencePath + '.tmp'
$regressionEvidencePath = Join-Path $evidenceRoot 'regression-summary.json'
$temporaryRegressionEvidencePath = $regressionEvidencePath + '.tmp'
$legacyManifestPath = Join-Path $evidenceRoot 'corpus-manifest.json'
$legacyTemporaryManifestPath = $legacyManifestPath + '.tmp'
$dotnet = (Resolve-Path -LiteralPath $DotNetPath).Path
$regressionHelper = Join-Path $PSScriptRoot 'WindowsCatalogBenchmarkRegression.ps1'
. $regressionHelper

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
    (Get-M14CatalogBoolean $globalJson.sdk 'allowPrerelease')) {
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

$baselineRecord = $null
if ($baselineEvidenceBound) {
    $baselineRecord = Import-M14CatalogBenchmarkEvidence -Path $BaselineEvidencePath
    $baselinePath = [IO.Path]::GetFullPath([string]$baselineRecord.FullPath)
    $evidenceRootPrefix = [IO.Path]::GetFullPath($evidenceRoot).TrimEnd(
        [IO.Path]::DirectorySeparatorChar,
        [IO.Path]::AltDirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    if ($baselinePath.StartsWith($evidenceRootPrefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'M14 regression baseline must be outside the transient candidate evidence directory.'
    }

    Assert-M14CatalogBenchmarkReferenceEvidence `
        -Record $baselineRecord `
        -ExpectedRunnerProfileId $RunnerProfileId
    & git -C $repositoryRoot merge-base --is-ancestor `
        ([string]$baselineRecord.Evidence.commitSha) $initialHead
    if ($LASTEXITCODE -ne 0) {
        throw 'M14 regression baseline commit must be an ancestor of or equal to repository HEAD.'
    }
}

New-Item -ItemType Directory -Force -Path $evidenceRoot | Out-Null
Remove-Item -LiteralPath $evidencePath -Force -ErrorAction SilentlyContinue
Remove-Item -LiteralPath $temporaryEvidencePath -Force -ErrorAction SilentlyContinue
Remove-Item -LiteralPath $regressionEvidencePath -Force -ErrorAction SilentlyContinue
Remove-Item -LiteralPath $temporaryRegressionEvidencePath -Force -ErrorAction SilentlyContinue
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
    'IPTVSUITE_M14_CATALOG_BACKGROUND_CONDITION',
    'IPTVSUITE_M14_CATALOG_RUNNER_PROFILE_ID'
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
    [Environment]::SetEnvironmentVariable('IPTVSUITE_M14_CATALOG_RUNNER_PROFILE_ID', $null, 'Process')
    if ($ReferenceMode) {
        $env:IPTVSUITE_M14_CATALOG_REFERENCE_MODE = '1'
        $env:IPTVSUITE_M14_CATALOG_CACHE_CONDITION = $CacheCondition
        $env:IPTVSUITE_M14_CATALOG_POWER_CONDITION = $PowerCondition
        $env:IPTVSUITE_M14_CATALOG_THERMAL_CONDITION = $ThermalCondition
        $env:IPTVSUITE_M14_CATALOG_BACKGROUND_CONDITION = $BackgroundCondition
        $env:IPTVSUITE_M14_CATALOG_RUNNER_PROFILE_ID = $RunnerProfileId
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
if ((Get-M14CatalogString $evidence 'commitSha') -cne $initialHead) {
    throw 'M14 catalog benchmark evidence commit binding is invalid.'
}
if ($ReferenceMode) {
    if ((Get-M14CatalogString $evidence 'result') -cne 'passed' -or
        -not (Get-M14CatalogBoolean $evidence 'referenceModeRequested') -or
        -not (Get-M14CatalogBoolean $evidence 'measurementIntegrityVerified') -or
        -not (Get-M14CatalogBoolean $evidence 'conditionDeclarationsComplete') -or
        -not (Get-M14CatalogBoolean $evidence 'referenceEligible') -or
        (Get-M14CatalogString $evidence.runnerProfile 'verification') -cne 'Declared' -or
        (Get-M14CatalogString $evidence.runnerProfile 'value') -cne $RunnerProfileId -or
        (Get-M14CatalogString $evidence.conditions.cache 'verification') -cne 'Declared' -or
        (Get-M14CatalogString $evidence.conditions.cache 'value') -cne 'Warm' -or
        (Get-M14CatalogString $evidence.conditions.power 'verification') -cne 'Declared' -or
        (Get-M14CatalogString $evidence.conditions.power 'value') -cne 'AcStable' -or
        (Get-M14CatalogString $evidence.conditions.thermal 'verification') -cne 'Declared' -or
        (Get-M14CatalogString $evidence.conditions.thermal 'value') -cne 'Nominal' -or
        (Get-M14CatalogString $evidence.conditions.background 'verification') -cne 'Declared' -or
        (Get-M14CatalogString $evidence.conditions.background 'value') -cne 'Controlled') {
        throw 'M14 catalog benchmark reference evidence is not eligible.'
    }
}
elseif ((Get-M14CatalogString $evidence 'result') -cne 'passed' -or
    (Get-M14CatalogBoolean $evidence 'referenceModeRequested') -or
    (Get-M14CatalogBoolean $evidence 'referenceEligible') -or
    (Get-M14CatalogString $evidence.runnerProfile 'verification') -cne 'Unverified' -or
    (Get-M14CatalogString $evidence.runnerProfile 'value') -cne 'Unverified') {
    throw 'M14 catalog benchmark evidence result or foundation eligibility is invalid.'
}
$candidateRecord = Import-M14CatalogBenchmarkEvidence -Path $evidencePath
if ($ReferenceMode) {
    Assert-M14CatalogBenchmarkReferenceEvidence `
        -Record $candidateRecord `
        -ExpectedRunnerProfileId $RunnerProfileId
}
if ((Test-Path -LiteralPath $legacyManifestPath) -or
    (Test-Path -LiteralPath $legacyTemporaryManifestPath)) {
    throw 'M14 catalog benchmark retained a legacy transient corpus manifest.'
}

if ($baselineEvidenceBound) {
    $stableBaselineRecord = Import-M14CatalogBenchmarkEvidence -Path $baselineRecord.FullPath
    if ([string]$stableBaselineRecord.Sha256 -cne [string]$baselineRecord.Sha256 -or
        [long]$stableBaselineRecord.ByteLength -ne [long]$baselineRecord.ByteLength) {
        throw 'M14 regression baseline evidence changed during the benchmark.'
    }

    $regressionSummary = Compare-M14CatalogRegression `
        -BaselineRecord $baselineRecord `
        -CandidateRecord $candidateRecord `
        -BaselineCommitAncestorOrSelf $true `
        -BaselineContentStable $true
    Write-M14CatalogRegressionSummaryAtomically `
        -Summary $regressionSummary `
        -Path $regressionEvidencePath
    if (-not (Get-M14CatalogBoolean $regressionSummary 'allPassed')) {
        throw "M14 same-runner regression gate failed. Evidence: $regressionEvidencePath"
    }
}

$mode = if ($ReferenceMode) { 'reference' } else { 'foundation' }
Write-Host "M14 catalog benchmark $mode mode passed. Evidence: $evidencePath"
