#requires -Version 5.1

[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$script:repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..\..\..'))
$script:helperPath = Join-Path $script:repositoryRoot 'eng\WindowsWack.ps1'
$script:testLeaf = 'IptvSuite-WindowsWack-' + [Guid]::NewGuid().ToString('N')
$script:testRoot = Join-Path ([System.IO.Path]::GetTempPath()) $script:testLeaf
$script:artifactRoot = Join-Path $script:testRoot 'artifacts'
$script:junctionPath = Join-Path $script:artifactRoot 'report-junction'
$script:utf8NoBom = New-Object System.Text.UTF8Encoding($false)
$script:rawMarker = 'SYNTHETIC-WACK-RAW-' + [Guid]::NewGuid().ToString('N')

function Assert-TestCondition {
    param(
        [Parameter(Mandatory)]
        [bool]$Condition,

        [Parameter(Mandatory)]
        [string]$Message
    )

    if (-not $Condition) {
        throw "Windows WACK self-test failed: $Message"
    }
}

function Assert-FailsWithCode {
    param(
        [Parameter(Mandatory)]
        [scriptblock]$Action,

        [Parameter(Mandatory)]
        [string]$Code
    )

    $message = $null
    try {
        & $Action | Out-Null
    }
    catch {
        $message = $_.Exception.Message
    }
    Assert-TestCondition `
        ($message -ceq "WindowsWack:$Code") `
        "Expected WindowsWack:$Code, received '$message'."
}

function Assert-ExactPropertyOrder {
    param(
        [Parameter(Mandatory)]
        [object]$Value,

        [Parameter(Mandatory)]
        [string[]]$Expected,

        [Parameter(Mandatory)]
        [string]$Name
    )

    $actual = @($Value.PSObject.Properties.Name)
    Assert-TestCondition `
        (($actual -join '|') -ceq ($Expected -join '|')) `
        "$Name property contract changed."
}

function Write-TestReport {
    param(
        [Parameter(Mandatory)]
        [string]$Name,

        [Parameter(Mandatory)]
        [string]$Text,

        [string]$Root = $script:artifactRoot
    )

    $path = Join-Path $Root ($Name + '.xml')
    [System.IO.File]::WriteAllText($path, $Text, $script:utf8NoBom)
    return $path
}

function Assert-ReportTextFails {
    param(
        [Parameter(Mandatory)]
        [string]$Name,

        [Parameter(Mandatory)]
        [string]$Text,

        [Parameter(Mandatory)]
        [string]$Code
    )

    $path = Write-TestReport -Name $Name -Text $Text
    Assert-FailsWithCode -Code $Code -Action {
        Assert-WindowsWackReport `
            -ReportPath $path `
            -ArtifactRoot $script:artifactRoot
    }
}

Assert-TestCondition `
    (Test-Path -LiteralPath $script:helperPath -PathType Leaf) `
    'Windows WACK helper is missing.'
. $script:helperPath

try {
    [System.IO.Directory]::CreateDirectory($script:artifactRoot) | Out-Null

    $helperTokens = $null
    $helperErrors = $null
    $helperAst = [System.Management.Automation.Language.Parser]::ParseFile(
        $script:helperPath,
        [ref]$helperTokens,
        [ref]$helperErrors)
    Assert-TestCondition ($helperErrors.Count -eq 0) 'Helper parser failed.'
    $helperText = [System.IO.File]::ReadAllText($script:helperPath)
    Assert-TestCondition `
        ($helperText.Contains('Windows Kits\10\App Certification Kit\appcert.exe') -and
            $helperText.Contains('$script:windowsWackMaximumReportBytes = 16MB') -and
            $helperText.Contains('[int]$timeoutValues[0] -le 60')) `
        'Exact WACK tool path or bounded limits changed.'
    $runnerFunctions = @($helperAst.FindAll({
                param($node)
                $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and
                $node.Name -ceq 'Invoke-WindowsWackDevelopmentIdentityPreflight'
            }, $true))
    Assert-TestCondition ($runnerFunctions.Count -eq 1) 'Bounded appcert runner is missing.'
    $runnerText = $runnerFunctions[0].Extent.Text
    Assert-TestCondition `
        ($runnerText.Contains("-Arguments 'reset'") -and
            $runnerText.Contains("'test -packagefullname '") -and
            $runnerText.Contains("' -reportoutputpath '") -and
            $runnerText.Contains("'finalizereport -reportfilepath '") -and
            $runnerText.Contains('$tool = Resolve-WindowsWackTool')) `
        'Official reset/test command order changed.'

    Assert-TestCondition `
        ((Resolve-WindowsWackTestExitDisposition -ExitCode 0) -ceq 'ReportComplete') `
        'Exit code zero was not classified as a complete report.'
    Assert-TestCondition `
        ((Resolve-WindowsWackTestExitDisposition -ExitCode 1) -ceq
            'ReportFinalizationRequired') `
        'Exit code one was not classified as report finalization.'
    $testExitFailures = [ordered]@{
        '-1' = 'TestInvalidCommandLine'
        '-2' = 'TestInfrastructureError'
        '-3' = 'TestUserInitiated'
        '-4' = 'TestInstallationError'
        '-5' = 'TestUnpackagingError'
        '2' = 'TestExitCodeUnknown'
    }
    foreach ($entry in $testExitFailures.GetEnumerator()) {
        Assert-FailsWithCode -Code $entry.Value -Action {
            Resolve-WindowsWackTestExitDisposition -ExitCode ([int]$entry.Key)
        }
    }
    Assert-WindowsWackCommandCompleted -Phase 'Reset' -ExitCode 0
    Assert-WindowsWackCommandCompleted -Phase 'Finalize' -ExitCode 0
    Assert-FailsWithCode -Code 'ResetInfrastructureError' -Action {
        Assert-WindowsWackCommandCompleted -Phase 'Reset' -ExitCode -2
    }
    Assert-FailsWithCode -Code 'FinalizeExitCodeUnknown' -Action {
        Assert-WindowsWackCommandCompleted -Phase 'Finalize' -ExitCode 1
    }

    $validText = @"
<?xml version="1.0" encoding="utf-8"?>
<?xml-stylesheet type="text/xsl" href="C:\Synthetic\ignored.xsl"?>
<REPORT OVERALL_RESULT="PASS" PARTIAL_RUN="FALSE" LATEST_VERSION="TRUE" PUBLISHER_DISPLAY_NAME="$script:rawMarker">
  <TEST NAME="Synthetic pass one"><RESULT>PASS</RESULT></TEST>
  <TEST NAME="Synthetic pass two"><RESULT><![CDATA[PASS]]></RESULT></TEST>
  <TEST NAME="Synthetic informational"><RESULT>NOT_APPLICABLE</RESULT></TEST>
  <DETAIL>$script:rawMarker</DETAIL>
</REPORT>
"@
    $validPath = Write-TestReport -Name 'valid' -Text $validText
    $validResult = Assert-WindowsWackReport `
        -ReportPath $validPath `
        -ArtifactRoot $script:artifactRoot
    Assert-ExactPropertyOrder `
        -Value $validResult `
        -Name 'Report result' `
        -Expected @(
            'ReportLength',
            'ReportSha256',
            'OverallResult',
            'PartialRun',
            'LatestVersion',
            'TestCount',
            'PassedTestCount',
            'FailedTestCount',
            'OtherTestCount')
    $validFile = Get-Item -LiteralPath $validPath -Force
    Assert-TestCondition ($validResult.ReportLength -eq $validFile.Length) 'Report length is not exact.'
    Assert-TestCondition `
        ($validResult.ReportSha256 -cmatch '\A[0-9a-f]{64}\z') `
        'Report hash is invalid.'
    Assert-TestCondition `
        ($validResult.OverallResult -ceq 'PASS' -and
            $validResult.PartialRun -ceq 'FALSE' -and
            $validResult.LatestVersion -ceq 'TRUE') `
        'Accepted root status was not normalized.'
    Assert-TestCondition `
        ($validResult.TestCount -eq 3 -and
            $validResult.PassedTestCount -eq 2 -and
            $validResult.FailedTestCount -eq 0 -and
            $validResult.OtherTestCount -eq 1) `
        'Robust test counters are invalid.'

    $packageSha256 = '0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef'
    $summary = New-WindowsWackDevelopmentIdentitySummary `
        -Tool ([pscustomobject]@{
                Version = '10.0.26100.1'
                Length = 123456L
                Sha256 = ('1' * 64)
            }) `
        -Report $validResult `
        -PackageSha256 $packageSha256 `
        -ResetResult ([pscustomobject]@{ ExitCode = 0; TimedOut = $false }) `
        -TestResult ([pscustomobject]@{ ExitCode = 0; TimedOut = $false })
    Assert-ExactPropertyOrder `
        -Value $summary `
        -Name 'Development identity summary' `
        -Expected @(
            'SchemaVersion',
            'Scope',
            'ClosedBlocker',
            'ReleaseReady',
            'PackageSha256',
            'ToolVersion',
            'ToolLength',
            'ToolSha256',
            'ReportLength',
            'ReportSha256',
            'OverallResult',
            'PartialRun',
            'LatestVersion',
            'ResetExitCode',
            'ResetTimedOut',
            'TestExitCode',
            'TestTimedOut',
            'TestCount',
            'PassedTestCount',
            'FailedTestCount',
            'OtherTestCount')
    Assert-TestCondition `
        ($summary.SchemaVersion -eq 1 -and
            $summary.Scope -ceq 'DevelopmentIdentityWackPreflightOnly' -and
            $summary.ClosedBlocker -ceq 'None' -and
            $summary.ReleaseReady -eq $false) `
        'Preflight-only disposition changed.'
    Assert-TestCondition `
        ($summary.PackageSha256 -ceq $packageSha256 -and
            $summary.ResetExitCode -eq 0 -and
            $summary.ResetTimedOut -eq $false -and
            $summary.TestExitCode -eq 0 -and
            $summary.TestTimedOut -eq $false) `
        'Exact package hash or process result binding changed.'
    $finalizedSummary = New-WindowsWackDevelopmentIdentitySummary `
        -Tool ([pscustomobject]@{
                Version = '10.0.26100.1'
                Length = 123456L
                Sha256 = ('1' * 64)
            }) `
        -Report $validResult `
        -PackageSha256 $packageSha256 `
        -ResetResult ([pscustomobject]@{ ExitCode = 0; TimedOut = $false }) `
        -TestResult ([pscustomobject]@{ ExitCode = 1; TimedOut = $false }) `
        -FinalizeResult ([pscustomobject]@{ ExitCode = 0; TimedOut = $false })
    Assert-TestCondition `
        ($finalizedSummary.SchemaVersion -eq 1 -and
            $finalizedSummary.TestExitCode -eq 1 -and
            $finalizedSummary.TestTimedOut -eq $false) `
        'Finalized report process binding is invalid.'
    Assert-FailsWithCode -Code 'FinalizeFailed' -Action {
        New-WindowsWackDevelopmentIdentitySummary `
            -Tool ([pscustomobject]@{
                    Version = '10.0.26100.1'
                    Length = 123456L
                    Sha256 = ('1' * 64)
                }) `
            -Report $validResult `
            -PackageSha256 $packageSha256 `
            -ResetResult ([pscustomobject]@{ ExitCode = 0; TimedOut = $false }) `
            -TestResult ([pscustomobject]@{ ExitCode = 1; TimedOut = $false })
    }
    Assert-FailsWithCode -Code 'FinalizeUnexpected' -Action {
        New-WindowsWackDevelopmentIdentitySummary `
            -Tool ([pscustomobject]@{
                    Version = '10.0.26100.1'
                    Length = 123456L
                    Sha256 = ('1' * 64)
                }) `
            -Report $validResult `
            -PackageSha256 $packageSha256 `
            -ResetResult ([pscustomobject]@{ ExitCode = 0; TimedOut = $false }) `
            -TestResult ([pscustomobject]@{ ExitCode = 0; TimedOut = $false }) `
            -FinalizeResult ([pscustomobject]@{ ExitCode = 0; TimedOut = $false })
    }
    $summaryJson = $summary | ConvertTo-Json -Depth 4 -Compress
    Assert-TestCondition `
        (-not $summaryJson.Contains($script:rawMarker) -and
            -not $summaryJson.Contains($script:testRoot) -and
            -not $summaryJson.Contains('Synthetic pass one')) `
        'Raw report content or a local path escaped into the summary.'

    $withoutLatestPath = Write-TestReport -Name 'latest-absent' -Text @'
<?xml version="1.0" encoding="utf-8"?>
<REPORT OVERALL_RESULT="PASS" PARTIAL_RUN="FALSE">
  <TEST NAME="Synthetic"><RESULT>PASS</RESULT></TEST>
</REPORT>
'@
    $withoutLatest = Assert-WindowsWackReport `
        -ReportPath $withoutLatestPath `
        -ArtifactRoot $script:artifactRoot
    Assert-TestCondition ($null -eq $withoutLatest.LatestVersion) 'Absent LATEST_VERSION was not accepted.'

    Assert-ReportTextFails `
        -Name 'malformed' `
        -Code 'ReportXmlInvalid' `
        -Text '<REPORT OVERALL_RESULT="PASS" PARTIAL_RUN="FALSE"><TEST></REPORT>'
    Assert-ReportTextFails `
        -Name 'dtd' `
        -Code 'ReportXmlInvalid' `
        -Text '<!DOCTYPE REPORT [<!ENTITY status "PASS">]><REPORT OVERALL_RESULT="&status;" PARTIAL_RUN="FALSE" />'
    Assert-ReportTextFails `
        -Name 'wrong-root' `
        -Code 'ReportRootInvalid' `
        -Text '<WRONG OVERALL_RESULT="PASS" PARTIAL_RUN="FALSE" />'
    Assert-ReportTextFails `
        -Name 'namespaced-root' `
        -Code 'ReportRootInvalid' `
        -Text '<REPORT xmlns="urn:synthetic" OVERALL_RESULT="PASS" PARTIAL_RUN="FALSE" />'
    Assert-ReportTextFails `
        -Name 'overall-missing' `
        -Code 'OverallResultMissing' `
        -Text '<REPORT PARTIAL_RUN="FALSE" />'
    Assert-ReportTextFails `
        -Name 'overall-fail' `
        -Code 'OverallResultFailed' `
        -Text '<REPORT OVERALL_RESULT="FAIL" PARTIAL_RUN="FALSE" />'
    Assert-ReportTextFails `
        -Name 'overall-warning' `
        -Code 'OverallResultWarning' `
        -Text '<REPORT OVERALL_RESULT="WARNING" PARTIAL_RUN="FALSE" />'
    Assert-ReportTextFails `
        -Name 'overall-unknown' `
        -Code 'OverallResultUnknown' `
        -Text '<REPORT OVERALL_RESULT="UNKNOWN" PARTIAL_RUN="FALSE" />'
    Assert-ReportTextFails `
        -Name 'partial-missing' `
        -Code 'PartialRunMissing' `
        -Text '<REPORT OVERALL_RESULT="PASS" />'
    Assert-ReportTextFails `
        -Name 'partial-true' `
        -Code 'PartialRunDetected' `
        -Text '<REPORT OVERALL_RESULT="PASS" PARTIAL_RUN="TRUE" />'
    Assert-ReportTextFails `
        -Name 'partial-unknown' `
        -Code 'PartialRunUnknown' `
        -Text '<REPORT OVERALL_RESULT="PASS" PARTIAL_RUN="UNKNOWN" />'
    Assert-ReportTextFails `
        -Name 'latest-false' `
        -Code 'LatestVersionFalse' `
        -Text '<REPORT OVERALL_RESULT="PASS" PARTIAL_RUN="FALSE" LATEST_VERSION="FALSE" />'
    Assert-ReportTextFails `
        -Name 'latest-unknown' `
        -Code 'LatestVersionUnknown' `
        -Text '<REPORT OVERALL_RESULT="PASS" PARTIAL_RUN="FALSE" LATEST_VERSION="UNKNOWN" />'

    $deepText = '<REPORT OVERALL_RESULT="PASS" PARTIAL_RUN="FALSE">' +
        ('<N>' * 129) + ('</N>' * 129) + '</REPORT>'
    Assert-ReportTextFails `
        -Name 'depth-bound' `
        -Code 'ReportXmlBoundsExceeded' `
        -Text $deepText

    $emptyPath = Join-Path $script:artifactRoot 'empty.xml'
    [System.IO.File]::WriteAllBytes($emptyPath, (New-Object byte[] 0))
    Assert-FailsWithCode -Code 'ReportEmpty' -Action {
        Assert-WindowsWackReport `
            -ReportPath $emptyPath `
            -ArtifactRoot $script:artifactRoot
    }

    $oversizePath = Join-Path $script:artifactRoot 'oversize.xml'
    $oversizeStream = [System.IO.File]::Open(
        $oversizePath,
        [System.IO.FileMode]::CreateNew,
        [System.IO.FileAccess]::Write,
        [System.IO.FileShare]::None)
    try {
        $oversizeStream.SetLength(16MB + 1)
    }
    finally {
        $oversizeStream.Dispose()
    }
    Assert-FailsWithCode -Code 'ReportTooLarge' -Action {
        Assert-WindowsWackReport `
            -ReportPath $oversizePath `
            -ArtifactRoot $script:artifactRoot
    }

    $outsidePath = Write-TestReport `
        -Name 'outside' `
        -Root $script:testRoot `
        -Text '<REPORT OVERALL_RESULT="PASS" PARTIAL_RUN="FALSE" />'
    Assert-FailsWithCode -Code 'ReportPathInvalid' -Action {
        Assert-WindowsWackReport `
            -ReportPath $outsidePath `
            -ArtifactRoot $script:artifactRoot
    }
    Assert-FailsWithCode -Code 'ReportPathInvalid' -Action {
        Assert-WindowsWackReport `
            -ReportPath $script:artifactRoot `
            -ArtifactRoot $script:artifactRoot
    }

    $junctionTarget = Join-Path $script:testRoot 'junction-target'
    [System.IO.Directory]::CreateDirectory($junctionTarget) | Out-Null
    Write-TestReport `
        -Name 'through-junction' `
        -Root $junctionTarget `
        -Text '<REPORT OVERALL_RESULT="PASS" PARTIAL_RUN="FALSE" />' | Out-Null
    New-Item `
        -ItemType Junction `
        -Path $script:junctionPath `
        -Target $junctionTarget `
        -ErrorAction Stop | Out-Null
    $junction = Get-Item -LiteralPath $script:junctionPath -Force
    Assert-TestCondition `
        (($junction.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) `
        'Synthetic junction is not a reparse point.'
    Assert-FailsWithCode -Code 'ReportPathInvalid' -Action {
        Assert-WindowsWackReport `
            -ReportPath (Join-Path $script:junctionPath 'through-junction.xml') `
            -ArtifactRoot $script:artifactRoot
    }
    Assert-FailsWithCode -Code 'ArtifactRootInvalid' -Action {
        Assert-WindowsWackReport `
            -ReportPath (Join-Path $script:junctionPath 'through-junction.xml') `
            -ArtifactRoot $script:junctionPath
    }
    [System.IO.Directory]::Delete($script:junctionPath)

    Assert-FailsWithCode -Code 'PackageSha256Invalid' -Action {
        New-WindowsWackDevelopmentIdentitySummary `
            -Tool ([pscustomobject]@{
                    Version = '10.0.26100.1'
                    Length = 123456L
                    Sha256 = ('1' * 64)
                }) `
            -Report $validResult `
            -PackageSha256 ('A' * 64) `
            -ResetResult ([pscustomobject]@{ ExitCode = 0; TimedOut = $false }) `
            -TestResult ([pscustomobject]@{ ExitCode = 0; TimedOut = $false })
    }

    Write-Output 'Windows WACK self-test passed.'
}
finally {
    if ([System.IO.Directory]::Exists($script:junctionPath)) {
        $junction = Get-Item -LiteralPath $script:junctionPath -Force
        if (($junction.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -eq 0) {
            throw 'Windows WACK self-test refused unsafe junction cleanup.'
        }
        [System.IO.Directory]::Delete($script:junctionPath)
    }

    if (Test-Path -LiteralPath $script:testRoot) {
        $resolvedTestRoot = [System.IO.Path]::GetFullPath($script:testRoot)
        $expectedParent = [System.IO.Path]::GetFullPath([System.IO.Path]::GetTempPath()).TrimEnd(
            [System.IO.Path]::DirectorySeparatorChar,
            [System.IO.Path]::AltDirectorySeparatorChar)
        $actualParent = [System.IO.Path]::GetDirectoryName($resolvedTestRoot)
        if (-not [string]::Equals(
                $actualParent,
                $expectedParent,
                [System.StringComparison]::OrdinalIgnoreCase) -or
            [System.IO.Path]::GetFileName($resolvedTestRoot) -cne $script:testLeaf -or
            ([System.IO.File]::GetAttributes($resolvedTestRoot) -band
                [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw 'Windows WACK self-test refused unsafe cleanup.'
        }
        Remove-Item -LiteralPath $resolvedTestRoot -Recurse -Force
    }
}
