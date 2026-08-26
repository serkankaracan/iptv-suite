using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace IptvSuite.ArchitectureTests;

[TestClass]
public sealed class M16PackageArtifactCaptureTests
{
    private const string CanaryMarker = "IPTVSUITE_TEST_ONLY_CANARY_V1";

    private static readonly string RepositoryRoot = FindRepositoryRoot();
    private static readonly string[] ProductionProjectReferences =
    [
        "IptvSuite.Application",
        "IptvSuite.Infrastructure",
    ];
    private static readonly string[] PackageSmokeParameters =
    [
        "Configuration",
        "DotNetPath",
        "EmitM14TraceMarkers",
        "RunWack",
        "EmitM16FinalArtifactSurfaces",
        "M16RunToken",
    ];
    private static readonly string[] ForbiddenM16UploadFragments =
    [
        ".artifacts/m16-final-artifact-scan/work",
        ".artifacts/m16-final-artifact-scan/process-io",
        ".artifacts/m16-final-artifact-scan/full-log",
        ".artifacts/m16-final-artifact-scan/scanner-io",
        ".artifacts/msix-smoke/m16-final-artifact-capture",
        ".artifacts/msix-smoke/m16-final-artifact-surfaces.json",
        ".artifacts/msix-smoke/m16-final-artifact-binding.json",
    ];
    private static readonly string[] M16BindingEvidenceProperties =
    [
        "SchemaVersion",
        "EvidenceKind",
        "RunId",
        "CommitSha",
        "PackageSha256",
        "PackageSbomApplicationPackageSha256",
        "ExactPackageInventorySha256",
        "PostScanPackageRehashPassed",
    ];

    [TestMethod]
    public void M16OnboardingCanaryRequiresTheExactOptInHandshake()
    {
        string packageSmoke = ReadRepositoryFile("eng", "Invoke-WindowsPackageSmoke.ps1");
        string harness = ReadRepositoryFile(
            "apps",
            "windows",
            "tests",
            "IptvSuite.PlaybackUiAcceptanceHarness",
            "Program.cs");
        string normalizedPackageSmoke = NormalizeWhitespace(packageSmoke);
        string normalizedHarness = NormalizeWhitespace(harness);

        StringAssert.Contains(
            normalizedPackageSmoke,
            $"$testCanaryMarker = \"{CanaryMarker}\"");
        StringAssert.Contains(
            normalizedPackageSmoke,
            "$expectedOnboardingPlaylistPath = if ($EmitM16FinalArtifactSurfaces) " +
            "{ \"/$testCanaryMarker/synthetic-onboarding.m3u\" } else " +
            "{ \"/synthetic-onboarding.m3u\" }");
        StringAssert.Contains(
            normalizedPackageSmoke,
            "if ($EmitM16FinalArtifactSurfaces) " +
            "{ $onboardingHarnessArgumentValues += $testCanaryMarker }");

        StringAssert.Contains(
            normalizedHarness,
            "private const string OnboardingPlaylistRoute = \"/synthetic-onboarding.m3u\";");
        StringAssert.Contains(
            normalizedHarness,
            "private const string M16CanaryOnboardingToken = TestCanary.Marker;");
        StringAssert.Contains(
            normalizedHarness,
            "private const string M16CanaryOnboardingPlaylistRoute = " +
            "\"/\" + TestCanary.Marker + OnboardingPlaylistRoute;");

        int normalRoute = IndexOfOrFail(
            normalizedHarness,
            "if (args is [OnboardingCommand, string onboardingFixtureRoot, " +
            "string onboardingControlDirectory, string pipeName])",
            0);
        int canaryRoute = IndexOfOrFail(
            normalizedHarness,
            "if (args is [OnboardingCommand, string canaryOnboardingFixtureRoot, " +
            "string canaryOnboardingControlDirectory, string canaryPipeName, " +
            "string canaryToken] && string.Equals( canaryToken, " +
            "M16CanaryOnboardingToken, StringComparison.Ordinal))",
            normalRoute);
        Assert.IsTrue(normalRoute < canaryRoute, "The normal four-argument route must remain first.");

        string normalBranch = normalizedHarness[normalRoute..canaryRoute];
        StringAssert.Contains(normalBranch, "pipeName, OnboardingPlaylistRoute, cancellation.Token");

        int invalidArguments = IndexOfOrFail(normalizedHarness, "return 2;", canaryRoute);
        string canaryBranch = normalizedHarness[canaryRoute..invalidArguments];
        StringAssert.Contains(canaryBranch, "StringComparison.Ordinal");
        StringAssert.Contains(
            canaryBranch,
            "canaryPipeName, M16CanaryOnboardingPlaylistRoute, cancellation.Token");
        Assert.IsFalse(
            canaryBranch.Contains("Contains(", StringComparison.Ordinal) ||
            canaryBranch.Contains("StartsWith(", StringComparison.Ordinal) ||
            canaryBranch.Contains("OrdinalIgnoreCase", StringComparison.Ordinal),
            "The M16 canary token must require an exact ordinal match.");
    }

