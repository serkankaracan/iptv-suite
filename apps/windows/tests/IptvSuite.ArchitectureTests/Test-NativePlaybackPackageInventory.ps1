#requires -Version 5.1

[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

Add-Type -AssemblyName System.IO.Compression

$script:utf8NoBom = New-Object System.Text.UTF8Encoding($false, $true)

function Assert-Test {
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

function Get-TestBytesSha256 {
    param(
        [Parameter(Mandatory)]
        [byte[]]$Bytes
    )

    $sha = [System.Security.Cryptography.SHA256]::Create()
    try {
        return ([System.BitConverter]::ToString($sha.ComputeHash($Bytes))).Replace("-", "").ToLowerInvariant()
    }
    finally {
        $sha.Dispose()
    }
}

function Get-TestStringSha256 {
    param(
        [Parameter(Mandatory)]
        [string]$Value
    )

    return Get-TestBytesSha256 -Bytes $script:utf8NoBom.GetBytes($Value)
}

function Get-TestDiagnosticStringSha256 {
    param(
        [Parameter(Mandatory)]
        [string]$Value
    )

    return Get-TestBytesSha256 -Bytes ([System.Text.Encoding]::Unicode.GetBytes($Value))
}

function Get-TestFileSha256 {
    param(
        [Parameter(Mandatory)]
        [string]$Path
    )

    return Get-TestBytesSha256 -Bytes ([System.IO.File]::ReadAllBytes($Path))
}

function Write-TestText {
    param(
        [Parameter(Mandatory)]
        [string]$Path,

        [Parameter(Mandatory)]
        [string]$Value
    )

    [System.IO.File]::WriteAllText($Path, $Value, $script:utf8NoBom)
}

function Write-TestJson {
    param(
        [Parameter(Mandatory)]
        [string]$Path,

        [Parameter(Mandatory)]
        [object]$Value
    )

    Write-TestText -Path $Path -Value ($Value | ConvertTo-Json -Depth 20)
}

function New-TestArchiveBytes {
    param(
        [Parameter(Mandatory)]
        [object[]]$Entries
    )

    $stream = New-Object System.IO.MemoryStream
    $archive = New-Object System.IO.Compression.ZipArchive(
        $stream,
        [System.IO.Compression.ZipArchiveMode]::Create,
        $true)
    try {
        foreach ($record in $Entries) {
            $entry = $archive.CreateEntry(
                [string]$record.Path,
                [System.IO.Compression.CompressionLevel]::Optimal)
            $entryStream = $entry.Open()
            try {
                $bytes = [byte[]]$record.Bytes
                $entryStream.Write($bytes, 0, $bytes.Length)
            }
            finally {
                $entryStream.Dispose()
            }
        }
    }
    finally {
        $archive.Dispose()
    }

    $bytes = $stream.ToArray()
    $stream.Dispose()
    return $bytes
}

function Write-TestArchive {
    param(
        [Parameter(Mandatory)]
        [string]$Path,

        [Parameter(Mandatory)]
        [object[]]$Entries
    )

    [System.IO.File]::WriteAllBytes($Path, (New-TestArchiveBytes -Entries $Entries))
}

function New-Entry {
    param(
        [Parameter(Mandatory)]
        [string]$Path,

        [Parameter(Mandatory)]
        [byte[]]$Bytes
    )

    return [pscustomobject]@{ Path = $Path; Bytes = $Bytes }
}

function Add-TestUtf8Bom {
    param(
        [Parameter(Mandatory)]
        [byte[]]$Bytes
    )

    $result = New-Object byte[] ($Bytes.Length + 3)
    $result[0] = 0xef
    $result[1] = 0xbb
    $result[2] = 0xbf
    [System.Array]::Copy($Bytes, 0, $result, 3, $Bytes.Length)
    return ,$result
}

function Copy-TestObject {
    param(
        [Parameter(Mandatory)]
        [object]$Value
    )

    return ($Value | ConvertTo-Json -Depth 20 | ConvertFrom-Json)
}

function Invoke-InventoryScenario {
    param(
        [Parameter(Mandatory)]
        [string]$Name,

        [Parameter(Mandatory)]
        [string]$Package,

        [Parameter(Mandatory)]
        [string]$RuntimePackage,

        [Parameter(Mandatory)]
        [string]$Manifest,

        [Parameter(Mandatory)]
        [string]$Specification,

        [Parameter(Mandatory)]
        [bool]$ExpectSuccess,

        [string]$ExpectedFailureCode = "",

        [string]$ExpectedDiagnostic = "",

        [AllowEmptyCollection()]
        [string[]]$ForbiddenOutputFragments = @(),

        [string]$LockFile = "",

        [string]$AssetsFile = ""
    )

    $evidence = Join-Path $script:testRoot "$Name-evidence.json"
    $effectiveLockFile = if ([string]::IsNullOrWhiteSpace($LockFile)) {
        $script:lockPath
    }
    else {
        $LockFile
    }
    $effectiveAssetsFile = if ([string]::IsNullOrWhiteSpace($AssetsFile)) {
        $script:assetsPath
    }
    else {
        $AssetsFile
    }
    $savedPreference = $ErrorActionPreference
    try {
        $ErrorActionPreference = "Continue"
        $output = & powershell.exe `
            -NoProfile `
            -ExecutionPolicy Bypass `
            -File $script:validatorPath `
            -PackagePath $Package `
            -RuntimePackagePath $RuntimePackage `
            -LockFilePath $effectiveLockFile `
            -AssetsFilePath $effectiveAssetsFile `
            -DepsFilePath $script:depsPath `
            -ManifestPath $Manifest `
            -SpecificationPath $Specification `
            -EvidencePath $evidence 2>&1
        $exitCode = $LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $savedPreference
    }
    if ($ExpectSuccess) {
        Assert-Test ($exitCode -eq 0) "Positive inventory scenario failed: $output"
        Assert-Test (Test-Path -LiteralPath $evidence -PathType Leaf) "Positive inventory evidence is missing."
        $evidenceText = [System.IO.File]::ReadAllText($evidence, $script:utf8NoBom)
        Assert-Test `
            (-not $evidenceText.Contains($script:testRoot)) `
            "Inventory evidence disclosed an absolute test path."
        $parsed = $evidenceText | ConvertFrom-Json
        Assert-Test `
            ($parsed.Result -eq "Pass" -and
                $parsed.Stage -eq "NativePackageInventory" -and
                $parsed.SchemaVersion -eq 1 -and
                $parsed.PackageEntryCount -eq 11 -and
                @($parsed.LockTargets).Count -eq 2 -and
                @($parsed.RuntimeExecutables).Count -eq 2 -and
                @($parsed.AppOwnedFiles).Count -eq 2 -and
                @($parsed.Packages).Count -eq 1 -and
                $parsed.Packages[0].Id -eq "Synthetic.Dependency") `
            "Positive inventory evidence has an invalid result envelope."
    }
    else {
        $outputText = @($output | ForEach-Object { [string]$_ }) -join "`n"
        Assert-Test ($exitCode -ne 0) "Negative inventory scenario '$Name' unexpectedly passed."
        Assert-Test (-not (Test-Path -LiteralPath $evidence)) "A failed inventory scenario published evidence."
        Assert-Test `
            (-not [string]::IsNullOrWhiteSpace($ExpectedFailureCode)) `
            "Negative inventory scenario '$Name' has no expected failure code."
        Assert-Test `
            ($outputText.Contains($ExpectedFailureCode)) `
            "Negative inventory scenario '$Name' failed for an unexpected reason: $output"
        $directFailureMessage = ""
        if (-not [string]::IsNullOrWhiteSpace($ExpectedDiagnostic)) {
            $directEvidence = Join-Path $script:testRoot "$Name-direct-evidence.json"
            $directSavedPreference = $ErrorActionPreference
            try {
                $ErrorActionPreference = "Stop"
                & $script:validatorPath `
                    -PackagePath $Package `
                    -RuntimePackagePath $RuntimePackage `
                    -LockFilePath $effectiveLockFile `
                    -AssetsFilePath $effectiveAssetsFile `
                    -DepsFilePath $script:depsPath `
                    -ManifestPath $Manifest `
                    -SpecificationPath $Specification `
                    -EvidencePath $directEvidence | Out-Null
            }
            catch {
                $directFailureMessage = [string]$_.Exception.Message
            }
            finally {
                $ErrorActionPreference = $directSavedPreference
            }
            Assert-Test `
                ([string]::Equals(
                        $directFailureMessage,
                        $ExpectedDiagnostic,
                        [System.StringComparison]::Ordinal)) `
                "Negative inventory scenario '$Name' did not emit the exact expected diagnostic: $output"
            Assert-Test `
                (-not (Test-Path -LiteralPath $directEvidence)) `
                "A direct failed inventory scenario published evidence."
        }
        foreach ($fragment in $ForbiddenOutputFragments) {
            Assert-Test `
                (-not $outputText.Contains($fragment) -and
                    -not $directFailureMessage.Contains($fragment)) `
                "Negative inventory scenario '$Name' disclosed a forbidden diagnostic fragment."
        }
    }
}

$script:validatorPath = [System.IO.Path]::GetFullPath(
    (Join-Path $PSScriptRoot "..\..\..\..\eng\Test-WindowsNativePlaybackPackageInventory.ps1"))
Assert-Test (Test-Path -LiteralPath $script:validatorPath -PathType Leaf) "Inventory validator is missing."

$artifactRoot = [System.IO.Path]::GetFullPath(
    (Join-Path $PSScriptRoot "..\..\..\..\.artifacts\native-package-inventory-self-test"))
if (-not (Test-Path -LiteralPath $artifactRoot -PathType Container)) {
    [void][System.IO.Directory]::CreateDirectory($artifactRoot)
}
$script:testRoot = Join-Path $artifactRoot ([guid]::NewGuid().ToString("N"))
[void][System.IO.Directory]::CreateDirectory($script:testRoot)

try {
    $packageFolder = Join-Path $script:testRoot "packages"
    $restoredPackage = Join-Path $packageFolder "synthetic.dependency\1.2.3"
    [void][System.IO.Directory]::CreateDirectory($restoredPackage)

    $dependencyBytes = $script:utf8NoBom.GetBytes("synthetic dependency binary v1")
    $toolchainBytes = $script:utf8NoBom.GetBytes("synthetic runtime pack binary v1")
    $appExeBytes = $script:utf8NoBom.GetBytes("synthetic app host v1")
    $appDllBytes = $script:utf8NoBom.GetBytes("synthetic app assembly v1")
    $runtimeBytes = $script:utf8NoBom.GetBytes("synthetic runtime executable v1")
    $nestedRuntimeBytes = $script:utf8NoBom.GetBytes("synthetic nested runtime executable v1")
    $licenseText = "Synthetic permissive license text."
    $noticeText = "Synthetic dependency notice."
    $nuspecText = @'
<?xml version="1.0" encoding="utf-8"?>
<package>
  <metadata>
    <id>Synthetic.Dependency</id>
    <version>1.2.3</version>
    <license type="file">LICENSE.txt</license>
    <licenseUrl>https://aka.ms/deprecateLicenseUrl</licenseUrl>
    <repository type="git" commit="synthetic-commit" />
    <projectUrl>https://github.com/example/synthetic-dependency</projectUrl>
  </metadata>
</package>
'@
    Write-TestText -Path (Join-Path $restoredPackage "synthetic.dependency.nuspec") -Value $nuspecText
    Write-TestText -Path (Join-Path $restoredPackage "LICENSE.txt") -Value $licenseText
    Write-TestText -Path (Join-Path $restoredPackage "NOTICE.txt") -Value $noticeText
    [void][System.IO.Directory]::CreateDirectory((Join-Path $restoredPackage "runtimes\win-x64\native"))
    [System.IO.File]::WriteAllBytes(
        (Join-Path $restoredPackage "runtimes\win-x64\native\Synthetic.Dependency.dll"),
        $dependencyBytes)

    $sourceManifestText = @'
<?xml version="1.0" encoding="utf-8"?>
<Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10" xmlns:rescap="http://schemas.microsoft.com/appx/manifest/foundation/windows10/restrictedcapabilities" IgnorableNamespaces="rescap">
  <Identity Name="Synthetic.NativePlayback.Local" Publisher="CN=Synthetic Local Test" Version="0.0.1.0" ProcessorArchitecture="x64" />
  <Dependencies><TargetDeviceFamily Name="Windows.Desktop" MinVersion="10.0.26100.0" MaxVersionTested="10.0.26100.0" /></Dependencies>
  <Applications><Application Id="App" Executable="Harness.exe" EntryPoint="Harness.App" /></Applications>
  <Capabilities><rescap:Capability Name="runFullTrust" /></Capabilities>
</Package>
'@
    $manifestPath = Join-Path $script:testRoot "Package.appxmanifest"
    Write-TestText -Path $manifestPath -Value $sourceManifestText
    $embeddedManifestText = $sourceManifestText.Replace(
        "</Dependencies>",
        "<PackageDependency Name=`"Microsoft.WindowsAppRuntime.2`" Publisher=`"CN=Microsoft Corporation`" MinVersion=`"2.4.0.0`" /></Dependencies>")
    $extensionText = "<Extensions><Extension Category=`"windows.activatableClass.inProcessServer`"><InProcessServer><Path>Synthetic.Dependency.dll</Path><ActivatableClass ActivatableClassId=`"Synthetic.Dependency.Component`" ThreadingModel=`"both`" /></InProcessServer></Extension></Extensions>"
    $embeddedManifestText = $embeddedManifestText.Replace(
        "</Applications>",
        "</Applications>$extensionText")

    $depsObject = [ordered]@{
        runtimeTarget = [ordered]@{
            name = ".NETCoreApp,Version=v10.0/win-x64"
            signature = ""
        }
        targets = [ordered]@{
            ".NETCoreApp,Version=v10.0/win-x64" = [ordered]@{
                "Synthetic.Dependency/1.2.3" = [ordered]@{
                    runtimeTargets = [ordered]@{
                        "runtimes/win-x64/native/Synthetic.Dependency.dll" = [ordered]@{
                            rid = "win-x64"
                            assetType = "native"
                        }
                    }
                }
                "runtimepack.Synthetic/1.0.0" = [ordered]@{
                    runtime = [ordered]@{
                        "System.Tool.dll" = [ordered]@{}
                    }
                }
            }
        }
        libraries = [ordered]@{
            "Synthetic.Dependency/1.2.3" = [ordered]@{ type = "package"; serviceable = $true }
            "runtimepack.Synthetic/1.0.0" = [ordered]@{ type = "runtimepack"; serviceable = $false }
        }
    }
    $script:depsPath = Join-Path $script:testRoot "Harness.deps.json"
    Write-TestJson -Path $script:depsPath -Value $depsObject
    [System.IO.File]::WriteAllBytes((Join-Path $script:testRoot "Harness.dll"), $appDllBytes)
    [System.IO.File]::WriteAllBytes((Join-Path $script:testRoot "Harness.exe"), $appExeBytes)

    $appEntries = @(
        (New-Entry -Path "[Content_Types].xml" -Bytes $script:utf8NoBom.GetBytes("<Types />")),
        (New-Entry -Path "AppxBlockMap.xml" -Bytes $script:utf8NoBom.GetBytes("<BlockMap />")),
        (New-Entry -Path "AppxManifest.xml" -Bytes (Add-TestUtf8Bom -Bytes $script:utf8NoBom.GetBytes($embeddedManifestText))),
        (New-Entry -Path "AppxMetadata/CodeIntegrity.cat" -Bytes $script:utf8NoBom.GetBytes("synthetic catalog bytes")),
        (New-Entry -Path "AppxSignature.p7x" -Bytes $script:utf8NoBom.GetBytes("synthetic signature bytes")),
        (New-Entry -Path "Assets/Logo.png" -Bytes ([byte[]](1, 2, 3, 4))),
        (New-Entry -Path "Harness.deps.json" -Bytes $script:utf8NoBom.GetBytes(($depsObject | ConvertTo-Json -Depth 20))),
        (New-Entry -Path "Harness.dll" -Bytes $appDllBytes),
        (New-Entry -Path "Harness.exe" -Bytes $appExeBytes),
        (New-Entry -Path "Synthetic.Dependency.dll" -Bytes $dependencyBytes),
        (New-Entry -Path "System.Tool.dll" -Bytes $toolchainBytes)
    )
    $packagePath = Join-Path $script:testRoot "Harness.msix"
    Write-TestArchive -Path $packagePath -Entries $appEntries

    $nestedManifestText = @'
<?xml version="1.0" encoding="utf-8"?>
<Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10"><Identity Name="Synthetic.Nested" Publisher="CN=Synthetic Local Test" Version="1.0.0.0" ProcessorArchitecture="x64" /></Package>
'@
    $nestedEntries = @(
        (New-Entry -Path "[Content_Types].xml" -Bytes $script:utf8NoBom.GetBytes("<Types />")),
        (New-Entry -Path "AppxManifest.xml" -Bytes $script:utf8NoBom.GetBytes($nestedManifestText)),
        (New-Entry -Path "NestedRuntime.dll" -Bytes $nestedRuntimeBytes)
    )
    $nestedBytes = New-TestArchiveBytes -Entries $nestedEntries
    $runtimeManifestText = @'
<?xml version="1.0" encoding="utf-8"?>
<Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10"><Identity Name="Microsoft.WindowsAppRuntime.2" Publisher="CN=Microsoft Corporation" Version="2.4.0.0" ProcessorArchitecture="x64" /></Package>
'@
    $runtimeEntries = @(
        (New-Entry -Path "[Content_Types].xml" -Bytes $script:utf8NoBom.GetBytes("<Types />")),
        (New-Entry -Path "AppxBlockMap.xml" -Bytes $script:utf8NoBom.GetBytes("<BlockMap />")),
        (New-Entry -Path "AppxManifest.xml" -Bytes (Add-TestUtf8Bom -Bytes $script:utf8NoBom.GetBytes($runtimeManifestText))),
        (New-Entry -Path "Runtime.dll" -Bytes $runtimeBytes),
        (New-Entry -Path "MSIX/Nested.msix" -Bytes $nestedBytes)
    )
    $runtimePath = Join-Path $script:testRoot "Microsoft.WindowsAppRuntime.2.msix"
    Write-TestArchive -Path $runtimePath -Entries $runtimeEntries

    $contentHash = "synthetic-content-hash"
    $lockTarget = [ordered]@{
        "Synthetic.Dependency" = [ordered]@{
            type = "Direct"
            resolved = "1.2.3"
            contentHash = $contentHash
        }
    }
    $ridLockTarget = [ordered]@{
        "Synthetic.Rid.Dependency" = [ordered]@{
            type = "Transitive"
            resolved = "4.5.6"
            contentHash = "synthetic-rid-content-hash"
        }
    }
    $lockObject = [ordered]@{
        version = 2
        dependencies = [ordered]@{
            "net10.0-windows10.0.26100" = $lockTarget
            "net10.0-windows10.0.26100/win-x64" = $ridLockTarget
        }
    }
    $script:lockPath = Join-Path $script:testRoot "packages.lock.json"
    Write-TestJson -Path $script:lockPath -Value $lockObject

    $packageFolders = [ordered]@{}
    $packageFolders[$packageFolder + [System.IO.Path]::DirectorySeparatorChar] = [ordered]@{}
    $libraries = [ordered]@{}
    $libraries["Synthetic.Dependency/1.2.3"] = [ordered]@{
        sha512 = $contentHash
        type = "package"
        path = "synthetic.dependency/1.2.3"
        files = @(
            "synthetic.dependency.nuspec",
            "LICENSE.txt",
            "NOTICE.txt",
            "runtimes/win-x64/native/Synthetic.Dependency.dll"
        )
    }
    $assetsObject = [ordered]@{
        packageFolders = $packageFolders
        libraries = $libraries
    }
    $script:assetsPath = Join-Path $script:testRoot "project.assets.json"
    Write-TestJson -Path $script:assetsPath -Value $assetsObject

    $specification = [ordered]@{
        SchemaVersion = 1
        Project = "Synthetic.NativePlayback"
        LockTargets = @(
            [ordered]@{
                Name = "net10.0-windows10.0.26100"
                Packages = $lockTarget
            },
            [ordered]@{
                Name = "net10.0-windows10.0.26100/win-x64"
                Packages = $ridLockTarget
            }
        )
        RuntimeIdentifier = "win-x64"
        PackageIdentity = [ordered]@{
            Name = "Synthetic.NativePlayback.Local"
            Publisher = "CN=Synthetic Local Test"
            Version = "0.0.1.0"
            Architecture = "x64"
        }
        TargetDeviceFamily = [ordered]@{
            Name = "Windows.Desktop"
            MinVersion = "10.0.26100.0"
            MaxVersionTested = "10.0.26100.0"
        }
        RuntimeDependency = [ordered]@{
            Name = "Microsoft.WindowsAppRuntime.2"
            Publisher = "CN=Microsoft Corporation"
            MinVersion = "2.4.0.0"
        }
        Capabilities = @("runFullTrust")
        Application = [ordered]@{
            Id = "App"
            SourceExecutable = "Harness.exe"
            SourceEntryPoint = "Harness.App"
            PackagedExecutable = "Harness.exe"
            PackagedEntryPoint = "Harness.App"
            PackagedRuntimeBehavior = ""
            PackagedTrustLevel = ""
        }
        Extensions = @([ordered]@{
                NamespaceUri = "http://schemas.microsoft.com/appx/manifest/foundation/windows10"
                Category = "windows.activatableClass.inProcessServer"
                InProcessServerPath = "Synthetic.Dependency.dll"
                ActivatableClasses = @([ordered]@{
                        Id = "Synthetic.Dependency.Component"
                        ThreadingModel = "both"
                    })
            })
        Packages = @([ordered]@{
                Id = "Synthetic.Dependency"
                Version = "1.2.3"
                Type = "Direct"
                ContentHash = $contentHash
                License = [ordered]@{
                    Kind = "file"
                    Value = "LICENSE.txt"
                    Sha256 = Get-TestFileSha256 -Path (Join-Path $restoredPackage "LICENSE.txt")
                }
                SourceUri = "https://github.com/example/synthetic-dependency"
                Notices = @([ordered]@{
                        Path = "NOTICE.txt"
                        Sha256 = Get-TestFileSha256 -Path (Join-Path $restoredPackage "NOTICE.txt")
                    })
                NuspecSha256 = Get-TestFileSha256 -Path (Join-Path $restoredPackage "synthetic.dependency.nuspec")
                Payload = @([ordered]@{
                        PackageEntryPath = "Synthetic.Dependency.dll"
                        RestoredPath = "runtimes/win-x64/native/Synthetic.Dependency.dll"
                        Sha256 = Get-TestBytesSha256 -Bytes $dependencyBytes
                    })
            })
        ToolchainPayload = @([ordered]@{
                Path = "System.Tool.dll"
                Sha256 = Get-TestBytesSha256 -Bytes $toolchainBytes
                OriginKind = "runtimepack"
                OriginVersion = "1.0.0"
            })
        AppOwnedFiles = @(
            [ordered]@{
                PackageEntryPath = "Harness.dll"
                OutputSiblingPath = "Harness.dll"
            },
            [ordered]@{
                PackageEntryPath = "Harness.exe"
                OutputSiblingPath = "Harness.exe"
            }
        )
        AllowedPackageEntries = @($appEntries | ForEach-Object { $_.Path })
        RuntimePackage = [ordered]@{
            FileName = "Microsoft.WindowsAppRuntime.2.msix"
            Sha256 = Get-TestFileSha256 -Path $runtimePath
            Identity = "Microsoft.WindowsAppRuntime.2"
            Publisher = "CN=Microsoft Corporation"
            Version = "2.4.0.0"
            Architecture = "x64"
            AllowedTopLevelEntries = @($runtimeEntries | ForEach-Object { $_.Path })
            ExecutableEntries = @(
                [ordered]@{
                    Path = "Runtime.dll"
                    Sha256 = Get-TestBytesSha256 -Bytes $runtimeBytes
                },
                [ordered]@{
                    Path = "MSIX/Nested.msix!/NestedRuntime.dll"
                    Sha256 = Get-TestBytesSha256 -Bytes $nestedRuntimeBytes
                }
            )
        }
    }
    $specificationPath = Join-Path $script:testRoot "inventory-spec.json"
    Write-TestJson -Path $specificationPath -Value $specification

    Invoke-InventoryScenario `
        -Name "positive" `
        -Package $packagePath `
        -RuntimePackage $runtimePath `
        -Manifest $manifestPath `
        -Specification $specificationPath `
        -ExpectSuccess $true

    $safeMissingEntry = "Diagnostics/Missing.txt"
    $safeUnexpectedEntry = "Diagnostics/Unexpected.txt"
    $safeMismatchEntries = @(
        $appEntries +
            (New-Entry -Path $safeUnexpectedEntry -Bytes $script:utf8NoBom.GetBytes("synthetic diagnostic")))
    $safeMismatchPackage = Join-Path $script:testRoot "safe-allowlist-mismatch.msix"
    Write-TestArchive -Path $safeMismatchPackage -Entries $safeMismatchEntries
    $safeMismatchSpecification = Copy-TestObject -Value $specification
    $safeMismatchSpecification.AllowedPackageEntries = @(
        $safeMismatchSpecification.AllowedPackageEntries + $safeMissingEntry)
    $safeMismatchSpecificationPath = Join-Path $script:testRoot "safe-allowlist-mismatch-spec.json"
    Write-TestJson -Path $safeMismatchSpecificationPath -Value $safeMismatchSpecification
    $safeMismatchDiagnostic =
        "Native package inventory validation failed: PackageEntryAllowlistMismatch. " +
        "Missing=[`"$safeMissingEntry`"]; Unexpected=[`"$safeUnexpectedEntry`"]."
    Invoke-InventoryScenario `
        -Name "safe-allowlist-mismatch" `
        -Package $safeMismatchPackage `
        -RuntimePackage $runtimePath `
        -Manifest $manifestPath `
        -Specification $safeMismatchSpecificationPath `
        -ExpectSuccess $false `
        -ExpectedFailureCode "PackageEntryAllowlistMismatch" `
        -ExpectedDiagnostic $safeMismatchDiagnostic

    $unsafeUnexpectedEntry = "Diagnostics/Unexpected.txt?userinfo=synthetic-value"
    $unsafeMismatchPackage = Join-Path $script:testRoot "unsafe-allowlist-mismatch.msix"
    Write-TestArchive -Path $unsafeMismatchPackage -Entries @(
        $appEntries +
            (New-Entry -Path $unsafeUnexpectedEntry -Bytes $script:utf8NoBom.GetBytes("synthetic diagnostic")))
    $unsafeEntryHash = Get-TestDiagnosticStringSha256 -Value $unsafeUnexpectedEntry
    $unsafeMismatchDiagnostic =
        "Native package inventory validation failed: PackageEntryAllowlistMismatch. " +
        "Missing=[]; Unexpected=[`"<redacted-sha256:$unsafeEntryHash>`"]."
    Invoke-InventoryScenario `
        -Name "unsafe-allowlist-mismatch" `
        -Package $unsafeMismatchPackage `
        -RuntimePackage $runtimePath `
        -Manifest $manifestPath `
        -Specification $specificationPath `
        -ExpectSuccess $false `
        -ExpectedFailureCode "PackageEntryAllowlistMismatch" `
        -ExpectedDiagnostic $unsafeMismatchDiagnostic `
        -ForbiddenOutputFragments @($unsafeUnexpectedEntry, "synthetic-value")

    $mutatedAppOwnedEntries = @($appEntries | ForEach-Object {
        if ([string]::Equals($_.Path, "Harness.dll", [System.StringComparison]::Ordinal)) {
            New-Entry `
                -Path $_.Path `
                -Bytes $script:utf8NoBom.GetBytes("mutated app assembly with the same package name")
        }
        else {
            $_
        }
    })
    $mutatedAppOwnedPackage = Join-Path $script:testRoot "mutated-app-owned.msix"
    Write-TestArchive -Path $mutatedAppOwnedPackage -Entries $mutatedAppOwnedEntries
    Invoke-InventoryScenario `
        -Name "mutated-app-owned" `
        -Package $mutatedAppOwnedPackage `
        -RuntimePackage $runtimePath `
        -Manifest $manifestPath `
        -Specification $specificationPath `
        -ExpectSuccess $false `
        -ExpectedFailureCode "AppOwnedPayloadMismatch"

    $extraTargetLock = Copy-TestObject -Value $lockObject
    $extraTargetLock.dependencies | Add-Member `
        -MemberType NoteProperty `
        -Name "net10.0-windows10.0.26100/win-arm64" `
        -Value ([pscustomobject]@{})
    $extraTargetLockPath = Join-Path $script:testRoot "extra-target-packages.lock.json"
    Write-TestJson -Path $extraTargetLockPath -Value $extraTargetLock
    Invoke-InventoryScenario `
        -Name "extra-lock-target" `
        -Package $packagePath `
        -RuntimePackage $runtimePath `
        -Manifest $manifestPath `
        -Specification $specificationPath `
        -ExpectSuccess $false `
        -ExpectedFailureCode "LockTargetsMismatch" `
        -LockFile $extraTargetLockPath

    $driftedRidLock = Copy-TestObject -Value $lockObject
    $driftedRidLock.dependencies."net10.0-windows10.0.26100/win-x64"."Synthetic.Rid.Dependency".resolved =
        "4.5.7"
    $driftedRidLockPath = Join-Path $script:testRoot "drifted-rid-packages.lock.json"
    Write-TestJson -Path $driftedRidLockPath -Value $driftedRidLock
    Invoke-InventoryScenario `
        -Name "drifted-rid-lock-target" `
        -Package $packagePath `
        -RuntimePackage $runtimePath `
        -Manifest $manifestPath `
        -Specification $specificationPath `
        -ExpectSuccess $false `
        -ExpectedFailureCode "LockTargetsMismatch" `
        -LockFile $driftedRidLockPath

    $testNuspecPath = Join-Path $restoredPackage "synthetic.dependency.nuspec"
    $expressionLicenseNuspecText = $nuspecText.Replace(
        "<license type=`"file`">LICENSE.txt</license>",
        "<license type=`"expression`">MIT</license>").Replace(
        "https://aka.ms/deprecateLicenseUrl",
        "https://licenses.nuget.org/MIT")
    try {
        Write-TestText -Path $testNuspecPath -Value $expressionLicenseNuspecText
        $expressionLicenseSpecification = Copy-TestObject -Value $specification
        $expressionLicenseSpecification.Packages[0].License.Kind = "expression"
        $expressionLicenseSpecification.Packages[0].License.Value = "MIT"
        $expressionLicenseSpecification.Packages[0].License.Sha256 =
            Get-TestStringSha256 -Value "MIT"
        $expressionLicenseSpecification.Packages[0].NuspecSha256 =
            Get-TestFileSha256 -Path $testNuspecPath
        $expressionLicenseSpecificationPath =
            Join-Path $script:testRoot "expression-license-spec.json"
        Write-TestJson `
            -Path $expressionLicenseSpecificationPath `
            -Value $expressionLicenseSpecification
        Invoke-InventoryScenario `
            -Name "expression-license" `
            -Package $packagePath `
            -RuntimePackage $runtimePath `
            -Manifest $manifestPath `
            -Specification $expressionLicenseSpecificationPath `
            -ExpectSuccess $true
    }
    finally {
        Write-TestText -Path $testNuspecPath -Value $nuspecText
    }

    $missingSourceNuspecText = $nuspecText.Replace(
        "    <projectUrl>https://github.com/example/synthetic-dependency</projectUrl>",
        "")
    try {
        Write-TestText -Path $testNuspecPath -Value $missingSourceNuspecText
        $missingSourceSpecification = Copy-TestObject -Value $specification
        $missingSourceSpecification.Packages[0].NuspecSha256 =
            Get-TestFileSha256 -Path $testNuspecPath
        $missingSourceSpecificationPath = Join-Path $script:testRoot "missing-source-spec.json"
        Write-TestJson `
            -Path $missingSourceSpecificationPath `
            -Value $missingSourceSpecification
        Invoke-InventoryScenario `
            -Name "missing-source" `
            -Package $packagePath `
            -RuntimePackage $runtimePath `
            -Manifest $manifestPath `
            -Specification $missingSourceSpecificationPath `
            -ExpectSuccess $false `
            -ExpectedFailureCode "PackageTupleMismatch"
    }
    finally {
        Write-TestText -Path $testNuspecPath -Value $nuspecText
    }

    $repositoryUrlNuspecText = $nuspecText.Replace(
        "<repository type=`"git`" commit=`"synthetic-commit`" />",
        "<repository type=`"git`" commit=`"synthetic-commit`" url=`"https://github.com/example/synthetic-repository`" />")
    try {
        Write-TestText -Path $testNuspecPath -Value $repositoryUrlNuspecText
        $repositoryUrlSpecification = Copy-TestObject -Value $specification
        $repositoryUrlSpecification.Packages[0].SourceUri =
            "https://github.com/example/synthetic-repository"
        $repositoryUrlSpecification.Packages[0].NuspecSha256 =
            Get-TestFileSha256 -Path $testNuspecPath
        $repositoryUrlSpecificationPath = Join-Path $script:testRoot "repository-url-spec.json"
        Write-TestJson `
            -Path $repositoryUrlSpecificationPath `
            -Value $repositoryUrlSpecification
        Invoke-InventoryScenario `
            -Name "repository-url" `
            -Package $packagePath `
            -RuntimePackage $runtimePath `
            -Manifest $manifestPath `
            -Specification $repositoryUrlSpecificationPath `
            -ExpectSuccess $true
    }
    finally {
        Write-TestText -Path $testNuspecPath -Value $nuspecText
    }

    $emptyRepositoryUrlNuspecText = $nuspecText.Replace(
        "<repository type=`"git`" commit=`"synthetic-commit`" />",
        "<repository type=`"git`" commit=`"synthetic-commit`" url=`"`" />")
    try {
        Write-TestText -Path $testNuspecPath -Value $emptyRepositoryUrlNuspecText
        $emptyRepositoryUrlSpecification = Copy-TestObject -Value $specification
        $emptyRepositoryUrlSpecification.Packages[0].NuspecSha256 =
            Get-TestFileSha256 -Path $testNuspecPath
        $emptyRepositoryUrlSpecificationPath =
            Join-Path $script:testRoot "empty-repository-url-spec.json"
        Write-TestJson `
            -Path $emptyRepositoryUrlSpecificationPath `
            -Value $emptyRepositoryUrlSpecification
        Invoke-InventoryScenario `
            -Name "empty-repository-url" `
            -Package $packagePath `
            -RuntimePackage $runtimePath `
            -Manifest $manifestPath `
            -Specification $emptyRepositoryUrlSpecificationPath `
            -ExpectSuccess $false `
            -ExpectedFailureCode "PackageTupleMismatch"
    }
    finally {
        Write-TestText -Path $testNuspecPath -Value $nuspecText
    }

    $ambiguousRepositoryUrlNuspecText = $nuspecText.Replace(
        "<repository type=`"git`" commit=`"synthetic-commit`" />",
        "<repository type=`"git`" commit=`"synthetic-commit`" URL=`"https://github.com/example/synthetic-repository`" />")
    try {
        Write-TestText -Path $testNuspecPath -Value $ambiguousRepositoryUrlNuspecText
        $ambiguousRepositoryUrlSpecification = Copy-TestObject -Value $specification
        $ambiguousRepositoryUrlSpecification.Packages[0].NuspecSha256 =
            Get-TestFileSha256 -Path $testNuspecPath
        $ambiguousRepositoryUrlSpecificationPath =
            Join-Path $script:testRoot "ambiguous-repository-url-spec.json"
        Write-TestJson `
            -Path $ambiguousRepositoryUrlSpecificationPath `
            -Value $ambiguousRepositoryUrlSpecification
        Invoke-InventoryScenario `
            -Name "ambiguous-repository-url" `
            -Package $packagePath `
            -RuntimePackage $runtimePath `
            -Manifest $manifestPath `
            -Specification $ambiguousRepositoryUrlSpecificationPath `
            -ExpectSuccess $false `
            -ExpectedFailureCode "PackageTupleMismatch"
    }
    finally {
        Write-TestText -Path $testNuspecPath -Value $nuspecText
    }

    $nonCanonicalLicenseUrlNuspecText = $nuspecText.Replace(
        "https://aka.ms/deprecateLicenseUrl",
        "https://example.com/non-canonical-license-marker")
    try {
        Write-TestText -Path $testNuspecPath -Value $nonCanonicalLicenseUrlNuspecText
        $nonCanonicalLicenseUrlSpecification = Copy-TestObject -Value $specification
        $nonCanonicalLicenseUrlSpecification.Packages[0].NuspecSha256 =
            Get-TestFileSha256 -Path $testNuspecPath
        $nonCanonicalLicenseUrlSpecificationPath =
            Join-Path $script:testRoot "non-canonical-license-url-spec.json"
        Write-TestJson `
            -Path $nonCanonicalLicenseUrlSpecificationPath `
            -Value $nonCanonicalLicenseUrlSpecification
        Invoke-InventoryScenario `
            -Name "non-canonical-license-url" `
            -Package $packagePath `
            -RuntimePackage $runtimePath `
            -Manifest $manifestPath `
            -Specification $nonCanonicalLicenseUrlSpecificationPath `
            -ExpectSuccess $false `
            -ExpectedFailureCode "PackageTupleMismatch"
    }
    finally {
        Write-TestText -Path $testNuspecPath -Value $nuspecText
    }

    $duplicateLicenseNuspecText = $nuspecText.Replace(
        "<license type=`"file`">LICENSE.txt</license>",
        "<license type=`"file`">LICENSE.txt</license><license type=`"file`">LICENSE.txt</license>")
    try {
        Write-TestText -Path $testNuspecPath -Value $duplicateLicenseNuspecText
        $duplicateLicenseSpecification = Copy-TestObject -Value $specification
        $duplicateLicenseSpecification.Packages[0].NuspecSha256 =
            Get-TestFileSha256 -Path $testNuspecPath
        $duplicateLicenseSpecificationPath =
            Join-Path $script:testRoot "duplicate-license-spec.json"
        Write-TestJson `
            -Path $duplicateLicenseSpecificationPath `
            -Value $duplicateLicenseSpecification
        Invoke-InventoryScenario `
            -Name "duplicate-license" `
            -Package $packagePath `
            -RuntimePackage $runtimePath `
            -Manifest $manifestPath `
            -Specification $duplicateLicenseSpecificationPath `
            -ExpectSuccess $false `
            -ExpectedFailureCode "PackageTupleMismatch"
    }
    finally {
        Write-TestText -Path $testNuspecPath -Value $nuspecText
    }

    $duplicateLicenseUrlNuspecText = $nuspecText.Replace(
        "<licenseUrl>https://aka.ms/deprecateLicenseUrl</licenseUrl>",
        "<licenseUrl>https://aka.ms/deprecateLicenseUrl</licenseUrl><licenseUrl>https://aka.ms/deprecateLicenseUrl</licenseUrl>")
    try {
        Write-TestText -Path $testNuspecPath -Value $duplicateLicenseUrlNuspecText
        $duplicateLicenseUrlSpecification = Copy-TestObject -Value $specification
        $duplicateLicenseUrlSpecification.Packages[0].NuspecSha256 =
            Get-TestFileSha256 -Path $testNuspecPath
        $duplicateLicenseUrlSpecificationPath =
            Join-Path $script:testRoot "duplicate-license-url-spec.json"
        Write-TestJson `
            -Path $duplicateLicenseUrlSpecificationPath `
            -Value $duplicateLicenseUrlSpecification
        Invoke-InventoryScenario `
            -Name "duplicate-license-url" `
            -Package $packagePath `
            -RuntimePackage $runtimePath `
            -Manifest $manifestPath `
            -Specification $duplicateLicenseUrlSpecificationPath `
            -ExpectSuccess $false `
            -ExpectedFailureCode "PackageTupleMismatch"
    }
    finally {
        Write-TestText -Path $testNuspecPath -Value $nuspecText
    }

    $testLicensePath = Join-Path $restoredPackage "LICENSE.txt"
    try {
        [System.IO.File]::Delete($testLicensePath)
        Invoke-InventoryScenario `
            -Name "missing-license-file" `
            -Package $packagePath `
            -RuntimePackage $runtimePath `
            -Manifest $manifestPath `
            -Specification $specificationPath `
            -ExpectSuccess $false `
            -ExpectedFailureCode "PackageTupleMismatch"
    }
    finally {
        Write-TestText -Path $testLicensePath -Value $licenseText
    }

    $reparsePackageRoot = Join-Path $script:testRoot "reparse-packages"
    [void][System.IO.Directory]::CreateDirectory($reparsePackageRoot)
    $reparsePackageIdPath = Join-Path $reparsePackageRoot "synthetic.dependency"
    try {
        [void](New-Item `
                -ItemType Junction `
                -Path $reparsePackageIdPath `
                -Target (Join-Path $packageFolder "synthetic.dependency") `
                -ErrorAction Stop)
        $reparseItem = Get-Item -LiteralPath $reparsePackageIdPath -Force
        Assert-Test `
            (($reparseItem.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) `
            "The package-cache ancestry test did not create a reparse point."
        $reparsePackageFolders = [ordered]@{}
        $reparsePackageFolders[
            $reparsePackageRoot + [System.IO.Path]::DirectorySeparatorChar] = [ordered]@{}
        $reparseAssets = [ordered]@{
            packageFolders = $reparsePackageFolders
            libraries = $libraries
        }
        $reparseAssetsPath = Join-Path $script:testRoot "reparse-project.assets.json"
        Write-TestJson -Path $reparseAssetsPath -Value $reparseAssets
        Invoke-InventoryScenario `
            -Name "package-cache-ancestry-reparse" `
            -Package $packagePath `
            -RuntimePackage $runtimePath `
            -Manifest $manifestPath `
            -Specification $specificationPath `
            -ExpectSuccess $false `
            -ExpectedFailureCode "PackageTupleMismatch" `
            -AssetsFile $reparseAssetsPath
    }
    finally {
        if (Test-Path -LiteralPath $reparsePackageIdPath) {
            [System.IO.Directory]::Delete($reparsePackageIdPath)
        }
    }

    $duplicateBomEntries = @($appEntries | ForEach-Object {
        if ([string]::Equals($_.Path, "AppxManifest.xml", [System.StringComparison]::Ordinal)) {
            New-Entry `
                -Path $_.Path `
                -Bytes (Add-TestUtf8Bom -Bytes $_.Bytes)
        }
        else {
            $_
        }
    })
    $duplicateBomPackage = Join-Path $script:testRoot "duplicate-bom.msix"
    Write-TestArchive -Path $duplicateBomPackage -Entries $duplicateBomEntries
    Invoke-InventoryScenario `
        -Name "duplicate-bom" `
        -Package $duplicateBomPackage `
        -RuntimePackage $runtimePath `
        -Manifest $manifestPath `
        -Specification $specificationPath `
        -ExpectSuccess $false `
        -ExpectedFailureCode "PackageManifestInvalid"

    $interiorBomManifestText = $embeddedManifestText.Replace(
        "<Package ",
        (([string][char]0xfeff) + "<Package "))
    $interiorBomEntries = @($appEntries | ForEach-Object {
        if ([string]::Equals($_.Path, "AppxManifest.xml", [System.StringComparison]::Ordinal)) {
            New-Entry `
                -Path $_.Path `
                -Bytes (Add-TestUtf8Bom -Bytes $script:utf8NoBom.GetBytes($interiorBomManifestText))
        }
        else {
            $_
        }
    })
    $interiorBomPackage = Join-Path $script:testRoot "interior-bom.msix"
    Write-TestArchive -Path $interiorBomPackage -Entries $interiorBomEntries
    Invoke-InventoryScenario `
        -Name "interior-bom" `
        -Package $interiorBomPackage `
        -RuntimePackage $runtimePath `
        -Manifest $manifestPath `
        -Specification $specificationPath `
        -ExpectSuccess $false `
        -ExpectedFailureCode "PackageManifestInvalid"

    $invalidUtf8Entries = @($appEntries | ForEach-Object {
        if ([string]::Equals($_.Path, "AppxManifest.xml", [System.StringComparison]::Ordinal)) {
            New-Entry `
                -Path $_.Path `
                -Bytes (Add-TestUtf8Bom -Bytes ([byte[]](0x3c, 0xff, 0x3e)))
        }
        else {
            $_
        }
    })
    $invalidUtf8Package = Join-Path $script:testRoot "invalid-utf8.msix"
    Write-TestArchive -Path $invalidUtf8Package -Entries $invalidUtf8Entries
    Invoke-InventoryScenario `
        -Name "invalid-utf8" `
        -Package $invalidUtf8Package `
        -RuntimePackage $runtimePath `
        -Manifest $manifestPath `
        -Specification $specificationPath `
        -ExpectSuccess $false `
        -ExpectedFailureCode "PackageManifestInvalid"

    $unknownPackagePath = Join-Path $script:testRoot "unknown-binary.msix"
    Write-TestArchive -Path $unknownPackagePath -Entries @(
        $appEntries + (New-Entry -Path "Unknown.dll" -Bytes $script:utf8NoBom.GetBytes("unknown")))
    $unknownBinarySpecification = Copy-TestObject -Value $specification
    $unknownBinarySpecification.AllowedPackageEntries = @(
        $unknownBinarySpecification.AllowedPackageEntries + "Unknown.dll")
    $unknownBinarySpecificationPath = Join-Path $script:testRoot "unknown-binary-spec.json"
    Write-TestJson -Path $unknownBinarySpecificationPath -Value $unknownBinarySpecification
    Invoke-InventoryScenario `
        -Name "unknown-binary" `
        -Package $unknownPackagePath `
        -RuntimePackage $runtimePath `
        -Manifest $manifestPath `
        -Specification $unknownBinarySpecificationPath `
        -ExpectSuccess $false `
        -ExpectedFailureCode "UnknownOrAmbiguousExecutablePayload"

    $extraCapabilityManifest = Join-Path $script:testRoot "extra-capability.appxmanifest"
    Write-TestText `
        -Path $extraCapabilityManifest `
        -Value $sourceManifestText.Replace(
            "<rescap:Capability Name=`"runFullTrust`" />",
            "<rescap:Capability Name=`"runFullTrust`" /><DeviceCapability Name=`"webcam`" />")
    Invoke-InventoryScenario `
        -Name "extra-capability" `
        -Package $packagePath `
        -RuntimePackage $runtimePath `
        -Manifest $extraCapabilityManifest `
        -Specification $specificationPath `
        -ExpectSuccess $false `
        -ExpectedFailureCode "SourceManifestInvalid"

    $runtimeDependencyDriftEntries = @($appEntries | ForEach-Object {
        if ([string]::Equals($_.Path, "AppxManifest.xml", [System.StringComparison]::Ordinal)) {
            New-Entry `
                -Path $_.Path `
                -Bytes $script:utf8NoBom.GetBytes(
                    $embeddedManifestText.Replace("MinVersion=`"2.4.0.0`"", "MinVersion=`"2.4.1.0`""))
        }
        else {
            $_
        }
    })
    $runtimeDependencyDriftPackage = Join-Path $script:testRoot "runtime-dependency-drift.msix"
    Write-TestArchive -Path $runtimeDependencyDriftPackage -Entries $runtimeDependencyDriftEntries
    Invoke-InventoryScenario `
        -Name "runtime-dependency-drift" `
        -Package $runtimeDependencyDriftPackage `
        -RuntimePackage $runtimePath `
        -Manifest $manifestPath `
        -Specification $specificationPath `
        -ExpectSuccess $false `
        -ExpectedFailureCode "PackageManifestMismatch"

    $applicationDriftEntries = @($appEntries | ForEach-Object {
        if ([string]::Equals($_.Path, "AppxManifest.xml", [System.StringComparison]::Ordinal)) {
            New-Entry `
                -Path $_.Path `
                -Bytes $script:utf8NoBom.GetBytes(
                    $embeddedManifestText.Replace("EntryPoint=`"Harness.App`"", "EntryPoint=`"Harness.Other`""))
        }
        else {
            $_
        }
    })
    $applicationDriftPackage = Join-Path $script:testRoot "application-drift.msix"
    Write-TestArchive -Path $applicationDriftPackage -Entries $applicationDriftEntries
    Invoke-InventoryScenario `
        -Name "application-drift" `
        -Package $applicationDriftPackage `
        -RuntimePackage $runtimePath `
        -Manifest $manifestPath `
        -Specification $specificationPath `
        -ExpectSuccess $false `
        -ExpectedFailureCode "PackageManifestMismatch"

    $extensionDriftEntries = @($appEntries | ForEach-Object {
        if ([string]::Equals($_.Path, "AppxManifest.xml", [System.StringComparison]::Ordinal)) {
            New-Entry `
                -Path $_.Path `
                -Bytes $script:utf8NoBom.GetBytes(
                    $embeddedManifestText.Replace(
                        "Synthetic.Dependency.Component",
                        "Synthetic.Dependency.DriftedComponent"))
        }
        else {
            $_
        }
    })
    $extensionDriftPackage = Join-Path $script:testRoot "extension-drift.msix"
    Write-TestArchive -Path $extensionDriftPackage -Entries $extensionDriftEntries
    Invoke-InventoryScenario `
        -Name "extension-drift" `
        -Package $extensionDriftPackage `
        -RuntimePackage $runtimePath `
        -Manifest $manifestPath `
        -Specification $specificationPath `
        -ExpectSuccess $false `
        -ExpectedFailureCode "PackageManifestMismatch"

    $extraExtension = "<Extension Category=`"windows.activatableClass.inProcessServer`"><InProcessServer><Path>Synthetic.Dependency.dll</Path><ActivatableClass ActivatableClassId=`"Synthetic.Dependency.ExtraComponent`" ThreadingModel=`"both`" /></InProcessServer></Extension>"
    $extraExtensionEntries = @($appEntries | ForEach-Object {
        if ([string]::Equals($_.Path, "AppxManifest.xml", [System.StringComparison]::Ordinal)) {
            New-Entry `
                -Path $_.Path `
                -Bytes $script:utf8NoBom.GetBytes(
                    $embeddedManifestText.Replace("</Extensions>", "$extraExtension</Extensions>"))
        }
        else {
            $_
        }
    })
    $extraExtensionPackage = Join-Path $script:testRoot "extra-extension.msix"
    Write-TestArchive -Path $extraExtensionPackage -Entries $extraExtensionEntries
    Invoke-InventoryScenario `
        -Name "extra-extension" `
        -Package $extraExtensionPackage `
        -RuntimePackage $runtimePath `
        -Manifest $manifestPath `
        -Specification $specificationPath `
        -ExpectSuccess $false `
        -ExpectedFailureCode "PackageManifestInvalid"

    $missingExtensionEntries = @($appEntries | ForEach-Object {
        if ([string]::Equals($_.Path, "AppxManifest.xml", [System.StringComparison]::Ordinal)) {
            New-Entry `
                -Path $_.Path `
                -Bytes $script:utf8NoBom.GetBytes($embeddedManifestText.Replace($extensionText, ""))
        }
        else {
            $_
        }
    })
    $missingExtensionPackage = Join-Path $script:testRoot "missing-extension.msix"
    Write-TestArchive -Path $missingExtensionPackage -Entries $missingExtensionEntries
    Invoke-InventoryScenario `
        -Name "missing-extension" `
        -Package $missingExtensionPackage `
        -RuntimePackage $runtimePath `
        -Manifest $manifestPath `
        -Specification $specificationPath `
        -ExpectSuccess $false `
        -ExpectedFailureCode "PackageManifestMismatch"

    $namespaceBypassEntries = @($appEntries | ForEach-Object {
        if ([string]::Equals($_.Path, "AppxManifest.xml", [System.StringComparison]::Ordinal)) {
            New-Entry `
                -Path $_.Path `
                -Bytes $script:utf8NoBom.GetBytes(
                    $embeddedManifestText.Replace(
                        "<Extension Category=",
                        "<Extension xmlns=`"urn:synthetic:extension-bypass`" Category="))
        }
        else {
            $_
        }
    })
    $namespaceBypassPackage = Join-Path $script:testRoot "extension-namespace-bypass.msix"
    Write-TestArchive -Path $namespaceBypassPackage -Entries $namespaceBypassEntries
    Invoke-InventoryScenario `
        -Name "extension-namespace-bypass" `
        -Package $namespaceBypassPackage `
        -RuntimePackage $runtimePath `
        -Manifest $manifestPath `
        -Specification $specificationPath `
        -ExpectSuccess $false `
        -ExpectedFailureCode "PackageManifestInvalid"

    $runtimeBindingDriftSpecification = Copy-TestObject -Value $specification
    $runtimeBindingDriftSpecification.RuntimeDependency.MinVersion = "2.3.0.0"
    $runtimeBindingDriftSpecificationPath = Join-Path $script:testRoot "runtime-binding-drift-spec.json"
    Write-TestJson -Path $runtimeBindingDriftSpecificationPath -Value $runtimeBindingDriftSpecification
    Invoke-InventoryScenario `
        -Name "runtime-binding-drift" `
        -Package $packagePath `
        -RuntimePackage $runtimePath `
        -Manifest $manifestPath `
        -Specification $runtimeBindingDriftSpecificationPath `
        -ExpectSuccess $false `
        -ExpectedFailureCode "RuntimeDependencyBindingMismatch"

    $missingLicenseSpecification = Copy-TestObject -Value $specification
    $missingLicenseSpecification.Packages[0].PSObject.Properties.Remove("License")
    $missingLicensePath = Join-Path $script:testRoot "missing-license-spec.json"
    Write-TestJson -Path $missingLicensePath -Value $missingLicenseSpecification
    Invoke-InventoryScenario `
        -Name "missing-license" `
        -Package $packagePath `
        -RuntimePackage $runtimePath `
        -Manifest $manifestPath `
        -Specification $missingLicensePath `
        -ExpectSuccess $false `
        -ExpectedFailureCode "PackageTupleMismatch"

    $missingNoticeSpecification = Copy-TestObject -Value $specification
    $missingNoticeSpecification.Packages[0].Notices = @()
    $missingNoticePath = Join-Path $script:testRoot "missing-notice-spec.json"
    Write-TestJson -Path $missingNoticePath -Value $missingNoticeSpecification
    Invoke-InventoryScenario `
        -Name "missing-notice" `
        -Package $packagePath `
        -RuntimePackage $runtimePath `
        -Manifest $manifestPath `
        -Specification $missingNoticePath `
        -ExpectSuccess $false `
        -ExpectedFailureCode "PackageTupleMismatch"

    $traversalPackagePath = Join-Path $script:testRoot "path-traversal.msix"
    Write-TestArchive -Path $traversalPackagePath -Entries @(
        $appEntries + (New-Entry -Path "../escape.dll" -Bytes $script:utf8NoBom.GetBytes("escape")))
    Invoke-InventoryScenario `
        -Name "path-traversal" `
        -Package $traversalPackagePath `
        -RuntimePackage $runtimePath `
        -Manifest $manifestPath `
        -Specification $specificationPath `
        -ExpectSuccess $false `
        -ExpectedFailureCode "PackageArchiveInvalid"

    $caseCollisionPackagePath = Join-Path $script:testRoot "case-collision.msix"
    Write-TestArchive -Path $caseCollisionPackagePath -Entries @(
        $appEntries + (New-Entry -Path "assets/logo.png" -Bytes ([byte[]](5, 6, 7, 8))))
    Invoke-InventoryScenario `
        -Name "case-collision" `
        -Package $caseCollisionPackagePath `
        -RuntimePackage $runtimePath `
        -Manifest $manifestPath `
        -Specification $specificationPath `
        -ExpectSuccess $false `
        -ExpectedFailureCode "PackageArchiveInvalid"

    $renamedPeBytes = [byte[]](0x4d, 0x5a, 0x01, 0x02, 0x03)
    $renamedPePackagePath = Join-Path $script:testRoot "renamed-pe.msix"
    Write-TestArchive -Path $renamedPePackagePath -Entries @(
        $appEntries + (New-Entry -Path "renamed-payload.dat" -Bytes $renamedPeBytes))
    $renamedPeSpecification = Copy-TestObject -Value $specification
    $renamedPeSpecification.AllowedPackageEntries = @(
        $renamedPeSpecification.AllowedPackageEntries + "renamed-payload.dat")
    $renamedPeSpecificationPath = Join-Path $script:testRoot "renamed-pe-spec.json"
    Write-TestJson -Path $renamedPeSpecificationPath -Value $renamedPeSpecification
    Invoke-InventoryScenario `
        -Name "renamed-pe" `
        -Package $renamedPePackagePath `
        -RuntimePackage $runtimePath `
        -Manifest $manifestPath `
        -Specification $renamedPeSpecificationPath `
        -ExpectSuccess $false `
        -ExpectedFailureCode "UnsupportedExecutablePayload"

    $gplMarkerPackagePath = Join-Path $script:testRoot "gpl-marker.msix"
    Write-TestArchive -Path $gplMarkerPackagePath -Entries @(
        $appEntries + (New-Entry -Path "plugins/agpl-addon.dll" -Bytes $script:utf8NoBom.GetBytes("marker")))
    Invoke-InventoryScenario `
        -Name "gpl-marker" `
        -Package $gplMarkerPackagePath `
        -RuntimePackage $runtimePath `
        -Manifest $manifestPath `
        -Specification $specificationPath `
        -ExpectSuccess $false `
        -ExpectedFailureCode "ForbiddenPayloadMarker"

    $unknownNestedEntries = @(
        $nestedEntries + (New-Entry -Path "UnknownNested.dll" -Bytes $script:utf8NoBom.GetBytes("nested unknown")))
    $unknownNestedBytes = New-TestArchiveBytes -Entries $unknownNestedEntries
    $unknownRuntimeEntries = @(
        $runtimeEntries | Where-Object { $_.Path -ne "MSIX/Nested.msix" }
    ) + (New-Entry -Path "MSIX/Nested.msix" -Bytes $unknownNestedBytes)
    $unknownRuntimePath = Join-Path $script:testRoot "Microsoft.WindowsAppRuntime.2-unknown.msix"
    Write-TestArchive -Path $unknownRuntimePath -Entries $unknownRuntimeEntries
    $unknownRuntimeExactPath = Join-Path $script:testRoot "unknown-runtime\Microsoft.WindowsAppRuntime.2.msix"
    [void][System.IO.Directory]::CreateDirectory([System.IO.Path]::GetDirectoryName($unknownRuntimeExactPath))
    [System.IO.File]::Copy($unknownRuntimePath, $unknownRuntimeExactPath)
    $unknownRuntimeSpecification = Copy-TestObject -Value $specification
    $unknownRuntimeSpecification.RuntimePackage.Sha256 = Get-TestFileSha256 -Path $unknownRuntimeExactPath
    $unknownRuntimeSpecificationPath = Join-Path $script:testRoot "unknown-runtime-spec.json"
    Write-TestJson -Path $unknownRuntimeSpecificationPath -Value $unknownRuntimeSpecification
    Invoke-InventoryScenario `
        -Name "unknown-nested-binary" `
        -Package $packagePath `
        -RuntimePackage $unknownRuntimeExactPath `
        -Manifest $manifestPath `
        -Specification $unknownRuntimeSpecificationPath `
        -ExpectSuccess $false `
        -ExpectedFailureCode "RuntimePackageMismatch"

    Write-Output "Native playback package inventory self-test passed."
}
finally {
    if (Test-Path -LiteralPath $script:testRoot -PathType Container) {
        [System.IO.Directory]::Delete($script:testRoot, $true)
    }
}
