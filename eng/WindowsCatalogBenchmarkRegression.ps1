$script:M14CatalogEvidenceMaximumBytes = 1MB
$script:M14CatalogRegressionMaximumIncreasePercent = 10.0

function Get-M14CatalogEvidenceProperty {
    param(
        [Parameter(Mandatory = $true)]
        [object]$Value,
        [Parameter(Mandatory = $true)]
        [string]$Name
    )

    if ($null -eq $Value) {
        throw "M14 catalog evidence property '$Name' is missing."
    }

    $property = $Value.PSObject.Properties[$Name]
    if ($null -eq $property -or $null -eq $property.Value) {
        throw "M14 catalog evidence property '$Name' is missing."
    }

    return $property.Value
}

function Get-M14CatalogFiniteNumber {
    param(
        [Parameter(Mandatory = $true)]
        [object]$Value,
        [Parameter(Mandatory = $true)]
        [string]$Name,
        [switch]$Positive
    )

    $rawValue = Get-M14CatalogEvidenceProperty -Value $Value -Name $Name
    if ($rawValue -isnot [byte] -and $rawValue -isnot [System.SByte] -and
        $rawValue -isnot [System.Int16] -and $rawValue -isnot [System.UInt16] -and
        $rawValue -isnot [int] -and $rawValue -isnot [System.UInt32] -and
        $rawValue -isnot [long] -and $rawValue -isnot [System.UInt64] -and
        $rawValue -isnot [float] -and $rawValue -isnot [double] -and
        $rawValue -isnot [decimal]) {
        throw "M14 catalog evidence metric '$Name' is not numeric."
    }

    try {
        $number = [Convert]::ToDouble(
            $rawValue,
            [Globalization.CultureInfo]::InvariantCulture)
    }
    catch {
        throw "M14 catalog evidence metric '$Name' is not numeric."
    }

    if ([double]::IsNaN($number) -or [double]::IsInfinity($number) -or
        ($Positive -and $number -le 0) -or (-not $Positive -and $number -lt 0)) {
        throw "M14 catalog evidence metric '$Name' is outside its finite bound."
    }

    return $number
}

function Get-M14CatalogString {
    param([Parameter(Mandatory = $true)][object]$Value, [Parameter(Mandatory = $true)][string]$Name)
    $result = Get-M14CatalogEvidenceProperty $Value $Name
    if ($result -isnot [string]) {
        throw "M14 catalog evidence property '$Name' is not a string."
    }

    return $result
}

function Get-M14CatalogBoolean {
    param([Parameter(Mandatory = $true)][object]$Value, [Parameter(Mandatory = $true)][string]$Name)
    $result = Get-M14CatalogEvidenceProperty $Value $Name
    if ($result -isnot [bool]) {
        throw "M14 catalog evidence property '$Name' is not Boolean."
    }

    return $result
}

function Get-M14CatalogInteger {
    param([Parameter(Mandatory = $true)][object]$Value, [Parameter(Mandatory = $true)][string]$Name)
    $result = Get-M14CatalogEvidenceProperty $Value $Name
    if ($result -isnot [byte] -and $result -isnot [System.SByte] -and
        $result -isnot [System.Int16] -and $result -isnot [System.UInt16] -and
        $result -isnot [int] -and $result -isnot [System.UInt32] -and
        $result -isnot [long] -and $result -isnot [System.UInt64]) {
        throw "M14 catalog evidence property '$Name' is not an integer."
    }

    return [long]$result
}

function ConvertTo-M14CatalogCanonicalJson {
    param([Parameter(Mandatory = $true)][object]$Value)

    return ($Value | ConvertTo-Json -Depth 20 -Compress)
}

