[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

if ($PSVersionTable.PSEdition -cne "Desktop" -or
    $PSVersionTable.PSVersion.Major -ne 5 -or
    $PSVersionTable.PSVersion.Minor -ne 1) {
    throw "M15 install-root audit self-test requires Windows PowerShell 5.1."
}

$script:utf8NoBom = New-Object System.Text.UTF8Encoding($false, $true)
$script:repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot "..\..\..\.."))
$script:helperPath = Join-Path $script:repositoryRoot "eng\WindowsPackageInstallRootAudit.ps1"
$script:runId = [Guid]::NewGuid().ToString("N")
$script:fixtureRoot = Join-Path `
    ([System.IO.Path]::GetTempPath()) `
    "IptvSuite-M15-InstallRootAudit-$($script:runId)"
$script:junctionPaths = [System.Collections.Generic.List[string]]::new()

function Assert-TestCondition {
    param(
        [Parameter(Mandatory = $true)]
        [bool]$Condition,

        [Parameter(Mandatory = $true)]
        [string]$Message
    )

    if (-not $Condition) {
        throw "M15 install-root audit self-test failed: $Message"
    }
}

function New-TestRoot {
    param(
        [Parameter(Mandatory = $true)]
        [ValidatePattern('\A[a-z0-9-]+\z')]
        [string]$Name
    )

    $path = Join-Path $script:fixtureRoot $Name
    [System.IO.Directory]::CreateDirectory($path) | Out-Null
    return [System.IO.Path]::GetFullPath($path).TrimEnd(
        [System.IO.Path]::DirectorySeparatorChar,
        [System.IO.Path]::AltDirectorySeparatorChar)
}

function Write-TestFile {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Root,

        [Parameter(Mandatory = $true)]
        [string]$RelativePath,

        [Parameter(Mandatory = $true)]
        [string]$Value
    )

    $path = Join-Path $Root $RelativePath
    $parent = [System.IO.Path]::GetDirectoryName($path)
    [System.IO.Directory]::CreateDirectory($parent) | Out-Null
    [System.IO.File]::WriteAllText($path, $Value, $script:utf8NoBom)
    return $path
}

function Assert-ExpectedAuditFailure {
    param(
        [Parameter(Mandatory = $true)]
        [scriptblock]$Action,

        [Parameter(Mandatory = $true)]
        [string]$Code
    )

    $expectedMessage = "PackageInstallRootAudit:$Code"
    $observedMessage = $null
    try {
        & $Action
    }
    catch {
        $observedMessage = $_.Exception.Message
    }

    Assert-TestCondition `
        ($observedMessage -ceq $expectedMessage) `
        "expected '$expectedMessage', received '$observedMessage'."
    foreach ($sensitiveValue in @(
            $script:fixtureRoot,
            [Environment]::UserName,
            [Environment]::MachineName)) {
        if (-not [string]::IsNullOrWhiteSpace($sensitiveValue)) {
            Assert-TestCondition `
                ($observedMessage.IndexOf(
                        $sensitiveValue,
                        [System.StringComparison]::OrdinalIgnoreCase) -lt 0) `
                "failure message disclosed sensitive context."
        }
    }
}

