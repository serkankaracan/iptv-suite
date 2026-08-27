[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
. (Join-Path $PSScriptRoot 'WindowsCatalogBenchmarkRegression.ps1')

function Assert-True {
    param([Parameter(Mandatory = $true)][bool]$Condition, [Parameter(Mandatory = $true)][string]$Message)
    if (-not $Condition) {
        throw $Message
    }
}

function Assert-Throws {
    param([Parameter(Mandatory = $true)][scriptblock]$Action, [Parameter(Mandatory = $true)][string]$Message)
    try {
        & $Action
    }
    catch {
        return
    }

    throw $Message
}

function New-ReferenceEvidence {
    param([double]$MetricValue = 100.0)

    return [pscustomobject][ordered]@{
        schemaVersion = 2
        milestone = 'M14'
        evidenceKind = 'catalog-performance-benchmark'
        configuration = 'Release'
        platform = 'x64'
        commitSha = ('1' * 40)
        sdkVersion = '10.0.302'
        runtime = '.NET 10.0.10'
        operatingSystemBuild = '10.0.26200.0'
        operatingSystemArchitecture = 'X64'
        processArchitecture = 'X64'
        processor = [pscustomobject][ordered]@{ verification = 'Observed'; value = 'Synthetic CPU' }
        logicalProcessorCount = 8
        iterations = 20
        authoritativeWarmIterations = 20
        minimumAuthoritativeWarmIterations = 20
        coldObservationsPerStage = 1
        result = 'passed'
        measurementIntegrityVerified = $true
        authoritativeWarmSampleCountVerified = $true
        conditionDeclarationsComplete = $true
        referenceModeRequested = $true
        runnerProfile = [pscustomobject][ordered]@{ verification = 'Declared'; value = 'm14-reference-a' }
        referenceEligible = $true
        referenceEligibilityRequirements = [pscustomobject][ordered]@{
            exactConditionDeclarations = $true
            declaredRunnerProfile = $true
            measurementIntegrity = $true
            passingBenchmarkResult = $true
        }
        conditions = [pscustomobject][ordered]@{
            cache = [pscustomobject][ordered]@{ verification = 'Declared'; value = 'Warm' }
            power = [pscustomobject][ordered]@{ verification = 'Declared'; value = 'AcStable' }
            thermal = [pscustomobject][ordered]@{ verification = 'Declared'; value = 'Nominal' }
            background = [pscustomobject][ordered]@{ verification = 'Declared'; value = 'Controlled' }
        }
        corpusManifest = [pscustomobject][ordered]@{
            retained = $false; byteLength = 123; sha256 = ('a' * 64); generator = 'Synthetic'; generatorVersion = 1
        }
        corpusSpecification = [pscustomobject][ordered]@{
            repositoryRelativePath = 'apps/windows/testdata/m14/catalog-corpus-spec.json'
            byteLength = 456
            sha256 = ('b' * 64)
        }
        syntheticLicense = [pscustomobject][ordered]@{
            expression = 'LicenseRef-IPTVSuite-Synthetic-Test-Only'
            status = 'UNVERIFIED'
            repositoryRelativePath = 'apps/windows/testdata/LICENSE.md'
            byteLength = 789
            sha256 = ('c' * 64)
        }
        corpora = @([pscustomobject][ordered]@{
            id = 'm14-50000'; sha256 = ('d' * 64); byteLength = 1000; channelCount = 50000
            categoryCount = 500; logoReferenceCount = 50000; expectedOutcome = 'Success'
        })
        stageScope = [pscustomobject][ordered]@{
            parserDiagnostic = 'parser'; combinedImport = 'combined'; coldObservation = 'cold'
            authoritativeTiming = 'warm'; resourcePass = 'resource'
        }
        query50k = [pscustomobject][ordered]@{
            recordCount = 50000
            iterations = 20
            catalogSchemaVersion = 5
        }
        cancellation = [pscustomobject][ordered]@{
            recordCount = 50000
            iterations = 20
            expectedErrorCode = 'OperationCancelled'
            measurementBoundary = 'CancellationRequestToLoaderCompletion'
        }
        entryLimitProbe = [pscustomobject][ordered]@{
            recordCount = 100000
            expectedOutcome = 'EntryLimitFailClosed'
            parserErrorCode = 'UnsupportedPlaylistFormat'
            combinedImportErrorCode = 'UnsupportedPlaylistFormat'
            persistedRowsAfterFailure = 0
        }
        plaintextLocatorCanaryScan = 'passed'
        budgets = [pscustomobject][ordered]@{
            parserP95Milliseconds = 2000
            normalizeProtectPersistIndexP95Milliseconds = 3000
            combinedImportP95Milliseconds = 5000
            importAllocationMaximumBytes = 157286400
            peakWorkingSetDeltaBytes = 262144000
            cancellationP95Milliseconds = 250
            queryP95Milliseconds = 100
            reopenP95Milliseconds = 500
        }
        budgetEvaluation = [pscustomobject][ordered]@{
            parserP95Milliseconds = $MetricValue
            normalizeProtectPersistIndexConservativeUpperBoundP95Milliseconds = $MetricValue
            normalizeProtectPersistIndexPassed = $true
            combinedImportP95Milliseconds = $MetricValue
            importAllocationMaximumBytes = 1000
            peakWorkingSetDeltaBytes = 1000
            peakWorkingSetSamplingComplete = $true
            cancellationP95Milliseconds = $MetricValue
            firstPageP95Milliseconds = $MetricValue
            categoryPageP95Milliseconds = $MetricValue
            searchP95Milliseconds = $MetricValue
            reopenP95Milliseconds = $MetricValue
            allPassed = $true
        }
    }
}

function New-EvidenceRecord {
    param([Parameter(Mandatory = $true)][object]$Evidence, [string]$Sha = ('e' * 64))
    return [pscustomobject]@{ FullPath = 'not-retained'; ByteLength = 512; Sha256 = $Sha; Evidence = $Evidence }
}

$temporaryBase = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd(
    [IO.Path]::DirectorySeparatorChar,
    [IO.Path]::AltDirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
$testRoot = Join-Path $temporaryBase ('iptvsuite-m14-regression-' + [Guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($testRoot) | Out-Null
try {
    $baseline = New-EvidenceRecord (New-ReferenceEvidence)
    $candidateAtBoundary = New-EvidenceRecord (New-ReferenceEvidence -MetricValue 110.0) ('f' * 64)
    $pass = Compare-M14CatalogRegression $baseline $candidateAtBoundary $true $true
    Assert-True $pass.allPassed 'Exact +10 percent regression boundary must pass.'
    Assert-True ($pass.metrics.Count -eq 8) 'The regression evidence must contain exactly eight metrics.'

    $candidateOver = New-EvidenceRecord (New-ReferenceEvidence -MetricValue 110.01) ('0' * 64)
    $candidateOver.Evidence.commitSha = '2' * 40
    $failed = Compare-M14CatalogRegression $baseline $candidateOver $true $true
    Assert-True (-not $failed.allPassed) 'A regression above +10 percent must fail.'
    Assert-True ($failed.result -ceq 'failed') 'A failed regression must have a stable failed result.'

    $environmentMismatch = New-EvidenceRecord (New-ReferenceEvidence)
    $environmentMismatch.Evidence.sdkVersion = '10.0.303'
    $incompatible = Compare-M14CatalogRegression $baseline $environmentMismatch $true $true
    Assert-True (-not $incompatible.binding.exactEnvironmentMatch) 'Environment mismatch must fail closed.'

    $workloadMismatch = New-EvidenceRecord (New-ReferenceEvidence)
    $workloadMismatch.Evidence.query50k.catalogSchemaVersion = 6
    $incompatible = Compare-M14CatalogRegression $baseline $workloadMismatch $true $true
    Assert-True (-not $incompatible.binding.exactWorkloadMatch) 'Query workload mismatch must fail closed.'

    $cancellationMismatch = New-EvidenceRecord (New-ReferenceEvidence)
    $cancellationMismatch.Evidence.cancellation.expectedErrorCode = 'Unexpected'
    $incompatible = Compare-M14CatalogRegression $baseline $cancellationMismatch $true $true
    Assert-True (-not $incompatible.binding.exactWorkloadMatch) 'Cancellation workload mismatch must fail closed.'

    $missingCancellationBoundary = New-EvidenceRecord (New-ReferenceEvidence)
    $missingCancellationBoundary.Evidence.cancellation.PSObject.Properties.Remove('measurementBoundary')
    Assert-Throws {
        Assert-M14CatalogBenchmarkReferenceEvidence $missingCancellationBoundary 'm14-reference-a'
    } 'A missing cancellation measurement boundary must fail closed.'

    $wrongCancellationBoundary = New-EvidenceRecord (New-ReferenceEvidence)
    $wrongCancellationBoundary.Evidence.cancellation.measurementBoundary =
        'LoaderStartToLoaderCompletion'
    Assert-Throws {
        Assert-M14CatalogBenchmarkReferenceEvidence $wrongCancellationBoundary 'm14-reference-a'
    } 'An unexpected cancellation measurement boundary must fail closed.'

    $entryLimitMismatch = New-EvidenceRecord (New-ReferenceEvidence)
    $entryLimitMismatch.Evidence.entryLimitProbe.persistedRowsAfterFailure = 1
    $incompatible = Compare-M14CatalogRegression $baseline $entryLimitMismatch $true $true
    Assert-True (-not $incompatible.binding.exactWorkloadMatch) 'Entry-limit workload mismatch must fail closed.'

    $budgetMismatch = New-EvidenceRecord (New-ReferenceEvidence)
    $budgetMismatch.Evidence.budgets.queryP95Milliseconds = 101
    $incompatible = Compare-M14CatalogRegression $baseline $budgetMismatch $true $true
    Assert-True (-not $incompatible.binding.exactBudgetContractMatch) 'Budget mismatch must fail closed.'

    $invalidRunner = New-EvidenceRecord (New-ReferenceEvidence)
    $invalidRunner.Evidence.runnerProfile.value = 'Host Name'
    Assert-Throws { Assert-M14CatalogBenchmarkReferenceEvidence $invalidRunner 'm14-reference-a' } `
        'Unsafe runner-profile metadata must be rejected.'

    $ineligible = New-EvidenceRecord (New-ReferenceEvidence)
    $ineligible.Evidence.referenceEligible = $false
    Assert-Throws { Assert-M14CatalogBenchmarkReferenceEvidence $ineligible 'm14-reference-a' } `
        'Ineligible baseline evidence must be rejected.'

    $legacySchema = New-EvidenceRecord (New-ReferenceEvidence)
    $legacySchema.Evidence.schemaVersion = 1
    Assert-Throws { Assert-M14CatalogBenchmarkReferenceEvidence $legacySchema 'm14-reference-a' } `
        'Legacy benchmark schema v1 must fail closed.'

    $spoofedBoolean = New-EvidenceRecord (New-ReferenceEvidence)
    $spoofedBoolean.Evidence.referenceEligible = 'false'
    Assert-Throws { Assert-M14CatalogBenchmarkReferenceEvidence $spoofedBoolean 'm14-reference-a' } `
        'A non-empty string must not spoof a Boolean evidence value.'

    $missingMetric = New-EvidenceRecord (New-ReferenceEvidence)
    $missingMetric.Evidence.budgetEvaluation.PSObject.Properties.Remove('searchP95Milliseconds')
    Assert-Throws { Compare-M14CatalogRegression $baseline $missingMetric $true $true } `
        'A missing p95 metric must be rejected.'

    $ancestorFalse = Compare-M14CatalogRegression $baseline $candidateAtBoundary $false $true
    Assert-True (-not $ancestorFalse.allPassed) 'Ancestor=false must fail closed.'
    $contentStableFalse = Compare-M14CatalogRegression $baseline $candidateAtBoundary $true $false
    Assert-True (-not $contentStableFalse.allPassed) 'ContentStable=false must fail closed.'

    $baselinePath = Join-Path $testRoot 'baseline.json'
    [IO.File]::WriteAllText(
        $baselinePath,
        ((New-ReferenceEvidence) | ConvertTo-Json -Depth 20),
        [Text.UTF8Encoding]::new($false))
    $firstImport = Import-M14CatalogBenchmarkEvidence $baselinePath
    Assert-M14CatalogBenchmarkReferenceEvidence $firstImport 'm14-reference-a'
    $changedEvidence = New-ReferenceEvidence
    $changedEvidence.commitSha = '3' * 40
    [IO.File]::WriteAllText(
        $baselinePath,
        ($changedEvidence | ConvertTo-Json -Depth 20),
        [Text.UTF8Encoding]::new($false))
    $secondImport = Import-M14CatalogBenchmarkEvidence $baselinePath
    Assert-True ($firstImport.Sha256 -cne $secondImport.Sha256) `
        'Baseline content mutation must change its immutable digest binding.'

    $oversizedPath = Join-Path $testRoot 'oversized.json'
    $oversized = [byte[]]::new((1MB) + 1)
    try {
        [IO.File]::WriteAllBytes($oversizedPath, $oversized)
    }
    finally {
        [Array]::Clear($oversized, 0, $oversized.Length)
    }
    Assert-Throws { Import-M14CatalogBenchmarkEvidence $oversizedPath } `
        'Evidence larger than 1 MiB must be rejected.'

    $summaryPath = Join-Path $testRoot 'regression-summary.json'
    Write-M14CatalogRegressionSummaryAtomically $failed $summaryPath
    Assert-True ([IO.File]::Exists($summaryPath)) 'Failed regression evidence must be retained.'
    Assert-True (-not [IO.File]::Exists($summaryPath + '.tmp')) 'Temporary evidence must not remain.'
    $summaryText = [IO.File]::ReadAllText($summaryPath)
    $summary = $summaryText | ConvertFrom-Json
    Assert-True ($summary.result -ceq 'failed') 'Retained regression evidence result must remain failed.'
    Assert-True ($summaryText.IndexOf($testRoot, [StringComparison]::OrdinalIgnoreCase) -lt 0) `
        'Regression evidence must not retain a raw path.'
    Assert-True ($summaryText.IndexOf('Synthetic CPU', [StringComparison]::Ordinal) -lt 0) `
        'Regression evidence must not retain raw processor metadata.'

    Write-Host 'M14 catalog benchmark regression self-test passed.'
}
finally {
    $resolvedTestRoot = [IO.Path]::GetFullPath($testRoot)
    if (-not $resolvedTestRoot.StartsWith($temporaryBase, [StringComparison]::OrdinalIgnoreCase) -or
        $resolvedTestRoot -eq $temporaryBase) {
        throw 'M14 regression self-test cleanup root escaped the temporary directory.'
    }

    if ([IO.Directory]::Exists($resolvedTestRoot)) {
        [IO.Directory]::Delete($resolvedTestRoot, $true)
    }
}