function Import-M14CatalogBenchmarkEvidence {
    param([Parameter(Mandatory = $true)][string]$Path)

    $resolved = Resolve-Path -LiteralPath $Path -ErrorAction Stop
    $item = Get-Item -LiteralPath $resolved.Path -Force
    if (-not $item.PSIsContainer -and
        $item.Length -gt 0 -and
        $item.Length -le $script:M14CatalogEvidenceMaximumBytes) {
        $bytes = [IO.File]::ReadAllBytes($item.FullName)
    }
    else {
        throw 'M14 catalog evidence must be a non-empty regular file no larger than 1 MiB.'
    }

    if ($bytes.LongLength -ne $item.Length) {
        [Array]::Clear($bytes, 0, $bytes.Length)
        throw 'M14 catalog evidence changed while it was being read.'
    }

    try {
        $strictUtf8 = [Text.UTF8Encoding]::new($false, $true)
        $text = $strictUtf8.GetString($bytes)
        try {
            $evidence = $text | ConvertFrom-Json -ErrorAction Stop
        }
        catch {
            throw 'M14 catalog evidence is not valid UTF-8 JSON.'
        }

        $digest = [Security.Cryptography.SHA256]::Create()
        try {
            $sha256 = ([BitConverter]::ToString($digest.ComputeHash($bytes))).Replace('-', '').ToLowerInvariant()
        }
        finally {
            $digest.Dispose()
        }

        return [pscustomobject]@{
            FullPath = $item.FullName
            ByteLength = [long]$item.Length
            Sha256 = $sha256
            Evidence = $evidence
        }
    }
    finally {
        [Array]::Clear($bytes, 0, $bytes.Length)
    }
}

function Get-M14CatalogQueryContract {
    param([Parameter(Mandatory = $true)][object]$Evidence)

    $query = Get-M14CatalogEvidenceProperty $Evidence 'query50k'
    $recordCount = Get-M14CatalogInteger $query 'recordCount'
    $catalogSchemaVersion = Get-M14CatalogInteger $query 'catalogSchemaVersion'
    $warmupIterations = Get-M14CatalogInteger $query 'warmupIterations'
    $iterations = Get-M14CatalogInteger $query 'iterations'
    $warmupSampleRole = Get-M14CatalogString $query 'warmupSampleRole'
    $authoritativeSampleRole = Get-M14CatalogString $query 'authoritativeSampleRole'
    $percentileEstimator = Get-M14CatalogString $query 'percentileEstimator'
    $expectedOperationOrder = @(
        'FirstPage',
        'CategoryPage',
        'Search',
        'ReopenFirstVisible')
    $operationOrder = @(Get-M14CatalogEvidenceProperty $query 'operationOrder')
    $rawSamples = @(Get-M14CatalogEvidenceProperty $query 'rawSamples')
    if ($recordCount -ne 50000 -or
        $catalogSchemaVersion -ne 5 -or
        $warmupIterations -ne 5 -or
        $iterations -ne 100 -or
        $warmupSampleRole -cne 'non-authoritative' -or
        $authoritativeSampleRole -cne 'authoritative-warm' -or
        $percentileEstimator -cne 'nearest-rank-ceiling' -or
        $operationOrder.Count -ne $expectedOperationOrder.Count -or
        $rawSamples.Count -ne 100) {
        throw 'M14 reference evidence query workload contract is invalid.'
    }

    for ($index = 0; $index -lt $expectedOperationOrder.Count; $index++) {
        if ($operationOrder[$index] -isnot [string] -or
            $operationOrder[$index] -cne $expectedOperationOrder[$index]) {
            throw 'M14 reference evidence query operation order is invalid.'
        }
    }

    for ($index = 0; $index -lt $rawSamples.Count; $index++) {
        $sample = $rawSamples[$index]
        if ((Get-M14CatalogInteger $sample 'iteration') -ne ($index + 1)) {
            throw 'M14 reference evidence query sample sequence is invalid.'
        }
        foreach ($metricName in @(
                'firstPageMilliseconds',
                'categoryPageMilliseconds',
                'searchMilliseconds',
                'reopenFirstVisibleMilliseconds')) {
            $null = Get-M14CatalogFiniteNumber $sample $metricName -Positive
        }
    }

    return [ordered]@{
        recordCount = $recordCount
        catalogSchemaVersion = $catalogSchemaVersion
        warmupIterations = $warmupIterations
        iterations = $iterations
        warmupSampleRole = $warmupSampleRole
        authoritativeSampleRole = $authoritativeSampleRole
        percentileEstimator = $percentileEstimator
        operationOrder = [string[]]$operationOrder
        authoritativeRawSampleCount = $rawSamples.Count
    }
}

