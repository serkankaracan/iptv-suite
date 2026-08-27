[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$script:utf8NoBom = New-Object System.Text.UTF8Encoding($false)
$script:repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot "..\..\..\.."))
$script:helperPath = Join-Path $script:repositoryRoot "eng\WindowsM16FinalArtifactEvidence.ps1"
$script:fixtureRoot = Join-Path `
    ([System.IO.Path]::GetTempPath()) `
    "IptvSuite-M16-FinalArtifact-$([Guid]::NewGuid().ToString('N'))"
$script:inputRoot = Join-Path $script:fixtureRoot "inputs"
$script:packagePath = Join-Path $script:inputRoot "package-intermediate.json"
$script:fullLogPath = Join-Path $script:inputRoot "full-log-report.json"
$script:outputPath = Join-Path $script:fixtureRoot "out\final-evidence.json"
$script:runId = "0123456789abcdef0123456789abcdef"
$script:commitSha = "0123456789abcdef0123456789abcdef01234567"
$script:packageSha256 = "a" * 64
$script:exactPackageInventorySha256 = "b" * 64

function Assert-TestCondition {
    param(
        [Parameter(Mandatory = $true)][bool]$Condition,
        [Parameter(Mandatory = $true)][string]$Message
    )

    if (-not $Condition) {
        throw "M16 final-artifact evidence self-test failed: $Message"
    }
}

function Write-TestText {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$Value
    )

    $parent = [System.IO.Path]::GetDirectoryName($Path)
    [System.IO.Directory]::CreateDirectory($parent) | Out-Null
    [System.IO.File]::WriteAllText($Path, $Value, $script:utf8NoBom)
}

function Write-TestJson {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)]$Value
    )

    Write-TestText -Path $Path -Value ($Value | ConvertTo-Json -Depth 10)
}

function New-TestSurface {
    param(
        [Parameter(Mandatory = $true)][string]$SurfaceId,
        [int]$Ordinal = 1
    )

    return [pscustomobject][ordered]@{
        SurfaceId = $SurfaceId
        SchemaVersion = 1
        Profile = "M16ReleaseCandidate"
        Result = "clean"
        FileCount = 1 + $Ordinal
        DirectoryCount = $Ordinal
        TotalFileBytes = 1000 + $Ordinal
        InventorySha256 = ([string]([char](96 + $Ordinal))) * 64
        FindingCount = 0
    }
}

function New-TestPackageIntermediate {
    return [pscustomobject][ordered]@{
        SchemaVersion = 1
        Milestone = "M16"
        EvidenceKind = "PackageBoundFinalArtifactSurfaces"
        Result = "passed"
        RunId = $script:runId
        CommitSha = $script:commitSha
        PackageSha256 = $script:packageSha256
        PackageSbomApplicationPackageSha256 = $script:packageSha256
        ScannerProfile = "M16ReleaseCandidate"
        Surfaces = @(
            (New-TestSurface "owned-app-data" 1),
            (New-TestSurface "exact-package" 2),
            (New-TestSurface "support-artifact" 3))
        SameBuildBindingPassed = $true
        RepositoryStable = $true
        RawSurfacesUploaded = $false
        SupportArtifactScope = "ReleaseAcceptanceOnly"
    }
}

function Reset-TestInputs {
    Write-TestJson -Path $script:packagePath -Value (New-TestPackageIntermediate)
    Write-TestJson -Path $script:fullLogPath -Value (New-TestSurface "full-log" 4)
}

function Invoke-TestEvidence {
    return New-WindowsM16FinalArtifactEvidence `
        -PackageIntermediatePath $script:packagePath `
        -FullLogScannerReportPath $script:fullLogPath `
        -InputRoot $script:inputRoot `
        -ExpectedRunId $script:runId `
        -ExpectedCommitSha $script:commitSha `
        -ExpectedPackageSha256 $script:packageSha256 `
        -ExpectedPackageSbomApplicationPackageSha256 $script:packageSha256 `
        -ExpectedExactPackageInventorySha256 $script:exactPackageInventorySha256
}

