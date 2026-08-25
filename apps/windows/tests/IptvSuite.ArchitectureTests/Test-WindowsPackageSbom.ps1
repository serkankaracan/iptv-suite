#requires -Version 5.1

[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$script:utf8NoBom = New-Object System.Text.UTF8Encoding($false)
$script:repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot "..\..\..\.."))
$script:helperPath = Join-Path $script:repositoryRoot "eng\WindowsPackageSbom.ps1"
$script:runnerPath = Join-Path $script:repositoryRoot "eng\Invoke-WindowsPackageSbom.ps1"
$script:configurationPath = Join-Path $script:repositoryRoot "eng\windows-package-sbom-tool.json"
$script:runId = [Guid]::NewGuid().ToString("N")
$script:testLeaf = "IptvSuite-WindowsPackageSbom-$($script:runId)"
$script:testRoot = [System.IO.Path]::GetFullPath(
    (Join-Path ([System.IO.Path]::GetTempPath()) $script:testLeaf))

function Assert-TestCondition {
    param(
        [Parameter(Mandatory)]
        [bool]$Condition,

        [Parameter(Mandatory)]
        [string]$Message
    )

    if (-not $Condition) {
        throw "Windows package SBOM self-test failed: $Message"
    }
}

function Copy-TestObject {
    param(
        [Parameter(Mandatory)]
        [object]$Value
    )

    return ($Value | ConvertTo-Json -Depth 12 | ConvertFrom-Json)
}

function New-SyntheticMsix {
    param(
        [Parameter(Mandatory)]
        [string]$Path,

        [Parameter(Mandatory)]
        [string]$Name,

        [Parameter(Mandatory)]
        [string]$Publisher,

        [Parameter(Mandatory)]
        [string]$Version,

        [switch]$EmitUtf8Bom
    )

    $manifest = @"
<?xml version="1.0" encoding="utf-8"?>
<Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10">
  <Identity Name="$Name" Publisher="$Publisher" Version="$Version" ProcessorArchitecture="x64" />
</Package>
"@
    $stream = [System.IO.File]::Open(
        $Path,
        [System.IO.FileMode]::CreateNew,
        [System.IO.FileAccess]::Write,
        [System.IO.FileShare]::None)
    $archive = $null
    try {
        $archive = New-Object System.IO.Compression.ZipArchive(
            $stream,
            [System.IO.Compression.ZipArchiveMode]::Create,
            $false)
        $manifestEntry = $archive.CreateEntry("AppxManifest.xml")
        $manifestStream = $manifestEntry.Open()
        $encoding = if ($EmitUtf8Bom) {
            New-Object System.Text.UTF8Encoding($true, $true)
        }
        else {
            $script:utf8NoBom
        }
        $writer = New-Object System.IO.StreamWriter($manifestStream, $encoding)
        try {
            $writer.Write($manifest)
        }
        finally {
            $writer.Dispose()
        }
    }
    finally {
        if ($null -ne $archive) {
            $archive.Dispose()
        }
        else {
            $stream.Dispose()
        }
    }
}

