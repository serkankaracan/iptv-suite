#requires -Version 5.1

[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$ControllerPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Assert-Contract {
    param(
        [Parameter(Mandatory)]
        [bool]$Condition,

        [Parameter(Mandatory)]
        [string]$Message
    )

    if (-not $Condition) {
        throw $Message
    }
}

function Test-IsDescendantOf {
    param(
        [Parameter(Mandatory)]
        [System.Management.Automation.Language.Ast]$Node,

        [Parameter(Mandatory)]
        [System.Management.Automation.Language.Ast]$Ancestor
    )

    $current = $Node
    while ($null -ne $current) {
        if ([object]::ReferenceEquals($current, $Ancestor)) {
            return $true
        }

        $current = $current.Parent
    }

    return $false
}

function Get-Ancestor {
    param(
        [Parameter(Mandatory)]
        [System.Management.Automation.Language.Ast]$Node,

        [Parameter(Mandatory)]
        [type]$Type
    )

    $current = $Node.Parent
    while ($null -ne $current) {
        if ($Type.IsInstanceOfType($current)) {
            return $current
        }

        $current = $current.Parent
    }

    return $null
}

function Get-CommandArgument {
    param(
        [Parameter(Mandatory)]
        [System.Management.Automation.Language.CommandAst]$Command,

        [Parameter(Mandatory)]
        [string]$Name
    )

    for ($index = 1; $index -lt ($Command.CommandElements.Count - 1); $index++) {
        $element = $Command.CommandElements[$index]
        if ($element -is [System.Management.Automation.Language.CommandParameterAst] -and
            $element.ParameterName -eq $Name) {
            return $Command.CommandElements[$index + 1]
        }
    }

    return $null
}

function Get-DirectCommand {
    param(
        [Parameter(Mandatory)]
        [System.Management.Automation.Language.StatementAst]$Statement
    )

    if ($Statement -isnot [System.Management.Automation.Language.PipelineAst] -or
        $Statement.PipelineElements.Count -ne 1 -or
        $Statement.PipelineElements[0] -isnot [System.Management.Automation.Language.CommandAst]) {
        return $null
    }

    return $Statement.PipelineElements[0]
}

function Get-VariableName {
    param(
        [Parameter(Mandatory)]
        [System.Management.Automation.Language.Ast]$Node
    )

    if ($Node -is [System.Management.Automation.Language.VariableExpressionAst]) {
        return $Node.VariablePath.UserPath
    }

    return $null
}

function Get-NormalizedVariableName {
    param(
        [Parameter(Mandatory)]
        [string]$Name
    )

    $scopeSeparator = $Name.LastIndexOf(':')
    if ($scopeSeparator -ge 0) {
        return $Name.Substring($scopeSeparator + 1)
    }

    return $Name
}

function Get-UnwrappedExpression {
    param(
        [Parameter(Mandatory)]
        [System.Management.Automation.Language.Ast]$Node
    )

    if ($Node -is [System.Management.Automation.Language.CommandExpressionAst]) {
        return $Node.Expression
    }

    return $Node
}

function Get-HashtableFromAssignment {
    param(
        [Parameter(Mandatory)]
        [System.Management.Automation.Language.AssignmentStatementAst]$Assignment
    )

    $tables = @($Assignment.Right.FindAll({
        param($node)
        $node -is [System.Management.Automation.Language.HashtableAst]
    }, $true))
    Assert-Contract ($tables.Count -eq 1) "An evidence assignment must contain exactly one hashtable."
    return $tables[0]
}

function Get-HashtableKeys {
    param(
        [Parameter(Mandatory)]
        [System.Management.Automation.Language.HashtableAst]$Hashtable
    )

    return @($Hashtable.KeyValuePairs | ForEach-Object {
        Assert-Contract `
            ($_.Item1 -is [System.Management.Automation.Language.StringConstantExpressionAst]) `
            "Evidence keys must be static strings."
        $_.Item1.Value
    })
}

function Assert-ExactSequence {
    param(
        [Parameter(Mandatory)]
        [AllowEmptyCollection()]
        [object[]]$Actual,

        [Parameter(Mandatory)]
        [AllowEmptyCollection()]
        [object[]]$Expected,

        [Parameter(Mandatory)]
        [string]$Message
    )

    Assert-Contract ($Actual.Count -eq $Expected.Count) $Message
    for ($index = 0; $index -lt $Expected.Count; $index++) {
        Assert-Contract `
            ([string]::Equals(
                [string]$Actual[$index],
                [string]$Expected[$index],
                [System.StringComparison]::Ordinal)) `
            $Message
    }
}

$resolvedControllerPath = (Resolve-Path -LiteralPath $ControllerPath -ErrorAction Stop).Path
$tokens = $null
$parseErrors = $null
$ast = [System.Management.Automation.Language.Parser]::ParseFile(
    $resolvedControllerPath,
    [ref]$tokens,
    [ref]$parseErrors)
Assert-Contract ($parseErrors.Count -eq 0) "The native playback controller has PowerShell 5.1 parse errors."

$probePropertyAssertions = @($ast.FindAll({
    param($node)
    if ($node -isnot [System.Management.Automation.Language.CommandAst] -or
        $node.GetCommandName() -ne "Assert-ExactJsonProperties") {
        return $false
    }

    $valueArgument = Get-CommandArgument -Command $node -Name "Value"
    if ($null -eq $valueArgument) {
        return $false
    }

    $valueExpression = Get-UnwrappedExpression -Node $valueArgument
    return (Get-VariableName -Node $valueExpression) -eq "probe"
}, $true))
Assert-Contract `
    ($probePropertyAssertions.Count -eq 1) `
    "The nested native playback probe must have one exact property assertion."
$probeExpectedNamesArgument = Get-CommandArgument `
    -Command $probePropertyAssertions[0] `
    -Name "ExpectedNames"
Assert-Contract `
    ($null -ne $probeExpectedNamesArgument) `
    "The nested native playback probe property assertion must declare its allowlist."
$actualProbePropertyNames = @($probeExpectedNamesArgument.FindAll({
    param($node)
    $node -is [System.Management.Automation.Language.StringConstantExpressionAst]
}, $true) | ForEach-Object {
    $_.Value
})
$expectedProbePropertyNames = @(
    "Success",
    "Failure",
    "SwitchCount",
    "StartupP95Milliseconds",
    "StartupMaximumMilliseconds",
    "HlsStartupP95Milliseconds",
    "DirectStartupP95Milliseconds",
    "StartupMaximumSwitchOrdinal",
    "StartupMaximumFixture",
    "StartupMaximumAttemptCount",
    "StartupMaximumSurfaceTransitionCount",
    "StartupMaximumPreWaitMilliseconds",
    "StartupMaximumMediaOpenWaitMilliseconds",
    "StartupMaximumSourceOpen",
    "HlsStartupMaximumMilliseconds",
    "DirectStartupMaximumMilliseconds",
    "StartupFailureStage",
    "StartupFailureSwitchOrdinal",
    "StartupFailureFixture",
    "StartupFailureAttemptCount",
    "StartupFailureSurfaceTransitionCount",
    "StartupFailureTotalMilliseconds",
    "StartupFailureSourceCreationMilliseconds",
    "StartupFailureSourceAssignmentMilliseconds",
    "StartupFailurePlayInvocationMilliseconds",
    "StartupFailureSourceOpen",
    "StartupFailureActiveStageElapsedMilliseconds",
    "SoakMinutes",
    "ResourceSampleCount",
    "WarmupPrivateBytes",
    "MemoryNetGrowthBytes",
    "MemoryNetGrowthPercent",
    "MemoryMonotonicIncrease",
    "WarmupHandleCount",
    "HandleNetGrowth",
    "SurfaceTransitionCount",
    "DetachedSourceCount",
    "PlaybackRetryCount",
    "SourceDetachP95Milliseconds",
    "SourceDetachMaximumMilliseconds",
    "CancellationProbeCount",
    "CancellationObservedCount",
    "CancellationSourceDetachCount",
    "CancellationRecoveryCount",
    "CancellationRecoverySourceDetachCount",
    "CancellationLatencyMilliseconds",
    "CancellationQuiescenceMilliseconds",
    "CancellationObservationMilliseconds",
    "CancellationSourceDetachMilliseconds",
    "CancellationRecoveryStartupMilliseconds",
    "CancellationRecoveryAdvanceMilliseconds",
    "CancellationRecoverySourceDetachMilliseconds",
    "CancellationSourceNullAfterObservation",
    "CancellationRecoveryUsedFreshSource",
    "CancellationNoAutomaticRestart",
    "PlaybackStateBeforeDetach",
    "SourceDetached",
    "CanPauseBeforeDetach",
    "CanSeekBeforeDetach",
    "TeardownStage",
    "ExceptionCategory",
    "ExceptionHResult",
    "InitialPrivateBytes",
    "FinalPrivateBytes",
    "InitialHandleCount",
    "FinalHandleCount"
)
Assert-ExactSequence `
    -Actual $actualProbePropertyNames `
    -Expected $expectedProbePropertyNames `
    -Message "The nested native playback probe must remain an exact ordered allowlist."

$mainTryCandidates = @($ast.EndBlock.Statements | Where-Object {
    if ($_ -isnot [System.Management.Automation.Language.TryStatementAst]) {
        return $false
    }

    $assignments = @($_.Body.Statements | Where-Object {
        $_ -is [System.Management.Automation.Language.AssignmentStatementAst] -and
        (Get-VariableName -Node $_.Left) -eq "successCandidate" -and
        @($_.Right.FindAll({
            param($node)
            $node -is [System.Management.Automation.Language.HashtableAst]
        }, $true)).Count -eq 1
    })
    return $assignments.Count -eq 1
})
Assert-Contract ($mainTryCandidates.Count -eq 1) "The controller must have one top-level evidence transaction try."
$mainTry = $mainTryCandidates[0]
Assert-Contract ($null -ne $mainTry.Finally) "The evidence transaction must have a finally cleanup block."

$successAssignments = @($mainTry.Body.Statements | Where-Object {
    $_ -is [System.Management.Automation.Language.AssignmentStatementAst] -and
    (Get-VariableName -Node $_.Left) -eq "successCandidate"
})
Assert-Contract ($successAssignments.Count -eq 1) "Success evidence must be prepared once in the transaction body."
$successAssignment = $successAssignments[0]
Assert-Contract `
    ([object]::ReferenceEquals($successAssignment.Parent, $mainTry.Body)) `
    "Success evidence preparation must be a direct transaction-body statement."
$successHashtable = Get-HashtableFromAssignment -Assignment $successAssignment

$expectedSuccessKeys = @(
    "SchemaVersion", "Stage", "Result", "RunId", "CompletedAtUtc",
    "Configuration", "Platform", "DotNetSdk", "CleanHeadBound", "CommitSha",
    "ControllerScriptSha256", "HarnessAssemblySha256", "FixtureManifestSha256",
    "FixtureCorpusVerified", "ProbeEnvelopeSchemaVersion", "ProbeRunIdBound",
    "SwitchCount", "StartupP95Milliseconds",
    "StartupMaximumMilliseconds", "HlsStartupP95Milliseconds", "DirectStartupP95Milliseconds",
    "SoakMinutes", "ResourceSampleCount", "WarmupPrivateBytes", "MemoryNetGrowthBytes",
    "MemoryNetGrowthPercent", "MemoryMonotonicIncrease", "WarmupHandleCount",
    "HandleNetGrowth", "SurfaceTransitionCount", "DetachedSourceCount", "PlaybackRetryCount",
    "SourceDetachP95Milliseconds", "SourceDetachMaximumMilliseconds", "NetworkInterruptionCount",
    "NetworkRecoveryCount", "LastInjectedRequestOrdinal", "LastRecoveryRequestOrdinal",
    "CancellationProbeCount", "CancellationObservedCount", "CancellationSourceDetachCount",
    "CancellationRecoveryCount", "CancellationRecoverySourceDetachCount",
    "CancellationLatencyMilliseconds", "CancellationQuiescenceMilliseconds",
    "CancellationObservationMilliseconds", "CancellationSourceDetachMilliseconds",
    "CancellationRecoveryStartupMilliseconds", "CancellationRecoveryAdvanceMilliseconds",
    "CancellationRecoverySourceDetachMilliseconds", "CancellationSourceNullAfterObservation",
    "CancellationRecoveryUsedFreshSource", "CancellationNoAutomaticRestart",
    "InitialPrivateBytes", "FinalPrivateBytes", "InitialHandleCount", "FinalHandleCount",
    "LoopbackRequestCount", "H264DecoderRegistered", "AacDecoderRegistered", "Transport",
    "Fixtures", "PackageSha256", "PackageSignatureStatus", "RuntimeDependencyPackageSha256",
    "RuntimeDependencyPackageSignatureStatus", "ResolvedWindowsAppRuntimeName",
    "ResolvedWindowsAppRuntimeVersion", "ResolvedWindowsAppRuntimeArchitecture",
    "ResolvedWindowsAppRuntimePublisherId", "ResolvedWindowsAppRuntimeIsFramework",
    "NormalCloseVerified", "ForcedProcessTerminationUsed",
    "ProcessCleanupPassed", "TlsServerDisposed", "PackageRemoved", "PackageAppDataRemoved",
    "PackageAppDataEmptyRootCleanupUsed", "RuntimePackageBaselinePreserved",
    "RuntimePackageGraphDisposition", "RuntimePackageSharedAdditionCount",
    "EphemeralCertificatesRemoved",
    "ExportedCertificateFilesRemoved", "PackageOutputRemoved", "EnvironmentRestored",
    "RepositoryCleanAfterRun"
)
Assert-ExactSequence `
    -Actual (Get-HashtableKeys -Hashtable $successHashtable) `
    -Expected $expectedSuccessKeys `
    -Message "Success evidence keys must remain an exact ordered allowlist."

$forbiddenSuccessVariables = @(
    "authority",
    "aumid",
    "installedPackage",
    "packageAppDataPath",
    "signingCertificate",
    "tlsCertificate",
    "packageEvidencePath"
)
$successVariables = @($successHashtable.FindAll({
    param($node)
    $node -is [System.Management.Automation.Language.VariableExpressionAst]
}, $true) | ForEach-Object {
    Get-NormalizedVariableName -Name $_.VariablePath.UserPath
})
foreach ($forbiddenVariable in $forbiddenSuccessVariables) {
    Assert-Contract `
        (@($successVariables | Where-Object {
            [string]::Equals(
                $_,
                $forbiddenVariable,
                [System.StringComparison]::OrdinalIgnoreCase)
        }).Count -eq 0) `
        "Success evidence contains a forbidden sensitive reference."
}

$cleanupCommands = @($mainTry.Finally.FindAll({
    param($node)
    $node -is [System.Management.Automation.Language.CommandAst] -and
    $node.GetCommandName() -eq "Invoke-CleanupStep"
}, $true))
$cleanupCodes = @($cleanupCommands | ForEach-Object {
    $code = Get-CommandArgument -Command $_ -Name "Code"
    Assert-Contract `
        ($code -is [System.Management.Automation.Language.StringConstantExpressionAst]) `
        "Cleanup codes must be static strings."
    $code.Value
})
$expectedCleanupCodes = @(
    "ProcessCleanupFailed",
    "TlsServerCleanupFailed",
    "PackageCleanupFailed",
    "PackageAppDataCleanupFailed",
    "RuntimeDependencyValidationFailed",
    "EnvironmentCleanupFailed",
    "TlsCertificateCleanupFailed",
    "SigningCertificateCleanupFailed",
    "ExportedCertificateCleanupFailed",
    "PackageOutputCleanupFailed"
)
Assert-ExactSequence `
    -Actual $cleanupCodes `
    -Expected $expectedCleanupCodes `
    -Message "Cleanup steps must remain complete and ordered."

$killCalls = @($ast.FindAll({
    param($node)
    $node -is [System.Management.Automation.Language.InvokeMemberExpressionAst] -and
    $node.Member -is [System.Management.Automation.Language.StringConstantExpressionAst] -and
    $node.Member.Value -eq "Kill"
}, $true))
Assert-Contract ($killCalls.Count -eq 1) "Only the tracked native playback process may have a kill fallback."
Assert-Contract `
    (Test-IsDescendantOf -Node $killCalls[0] -Ancestor $mainTry.Finally) `
    "The tracked-process kill fallback must exist only in transaction cleanup."

$atomicHelpers = @($ast.FindAll({
    param($node)
    $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and
    $node.Name -eq "Write-JsonAtomically"
}, $true))
Assert-Contract ($atomicHelpers.Count -eq 1) "The controller must have one atomic JSON helper."
$atomicHelperText = $atomicHelpers[0].Body.Extent.Text
Assert-Contract `
    ($atomicHelperText.Contains("[System.IO.FileMode]::CreateNew") -and
        $atomicHelperText.Contains('$stream.Flush($true)') -and
        $atomicHelperText.Contains('[System.IO.File]::Move($temporaryPath, $DestinationPath)')) `
    "The atomic JSON helper must create, flush, and move without overwrite."
Assert-Contract `
    (-not $atomicHelperText.Contains("Move-Item") -and
        -not $atomicHelperText.Contains("-Force")) `
    "The atomic JSON helper must not use overwrite-capable publication."

$jsonWrites = @($ast.FindAll({
    param($node)
    $node -is [System.Management.Automation.Language.CommandAst] -and
    $node.GetCommandName() -eq "Write-JsonAtomically"
}, $true))
Assert-Contract ($jsonWrites.Count -eq 2) "The controller must have exactly one success and one failure publication."
$successWrites = @($jsonWrites | Where-Object {
    (Get-VariableName -Node (Get-CommandArgument -Command $_ -Name "Value")) -eq "successCandidate" -and
    (Get-VariableName -Node (Get-CommandArgument -Command $_ -Name "DestinationPath")) -eq "evidencePath"
})
$failureWrites = @($jsonWrites | Where-Object {
    (Get-VariableName -Node (Get-CommandArgument -Command $_ -Name "Value")) -eq "failureEvidence" -and
    (Get-VariableName -Node (Get-CommandArgument -Command $_ -Name "DestinationPath")) -eq "failureEvidencePath"
})
Assert-Contract ($successWrites.Count -eq 1) "Success evidence must have one exact atomic publication."
Assert-Contract ($failureWrites.Count -eq 1) "Failure evidence must have one exact atomic publication."
$successWrite = $successWrites[0]
$failureWrite = $failureWrites[0]
Assert-Contract `
    (-not (Test-IsDescendantOf -Node $successWrite -Ancestor $mainTry)) `
    "Success evidence must not be published before transaction cleanup completes."
Assert-Contract `
    (-not (Test-IsDescendantOf -Node $failureWrite -Ancestor $mainTry)) `
    "Failure evidence must not be published before transaction cleanup completes."

$successIf = Get-Ancestor -Node $successWrite -Type ([System.Management.Automation.Language.IfStatementAst])
Assert-Contract ($null -ne $successIf) "Success publication must be guarded by a top-level if statement."
Assert-Contract `
    ([object]::ReferenceEquals($successIf.Parent, $ast.EndBlock)) `
    "Success publication must use a top-level cleanup-success guard."
Assert-Contract `
    ($successIf.Extent.StartOffset -gt $mainTry.Extent.EndOffset) `
    "Success publication must occur after the transaction try/finally."

$failureIf = Get-Ancestor -Node $failureWrite -Type ([System.Management.Automation.Language.IfStatementAst])
Assert-Contract ($null -ne $failureIf) "Failure publication must be guarded by a top-level if statement."
Assert-Contract `
    ([object]::ReferenceEquals($failureIf.Parent, $ast.EndBlock)) `
    "Failure publication must use a top-level transaction-failure guard."
Assert-Contract `
    ($failureIf.Extent.StartOffset -gt $mainTry.Extent.EndOffset) `
    "Failure publication must occur after the transaction try/finally."
$failureConditionPipeline = $failureIf.Clauses[0].Item1
Assert-Contract `
    ($failureConditionPipeline.PipelineElements.Count -eq 1 -and
        $failureConditionPipeline.PipelineElements[0] -is [System.Management.Automation.Language.CommandExpressionAst]) `
    "The failure guard must be one expression."
$failureCondition = $failureConditionPipeline.PipelineElements[0].Expression
Assert-Contract `
    ($failureCondition -is [System.Management.Automation.Language.BinaryExpressionAst] -and
        $failureCondition.Operator -eq [System.Management.Automation.Language.TokenKind]::Or) `
    "The failure guard must require a primary or cleanup failure."
$primaryFailureGuard = $failureCondition.Left
$cleanupFailureGuard = $failureCondition.Right
Assert-Contract `
    ($primaryFailureGuard -is [System.Management.Automation.Language.BinaryExpressionAst] -and
        $primaryFailureGuard.Operator -eq [System.Management.Automation.Language.TokenKind]::Ine -and
        (Get-VariableName -Node $primaryFailureGuard.Left) -eq "null" -and
        (Get-VariableName -Node $primaryFailureGuard.Right) -eq "primaryFailure") `
    "The failure guard must require a non-null primary failure."
Assert-Contract `
    ($cleanupFailureGuard -is [System.Management.Automation.Language.BinaryExpressionAst] -and
        $cleanupFailureGuard.Operator -eq [System.Management.Automation.Language.TokenKind]::Ine -and
        $cleanupFailureGuard.Left -is [System.Management.Automation.Language.MemberExpressionAst] -and
        (Get-VariableName -Node $cleanupFailureGuard.Left.Expression) -eq "cleanupFailures" -and
        $cleanupFailureGuard.Left.Member.Value -eq "Count" -and
        $cleanupFailureGuard.Right -is [System.Management.Automation.Language.ConstantExpressionAst] -and
        $cleanupFailureGuard.Right.Value -eq 0) `
    "The failure guard must require one or more cleanup failures."

$conditionPipeline = $successIf.Clauses[0].Item1
Assert-Contract `
    ($conditionPipeline.PipelineElements.Count -eq 1 -and
        $conditionPipeline.PipelineElements[0] -is [System.Management.Automation.Language.CommandExpressionAst]) `
    "The success guard must be one expression."
$condition = $conditionPipeline.PipelineElements[0].Expression
Assert-Contract `
    ($condition -is [System.Management.Automation.Language.BinaryExpressionAst] -and
        $condition.Operator -eq [System.Management.Automation.Language.TokenKind]::And) `
    "The success guard must require both primary and cleanup success."
$primaryGuard = $condition.Left
$cleanupGuard = $condition.Right
Assert-Contract `
    ($primaryGuard -is [System.Management.Automation.Language.BinaryExpressionAst] -and
        $primaryGuard.Operator -eq [System.Management.Automation.Language.TokenKind]::Ieq -and
        (Get-VariableName -Node $primaryGuard.Left) -eq "null" -and
        (Get-VariableName -Node $primaryGuard.Right) -eq "primaryFailure") `
    "The success guard must require a null primary failure."
Assert-Contract `
    ($cleanupGuard -is [System.Management.Automation.Language.BinaryExpressionAst] -and
        $cleanupGuard.Operator -eq [System.Management.Automation.Language.TokenKind]::Ieq -and
        $cleanupGuard.Left -is [System.Management.Automation.Language.MemberExpressionAst] -and
        (Get-VariableName -Node $cleanupGuard.Left.Expression) -eq "cleanupFailures" -and
        $cleanupGuard.Left.Member.Value -eq "Count" -and
        $cleanupGuard.Right -is [System.Management.Automation.Language.ConstantExpressionAst] -and
        $cleanupGuard.Right.Value -eq 0) `
    "The success guard must require zero cleanup failures."

$successBody = $successIf.Clauses[0].Item2
$publicationTries = @($successBody.Statements | Where-Object {
    $_ -is [System.Management.Automation.Language.TryStatementAst]
})
Assert-Contract ($publicationTries.Count -eq 1) "The guarded publication must have one direct try statement."
$publicationTry = $publicationTries[0]
Assert-Contract `
    (Test-IsDescendantOf -Node $successWrite -Ancestor $publicationTry.Body) `
    "Success evidence must be written from the publication try body."
foreach ($catchClause in $publicationTry.CatchClauses) {
    Assert-Contract `
        (-not (Test-IsDescendantOf -Node $successWrite -Ancestor $catchClause.Body)) `
        "Success evidence must not be written from a publication catch block."
}
if ($null -ne $publicationTry.Finally) {
    Assert-Contract `
        (-not (Test-IsDescendantOf -Node $successWrite -Ancestor $publicationTry.Finally)) `
        "Success evidence must not be written from a publication finally block."
}

$publicationStatements = @($publicationTry.Body.Statements)
Assert-Contract ($publicationStatements.Count -ge 5) "The success publication sequence is incomplete."
$cleanupVerificationCommand = Get-DirectCommand -Statement $publicationStatements[0]
$publicationCommand = Get-DirectCommand -Statement $publicationStatements[-2]
$atomicWriteCommand = Get-DirectCommand -Statement $publicationStatements[-1]
Assert-Contract `
    ($null -ne $cleanupVerificationCommand -and
        $cleanupVerificationCommand.GetCommandName() -eq "Set-FailurePoint" -and
        (Get-CommandArgument -Command $cleanupVerificationCommand -Name "Stage").Value -eq "CleanupVerification") `
    "Cleanup verification must begin the guarded publication."
Assert-Contract `
    ($publicationStatements[1] -is [System.Management.Automation.Language.IfStatementAst]) `
    "Cleanup verification must reject incomplete evidence before flag publication."
Assert-Contract `
    ($null -ne $publicationCommand -and
        $publicationCommand.GetCommandName() -eq "Set-FailurePoint" -and
        (Get-CommandArgument -Command $publicationCommand -Name "Stage").Value -eq "EvidencePublication") `
    "Evidence publication must set its stable failure point immediately before writing."
Assert-Contract `
    ([object]::ReferenceEquals($atomicWriteCommand, $successWrite)) `
    "The atomic success write must be the final publication-try statement."

$publicationAssignments = @($publicationStatements[2..($publicationStatements.Count - 3)])
Assert-Contract `
    (@($publicationAssignments | Where-Object {
        $_ -isnot [System.Management.Automation.Language.AssignmentStatementAst]
    }).Count -eq 0) `
    "Only evidence flag assignments may occur between verification and publication."
$publishedKeys = @($publicationAssignments | ForEach-Object {
    Assert-Contract `
        ($_.Left -is [System.Management.Automation.Language.IndexExpressionAst] -and
            (Get-VariableName -Node $_.Left.Target) -eq "successCandidate" -and
            $_.Left.Index -is [System.Management.Automation.Language.StringConstantExpressionAst]) `
        "Published cleanup flags must target static success-evidence keys."
    $_.Left.Index.Value
})
$expectedPublishedKeys = @(
    "CompletedAtUtc",
    "NormalCloseVerified",
    "ForcedProcessTerminationUsed",
    "ProcessCleanupPassed",
    "TlsServerDisposed",
    "PackageRemoved",
    "PackageAppDataRemoved",
    "PackageAppDataEmptyRootCleanupUsed",
    "RuntimePackageBaselinePreserved",
    "RuntimePackageGraphDisposition",
    "RuntimePackageSharedAdditionCount",
    "EphemeralCertificatesRemoved",
    "ExportedCertificateFilesRemoved",
    "PackageOutputRemoved",
    "EnvironmentRestored",
    "RepositoryCleanAfterRun"
)
Assert-ExactSequence `
    -Actual $publishedKeys `
    -Expected $expectedPublishedKeys `
    -Message "The exact cleanup evidence flags must be published in order."

$expectedFlagValues = @{
    NormalCloseVerified = "true"
    ForcedProcessTerminationUsed = "false"
    ProcessCleanupPassed = "true"
    TlsServerDisposed = "true"
    PackageRemoved = "true"
    PackageAppDataRemoved = "true"
    PackageAppDataEmptyRootCleanupUsed = "packageAppDataEmptyRootCleanupUsed"
    RuntimePackageBaselinePreserved = "true"
    RuntimePackageGraphDisposition = "runtimePackageGraphDisposition"
    RuntimePackageSharedAdditionCount = "runtimePackageSharedAdditionCount"
    EphemeralCertificatesRemoved = "true"
    ExportedCertificateFilesRemoved = "true"
    PackageOutputRemoved = "true"
    EnvironmentRestored = "true"
    RepositoryCleanAfterRun = "true"
}
foreach ($assignment in $publicationAssignments | Select-Object -Skip 1) {
    $key = $assignment.Left.Index.Value
    $valueExpression = Get-UnwrappedExpression -Node $assignment.Right
    Assert-Contract `
        ((Get-VariableName -Node $valueExpression) -ceq $expectedFlagValues[$key]) `
        "A cleanup evidence flag has an unsafe publication value."
}

$failureAssignments = @($ast.FindAll({
    param($node)
    $node -is [System.Management.Automation.Language.AssignmentStatementAst] -and
    $node.Left -is [System.Management.Automation.Language.VariableExpressionAst] -and
    $node.Left.VariablePath.UserPath -eq "failureEvidence"
}, $true))
Assert-Contract ($failureAssignments.Count -eq 1) "Failure evidence must be prepared exactly once."
$failureHashtable = Get-HashtableFromAssignment -Assignment $failureAssignments[0]
Assert-ExactSequence `
    -Actual (Get-HashtableKeys -Hashtable $failureHashtable) `
    -Expected @("Stage", "Code") `
    -Message "Failure evidence must remain a stable two-field allowlist."

function Get-ControllerFunctionDefinition {
    param(
        [Parameter(Mandatory)]
        [string]$Name
    )

    $definitions = @($ast.FindAll({
        param($node)
        $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and
        [string]::Equals($node.Name, $Name, [System.StringComparison]::Ordinal)
    }, $true))
    Assert-Contract ($definitions.Count -eq 1) "A required runtime cleanup function is missing or ambiguous."
    return $definitions[0]
}

function New-RuntimePackageRecord {
    param(
        [Parameter(Mandatory)]
        [string]$Version,

        [Parameter(Mandatory)]
        [string]$Architecture,

        [Parameter(Mandatory)]
        [string]$PackageFullName,

        [string]$Name = "Microsoft.WindowsAppRuntime.2",

        [string]$Publisher = "CN=Microsoft Corporation, O=Microsoft Corporation, L=Redmond, S=Washington, C=US",

        [string]$PackageFamilyName = "Microsoft.WindowsAppRuntime.2_8wekyb3d8bbwe",

        [bool]$IsFramework = $true
    )

    return [pscustomobject]@{
        Name = $Name
        Publisher = $Publisher
        PackageFamilyName = $PackageFamilyName
        Version = [version]$Version
        Architecture = $Architecture
        IsFramework = $IsFramework
        PackageFullName = $PackageFullName
    }
}

$script:expectedRuntimeDependencyName = "Microsoft.WindowsAppRuntime.2"
$script:expectedRuntimeDependencyPublisher =
    "CN=Microsoft Corporation, O=Microsoft Corporation, L=Redmond, S=Washington, C=US"
$script:expectedRuntimeDependencyPublisherId = "8wekyb3d8bbwe"
$script:expectedRuntimeDependencyVersion = "2.4.0.0"
$script:expectedRuntimeDependencyArchitectures = @("X64", "X86")
$global:runtimeCleanupClock = [datetime]::Parse("2026-08-22T00:00:00Z").ToUniversalTime()
$global:runtimeCleanupCurrentPackages = @()

function global:Invoke-MockGetDate {
    $global:runtimeCleanupClock = $global:runtimeCleanupClock.AddSeconds(10)
    return $global:runtimeCleanupClock
}

function global:Invoke-MockStartSleep {
    param([int]$Milliseconds)
}

function global:Invoke-MockGetRuntimeDependencyPackages {
    return @($global:runtimeCleanupCurrentPackages)
}

$runtimeGraphTestText =
    (Get-ControllerFunctionDefinition -Name "Test-RuntimeDependencyPackageGraph").Extent.Text.Replace(
        "Get-RuntimeDependencyPackages",
        "Invoke-MockGetRuntimeDependencyPackages")
$runtimeGraphValidationText =
    (Get-ControllerFunctionDefinition -Name "Validate-RuntimeDependencyPackageState").Extent.Text.Replace(
        "Get-RuntimeDependencyPackages",
        "Invoke-MockGetRuntimeDependencyPackages").Replace(
        "Get-Date",
        "Invoke-MockGetDate").Replace(
        "Start-Sleep",
        "Invoke-MockStartSleep")
. ([scriptblock]::Create($runtimeGraphTestText))
. ([scriptblock]::Create($runtimeGraphValidationText))

function Invoke-RuntimeValidationScenario {
    param(
        [Parameter(Mandatory)]
        [AllowEmptyCollection()]
        [object[]]$Before,

        [Parameter(Mandatory)]
        [AllowEmptyCollection()]
        [object[]]$Current,

        [Parameter(Mandatory)]
        [bool]$ExpectFailure,

        [Parameter(Mandatory)]
        [string]$Name
    )

    $global:runtimeCleanupClock = [datetime]::Parse("2026-08-22T00:00:00Z").ToUniversalTime()
    $script:runtimePackagesBefore = @($Before | ForEach-Object { $_.PackageFullName } | Sort-Object)
    $global:runtimeCleanupCurrentPackages = @($Current)
    $currentNamesBeforeValidation = @($Current | ForEach-Object { $_.PackageFullName } | Sort-Object)
    $script:runtimePackageBaselinePreserved = $false
    $script:runtimePackageGraphDisposition = $null
    $script:runtimePackageSharedAdditionCount = -1
    $script:runtimeDependencyCleanupDiagnostic = "NotStarted"
    $script:installAttempted = $true
    $scenarioAddedPackages = @($global:runtimeCleanupCurrentPackages | Where-Object {
        $currentFullName = $_.PackageFullName
        @($script:runtimePackagesBefore | Where-Object {
            [string]::Equals($_, $currentFullName, [System.StringComparison]::OrdinalIgnoreCase)
        }).Count -eq 0
    })

    $failed = $false
    $failureMessage = $null
    try {
        Validate-RuntimeDependencyPackageState
    }
    catch {
        $failed = $true
        $failureMessage = $_.Exception.Message
    }

    Assert-Contract `
        ($failed -eq $ExpectFailure) `
        "Runtime validation scenario '$Name' returned an unexpected result: $failureMessage; diagnostic=$script:runtimeDependencyCleanupDiagnostic; added=$($scenarioAddedPackages.Count); current=$($global:runtimeCleanupCurrentPackages.Count)"
    Assert-ExactSequence `
        -Actual @($global:runtimeCleanupCurrentPackages | ForEach-Object { $_.PackageFullName } | Sort-Object) `
        -Expected $currentNamesBeforeValidation `
        -Message "Runtime validation scenario '$Name' mutated the shared package graph."
    if (-not $ExpectFailure) {
        Assert-Contract `
            $script:runtimePackageBaselinePreserved `
            "Runtime validation scenario '$Name' did not preserve the baseline invariant."
        $expectedDisposition = if ($scenarioAddedPackages.Count -eq 0) {
            "ExactRestored"
        }
        else {
            "SharedAdditionsPreserved"
        }
        Assert-Contract `
            ([string]::Equals(
                    $script:runtimePackageGraphDisposition,
                    $expectedDisposition,
                    [System.StringComparison]::Ordinal) -and
                $script:runtimePackageSharedAdditionCount -eq $scenarioAddedPackages.Count) `
            "Runtime validation scenario '$Name' published an invalid graph disposition."
    }
}