function Assert-M14CatalogBenchmarkReferenceEvidence {
    param(
        [Parameter(Mandatory = $true)]
        [object]$Record,
        [Parameter(Mandatory = $true)]
        [string]$ExpectedRunnerProfileId
    )

    if ($ExpectedRunnerProfileId -cnotmatch '^[a-z0-9][a-z0-9._-]{0,63}$') {
        throw 'M14 runner-profile identifier is outside its closed vocabulary.'
    }

    $evidence = Get-M14CatalogEvidenceProperty -Value $Record -Name 'Evidence'
    if ((Get-M14CatalogInteger $evidence 'schemaVersion') -ne 3 -or
        (Get-M14CatalogString $evidence 'milestone') -cne 'M14' -or
        (Get-M14CatalogString $evidence 'evidenceKind') -cne 'catalog-performance-benchmark' -or
        (Get-M14CatalogString $evidence 'configuration') -cne 'Release' -or
        (Get-M14CatalogString $evidence 'platform') -cne 'x64' -or
        (Get-M14CatalogString $evidence 'result') -cne 'passed' -or
        -not (Get-M14CatalogBoolean $evidence 'referenceModeRequested') -or
        -not (Get-M14CatalogBoolean $evidence 'referenceEligible') -or
        -not (Get-M14CatalogBoolean $evidence 'measurementIntegrityVerified') -or
        -not (Get-M14CatalogBoolean $evidence 'conditionDeclarationsComplete')) {
        throw 'M14 regression input is not eligible reference evidence.'
    }

    $commit = Get-M14CatalogString $evidence 'commitSha'
    if ($commit -cnotmatch '^[0-9a-f]{40}$') {
        throw 'M14 reference evidence commit binding is invalid.'
    }

    $runner = Get-M14CatalogEvidenceProperty $evidence 'runnerProfile'
    if ((Get-M14CatalogString $runner 'verification') -cne 'Declared' -or
        (Get-M14CatalogString $runner 'value') -cne $ExpectedRunnerProfileId) {
        throw 'M14 reference evidence runner-profile binding is invalid.'
    }

    $eligibility = Get-M14CatalogEvidenceProperty $evidence 'referenceEligibilityRequirements'
    if (-not (Get-M14CatalogBoolean $eligibility 'exactConditionDeclarations') -or
        -not (Get-M14CatalogBoolean $eligibility 'declaredRunnerProfile') -or
        -not (Get-M14CatalogBoolean $eligibility 'measurementIntegrity') -or
        -not (Get-M14CatalogBoolean $eligibility 'passingBenchmarkResult') -or
        -not (Get-M14CatalogBoolean $evidence 'authoritativeWarmSampleCountVerified')) {
        throw 'M14 reference evidence eligibility requirements are inconsistent.'
    }

    $conditions = Get-M14CatalogEvidenceProperty $evidence 'conditions'
    $expectedConditions = [ordered]@{
        cache = 'Warm'
        power = 'AcStable'
        thermal = 'Nominal'
        background = 'Controlled'
    }
    foreach ($name in $expectedConditions.Keys) {
        $condition = Get-M14CatalogEvidenceProperty $conditions $name
        if ((Get-M14CatalogString $condition 'verification') -cne 'Declared' -or
            (Get-M14CatalogString $condition 'value') -cne $expectedConditions[$name]) {
            throw "M14 reference evidence condition '$name' is invalid."
        }
    }

    $processor = Get-M14CatalogEvidenceProperty $evidence 'processor'
    $processorValue = Get-M14CatalogString $processor 'value'
    if ((Get-M14CatalogString $processor 'verification') -cne 'Observed' -or
        [string]::IsNullOrWhiteSpace($processorValue) -or $processorValue.Length -gt 128) {
        throw 'M14 reference evidence processor profile is not observed and bounded.'
    }

    $logicalProcessorCount = Get-M14CatalogInteger $evidence 'logicalProcessorCount'
    if ($logicalProcessorCount -lt 1 -or $logicalProcessorCount -gt 1024) {
        throw 'M14 reference evidence logical-processor count is invalid.'
    }

    $budgetEvaluation = Get-M14CatalogEvidenceProperty $evidence 'budgetEvaluation'
    if (-not (Get-M14CatalogBoolean $budgetEvaluation 'allPassed') -or
        -not (Get-M14CatalogBoolean $budgetEvaluation 'normalizeProtectPersistIndexPassed') -or
        -not (Get-M14CatalogBoolean $budgetEvaluation 'peakWorkingSetSamplingComplete')) {
        throw 'M14 reference evidence absolute budget result is invalid.'
    }

    foreach ($metric in @(
        'parserP95Milliseconds',
        'normalizeProtectPersistIndexConservativeUpperBoundP95Milliseconds',
        'combinedImportP95Milliseconds',
        'cancellationP95Milliseconds',
        'firstPageP95Milliseconds',
        'categoryPageP95Milliseconds',
        'searchP95Milliseconds',
        'reopenP95Milliseconds')) {
        $null = Get-M14CatalogFiniteNumber $budgetEvaluation $metric -Positive
    }

    $cancellation = Get-M14CatalogEvidenceProperty $evidence 'cancellation'
    if ((Get-M14CatalogString $cancellation 'measurementBoundary') -cne
        'CancellationRequestToLoaderCompletion') {
        throw 'M14 reference evidence cancellation measurement boundary is invalid.'
    }
    $null = Get-M14CatalogQueryContract $evidence

    $null = Get-M14CatalogEvidenceProperty $evidence 'budgets'
    $null = Get-M14CatalogEvidenceProperty $evidence 'corpora'
    $null = Get-M14CatalogEvidenceProperty $evidence 'corpusManifest'
    $null = Get-M14CatalogEvidenceProperty $evidence 'corpusSpecification'
    $null = Get-M14CatalogEvidenceProperty $evidence 'syntheticLicense'
    $null = Get-M14CatalogEvidenceProperty $evidence 'query50k'
    $null = Get-M14CatalogEvidenceProperty $evidence 'cancellation'
    $null = Get-M14CatalogEvidenceProperty $evidence 'entryLimitProbe'
    if ((Get-M14CatalogString $evidence 'plaintextLocatorCanaryScan') -cne 'passed' -or
        (Get-M14CatalogBoolean (Get-M14CatalogEvidenceProperty $evidence 'corpusManifest') 'retained')) {
        throw 'M14 reference evidence safety or transient-corpus contract is invalid.'
    }

    $null = Get-M14CatalogEnvironmentContract $evidence
    $null = Get-M14CatalogWorkloadContract $evidence
    $budgets = Get-M14CatalogEvidenceProperty $evidence 'budgets'
    foreach ($budgetName in @(
        'parserP95Milliseconds',
        'normalizeProtectPersistIndexP95Milliseconds',
        'combinedImportP95Milliseconds',
        'importAllocationMaximumBytes',
        'peakWorkingSetDeltaBytes',
        'cancellationP95Milliseconds',
        'queryP95Milliseconds',
        'reopenP95Milliseconds')) {
        $null = Get-M14CatalogFiniteNumber $budgets $budgetName -Positive
    }
}