    [TestMethod]
    public void ProductionPackageExcludesTheAcceptanceHarnessAndTestCanary()
    {
        string projectPath = Path.Combine(
            RepositoryRoot,
            "apps",
            "windows",
            "src",
            "IptvSuite.Windows",
            "IptvSuite.Windows.csproj");
        XDocument project = XDocument.Load(projectPath, LoadOptions.None);
        string[] projectReferences = project
            .Descendants()
            .Where(element => element.Name.LocalName == "ProjectReference")
            .Select(element => element.Attribute("Include")?.Value ?? string.Empty)
            .Select(path => Path.GetFileNameWithoutExtension(path) ?? string.Empty)
            .Where(name => name.Length > 0)
            .Order(StringComparer.Ordinal)
            .ToArray();

        CollectionAssert.AreEqual(
            ProductionProjectReferences,
            projectReferences);
        Assert.IsFalse(
            File.ReadAllText(projectPath).Contains(
                "IptvSuite.PlaybackUiAcceptanceHarness",
                StringComparison.Ordinal),
            "The production project must not reference the acceptance harness.");

        string packageSmoke = ReadRepositoryFile("eng", "Invoke-WindowsPackageSmoke.ps1");
        StringAssert.Contains(
            packageSmoke,
            "IptvSuite\\.PlaybackUiAcceptanceHarness(?:\\..*)?");
        StringAssert.Contains(
            packageSmoke,
            "throw \"Test canary marker detected in a production payload path.\"");
        StringAssert.Contains(
            packageSmoke,
            "Assert-ProductionPackagePayload -PackagePath $packages[0].FullName");
    }

