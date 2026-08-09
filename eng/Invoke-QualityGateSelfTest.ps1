[CmdletBinding()]
param(
    [string]$DotNetPath = "dotnet",

    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$unitTestProject = Join-Path $repositoryRoot "apps\windows\tests\IptvSuite.UnitTests\IptvSuite.UnitTests.csproj"
$sentinelFilter = "FullyQualifiedName=IptvSuite.UnitTests.QualityGateSentinelTests.PipelineStopsWhenSentinelIsExplicitlyArmed"
$armVariable = "IPTV_SUITE_ARM_QUALITY_GATE_SENTINEL"
$previousValue = [Environment]::GetEnvironmentVariable($armVariable, "Process")
$selfTestRoot = Join-Path $repositoryRoot ".artifacts\quality-gates\self-test"
$expectedSelfTestRoot = [IO.Path]::GetFullPath(
    (Join-Path $repositoryRoot ".artifacts\quality-gates\self-test"))

function Assert-SentinelResult {
    param(
        [Parameter(Mandatory)]
        [string]$TrxPath,

        [Parameter(Mandatory)]
        [ValidateSet("Failed", "Passed")]
        [string]$ExpectedOutcome
    )

    if (-not (Test-Path -LiteralPath $TrxPath -PathType Leaf)) {
        throw "The quality-gate sentinel did not produce its expected TRX evidence."
    }

    [xml]$trx = Get-Content -Raw -LiteralPath $TrxPath
    $results = @($trx.SelectNodes("//*[local-name()='UnitTestResult']"))
    $testMethods = @($trx.SelectNodes("//*[local-name()='UnitTest']/*[local-name()='TestMethod']"))
    if ($results.Count -ne 1 -or
        $results[0].GetAttribute("testName") -ne "PipelineStopsWhenSentinelIsExplicitlyArmed" -or
        $results[0].GetAttribute("outcome") -ne $ExpectedOutcome -or
        $testMethods.Count -ne 1 -or
        $testMethods[0].GetAttribute("className") -ne "IptvSuite.UnitTests.QualityGateSentinelTests" -or
        $testMethods[0].GetAttribute("name") -ne "PipelineStopsWhenSentinelIsExplicitlyArmed") {
        throw "The quality-gate sentinel TRX did not contain the exact expected test and outcome."
    }
}

$resolvedSelfTestRoot = [IO.Path]::GetFullPath($selfTestRoot)
if ($resolvedSelfTestRoot -ne $expectedSelfTestRoot -or
    [IO.Directory]::GetParent($resolvedSelfTestRoot).FullName -ne
        [IO.Path]::GetFullPath((Join-Path $repositoryRoot ".artifacts\quality-gates"))) {
    throw "Refusing to use an unexpected self-test result directory."
}

if (Test-Path -LiteralPath $selfTestRoot) {
    throw "The self-test result directory must not exist before the isolated probe."
}
New-Item -ItemType Directory -Path $selfTestRoot -Force | Out-Null

try {
    [Environment]::SetEnvironmentVariable($armVariable, "1", "Process")
    & $DotNetPath test $unitTestProject `
        -c $Configuration `
        --no-build `
        --no-restore `
        --filter $sentinelFilter `
        --logger "trx;LogFileName=armed.trx" `
        --results-directory $selfTestRoot `
        --nologo

    $armedExitCode = $LASTEXITCODE
    if ($armedExitCode -eq 0) {
        throw "The deliberately armed quality-gate sentinel unexpectedly passed."
    }
    Assert-SentinelResult -TrxPath (Join-Path $selfTestRoot "armed.trx") -ExpectedOutcome "Failed"

    [Environment]::SetEnvironmentVariable($armVariable, $null, "Process")
    & $DotNetPath test $unitTestProject `
        -c $Configuration `
        --no-build `
        --no-restore `
        --filter $sentinelFilter `
        --logger "trx;LogFileName=disarmed.trx" `
        --results-directory $selfTestRoot `
        --nologo

    if ($LASTEXITCODE -ne 0) {
        throw "The disarmed quality-gate sentinel did not return to green."
    }
    Assert-SentinelResult -TrxPath (Join-Path $selfTestRoot "disarmed.trx") -ExpectedOutcome "Passed"

    Write-Host "Quality-gate self-test passed: armed invocation failed and disarmed invocation passed."
}
finally {
    [Environment]::SetEnvironmentVariable($armVariable, $previousValue, "Process")
    if (Test-Path -LiteralPath $selfTestRoot) {
        if ((Get-Item -LiteralPath $selfTestRoot -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) {
            throw "Refusing to clean a reparse-point self-test directory."
        }

        $selfTestEntries = @(Get-ChildItem -LiteralPath $selfTestRoot -Force)
        foreach ($entry in $selfTestEntries) {
            if ($entry.PSIsContainer -or
                ($entry.Attributes -band [IO.FileAttributes]::ReparsePoint) -or
                $entry.Name -notin @("armed.trx", "disarmed.trx")) {
                throw "Refusing to clean an unexpected self-test entry."
            }

            Remove-Item -LiteralPath $entry.FullName -Force
        }

        Remove-Item -LiteralPath $selfTestRoot -Force
    }
}
