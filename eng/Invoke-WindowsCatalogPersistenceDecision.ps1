[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$DotNetPath,
    [switch]$AllowDecision
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

if (-not $AllowDecision) {
    throw 'M8 catalog Decision requires explicit -AllowDecision acknowledgement.'
}

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$dotnet = (Resolve-Path -LiteralPath $DotNetPath).Path
$project = Join-Path $repositoryRoot 'apps\windows\tests\IptvSuite.IntegrationTests\IptvSuite.IntegrationTests.csproj'
$evidenceRoot = Join-Path $repositoryRoot '.artifacts\m8-catalog-performance\evidence'
$initialHead = (& git -C $repositoryRoot rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0 -or $initialHead -notmatch '^[0-9a-f]{40}$') {
    throw 'Unable to bind M8 Decision to the repository HEAD.'
}

$initialStatus = @(& git -C $repositoryRoot status --porcelain=v1)
if ($LASTEXITCODE -ne 0 -or $initialStatus.Count -ne 0) {
    throw 'M8 catalog Decision requires a clean repository.'
}

New-Item -ItemType Directory -Force -Path $evidenceRoot | Out-Null
$env:IPTVSUITE_M8_CATALOG_DECISION = '1'
$env:IPTVSUITE_M8_CATALOG_EVIDENCE_ROOT = [System.IO.Path]::GetFullPath($evidenceRoot)
$env:IPTVSUITE_M8_CATALOG_COMMIT = $initialHead
try {
    & $dotnet test $project -c Release -p:Platform=x64 --no-restore `
        --filter 'FullyQualifiedName~SqliteCatalogPerformanceDecisionTests.MeasureParserToProtectedSqliteDecisionMatrix' `
        --logger 'console;verbosity=minimal' -m:1
    if ($LASTEXITCODE -ne 0) {
        throw 'M8 catalog Decision test failed.'
    }
}
finally {
    Remove-Item Env:IPTVSUITE_M8_CATALOG_DECISION -ErrorAction SilentlyContinue
    Remove-Item Env:IPTVSUITE_M8_CATALOG_EVIDENCE_ROOT -ErrorAction SilentlyContinue
    Remove-Item Env:IPTVSUITE_M8_CATALOG_COMMIT -ErrorAction SilentlyContinue
}

$finalHead = (& git -C $repositoryRoot rev-parse HEAD).Trim()
$finalStatus = @(& git -C $repositoryRoot status --porcelain=v1)
if ($LASTEXITCODE -ne 0 -or $finalHead -ne $initialHead -or $finalStatus.Count -ne 0) {
    throw 'Repository binding changed during M8 catalog Decision.'
}

$evidencePath = Join-Path $evidenceRoot 'decision-summary.json'
if (-not (Test-Path -LiteralPath $evidencePath -PathType Leaf)) {
    throw 'M8 catalog Decision evidence was not published.'
}

Write-Host "M8 catalog persistence Decision passed. Evidence: $evidencePath"