    [TestMethod]
    public void M16PackageCaptureIsExplicitFixedBoundedAndOrdered()
    {
        string packageSmoke = ReadRepositoryFile("eng", "Invoke-WindowsPackageSmoke.ps1");

        string parameterBlock = packageSmoke[..IndexOfOrFail(
            packageSmoke,
            "Set-StrictMode -Version Latest",
            0)];
        string[] parameters = Regex.Matches(parameterBlock, @"\$(?<name>[A-Za-z][A-Za-z0-9]*)")
            .Select(match => match.Groups["name"].Value)
            .ToArray();
        CollectionAssert.AreEqual(
            PackageSmokeParameters,
            parameters,
            "The public package-smoke surface must not accept M16 roots, profiles, or limits.");

        StringAssert.Contains(packageSmoke, "function Initialize-M16CaptureRoot");
        StringAssert.Contains(packageSmoke, "function Invoke-M16ReleaseSurfaceScan");
        StringAssert.Contains(packageSmoke, "function New-M16ReleaseAcceptanceSupportArtifact");
        StringAssert.Contains(packageSmoke, "function Assert-M16ReleaseAcceptanceSupportArtifact");
        StringAssert.Contains(packageSmoke, "function Remove-ExactM16CaptureRoot");
        StringAssert.Contains(
            NormalizeWhitespace(packageSmoke),
            "if ([bool]$EmitM16FinalArtifactSurfaces -ne $m16RunTokenProvided) " +
            "{ throw \"The M16 final-artifact mode requires one exact controller-issued run token.\" }");
        int testingBuild = IndexOfOrFail(
            packageSmoke,
            "& $DotNetPath build $testingProjectPath",
            0);
        int testingBuildGuard = packageSmoke.LastIndexOf(
            "if ($EmitM16FinalArtifactSurfaces) {",
            testingBuild,
            StringComparison.Ordinal);
        Assert.IsTrue(
            testingBuildGuard >= 0 && testingBuild - testingBuildGuard < 200,
            "The testing scanner build must remain inside the explicit M16 mode.");
        Assert.AreEqual(
            1,
            Regex.Count(packageSmoke, Regex.Escape("& $DotNetPath build $testingProjectPath")),
            "The testing scanner build must have exactly one explicit-mode owner.");

        string scanner = ExtractBetween(
            packageSmoke,
            "function Invoke-M16ReleaseSurfaceScan",
            "function Remove-ExactDevelopmentPackage");
        StringAssert.Contains(
            NormalizeWhitespace(scanner),
            "[ValidateSet(\"owned-app-data\", \"exact-package\", \"support-artifact\")] " +
            "[string]$SurfaceId");
        StringAssert.Contains(
            NormalizeWhitespace(scanner),
            "$testingAssemblyPath, \"scan-release-artifacts\", $resolvedRoot, " +
            "\"M16\", \"FINAL_ARTIFACTS\"");
        StringAssert.Contains(scanner, "Read-WindowsM16FinalStrictJson");
        StringAssert.Contains(scanner, "Assert-WindowsM16FinalExactPropertySet");
        StringAssert.Contains(scanner, "Invoke-WindowsBoundedProcess");
        StringAssert.Contains(scanner, "-TimeoutMilliseconds 600000");
        StringAssert.Contains(scanner, "-MaximumOutputBytes 131072");
        Assert.IsFalse(
            scanner.Contains("Start-Process", StringComparison.Ordinal),
            "The package-side scanner must use a hard-capped Job Object runner.");
        StringAssert.Contains(scanner, "$standardOutputFile.Length -gt 4096");
        Assert.IsFalse(
            parameterBlock.Contains("RootPath", StringComparison.Ordinal) ||
            parameterBlock.Contains("SurfaceId", StringComparison.Ordinal) ||
            parameterBlock.Contains("Maximum", StringComparison.Ordinal) ||
            parameterBlock.Contains("Limit", StringComparison.Ordinal),
            "M16 scan roots and limits must not be caller-controlled.");

        int mainStart = IndexOfOrFail(
            packageSmoke,
            "try {\n    if ($EmitM16FinalArtifactSurfaces) {",
            0);
        int cleanCommit = IndexOfOrFail(
            packageSmoke,
            "$m16CommitSha = Get-M16CleanRepositoryCommit",
            mainStart);
        int productionBuild = IndexOfOrFail(
            packageSmoke,
            "& $DotNetPath build $projectPath",
            cleanCommit);
        Assert.IsTrue(
            cleanCommit < productionBuild,
            "The capture must bind a clean commit before producing the package.");

        int installRootCompletion = IndexOfOrFail(
            packageSmoke,
            "$packageInstallRootAuditResult = Complete-WindowsPackageInstallRootAudit",
            productionBuild);
        int sbomBinding = IndexOfOrFail(
            packageSmoke,
            "$packageSbomResult.ApplicationPackageSha256 -cne $packageSha256",
            installRootCompletion);
        int stagePackage = IndexOfOrFail(
            packageSmoke,
            "Copy-Item -LiteralPath $packages[0].FullName -Destination $stagedPackagePath",
            sbomBinding);
        int stagedHash = IndexOfOrFail(
            packageSmoke,
            "$packageSha256) {\n                throw \"The staged M16 package is not bound",
            stagePackage);
        int stagedReadLock = IndexOfOrFail(
            packageSmoke,
            "[System.IO.FileShare]::Read)",
            stagePackage);
        int expandPackage = IndexOfOrFail(
            packageSmoke,
            "Expand-MsixForInspection",
            stagedHash);
        int supportArtifact = IndexOfOrFail(
            packageSmoke,
            "$supportArtifactSurface = New-M16ReleaseAcceptanceSupportArtifact",
            expandPackage);
        int ownedScan = IndexOfOrFail(
            packageSmoke,
            "$ownedAppDataSurfaceReport = Invoke-M16ReleaseSurfaceScan",
            supportArtifact);
        int exactPackageScan = IndexOfOrFail(
            packageSmoke,
            "$exactPackageSurfaceReport = Invoke-M16ReleaseSurfaceScan",
            ownedScan);
        int supportScan = IndexOfOrFail(
            packageSmoke,
            "$supportArtifactSurfaceReport = Invoke-M16ReleaseSurfaceScan",
            exactPackageScan);
        int postScanRehash = IndexOfOrFail(
            packageSmoke,
            "$m16PostScanPackageSha256 = (Get-FileHash",
            supportScan);
        int supportRevalidation = IndexOfOrFail(
            packageSmoke,
            "$supportArtifactAfterScan = Assert-M16ReleaseAcceptanceSupportArtifact",
            postScanRehash);
        int aggregateBound = IndexOfOrFail(
            packageSmoke,
            "$m16AggregateEntryCount -gt 25000 -or",
            supportScan);
        int stableAfterScan = IndexOfOrFail(
            packageSmoke,
            "Assert-M16RepositoryStable -ExpectedCommit $m16CommitSha",
            aggregateBound);
        int uninstall = IndexOfOrFail(
            packageSmoke,
            "Remove-ExactDevelopmentPackage",
            stableAfterScan);

        AssertAscending(
            installRootCompletion,
            sbomBinding,
            stagePackage,
            stagedReadLock,
            stagedHash,
            expandPackage,
            supportArtifact,
            ownedScan,
            exactPackageScan,
            supportScan,
            postScanRehash,
            supportRevalidation,
            aggregateBound,
            stableAfterScan,
            uninstall);
        StringAssert.Contains(packageSmoke, "$m16AggregateFileBytes -gt 8589934592");
        StringAssert.Contains(
            packageSmoke,
            "$m16SurfaceReports[0].SurfaceId -cne \"owned-app-data\"");
        StringAssert.Contains(
            packageSmoke,
            "$m16SurfaceReports[1].SurfaceId -cne \"exact-package\"");
        StringAssert.Contains(
            packageSmoke,
            "$m16SurfaceReports[2].SurfaceId -cne \"support-artifact\"");

        int mainFinally = IndexOfOrFail(packageSmoke, "\nfinally {", uninstall);
        int packageOutputCleanup = IndexOfOrFail(
            packageSmoke,
            "\"Remove exact package-output directory\"",
            mainFinally);
        int failedCaptureCleanup = IndexOfOrFail(
            packageSmoke,
            "\"Remove failed M16 final-artifact capture\"",
            packageOutputCleanup);
        int failureGate = IndexOfOrFail(
            packageSmoke,
            "if ($null -ne $primaryFailure -or $cleanupFailures.Count -ne 0)",
            failedCaptureCleanup);

        int stableBeforeEvidence = IndexOfOrFail(
            packageSmoke,
            "Assert-M16RepositoryStable -ExpectedCommit $m16CommitSha",
            failureGate);
        int evidenceWrite = IndexOfOrFail(
            packageSmoke,
            "Write-M16SurfaceEvidenceAtomically -Value $m16SurfaceEvidence",
            stableBeforeEvidence);
        int bindingWrite = IndexOfOrFail(
            packageSmoke,
            "Write-M16BindingEvidenceAtomically -Value $m16BindingEvidence",
            stableBeforeEvidence);
        int retainExactPackage = IndexOfOrFail(
            packageSmoke,
            "Remove-ExactM16CaptureRoot -RetainExactPackage",
            evidenceWrite);
        AssertAscending(
            mainFinally,
            packageOutputCleanup,
            failedCaptureCleanup,
            failureGate,
            stableBeforeEvidence,
            bindingWrite,
            evidenceWrite,
            retainExactPackage);
        Assert.AreEqual(
            2,
            Regex.Count(
                packageSmoke,
                Regex.Escape("Assert-M16RepositoryStable -ExpectedCommit $m16CommitSha")),
            "The repository must be revalidated after scanning and before evidence publication.");
    }