function Assert-TestRejected {
    param(
        [Parameter(Mandatory = $true)][scriptblock]$Action,
        [Parameter(Mandatory = $true)][string]$ExpectedCode,
        [Parameter(Mandatory = $true)][string]$CaseName
    )

    $message = $null
    try {
        & $Action | Out-Null
    }
    catch {
        $message = $_.Exception.Message
    }
    Assert-TestCondition `
        ($message -ceq "M16FinalArtifactEvidence:$ExpectedCode") `
        "$CaseName returned '$message'."
}

function Invoke-PackageMutationCase {
    param(
        [Parameter(Mandatory = $true)][scriptblock]$Mutation,
        [string]$ExpectedCode = "InputContractInvalid",
        [Parameter(Mandatory = $true)][string]$CaseName
    )

    Reset-TestInputs
    $value = New-TestPackageIntermediate
    & $Mutation $value
    Write-TestJson -Path $script:packagePath -Value $value
    Assert-TestRejected -Action { Invoke-TestEvidence } -ExpectedCode $ExpectedCode -CaseName $CaseName
}

Assert-TestCondition (Test-Path -LiteralPath $script:helperPath -PathType Leaf) `
    "helper is missing."
[System.IO.Directory]::CreateDirectory($script:inputRoot) | Out-Null

try {
    . $script:helperPath
    Assert-TestRejected `
        -Action {
            Assert-WindowsM16FinalPatternString `
                -Value ($script:runId + "`n") `
                -Pattern '^[0-9a-f]{32}$'
        } `
        -ExpectedCode "InputContractInvalid" `
        -CaseName "LF-terminated exact identifier"
    Assert-TestRejected `
        -Action {
            Assert-WindowsM16FinalPatternString `
                -Value ($script:packageSha256 + "`r`n") `
                -Pattern '^[0-9a-f]{64}$'
        } `
        -ExpectedCode "InputContractInvalid" `
        -CaseName "CRLF-terminated exact hash"
    Reset-TestInputs
    $evidence = Invoke-TestEvidence
    Assert-TestCondition ($evidence.SchemaVersion -eq 1) "schema version changed."
    Assert-TestCondition ($evidence.EvidenceKind -ceq "FinalArtifactCanaryScan") `
        "evidence kind changed."
    Assert-TestCondition ($evidence.Result -ceq "passed") "clean result did not pass."
    Assert-TestCondition ($evidence.SurfaceCount -eq 4) "surface count changed."
    Assert-TestCondition `
        ((($evidence.Surfaces | ForEach-Object { $_.SurfaceId }) -join ',') -ceq `
            "owned-app-data,exact-package,support-artifact,full-log") `
        "surface order changed."
    Assert-TestCondition ($evidence.TotalFileCount -eq 14) "file aggregate changed."
    Assert-TestCondition ($evidence.TotalDirectoryCount -eq 10) "directory aggregate changed."
    Assert-TestCondition ($evidence.TotalFileBytes -eq 4010) "byte aggregate changed."
    Assert-TestCondition `
        ($evidence.PackageSha256 -ceq $evidence.PackageSbomApplicationPackageSha256) `
        "package/SBOM binding changed."

    Write-WindowsM16FinalArtifactEvidenceAtomically `
        -Value $evidence `
        -DestinationPath $script:outputPath
    $firstOutput = [System.IO.File]::ReadAllBytes($script:outputPath)
    Assert-TestCondition `
        (-not ($firstOutput.Length -ge 3 -and
               $firstOutput[0] -eq 0xef -and
               $firstOutput[1] -eq 0xbb -and
               $firstOutput[2] -eq 0xbf)) `
        "output contains a UTF-8 BOM."
    $outputText = $script:utf8NoBom.GetString($firstOutput)
    Assert-TestCondition `
        ($outputText.IndexOf($script:fixtureRoot, [StringComparison]::OrdinalIgnoreCase) -lt 0) `
        "output disclosed an input path."
    Assert-TestCondition `
        ($outputText.IndexOf("package-intermediate.json", [StringComparison]::Ordinal) -lt 0) `
        "output disclosed an input file name."
    Write-WindowsM16FinalArtifactEvidenceAtomically `
        -Value $evidence `
        -DestinationPath $script:outputPath
    Assert-TestCondition `
        (@(Get-ChildItem -LiteralPath ([System.IO.Path]::GetDirectoryName($script:outputPath)) `
                -Force | Where-Object { $_.Name -ne "final-evidence.json" }).Count -eq 0) `
        "atomic replacement left a temporary artifact."

    $script:originalRemovePublicationFileOnce =
        ${function:Remove-WindowsM16FinalPublicationFileOnce}
    $script:deleteInjectionMode = ""
    $script:deleteInvocationCount = 0
    $deleteRetryPath = Join-Path $script:fixtureRoot "out\delete-retry.json"
    try {
        function Remove-WindowsM16FinalPublicationFileOnce {
            param(
                [Parameter(Mandatory = $true)][string]$Path
            )

            $script:deleteInvocationCount++
            switch ($script:deleteInjectionMode) {
                "VisibleAfterFirstDelete" {
                    if ($script:deleteInvocationCount -eq 1) {
                        return
                    }
                }
                "SharingViolationOnce" {
                    if ($script:deleteInvocationCount -eq 1) {
                        throw [System.IO.IOException]::new(
                            "synthetic lock violation",
                            [Convert]::ToInt32("80070021", 16))
                    }
                }
                "AccessDenied" {
                    throw [System.UnauthorizedAccessException]::new(
                        "synthetic access denied")
                }
                "PersistentSharingViolation" {
                    throw [System.IO.IOException]::new(
                        "synthetic sharing violation",
                        [Convert]::ToInt32("80070020", 16))
                }
                "DeleteOnFinalSharingViolation" {
                    if ($script:deleteInvocationCount -lt 3) {
                        throw [System.IO.IOException]::new(
                            "synthetic sharing violation",
                            [Convert]::ToInt32("80070020", 16))
                    }
                    & $script:originalRemovePublicationFileOnce -Path $Path
                    throw [System.IO.IOException]::new(
                        "synthetic post-delete sharing violation",
                        [Convert]::ToInt32("80070020", 16))
                }
                "NamedStreamAfterFirstDelete" {
                    if ($script:deleteInvocationCount -eq 1) {
                        Set-Content `
                            -LiteralPath $Path `
                            -Stream "m16-delete-race" `
                            -Value "synthetic named stream" `
                            -Encoding Ascii
                        return
                    }
                }
            }

            & $script:originalRemovePublicationFileOnce -Path $Path
        }

        Write-TestText -Path $deleteRetryPath -Value "delete retry fixture"
        $script:deleteInjectionMode = "VisibleAfterFirstDelete"
        $script:deleteInvocationCount = 0
        Remove-WindowsM16FinalTemporaryArtifact `
            -Path $deleteRetryPath `
            -ExpectedParent ([System.IO.Path]::GetDirectoryName($deleteRetryPath))
        Assert-TestCondition `
            ($script:deleteInvocationCount -eq 2 -and
             -not (Test-Path -LiteralPath $deleteRetryPath)) `
            "visible-after-delete cleanup did not converge in exactly two attempts."

        Write-TestText -Path $deleteRetryPath -Value "delete retry fixture"
        $script:deleteInjectionMode = "SharingViolationOnce"
        $script:deleteInvocationCount = 0
        Remove-WindowsM16FinalTemporaryArtifact `
            -Path $deleteRetryPath `
            -ExpectedParent ([System.IO.Path]::GetDirectoryName($deleteRetryPath))
        Assert-TestCondition `
            ($script:deleteInvocationCount -eq 2 -and
             -not (Test-Path -LiteralPath $deleteRetryPath)) `
            "sharing-violation cleanup did not converge in exactly two attempts."

        Write-TestText -Path $deleteRetryPath -Value "delete retry fixture"
        $script:deleteInjectionMode = "AccessDenied"
        $script:deleteInvocationCount = 0
        $deleteFailure = $null
        try {
            Remove-WindowsM16FinalTemporaryArtifact `
                -Path $deleteRetryPath `
                -ExpectedParent ([System.IO.Path]::GetDirectoryName($deleteRetryPath))
        }
        catch {
            $deleteFailure = $_.Exception
        }
        Assert-TestCondition `
            ($script:deleteInvocationCount -eq 1 -and
             $deleteFailure -is [System.UnauthorizedAccessException] -and
             (Test-Path -LiteralPath $deleteRetryPath)) `
            "non-transient delete failure was retried or suppressed."
        & $script:originalRemovePublicationFileOnce -Path $deleteRetryPath

        Write-TestText -Path $deleteRetryPath -Value "delete retry fixture"
        $script:deleteInjectionMode = "PersistentSharingViolation"
        $script:deleteInvocationCount = 0
        Assert-TestRejected `
            -Action {
                Remove-WindowsM16FinalTemporaryArtifact `
                    -Path $deleteRetryPath `
                    -ExpectedParent ([System.IO.Path]::GetDirectoryName($deleteRetryPath))
            } `
            -ExpectedCode "OutputWriteFailed" `
            -CaseName "persistent sharing violation"
        Assert-TestCondition `
            ($script:deleteInvocationCount -eq 3 -and
             (Test-Path -LiteralPath $deleteRetryPath)) `
            "persistent sharing violation did not fail closed after three attempts."
        & $script:originalRemovePublicationFileOnce -Path $deleteRetryPath

        $script:deleteInjectionMode = "DeleteOnFinalSharingViolation"
        $script:deleteInvocationCount = 0
        Write-WindowsM16FinalArtifactEvidenceAtomically `
            -Value $evidence `
            -DestinationPath $script:outputPath
        $deleteOnFinalOrphans = @(
            Get-ChildItem `
                -LiteralPath ([System.IO.Path]::GetDirectoryName($script:outputPath)) `
                -Force |
                Where-Object { $_.Name -cmatch '\.(?:tmp|bak|rollback)$' })
        Assert-TestCondition `
            ($script:deleteInvocationCount -eq 3 -and
             (Test-Path -LiteralPath $script:outputPath -PathType Leaf) -and
             $deleteOnFinalOrphans.Count -eq 0) `
            "final-attempt backup delete did not preserve a clean committed destination."

        Write-TestText -Path $deleteRetryPath -Value "delete retry fixture"
        $script:deleteInjectionMode = "NamedStreamAfterFirstDelete"
        $script:deleteInvocationCount = 0
        Assert-TestRejected `
            -Action {
                Remove-WindowsM16FinalTemporaryArtifact `
                    -Path $deleteRetryPath `
                    -ExpectedParent ([System.IO.Path]::GetDirectoryName($deleteRetryPath))
            } `
            -ExpectedCode "OutputWriteFailed" `
            -CaseName "named stream introduced between delete attempts"
        Assert-TestCondition `
            ($script:deleteInvocationCount -eq 1 -and
             (Test-Path -LiteralPath $deleteRetryPath)) `
            "named-stream retry preflight invoked deletion again."
        Microsoft.PowerShell.Management\Remove-Item `
            -LiteralPath $deleteRetryPath `
            -Stream "m16-delete-race" `
            -Force `
            -ErrorAction Stop
        & $script:originalRemovePublicationFileOnce -Path $deleteRetryPath
    }
    finally {
        if (Test-Path -LiteralPath $deleteRetryPath) {
            Microsoft.PowerShell.Management\Remove-Item `
                -LiteralPath $deleteRetryPath `
                -Force `
                -ErrorAction Stop
        }
        ${function:Remove-WindowsM16FinalPublicationFileOnce} =
            $script:originalRemovePublicationFileOnce
        Remove-Variable `
            -Name originalRemovePublicationFileOnce, `
                deleteInjectionMode, `
                deleteInvocationCount `
            -Scope Script `
            -ErrorAction SilentlyContinue
    }

    $script:originalNoNamedStreamsForRollback =
        ${function:Assert-WindowsM16FinalNoNamedStreams}
    $script:forcedPostPublicationFailurePath = ""
    $script:forcedPostPublicationFailureOnCall = 0
    $script:forcedPostPublicationFailureCallCount = 0
    $script:forceRollbackRestoreFailure = $false
    $script:forcedRollbackDestinationLock = $null
    try {
        function Assert-WindowsM16FinalNoNamedStreams {
            param(
                [Parameter(Mandatory = $true)][string]$Path,
                [Parameter(Mandatory = $true)][string]$Code
            )

            $resolvedPath = [System.IO.Path]::GetFullPath($Path)
            if (-not [string]::IsNullOrEmpty($script:forcedPostPublicationFailurePath) -and
                $resolvedPath.Equals(
                    $script:forcedPostPublicationFailurePath,
                    [System.StringComparison]::OrdinalIgnoreCase)) {
                $script:forcedPostPublicationFailureCallCount++
                if ($script:forcedPostPublicationFailureCallCount -eq
                        $script:forcedPostPublicationFailureOnCall) {
                    if ($script:forceRollbackRestoreFailure) {
                        $script:forcedRollbackDestinationLock =
                            [System.IO.File]::Open(
                                $resolvedPath,
                                [System.IO.FileMode]::Open,
                                [System.IO.FileAccess]::Read,
                                [System.IO.FileShare]::None)
                    }
                    Fail-WindowsM16FinalArtifactEvidence -Code $Code
                }
            }

            & $script:originalNoNamedStreamsForRollback -Path $Path -Code $Code
        }

        $createRollbackPath = Join-Path `
            $script:fixtureRoot `
            "out\rollback-new-destination.json"
        $script:forcedPostPublicationFailurePath =
            [System.IO.Path]::GetFullPath($createRollbackPath)
        $script:forcedPostPublicationFailureOnCall = 1
        $script:forcedPostPublicationFailureCallCount = 0
        Assert-TestRejected `
            -Action {
                Write-WindowsM16FinalArtifactEvidenceAtomically `
                    -Value $evidence `
                    -DestinationPath $createRollbackPath
            } `
            -ExpectedCode "OutputPathInvalid" `
            -CaseName "new-destination post-publication rollback"
        Assert-TestCondition `
            (-not (Test-Path -LiteralPath $createRollbackPath)) `
            "failed new-destination publication was not rolled back."

        $replaceRollbackPath = Join-Path `
            $script:fixtureRoot `
            "out\rollback-existing-destination.json"
        $originalText = "previous-sanitized-evidence"
        Write-TestText -Path $replaceRollbackPath -Value $originalText
        $originalBytes = [System.IO.File]::ReadAllBytes($replaceRollbackPath)
        $script:forcedPostPublicationFailurePath =
            [System.IO.Path]::GetFullPath($replaceRollbackPath)
        $script:forcedPostPublicationFailureOnCall = 2
        $script:forcedPostPublicationFailureCallCount = 0
        Assert-TestRejected `
            -Action {
                Write-WindowsM16FinalArtifactEvidenceAtomically `
                    -Value $evidence `
                    -DestinationPath $replaceRollbackPath
            } `
            -ExpectedCode "OutputPathInvalid" `
            -CaseName "replacement post-publication rollback"
        $restoredBytes = [System.IO.File]::ReadAllBytes($replaceRollbackPath)
        Assert-TestCondition `
            ([Convert]::ToBase64String($restoredBytes) -ceq
                [Convert]::ToBase64String($originalBytes)) `
            "failed replacement did not restore the previous destination bytes."
        $rollbackOrphans = @(
            Get-ChildItem `
                -LiteralPath ([System.IO.Path]::GetDirectoryName($script:outputPath)) `
                -Force |
                Where-Object { $_.Name -cmatch '\.(?:tmp|bak|rollback)$' })
        Assert-TestCondition `
            ($rollbackOrphans.Count -eq 0) `
            "publication rollback left a temporary artifact."

        $failedRestorePath = Join-Path `
            $script:fixtureRoot `
            "out\rollback-failed-restore.json"
        Write-TestText -Path $failedRestorePath -Value $originalText
        $script:forcedPostPublicationFailurePath =
            [System.IO.Path]::GetFullPath($failedRestorePath)
        $script:forcedPostPublicationFailureOnCall = 2
        $script:forcedPostPublicationFailureCallCount = 0
        $script:forceRollbackRestoreFailure = $true
        try {
            Assert-TestRejected `
                -Action {
                    Write-WindowsM16FinalArtifactEvidenceAtomically `
                        -Value $evidence `
                        -DestinationPath $failedRestorePath
                } `
                -ExpectedCode "OutputRollbackFailed" `
                -CaseName "replacement rollback restore failure"
        }
        finally {
            if ($null -ne $script:forcedRollbackDestinationLock) {
                $script:forcedRollbackDestinationLock.Dispose()
                $script:forcedRollbackDestinationLock = $null
            }
            $script:forceRollbackRestoreFailure = $false
        }
        $failedRestoreLeaf = [System.IO.Path]::GetFileName($failedRestorePath)
        $preservedBackups = @(
            Get-ChildItem `
                -LiteralPath ([System.IO.Path]::GetDirectoryName($failedRestorePath)) `
                -Force |
                Where-Object {
                    $_.Name -clike "$failedRestoreLeaf.*.bak"
                })
        Assert-TestCondition `
            ($preservedBackups.Count -eq 1) `
            "failed restore did not preserve exactly one previous-evidence backup."
        $preservedBytes = [System.IO.File]::ReadAllBytes($preservedBackups[0].FullName)
        Assert-TestCondition `
            ([Convert]::ToBase64String($preservedBytes) -ceq
                [Convert]::ToBase64String($originalBytes)) `
            "failed restore did not preserve the previous destination bytes."
        Remove-Item -LiteralPath $failedRestorePath -Force -ErrorAction Stop
        Remove-Item -LiteralPath $preservedBackups[0].FullName -Force -ErrorAction Stop
    }
    finally {
        if ($null -ne $script:forcedRollbackDestinationLock) {
            $script:forcedRollbackDestinationLock.Dispose()
        }
        ${function:Assert-WindowsM16FinalNoNamedStreams} =
            $script:originalNoNamedStreamsForRollback
        Remove-Variable `
            -Name originalNoNamedStreamsForRollback, `
                forcedPostPublicationFailurePath, `
                forcedPostPublicationFailureOnCall, `
                forcedPostPublicationFailureCallCount, `
                forceRollbackRestoreFailure, `
                forcedRollbackDestinationLock `
            -Scope Script `
            -ErrorAction SilentlyContinue
    }

    Set-Content `
        -LiteralPath $script:outputPath `
        -Stream "m16-self-test" `
        -Value "synthetic named stream" `
        -Encoding Ascii
    Assert-TestRejected `
        -Action {
            Write-WindowsM16FinalArtifactEvidenceAtomically `
                -Value $evidence `
                -DestinationPath $script:outputPath
        } `
        -ExpectedCode "OutputPathInvalid" `
        -CaseName "destination named stream"
    Remove-Item `
        -LiteralPath $script:outputPath `
        -Stream "m16-self-test" `
        -Force

    Reset-TestInputs
    Set-Content `
        -LiteralPath $script:packagePath `
        -Stream "m16-self-test" `
        -Value "synthetic named stream" `
        -Encoding Ascii
    Assert-TestRejected `
        -Action { Invoke-TestEvidence } `
        -ExpectedCode "InputFileInvalid" `
        -CaseName "package intermediate named stream"
    Remove-Item `
        -LiteralPath $script:packagePath `
        -Stream "m16-self-test" `
        -Force

    Reset-TestInputs
    Set-Content `
        -LiteralPath $script:fullLogPath `
        -Stream "m16-self-test" `
        -Value "synthetic named stream" `
        -Encoding Ascii
    Assert-TestRejected `
        -Action { Invoke-TestEvidence } `
        -ExpectedCode "InputFileInvalid" `
        -CaseName "full-log report named stream"
    Remove-Item `
        -LiteralPath $script:fullLogPath `
        -Stream "m16-self-test" `
        -Force

    Reset-TestInputs
    $cleanText = [System.IO.File]::ReadAllText($script:packagePath, $script:utf8NoBom)
    $duplicateText = $cleanText.Replace(
        '"SchemaVersion":  1,',
        '"SchemaVersion":  1,"schemaVersion":  1,')
    Assert-TestCondition ($duplicateText -cne $cleanText) "duplicate mutation was not applied."
    Write-TestText -Path $script:packagePath -Value $duplicateText
    Assert-TestRejected `
        -Action { Invoke-TestEvidence } `
        -ExpectedCode "InputDuplicateProperty" `
        -CaseName "nested/case-insensitive duplicate property"

    Reset-TestInputs
    $plain = [System.IO.File]::ReadAllBytes($script:packagePath)
    $withBom = New-Object byte[] ($plain.Length + 3)
    $withBom[0] = 0xef; $withBom[1] = 0xbb; $withBom[2] = 0xbf
    [System.Array]::Copy($plain, 0, $withBom, 3, $plain.Length)
    [System.IO.File]::WriteAllBytes($script:packagePath, $withBom)
    Assert-TestRejected `
        -Action { Invoke-TestEvidence } `
        -ExpectedCode "InputEncodingInvalid" `
        -CaseName "UTF-8 BOM"

    Invoke-PackageMutationCase -CaseName "extra property" -Mutation {
        param($value)
        Add-Member -InputObject $value -NotePropertyName "Unexpected" -NotePropertyValue $true
    }
    Invoke-PackageMutationCase -CaseName "missing property" -Mutation {
        param($value)
        $value.PSObject.Properties.Remove("Milestone")
    }
    Invoke-PackageMutationCase -CaseName "type coercion" -Mutation {
        param($value)
        $value.Surfaces[0].FileCount = "2"
    }
    Invoke-PackageMutationCase -CaseName "finding result" -Mutation {
        param($value)
        $value.Surfaces[0].Result = "finding"
    }
    Invoke-PackageMutationCase -CaseName "finding count" -Mutation {
        param($value)
        $value.Surfaces[0].FindingCount = 1
    }
    Invoke-PackageMutationCase -CaseName "empty surface" -Mutation {
        param($value)
        $value.Surfaces[0].FileCount = 0
    }
    Invoke-PackageMutationCase -CaseName "surface entry bound" -Mutation {
        param($value)
        $value.Surfaces[0].FileCount = 25001
    }
    Invoke-PackageMutationCase -CaseName "run mismatch" -Mutation {
        param($value)
        $value.RunId = "f" * 32
    }
    Invoke-PackageMutationCase -CaseName "commit mismatch" -Mutation {
        param($value)
        $value.CommitSha = "f" * 40
    }
    Invoke-PackageMutationCase -CaseName "package mismatch" -Mutation {
        param($value)
        $value.PackageSha256 = "b" * 64
    }
    Invoke-PackageMutationCase -CaseName "SBOM mismatch" -Mutation {
        param($value)
        $value.PackageSbomApplicationPackageSha256 = "b" * 64
    }
    Invoke-PackageMutationCase -CaseName "same-build false" -Mutation {
        param($value)
        $value.SameBuildBindingPassed = $false
    }
    Invoke-PackageMutationCase -CaseName "repository unstable" -Mutation {
        param($value)
        $value.RepositoryStable = $false
    }
    Invoke-PackageMutationCase -CaseName "raw surface upload" -Mutation {
        param($value)
        $value.RawSurfacesUploaded = $true
    }
    Invoke-PackageMutationCase -CaseName "surface order mismatch" -Mutation {
        param($value)
        $temporary = $value.Surfaces[0]
        $value.Surfaces[0] = $value.Surfaces[1]
        $value.Surfaces[1] = $temporary
    }

    Reset-TestInputs
    $wrongCaseText = [System.IO.File]::ReadAllText($script:packagePath, $script:utf8NoBom).
        Replace('"RunId":', '"runId":')
    Write-TestText -Path $script:packagePath -Value $wrongCaseText
    Assert-TestRejected `
        -Action { Invoke-TestEvidence } `
        -ExpectedCode "InputContractInvalid" `
        -CaseName "wrong-case property"

    Reset-TestInputs
    $fullLog = New-TestSurface "full-log" 4
    $fullLog.TotalFileBytes = 0
    Write-TestJson -Path $script:fullLogPath -Value $fullLog
    Assert-TestRejected `
        -Action { Invoke-TestEvidence } `
        -ExpectedCode "InputContractInvalid" `
        -CaseName "empty full-log surface"

    Reset-TestInputs
    $fullLog = New-TestSurface "full-log" 4
    $fullLog.InventorySha256 = "A" * 64
    Write-TestJson -Path $script:fullLogPath -Value $fullLog
    Assert-TestRejected `
        -Action { Invoke-TestEvidence } `
        -ExpectedCode "InputContractInvalid" `
        -CaseName "noncanonical inventory hash"

    Assert-TestRejected `
        -Action {
            New-WindowsM16FinalArtifactEvidence `
                -PackageIntermediatePath $script:packagePath `
                -FullLogScannerReportPath $script:fullLogPath `
                -InputRoot $script:inputRoot `
                -ExpectedRunId $script:runId `
                -ExpectedCommitSha $script:commitSha `
                -ExpectedPackageSha256 $script:packageSha256 `
                -ExpectedPackageSbomApplicationPackageSha256 ("b" * 64) `
                -ExpectedExactPackageInventorySha256 $script:exactPackageInventorySha256
        } `
        -ExpectedCode "BindingMismatch" `
        -CaseName "caller package/SBOM mismatch"

    Reset-TestInputs
    Assert-TestRejected `
        -Action {
            New-WindowsM16FinalArtifactEvidence `
                -PackageIntermediatePath $script:packagePath `
                -FullLogScannerReportPath $script:fullLogPath `
                -InputRoot $script:inputRoot `
                -ExpectedRunId $script:runId `
                -ExpectedCommitSha $script:commitSha `
                -ExpectedPackageSha256 $script:packageSha256 `
                -ExpectedPackageSbomApplicationPackageSha256 $script:packageSha256 `
                -ExpectedExactPackageInventorySha256 ("c" * 64)
        } `
        -ExpectedCode "BindingMismatch" `
        -CaseName "independent exact-package inventory mismatch"

    Reset-TestInputs
    Write-TestText `
        -Path $script:packagePath `
        -Value (" " * ((128KB) + 1))
    Assert-TestRejected `
        -Action { Invoke-TestEvidence } `
        -ExpectedCode "InputFileInvalid" `
        -CaseName "per-input byte bound"

    Write-Output "Windows M16 final-artifact evidence self-test passed."
}
finally {
    if (Test-Path -LiteralPath $script:fixtureRoot) {
        $resolved = [System.IO.Path]::GetFullPath($script:fixtureRoot)
        $temporaryRoot = [System.IO.Path]::GetFullPath([System.IO.Path]::GetTempPath())
        if (-not $resolved.StartsWith($temporaryRoot, [System.StringComparison]::OrdinalIgnoreCase) -or
            $resolved.IndexOf("IptvSuite-M16-FinalArtifact-", [System.StringComparison]::Ordinal) -lt 0) {
            throw "M16 final-artifact evidence self-test refused unsafe cleanup."
        }
        Remove-Item -LiteralPath $resolved -Recurse -Force
    }
}
