[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$script:windowsFinalExpectedSdk = "10.0.302"
$script:windowsFinalMaximumLogBytes = 20MB
$script:windowsFinalMaximumScannerOutputBytes = 128KB
$script:windowsFinalMaximumCleanupEntries = 1024
$script:windowsFinalMaximumCleanupBytes = 64MB
$script:windowsFinalPackageTimeoutMilliseconds = 2700000
$script:windowsFinalScannerTimeoutMilliseconds = 600000
$script:windowsFinalUtf8NoBom = New-Object System.Text.UTF8Encoding($false)
$script:windowsFinalPackageIdentityMutexName =
    "Global\IptvSuite.PackageSmoke.IptvSuite.LocalDev.6f0d9a64"

function Fail-WindowsFinalArtifactCanaryScan {
    param(
        [Parameter(Mandatory = $true)]
        [ValidatePattern('\A[A-Za-z][A-Za-z0-9]+\z')]
        [string]$Code
    )

    throw "WindowsFinalArtifactCanaryScan:$Code"
}

function Assert-WindowsFinalCondition {
    param(
        [Parameter(Mandatory = $true)][bool]$Condition,
        [Parameter(Mandatory = $true)][string]$Code
    )

    if (-not $Condition) {
        Fail-WindowsFinalArtifactCanaryScan -Code $Code
    }
}

function Test-WindowsFinalPathContainedByRoot {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$Root
    )

    $rootWithSeparator = $Root.TrimEnd(
        [System.IO.Path]::DirectorySeparatorChar,
        [System.IO.Path]::AltDirectorySeparatorChar) +
        [System.IO.Path]::DirectorySeparatorChar
    return $Path.StartsWith(
        $rootWithSeparator,
        [System.StringComparison]::OrdinalIgnoreCase)
}

function Assert-WindowsFinalNoAlternateDataStream {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$Code
    )

    try {
        $root = [System.IO.Path]::GetPathRoot($Path)
        Assert-WindowsFinalCondition `
            (-not [string]::IsNullOrWhiteSpace($root)) $Code
        Assert-WindowsFinalCondition `
            ($Path.Substring($root.Length).IndexOf(':') -lt 0) $Code
    }
    catch {
        if ($_.Exception.Message -cmatch
            '\AWindowsFinalArtifactCanaryScan:[A-Za-z][A-Za-z0-9]+\z') {
            throw $_.Exception.Message
        }
        Fail-WindowsFinalArtifactCanaryScan -Code $Code
    }
}

function Assert-WindowsFinalNoNamedStreams {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$Code
    )

    try {
        $fullPath = [System.IO.Path]::GetFullPath($Path)
        $item = Get-Item -LiteralPath $fullPath -Force -ErrorAction Stop
        $streams = @(Get-Item `
                -LiteralPath $fullPath `
                -Stream * `
                -Force `
                -ErrorAction Stop)
        if ($item.PSIsContainer) {
            Assert-WindowsFinalCondition ($streams.Count -eq 0) $Code
        }
        else {
            Assert-WindowsFinalCondition ($streams.Count -eq 1) $Code
            $stream = $streams[0]
            Assert-WindowsFinalCondition `
                ($stream.Stream -is [string] -and
                 $stream.Stream -ceq ':$DATA' -and
                 ($stream.Length -is [int64] -or $stream.Length -is [int32]) -and
                 [long]$stream.Length -eq [long]$item.Length) `
                $Code
        }
    }
    catch {
        if ($_.Exception.Message -cmatch
            '\AWindowsFinalArtifactCanaryScan:[A-Za-z][A-Za-z0-9]+\z') {
            throw $_.Exception.Message
        }
        Fail-WindowsFinalArtifactCanaryScan -Code $Code
    }
}