    [TestMethod]
    public void PackageRegistrationIntentAndConcurrencyBindingRemainExact()
    {
        string packageSmoke = ReadRepositoryFile("eng", "Invoke-WindowsPackageSmoke.ps1");
        string normalized = NormalizeWhitespace(packageSmoke);

        StringAssert.Contains(
            normalized,
            "$packageIdentityMutexName = " +
            "\"Global\\IptvSuite.PackageSmoke.IptvSuite.LocalDev.6f0d9a64\"");
        StringAssert.Contains(packageSmoke, "function Enter-WindowsPackageIdentityMutex");
        StringAssert.Contains(
            packageSmoke,
            "function Assert-WindowsPackageIdentityMutexOwnedByController");
        StringAssert.Contains(
            normalized,
            "if ($EmitM16FinalArtifactSurfaces) " +
            "{ Assert-WindowsPackageIdentityMutexOwnedByController } else " +
            "{ $packageIdentityMutex = Enter-WindowsPackageIdentityMutex }");
        StringAssert.Contains(packageSmoke, "PackageFullNameFromId");
        StringAssert.Contains(packageSmoke, "ProcessorArchitecture = 9");

        string ownershipWriter = ExtractBetween(
            packageSmoke,
            "function Write-M16CleanupOwnershipValue",
            "function Initialize-M16CaptureRoot");
        int schemaVersion = IndexOfOrFail(ownershipWriter, "SchemaVersion = 1", 0);
        int runToken = IndexOfOrFail(ownershipWriter, "RunToken = $runId", schemaVersion);
        int expectedFullName = IndexOfOrFail(
            ownershipWriter,
            "ExpectedPackageFullName = $Value",
            runToken);
        AssertAscending(schemaVersion, runToken, expectedFullName);
        StringAssert.Contains(ownershipWriter, "ConvertTo-Json -Depth 2 -Compress");
        StringAssert.Contains(ownershipWriter, "{ 512 } else { 128 }");
        StringAssert.Contains(ownershipWriter, "[System.IO.FileMode]::CreateNew");
        StringAssert.Contains(ownershipWriter, "$stream.Flush($true)");

        int expectedIdentity = IndexOfOrFail(
            packageSmoke,
            "$expectedPackageFullName =",
            0);
        int uninstallBeforeInstall = IndexOfOrFail(
            packageSmoke,
            "Remove-ExactDevelopmentPackage",
            expectedIdentity);
        int durableIntent = IndexOfOrFail(
            packageSmoke,
            "-Name \"package-registration.intent\"",
            uninstallBeforeInstall);
        int exactIntentValue = IndexOfOrFail(
            packageSmoke,
            "-Value $expectedPackageFullName",
            durableIntent);
        int installAttempted = IndexOfOrFail(
            packageSmoke,
            "$installAttempted = $true",
            exactIntentValue);
        int addPackage = IndexOfOrFail(
            packageSmoke,
            "Add-AppxPackage",
            installAttempted);
        int installedIdentityCheck = IndexOfOrFail(
            packageSmoke,
            "$installedPackageFullName -cne $expectedPackageFullName",
            addPackage);
        AssertAscending(
            expectedIdentity,
            uninstallBeforeInstall,
            durableIntent,
            exactIntentValue,
            installAttempted,
            addPackage,
            installedIdentityCheck);
    }