$runtimeX64V24 = New-RuntimePackageRecord `
    -Version "2.4.0.0" `
    -Architecture "X64" `
    -PackageFullName "Microsoft.WindowsAppRuntime.2_2.4.0.0_x64__8wekyb3d8bbwe"
$runtimeX86V24 = New-RuntimePackageRecord `
    -Version "2.4.0.0" `
    -Architecture "X86" `
    -PackageFullName "Microsoft.WindowsAppRuntime.2_2.4.0.0_x86__8wekyb3d8bbwe"
$runtimeX64V231 = New-RuntimePackageRecord `
    -Version "2.3.1.0" `
    -Architecture "X64" `
    -PackageFullName "Microsoft.WindowsAppRuntime.2_2.3.1.0_x64__8wekyb3d8bbwe"
$runtimeArm64V24 = New-RuntimePackageRecord `
    -Version "2.4.0.0" `
    -Architecture "Arm64" `
    -PackageFullName "Microsoft.WindowsAppRuntime.2_2.4.0.0_arm64__8wekyb3d8bbwe"
$runtimeWrongFamily = New-RuntimePackageRecord `
    -Version "2.4.0.0" `
    -Architecture "X64" `
    -PackageFullName "Microsoft.WindowsAppRuntime.2_2.4.0.0_x64__unknown" `
    -PackageFamilyName "Microsoft.WindowsAppRuntime.2_unknown"