function Get-M14CatalogEnvironmentContract {
    param([Parameter(Mandatory = $true)][object]$Evidence)

    return [ordered]@{
        configuration = Get-M14CatalogString $Evidence 'configuration'
        platform = Get-M14CatalogString $Evidence 'platform'
        sdkVersion = Get-M14CatalogString $Evidence 'sdkVersion'
        runtime = Get-M14CatalogString $Evidence 'runtime'
        operatingSystemBuild = Get-M14CatalogString $Evidence 'operatingSystemBuild'
        operatingSystemArchitecture = Get-M14CatalogString $Evidence 'operatingSystemArchitecture'
        processArchitecture = Get-M14CatalogString $Evidence 'processArchitecture'
        logicalProcessorCount = Get-M14CatalogInteger $Evidence 'logicalProcessorCount'
        processor = Get-M14CatalogEvidenceProperty $Evidence 'processor'
        runnerProfile = Get-M14CatalogEvidenceProperty $Evidence 'runnerProfile'
        conditions = Get-M14CatalogEvidenceProperty $Evidence 'conditions'
    }
}

function Get-M14CatalogWorkloadContract {
    param([Parameter(Mandatory = $true)][object]$Evidence)

    $cancellation = Get-M14CatalogEvidenceProperty $Evidence 'cancellation'
    $entryLimitProbe = Get-M14CatalogEvidenceProperty $Evidence 'entryLimitProbe'
    return [ordered]@{
        iterations = Get-M14CatalogInteger $Evidence 'iterations'
        authoritativeWarmIterations = Get-M14CatalogInteger $Evidence 'authoritativeWarmIterations'
        minimumAuthoritativeWarmIterations = Get-M14CatalogInteger $Evidence 'minimumAuthoritativeWarmIterations'
        coldObservationsPerStage = Get-M14CatalogInteger $Evidence 'coldObservationsPerStage'
        corpusManifest = Get-M14CatalogEvidenceProperty $Evidence 'corpusManifest'
        corpusSpecification = Get-M14CatalogEvidenceProperty $Evidence 'corpusSpecification'
        syntheticLicense = Get-M14CatalogEvidenceProperty $Evidence 'syntheticLicense'
        corpora = Get-M14CatalogEvidenceProperty $Evidence 'corpora'
        stageScope = Get-M14CatalogEvidenceProperty $Evidence 'stageScope'
        query50k = Get-M14CatalogQueryContract $Evidence
        cancellation = [ordered]@{
            recordCount = Get-M14CatalogInteger $cancellation 'recordCount'
            iterations = Get-M14CatalogInteger $cancellation 'iterations'
            expectedErrorCode = Get-M14CatalogString $cancellation 'expectedErrorCode'
            measurementBoundary = Get-M14CatalogString $cancellation 'measurementBoundary'
        }
        entryLimitProbe = [ordered]@{
            recordCount = Get-M14CatalogInteger $entryLimitProbe 'recordCount'
            expectedOutcome = Get-M14CatalogString $entryLimitProbe 'expectedOutcome'
            parserErrorCode = Get-M14CatalogString $entryLimitProbe 'parserErrorCode'
            combinedImportErrorCode = Get-M14CatalogString $entryLimitProbe 'combinedImportErrorCode'
            persistedRowsAfterFailure = Get-M14CatalogInteger $entryLimitProbe 'persistedRowsAfterFailure'
        }
    }
}