    [TestMethod]
    public void M16SupportAndIntermediateEvidenceRemainSanitizedAndExact()
    {
        string packageSmoke = ReadRepositoryFile("eng", "Invoke-WindowsPackageSmoke.ps1");
        string supportArtifact = ExtractBetween(
            packageSmoke,
            "function New-M16ReleaseAcceptanceSupportArtifact",
            "function Invoke-M16ReleaseSurfaceScan");

        StringAssert.Contains(supportArtifact, "Scope = \"ReleaseAcceptanceSupportArtifact\"");
        StringAssert.Contains(supportArtifact, "[System.IO.FileMode]::CreateNew");
        StringAssert.Contains(supportArtifact, "Assert-M16ReleaseAcceptanceSupportArtifact");
        foreach (string falseField in new[]
        {
            "RawLocatorIncluded = $false",
            "RequestHeadersOrBodiesIncluded = $false",
            "FullMemoryDumpIncluded = $false",
            "AutomatedUploadEnabled = $false",
        })
        {
            StringAssert.Contains(supportArtifact, falseField);
        }

        string bindingBlock = ExtractBetween(
            packageSmoke,
            "$m16BindingEvidence = [ordered]@{",
            "$m16SurfaceEvidence = [ordered]@{");
        string[] bindingProperties = Regex.Matches(
                bindingBlock,
                @"(?m)^\s{12}(?<name>[A-Za-z][A-Za-z0-9]*)\s*=")
            .Select(match => match.Groups["name"].Value)
            .ToArray();
        CollectionAssert.AreEqual(
            M16BindingEvidenceProperties,
            bindingProperties,
            "The controller binding schema must remain exact and ordered.");
        StringAssert.Contains(
            bindingBlock,
            "EvidenceKind = \"PackageBoundFinalArtifactBinding\"");
        StringAssert.Contains(
            bindingBlock,
            "PackageSha256 = $m16PostScanPackageSha256");
        StringAssert.Contains(
            bindingBlock,
            "PostScanPackageRehashPassed = $true");

        string evidenceBlock = ExtractBetween(
            packageSmoke,
            "$m16SurfaceEvidence = [ordered]@{",
            "Write-M16SurfaceEvidenceAtomically -Value $m16SurfaceEvidence");
        string[] actualProperties = Regex.Matches(
                evidenceBlock,
                @"(?m)^\s{12}(?<name>[A-Za-z][A-Za-z0-9]*)\s*=")
            .Select(match => match.Groups["name"].Value)
            .ToArray();
        string[] expectedProperties =
        [
            "SchemaVersion",
            "Milestone",
            "EvidenceKind",
            "Result",
            "RunId",
            "CommitSha",
            "PackageSha256",
            "PackageSbomApplicationPackageSha256",
            "ScannerProfile",
            "Surfaces",
            "SameBuildBindingPassed",
            "RepositoryStable",
            "RawSurfacesUploaded",
            "SupportArtifactScope",
        ];
        CollectionAssert.AreEqual(
            expectedProperties,
            actualProperties,
            "The package-side intermediate evidence schema must remain exact and ordered.");

        StringAssert.Contains(evidenceBlock, "EvidenceKind = \"PackageBoundFinalArtifactSurfaces\"");
        StringAssert.Contains(evidenceBlock, "ScannerProfile = \"M16ReleaseCandidate\"");
        StringAssert.Contains(evidenceBlock, "SameBuildBindingPassed = $true");
        StringAssert.Contains(evidenceBlock, "RepositoryStable = $true");
        StringAssert.Contains(evidenceBlock, "RawSurfacesUploaded = $false");
        StringAssert.Contains(evidenceBlock, "SupportArtifactScope = \"ReleaseAcceptanceOnly\"");
        Assert.IsFalse(
            Regex.IsMatch(evidenceBlock, @"(?i)\b(raw(path|root)|locator|header|body|dump)\b"),
            "The sanitized package-side evidence must not expose raw artifact content or locations.");

        string normalized = NormalizeWhitespace(packageSmoke);
        StringAssert.Contains(
            normalized,
            "if ($EmitM16FinalArtifactSurfaces) " +
            "{ Initialize-M16CaptureRoot }");
        StringAssert.Contains(
            normalized,
            "Remove-ExactM16CaptureRoot -RetainExactPackage");
        StringAssert.Contains(
            normalized,
            "-Name \"Remove failed M16 final-artifact capture\"");
        StringAssert.Contains(
            normalized,
            "-Action { Remove-ExactM16CaptureRoot }");
    }