function Assert-CleanAuditResult {
    param(
        [Parameter(Mandatory = $true)]
        [object]$Result
    )

    $expectedProperties = @(
        "SchemaVersion",
        "Scope",
        "ExcludedEntryCount",
        "BaselineEntryCount",
        "BaselineFileCount",
        "BaselineTotalBytes",
        "BaselineManifestSha256",
        "FinalEntryCount",
        "FinalFileCount",
        "FinalTotalBytes",
        "FinalManifestSha256",
        "MutationEventCount",
        "WatcherOverflow",
        "SnapshotEquivalent",
        "RuntimeWriteAuditPassed")
    $actualProperties = @($Result.PSObject.Properties.Name)
    Assert-TestCondition `
        ($actualProperties.Count -eq $expectedProperties.Count) `
        "sanitized result property count changed."
    for ($index = 0; $index -lt $expectedProperties.Count; $index++) {
        Assert-TestCondition `
            ($actualProperties[$index] -ceq $expectedProperties[$index]) `
            "sanitized result property order changed."
    }

    Assert-TestCondition ($Result.SchemaVersion -eq 1) "schema version changed."
    Assert-TestCondition ($Result.SchemaVersion -is [int]) "schema version type changed."
    Assert-TestCondition `
        ($Result.Scope -ceq "ExactRegisteredProductPackageInstallLocation") `
        "audit scope changed."
    Assert-TestCondition ($Result.ExcludedEntryCount -eq 0) "an install-root exclusion was introduced."
    Assert-TestCondition ($Result.ExcludedEntryCount -is [int]) "excluded count type changed."
    Assert-TestCondition `
        ($Result.BaselineEntryCount -eq $Result.FinalEntryCount -and
         $Result.BaselineFileCount -eq $Result.FinalFileCount -and
         $Result.BaselineTotalBytes -eq $Result.FinalTotalBytes -and
         $Result.BaselineManifestSha256 -ceq $Result.FinalManifestSha256) `
        "clean snapshots differ."
    Assert-TestCondition `
        ($Result.BaselineManifestSha256 -cmatch '\A[0-9a-f]{64}\z') `
        "manifest digest is not canonical lowercase SHA-256."
    Assert-TestCondition ($Result.MutationEventCount -eq 0) "clean audit observed a mutation."
    Assert-TestCondition `
        ($Result.WatcherOverflow -is [bool] -and -not $Result.WatcherOverflow) `
        "watcher overflow contract changed."
    Assert-TestCondition `
        ($Result.SnapshotEquivalent -is [bool] -and $Result.SnapshotEquivalent) `
        "snapshot equivalence contract changed."
    Assert-TestCondition `
        ($Result.RuntimeWriteAuditPassed -is [bool] -and $Result.RuntimeWriteAuditPassed) `
        "runtime audit result contract changed."

    $resultJson = $Result | ConvertTo-Json -Depth 4 -Compress
    foreach ($sensitiveValue in @(
            $script:fixtureRoot,
            [Environment]::UserName,
            [Environment]::MachineName)) {
        if (-not [string]::IsNullOrWhiteSpace($sensitiveValue)) {
            Assert-TestCondition `
                ($resultJson.IndexOf(
                        $sensitiveValue,
                        [System.StringComparison]::OrdinalIgnoreCase) -lt 0) `
                "sanitized result disclosed sensitive context."
        }
    }
}

function Assert-SanitizedAuditHandle {
    param(
        [Parameter(Mandatory = $true)]
        [object]$Audit
    )

    $properties = @($Audit.PSObject.Properties.Name)
    Assert-TestCondition `
        ($properties.Count -eq 1 -and $properties[0] -ceq "Token") `
        "public audit handle exposed internal state."
    Assert-TestCondition `
        ($Audit.Token -is [string] -and $Audit.Token -cmatch '\A[0-9a-f]{32}\z') `
        "public audit handle token contract changed."
    $serialized = $Audit | ConvertTo-Json -Depth 4 -Compress
    foreach ($sensitiveValue in @(
            $script:fixtureRoot,
            [Environment]::UserName,
            [Environment]::MachineName)) {
        if (-not [string]::IsNullOrWhiteSpace($sensitiveValue)) {
            Assert-TestCondition `
                ($serialized.IndexOf(
                        $sensitiveValue,
                        [System.StringComparison]::OrdinalIgnoreCase) -lt 0) `
                "public audit handle disclosed sensitive context."
        }
    }
}

function Wait-TestAuditMutation {
    param(
        [Parameter(Mandatory = $true)]
        [object]$Audit
    )

    for ($attempt = 0; $attempt -lt 100; $attempt++) {
        $internalState = $script:packageInstallRootAuditHandles[$Audit.Token]
        if ($null -eq $internalState) {
            throw "M15 install-root audit self-test failed: audit state disappeared."
        }
        $state = $internalState.Collector.GetStateAfterBarrier(10)
        if ($state.Overflowed -or $state.EventCount -gt 0) {
            return
        }
        Start-Sleep -Milliseconds 10
    }

    throw "M15 install-root audit self-test failed: watcher did not observe mutation."
}

if (-not (Test-Path -LiteralPath $script:helperPath -PathType Leaf)) {
    throw "M15 install-root audit self-test failed: helper is missing."
}

. $script:helperPath

