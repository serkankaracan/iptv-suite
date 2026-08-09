[CmdletBinding()]
param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",

    [string]$DotNetPath = "dotnet"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$expectedName = "IptvSuite.LocalDev.6f0d9a64"
$expectedPublisher = "CN=IptvSuite Local Development"
$expectedApplicationId = "App"
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$projectPath = Join-Path $repositoryRoot "apps\windows\src\IptvSuite.Windows\IptvSuite.Windows.csproj"
$sourceManifestPath = Join-Path $repositoryRoot "apps\windows\src\IptvSuite.Windows\Package.appxmanifest"
$artifactRoot = Join-Path $repositoryRoot ".artifacts\msix-smoke"
$runId = [Guid]::NewGuid().ToString("N")
$packageOutput = Join-Path $artifactRoot "packages\$runId"
$publicCertificatePath = Join-Path $artifactRoot "$runId.cer"
$evidencePath = Join-Path $artifactRoot "last-success.json"
$failureEvidencePath = Join-Path $artifactRoot "last-failure.json"

$certificate = $null
$installedPackage = $null
$launchedProcess = $null
$environmentBackup = @{}
$msBuildEnvironment = @{
    AppxBundle                    = "Never"
    AppxPackageDir                = "$packageOutput\"
    AppxPackageSigningEnabled     = "true"
    AppxSymbolPackageEnabled      = "false"
    DebugSymbols                  = "false"
    DebugType                     = "None"
    GenerateAppxPackageOnBuild    = "true"
    UapAppxPackageBuildMode       = "SideloadOnly"
}

New-Item -ItemType Directory -Path $artifactRoot -Force | Out-Null

function Assert-ManifestPolicy {
    param(
        [Parameter(Mandatory)]
        [xml]$Manifest
    )

    $identity = $Manifest.SelectSingleNode("/*[local-name()='Package']/*[local-name()='Identity']")
    if ($null -eq $identity) {
        throw "Package identity is missing."
    }

    if ($identity.GetAttribute("Name") -ne $expectedName) {
        throw "Unexpected package name."
    }

    if ($identity.GetAttribute("Publisher") -ne $expectedPublisher) {
        throw "Unexpected package publisher."
    }

    $applications = @($Manifest.SelectNodes("/*[local-name()='Package']/*[local-name()='Applications']/*[local-name()='Application']"))
    if ($applications.Count -ne 1 -or $applications[0].GetAttribute("Id") -ne $expectedApplicationId) {
        throw "The package must contain exactly the M1 application."
    }

    $capabilities = @(
        $Manifest.SelectNodes("/*[local-name()='Package']/*[local-name()='Capabilities']/*") |
            ForEach-Object { $_.GetAttribute("Name") }
    )
    $capabilityDifference = @(Compare-Object -ReferenceObject @("runFullTrust") -DifferenceObject $capabilities)
    if ($capabilityDifference.Count -ne 0) {
        throw "Unexpected capability set: $($capabilities -join ', ')"
    }
}

function Assert-BuiltManifestPolicy {
    param(
        [Parameter(Mandatory)]
        [xml]$Manifest
    )

    Assert-ManifestPolicy -Manifest $Manifest

    $identity = $Manifest.SelectSingleNode("/*[local-name()='Package']/*[local-name()='Identity']")
    if ($identity.GetAttribute("ProcessorArchitecture") -ne "x64") {
        throw "The built package must target x64 only."
    }

    $targetFamilies = @($Manifest.SelectNodes("/*[local-name()='Package']/*[local-name()='Dependencies']/*[local-name()='TargetDeviceFamily']"))
    if ($targetFamilies.Count -ne 1 -or
        $targetFamilies[0].GetAttribute("Name") -ne "Windows.Desktop" -or
        $targetFamilies[0].GetAttribute("MinVersion") -ne "10.0.26100.0") {
        throw "Unexpected Windows device-family baseline."
    }

    $frameworkDependencies = @($Manifest.SelectNodes("/*[local-name()='Package']/*[local-name()='Dependencies']/*[local-name()='PackageDependency']"))
    if ($frameworkDependencies.Count -ne 1 -or
        $frameworkDependencies[0].GetAttribute("Name") -ne "Microsoft.WindowsAppRuntime.2" -or
        $frameworkDependencies[0].GetAttribute("MinVersion") -ne "2.3.1.0") {
        throw "The MSIX must remain framework-dependent on Windows App Runtime 2.3.1."
    }
}

function Remove-ExactDevelopmentPackage {
    $packages = @(
        Get-AppxPackage -Name $expectedName -ErrorAction SilentlyContinue |
            Where-Object { $_.Publisher -eq $expectedPublisher }
    )

    if ($packages.Count -gt 1) {
        throw "Refusing cleanup: more than one exact development package is registered."
    }

    if ($packages.Count -eq 1) {
        Remove-AppxPackage -Package $packages[0].PackageFullName
    }
}