    [TestMethod]
    public void NormalPackageSmokeDoesNotDeleteM16IntermediateEvidence()
    {
        string packageSmoke = ReadRepositoryFile("eng", "Invoke-WindowsPackageSmoke.ps1");
        string staleBase = ExtractBetween(
            packageSmoke,
            "    $staleEvidencePaths = @(",
            "    if ($EmitM16FinalArtifactSurfaces) {\n        $staleEvidencePaths += @(");
        Assert.IsFalse(
            staleBase.Contains("m16SurfaceEvidencePath", StringComparison.Ordinal) ||
            staleBase.Contains("m16BindingEvidencePath", StringComparison.Ordinal),
            "Normal startup must not delete evidence owned by an M16 controller.");
        string staleM16Branch = ExtractBetween(
            packageSmoke,
            "    if ($EmitM16FinalArtifactSurfaces) {\n        $staleEvidencePaths += @(",
            "    foreach ($staleEvidencePath in $staleEvidencePaths)");
        StringAssert.Contains(staleM16Branch, "$m16SurfaceEvidencePath");
        StringAssert.Contains(staleM16Branch, "$m16BindingEvidencePath");

        string partialBase = ExtractBetween(
            packageSmoke,
            "    $partialEvidencePaths = @(",
            "    if ($EmitM16FinalArtifactSurfaces) {\n        $partialEvidencePaths += @(");
        Assert.IsFalse(
            partialBase.Contains("m16SurfaceEvidencePath", StringComparison.Ordinal) ||
            partialBase.Contains("m16BindingEvidencePath", StringComparison.Ordinal),
            "Normal publication rollback must not delete M16 intermediates.");
        string partialM16Branch = ExtractBetween(
            packageSmoke,
            "    if ($EmitM16FinalArtifactSurfaces) {\n        $partialEvidencePaths += @(",
            "    foreach ($partialEvidencePath in $partialEvidencePaths)");
        StringAssert.Contains(partialM16Branch, "$m16SurfaceEvidencePath");
        StringAssert.Contains(partialM16Branch, "$m16BindingEvidencePath");
    }