$primaryFailure = $null
$cleanupFailures = [System.Collections.Generic.List[string]]::new()
try {
    try {
        [System.IO.Directory]::CreateDirectory($script:fixtureRoot) | Out-Null

    $cleanA = New-TestRoot -Name "clean-a"
    [System.IO.Directory]::CreateDirectory((Join-Path $cleanA "empty")) | Out-Null
    Write-TestFile -Root $cleanA -RelativePath "zeta\second.bin" -Value "second" | Out-Null
    Write-TestFile -Root $cleanA -RelativePath "alpha\first.txt" -Value "first" | Out-Null
    $cleanAuditA = Start-WindowsPackageInstallRootAudit -RootPath $cleanA
    Assert-SanitizedAuditHandle -Audit $cleanAuditA
    $cleanResultA = Complete-WindowsPackageInstallRootAudit -Audit $cleanAuditA
    Assert-CleanAuditResult -Result $cleanResultA
    Assert-TestCondition `
        ($cleanResultA.BaselineEntryCount -eq 6 -and
         $cleanResultA.BaselineFileCount -eq 2 -and
         $cleanResultA.BaselineTotalBytes -eq 11) `
        "clean inventory scalar values changed."

    $cleanB = New-TestRoot -Name "clean-b"
    Write-TestFile -Root $cleanB -RelativePath "alpha\first.txt" -Value "first" | Out-Null
    Write-TestFile -Root $cleanB -RelativePath "zeta\second.bin" -Value "second" | Out-Null
    [System.IO.Directory]::CreateDirectory((Join-Path $cleanB "empty")) | Out-Null
    $cleanAuditB = Start-WindowsPackageInstallRootAudit -RootPath $cleanB
    $cleanResultB = Complete-WindowsPackageInstallRootAudit -Audit $cleanAuditB
    Assert-CleanAuditResult -Result $cleanResultB
    Assert-TestCondition `
        ($cleanResultA.BaselineManifestSha256 -ceq
            $cleanResultB.BaselineManifestSha256) `
        "inventory digest depends on root path or creation order."

    $cultureA = New-TestRoot -Name "culture-a"
    $unicodeCultureName = ([string][char]0x0130) + "stanbul.txt"
    Write-TestFile -Root $cultureA -RelativePath $unicodeCultureName -Value "one" | Out-Null
    Write-TestFile -Root $cultureA -RelativePath "alpha.txt" -Value "two" | Out-Null
    $cultureB = New-TestRoot -Name "culture-b"
    Write-TestFile -Root $cultureB -RelativePath "alpha.txt" -Value "two" | Out-Null
    Write-TestFile -Root $cultureB -RelativePath $unicodeCultureName -Value "one" | Out-Null
    $originalCulture = [System.Threading.Thread]::CurrentThread.CurrentCulture
    try {
        [System.Threading.Thread]::CurrentThread.CurrentCulture =
            [System.Globalization.CultureInfo]::GetCultureInfo("tr-TR")
        $cultureAuditA = Start-WindowsPackageInstallRootAudit -RootPath $cultureA
        $cultureResultA = Complete-WindowsPackageInstallRootAudit -Audit $cultureAuditA
        [System.Threading.Thread]::CurrentThread.CurrentCulture =
            [System.Globalization.CultureInfo]::GetCultureInfo("en-US")
        $cultureAuditB = Start-WindowsPackageInstallRootAudit -RootPath $cultureB
        $cultureResultB = Complete-WindowsPackageInstallRootAudit -Audit $cultureAuditB
    }
    finally {
        [System.Threading.Thread]::CurrentThread.CurrentCulture = $originalCulture
    }
    Assert-TestCondition `
        ($cultureResultA.BaselineManifestSha256 -ceq
            $cultureResultB.BaselineManifestSha256) `
        "inventory digest depends on current culture."

    $persistentRoot = New-TestRoot -Name "persistent"
    $persistentFile = Write-TestFile `
        -Root $persistentRoot `
        -RelativePath "payload.bin" `
        -Value "baseline"
    $persistentAudit = Start-WindowsPackageInstallRootAudit -RootPath $persistentRoot
    [System.IO.File]::WriteAllText($persistentFile, "mutated!", $script:utf8NoBom)
    Assert-ExpectedAuditFailure `
        -Code "SnapshotMismatch" `
        -Action { Complete-WindowsPackageInstallRootAudit -Audit $persistentAudit }

    $transientRoot = New-TestRoot -Name "transient"
    Write-TestFile -Root $transientRoot -RelativePath "payload.bin" -Value "stable" | Out-Null
    $transientAudit = Start-WindowsPackageInstallRootAudit -RootPath $transientRoot
    $transientPath = Join-Path $transientRoot "transient.bin"
    [System.IO.File]::WriteAllText($transientPath, "short-lived", $script:utf8NoBom)
    Wait-TestAuditMutation -Audit $transientAudit
    [System.IO.File]::Delete($transientPath)
    Assert-ExpectedAuditFailure `
        -Code "MutationObserved" `
        -Action { Complete-WindowsPackageInstallRootAudit -Audit $transientAudit }

    $adsRoot = New-TestRoot -Name "ads"
    $adsFile = Write-TestFile -Root $adsRoot -RelativePath "payload.bin" -Value "default"
    $adsCreated = $false
    try {
        Set-Content `
            -LiteralPath $adsFile `
            -Stream "hidden" `
            -Value "hidden" `
            -Encoding UTF8 `
            -ErrorAction Stop
        $adsCreated = $true
    }
    catch {
        $drive = [System.IO.DriveInfo]::new([System.IO.Path]::GetPathRoot($adsRoot))
        Assert-TestCondition `
            ($drive.DriveFormat -cne "NTFS") `
            "ADS creation failed on NTFS."
        Write-Host "M15 install-root audit self-test: ADS check skipped on non-NTFS."
    }
    if ($adsCreated) {
        Assert-ExpectedAuditFailure `
            -Code "AlternateDataStreamDetected" `
            -Action { Start-WindowsPackageInstallRootAudit -RootPath $adsRoot }

        Remove-Item -LiteralPath $adsFile -Stream "hidden" -ErrorAction Stop
        $transientAdsAudit = Start-WindowsPackageInstallRootAudit -RootPath $adsRoot
        Set-Content `
            -LiteralPath $adsFile `
            -Stream "temporary" `
            -Value "temporary" `
            -Encoding UTF8 `
            -ErrorAction Stop
        Wait-TestAuditMutation -Audit $transientAdsAudit
        Remove-Item -LiteralPath $adsFile -Stream "temporary" -ErrorAction Stop
        Assert-ExpectedAuditFailure `
            -Code "MutationObserved" `
            -Action {
                Complete-WindowsPackageInstallRootAudit -Audit $transientAdsAudit
            }

        $directoryAdsRoot = New-TestRoot -Name "directory-ads"
        $directoryAdsChild = Join-Path $directoryAdsRoot "child"
        [System.IO.Directory]::CreateDirectory($directoryAdsChild) | Out-Null
        $directoryAdsAudit =
            Start-WindowsPackageInstallRootAudit -RootPath $directoryAdsRoot
        Set-Content `
            -LiteralPath $directoryAdsChild `
            -Stream "retained" `
            -Value "retained" `
            -Encoding UTF8 `
            -ErrorAction Stop
        Assert-ExpectedAuditFailure `
            -Code "AlternateDataStreamDetected" `
            -Action {
                Complete-WindowsPackageInstallRootAudit -Audit $directoryAdsAudit
            }

        Remove-Item `
            -LiteralPath $directoryAdsChild `
            -Stream "retained" `
            -ErrorAction Stop
        $transientDirectoryAdsAudit =
            Start-WindowsPackageInstallRootAudit -RootPath $directoryAdsRoot
        Set-Content `
            -LiteralPath $directoryAdsChild `
            -Stream "temporary" `
            -Value "temporary" `
            -Encoding UTF8 `
            -ErrorAction Stop
        Wait-TestAuditMutation -Audit $transientDirectoryAdsAudit
        Remove-Item `
            -LiteralPath $directoryAdsChild `
            -Stream "temporary" `
            -ErrorAction Stop
        Assert-ExpectedAuditFailure `
            -Code "MutationObserved" `
            -Action {
                Complete-WindowsPackageInstallRootAudit `
                    -Audit $transientDirectoryAdsAudit
            }
    }

    $junctionTarget = New-TestRoot -Name "junction-target"
    Write-TestFile -Root $junctionTarget -RelativePath "target.txt" -Value "target" | Out-Null
    $junctionRoot = Join-Path $script:fixtureRoot "junction-root"
    $junctionCreated = $false
    try {
        New-Item `
            -ItemType Junction `
            -Path $junctionRoot `
            -Target $junctionTarget `
            -ErrorAction Stop | Out-Null
        $junctionCreated = $true
        $script:junctionPaths.Add($junctionRoot) | Out-Null
    }
    catch {
        $drive = [System.IO.DriveInfo]::new(
            [System.IO.Path]::GetPathRoot($junctionTarget))
        Assert-TestCondition `
            ($drive.DriveFormat -cne "NTFS") `
            "junction creation failed on NTFS."
        Write-Host "M15 install-root audit self-test: reparse check skipped on non-NTFS."
    }
    if ($junctionCreated) {
        Assert-ExpectedAuditFailure `
            -Code "RootReparsePoint" `
            -Action {
                Start-WindowsPackageInstallRootAudit `
                    -RootPath ([System.IO.Path]::GetFullPath($junctionRoot))
            }

        $nestedRoot = New-TestRoot -Name "nested-junction"
        $nestedJunction = Join-Path $nestedRoot "redirect"
        New-Item `
            -ItemType Junction `
            -Path $nestedJunction `
            -Target $junctionTarget `
            -ErrorAction Stop | Out-Null
        $script:junctionPaths.Add($nestedJunction) | Out-Null
        Assert-ExpectedAuditFailure `
            -Code "ReparsePointDetected" `
            -Action {
                Start-WindowsPackageInstallRootAudit -RootPath $nestedRoot
            }

        [System.IO.Directory]::Delete($nestedJunction, $false)
        $script:junctionPaths.Remove($nestedJunction) | Out-Null
        [System.IO.Directory]::CreateDirectory($nestedJunction) | Out-Null
        Write-TestFile `
            -Root $nestedJunction `
            -RelativePath "before.txt" `
            -Value "before" | Out-Null
        $swapAudit = Start-WindowsPackageInstallRootAudit -RootPath $nestedRoot
        [System.IO.Directory]::Delete($nestedJunction, $true)
        New-Item `
            -ItemType Junction `
            -Path $nestedJunction `
            -Target $junctionTarget `
            -ErrorAction Stop | Out-Null
        $script:junctionPaths.Add($nestedJunction) | Out-Null
        Assert-ExpectedAuditFailure `
            -Code "ReparsePointDetected" `
            -Action {
                Complete-WindowsPackageInstallRootAudit -Audit $swapAudit
            }
    }

    $replacementRoot = New-TestRoot -Name "root-replacement"
    Write-TestFile `
        -Root $replacementRoot `
        -RelativePath "payload.bin" `
        -Value "identical" | Out-Null
    $replacementAudit =
        Start-WindowsPackageInstallRootAudit -RootPath $replacementRoot
    $movedReplacementRoot = Join-Path $script:fixtureRoot "root-replacement-original"
    [System.IO.Directory]::Move($replacementRoot, $movedReplacementRoot)
    [System.IO.Directory]::CreateDirectory($replacementRoot) | Out-Null
    Write-TestFile `
        -Root $replacementRoot `
        -RelativePath "payload.bin" `
        -Value "identical" | Out-Null
    Assert-ExpectedAuditFailure `
        -Code "SnapshotMismatch" `
        -Action {
            Complete-WindowsPackageInstallRootAudit -Audit $replacementAudit
        }

    $entryBoundRoot = New-TestRoot -Name "entry-bound"
    Write-TestFile -Root $entryBoundRoot -RelativePath "one" -Value "1" | Out-Null
    Write-TestFile -Root $entryBoundRoot -RelativePath "two" -Value "2" | Out-Null
    Assert-ExpectedAuditFailure `
        -Code "EntryCountExceeded" `
        -Action {
            Start-WindowsPackageInstallRootAudit `
                -RootPath $entryBoundRoot `
                -MaximumEntryCount 2
        }
    Assert-ExpectedAuditFailure `
        -Code "FileCountExceeded" `
        -Action {
            Start-WindowsPackageInstallRootAudit `
                -RootPath $entryBoundRoot `
                -MaximumFileCount 1
        }
    $exactBoundAudit = Start-WindowsPackageInstallRootAudit `
        -RootPath $entryBoundRoot `
        -MaximumEntryCount 3 `
        -MaximumFileCount 2 `
        -MaximumDepth 1 `
        -MaximumTotalBytes 2 `
        -MaximumFileBytes 1 `
        -MaximumRelativePathUtf8Bytes 3
    $exactBoundResult =
        Complete-WindowsPackageInstallRootAudit -Audit $exactBoundAudit
    Assert-TestCondition `
        ($exactBoundResult.BaselineEntryCount -eq 3 -and
         $exactBoundResult.BaselineFileCount -eq 2 -and
         $exactBoundResult.BaselineTotalBytes -eq 2) `
        "exact limits were not accepted."

    $depthBoundRoot = New-TestRoot -Name "depth-bound"
    Write-TestFile -Root $depthBoundRoot -RelativePath "one\two\deep.txt" -Value "deep" | Out-Null
    Assert-ExpectedAuditFailure `
        -Code "DepthExceeded" `
        -Action {
            Start-WindowsPackageInstallRootAudit `
                -RootPath $depthBoundRoot `
                -MaximumDepth 1
        }

    $sizeBoundRoot = New-TestRoot -Name "size-bound"
    Write-TestFile -Root $sizeBoundRoot -RelativePath "payload.bin" -Value "12345" | Out-Null
    Assert-ExpectedAuditFailure `
        -Code "FileSizeExceeded" `
        -Action {
            Start-WindowsPackageInstallRootAudit `
                -RootPath $sizeBoundRoot `
                -MaximumFileBytes 4
        }
    Assert-ExpectedAuditFailure `
        -Code "TotalSizeExceeded" `
        -Action {
            Start-WindowsPackageInstallRootAudit `
                -RootPath $sizeBoundRoot `
                -MaximumTotalBytes 4
        }

    $pathBoundRoot = New-TestRoot -Name "path-bound"
    Write-TestFile -Root $pathBoundRoot -RelativePath "long-name.bin" -Value "x" | Out-Null
    Assert-ExpectedAuditFailure `
        -Code "RelativePathTooLong" `
        -Action {
            Start-WindowsPackageInstallRootAudit `
                -RootPath $pathBoundRoot `
                -MaximumRelativePathUtf8Bytes 4
        }
    Assert-ExpectedAuditFailure `
        -Code "ManifestSizeExceeded" `
        -Action {
            Start-WindowsPackageInstallRootAudit `
                -RootPath $pathBoundRoot `
                -MaximumManifestBytes 64
        }

    $utf8BoundRoot = New-TestRoot -Name "utf8-bound"
    $utf8FourByteName = ([string][char]0x00e9) + ([string][char]0x00e9)
    Write-TestFile -Root $utf8BoundRoot -RelativePath $utf8FourByteName -Value "x" | Out-Null
    Assert-ExpectedAuditFailure `
        -Code "RelativePathTooLong" `
        -Action {
            Start-WindowsPackageInstallRootAudit `
                -RootPath $utf8BoundRoot `
                -MaximumRelativePathUtf8Bytes 3
        }
    $utf8ExactAudit = Start-WindowsPackageInstallRootAudit `
        -RootPath $utf8BoundRoot `
        -MaximumRelativePathUtf8Bytes 4
    Stop-WindowsPackageInstallRootAudit -Audit $utf8ExactAudit

    Assert-ExpectedAuditFailure `
        -Code "RootInvalid" `
        -Action { Start-WindowsPackageInstallRootAudit -RootPath "relative-root" }
    Assert-ExpectedAuditFailure `
        -Code "RootMissing" `
        -Action {
            Start-WindowsPackageInstallRootAudit `
                -RootPath ([System.IO.Path]::GetFullPath(
                    (Join-Path $script:fixtureRoot "missing-root")))
        }

    $fileRoot = Write-TestFile -Root $script:fixtureRoot -RelativePath "not-a-root.bin" -Value "file"
    Assert-ExpectedAuditFailure `
        -Code "RootNotDirectory" `
        -Action { Start-WindowsPackageInstallRootAudit -RootPath $fileRoot }
    Assert-ExpectedAuditFailure `
        -Code "RootNotCanonical" `
        -Action {
            Start-WindowsPackageInstallRootAudit `
                -RootPath (Join-Path $cleanA "..\clean-a")
        }
    Assert-ExpectedAuditFailure `
        -Code "RootTooBroad" `
        -Action {
            Start-WindowsPackageInstallRootAudit `
                -RootPath ([System.IO.Path]::GetPathRoot($script:fixtureRoot))
        }

    $cleanupRoot = New-TestRoot -Name "cleanup"
    Write-TestFile -Root $cleanupRoot -RelativePath "payload.bin" -Value "cleanup" | Out-Null
    $cleanupAudit = Start-WindowsPackageInstallRootAudit -RootPath $cleanupRoot
    Stop-WindowsPackageInstallRootAudit -Audit $cleanupAudit
    Stop-WindowsPackageInstallRootAudit -Audit $cleanupAudit
    Assert-TestCondition `
        (-not $script:packageInstallRootAuditHandles.ContainsKey($cleanupAudit.Token)) `
        "successful Stop retained internal audit state."

    $faultToken = [Guid]::NewGuid().ToString("N")
    $faultCollector = [pscustomobject]@{ DisposeAttempts = 0 }
    $faultCollector | Add-Member -MemberType ScriptMethod -Name Dispose -Value {
        $this.DisposeAttempts++
        if ($this.DisposeAttempts -eq 1) {
            throw "InjectedDisposeFailure"
        }
    }
    $faultHandle = [pscustomobject]@{
        PSTypeName = "IptvSuite.PackageInstallRootAudit.Handle"
        Token = $faultToken
    }
    $script:packageInstallRootAuditHandles.Add($faultToken, [pscustomobject]@{
        Collector = $faultCollector
    })
    Assert-ExpectedAuditFailure `
        -Code "WatcherDisposeFailed" `
        -Action { Stop-WindowsPackageInstallRootAudit -Audit $faultHandle }
    Assert-TestCondition `
        ($script:packageInstallRootAuditHandles.ContainsKey($faultToken)) `
        "failed cleanup discarded the retryable audit token."
    Stop-WindowsPackageInstallRootAudit -Audit $faultHandle
    Assert-TestCondition `
        (-not $script:packageInstallRootAuditHandles.ContainsKey($faultToken) -and
         $faultCollector.DisposeAttempts -eq 2) `
        "cleanup retry did not dispose and release audit state."

    }
    catch {
        $primaryFailure = $_
    }
}
finally {
    foreach ($auditState in @($script:packageInstallRootAuditHandles.Values)) {
        try {
            $auditState.Collector.Dispose()
        }
        catch {
            $cleanupFailures.Add("WatcherCleanupFailed") | Out-Null
        }
    }
    $script:packageInstallRootAuditHandles.Clear()

    foreach ($junctionPath in $script:junctionPaths) {
        try {
            if (Test-Path -LiteralPath $junctionPath) {
                [System.IO.Directory]::Delete($junctionPath, $false)
            }
        }
        catch {
            $cleanupFailures.Add("JunctionCleanupFailed") | Out-Null
        }
    }

    $resolvedFixtureRoot = [System.IO.Path]::GetFullPath($script:fixtureRoot)
    $resolvedTempRoot = [System.IO.Path]::GetFullPath([System.IO.Path]::GetTempPath())
    $expectedLeaf = "IptvSuite-M15-InstallRootAudit-$($script:runId)"
    if (-not $resolvedFixtureRoot.StartsWith(
            $resolvedTempRoot,
            [System.StringComparison]::OrdinalIgnoreCase) -or
        [System.IO.Path]::GetFileName($resolvedFixtureRoot) -cne $expectedLeaf) {
        $cleanupFailures.Add("UnsafeCleanupRefused") | Out-Null
    }
    elseif (Test-Path -LiteralPath $resolvedFixtureRoot) {
        try {
            Remove-Item -LiteralPath $resolvedFixtureRoot -Recurse -Force -ErrorAction Stop
        }
        catch {
            $cleanupFailures.Add("FixtureCleanupFailed") | Out-Null
        }
    }
}

if ($null -ne $primaryFailure -or $cleanupFailures.Count -ne 0) {
    $primaryStatus = if ($null -eq $primaryFailure) {
        "none"
    }
    else {
        $primaryFailure.Exception.Message
    }
    $cleanupStatus = if ($cleanupFailures.Count -eq 0) {
        "none"
    }
    else {
        [string]::Join(",", $cleanupFailures)
    }
    throw "M15 install-root audit self-test failed: primary=$primaryStatus; cleanup=$cleanupStatus."
}

Write-Host "M15 install-root audit PowerShell 5.1 self-test passed."
