[CmdletBinding()]
param(
    [string]$DotNetPath = "dotnet"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$solutionPath = Join-Path $repositoryRoot "apps\windows\IptvSuite.Windows.sln"
$nuGetConfigPath = Join-Path $repositoryRoot "NuGet.config"
$globalJsonPath = Join-Path $repositoryRoot "global.json"
$fixtureSpecificationPath = Join-Path $repositoryRoot "apps\windows\testdata\m2\fixture-spec.json"
$fixtureLicenseSourcePath = Join-Path $repositoryRoot "apps\windows\testdata\LICENSES\LicenseRef-IPTVSuite-Synthetic-Test-Only.txt"
$testingToolPath = Join-Path $repositoryRoot "apps\windows\tests\IptvSuite.Testing\bin\Release\net10.0\IptvSuite.Testing.dll"
$qualityRoot = Join-Path $repositoryRoot ".artifacts\quality-gates"
$testResultsRoot = Join-Path $qualityRoot "test-results"
$fixtureRoot = Join-Path $qualityRoot "fixtures"
$evidenceRoot = Join-Path $qualityRoot "evidence"
$summaryPath = Join-Path $evidenceRoot "quality-summary.json"
$selfTestScript = Join-Path $PSScriptRoot "Invoke-QualityGateSelfTest.ps1"
# A single MSBuild node keeps the gate reliable on high-core Windows hosts and bounds process memory.
$maximumBuildNodes = 1

$testProjects = [ordered]@{
    architecture = Join-Path $repositoryRoot "apps\windows\tests\IptvSuite.ArchitectureTests\IptvSuite.ArchitectureTests.csproj"
    unit = Join-Path $repositoryRoot "apps\windows\tests\IptvSuite.UnitTests\IptvSuite.UnitTests.csproj"
    integration = Join-Path $repositoryRoot "apps\windows\tests\IptvSuite.IntegrationTests\IptvSuite.IntegrationTests.csproj"
}

function Invoke-CheckedDotNet {
    param(
        [Parameter(Mandatory)]
        [string[]]$ArgumentList,

        [Parameter(Mandatory)]
        [string]$FailureMessage
    )

    & $DotNetPath @ArgumentList | Out-Host
    $exitCode = $LASTEXITCODE
    if ($exitCode -ne 0) {
        throw "$FailureMessage Exit code: $exitCode."
    }
}

function Assert-SafeQualityRoot {
    $expectedArtifactsRoot = [IO.Path]::GetFullPath((Join-Path $repositoryRoot ".artifacts"))
    $resolvedQualityRoot = [IO.Path]::GetFullPath($qualityRoot)
    $expectedQualityRoot = [IO.Path]::GetFullPath((Join-Path $expectedArtifactsRoot "quality-gates"))

    if ($resolvedQualityRoot -ne $expectedQualityRoot -or
        [IO.Directory]::GetParent($resolvedQualityRoot).FullName -ne $expectedArtifactsRoot) {
        throw "Refusing to clean an unexpected quality-gate directory."
    }

    if (Test-Path -LiteralPath $expectedArtifactsRoot) {
        $artifactsRootAttributes = (Get-Item -LiteralPath $expectedArtifactsRoot -Force).Attributes
        if ($artifactsRootAttributes -band [IO.FileAttributes]::ReparsePoint) {
            throw "Refusing to use a reparse-point artifact root."
        }
    }

    if (Test-Path -LiteralPath $resolvedQualityRoot) {
        $pending = [Collections.Generic.Queue[IO.DirectoryInfo]]::new()
        $qualityRootItem = Get-Item -LiteralPath $resolvedQualityRoot -Force
        if ($qualityRootItem.Attributes -band [IO.FileAttributes]::ReparsePoint) {
            throw "Refusing to clean a reparse-point quality root."
        }

        $pending.Enqueue($qualityRootItem)
        while ($pending.Count -gt 0) {
            $directory = $pending.Dequeue()
            foreach ($entry in $directory.GetFileSystemInfos()) {
                if ($entry.Attributes -band [IO.FileAttributes]::ReparsePoint) {
                    throw "Refusing to clean a quality tree that contains a reparse point."
                }

                if ($entry -is [IO.DirectoryInfo]) {
                    $pending.Enqueue($entry)
                }
            }
        }
    }
}

function Invoke-ScannerCliSelfTest {
    $probeRoot = Join-Path $qualityRoot "scanner-cli-probe"
    $expectedProbeRoot = [IO.Path]::GetFullPath((Join-Path $qualityRoot "scanner-cli-probe"))
    $resolvedProbeRoot = [IO.Path]::GetFullPath($probeRoot)
    if ($resolvedProbeRoot -ne $expectedProbeRoot -or
        [IO.Directory]::GetParent($resolvedProbeRoot).FullName -ne [IO.Path]::GetFullPath($qualityRoot)) {
        throw "Refusing to use an unexpected scanner-probe directory."
    }

    try {
        if (Test-Path -LiteralPath $probeRoot) {
            throw "The scanner-probe directory must not exist before the isolated probe."
        }

        New-Item -ItemType Directory -Path $probeRoot -Force | Out-Null
        $marker = @("IPTVSUITE", "TEST", "ONLY", "CANARY", "V1") -join "_"
        $probeValue = [string]::Join(
            "::",
            @($marker, "FOREIGN", "CLI_PROBE", "NOT_A_REAL_CREDENTIAL"))
        $probePath = Join-Path $probeRoot "contaminated.bin"
        [IO.File]::WriteAllBytes($probePath, [Text.Encoding]::BigEndianUnicode.GetBytes($probeValue))

        & $DotNetPath $testingToolPath scan-artifacts $probeRoot CI QUALITY_ARTIFACTS | Out-Host
        $contaminatedExitCode = $LASTEXITCODE
        if ($contaminatedExitCode -ne 2) {
            throw "The contaminated artifact scanner probe must return exact exit code 2."
        }

        [IO.File]::WriteAllBytes($probePath, [Text.Encoding]::UTF8.GetBytes("clean synthetic artifact"))
        & $DotNetPath $testingToolPath scan-artifacts $probeRoot CI QUALITY_ARTIFACTS | Out-Host
        $cleanExitCode = $LASTEXITCODE
        if ($cleanExitCode -ne 0) {
            throw "The cleaned artifact scanner probe must return exact exit code 0."
        }
    }
    finally {
        if (Test-Path -LiteralPath $probeRoot) {
            if ((Get-Item -LiteralPath $probeRoot -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) {
                throw "Refusing to clean a reparse-point scanner-probe directory."
            }

            $probeEntries = @(Get-ChildItem -LiteralPath $probeRoot -Force)
            if ($probeEntries.Count -ne 1 -or
                $probeEntries[0].PSIsContainer -or
                ($probeEntries[0].Attributes -band [IO.FileAttributes]::ReparsePoint) -or
                $probeEntries[0].Name -ne "contaminated.bin") {
                throw "Refusing to clean an unexpected scanner-probe entry."
            }

            Remove-Item -LiteralPath $probeEntries[0].FullName -Force
            Remove-Item -LiteralPath $probeRoot -Force
        }
    }
}

function Get-TestResultSet {
    param(
        [Parameter(Mandatory)]
        [string]$RunDirectory
    )

    $trxFiles = @(Get-ChildItem -LiteralPath $RunDirectory -Filter "*.trx" -File | Sort-Object -Property Name)
    if ($trxFiles.Count -ne $testProjects.Count) {
        throw "Expected $($testProjects.Count) TRX files in the quality run, found $($trxFiles.Count)."
    }

    $results = @()
    foreach ($trxFile in $trxFiles) {
        [xml]$trx = Get-Content -Raw -LiteralPath $trxFile.FullName
        $unitTestResults = @($trx.SelectNodes("//*[local-name()='UnitTestResult']"))
        foreach ($unitTestResult in $unitTestResults) {
            $testName = $unitTestResult.GetAttribute("testName")
            $outcome = $unitTestResult.GetAttribute("outcome")
            if ([string]::IsNullOrWhiteSpace($testName) -or $outcome -ne "Passed") {
                throw "A TRX file contains a missing test name or a non-passing result."
            }

            $results += "$testName|$outcome"
        }
    }

    if ($results.Count -eq 0) {
        throw "The quality run did not produce any test results."
    }

    return @($results | Sort-Object)
}

function Invoke-TestRun {
    param(
        [Parameter(Mandatory)]
        [ValidateSet(1, 2)]
        [int]$RunNumber
    )

    $runDirectory = Join-Path $testResultsRoot "run-$RunNumber"
    New-Item -ItemType Directory -Path $runDirectory -Force | Out-Null

    foreach ($testProject in $testProjects.GetEnumerator()) {
        $trxName = "$($testProject.Key).trx"
        Invoke-CheckedDotNet -FailureMessage "$($testProject.Key) tests failed in run $RunNumber." -ArgumentList @(
            "test",
            $testProject.Value,
            "-c", "Release",
            "--no-build",
            "--no-restore",
            "--logger", "trx;LogFileName=$trxName",
            "--results-directory", $runDirectory,
            "--blame-hang",
            "--blame-hang-timeout", "2m",
            "--blame-hang-dump-type", "none",
            "-maxcpucount:$maximumBuildNodes",
            "--nologo"
        )
    }

    return @(Get-TestResultSet -RunDirectory $runDirectory)
}

function Invoke-FixtureGeneration {
    param(
        [Parameter(Mandatory)]
        [ValidateSet(1, 2)]
        [int]$RunNumber
    )

    $outputDirectory = Join-Path $fixtureRoot "run-$RunNumber"
    New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null
    Invoke-CheckedDotNet -FailureMessage "Fixture generation failed in run $RunNumber." -ArgumentList @(
        $testingToolPath,
        "generate-fixtures",
        $fixtureSpecificationPath,
        $outputDirectory
    )

    return $outputDirectory
}

Assert-SafeQualityRoot
if (Test-Path -LiteralPath $qualityRoot) {
    Remove-Item -LiteralPath $qualityRoot -Recurse -Force
}

New-Item -ItemType Directory -Path $testResultsRoot -Force | Out-Null
New-Item -ItemType Directory -Path $fixtureRoot -Force | Out-Null
New-Item -ItemType Directory -Path $evidenceRoot -Force | Out-Null

$globalJson = Get-Content -Raw -LiteralPath $globalJsonPath | ConvertFrom-Json
$expectedSdk = $globalJson.sdk.version
if ($globalJson.sdk.rollForward -ne "disable") {
    throw "global.json must disable SDK roll-forward for the exact-SDK quality gate."
}

$actualSdk = (& $DotNetPath --version).Trim()
if ($LASTEXITCODE -ne 0 -or $actualSdk -ne $expectedSdk) {
    throw "Expected .NET SDK $expectedSdk, received '$actualSdk'."
}

Invoke-CheckedDotNet -FailureMessage "Locked restore failed." -ArgumentList @(
    "restore",
    $solutionPath,
    "--locked-mode",
    "--configfile", $nuGetConfigPath,
    "-p:Platform=x64",
    "--disable-parallel",
    "--nologo"
)

foreach ($configuration in @("Debug", "Release")) {
    Invoke-CheckedDotNet -FailureMessage "$configuration x64 build failed." -ArgumentList @(
        "build",
        $solutionPath,
        "-c", $configuration,
        "-p:Platform=x64",
        "--no-restore",
        "-maxcpucount:$maximumBuildNodes",
        "--nologo"
    )
}

$runOneResults = @(Invoke-TestRun -RunNumber 1)
$runTwoResults = @(Invoke-TestRun -RunNumber 2)
$resultDifference = @(Compare-Object -ReferenceObject $runOneResults -DifferenceObject $runTwoResults)
if ($resultDifference.Count -ne 0) {
    throw "The two clean test runs produced different test sets or outcomes."
}

$fixtureRunOne = Invoke-FixtureGeneration -RunNumber 1
$fixtureRunTwo = Invoke-FixtureGeneration -RunNumber 2
$fixtureLicenseEvidenceDirectory = Join-Path $fixtureRoot "LICENSES"
New-Item -ItemType Directory -Path $fixtureLicenseEvidenceDirectory -Force | Out-Null
Copy-Item -LiteralPath $fixtureLicenseSourcePath -Destination $fixtureLicenseEvidenceDirectory
$fixtureFiles = @("records.json", "fixture-manifest.json")
$fixtureHashes = [ordered]@{}
foreach ($fixtureFile in $fixtureFiles) {
    $runOnePath = Join-Path $fixtureRunOne $fixtureFile
    $runTwoPath = Join-Path $fixtureRunTwo $fixtureFile
    $runOneHash = (Get-FileHash -LiteralPath $runOnePath -Algorithm SHA256).Hash.ToLowerInvariant()
    $runTwoHash = (Get-FileHash -LiteralPath $runTwoPath -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($runOneHash -ne $runTwoHash) {
        throw "Fixture '$fixtureFile' was not byte-identical across clean runs."
    }

    $fixtureHashes[$fixtureFile] = $runOneHash
}

& $selfTestScript -DotNetPath $DotNetPath -Configuration Release
Invoke-ScannerCliSelfTest

Invoke-CheckedDotNet -FailureMessage "Artifact canary scan failed before summary generation." -ArgumentList @(
    $testingToolPath,
    "scan-artifacts",
    $qualityRoot,
    "CI",
    "QUALITY_ARTIFACTS"
)

$fixtureSpecification = Get-Content -Raw -LiteralPath $fixtureSpecificationPath | ConvertFrom-Json
$commitSha = $null
if ($env:GITHUB_SHA -match '\A[0-9a-fA-F]{40}\z') {
    $commitSha = $env:GITHUB_SHA.ToLowerInvariant()
}

$summary = [ordered]@{
    schemaVersion = 1
    milestone = "M4-foundation"
    commitSha = $commitSha
    sdkVersion = $actualSdk
    configuration = "Debug+Release"
    platform = "x64"
    cleanRunCount = 2
    testCountPerRun = $runOneResults.Count
    testResults = $runOneResults
    fixture = [ordered]@{
        generatorName = $fixtureSpecification.generatorName
        generatorVersion = $fixtureSpecification.generatorVersion
        algorithmVersion = $fixtureSpecification.algorithmVersion
        seed = $fixtureSpecification.seed
        provenance = "synthetic"
        recordsSha256 = $fixtureHashes["records.json"]
        manifestSha256 = $fixtureHashes["fixture-manifest.json"]
    }
    qualityGateSentinel = "armed-failed-and-disarmed-passed"
    scannerCliSelfTest = "contaminated-exit-2-and-clean-exit-0"
    artifactCanaryScan = "artifact-files-only-passed"
}

$summary | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $summaryPath -Encoding UTF8

Invoke-CheckedDotNet -FailureMessage "Final artifact canary scan failed." -ArgumentList @(
    $testingToolPath,
    "scan-artifacts",
    $qualityRoot,
    "CI",
    "QUALITY_ARTIFACTS"
)

Write-Host "M4 foundation Windows quality gates passed: $($runOneResults.Count) tests x 2 deterministic runs."
Write-Host "Evidence: $summaryPath"