function Compare-M14CatalogRegression {
    param(
        [Parameter(Mandatory = $true)][object]$BaselineRecord,
        [Parameter(Mandatory = $true)][object]$CandidateRecord,
        [Parameter(Mandatory = $true)][bool]$BaselineCommitAncestorOrSelf,
        [Parameter(Mandatory = $true)][bool]$BaselineContentStable
    )

    $baseline = Get-M14CatalogEvidenceProperty $BaselineRecord 'Evidence'
    $candidate = Get-M14CatalogEvidenceProperty $CandidateRecord 'Evidence'
    $runnerProfile = Get-M14CatalogEvidenceProperty $baseline 'runnerProfile'
    $runnerProfileId = Get-M14CatalogString $runnerProfile 'value'
    Assert-M14CatalogBenchmarkReferenceEvidence $BaselineRecord $runnerProfileId
    Assert-M14CatalogBenchmarkReferenceEvidence $CandidateRecord $runnerProfileId

    $schemaCompatible =
        (Get-M14CatalogInteger $baseline 'schemaVersion') -eq
        (Get-M14CatalogInteger $candidate 'schemaVersion')
    $environmentCompatible = (ConvertTo-M14CatalogCanonicalJson (
        Get-M14CatalogEnvironmentContract $baseline)) -ceq (ConvertTo-M14CatalogCanonicalJson (
        Get-M14CatalogEnvironmentContract $candidate))
    $workloadCompatible = (ConvertTo-M14CatalogCanonicalJson (
        Get-M14CatalogWorkloadContract $baseline)) -ceq (ConvertTo-M14CatalogCanonicalJson (
        Get-M14CatalogWorkloadContract $candidate))
    $budgetsCompatible = (ConvertTo-M14CatalogCanonicalJson (
        Get-M14CatalogEvidenceProperty $baseline 'budgets')) -ceq (ConvertTo-M14CatalogCanonicalJson (
        Get-M14CatalogEvidenceProperty $candidate 'budgets'))

    $baselineEvaluation = Get-M14CatalogEvidenceProperty $baseline 'budgetEvaluation'
    $candidateEvaluation = Get-M14CatalogEvidenceProperty $candidate 'budgetEvaluation'
    $metricNames = [ordered]@{
        parserP95Milliseconds = 'parser-p95'
        normalizeProtectPersistIndexConservativeUpperBoundP95Milliseconds = 'normalize-protect-persist-index-upper-bound-p95'
        combinedImportP95Milliseconds = 'combined-import-p95'
        cancellationP95Milliseconds = 'cancellation-p95'
        firstPageP95Milliseconds = 'first-page-p95'
        categoryPageP95Milliseconds = 'category-page-p95'
        searchP95Milliseconds = 'search-p95'
        reopenP95Milliseconds = 'reopen-p95'
    }
    $metrics = @()
    foreach ($propertyName in $metricNames.Keys) {
        $baselineValue = Get-M14CatalogFiniteNumber $baselineEvaluation $propertyName -Positive
        $candidateValue = Get-M14CatalogFiniteNumber $candidateEvaluation $propertyName -Positive
        $allowedMaximum = $baselineValue *
            (1.0 + ($script:M14CatalogRegressionMaximumIncreasePercent / 100.0))
        $changePercent = (($candidateValue - $baselineValue) / $baselineValue) * 100.0
        $metrics += [pscustomobject][ordered]@{
            name = $metricNames[$propertyName]
            unit = 'milliseconds'
            baseline = $baselineValue
            candidate = $candidateValue
            allowedMaximum = $allowedMaximum
            changePercent = $changePercent
            passed = $candidateValue -le $allowedMaximum
        }
    }

    $compatibilityPassed = $schemaCompatible -and $environmentCompatible -and
        $workloadCompatible -and $budgetsCompatible
    $metricGatePassed = @($metrics | Where-Object { -not $_.passed }).Count -eq 0
    $allPassed = $compatibilityPassed -and $metricGatePassed -and
        $BaselineCommitAncestorOrSelf -and $BaselineContentStable
    return [pscustomobject][ordered]@{
        schemaVersion = 1
        milestone = 'M14'
        evidenceKind = 'catalog-performance-regression'
        result = if ($allPassed) { 'passed' } else { 'failed' }
        candidate = [pscustomobject][ordered]@{
            commitSha = Get-M14CatalogEvidenceProperty $candidate 'commitSha'
            byteLength = [long](Get-M14CatalogEvidenceProperty $CandidateRecord 'ByteLength')
            sha256 = Get-M14CatalogEvidenceProperty $CandidateRecord 'Sha256'
        }
        baseline = [pscustomobject][ordered]@{
            commitSha = Get-M14CatalogEvidenceProperty $baseline 'commitSha'
            byteLength = [long](Get-M14CatalogEvidenceProperty $BaselineRecord 'ByteLength')
            sha256 = Get-M14CatalogEvidenceProperty $BaselineRecord 'Sha256'
        }
        binding = [pscustomobject][ordered]@{
            runnerProfile = [pscustomobject][ordered]@{
                verification = 'Declared'
                value = $runnerProfileId
            }
            physicalMachineIdentityVerified = $false
            baselineCommitAncestorOrSelf = $BaselineCommitAncestorOrSelf
            baselineContentStable = $BaselineContentStable
            exactEnvironmentMatch = $environmentCompatible
            exactWorkloadMatch = $workloadCompatible
            exactBudgetContractMatch = $budgetsCompatible
            exactSchemaMatch = $schemaCompatible
        }
        threshold = [pscustomobject][ordered]@{
            metric = 'p95'
            maximumIncreasePercent = $script:M14CatalogRegressionMaximumIncreasePercent
        }
        metrics = $metrics
        absoluteBudgetResult = 'passed'
        allPassed = $allPassed
        nonClaims = @(
            'Runner-profile identity is caller-declared and does not independently verify a physical machine.',
            'No hostname, user identity, device serial, MachineGuid, path or raw hardware profile is retained.',
            'This comparison covers the component catalog benchmark only; packaged UI, image and ETW acceptance are separate.'
        )
    }
}