function New-SyntheticToolPayload {
    param(
        [Parameter(Mandatory)]
        [string]$Root
    )

    $payloadRoot = Join-Path $Root "tools\net8.0\any"
    [System.IO.Directory]::CreateDirectory((Join-Path $payloadRoot "runtimes\win")) | Out-Null
    $payloads = [ordered]@{
        "Microsoft.Sbom.DotNetTool.dll" = "synthetic-entrypoint"
        "Microsoft.Sbom.DotNetTool.deps.json" = '{"synthetic":true}'
        "Microsoft.Sbom.DotNetTool.runtimeconfig.json" = '{"runtimeOptions":{}}'
        "runtimes/win/helper.dll" = "synthetic-helper"
    }
    foreach ($relativePath in $payloads.Keys) {
        $filePath = Join-Path $payloadRoot $relativePath.Replace('/', '\')
        [System.IO.File]::WriteAllText(
            $filePath,
            [string]$payloads[$relativePath],
            $script:utf8NoBom)
    }

    $packagePath = Join-Path $Root "microsoft.sbom.dotnettool.4.1.5.nupkg"
    $packageStream = [System.IO.File]::Open(
        $packagePath,
        [System.IO.FileMode]::CreateNew,
        [System.IO.FileAccess]::Write,
        [System.IO.FileShare]::None)
    $archive = $null
    try {
        $archive = New-Object System.IO.Compression.ZipArchive(
            $packageStream,
            [System.IO.Compression.ZipArchiveMode]::Create,
            $false)
        foreach ($relativePath in $payloads.Keys) {
            $entry = $archive.CreateEntry("tools/net8.0/any/$relativePath")
            $entryStream = $entry.Open()
            $sourceStream = [System.IO.File]::Open(
                (Join-Path $payloadRoot $relativePath.Replace('/', '\')),
                [System.IO.FileMode]::Open,
                [System.IO.FileAccess]::Read,
                [System.IO.FileShare]::None)
            try {
                $sourceStream.CopyTo($entryStream)
            }
            finally {
                $sourceStream.Dispose()
                $entryStream.Dispose()
            }
        }
    }
    finally {
        if ($null -ne $archive) {
            $archive.Dispose()
        }
        else {
            $packageStream.Dispose()
        }
    }

    return Get-Item -LiteralPath $packagePath -Force
}

function Get-TestPackageVerificationCode {
    param(
        [Parameter(Mandatory)]
        [string[]]$FileSha1Values
    )

    $sorted = @(
        $FileSha1Values |
            ForEach-Object { $_.ToUpperInvariant() } |
            Sort-Object -CaseSensitive)
    $bytes = [System.Text.Encoding]::ASCII.GetBytes(($sorted -join ""))
    $algorithm = [System.Security.Cryptography.SHA1]::Create()
    try {
        return ([BitConverter]::ToString($algorithm.ComputeHash($bytes))).Replace('-', '').ToLowerInvariant()
    }
    finally {
        $algorithm.Dispose()
    }
}

function New-ValidSpdxDocument {
    param(
        [Parameter(Mandatory)]
        [object]$Configuration,

        [Parameter(Mandatory)]
        [System.IO.FileInfo]$ApplicationPackage,

        [Parameter(Mandatory)]
        [System.IO.FileInfo]$RuntimePackage,

        [Parameter(Mandatory)]
        [string]$Namespace,

        [Parameter(Mandatory)]
        [string]$Version,

        [Parameter(Mandatory)]
        [string]$ApplicationArtifactSpdxId,

        [Parameter(Mandatory)]
        [string]$RuntimeArtifactSpdxId
    )

    $applicationSha1 = Get-WindowsPackageSbomSha1 -Path $ApplicationPackage.FullName
    $applicationSha256 = Get-WindowsPackageSbomSha256 -Path $ApplicationPackage.FullName
    $runtimeSha1 = Get-WindowsPackageSbomSha1 -Path $RuntimePackage.FullName
    $runtimeSha256 = Get-WindowsPackageSbomSha256 -Path $RuntimePackage.FullName
    $applicationFileSpdxId = "SPDXRef-File-ApplicationMsix"
    $runtimeFileSpdxId = "SPDXRef-File-RuntimeMsix"

    $packages = New-Object 'System.Collections.Generic.List[object]'
    $packages.Add([ordered]@{
            name = [string]$Configuration.packageName
            SPDXID = "SPDXRef-RootPackage"
            versionInfo = $Version
            packageFileName = "./release-set"
            downloadLocation = "NOASSERTION"
            filesAnalyzed = $true
            packageVerificationCode = [ordered]@{
                packageVerificationCodeValue = Get-TestPackageVerificationCode `
                    -FileSha1Values @($applicationSha1, $runtimeSha1)
            }
            hasFiles = @($applicationFileSpdxId, $runtimeFileSpdxId)
            licenseConcluded = "NOASSERTION"
            licenseDeclared = "NOASSERTION"
            copyrightText = "NOASSERTION"
        })
    $packages.Add([ordered]@{
            name = "Synthetic.Application.MSIX"
            SPDXID = $ApplicationArtifactSpdxId
            versionInfo = $Version
            packageFileName = $ApplicationPackage.Name
            downloadLocation = "NOASSERTION"
            filesAnalyzed = $false
            checksums = @(
                [ordered]@{
                    algorithm = "SHA256"
                    checksumValue = $applicationSha256
                })
            licenseConcluded = "NOASSERTION"
            licenseDeclared = "NOASSERTION"
            copyrightText = "NOASSERTION"
        })
    $packages.Add([ordered]@{
            name = "Synthetic.WindowsAppRuntime.MSIX"
            SPDXID = $RuntimeArtifactSpdxId
            versionInfo = "2.4.0.0"
            packageFileName = $RuntimePackage.Name
            downloadLocation = "NOASSERTION"
            filesAnalyzed = $false
            checksums = @(
                [ordered]@{
                    algorithm = "SHA256"
                    checksumValue = $runtimeSha256
                })
            licenseConcluded = "NOASSERTION"
            licenseDeclared = "NOASSERTION"
            copyrightText = "NOASSERTION"
        })

    $componentOrdinal = 0
    foreach ($component in @($Configuration.expectedComponents)) {
        $componentOrdinal++
        $componentName = [string]$component.name
        $componentVersion = [string]$component.version
        $packages.Add([ordered]@{
                name = $componentName
                SPDXID = "SPDXRef-Component-$($componentOrdinal.ToString('D3'))"
                versionInfo = $componentVersion
                downloadLocation = "NOASSERTION"
                filesAnalyzed = $false
                licenseConcluded = "NOASSERTION"
                licenseDeclared = "NOASSERTION"
                copyrightText = "NOASSERTION"
                externalRefs = @(
                    [ordered]@{
                        referenceCategory = "PACKAGE-MANAGER"
                        referenceType = "purl"
                        referenceLocator = "pkg:nuget/$componentName@$componentVersion"
                    })
            })
    }

    return [ordered]@{
        spdxVersion = "SPDX-2.2"
        dataLicense = "CC0-1.0"
        SPDXID = "SPDXRef-DOCUMENT"
        name = [string]$Configuration.packageName
        documentNamespace = $Namespace
        creationInfo = [ordered]@{
            created = "2026-08-25T00:00:00Z"
            creators = @("Tool: IptvSuite.WindowsPackageSbom.SelfTest-1.0")
        }
        documentDescribes = @("SPDXRef-RootPackage")
        packages = @($packages.ToArray())
        files = @(
            [ordered]@{
                fileName = "./$($ApplicationPackage.Name)"
                SPDXID = $applicationFileSpdxId
                fileTypes = @("ARCHIVE")
                checksums = @(
                    [ordered]@{ algorithm = "SHA1"; checksumValue = $applicationSha1 },
                    [ordered]@{ algorithm = "SHA256"; checksumValue = $applicationSha256 })
                licenseConcluded = "NOASSERTION"
                licenseInfoInFiles = @("NOASSERTION")
                copyrightText = "NOASSERTION"
            },
            [ordered]@{
                fileName = "./$($RuntimePackage.Name)"
                SPDXID = $runtimeFileSpdxId
                fileTypes = @("ARCHIVE")
                checksums = @(
                    [ordered]@{ algorithm = "SHA1"; checksumValue = $runtimeSha1 },
                    [ordered]@{ algorithm = "SHA256"; checksumValue = $runtimeSha256 })
                licenseConcluded = "NOASSERTION"
                licenseInfoInFiles = @("NOASSERTION")
                copyrightText = "NOASSERTION"
            })
        relationships = @(
            [ordered]@{
                spdxElementId = "SPDXRef-DOCUMENT"
                relationshipType = "DESCRIBES"
                relatedSpdxElement = "SPDXRef-RootPackage"
            },
            [ordered]@{
                spdxElementId = "SPDXRef-RootPackage"
                relationshipType = "CONTAINS"
                relatedSpdxElement = $ApplicationArtifactSpdxId
            },
            [ordered]@{
                spdxElementId = "SPDXRef-RootPackage"
                relationshipType = "CONTAINS"
                relatedSpdxElement = $RuntimeArtifactSpdxId
            },
            [ordered]@{
                spdxElementId = $ApplicationArtifactSpdxId
                relationshipType = "DEPENDS_ON"
                relatedSpdxElement = $RuntimeArtifactSpdxId
            })
    }
}

function Write-TestDocument {
    param(
        [Parameter(Mandatory)]
        [object]$Document,

        [Parameter(Mandatory)]
        [string]$Name
    )

    $path = Join-Path $script:testRoot "$Name.spdx.json"
    Write-WindowsPackageSbomJsonAtomically `
        -Value $Document `
        -DestinationPath $path `
        -MaximumBytes 4MB
    return Get-Item -LiteralPath $path -Force
}

function Assert-DocumentFailure {
    param(
        [Parameter(Mandatory)]
        [object]$Document,

        [Parameter(Mandatory)]
        [object]$Configuration,

        [Parameter(Mandatory)]
        [string]$Name,

        [Parameter(Mandatory)]
        [string]$ExpectedCode,

        [Parameter(Mandatory)]
        [System.IO.FileInfo]$ApplicationPackage,

        [Parameter(Mandatory)]
        [System.IO.FileInfo]$RuntimePackage,

        [Parameter(Mandatory)]
        [string]$Namespace,

        [string]$ApplicationArtifactSpdxId = "SPDXRef-Package-ApplicationMsix",

        [string]$RuntimeArtifactSpdxId = "SPDXRef-Package-RuntimeMsix"
    )

    $documentFile = Write-TestDocument -Document $Document -Name $Name
    $actualMessage = $null
    try {
        Assert-WindowsPackageSbomDocument `
            -SbomFile $documentFile `
            -Configuration $Configuration `
            -ExpectedNamespace $Namespace `
            -ExpectedVersion "0.1.0.0" `
            -ApplicationPackage $ApplicationPackage `
            -RuntimePackage $RuntimePackage `
            -ApplicationArtifactSpdxId $ApplicationArtifactSpdxId `
            -RuntimeArtifactSpdxId $RuntimeArtifactSpdxId | Out-Null
    }
    catch {
        $actualMessage = $_.Exception.Message
    }

    Assert-TestCondition `
        ($actualMessage -ceq "WindowsPackageSbom:$ExpectedCode") `
        "$Name expected WindowsPackageSbom:$ExpectedCode, received '$actualMessage'."
}

Assert-TestCondition (Test-Path -LiteralPath $script:helperPath -PathType Leaf) "SBOM helper is missing."
Assert-TestCondition (Test-Path -LiteralPath $script:runnerPath -PathType Leaf) "SBOM runner is missing."
Assert-TestCondition (Test-Path -LiteralPath $script:configurationPath -PathType Leaf) "SBOM tool configuration is missing."
. $script:helperPath

try {
    [System.IO.Directory]::CreateDirectory($script:testRoot) | Out-Null
    $runnerTokens = $null
    $runnerErrors = $null
    $runnerAst = [System.Management.Automation.Language.Parser]::ParseFile(
        $script:runnerPath,
        [ref]$runnerTokens,
        [ref]$runnerErrors)
    Assert-TestCondition ($runnerErrors.Count -eq 0) "SBOM runner parser failed."
    $payloadFunctions = @($runnerAst.FindAll({
                param($node)
                $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and
                $node.Name -ceq "Assert-ExactSbomToolPayload"
            }, $true))
    Assert-TestCondition ($payloadFunctions.Count -eq 1) "exact tool payload validator is missing."
    . ([scriptblock]::Create($payloadFunctions[0].Extent.Text))

    $configuration = [System.IO.File]::ReadAllText($script:configurationPath, $script:utf8NoBom) |
        ConvertFrom-Json
    Assert-TestCondition ([int]$configuration.schemaVersion -eq 1) "configuration schema changed."
    Assert-TestCondition ([string]$configuration.manifestInfo -ceq "SPDX:2.2") "SPDX format changed."
    Assert-TestCondition (@($configuration.expectedComponents).Count -eq 24) "expected component set changed."

    $syntheticToolRoot = Join-Path $script:testRoot "synthetic-tool"
    [System.IO.Directory]::CreateDirectory($syntheticToolRoot) | Out-Null
    $syntheticToolPackage = New-SyntheticToolPayload -Root $syntheticToolRoot
    $syntheticToolPackageSha256 = Get-WindowsPackageSbomSha256 $syntheticToolPackage.FullName
    $syntheticPayloadResult = Assert-ExactSbomToolPayload `
        -Package $syntheticToolPackage `
        -ExpectedPackageSha256 $syntheticToolPackageSha256
    Assert-TestCondition `
        ($syntheticPayloadResult.FileCount -eq 4 -and $syntheticPayloadResult.TotalBytes -gt 0) `
        "synthetic exact tool payload binding failed."

    $alteredToolRoot = Join-Path $script:testRoot "altered-tool"
    Copy-Item -LiteralPath $syntheticToolRoot -Destination $alteredToolRoot -Recurse -Force
    $alteredEntrypoint = Join-Path $alteredToolRoot "tools\net8.0\any\Microsoft.Sbom.DotNetTool.dll"
    $alteredStream = [System.IO.File]::Open(
        $alteredEntrypoint,
        [System.IO.FileMode]::Append,
        [System.IO.FileAccess]::Write,
        [System.IO.FileShare]::None)
    try {
        $alteredStream.WriteByte(0)
    }
    finally {
        $alteredStream.Dispose()
    }
    $alteredPayloadMessage = $null
    try {
        Assert-ExactSbomToolPayload `
            -Package (Get-Item -LiteralPath (
                Join-Path $alteredToolRoot "microsoft.sbom.dotnettool.4.1.5.nupkg") -Force) `
            -ExpectedPackageSha256 $syntheticToolPackageSha256 | Out-Null
    }
    catch {
        $alteredPayloadMessage = $_.Exception.Message
    }
    Assert-TestCondition `
        ($alteredPayloadMessage -ceq "WindowsPackageSbom:ToolPayloadMismatch") `
        "altered tool payload was not rejected with the stable failure code."

    $installedToolPackagePath = Join-Path $script:repositoryRoot (
        ".artifacts\windows-package-sbom-tool\.store\microsoft.sbom.dotnettool\4.1.5\" +
        "microsoft.sbom.dotnettool\4.1.5\microsoft.sbom.dotnettool.4.1.5.nupkg")
    if (Test-Path -LiteralPath $installedToolPackagePath -PathType Leaf) {
        $installedPayloadResult = Assert-ExactSbomToolPayload `
            -Package (Get-Item -LiteralPath $installedToolPackagePath -Force) `
            -ExpectedPackageSha256 ([string]$configuration.nupkgSha256)
        Assert-TestCondition `
            ($installedPayloadResult.FileCount -eq 127 -and
             $installedPayloadResult.TotalBytes -eq 16421863) `
            "real exact installed tool payload binding failed."
    }

    $applicationPath = Join-Path $script:testRoot "Synthetic.Application_0.1.0.0_x64.msix"
    $runtimePath = Join-Path $script:testRoot "Microsoft.WindowsAppRuntime.2_2.4.0.0_x64.msix"
    New-SyntheticMsix `
        -Path $applicationPath `
        -Name "Synthetic.Application" `
        -Publisher "CN=Synthetic Application" `
        -Version "0.1.0.0"
    New-SyntheticMsix `
        -Path $runtimePath `
        -Name "Microsoft.WindowsAppRuntime.2" `
        -Publisher "CN=Microsoft Corporation, O=Microsoft Corporation, L=Redmond, S=Washington, C=US" `
        -Version "2.4.0.0" `
        -EmitUtf8Bom

    $applicationPackage = Get-Item -LiteralPath $applicationPath -Force
    $runtimePackage = Get-Item -LiteralPath $runtimePath -Force
    $applicationManifest = Get-WindowsPackageSbomArchiveManifest `
        -Package $applicationPackage `
        -Code "SyntheticApplicationInvalid"
    $runtimeManifest = Get-WindowsPackageSbomArchiveManifest `
        -Package $runtimePackage `
        -Code "SyntheticRuntimeInvalid"
    Assert-TestCondition `
        ($applicationManifest.Name -ceq "Synthetic.Application" -and
         $applicationManifest.Version -ceq "0.1.0.0" -and
         $applicationManifest.Architecture -ceq "x64") `
        "synthetic application identity was not read exactly."
    Assert-TestCondition `
        ($runtimeManifest.Name -ceq "Microsoft.WindowsAppRuntime.2" -and
         $runtimeManifest.Version -ceq "2.4.0.0" -and
         $runtimeManifest.Architecture -ceq "x64") `
        "synthetic runtime identity was not read exactly."

    $namespace = "https://github.com/serkankaracan/iptv-suite/sbom/self-test/$($script:runId)"
    $applicationArtifactSpdxId = "SPDXRef-Package-ApplicationMsix"
    $runtimeArtifactSpdxId = "SPDXRef-Package-RuntimeMsix"
    $validDocument = New-ValidSpdxDocument `
        -Configuration $configuration `
        -ApplicationPackage $applicationPackage `
        -RuntimePackage $runtimePackage `
        -Namespace $namespace `
        -Version $applicationManifest.Version `
        -ApplicationArtifactSpdxId $applicationArtifactSpdxId `
        -RuntimeArtifactSpdxId $runtimeArtifactSpdxId
    $validFile = Write-TestDocument -Document $validDocument -Name "valid"
    # Exercise the atomic replacement path as well as first publication.
    $validFile = Write-TestDocument -Document $validDocument -Name "valid"
    $validResult = Assert-WindowsPackageSbomDocument `
        -SbomFile $validFile `
        -Configuration $configuration `
        -ExpectedNamespace $namespace `
        -ExpectedVersion $applicationManifest.Version `
        -ApplicationPackage $applicationPackage `
        -RuntimePackage $runtimePackage `
        -ApplicationArtifactSpdxId $applicationArtifactSpdxId `
        -RuntimeArtifactSpdxId $runtimeArtifactSpdxId
    Assert-TestCondition ($validResult.FileCount -eq 2) "valid document file count is invalid."
    Assert-TestCondition `
        ($validResult.ComponentCount -eq @($configuration.expectedComponents).Count) `
        "valid document component count is invalid."

    $wrongHash = Copy-TestObject -Value $validDocument
    ($wrongHash.packages | Where-Object { $_.SPDXID -ceq $applicationArtifactSpdxId }).checksums[0].checksumValue =
        ("0" * 64)
    Assert-DocumentFailure `
        -Document $wrongHash `
        -Configuration $configuration `
        -Name "wrong-hash" `
        -ExpectedCode "ApplicationArtifactInvalid" `
        -ApplicationPackage $applicationPackage `
        -RuntimePackage $runtimePackage `
        -Namespace $namespace

    $missingComponent = Copy-TestObject -Value $validDocument
    $missingName = [string]$configuration.expectedComponents[0].name
    $missingComponent.packages = @($missingComponent.packages | Where-Object {
        [string]$_.name -cne $missingName
    })
    Assert-DocumentFailure `
        -Document $missingComponent `
        -Configuration $configuration `
        -Name "missing-component" `
        -ExpectedCode "PackageSetInvalid" `
        -ApplicationPackage $applicationPackage `
        -RuntimePackage $runtimePackage `
        -Namespace $namespace

    $forbiddenConfiguration = Copy-TestObject -Value $configuration
    $forbiddenConfiguration.expectedComponents[0].name = "LibVLCSharp"
    $forbiddenConfiguration.expectedComponents[0].version = "3.10.0"
    $forbiddenComponent = Copy-TestObject -Value $validDocument
    $forbiddenPackage = @($forbiddenComponent.packages | Where-Object {
        [string]$_.name -ceq $missingName
    })[0]
    $forbiddenPackage.name = "LibVLCSharp"
    $forbiddenPackage.versionInfo = "3.10.0"
    $forbiddenPackage.externalRefs[0].referenceLocator = "pkg:nuget/LibVLCSharp@3.10.0"
    Assert-DocumentFailure `
        -Document $forbiddenComponent `
        -Configuration $forbiddenConfiguration `
        -Name "forbidden-libvlc" `
        -ExpectedCode "ForbiddenComponentDetected" `
        -ApplicationPackage $applicationPackage `
        -RuntimePackage $runtimePackage `
        -Namespace $namespace

    $caseCollision = Copy-TestObject -Value $validDocument
    $collidingApplicationSpdxId = "spdxref-document"
    ($caseCollision.packages | Where-Object {
        [string]$_.SPDXID -ceq $applicationArtifactSpdxId
    }).SPDXID = $collidingApplicationSpdxId
    foreach ($relationship in @($caseCollision.relationships)) {
        if ([string]$relationship.spdxElementId -ceq $applicationArtifactSpdxId) {
            $relationship.spdxElementId = $collidingApplicationSpdxId
        }
        if ([string]$relationship.relatedSpdxElement -ceq $applicationArtifactSpdxId) {
            $relationship.relatedSpdxElement = $collidingApplicationSpdxId
        }
    }
    Assert-DocumentFailure `
        -Document $caseCollision `
        -Configuration $configuration `
        -Name "spdx-id-case-collision" `
        -ExpectedCode "SpdxIdCollision" `
        -ApplicationPackage $applicationPackage `
        -RuntimePackage $runtimePackage `
        -Namespace $namespace `
        -ApplicationArtifactSpdxId $collidingApplicationSpdxId

    $absolutePathLeak = Copy-TestObject -Value $validDocument
    ($absolutePathLeak.packages | Where-Object {
        [string]$_.SPDXID -ceq "SPDXRef-RootPackage"
    }) | Add-Member -NotePropertyName comment -NotePropertyValue "C:\Sensitive\package.msix"
    Assert-DocumentFailure `
        -Document $absolutePathLeak `
        -Configuration $configuration `
        -Name "absolute-path-leak" `
        -ExpectedCode "DocumentContainsUnsafeText" `
        -ApplicationPackage $applicationPackage `
        -RuntimePackage $runtimePackage `
        -Namespace $namespace

    $missingRelationship = Copy-TestObject -Value $validDocument
    $requiredDependency = "$applicationArtifactSpdxId|DEPENDS_ON|$runtimeArtifactSpdxId"
    $missingRelationship.relationships = @($missingRelationship.relationships | Where-Object {
        "$([string]$_.spdxElementId)|$([string]$_.relationshipType)|$([string]$_.relatedSpdxElement)" -cne
            $requiredDependency
    })
    Assert-DocumentFailure `
        -Document $missingRelationship `
        -Configuration $configuration `
        -Name "missing-required-relation" `
        -ExpectedCode "RelationshipSetInvalid" `
        -ApplicationPackage $applicationPackage `
        -RuntimePackage $runtimePackage `
        -Namespace $namespace

    $extraFile = Copy-TestObject -Value $validDocument
    $extraFile.files = @($extraFile.files) + [pscustomobject]@{
        fileName = "./unexpected.bin"
        SPDXID = "SPDXRef-File-Unexpected"
        fileTypes = @("BINARY")
        checksums = @(
            [pscustomobject]@{ algorithm = "SHA1"; checksumValue = ("1" * 40) },
            [pscustomobject]@{ algorithm = "SHA256"; checksumValue = ("1" * 64) })
        licenseConcluded = "NOASSERTION"
        licenseInfoInFiles = @("NOASSERTION")
        copyrightText = "NOASSERTION"
    }
    Assert-DocumentFailure `
        -Document $extraFile `
        -Configuration $configuration `
        -Name "extra-file" `
        -ExpectedCode "FileSetInvalid" `
        -ApplicationPackage $applicationPackage `
        -RuntimePackage $runtimePackage `
        -Namespace $namespace

    Write-Output "Windows package SBOM self-test passed."
}
finally {
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
            throw "Windows package SBOM self-test refused unsafe cleanup."
        }

        Remove-Item -LiteralPath $resolvedTestRoot -Recurse -Force
    }
}