Invoke-RuntimeValidationScenario `
    -Before @() `
    -Current @($runtimeX64V24) `
    -ExpectFailure $false `
    -Name "exact locked dependency becomes shared runtime state"
Invoke-RuntimeValidationScenario `
    -Before @() `
    -Current @($runtimeX64V24, $runtimeX64V24) `
    -ExpectFailure $true `
    -Name "duplicate shared runtime registration"
Invoke-RuntimeValidationScenario `
    -Before @() `
    -Current @($runtimeX64V24, $runtimeX86V24) `
    -ExpectFailure $false `
    -Name "empty baseline with shared x64 and x86 pair"
Invoke-RuntimeValidationScenario `
    -Before @() `
    -Current @($runtimeX86V24) `
    -ExpectFailure $true `
    -Name "x86 without x64 sibling"
Invoke-RuntimeValidationScenario `
    -Before @() `
    -Current @($runtimeX64V231, $runtimeX86V24) `
    -ExpectFailure $true `
    -Name "x86 with mismatched x64 version"
Invoke-RuntimeValidationScenario `
    -Before @() `
    -Current @($runtimeX64V24, $runtimeArm64V24) `
    -ExpectFailure $true `
    -Name "unsupported architecture"
Invoke-RuntimeValidationScenario `
    -Before @() `
    -Current @($runtimeX64V24, $runtimeWrongFamily) `
    -ExpectFailure $true `
    -Name "unexpected family"
Invoke-RuntimeValidationScenario `
    -Before @($runtimeX64V24) `
    -Current @($runtimeX64V24, $runtimeX86V24) `
    -ExpectFailure $false `
    -Name "baseline x64 with added shared x86 sibling"
Invoke-RuntimeValidationScenario `
    -Before @($runtimeX64V24, $runtimeX86V24) `
    -Current @($runtimeX64V24, $runtimeX86V24) `
    -ExpectFailure $false `
    -Name "unchanged baseline"
Invoke-RuntimeValidationScenario `
    -Before @($runtimeX64V231) `
    -Current @($runtimeX64V24) `
    -ExpectFailure $true `
    -Name "same count with a replaced baseline package"

Write-Output "Native playback evidence AST contract passed."