try {
    $currentIdentity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $currentPrincipal = [Security.Principal.WindowsPrincipal]::new($currentIdentity)
    if (-not $currentPrincipal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw "Run this smoke test from an elevated PowerShell session so the temporary public certificate can be trusted and removed."
    }

    if (Test-Path -LiteralPath $failureEvidencePath) {
        Remove-Item -LiteralPath $failureEvidencePath -Force
    }

    $expectedSdk = (Get-Content -Raw (Join-Path $repositoryRoot "global.json") | ConvertFrom-Json).sdk.version
    $actualSdk = (& $DotNetPath --version).Trim()
    if ($LASTEXITCODE -ne 0 -or $actualSdk -ne $expectedSdk) {
        throw "Expected .NET SDK $expectedSdk, received '$actualSdk'."
    }

    [xml]$sourceManifest = Get-Content -Raw $sourceManifestPath
    Assert-ManifestPolicy -Manifest $sourceManifest

    if (Get-ChildItem -Path (Join-Path $repositoryRoot "apps") -Filter "Package.StoreAssociation.xml" -Recurse -File) {
        throw "Package.StoreAssociation.xml is forbidden for the disposable M1 identity."
    }

    New-Item -ItemType Directory -Path $artifactRoot -Force | Out-Null
    New-Item -ItemType Directory -Path $packageOutput -Force | Out-Null

    $certificate = New-SelfSignedCertificate `
        -Type Custom `
        -Subject $expectedPublisher `
        -CertStoreLocation "Cert:\CurrentUser\My" `
        -KeyAlgorithm RSA `
        -KeyLength 2048 `
        -HashAlgorithm SHA256 `
        -KeyExportPolicy NonExportable `
        -KeyUsage DigitalSignature `
        -NotAfter (Get-Date).AddDays(7) `
        -TextExtension @(
            "2.5.29.37={text}1.3.6.1.5.5.7.3.3",
            "2.5.29.19={text}"
        )

    if (-not $certificate.HasPrivateKey -or $certificate.Subject -ne $expectedPublisher) {
        throw "The local signing certificate does not match the manifest publisher."
    }

    $enhancedKeyUsageExtension = $certificate.Extensions |
        Where-Object { $_.Oid.Value -eq "2.5.29.37" } |
        Select-Object -First 1
    $enhancedKeyUsage = [System.Security.Cryptography.X509Certificates.X509EnhancedKeyUsageExtension]$enhancedKeyUsageExtension
    $codeSigningUsage = @($enhancedKeyUsage.EnhancedKeyUsages | ForEach-Object { $_.Value })
    if ($codeSigningUsage -notcontains "1.3.6.1.5.5.7.3.3") {
        throw "The local signing certificate is missing the code-signing EKU."
    }

    Export-Certificate -Cert $certificate -FilePath $publicCertificatePath | Out-Null
    Import-Certificate -FilePath $publicCertificatePath -CertStoreLocation "Cert:\LocalMachine\TrustedPeople" | Out-Null

    $msBuildEnvironment.PackageCertificateThumbprint = $certificate.Thumbprint
    foreach ($entry in $msBuildEnvironment.GetEnumerator()) {
        $environmentBackup[$entry.Key] = [Environment]::GetEnvironmentVariable($entry.Key, "Process")
        [Environment]::SetEnvironmentVariable($entry.Key, $entry.Value, "Process")
    }

    & $DotNetPath build $projectPath -c $Configuration -p:Platform=x64 --no-restore
    if ($LASTEXITCODE -ne 0) {
        throw "Signed MSIX build failed."
    }

    $packages = @(
        Get-ChildItem -Path $packageOutput -Filter "IptvSuite.Windows_*.msix" -Recurse -File |
            Where-Object { $_.FullName -notmatch "[\\/]Dependencies[\\/]" }
    )
    if ($packages.Count -ne 1) {
        throw "Expected exactly one x64 MSIX, found $($packages.Count)."
    }

    $runtimeDependencies = @(
        Get-ChildItem -Path $packageOutput -Filter "Microsoft.WindowsAppRuntime.2.msix" -Recurse -File |
            Where-Object { $_.Directory.Name -eq "x64" }
    )
    if ($runtimeDependencies.Count -ne 1) {
        throw "Expected exactly one x64 Windows App Runtime dependency package."
    }

    $signature = Get-AuthenticodeSignature -FilePath $packages[0].FullName
    if ($null -eq $signature.SignerCertificate -or $signature.SignerCertificate.Thumbprint -ne $certificate.Thumbprint) {
        throw "The generated MSIX signer does not match the ephemeral certificate."
    }

    if ($signature.Status -in @("HashMismatch", "NotSigned")) {
        throw "The generated MSIX signature failed integrity validation: $($signature.Status)."
    }

    Remove-ExactDevelopmentPackage
    Add-AppxPackage -Path $packages[0].FullName -DependencyPath $runtimeDependencies[0].FullName

    $installedPackages = @(
        Get-AppxPackage -Name $expectedName |
            Where-Object { $_.Publisher -eq $expectedPublisher }
    )
    if ($installedPackages.Count -ne 1) {
        throw "Expected exactly one installed development package."
    }

    $installedPackage = $installedPackages[0]
    if ($installedPackage.Architecture -ne "X64") {
        throw "Expected an x64 package, received $($installedPackage.Architecture)."
    }

    $installedManifest = $installedPackage | Get-AppxPackageManifest
    [xml]$installedManifestXml = $installedManifest.Package.OuterXml
    Assert-BuiltManifestPolicy -Manifest $installedManifestXml

    $existingProcesses = @(Get-Process -Name "IptvSuite.Windows" -ErrorAction SilentlyContinue)
    if ($existingProcesses.Count -ne 0) {
        throw "IptvSuite.Windows is already running; refusing an ambiguous launch smoke."
    }

    $aumid = "$($installedPackage.PackageFamilyName)!$expectedApplicationId"
    Start-Process -FilePath "explorer.exe" -ArgumentList "shell:AppsFolder\$aumid" | Out-Null

    $launchDeadline = (Get-Date).AddSeconds(20)
    do {
        Start-Sleep -Milliseconds 250
        $launchedProcess = Get-Process -Name "IptvSuite.Windows" -ErrorAction SilentlyContinue |
            Where-Object { $_.MainWindowHandle -ne 0 } |
            Select-Object -First 1
    } while ($null -eq $launchedProcess -and (Get-Date) -lt $launchDeadline)

    if ($null -eq $launchedProcess) {
        throw "The installed package did not create a visible application window."
    }

    Start-Sleep -Seconds 2
    if (-not $launchedProcess.CloseMainWindow()) {
        throw "The application rejected a normal window-close request."
    }

    if (-not $launchedProcess.WaitForExit(10000)) {
        throw "The application did not exit after a normal window-close request."
    }

    $packageFamilyName = $installedPackage.PackageFamilyName
    Remove-ExactDevelopmentPackage
    $installedPackage = $null

    if (Get-AppxPackage -Name $expectedName -ErrorAction SilentlyContinue) {
        throw "The development package is still registered after uninstall."
    }

    $appDataPath = Join-Path $env:LOCALAPPDATA "Packages\$packageFamilyName"
    $cleanupDeadline = (Get-Date).AddSeconds(10)
    while ((Test-Path -LiteralPath $appDataPath) -and (Get-Date) -lt $cleanupDeadline) {
        Start-Sleep -Milliseconds 250
    }
    if (Test-Path -LiteralPath $appDataPath) {
        throw "Package app-data remains after uninstall: $appDataPath"
    }

    [ordered]@{
        CompletedAt       = (Get-Date).ToUniversalTime().ToString("O")
        Configuration     = $Configuration
        DotNetSdk         = $actualSdk
        PackageFile       = $packages[0].Name
        PackageName       = $expectedName
        PackagePublisher  = $expectedPublisher
        PackageFamilyName = $packageFamilyName
        Architecture      = "x64"
        Capabilities      = @("runFullTrust")
        SignatureStatus   = $signature.Status.ToString()
        NormalClose       = $true
        PackageRemoved    = $true
    } | ConvertTo-Json | Set-Content -Path $evidencePath -Encoding UTF8

    Write-Host "MSIX smoke passed: signed, installed, launched, closed, and uninstalled $($packages[0].Name)."
}
catch {
    [ordered]@{
        FailedAt      = (Get-Date).ToUniversalTime().ToString("O")
        Configuration = $Configuration
        Error         = $_.Exception.Message
    } | ConvertTo-Json | Set-Content -Path $failureEvidencePath -Encoding UTF8

    throw
}
finally {
    if ($null -ne $launchedProcess -and -not $launchedProcess.HasExited) {
        Stop-Process -Id $launchedProcess.Id -Force -ErrorAction SilentlyContinue
    }

    if ($null -ne $installedPackage) {
        Remove-ExactDevelopmentPackage
    }

    foreach ($entry in $environmentBackup.GetEnumerator()) {
        [Environment]::SetEnvironmentVariable($entry.Key, $entry.Value, "Process")
    }

    if ($null -ne $certificate) {
        foreach ($store in @("Cert:\LocalMachine\TrustedPeople", "Cert:\CurrentUser\My")) {
            $certificatePath = "$store\$($certificate.Thumbprint)"
            $candidate = Get-Item -LiteralPath $certificatePath -ErrorAction SilentlyContinue
            if ($null -ne $candidate) {
                if ($candidate.Subject -ne $expectedPublisher) {
                    throw "Refusing certificate cleanup because the subject does not match."
                }

                Remove-Item -LiteralPath $certificatePath -Force
            }
        }
    }

    if (Test-Path -LiteralPath $publicCertificatePath) {
        Remove-Item -LiteralPath $publicCertificatePath -Force
    }
}