    [TestMethod]
    public void WindowsWorkflowKeepsM16DispatchIsolatedAndUploadsOnlyFinalEvidence()
    {
        string workflow = ReadRepositoryFile(".github", "workflows", "windows-quality.yml");
        string normalized = NormalizeWhitespace(workflow);

        string dispatchInput = ExtractBetween(
            workflow,
            "      run_m16_final_artifacts:",
            "\npermissions:");
        Assert.AreEqual(
            "run_m16_final_artifacts: description: Run the bounded M16 four-surface " +
            "final-artifact canary scan required: false default: false type: boolean",
            NormalizeWhitespace(dispatchInput),
            "The M16 workflow-dispatch input must remain an exact optional boolean.");

        StringAssert.Contains(
            normalized,
            "- name: Reject incompatible package acceptance modes " +
            "if: ${{ github.event_name == 'workflow_dispatch' && inputs.run_wack && " +
            "inputs.run_m16_final_artifacts }} shell: pwsh run: throw " +
            "\"WACK and M16 final-artifact modes must run independently.\"");
        StringAssert.Contains(
            normalized,
            "- name: Build, scan, install, launch, and remove signed MSIX " +
            "if: ${{ github.event_name != 'workflow_dispatch' || " +
            "(!inputs.run_wack && !inputs.run_m16_final_artifacts) }} " +
            "shell: powershell run: .\\eng\\Invoke-WindowsPackageSmoke.ps1 " +
            "-Configuration Release");
        StringAssert.Contains(
            normalized,
            "- name: Build, scan, install, launch, run development WACK preflight, " +
            "and remove signed MSIX if: ${{ github.event_name == 'workflow_dispatch' && " +
            "inputs.run_wack && !inputs.run_m16_final_artifacts }} shell: powershell " +
            "run: .\\eng\\Invoke-WindowsPackageSmoke.ps1 -Configuration Release -RunWack");
        StringAssert.Contains(
            normalized,
            "- name: Build and scan M16 package-bound final-artifact surfaces " +
            "if: ${{ github.event_name == 'workflow_dispatch' && " +
            "inputs.run_m16_final_artifacts && !inputs.run_wack }} shell: powershell " +
            "run: .\\eng\\Invoke-WindowsFinalArtifactCanaryScan.ps1");

        string m16Upload = ExtractBetween(
            workflow,
            "      - name: Upload sanitized M16 final-artifact evidence",
            "      - name: Upload sanitized development-identity WACK preflight evidence");
        StringAssert.Contains(
            NormalizeWhitespace(m16Upload),
            "if: ${{ success() && github.event_name == 'workflow_dispatch' && " +
            "inputs.run_m16_final_artifacts }} uses: actions/upload-artifact@");
        Assert.AreEqual(
            1,
            Regex.Count(
                m16Upload,
                @"(?m)^\s+path:\s+\.artifacts/m16-final-artifact-scan/last-success\.json\s*$"),
            "M16 must upload exactly one sanitized final evidence path.");
        Assert.AreEqual(
            1,
            Regex.Count(m16Upload, @"(?m)^\s+path:\s+"),
            "The M16 upload step must not include additional paths.");
        Assert.IsFalse(
            m16Upload.Contains("path: |", StringComparison.Ordinal),
            "The M16 upload step must remain a single-file upload.");

        string uploadBlocks = string.Join(
            "\n",
            Regex.Matches(
                    workflow,
                    @"(?ms)^\s{6}- name: Upload .*?(?=^\s{6}- name:|^\s{2}[A-Za-z0-9_-]+:|\z)")
                .Select(match => match.Value)
                .Where(block => block.Contains("actions/upload-artifact@", StringComparison.Ordinal)));
        foreach (string forbiddenFragment in ForbiddenM16UploadFragments)
        {
            Assert.IsFalse(
                uploadBlocks.Contains(forbiddenFragment, StringComparison.OrdinalIgnoreCase),
                $"Raw or intermediate M16 path must not be uploaded: {forbiddenFragment}");
        }

        Assert.IsFalse(
            workflow.Contains("continue-on-error", StringComparison.OrdinalIgnoreCase),
            "The Windows workflow must not downgrade an M16 or quality failure.");
    }

    private static string ReadRepositoryFile(params string[] pathSegments)
    {
        return File.ReadAllText(Path.Combine([RepositoryRoot, .. pathSegments]))
            .Replace("\r\n", "\n", StringComparison.Ordinal);
    }

    private static string NormalizeWhitespace(string value) =>
        Regex.Replace(value, @"\s+", " ").Trim();

    private static string ExtractBetween(string value, string startMarker, string endMarker)
    {
        int start = IndexOfOrFail(value, startMarker, 0);
        int end = IndexOfOrFail(value, endMarker, start + startMarker.Length);
        return value[start..end];
    }

    private static int IndexOfOrFail(string value, string marker, int startIndex)
    {
        int index = value.IndexOf(marker, startIndex, StringComparison.Ordinal);
        Assert.IsTrue(index >= 0, $"Required contract marker was not found: {marker}");
        return index;
    }

    private static void AssertAscending(params int[] indices)
    {
        for (int index = 1; index < indices.Length; index++)
        {
            Assert.IsTrue(
                indices[index - 1] < indices[index],
                $"Contract operation {index} must precede operation {index + 1}.");
        }
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Directory.Packages.props")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Repository root was not found from the test output directory.");
    }
}