function Write-M14CatalogRegressionSummaryAtomically {
    param(
        [Parameter(Mandatory = $true)][object]$Summary,
        [Parameter(Mandatory = $true)][string]$Path
    )

    $fullPath = [IO.Path]::GetFullPath($Path)
    $temporaryPath = $fullPath + '.tmp'
    $directory = Split-Path -Parent $fullPath
    [IO.Directory]::CreateDirectory($directory) | Out-Null
    if ([IO.File]::Exists($fullPath) -or [IO.File]::Exists($temporaryPath)) {
        throw 'M14 regression evidence output must not already exist.'
    }

    $json = $Summary | ConvertTo-Json -Depth 20
    $bytes = [Text.UTF8Encoding]::new($false).GetBytes($json)
    if ($bytes.Length -le 0 -or $bytes.Length -gt $script:M14CatalogEvidenceMaximumBytes) {
        throw 'M14 regression evidence exceeds its bounded output size.'
    }

    try {
        [IO.File]::WriteAllBytes($temporaryPath, $bytes)
        [IO.File]::Move($temporaryPath, $fullPath)
    }
    finally {
        [Array]::Clear($bytes, 0, $bytes.Length)
        if ([IO.File]::Exists($temporaryPath)) {
            [IO.File]::Delete($temporaryPath)
        }
    }
}