function Assert-WindowsFinalNoReparseDirectoryChain {
    param(
        [Parameter(Mandatory = $true)][string]$DirectoryPath,
        [Parameter(Mandatory = $true)][string]$Code
    )

    try {
        $fullDirectory = [System.IO.Path]::GetFullPath($DirectoryPath)
        Assert-WindowsFinalNoAlternateDataStream -Path $fullDirectory -Code $Code
        $root = [System.IO.Path]::GetPathRoot($fullDirectory)
        Assert-WindowsFinalCondition `
            (Test-Path -LiteralPath $root -PathType Container) $Code

        $rootItem = Get-Item -LiteralPath $root -Force -ErrorAction Stop
        Assert-WindowsFinalCondition `
            ($rootItem.PSIsContainer -and
             (($rootItem.Attributes -band
                    [System.IO.FileAttributes]::ReparsePoint) -eq 0)) $Code

        $rootWithSeparator = $root.TrimEnd(
            [System.IO.Path]::DirectorySeparatorChar,
            [System.IO.Path]::AltDirectorySeparatorChar) +
            [System.IO.Path]::DirectorySeparatorChar
        $relative = $fullDirectory.Substring($rootWithSeparator.Length)
        $current = $root
        foreach ($part in @($relative.Split(
                    @('\', '/'),
                    [System.StringSplitOptions]::RemoveEmptyEntries))) {
            $current = Join-Path $current $part
            if (Test-Path -LiteralPath $current) {
                $item = Get-Item -LiteralPath $current -Force -ErrorAction Stop
                Assert-WindowsFinalCondition `
                    ($item.PSIsContainer -and
                     (($item.Attributes -band
                            [System.IO.FileAttributes]::ReparsePoint) -eq 0)) $Code
                Assert-WindowsFinalNoNamedStreams -Path $current -Code $Code
            }
        }
    }
    catch {
        if ($_.Exception.Message -cmatch
            '\AWindowsFinalArtifactCanaryScan:[A-Za-z][A-Za-z0-9]+\z') {
            throw $_.Exception.Message
        }
        Fail-WindowsFinalArtifactCanaryScan -Code $Code
    }
}

function Assert-WindowsFinalRegularFile {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$AllowedRoot,
        [Parameter(Mandatory = $true)][long]$MaximumBytes,
        [Parameter(Mandatory = $true)][string]$Code,
        [switch]$AllowEmpty
    )

    try {
        $fullRoot = [System.IO.Path]::GetFullPath($AllowedRoot)
        $fullPath = [System.IO.Path]::GetFullPath($Path)
        Assert-WindowsFinalNoAlternateDataStream -Path $fullRoot -Code $Code
        Assert-WindowsFinalNoAlternateDataStream -Path $fullPath -Code $Code
        Assert-WindowsFinalCondition `
            (Test-WindowsFinalPathContainedByRoot -Path $fullPath -Root $fullRoot) $Code
        Assert-WindowsFinalNoReparseDirectoryChain `
            -DirectoryPath $fullRoot -Code $Code
        Assert-WindowsFinalNoReparseDirectoryChain `
            -DirectoryPath ([System.IO.Path]::GetDirectoryName($fullPath)) -Code $Code
        Assert-WindowsFinalCondition `
            (Test-Path -LiteralPath $fullPath -PathType Leaf) $Code
        $item = Get-Item -LiteralPath $fullPath -Force -ErrorAction Stop
        Assert-WindowsFinalCondition `
            (-not $item.PSIsContainer -and
             (($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -eq 0) -and
             $MaximumBytes -gt 0 -and $item.Length -le $MaximumBytes -and
              ($AllowEmpty -or $item.Length -gt 0)) $Code
        Assert-WindowsFinalNoNamedStreams -Path $fullPath -Code $Code
        return $item
    }
    catch {
        if ($_.Exception.Message -cmatch
            '\AWindowsFinalArtifactCanaryScan:[A-Za-z][A-Za-z0-9]+\z') {
            throw $_.Exception.Message
        }
        Fail-WindowsFinalArtifactCanaryScan -Code $Code
    }
}

function Remove-WindowsFinalExactFile {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$ExpectedPath,
        [Parameter(Mandatory = $true)][string]$ParentRoot,
        [Parameter(Mandatory = $true)][long]$MaximumBytes
    )

    $fullPath = [System.IO.Path]::GetFullPath($Path)
    $fullExpected = [System.IO.Path]::GetFullPath($ExpectedPath)
    $fullParent = [System.IO.Path]::GetFullPath($ParentRoot)
    Assert-WindowsFinalNoAlternateDataStream -Path $fullPath -Code "CleanupRefused"
    Assert-WindowsFinalCondition `
        ($fullPath.Equals($fullExpected, [System.StringComparison]::OrdinalIgnoreCase) -and
         [System.IO.Directory]::GetParent($fullPath).FullName.Equals(
            $fullParent,
            [System.StringComparison]::OrdinalIgnoreCase)) `
        "CleanupRefused"
    if (-not (Test-Path -LiteralPath $fullPath)) {
        return
    }

    [void](Assert-WindowsFinalRegularFile `
            -Path $fullPath `
            -AllowedRoot $fullParent `
            -MaximumBytes $MaximumBytes `
            -Code "CleanupRefused" `
            -AllowEmpty)
    Remove-Item -LiteralPath $fullPath -Force -ErrorAction Stop
}

function Remove-WindowsFinalExactDirectory {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$ExpectedPath,
        [Parameter(Mandatory = $true)][string]$ParentRoot,
        [long]$MaximumEntries = $script:windowsFinalMaximumCleanupEntries,
        [long]$MaximumBytes = $script:windowsFinalMaximumCleanupBytes
    )

    $fullPath = [System.IO.Path]::GetFullPath($Path)
    $fullExpected = [System.IO.Path]::GetFullPath($ExpectedPath)
    $fullParent = [System.IO.Path]::GetFullPath($ParentRoot)
    Assert-WindowsFinalNoAlternateDataStream -Path $fullPath -Code "CleanupRefused"
    Assert-WindowsFinalCondition `
        ($fullPath.Equals($fullExpected, [System.StringComparison]::OrdinalIgnoreCase) -and
         [System.IO.Directory]::GetParent($fullPath).FullName.Equals(
            $fullParent,
            [System.StringComparison]::OrdinalIgnoreCase)) `
        "CleanupRefused"
    if (-not (Test-Path -LiteralPath $fullPath)) {
        return
    }

    Assert-WindowsFinalNoReparseDirectoryChain `
        -DirectoryPath $fullParent -Code "CleanupRefused"
    $rootItem = Get-Item -LiteralPath $fullPath -Force -ErrorAction Stop
    Assert-WindowsFinalCondition `
        ($rootItem.PSIsContainer -and
         (($rootItem.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -eq 0)) `
        "CleanupRefused"
    Assert-WindowsFinalNoNamedStreams `
        -Path $fullPath `
        -Code "CleanupRefused"

    $pending = New-Object 'System.Collections.Generic.Stack[string]'
    $pending.Push($fullPath)
    [long]$entryCount = 0
    [long]$totalBytes = 0
    while ($pending.Count -gt 0) {
        $directory = $pending.Pop()
        foreach ($entry in @(Get-ChildItem -LiteralPath $directory -Force -ErrorAction Stop)) {
            $entryCount++
            Assert-WindowsFinalCondition `
                ($MaximumEntries -gt 0 -and $entryCount -le $MaximumEntries) `
                "CleanupRefused"
            Assert-WindowsFinalNoNamedStreams `
                -Path $entry.FullName `
                -Code "CleanupRefused"
            Assert-WindowsFinalCondition `
                (($entry.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -eq 0) `
                "CleanupRefused"
            if ($entry.PSIsContainer) {
                $pending.Push($entry.FullName)
            }
            else {
                Assert-WindowsFinalCondition `
                    ($entry.Length -le
                        ($MaximumBytes - $totalBytes)) `
                    "CleanupRefused"
                $totalBytes += $entry.Length
            }
        }
    }

    Remove-Item -LiteralPath $fullPath -Recurse -Force -ErrorAction Stop
}

function Get-WindowsFinalPaths {
    $repositoryRoot = [System.IO.Path]::GetFullPath(
        (Split-Path -Parent $PSScriptRoot))
    $artifactUmbrella = [System.IO.Path]::Combine($repositoryRoot, ".artifacts")
    $outputRoot = [System.IO.Path]::Combine(
        $artifactUmbrella,
        "m16-final-artifact-scan")
    $workRoot = [System.IO.Path]::Combine($outputRoot, "work")
    $packageArtifactRoot = [System.IO.Path]::Combine(
        $artifactUmbrella,
        "msix-smoke")
    $repositoryDotNetPath = [System.IO.Path]::Combine(
        $artifactUmbrella,
        "dotnet",
        "dotnet.exe")

    return [pscustomobject][ordered]@{
        RepositoryRoot = $repositoryRoot
        ArtifactUmbrella = $artifactUmbrella
        OutputRoot = $outputRoot
        WorkRoot = $workRoot
        ProcessIoRoot = [System.IO.Path]::Combine($workRoot, "process-io")
        FullLogRoot = [System.IO.Path]::Combine($workRoot, "full-log")
        ScannerIoRoot = [System.IO.Path]::Combine($workRoot, "scanner-io")
        FinalEvidencePath = [System.IO.Path]::Combine($outputRoot, "last-success.json")
        PackageArtifactRoot = $packageArtifactRoot
        PackageIntermediatePath = [System.IO.Path]::Combine(
            $packageArtifactRoot,
            "m16-final-artifact-surfaces.json")
        PackageBindingPath = [System.IO.Path]::Combine(
            $packageArtifactRoot,
            "m16-final-artifact-binding.json")
        PackageCaptureParent = [System.IO.Path]::Combine(
            $packageArtifactRoot,
            "m16-final-artifact-capture")
        PackageOwnershipParent = [System.IO.Path]::Combine(
            $packageArtifactRoot,
            "m16-final-artifact-ownership")
        PackageOutputParent = [System.IO.Path]::Combine(
            $packageArtifactRoot,
            "packages")
        PlaybackControlParent = [System.IO.Path]::Combine(
            $packageArtifactRoot,
            "playback-ui")
        OnboardingControlParent = [System.IO.Path]::Combine(
            $packageArtifactRoot,
            "onboarding-ui")
        PackageSmokePath = [System.IO.Path]::Combine(
            $repositoryRoot,
            "eng",
            "Invoke-WindowsPackageSmoke.ps1")
        EvidenceHelperPath = [System.IO.Path]::Combine(
            $repositoryRoot,
            "eng",
            "WindowsM16FinalArtifactEvidence.ps1")
        BoundedProcessHelperPath = [System.IO.Path]::Combine(
            $repositoryRoot,
            "eng",
            "WindowsBoundedProcess.ps1")
        DotNetPath = $repositoryDotNetPath
        TestingAssemblyPath = [System.IO.Path]::Combine(
            $repositoryRoot,
            "apps",
            "windows",
            "tests",
            "IptvSuite.Testing",
            "bin",
            "x64",
            "Release",
            "net10.0",
            "IptvSuite.Testing.dll")
        GlobalJsonPath = [System.IO.Path]::Combine($repositoryRoot, "global.json")
        WindowsPowerShellPath = [System.IO.Path]::Combine(
            $env:SystemRoot,
            "System32",
            "WindowsPowerShell",
            "v1.0",
            "powershell.exe")
    }
}

function Get-WindowsFinalPackageRunPaths {
    param(
        [Parameter(Mandatory = $true)]$Paths,
        [Parameter(Mandatory = $true)]
        [ValidatePattern('\A[0-9a-f]{32}\z')]
        [string]$RunToken
    )

    return [pscustomobject][ordered]@{
        CaptureRoot = [System.IO.Path]::Combine(
            $Paths.PackageCaptureParent,
            $RunToken)
        OwnershipRoot = [System.IO.Path]::Combine(
            $Paths.PackageOwnershipParent,
            $RunToken)
        PackageOutput = [System.IO.Path]::Combine(
            $Paths.PackageOutputParent,
            $RunToken)
        PlaybackControl = [System.IO.Path]::Combine(
            $Paths.PlaybackControlParent,
            $RunToken)
        OnboardingControl = [System.IO.Path]::Combine(
            $Paths.OnboardingControlParent,
            $RunToken)
        PublicSigningCertificate = [System.IO.Path]::Combine(
            $Paths.PackageArtifactRoot,
            "$RunToken.cer")
        SigningThumbprint = [System.IO.Path]::Combine(
            $Paths.PackageOwnershipParent,
            $RunToken,
            "signing-certificate.thumbprint")
        PackageRegistrationIntent = [System.IO.Path]::Combine(
            $Paths.PackageOwnershipParent,
            $RunToken,
            "package-registration.intent")
        OnboardingThumbprint = [System.IO.Path]::Combine(
            $Paths.PackageOwnershipParent,
            $RunToken,
            "onboarding-loopback.thumbprint")
        PlaybackThumbprint = [System.IO.Path]::Combine(
            $Paths.PackageOwnershipParent,
            $RunToken,
            "playback-loopback.thumbprint")
        OnboardingPublicCertificate = [System.IO.Path]::Combine(
            $Paths.OnboardingControlParent,
            $RunToken,
            "loopback.cer")
        PlaybackPublicCertificate = [System.IO.Path]::Combine(
            $Paths.PlaybackControlParent,
            $RunToken,
            "loopback.cer")
    }
}

function Initialize-WindowsFinalPackageOwnership {
    param(
        [Parameter(Mandatory = $true)]$Paths,
        [Parameter(Mandatory = $true)]$RunPaths
    )

    [System.IO.Directory]::CreateDirectory($Paths.PackageArtifactRoot) | Out-Null
    Assert-WindowsFinalNoReparseDirectoryChain `
        -DirectoryPath $Paths.PackageArtifactRoot `
        -Code "PackageCleanupStateInvalid"
    if (Test-Path -LiteralPath $Paths.PackageOwnershipParent) {
        $ownershipParent = Get-Item `
            -LiteralPath $Paths.PackageOwnershipParent `
            -Force `
            -ErrorAction Stop
        Assert-WindowsFinalCondition `
            ($ownershipParent.PSIsContainer -and
             (($ownershipParent.Attributes -band
                    [System.IO.FileAttributes]::ReparsePoint) -eq 0) -and
             @(Get-ChildItem `
                    -LiteralPath $Paths.PackageOwnershipParent `
                    -Force `
                    -ErrorAction Stop).Count -eq 0) `
            "PackageCleanupStateInvalid"
        Assert-WindowsFinalNoNamedStreams `
            -Path $Paths.PackageOwnershipParent `
            -Code "PackageCleanupStateInvalid"
    }
    else {
        [System.IO.Directory]::CreateDirectory(
            $Paths.PackageOwnershipParent) | Out-Null
    }
    Assert-WindowsFinalNoReparseDirectoryChain `
        -DirectoryPath $Paths.PackageOwnershipParent `
        -Code "PackageCleanupStateInvalid"
    Assert-WindowsFinalCondition `
        (-not (Test-Path -LiteralPath $RunPaths.OwnershipRoot)) `
        "PackageCleanupStateInvalid"
    [System.IO.Directory]::CreateDirectory($RunPaths.OwnershipRoot) | Out-Null
    Assert-WindowsFinalNoReparseDirectoryChain `
        -DirectoryPath $RunPaths.OwnershipRoot `
        -Code "PackageCleanupStateInvalid"
}

function Get-WindowsFinalOwnershipValue {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$OwnershipRoot,
        [Parameter(Mandatory = $true)][string]$Pattern,
        [long]$MaximumBytes = 128
    )

    if (-not (Test-Path -LiteralPath $Path)) {
        return $null
    }
    $item = Assert-WindowsFinalRegularFile `
        -Path $Path `
        -AllowedRoot $OwnershipRoot `
        -MaximumBytes $MaximumBytes `
        -Code "PackageCleanupStateInvalid"
    $stream = [System.IO.File]::Open(
        $item.FullName,
        [System.IO.FileMode]::Open,
        [System.IO.FileAccess]::Read,
        [System.IO.FileShare]::None)
    try {
        $bytes = New-Object byte[] ([int]$stream.Length)
        $offset = 0
        while ($offset -lt $bytes.Length) {
            $read = $stream.Read($bytes, $offset, $bytes.Length - $offset)
            Assert-WindowsFinalCondition ($read -gt 0) "PackageCleanupStateInvalid"
            $offset += $read
        }
    }
    finally {
        $stream.Dispose()
    }
    try {
        $utf8 = New-Object System.Text.UTF8Encoding($false, $true)
        $value = $utf8.GetString($bytes)
        Assert-WindowsFinalCondition `
            ($value -cmatch $Pattern) `
            "PackageCleanupStateInvalid"
        return $value
    }
    catch {
        if ($_.Exception.Message -cmatch
            '\AWindowsFinalArtifactCanaryScan:[A-Za-z][A-Za-z0-9]+\z') {
            throw $_.Exception.Message
        }
        Fail-WindowsFinalArtifactCanaryScan -Code "PackageCleanupStateInvalid"
    }
    finally {
        if ($null -ne $bytes) {
            [System.Array]::Clear($bytes, 0, $bytes.Length)
        }
    }
}

function Get-WindowsFinalPackageRegistrationIntent {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$OwnershipRoot,
        [Parameter(Mandatory = $true)]
        [ValidatePattern('\A[0-9a-f]{32}\z')]
        [string]$ExpectedRunToken
    )

    if (-not (Test-Path -LiteralPath $Path)) {
        return $null
    }
    $intentPattern =
        '\A\{"SchemaVersion":1,"RunToken":"(?<run>[0-9a-f]{32})",' +
        '"ExpectedPackageFullName":"(?<package>' +
        'IptvSuite\.LocalDev\.6f0d9a64_' +
        '[0-9]+\.[0-9]+\.[0-9]+\.[0-9]+_x64__[0-9a-z]{13})"\}\z'
    $serialized = Get-WindowsFinalOwnershipValue `
        -Path $Path `
        -OwnershipRoot $OwnershipRoot `
        -Pattern $intentPattern `
        -MaximumBytes 512
    Assert-WindowsFinalCondition `
        (-not [string]::IsNullOrEmpty($serialized)) `
        "PackageCleanupStateInvalid"
    $match = [System.Text.RegularExpressions.Regex]::Match(
        $serialized,
        $intentPattern,
        [System.Text.RegularExpressions.RegexOptions]::CultureInvariant)
    Assert-WindowsFinalCondition `
        ($match.Success -and
         $match.Groups['run'].Value -ceq $ExpectedRunToken) `
        "PackageCleanupStateInvalid"
    return [pscustomobject][ordered]@{
        RunToken = $match.Groups['run'].Value
        ExpectedPackageFullName = $match.Groups['package'].Value
    }
}

function Get-WindowsFinalCertificateFileThumbprint {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$AllowedRoot,
        [Parameter(Mandatory = $true)][string]$ExpectedSubject
    )

    if (-not (Test-Path -LiteralPath $Path)) {
        return $null
    }
    [void](Assert-WindowsFinalRegularFile `
            -Path $Path `
            -AllowedRoot $AllowedRoot `
            -MaximumBytes 64KB `
            -Code "PackageCleanupStateInvalid")
    $certificate = $null
    try {
        $certificate =
            [System.Security.Cryptography.X509Certificates.X509Certificate2]::new(
                $Path)
        Assert-WindowsFinalCondition `
            (-not $certificate.HasPrivateKey -and
             $certificate.Subject -ceq $ExpectedSubject -and
             $certificate.Issuer -ceq $ExpectedSubject -and
             $certificate.Thumbprint -cmatch '\A[0-9A-F]{40}\z') `
            "PackageCleanupStateInvalid"
        return $certificate.Thumbprint
    }
    catch {
        if ($_.Exception.Message -cmatch
            '\AWindowsFinalArtifactCanaryScan:[A-Za-z][A-Za-z0-9]+\z') {
            throw $_.Exception.Message
        }
        Fail-WindowsFinalArtifactCanaryScan -Code "PackageCleanupStateInvalid"
    }
    finally {
        if ($null -ne $certificate) {
            $certificate.Dispose()
        }
    }
}

function Resolve-WindowsFinalOwnedThumbprint {
    param(
        [string]$RecordedThumbprint,
        [string]$CertificateThumbprint
    )

    if (-not [string]::IsNullOrEmpty($RecordedThumbprint) -and
        -not [string]::IsNullOrEmpty($CertificateThumbprint)) {
        Assert-WindowsFinalCondition `
            ($RecordedThumbprint -ceq $CertificateThumbprint) `
            "PackageCleanupStateInvalid"
    }
    if (-not [string]::IsNullOrEmpty($RecordedThumbprint)) {
        return $RecordedThumbprint
    }
    return $CertificateThumbprint
}

function Remove-WindowsFinalOwnedCertificate {
    param(
        [Parameter(Mandatory = $true)][string]$StorePath,
        [Parameter(Mandatory = $true)]
        [ValidatePattern('\A[0-9A-F]{40}\z')]
        [string]$Thumbprint,
        [Parameter(Mandatory = $true)][string]$ExpectedSubject,
        [string]$ExpectedFriendlyName,
        [switch]$RequirePrivateKey
    )

    $certificatePath = "$StorePath\$Thumbprint"
    $candidate = Get-Item `
        -LiteralPath $certificatePath `
        -ErrorAction SilentlyContinue
    if ($null -eq $candidate) {
        return
    }
    Assert-WindowsFinalCondition `
        ($candidate.Subject -ceq $ExpectedSubject -and
         $candidate.Issuer -ceq $ExpectedSubject -and
         $candidate.Thumbprint -ceq $Thumbprint -and
         ([string]::IsNullOrEmpty($ExpectedFriendlyName) -or
          $candidate.FriendlyName -ceq $ExpectedFriendlyName) -and
         (-not $RequirePrivateKey -or $candidate.HasPrivateKey)) `
        "PackageCleanupStateInvalid"
    Remove-Item -LiteralPath $certificatePath -Force -ErrorAction Stop
}

function Stop-WindowsFinalExactPackageProcesses {
    param(
        [Parameter(Mandatory = $true)][string]$InstallLocation,
        [Parameter(Mandatory = $true)][string]$PackageFullName
    )

    try {
        Assert-WindowsFinalCondition `
            ([System.IO.Path]::IsPathRooted($InstallLocation) -and
             -not [string]::IsNullOrWhiteSpace($PackageFullName)) `
            "PackageCleanupStateInvalid"
        $fullInstallLocation = [System.IO.Path]::GetFullPath(
            $InstallLocation).TrimEnd(
                [System.IO.Path]::DirectorySeparatorChar,
                [System.IO.Path]::AltDirectorySeparatorChar)
        Assert-WindowsFinalCondition `
            ([System.IO.Path]::GetFileName($fullInstallLocation) -ceq
                $PackageFullName) `
            "PackageCleanupStateInvalid"
        $installItem = Get-Item `
            -LiteralPath $fullInstallLocation `
            -Force `
            -ErrorAction Stop
        Assert-WindowsFinalCondition `
            ($installItem.PSIsContainer -and
             (($installItem.Attributes -band
                    [System.IO.FileAttributes]::ReparsePoint) -eq 0)) `
            "PackageCleanupStateInvalid"

        $expectedExecutable = [System.IO.Path]::GetFullPath(
            [System.IO.Path]::Combine(
                $fullInstallLocation,
                "IptvSuite.Windows.exe"))
        Assert-WindowsFinalCondition `
            ([System.IO.Directory]::GetParent($expectedExecutable).FullName.Equals(
                $fullInstallLocation,
                [System.StringComparison]::OrdinalIgnoreCase) -and
             [System.IO.File]::Exists($expectedExecutable)) `
            "PackageCleanupStateInvalid"
        $executableItem = Get-Item `
            -LiteralPath $expectedExecutable `
            -Force `
            -ErrorAction Stop
        Assert-WindowsFinalCondition `
            (-not $executableItem.PSIsContainer -and
             (($executableItem.Attributes -band
                    [System.IO.FileAttributes]::ReparsePoint) -eq 0)) `
            "PackageCleanupStateInvalid"

        $candidateProcesses = @(
            Get-Process `
                -Name "IptvSuite.Windows" `
                -ErrorAction SilentlyContinue)
        Assert-WindowsFinalCondition `
            ($candidateProcesses.Count -le 64) `
            "PackageCleanupStateInvalid"
        $stopDeadline = [DateTime]::UtcNow.AddSeconds(15)
        foreach ($process in $candidateProcesses) {
            try {
                $process.Refresh()
                if ($process.HasExited) {
                    continue
                }
                $processPath = [System.IO.Path]::GetFullPath(
                    [string]$process.MainModule.FileName)
                if (-not $processPath.Equals(
                        $expectedExecutable,
                        [System.StringComparison]::OrdinalIgnoreCase)) {
                    continue
                }
                $process.Kill()
                $remainingMilliseconds = [int][Math]::Floor(
                    ($stopDeadline - [DateTime]::UtcNow).TotalMilliseconds)
                Assert-WindowsFinalCondition `
                    ($remainingMilliseconds -gt 0 -and
                     $process.WaitForExit(
                        [Math]::Min(10000, $remainingMilliseconds))) `
                    "PackageCleanupStateInvalid"
            }
            catch {
                $stillRunning = $null -ne (Get-Process `
                        -Id $process.Id `
                        -ErrorAction SilentlyContinue)
                if ($stillRunning) {
                    throw
                }
            }
            finally {
                $process.Dispose()
            }
        }

        $remainingProcesses = @(
            Get-Process `
                -Name "IptvSuite.Windows" `
                -ErrorAction SilentlyContinue)
        Assert-WindowsFinalCondition `
            ($remainingProcesses.Count -le 64) `
            "PackageCleanupStateInvalid"
        foreach ($process in $remainingProcesses) {
            try {
                $process.Refresh()
                if (-not $process.HasExited) {
                    $processPath = [System.IO.Path]::GetFullPath(
                        [string]$process.MainModule.FileName)
                    Assert-WindowsFinalCondition `
                        (-not $processPath.Equals(
                            $expectedExecutable,
                            [System.StringComparison]::OrdinalIgnoreCase)) `
                        "PackageCleanupStateInvalid"
                }
            }
            catch {
                $stillRunning = $null -ne (Get-Process `
                        -Id $process.Id `
                        -ErrorAction SilentlyContinue)
                if ($stillRunning) {
                    throw
                }
            }
            finally {
                $process.Dispose()
            }
        }
    }
    catch {
        if ($_.Exception.Message -cmatch
            '\AWindowsFinalArtifactCanaryScan:[A-Za-z][A-Za-z0-9]+\z') {
            throw $_.Exception.Message
        }
        Fail-WindowsFinalArtifactCanaryScan -Code "PackageCleanupStateInvalid"
    }
}

function Remove-WindowsFinalExactEmptyDirectory {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$ExpectedPath,
        [Parameter(Mandatory = $true)][string]$ParentRoot
    )

    $fullPath = [System.IO.Path]::GetFullPath($Path)
    $fullExpected = [System.IO.Path]::GetFullPath($ExpectedPath)
    $fullParent = [System.IO.Path]::GetFullPath($ParentRoot)
    Assert-WindowsFinalCondition `
        ($fullPath.Equals($fullExpected, [System.StringComparison]::OrdinalIgnoreCase) -and
         [System.IO.Directory]::GetParent($fullPath).FullName.Equals(
            $fullParent,
            [System.StringComparison]::OrdinalIgnoreCase)) `
        "CleanupRefused"
    if (-not (Test-Path -LiteralPath $fullPath)) {
        return
    }
    Assert-WindowsFinalNoReparseDirectoryChain `
        -DirectoryPath $fullParent `
        -Code "CleanupRefused"
    $item = Get-Item -LiteralPath $fullPath -Force -ErrorAction Stop
    Assert-WindowsFinalCondition `
        ($item.PSIsContainer -and
         (($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -eq 0) -and
         @(Get-ChildItem -LiteralPath $fullPath -Force -ErrorAction Stop).Count -eq 0) `
        "CleanupRefused"
    Assert-WindowsFinalNoNamedStreams -Path $fullPath -Code "CleanupRefused"
    Remove-Item -LiteralPath $fullPath -Force -ErrorAction Stop
}

function Remove-WindowsFinalPackageSideState {
    param(
        [Parameter(Mandatory = $true)]$Paths,
        [Parameter(Mandatory = $true)]$RunPaths,
        [Parameter(Mandatory = $true)]
        [ValidatePattern('\A[0-9a-f]{32}\z')]
        [string]$RunToken
    )

    $expectedPublisher = "CN=IptvSuite Local Development"
    $expectedLoopbackSubject = "CN=IPTVSuite Synthetic Loopback"
    $expectedSigningFriendlyName = "IptvSuite M16 Final Artifact $RunToken"

    $signingThumbprint = Get-WindowsFinalOwnershipValue `
        -Path $RunPaths.SigningThumbprint `
        -OwnershipRoot $RunPaths.OwnershipRoot `
        -Pattern '\A[0-9A-F]{40}\z'
    $signingFileThumbprint = Get-WindowsFinalCertificateFileThumbprint `
        -Path $RunPaths.PublicSigningCertificate `
        -AllowedRoot $Paths.PackageArtifactRoot `
        -ExpectedSubject $expectedPublisher
    $signingThumbprint = Resolve-WindowsFinalOwnedThumbprint `
        -RecordedThumbprint $signingThumbprint `
        -CertificateThumbprint $signingFileThumbprint

    $friendlySigningCertificates = @(
        Get-ChildItem `
            -LiteralPath "Microsoft.PowerShell.Security\Certificate::CurrentUser\My" `
            -ErrorAction Stop |
            Where-Object {
                $_.FriendlyName -ceq $expectedSigningFriendlyName -and
                $_.Subject -ceq $expectedPublisher -and
                $_.Issuer -ceq $expectedPublisher
            })
    Assert-WindowsFinalCondition `
        ($friendlySigningCertificates.Count -le 1) `
        "PackageCleanupStateInvalid"
    if ($friendlySigningCertificates.Count -eq 1) {
        $friendlyThumbprint = $friendlySigningCertificates[0].Thumbprint
        Assert-WindowsFinalCondition `
            ($friendlyThumbprint -cmatch '\A[0-9A-F]{40}\z' -and
             ([string]::IsNullOrEmpty($signingThumbprint) -or
              $signingThumbprint -ceq $friendlyThumbprint)) `
            "PackageCleanupStateInvalid"
        $signingThumbprint = $friendlyThumbprint
    }
    if (-not [string]::IsNullOrEmpty($signingThumbprint)) {
        Remove-WindowsFinalOwnedCertificate `
            -StorePath "Microsoft.PowerShell.Security\Certificate::LocalMachine\TrustedPeople" `
            -Thumbprint $signingThumbprint `
            -ExpectedSubject $expectedPublisher
        Remove-WindowsFinalOwnedCertificate `
            -StorePath "Microsoft.PowerShell.Security\Certificate::CurrentUser\My" `
            -Thumbprint $signingThumbprint `
            -ExpectedSubject $expectedPublisher `
            -ExpectedFriendlyName $expectedSigningFriendlyName `
            -RequirePrivateKey
    }

    foreach ($loopback in @(
            [pscustomobject]@{
                RecordPath = $RunPaths.OnboardingThumbprint
                CertificatePath = $RunPaths.OnboardingPublicCertificate
                CertificateRoot = $RunPaths.OnboardingControl
            },
            [pscustomobject]@{
                RecordPath = $RunPaths.PlaybackThumbprint
                CertificatePath = $RunPaths.PlaybackPublicCertificate
                CertificateRoot = $RunPaths.PlaybackControl
            })) {
        $recorded = Get-WindowsFinalOwnershipValue `
            -Path $loopback.RecordPath `
            -OwnershipRoot $RunPaths.OwnershipRoot `
            -Pattern '\A[0-9A-F]{40}\z'
        $fromFile = Get-WindowsFinalCertificateFileThumbprint `
            -Path $loopback.CertificatePath `
            -AllowedRoot $loopback.CertificateRoot `
            -ExpectedSubject $expectedLoopbackSubject
        $thumbprint = Resolve-WindowsFinalOwnedThumbprint `
            -RecordedThumbprint $recorded `
            -CertificateThumbprint $fromFile
        if (-not [string]::IsNullOrEmpty($thumbprint)) {
            Remove-WindowsFinalOwnedCertificate `
                -StorePath "Microsoft.PowerShell.Security\Certificate::LocalMachine\Root" `
                -Thumbprint $thumbprint `
                -ExpectedSubject $expectedLoopbackSubject
        }
    }

    $packageIntent = Get-WindowsFinalPackageRegistrationIntent `
        -Path $RunPaths.PackageRegistrationIntent `
        -OwnershipRoot $RunPaths.OwnershipRoot `
        -ExpectedRunToken $RunToken
    if ($null -ne $packageIntent) {
        $packages = @(
            Get-AppxPackage `
                -Name "IptvSuite.LocalDev.6f0d9a64" `
                -ErrorAction Stop |
                Where-Object {
                    $_.Publisher -ceq $expectedPublisher
                })
        Assert-WindowsFinalCondition `
            ($packages.Count -le 1 -and
             ($packages.Count -eq 0 -or
              ([string]$packages[0].Architecture -ceq "X64" -and
               [string]$packages[0].PackageFullName -ceq
                    $packageIntent.ExpectedPackageFullName))) `
            "PackageCleanupStateInvalid"
        if ($packages.Count -eq 1) {
            Stop-WindowsFinalExactPackageProcesses `
                -InstallLocation ([string]$packages[0].InstallLocation) `
                -PackageFullName ([string]$packages[0].PackageFullName)
            Remove-AppxPackage `
                -Package $packages[0].PackageFullName `
                -ErrorAction Stop
        }
        $remaining = @(
            Get-AppxPackage `
                -Name "IptvSuite.LocalDev.6f0d9a64" `
                -ErrorAction Stop |
                Where-Object {
                    $_.Publisher -ceq $expectedPublisher
                })
        Assert-WindowsFinalCondition `
            ($remaining.Count -eq 0 -or
             ($remaining.Count -eq 1 -and
              [string]$remaining[0].PackageFullName -cne
                $packageIntent.ExpectedPackageFullName)) `
            "PackageCleanupStateInvalid"
    }

    Remove-WindowsFinalExactFile `
        -Path $RunPaths.PublicSigningCertificate `
        -ExpectedPath $RunPaths.PublicSigningCertificate `
        -ParentRoot $Paths.PackageArtifactRoot `
        -MaximumBytes 64KB
    Remove-WindowsFinalExactDirectory `
        -Path $RunPaths.OnboardingControl `
        -ExpectedPath $RunPaths.OnboardingControl `
        -ParentRoot $Paths.OnboardingControlParent
    Remove-WindowsFinalExactDirectory `
        -Path $RunPaths.PlaybackControl `
        -ExpectedPath $RunPaths.PlaybackControl `
        -ParentRoot $Paths.PlaybackControlParent
    Remove-WindowsFinalExactDirectory `
        -Path $RunPaths.CaptureRoot `
        -ExpectedPath $RunPaths.CaptureRoot `
        -ParentRoot $Paths.PackageCaptureParent `
        -MaximumEntries 26000 `
        -MaximumBytes 9GB
    Remove-WindowsFinalExactDirectory `
        -Path $RunPaths.PackageOutput `
        -ExpectedPath $RunPaths.PackageOutput `
        -ParentRoot $Paths.PackageOutputParent `
        -MaximumEntries 25000 `
        -MaximumBytes 8GB
    Remove-WindowsFinalExactDirectory `
        -Path $RunPaths.OwnershipRoot `
        -ExpectedPath $RunPaths.OwnershipRoot `
        -ParentRoot $Paths.PackageOwnershipParent `
        -MaximumEntries 16 `
        -MaximumBytes 4KB

    foreach ($parent in @(
            [pscustomobject]@{
                Path = $Paths.PackageCaptureParent
                Parent = $Paths.PackageArtifactRoot
            },
            [pscustomobject]@{
                Path = $Paths.PackageOwnershipParent
                Parent = $Paths.PackageArtifactRoot
            },
            [pscustomobject]@{
                Path = $Paths.PackageOutputParent
                Parent = $Paths.PackageArtifactRoot
            },
            [pscustomobject]@{
                Path = $Paths.PlaybackControlParent
                Parent = $Paths.PackageArtifactRoot
            },
            [pscustomobject]@{
                Path = $Paths.OnboardingControlParent
                Parent = $Paths.PackageArtifactRoot
            })) {
        if (Test-Path -LiteralPath $parent.Path -PathType Container) {
            if (@(Get-ChildItem `
                    -LiteralPath $parent.Path `
                    -Force `
                    -ErrorAction Stop).Count -eq 0) {
                Remove-WindowsFinalExactEmptyDirectory `
                    -Path $parent.Path `
                    -ExpectedPath $parent.Path `
                    -ParentRoot $parent.Parent
            }
        }
    }
}

function Remove-WindowsFinalStalePackageOwnership {
    param(
        [Parameter(Mandatory = $true)]$Paths
    )

    if (Test-Path -LiteralPath $Paths.PackageOwnershipParent) {
        Assert-WindowsFinalNoReparseDirectoryChain `
            -DirectoryPath $Paths.PackageOwnershipParent `
            -Code "PackageCleanupStateInvalid"
        $ownershipParent = Get-Item `
            -LiteralPath $Paths.PackageOwnershipParent `
            -Force `
            -ErrorAction Stop
        Assert-WindowsFinalCondition `
            ($ownershipParent.PSIsContainer -and
             (($ownershipParent.Attributes -band
                    [System.IO.FileAttributes]::ReparsePoint) -eq 0)) `
            "PackageCleanupStateInvalid"
        Assert-WindowsFinalNoNamedStreams `
            -Path $Paths.PackageOwnershipParent `
            -Code "PackageCleanupStateInvalid"

        $staleEntries = @(
            Get-ChildItem `
                -LiteralPath $Paths.PackageOwnershipParent `
                -Force `
                -ErrorAction Stop)
        Assert-WindowsFinalCondition `
            ($staleEntries.Count -le 1) "PackageCleanupStateInvalid"
        foreach ($staleEntry in $staleEntries) {
            Assert-WindowsFinalCondition `
                ($staleEntry.PSIsContainer -and
                 (($staleEntry.Attributes -band
                        [System.IO.FileAttributes]::ReparsePoint) -eq 0) -and
                 $staleEntry.Name -cmatch '\A[0-9a-f]{32}\z' -and
                 [System.IO.Directory]::GetParent(
                    $staleEntry.FullName).FullName.Equals(
                        $Paths.PackageOwnershipParent,
                        [System.StringComparison]::OrdinalIgnoreCase)) `
                "PackageCleanupStateInvalid"
            Assert-WindowsFinalNoNamedStreams `
                -Path $staleEntry.FullName `
                -Code "PackageCleanupStateInvalid"
            $ownershipEntries = @(
                Get-ChildItem `
                    -LiteralPath $staleEntry.FullName `
                    -Force `
                    -ErrorAction Stop)
            $allowedOwnershipFiles = @(
                "signing-certificate.thumbprint",
                "package-registration.intent",
                "onboarding-loopback.thumbprint",
                "playback-loopback.thumbprint")
            Assert-WindowsFinalCondition `
                ($ownershipEntries.Count -le $allowedOwnershipFiles.Count) `
                "PackageCleanupStateInvalid"
            [long]$ownershipBytes = 0
            foreach ($ownershipEntry in $ownershipEntries) {
                $entryMaximumBytes = if ($ownershipEntry.Name -ceq
                        "package-registration.intent") { 512 } else { 128 }
                Assert-WindowsFinalCondition `
                    (-not $ownershipEntry.PSIsContainer -and
                     (($ownershipEntry.Attributes -band
                            [System.IO.FileAttributes]::ReparsePoint) -eq 0) -and
                     $allowedOwnershipFiles -ccontains $ownershipEntry.Name -and
                     $ownershipEntry.Length -gt 0 -and
                     $ownershipEntry.Length -le $entryMaximumBytes -and
                     $ownershipEntry.Length -le (896 - $ownershipBytes)) `
                    "PackageCleanupStateInvalid"
                Assert-WindowsFinalNoNamedStreams `
                    -Path $ownershipEntry.FullName `
                    -Code "PackageCleanupStateInvalid"
                $ownershipBytes += $ownershipEntry.Length
            }
            $staleRunPaths = Get-WindowsFinalPackageRunPaths `
                -Paths $Paths `
                -RunToken $staleEntry.Name
            Assert-WindowsFinalCondition `
                ($staleRunPaths.OwnershipRoot.Equals(
                    $staleEntry.FullName,
                    [System.StringComparison]::OrdinalIgnoreCase)) `
                "PackageCleanupStateInvalid"
            Remove-WindowsFinalPackageSideState `
                -Paths $Paths `
                -RunPaths $staleRunPaths `
                -RunToken $staleEntry.Name
        }
    }

    foreach ($ownedParent in @(
            $Paths.PackageCaptureParent,
            $Paths.PackageOwnershipParent,
            $Paths.PackageOutputParent,
            $Paths.PlaybackControlParent,
            $Paths.OnboardingControlParent)) {
        if (Test-Path -LiteralPath $ownedParent) {
            Assert-WindowsFinalNoReparseDirectoryChain `
                -DirectoryPath $ownedParent `
                -Code "PackageCleanupStateInvalid"
            Assert-WindowsFinalCondition `
                (@(Get-ChildItem `
                        -LiteralPath $ownedParent `
                        -Force `
                        -ErrorAction Stop).Count -eq 0) `
                "PackageCleanupStateInvalid"
        }
    }
}

function Enter-WindowsFinalRunMutex {
    param(
        [Parameter(Mandatory = $true)][string]$RepositoryRoot
    )

    $bytes = $script:windowsFinalUtf8NoBom.GetBytes(
        ([System.IO.Path]::GetFullPath($RepositoryRoot)).ToUpperInvariant())
    $sha256 = [System.Security.Cryptography.SHA256]::Create()
    try {
        $suffix = (($sha256.ComputeHash($bytes) | ForEach-Object {
                    $_.ToString("x2")
                }) -join '')
    }
    finally {
        $sha256.Dispose()
        [System.Array]::Clear($bytes, 0, $bytes.Length)
    }
    $mutex = [System.Threading.Mutex]::new(
        $false,
        "Local\IptvSuite.M16.FinalArtifact.$suffix")
    try {
        $acquired = $mutex.WaitOne(0)
    }
    catch [System.Threading.AbandonedMutexException] {
        $acquired = $true
    }
    if (-not $acquired) {
        $mutex.Dispose()
        Fail-WindowsFinalArtifactCanaryScan -Code "ConcurrentRun"
    }
    return $mutex
}

function Enter-WindowsFinalPackageIdentityMutex {
    $mutex = [System.Threading.Mutex]::new(
        $false,
        $script:windowsFinalPackageIdentityMutexName)
    try {
        $acquired = $mutex.WaitOne(0)
    }
    catch [System.Threading.AbandonedMutexException] {
        $acquired = $true
    }
    if (-not $acquired) {
        $mutex.Dispose()
        Fail-WindowsFinalArtifactCanaryScan -Code "ConcurrentPackageSmoke"
    }
    return $mutex
}

function Exit-WindowsFinalRunMutex {
    param(
        [Parameter(Mandatory = $true)][System.Threading.Mutex]$Mutex
    )

    $releaseFailed = $false
    try {
        $Mutex.ReleaseMutex()
    }
    catch {
        $releaseFailed = $true
    }
    try {
        $Mutex.Dispose()
    }
    catch {
        $releaseFailed = $true
    }
    if ($releaseFailed) {
        Fail-WindowsFinalArtifactCanaryScan -Code "RunMutexReleaseFailed"
    }
}

function Get-WindowsFinalCleanRepositoryCommit {
    param(
        [Parameter(Mandatory = $true)][string]$RepositoryRoot
    )

    try {
        $status = @(& git.exe -C $RepositoryRoot status `
                --porcelain=v1 --untracked-files=all 2>$null)
        Assert-WindowsFinalCondition `
            ($LASTEXITCODE -eq 0 -and $status.Count -eq 0) `
            "RepositoryDirty"
        $head = @(& git.exe -C $RepositoryRoot rev-parse HEAD 2>$null)
        Assert-WindowsFinalCondition `
            ($LASTEXITCODE -eq 0 -and $head.Count -eq 1) `
            "RepositoryBindingFailed"
        $commit = ([string]$head[0]).Trim().ToLowerInvariant()
        Assert-WindowsFinalCondition `
            ($commit -cmatch '\A[0-9a-f]{40}\z') `
            "RepositoryBindingFailed"

        $githubSha = [Environment]::GetEnvironmentVariable("GITHUB_SHA", "Process")
        if (-not [string]::IsNullOrEmpty($githubSha)) {
            Assert-WindowsFinalCondition `
                ($githubSha -cmatch '\A[0-9a-fA-F]{40}\z' -and
                 $githubSha.ToLowerInvariant() -ceq $commit) `
                "RepositoryBindingFailed"
        }
        return $commit
    }
    catch {
        if ($_.Exception.Message -cmatch
            '\AWindowsFinalArtifactCanaryScan:[A-Za-z][A-Za-z0-9]+\z') {
            throw $_.Exception.Message
        }
        Fail-WindowsFinalArtifactCanaryScan -Code "RepositoryInspectionFailed"
    }
}

function Assert-WindowsFinalRepositoryStable {
    param(
        [Parameter(Mandatory = $true)][string]$RepositoryRoot,
        [Parameter(Mandatory = $true)][string]$ExpectedCommit
    )

    $actual = Get-WindowsFinalCleanRepositoryCommit -RepositoryRoot $RepositoryRoot
    Assert-WindowsFinalCondition `
        ($actual -ceq $ExpectedCommit) "RepositoryChanged"
}

function Initialize-WindowsFinalWorkspace {
    param(
        [Parameter(Mandatory = $true)]$Paths
    )

    Assert-WindowsFinalNoReparseDirectoryChain `
        -DirectoryPath $Paths.RepositoryRoot -Code "WorkspaceInvalid"
    foreach ($directory in @($Paths.ArtifactUmbrella, $Paths.OutputRoot)) {
        [System.IO.Directory]::CreateDirectory($directory) | Out-Null
        Assert-WindowsFinalNoReparseDirectoryChain `
            -DirectoryPath $directory -Code "WorkspaceInvalid"
    }

    Remove-WindowsFinalExactFile `
        -Path $Paths.FinalEvidencePath `
        -ExpectedPath $Paths.FinalEvidencePath `
        -ParentRoot $Paths.OutputRoot `
        -MaximumBytes 64KB

    Remove-WindowsFinalExactDirectory `
        -Path $Paths.WorkRoot `
        -ExpectedPath $Paths.WorkRoot `
        -ParentRoot $Paths.OutputRoot

    Assert-WindowsFinalCondition `
        (@(Get-ChildItem -LiteralPath $Paths.OutputRoot -Force).Count -eq 0) `
        "WorkspaceContainsUnexpectedEntry"

    [System.IO.Directory]::CreateDirectory($Paths.WorkRoot) | Out-Null
    foreach ($directory in @(
            $Paths.ProcessIoRoot,
            $Paths.FullLogRoot,
            $Paths.ScannerIoRoot)) {
        [System.IO.Directory]::CreateDirectory($directory) | Out-Null
        Assert-WindowsFinalNoReparseDirectoryChain `
            -DirectoryPath $directory -Code "WorkspaceInvalid"
    }
}

function Assert-WindowsFinalHostAndSdk {
    param(
        [Parameter(Mandatory = $true)]$Paths
    )

    Assert-WindowsFinalCondition `
        ($env:OS -ceq "Windows_NT" -and
         $PSVersionTable.PSEdition -ceq "Desktop" -and
         $PSVersionTable.PSVersion.Major -eq 5 -and
         $PSVersionTable.PSVersion.Minor -eq 1) `
        "HostUnsupported"
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($identity)
    Assert-WindowsFinalCondition `
        ($principal.IsInRole(
            [Security.Principal.WindowsBuiltInRole]::Administrator)) `
        "ElevationRequired"

    if (-not [System.IO.File]::Exists($Paths.DotNetPath)) {
        $dotNetCommands = @(Get-Command `
                dotnet.exe `
                -CommandType Application `
                -ErrorAction Stop)
        Assert-WindowsFinalCondition `
            ($dotNetCommands.Count -ge 1 -and
             -not [string]::IsNullOrWhiteSpace([string]$dotNetCommands[0].Source)) `
            "SdkContractInvalid"
        $Paths.DotNetPath = [System.IO.Path]::GetFullPath(
            [string]$dotNetCommands[0].Source)
    }

    foreach ($fixedFile in @(
            $Paths.PackageSmokePath,
            $Paths.EvidenceHelperPath,
            $Paths.BoundedProcessHelperPath,
            $Paths.GlobalJsonPath)) {
        [void](Assert-WindowsFinalRegularFile `
                -Path $fixedFile `
                -AllowedRoot $Paths.RepositoryRoot `
                -MaximumBytes 8MB `
                -Code "RequiredInputInvalid")
    }
    $dotNetAllowedRoot = if (Test-WindowsFinalPathContainedByRoot `
            -Path $Paths.DotNetPath `
            -Root $Paths.RepositoryRoot) {
        $Paths.RepositoryRoot
    }
    else {
        [System.IO.Path]::GetPathRoot($Paths.DotNetPath)
    }
    [void](Assert-WindowsFinalRegularFile `
            -Path $Paths.DotNetPath `
            -AllowedRoot $dotNetAllowedRoot `
            -MaximumBytes 8MB `
            -Code "RequiredInputInvalid")
    [void](Assert-WindowsFinalRegularFile `
            -Path $Paths.WindowsPowerShellPath `
            -AllowedRoot ([System.IO.Path]::GetPathRoot($Paths.WindowsPowerShellPath)) `
            -MaximumBytes 4MB `
            -Code "HostUnsupported")

    try {
        $global = Get-Content -Raw -LiteralPath $Paths.GlobalJsonPath |
            ConvertFrom-Json -ErrorAction Stop
        Assert-WindowsFinalCondition `
            ($global.sdk.version -is [string] -and
             $global.sdk.version -ceq $script:windowsFinalExpectedSdk -and
             $global.sdk.rollForward -ceq "disable" -and
             $global.sdk.allowPrerelease -is [bool] -and
             -not $global.sdk.allowPrerelease) `
            "SdkContractInvalid"
        $actual = @(& $Paths.DotNetPath --version 2>$null)
        Assert-WindowsFinalCondition `
            ($LASTEXITCODE -eq 0 -and $actual.Count -eq 1 -and
             ([string]$actual[0]).Trim() -ceq $script:windowsFinalExpectedSdk) `
            "SdkContractInvalid"
    }
    catch {
        if ($_.Exception.Message -cmatch
            '\AWindowsFinalArtifactCanaryScan:[A-Za-z][A-Za-z0-9]+\z') {
            throw $_.Exception.Message
        }
        Fail-WindowsFinalArtifactCanaryScan -Code "SdkContractInvalid"
    }
}

function Join-WindowsFinalProcessArguments {
    param(
        [Parameter(Mandatory = $true)][string[]]$Values
    )

    $quoted = New-Object System.Collections.Generic.List[string]
    foreach ($value in $Values) {
        Assert-WindowsFinalCondition `
            (-not [string]::IsNullOrWhiteSpace($value) -and
             $value.IndexOf('"') -lt 0 -and
             $value.IndexOf([char]0) -lt 0 -and
             $value.IndexOf([char]13) -lt 0 -and
             $value.IndexOf([char]10) -lt 0) `
            "ProcessArgumentInvalid"
        $quoted.Add('"' + $value + '"')
    }
    return ($quoted -join ' ')
}

function Invoke-WindowsFinalBoundedProcess {
    param(
        [Parameter(Mandatory = $true)][string]$FilePath,
        [Parameter(Mandatory = $true)][string[]]$ArgumentValues,
        [Parameter(Mandatory = $true)][string]$WorkingDirectory,
        [Parameter(Mandatory = $true)][string]$OutputDirectory,
        [Parameter(Mandatory = $true)]
        [ValidateSet("package", "scanner", "exact-package-scanner")]
        [string]$ProcessKind
    )

    $maximumBytes = if ($ProcessKind -ceq "package") {
        $script:windowsFinalMaximumLogBytes
    }
    else {
        $script:windowsFinalMaximumScannerOutputBytes
    }
    $timeoutMilliseconds = if ($ProcessKind -ceq "package") {
        $script:windowsFinalPackageTimeoutMilliseconds
    }
    else {
        $script:windowsFinalScannerTimeoutMilliseconds
    }
    $stdoutPath = [System.IO.Path]::Combine($OutputDirectory, "$ProcessKind.stdout")
    $stderrPath = [System.IO.Path]::Combine($OutputDirectory, "$ProcessKind.stderr")
    Assert-WindowsFinalCondition `
        (-not (Test-Path -LiteralPath $stdoutPath) -and
         -not (Test-Path -LiteralPath $stderrPath)) `
        "ProcessOutputInvalid"

    try {
        return Invoke-WindowsBoundedProcess `
            -FilePath $FilePath `
            -ArgumentString (Join-WindowsFinalProcessArguments -Values $ArgumentValues) `
            -WorkingDirectory $WorkingDirectory `
            -StandardOutputPath $stdoutPath `
            -StandardErrorPath $stderrPath `
            -TimeoutMilliseconds $timeoutMilliseconds `
            -MaximumOutputBytes $maximumBytes
    }
    catch {
        if ($_.Exception.Message -cmatch '\AWindowsBoundedProcess:([A-Za-z][A-Za-z0-9]+)\z') {
            switch -CaseSensitive ($Matches[1]) {
                "OutputLimitExceeded" {
                    Fail-WindowsFinalArtifactCanaryScan -Code "ProcessOutputLimitExceeded"
                }
                "ProcessTimeout" {
                    Fail-WindowsFinalArtifactCanaryScan -Code "ProcessTimeout"
                }
                { $_ -in @(
                        "ProcessTerminationFailed",
                        "JobCreationFailed",
                        "JobConfigurationFailed",
                        "JobAssignmentFailed",
                        "JobCloseFailed") } {
                    Fail-WindowsFinalArtifactCanaryScan -Code "ProcessTerminationFailed"
                }
            }
        }
        Fail-WindowsFinalArtifactCanaryScan -Code "ProcessLaunchFailed"
    }
}

function Copy-WindowsFinalFileToStream {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][System.IO.Stream]$Destination
    )

    $source = [System.IO.File]::Open(
        $Path,
        [System.IO.FileMode]::Open,
        [System.IO.FileAccess]::Read,
        [System.IO.FileShare]::None)
    try {
        $source.CopyTo($Destination)
    }
    finally {
        $source.Dispose()
    }
}

function New-WindowsFinalCombinedLog {
    param(
        [Parameter(Mandatory = $true)]$ProcessResult,
        [Parameter(Mandatory = $true)][string]$DestinationPath,
        [Parameter(Mandatory = $true)][string]$AllowedRoot
    )

    $stdout = Assert-WindowsFinalRegularFile `
        -Path $ProcessResult.StandardOutputPath `
        -AllowedRoot $AllowedRoot `
        -MaximumBytes $script:windowsFinalMaximumLogBytes `
        -Code "ProcessOutputInvalid" `
        -AllowEmpty
    $stderr = Assert-WindowsFinalRegularFile `
        -Path $ProcessResult.StandardErrorPath `
        -AllowedRoot $AllowedRoot `
        -MaximumBytes $script:windowsFinalMaximumLogBytes `
        -Code "ProcessOutputInvalid" `
        -AllowEmpty
    $stdoutHeader = $script:windowsFinalUtf8NoBom.GetBytes(
        "=== PACKAGE_STDOUT ===`r`n")
    $stderrHeader = $script:windowsFinalUtf8NoBom.GetBytes(
        "`r`n=== PACKAGE_STDERR ===`r`n")
    [long]$requiredBytes =
        $stdoutHeader.Length + $stdout.Length + $stderrHeader.Length + $stderr.Length
    Assert-WindowsFinalCondition `
        ($requiredBytes -gt 0 -and
         $requiredBytes -le $script:windowsFinalMaximumLogBytes -and
         -not (Test-Path -LiteralPath $DestinationPath)) `
        "ProcessOutputLimitExceeded"

    $destination = [System.IO.File]::Open(
        $DestinationPath,
        [System.IO.FileMode]::CreateNew,
        [System.IO.FileAccess]::Write,
        [System.IO.FileShare]::None)
    try {
        $destination.Write($stdoutHeader, 0, $stdoutHeader.Length)
        Copy-WindowsFinalFileToStream `
            -Path $ProcessResult.StandardOutputPath -Destination $destination
        $destination.Write($stderrHeader, 0, $stderrHeader.Length)
        Copy-WindowsFinalFileToStream `
            -Path $ProcessResult.StandardErrorPath -Destination $destination
        $destination.Flush($true)
    }
    finally {
        $destination.Dispose()
    }
    [void](Assert-WindowsFinalRegularFile `
            -Path $DestinationPath `
            -AllowedRoot ([System.IO.Path]::GetDirectoryName($DestinationPath)) `
            -MaximumBytes $script:windowsFinalMaximumLogBytes `
            -Code "ProcessOutputInvalid")
}

function New-WindowsFinalFullLogScannerReport {
    param(
        [Parameter(Mandatory = $true)]$Paths
    )

    [void](Assert-WindowsFinalRegularFile `
            -Path $Paths.TestingAssemblyPath `
            -AllowedRoot $Paths.RepositoryRoot `
            -MaximumBytes 32MB `
            -Code "ScannerUnavailable")
    $scanner = Invoke-WindowsFinalBoundedProcess `
        -FilePath $Paths.DotNetPath `
        -ArgumentValues @(
            $Paths.TestingAssemblyPath,
            "scan-release-artifacts",
            $Paths.FullLogRoot,
            "M16",
            "FINAL_ARTIFACTS") `
        -WorkingDirectory $Paths.RepositoryRoot `
        -OutputDirectory $Paths.ScannerIoRoot `
        -ProcessKind "scanner"
    Assert-WindowsFinalCondition `
        ($scanner.ExitCode -eq 0) "FullLogScanRejected"
    Assert-WindowsFinalCondition `
        ($scanner.StandardOutputLength -gt 0 -and
         $scanner.StandardOutputLength -le 4096 -and
         $scanner.StandardErrorLength -eq 0) `
        "ScannerOutputInvalid"

    $record = Read-WindowsM16FinalStrictJson `
        -Path $scanner.StandardOutputPath `
        -InputRoot $Paths.ArtifactUmbrella
    $value = $record.Value
    Assert-WindowsM16FinalExactPropertySet -Value $value -Expected @(
        "schemaVersion",
        "profile",
        "result",
        "fileCount",
        "directoryCount",
        "totalFileBytes",
        "inventorySha256",
        "findingCount")
    Assert-WindowsM16FinalExactInteger `
        (Get-WindowsM16FinalExactProperty $value "schemaVersion") 1
    Assert-WindowsM16FinalExactString `
        (Get-WindowsM16FinalExactProperty $value "profile") "M16ReleaseCandidate"
    Assert-WindowsM16FinalExactString `
        (Get-WindowsM16FinalExactProperty $value "result") "clean"
    Assert-WindowsM16FinalIntegerRange `
        (Get-WindowsM16FinalExactProperty $value "fileCount") 1 25000
    Assert-WindowsM16FinalIntegerRange `
        (Get-WindowsM16FinalExactProperty $value "directoryCount") 0 25000
    Assert-WindowsM16FinalIntegerRange `
        (Get-WindowsM16FinalExactProperty $value "totalFileBytes") 1 8GB
    Assert-WindowsM16FinalPatternString `
        (Get-WindowsM16FinalExactProperty $value "inventorySha256") `
        '\A[0-9a-f]{64}\z'
    Assert-WindowsM16FinalExactInteger `
        (Get-WindowsM16FinalExactProperty $value "findingCount") 0

    $normalized = [pscustomobject][ordered]@{
        SurfaceId = "full-log"
        SchemaVersion = 1
        Profile = "M16ReleaseCandidate"
        Result = "clean"
        FileCount = [long](Get-WindowsM16FinalExactProperty $value "fileCount")
        DirectoryCount = [long](
            Get-WindowsM16FinalExactProperty $value "directoryCount")
        TotalFileBytes = [long](
            Get-WindowsM16FinalExactProperty $value "totalFileBytes")
        InventorySha256 = [string](
            Get-WindowsM16FinalExactProperty $value "inventorySha256")
        FindingCount = 0
    }
    $reportPath = [System.IO.Path]::Combine(
        $Paths.ScannerIoRoot,
        "full-log-report.json")
    Assert-WindowsFinalCondition `
        (-not (Test-Path -LiteralPath $reportPath)) "ScannerOutputInvalid"
    Write-WindowsM16FinalArtifactEvidenceAtomically `
        -Value $normalized `
        -DestinationPath $reportPath
    return $reportPath
}

function Get-WindowsFinalLockedStreamSha256 {
    param(
        [Parameter(Mandatory = $true)][System.IO.FileStream]$Stream
    )

    Assert-WindowsFinalCondition `
        ($Stream.CanRead -and $Stream.CanSeek -and $Stream.Length -gt 0 -and
         $Stream.Length -le 4GB) `
        "PackageBindingMismatch"
    $sha256 = [System.Security.Cryptography.SHA256]::Create()
    $hash = $null
    try {
        $Stream.Position = 0
        $hash = $sha256.ComputeHash($Stream)
        return (($hash | ForEach-Object { $_.ToString("x2") }) -join '')
    }
    finally {
        $Stream.Position = 0
        $sha256.Dispose()
        if ($null -ne $hash) {
            [System.Array]::Clear($hash, 0, $hash.Length)
        }
    }
}

function Get-WindowsFinalOuterExactPackageInventory {
    param(
        [Parameter(Mandatory = $true)]$Paths,
        [Parameter(Mandatory = $true)][string]$ExactPackageRoot
    )

    [void](Assert-WindowsFinalRegularFile `
            -Path $Paths.TestingAssemblyPath `
            -AllowedRoot $Paths.RepositoryRoot `
            -MaximumBytes 32MB `
            -Code "ScannerUnavailable")
    $scanner = Invoke-WindowsFinalBoundedProcess `
        -FilePath $Paths.DotNetPath `
        -ArgumentValues @(
            $Paths.TestingAssemblyPath,
            "scan-release-artifacts",
            $ExactPackageRoot,
            "M16",
            "FINAL_ARTIFACTS") `
        -WorkingDirectory $Paths.RepositoryRoot `
        -OutputDirectory $Paths.ScannerIoRoot `
        -ProcessKind "exact-package-scanner"
    Assert-WindowsFinalCondition `
        ($scanner.ExitCode -eq 0) "ExactPackageScanRejected"
    Assert-WindowsFinalCondition `
        ($scanner.StandardOutputLength -gt 0 -and
         $scanner.StandardOutputLength -le 4096 -and
         $scanner.StandardErrorLength -eq 0) `
        "ScannerOutputInvalid"

    $record = Read-WindowsM16FinalStrictJson `
        -Path $scanner.StandardOutputPath `
        -InputRoot $Paths.ArtifactUmbrella
    $value = $record.Value
    Assert-WindowsM16FinalExactPropertySet -Value $value -Expected @(
        "schemaVersion",
        "profile",
        "result",
        "fileCount",
        "directoryCount",
        "totalFileBytes",
        "inventorySha256",
        "findingCount")
    Assert-WindowsM16FinalExactInteger `
        (Get-WindowsM16FinalExactProperty $value "schemaVersion") 1
    Assert-WindowsM16FinalExactString `
        (Get-WindowsM16FinalExactProperty $value "profile") "M16ReleaseCandidate"
    Assert-WindowsM16FinalExactString `
        (Get-WindowsM16FinalExactProperty $value "result") "clean"
    $fileCount = Get-WindowsM16FinalExactProperty $value "fileCount"
    $directoryCount = Get-WindowsM16FinalExactProperty $value "directoryCount"
    Assert-WindowsM16FinalIntegerRange $fileCount 1 25000
    Assert-WindowsM16FinalIntegerRange $directoryCount 0 25000
    Assert-WindowsFinalCondition `
        (([long]$fileCount + [long]$directoryCount) -le 25000) `
        "ExactPackageScanRejected"
    Assert-WindowsM16FinalIntegerRange `
        (Get-WindowsM16FinalExactProperty $value "totalFileBytes") 1 8GB
    $inventorySha256 = Get-WindowsM16FinalExactProperty `
        $value "inventorySha256"
    Assert-WindowsM16FinalPatternString `
        $inventorySha256 '\A[0-9a-f]{64}\z'
    Assert-WindowsM16FinalExactInteger `
        (Get-WindowsM16FinalExactProperty $value "findingCount") 0
    return [string]$inventorySha256
}

function Get-WindowsFinalOuterPackageExpectation {
    param(
        [Parameter(Mandatory = $true)]$Paths,
        [Parameter(Mandatory = $true)]$RunPaths
    )

    $exactPackageRoot = [System.IO.Path]::GetFullPath(
        [System.IO.Path]::Combine($RunPaths.CaptureRoot, "exact-package"))
    Assert-WindowsFinalCondition `
        ([System.IO.Directory]::GetParent($exactPackageRoot).FullName.Equals(
            [System.IO.Path]::GetFullPath($RunPaths.CaptureRoot),
            [System.StringComparison]::OrdinalIgnoreCase) -and
         (Test-Path -LiteralPath $exactPackageRoot -PathType Container)) `
        "PackageBindingMismatch"
    Assert-WindowsFinalNoReparseDirectoryChain `
        -DirectoryPath $exactPackageRoot `
        -Code "PackageBindingMismatch"
    Assert-WindowsFinalNoNamedStreams `
        -Path $exactPackageRoot `
        -Code "PackageBindingMismatch"

    $packagePath = [System.IO.Path]::GetFullPath(
        [System.IO.Path]::Combine($exactPackageRoot, "package.msix"))
    [void](Assert-WindowsFinalRegularFile `
            -Path $packagePath `
            -AllowedRoot $exactPackageRoot `
            -MaximumBytes 4GB `
            -Code "PackageBindingMismatch")
    $packageLock = $null
    try {
        $packageLock = [System.IO.File]::Open(
            $packagePath,
            [System.IO.FileMode]::Open,
            [System.IO.FileAccess]::Read,
            [System.IO.FileShare]::Read)
        Assert-WindowsFinalCondition `
            ($packageLock.Length -gt 0 -and $packageLock.Length -le 4GB) `
            "PackageBindingMismatch"
        $packageSha256 = Get-WindowsFinalLockedStreamSha256 -Stream $packageLock
        $inventorySha256 = Get-WindowsFinalOuterExactPackageInventory `
            -Paths $Paths `
            -ExactPackageRoot $exactPackageRoot
        $postScanPackageSha256 = Get-WindowsFinalLockedStreamSha256 `
            -Stream $packageLock
        Assert-WindowsFinalCondition `
            ($packageSha256 -ceq $postScanPackageSha256) `
            "PackageBindingMismatch"
    }
    finally {
        if ($null -ne $packageLock) {
            $packageLock.Dispose()
        }
    }

    return [pscustomobject][ordered]@{
        PackageSha256 = $packageSha256
        PackageSbomApplicationPackageSha256 = $packageSha256
        ExactPackageInventorySha256 = $inventorySha256
    }
}

function Get-WindowsFinalPackageBinding {
    param(
        [Parameter(Mandatory = $true)]$Paths,
        [Parameter(Mandatory = $true)][string]$ExpectedRunId,
        [Parameter(Mandatory = $true)][string]$ExpectedCommit,
        [Parameter(Mandatory = $true)]
        [ValidatePattern('\A[0-9a-f]{64}\z')]
        [string]$ExpectedPackageSha256,
        [Parameter(Mandatory = $true)]
        [ValidatePattern('\A[0-9a-f]{64}\z')]
        [string]$ExpectedExactPackageInventorySha256
    )

    $record = Read-WindowsM16FinalStrictJson `
        -Path $Paths.PackageBindingPath `
        -InputRoot $Paths.ArtifactUmbrella
    $value = $record.Value
    Assert-WindowsM16FinalExactPropertySet -Value $value -Expected @(
        "SchemaVersion",
        "EvidenceKind",
        "RunId",
        "CommitSha",
        "PackageSha256",
        "PackageSbomApplicationPackageSha256",
        "ExactPackageInventorySha256",
        "PostScanPackageRehashPassed")
    Assert-WindowsM16FinalExactInteger `
        (Get-WindowsM16FinalExactProperty $value "SchemaVersion") 1
    Assert-WindowsM16FinalExactString `
        (Get-WindowsM16FinalExactProperty $value "EvidenceKind") `
        "PackageBoundFinalArtifactBinding"
    Assert-WindowsM16FinalExactString `
        (Get-WindowsM16FinalExactProperty $value "RunId") $ExpectedRunId
    Assert-WindowsM16FinalExactString `
        (Get-WindowsM16FinalExactProperty $value "CommitSha") $ExpectedCommit
    $packageSha256 = Get-WindowsM16FinalExactProperty $value "PackageSha256"
    $packageSbomSha256 = Get-WindowsM16FinalExactProperty `
        $value "PackageSbomApplicationPackageSha256"
    $exactPackageInventorySha256 = Get-WindowsM16FinalExactProperty `
        $value "ExactPackageInventorySha256"
    Assert-WindowsM16FinalPatternString $packageSha256 '\A[0-9a-f]{64}\z'
    Assert-WindowsM16FinalPatternString $packageSbomSha256 '\A[0-9a-f]{64}\z'
    Assert-WindowsM16FinalPatternString `
        $exactPackageInventorySha256 '\A[0-9a-f]{64}\z'
    Assert-WindowsFinalCondition `
        ($packageSha256 -ceq $ExpectedPackageSha256 -and
         $packageSbomSha256 -ceq $ExpectedPackageSha256 -and
         $exactPackageInventorySha256 -ceq
            $ExpectedExactPackageInventorySha256) `
        "PackageBindingMismatch"
    $postScanRehash = Get-WindowsM16FinalExactProperty `
        $value "PostScanPackageRehashPassed"
    Assert-WindowsFinalCondition `
        ($postScanRehash -is [bool] -and $postScanRehash) `
        "PackageBindingMismatch"

    return $true
}

function Get-WindowsFinalStableFailureCode {
    param(
        [Parameter(Mandatory = $true)][System.Management.Automation.ErrorRecord]$ErrorRecord
    )

    $message = $ErrorRecord.Exception.Message
    if ($message -cmatch
        '\AWindowsFinalArtifactCanaryScan:([A-Za-z][A-Za-z0-9]+)\z') {
        return $Matches[1]
    }
    if ($message -cmatch '\AM16FinalArtifactEvidence:[A-Za-z][A-Za-z0-9]+\z') {
        return "EvidenceRejected"
    }
    return "UnexpectedFailure"
}

function Assert-WindowsFinalOutputRootContract {
    param(
        [Parameter(Mandatory = $true)]$Paths,
        [switch]$RequireEvidence
    )

    $entries = @(Get-ChildItem -LiteralPath $Paths.OutputRoot -Force)
    $expectedCount = if ($RequireEvidence) { 1 } else { 0 }
    Assert-WindowsFinalCondition `
        ($entries.Count -eq $expectedCount) "OutputContractInvalid"
    foreach ($entry in $entries) {
        Assert-WindowsFinalCondition `
            ($entry.Name -ceq "last-success.json" -and
             -not $entry.PSIsContainer -and
             (($entry.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -eq 0) -and
             $entry.Length -gt 0 -and $entry.Length -le 64KB) `
            "OutputContractInvalid"
        Assert-WindowsFinalNoNamedStreams `
            -Path $entry.FullName `
            -Code "OutputContractInvalid"
    }
}

function Invoke-WindowsFinalArtifactCanaryScanCore {
    param(
        [Parameter(Mandatory = $true)]$Paths,
        [Parameter(Mandatory = $true)]
        [ValidatePattern('\A[0-9a-f]{32}\z')]
        [string]$RunToken
    )

    $failureCode = $null
    $cleanupFailed = $false
    $finalEvidence = $null
    $expectedCommit = $null
    $packageOwnershipCleanupRequired = $false
    $runPaths = Get-WindowsFinalPackageRunPaths `
        -Paths $Paths `
        -RunToken $RunToken

    try {
        $expectedCommit = Get-WindowsFinalCleanRepositoryCommit `
            -RepositoryRoot $Paths.RepositoryRoot
        Assert-WindowsFinalHostAndSdk -Paths $Paths
        Assert-WindowsFinalRepositoryStable `
            -RepositoryRoot $Paths.RepositoryRoot `
            -ExpectedCommit $expectedCommit
        Remove-WindowsFinalExactFile `
            -Path $Paths.PackageIntermediatePath `
            -ExpectedPath $Paths.PackageIntermediatePath `
            -ParentRoot $Paths.PackageArtifactRoot `
            -MaximumBytes 128KB
        Remove-WindowsFinalExactFile `
            -Path $Paths.PackageBindingPath `
            -ExpectedPath $Paths.PackageBindingPath `
            -ParentRoot $Paths.PackageArtifactRoot `
            -MaximumBytes 128KB

        . $Paths.EvidenceHelperPath
        . $Paths.BoundedProcessHelperPath
        $packageOwnershipCleanupRequired = $true
        Initialize-WindowsFinalPackageOwnership `
            -Paths $Paths `
            -RunPaths $runPaths
        $packageProcess = Invoke-WindowsFinalBoundedProcess `
            -FilePath $Paths.WindowsPowerShellPath `
            -ArgumentValues @(
                "-NoProfile",
                "-NonInteractive",
                "-ExecutionPolicy",
                "Bypass",
                "-File",
                $Paths.PackageSmokePath,
                "-Configuration",
                "Release",
                 "-DotNetPath",
                 $Paths.DotNetPath,
                 "-EmitM16FinalArtifactSurfaces",
                 "-M16RunToken",
                 $RunToken) `
            -WorkingDirectory $Paths.RepositoryRoot `
            -OutputDirectory $Paths.ProcessIoRoot `
            -ProcessKind "package"
        $combinedLogPath = [System.IO.Path]::Combine(
            $Paths.FullLogRoot,
            "package-smoke.log")
        New-WindowsFinalCombinedLog `
            -ProcessResult $packageProcess `
            -DestinationPath $combinedLogPath `
            -AllowedRoot $Paths.ProcessIoRoot
        Remove-WindowsFinalExactFile `
            -Path $packageProcess.StandardOutputPath `
            -ExpectedPath ([System.IO.Path]::Combine(
                $Paths.ProcessIoRoot,
                "package.stdout")) `
            -ParentRoot $Paths.ProcessIoRoot `
            -MaximumBytes $script:windowsFinalMaximumLogBytes
        Remove-WindowsFinalExactFile `
            -Path $packageProcess.StandardErrorPath `
            -ExpectedPath ([System.IO.Path]::Combine(
                $Paths.ProcessIoRoot,
                "package.stderr")) `
            -ParentRoot $Paths.ProcessIoRoot `
            -MaximumBytes $script:windowsFinalMaximumLogBytes
        Assert-WindowsFinalCondition `
            ($packageProcess.ExitCode -eq 0) "PackageSmokeFailed"

        Assert-WindowsFinalRepositoryStable `
            -RepositoryRoot $Paths.RepositoryRoot `
            -ExpectedCommit $expectedCommit
        $fullLogReportPath = New-WindowsFinalFullLogScannerReport -Paths $Paths
        $outerPackageExpectation = Get-WindowsFinalOuterPackageExpectation `
            -Paths $Paths `
            -RunPaths $runPaths
        [void](Get-WindowsFinalPackageBinding `
            -Paths $Paths `
            -ExpectedRunId $RunToken `
            -ExpectedCommit $expectedCommit `
            -ExpectedPackageSha256 $outerPackageExpectation.PackageSha256 `
            -ExpectedExactPackageInventorySha256 `
                $outerPackageExpectation.ExactPackageInventorySha256)
        $finalEvidence = New-WindowsM16FinalArtifactEvidence `
            -PackageIntermediatePath $Paths.PackageIntermediatePath `
            -FullLogScannerReportPath $fullLogReportPath `
            -InputRoot $Paths.ArtifactUmbrella `
            -ExpectedRunId $RunToken `
            -ExpectedCommitSha $expectedCommit `
            -ExpectedPackageSha256 $outerPackageExpectation.PackageSha256 `
            -ExpectedPackageSbomApplicationPackageSha256 `
                $outerPackageExpectation.PackageSbomApplicationPackageSha256 `
            -ExpectedExactPackageInventorySha256 `
                $outerPackageExpectation.ExactPackageInventorySha256
        Assert-WindowsFinalRepositoryStable `
            -RepositoryRoot $Paths.RepositoryRoot `
            -ExpectedCommit $expectedCommit
    }
    catch {
        $failureCode = Get-WindowsFinalStableFailureCode -ErrorRecord $_
    }
    finally {
        if ($packageOwnershipCleanupRequired) {
            try {
                Remove-WindowsFinalPackageSideState `
                    -Paths $Paths `
                    -RunPaths $runPaths `
                    -RunToken $RunToken
            }
            catch {
                $cleanupFailed = $true
            }
        }
        try {
            Remove-WindowsFinalExactFile `
                -Path $Paths.PackageIntermediatePath `
                -ExpectedPath $Paths.PackageIntermediatePath `
                -ParentRoot $Paths.PackageArtifactRoot `
                -MaximumBytes 128KB
        }
        catch {
            $cleanupFailed = $true
        }
        try {
            Remove-WindowsFinalExactFile `
                -Path $Paths.PackageBindingPath `
                -ExpectedPath $Paths.PackageBindingPath `
                -ParentRoot $Paths.PackageArtifactRoot `
                -MaximumBytes 128KB
        }
        catch {
            $cleanupFailed = $true
        }
        try {
            Remove-WindowsFinalExactDirectory `
                -Path $Paths.WorkRoot `
                -ExpectedPath $Paths.WorkRoot `
                -ParentRoot $Paths.OutputRoot
        }
        catch {
            $cleanupFailed = $true
        }
    }

    if ($cleanupFailed) {
        Fail-WindowsFinalArtifactCanaryScan -Code "CleanupFailed"
    }
    if ($null -ne $failureCode) {
        Fail-WindowsFinalArtifactCanaryScan -Code $failureCode
    }
    if ($null -eq $finalEvidence -or [string]::IsNullOrWhiteSpace($expectedCommit)) {
        Fail-WindowsFinalArtifactCanaryScan -Code "EvidenceUnavailable"
    }

    try {
        Assert-WindowsFinalRepositoryStable `
            -RepositoryRoot $Paths.RepositoryRoot `
            -ExpectedCommit $expectedCommit
        Assert-WindowsFinalOutputRootContract -Paths $Paths
        Write-WindowsM16FinalArtifactEvidenceAtomically `
            -Value $finalEvidence `
            -DestinationPath $Paths.FinalEvidencePath
        Assert-WindowsFinalOutputRootContract -Paths $Paths -RequireEvidence
    }
    catch {
        $code = Get-WindowsFinalStableFailureCode -ErrorRecord $_
        try {
            Remove-WindowsFinalExactFile `
                -Path $Paths.FinalEvidencePath `
                -ExpectedPath $Paths.FinalEvidencePath `
                -ParentRoot $Paths.OutputRoot `
                -MaximumBytes 64KB
        }
        catch {
            Fail-WindowsFinalArtifactCanaryScan -Code "CleanupFailed"
        }
        Fail-WindowsFinalArtifactCanaryScan -Code $code
    }
}

function Invoke-WindowsFinalArtifactCanaryScan {
    $paths = Get-WindowsFinalPaths
    $runMutex = Enter-WindowsFinalRunMutex `
        -RepositoryRoot $paths.RepositoryRoot
    $packageIdentityMutex = $null
    $runToken = $null
    $workspaceInvalidationAttempted = $false
    $primaryFailureCode = $null
    $releaseFailureCode = $null
    try {
        $workspaceInvalidationAttempted = $true
        Initialize-WindowsFinalWorkspace -Paths $paths
        $packageIdentityMutex = Enter-WindowsFinalPackageIdentityMutex
        Remove-WindowsFinalStalePackageOwnership -Paths $paths
        $runToken = [Guid]::NewGuid().ToString("N")
        Invoke-WindowsFinalArtifactCanaryScanCore `
            -Paths $paths `
            -RunToken $runToken
    }
    catch {
        $primaryFailureCode = Get-WindowsFinalStableFailureCode `
            -ErrorRecord $_
    }
    $rollbackFailed = $false
    if ($null -ne $primaryFailureCode -and $workspaceInvalidationAttempted) {
        try {
            Remove-WindowsFinalExactFile `
                -Path $paths.FinalEvidencePath `
                -ExpectedPath $paths.FinalEvidencePath `
                -ParentRoot $paths.OutputRoot `
                -MaximumBytes 64KB
        }
        catch {
            $rollbackFailed = $true
        }
        try {
            Remove-WindowsFinalExactDirectory `
                -Path $paths.WorkRoot `
                -ExpectedPath $paths.WorkRoot `
                -ParentRoot $paths.OutputRoot
        }
        catch {
            $rollbackFailed = $true
        }
    }
    foreach ($mutex in @($packageIdentityMutex, $runMutex)) {
        if ($null -eq $mutex) {
            continue
        }
        try {
            Exit-WindowsFinalRunMutex -Mutex $mutex
        }
        catch {
            if ($null -eq $releaseFailureCode) {
                $releaseFailureCode = Get-WindowsFinalStableFailureCode `
                    -ErrorRecord $_
            }
        }
    }

    if ($null -ne $releaseFailureCode -and
        $null -eq $primaryFailureCode -and
        $workspaceInvalidationAttempted) {
        try {
            Remove-WindowsFinalExactFile `
                -Path $paths.FinalEvidencePath `
                -ExpectedPath $paths.FinalEvidencePath `
                -ParentRoot $paths.OutputRoot `
                -MaximumBytes 64KB
        }
        catch {
            $rollbackFailed = $true
        }
        try {
            Remove-WindowsFinalExactDirectory `
                -Path $paths.WorkRoot `
                -ExpectedPath $paths.WorkRoot `
                -ParentRoot $paths.OutputRoot
        }
        catch {
            $rollbackFailed = $true
        }
    }

    if ($null -ne $primaryFailureCode -or $null -ne $releaseFailureCode) {
        if ($rollbackFailed) {
            Fail-WindowsFinalArtifactCanaryScan -Code "CleanupFailed"
        }
        if ($null -ne $primaryFailureCode) {
            Fail-WindowsFinalArtifactCanaryScan -Code $primaryFailureCode
        }
        Fail-WindowsFinalArtifactCanaryScan -Code $releaseFailureCode
    }

    Write-Output "M16 final artifact canary scan passed."
}

if ($MyInvocation.InvocationName -ne '.') {
    Invoke-WindowsFinalArtifactCanaryScan
}
