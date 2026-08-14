[CmdletBinding()]
param(
    [ValidateSet("Smoke", "Decision")]
    [string]$Mode = "Smoke",

    [switch]$AllowDecision,

    [string]$DotNetPath = "dotnet"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$globalJsonPath = Join-Path $repositoryRoot "global.json"
$nuGetConfigPath = Join-Path $repositoryRoot "NuGet.config"
$projectPath = Join-Path $repositoryRoot "apps\windows\tests\IptvSuite.ProtectedCatalogSpike\IptvSuite.ProtectedCatalogSpike.csproj"
$assemblyPath = Join-Path $repositoryRoot "apps\windows\tests\IptvSuite.ProtectedCatalogSpike\bin\x64\Release\net10.0\IptvSuite.ProtectedCatalogSpike.dll"
$maximumBuildNodes = 1

if ($Mode -eq "Decision" -and -not $AllowDecision) {
    throw "Decision mode requires the explicit -AllowDecision switch."
}

$globalJson = Get-Content -Raw -LiteralPath $globalJsonPath | ConvertFrom-Json
$expectedSdk = $globalJson.sdk.version
if ($globalJson.sdk.rollForward -ne "disable" -or $globalJson.sdk.allowPrerelease -ne $false) {
    throw "The exact stable SDK contract is not configured."
}

$actualSdkOutput = @(& $DotNetPath --version 2>$null)
if ($LASTEXITCODE -ne 0 -or $actualSdkOutput.Count -ne 1) {
    throw "The .NET SDK version could not be validated."
}

$actualSdk = $actualSdkOutput[0].Trim()
if ($actualSdk -ne $expectedSdk) {
    throw "The exact repository SDK is required."
}

function Invoke-CheckedDotNet {
    param(
        [Parameter(Mandatory)]
        [string[]]$ArgumentList,

        [Parameter(Mandatory)]
        [string]$FailureCode
    )

    & $DotNetPath @ArgumentList | Out-Host
    if ($LASTEXITCODE -ne 0) {
        throw "Protected-catalog spike failed; failure=$FailureCode."
    }
}

$previousCliUseMsbuildServer = $env:DOTNET_CLI_USE_MSBUILD_SERVER
$previousDisableNodeReuse = $env:MSBUILDDISABLENODEREUSE
$previousValidatedSdk = $env:IPTVSUITE_PROTECTED_CATALOG_SPIKE_VALIDATED_SDK
$previousRunnerAssemblySha256 = $env:IPTVSUITE_PROTECTED_CATALOG_SPIKE_RUNNER_ASSEMBLY_SHA256
$locationPushed = $false

try {
    $env:DOTNET_CLI_USE_MSBUILD_SERVER = "0"
    $env:MSBUILDDISABLENODEREUSE = "1"
    $env:IPTVSUITE_PROTECTED_CATALOG_SPIKE_VALIDATED_SDK = $actualSdk
    Push-Location -LiteralPath $repositoryRoot
    $locationPushed = $true

    Invoke-CheckedDotNet -FailureCode "restore-failed" -ArgumentList @(
        "restore",
        $projectPath,
        "--locked-mode",
        "--configfile", $nuGetConfigPath,
        "-p:Platform=x64",
        "--disable-parallel",
        "--nologo"
    )

    Invoke-CheckedDotNet -FailureCode "build-failed" -ArgumentList @(
        "build",
        $projectPath,
        "-c", "Release",
        "-p:Platform=x64",
        "--no-restore",
        "--no-incremental",
        "-maxcpucount:$maximumBuildNodes",
        "-p:UseSharedCompilation=false",
        "--nologo"
    )

    if (-not (Test-Path -LiteralPath $assemblyPath -PathType Leaf)) {
        throw "Protected-catalog spike failed; failure=runner-assembly-missing."
    }

    $runnerAssemblySha256 = (Get-FileHash -LiteralPath $assemblyPath -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($runnerAssemblySha256 -notmatch '^[0-9a-f]{64}$') {
        throw "Protected-catalog spike failed; failure=runner-assembly-hash-invalid."
    }

    $env:IPTVSUITE_PROTECTED_CATALOG_SPIKE_RUNNER_ASSEMBLY_SHA256 = $runnerAssemblySha256

    $runArguments = @($assemblyPath, "--mode", $Mode)
    if ($Mode -eq "Decision") {
        $runArguments += "--acknowledge-long-running-decision"
    }

    Invoke-CheckedDotNet -FailureCode "execution-failed" -ArgumentList $runArguments
}
finally {
    if ($locationPushed) {
        Pop-Location
    }

    if ($null -eq $previousCliUseMsbuildServer) {
        Remove-Item Env:DOTNET_CLI_USE_MSBUILD_SERVER -ErrorAction SilentlyContinue
    }
    else {
        $env:DOTNET_CLI_USE_MSBUILD_SERVER = $previousCliUseMsbuildServer
    }

    if ($null -eq $previousDisableNodeReuse) {
        Remove-Item Env:MSBUILDDISABLENODEREUSE -ErrorAction SilentlyContinue
    }
    else {
        $env:MSBUILDDISABLENODEREUSE = $previousDisableNodeReuse
    }

    if ($null -eq $previousValidatedSdk) {
        Remove-Item Env:IPTVSUITE_PROTECTED_CATALOG_SPIKE_VALIDATED_SDK -ErrorAction SilentlyContinue
    }
    else {
        $env:IPTVSUITE_PROTECTED_CATALOG_SPIKE_VALIDATED_SDK = $previousValidatedSdk
    }

    if ($null -eq $previousRunnerAssemblySha256) {
        Remove-Item Env:IPTVSUITE_PROTECTED_CATALOG_SPIKE_RUNNER_ASSEMBLY_SHA256 -ErrorAction SilentlyContinue
    }
    else {
        $env:IPTVSUITE_PROTECTED_CATALOG_SPIKE_RUNNER_ASSEMBLY_SHA256 = $previousRunnerAssemblySha256
    }
}
