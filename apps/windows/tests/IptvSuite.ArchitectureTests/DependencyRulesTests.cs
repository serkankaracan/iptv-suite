using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace IptvSuite.ArchitectureTests;

[TestClass]
public sealed class DependencyRulesTests
{
    private const string DevelopmentIdentity = "IptvSuite.LocalDev.6f0d9a64";
    private const string DevelopmentPublisher = "CN=IptvSuite Local Development";
    private const string LifecycleHarnessIdentity = "ProtectedStore.PackageLifecycleTest.Local.5d8c7a91";
    private const string LifecycleHarnessPublisher = "CN=Protected Store Package Lifecycle Local Test";

    private static readonly string RepositoryRoot = FindRepositoryRoot();
    private static readonly string[] RequiredCapabilities = ["runFullTrust"];

    private static readonly ProjectRule[] ProjectRules =
    [
        new(
            "IptvSuite.Domain",
            "apps/windows/src/IptvSuite.Domain/IptvSuite.Domain.csproj",
            [],
            [],
            []),
        new(
            "IptvSuite.Application",
            "apps/windows/src/IptvSuite.Application/IptvSuite.Application.csproj",
            ["IptvSuite.Domain"],
            [],
            []),
        new(
            "IptvSuite.Infrastructure",
            "apps/windows/src/IptvSuite.Infrastructure/IptvSuite.Infrastructure.csproj",
            ["IptvSuite.Application"],
            [],
            ["System.Security.Cryptography.ProtectedData"]),
        new(
            "IptvSuite.Windows",
            "apps/windows/src/IptvSuite.Windows/IptvSuite.Windows.csproj",
            ["IptvSuite.Application", "IptvSuite.Infrastructure"],
            [],
            ["Microsoft.Windows.SDK.BuildTools", "Microsoft.WindowsAppSDK"]),
        new(
            "IptvSuite.ArchitectureTests",
            "apps/windows/tests/IptvSuite.ArchitectureTests/IptvSuite.ArchitectureTests.csproj",
            [],
            [],
            ["MSTest"]),
        new(
            "IptvSuite.Testing",
            "apps/windows/tests/IptvSuite.Testing/IptvSuite.Testing.csproj",
            [],
            ["Microsoft.AspNetCore.App"],
            ["Microsoft.Extensions.TimeProvider.Testing"]),
        new(
            "IptvSuite.UnitTests",
            "apps/windows/tests/IptvSuite.UnitTests/IptvSuite.UnitTests.csproj",
            ["IptvSuite.Application", "IptvSuite.Domain", "IptvSuite.Testing"],
            [],
            ["MSTest"]),
        new(
            "IptvSuite.IntegrationTests",
            "apps/windows/tests/IptvSuite.IntegrationTests/IptvSuite.IntegrationTests.csproj",
            ["IptvSuite.Application", "IptvSuite.Infrastructure", "IptvSuite.Testing"],
            [],
            ["MSTest"]),
        new(
            "IptvSuite.SecretStoreSpike",
            "apps/windows/tests/IptvSuite.SecretStoreSpike/IptvSuite.SecretStoreSpike.csproj",
            ["IptvSuite.Application", "IptvSuite.Domain", "IptvSuite.Infrastructure", "IptvSuite.Testing"],
            [],
            []),
        new(
            "IptvSuite.PackageLifecycleHarness",
            "apps/windows/tests/IptvSuite.PackageLifecycleHarness/IptvSuite.PackageLifecycleHarness.csproj",
            ["IptvSuite.Application", "IptvSuite.Domain", "IptvSuite.Infrastructure"],
            [],
            ["Microsoft.Windows.SDK.BuildTools", "Microsoft.WindowsAppSDK"]),
    ];

    [TestMethod]
    public void ProjectReferencesMatchApprovedArchitecture()
    {
        Dictionary<string, string[]> graph = [];

        foreach (ProjectRule rule in ProjectRules)
        {
            XDocument project = LoadXml(rule.RelativePath);
            string[] actual = GetIncludes(project, "ProjectReference")
                .Select(path => Path.GetFileNameWithoutExtension(path) ?? throw new InvalidDataException("Invalid project path."))
                .Order(StringComparer.Ordinal)
                .ToArray();
            string[] expected = rule.ProjectReferences.Order(StringComparer.Ordinal).ToArray();

            CollectionAssert.AreEqual(expected, actual, $"Unexpected project reference in {rule.Name}.");

            string[] actualFrameworks = GetIncludes(project, "FrameworkReference")
                .Order(StringComparer.Ordinal)
                .ToArray();
            string[] expectedFrameworks = rule.FrameworkReferences.Order(StringComparer.Ordinal).ToArray();
            CollectionAssert.AreEqual(
                expectedFrameworks,
                actualFrameworks,
                $"Unexpected framework reference in {rule.Name}.");
            graph.Add(rule.Name, actual);
        }

        AssertNoPath(graph, "IptvSuite.Domain", "IptvSuite.Application");
        AssertNoPath(graph, "IptvSuite.Domain", "IptvSuite.Infrastructure");
        AssertNoPath(graph, "IptvSuite.Domain", "IptvSuite.Windows");
        AssertNoPath(graph, "IptvSuite.Application", "IptvSuite.Infrastructure");
        AssertNoPath(graph, "IptvSuite.Application", "IptvSuite.Windows");
        AssertNoPath(graph, "IptvSuite.Infrastructure", "IptvSuite.Windows");
        AssertNoPath(graph, "IptvSuite.Windows", "IptvSuite.PackageLifecycleHarness");
    }

    [TestMethod]
    public void PackageReferencesAreMinimalAndCentrallyVersioned()
    {
        foreach (ProjectRule rule in ProjectRules)
        {
            XDocument project = LoadXml(rule.RelativePath);
            XElement[] references = project.Descendants()
                .Where(element => element.Name.LocalName == "PackageReference")
                .ToArray();
            string[] actual = references
                .Select(GetRequiredInclude)
                .Order(StringComparer.Ordinal)
                .ToArray();
            string[] expected = rule.PackageReferences.Order(StringComparer.Ordinal).ToArray();

            CollectionAssert.AreEqual(expected, actual, $"Unexpected package reference in {rule.Name}.");
            Assert.IsFalse(
                references.Any(reference =>
                    reference.Attribute("Version") is not null ||
                    reference.Elements().Any(element => element.Name.LocalName == "Version")),
                $"{rule.Name} must use central package versions.");
        }
    }

    [TestMethod]
    public void CentralPackagesAreExactStableVersions()
    {
        XDocument centralPackages = LoadXml("Directory.Packages.props");
        Dictionary<string, string> actual = centralPackages.Descendants()
            .Where(element => element.Name.LocalName == "PackageVersion")
            .ToDictionary(
                GetRequiredInclude,
                element => element.Attribute("Version")?.Value ?? throw new InvalidDataException("PackageVersion requires Version."),
                StringComparer.Ordinal);
        Dictionary<string, string> expected = new(StringComparer.Ordinal)
        {
            ["Microsoft.WindowsAppSDK"] = "2.3.1",
            ["Microsoft.Windows.SDK.BuildTools"] = "10.0.26100.8249",
            ["Microsoft.Extensions.TimeProvider.Testing"] = "10.8.0",
            ["MSTest"] = "4.3.3",
            ["System.Security.Cryptography.ProtectedData"] = "10.0.10",
        };

        CollectionAssert.AreEquivalent(expected.Keys.ToArray(), actual.Keys.ToArray());

        foreach ((string packageName, string expectedVersion) in expected)
        {
            Assert.AreEqual(expectedVersion, actual[packageName], $"Unexpected version for {packageName}.");
            Assert.IsFalse(actual[packageName].Contains('-', StringComparison.Ordinal), $"{packageName} must be stable.");
        }
    }

    [TestMethod]
    public void ToolchainAndBuildDefaultsArePinned()
    {
        string globalJsonPath = Path.Combine(RepositoryRoot, "global.json");
        using JsonDocument globalJson = JsonDocument.Parse(File.ReadAllText(globalJsonPath));
        JsonElement sdk = globalJson.RootElement.GetProperty("sdk");

        Assert.AreEqual("10.0.302", sdk.GetProperty("version").GetString());
        Assert.AreEqual("disable", sdk.GetProperty("rollForward").GetString());
        Assert.IsFalse(sdk.GetProperty("allowPrerelease").GetBoolean());

        XDocument buildProperties = LoadXml("Directory.Build.props");
        Dictionary<string, string> expectedBuildProperties = new(StringComparer.Ordinal)
        {
            ["LangVersion"] = "14.0",
            ["Nullable"] = "enable",
            ["TreatWarningsAsErrors"] = "true",
            ["EnforceCodeStyleInBuild"] = "true",
            ["Deterministic"] = "true",
            ["RestorePackagesWithLockFile"] = "true",
            ["RestoreUseStaticGraphEvaluation"] = "true",
            ["DisableImplicitNuGetFallbackFolder"] = "true",
        };

        foreach ((string propertyName, string expectedValue) in expectedBuildProperties)
        {
            Assert.AreEqual(expectedValue, GetProperty(buildProperties, propertyName), $"Unexpected {propertyName} value.");
        }

        XDocument solutionProperties = LoadXml("Directory.Solution.props");
        Assert.AreEqual("true", GetProperty(solutionProperties, "RestoreUseStaticGraphEvaluation"));
    }

    [TestMethod]
    public void WinUiAndMsixSettingsStayInApprovedPackagedProjects()
    {
        foreach (ProjectRule rule in ProjectRules)
        {
            XDocument project = LoadXml(rule.RelativePath);
            string? useWinUi = GetProperty(project, "UseWinUI");
            string? enableMsixTooling = GetProperty(project, "EnableMsixTooling");

            if (rule.Name is "IptvSuite.Windows" or "IptvSuite.PackageLifecycleHarness")
            {
                Assert.AreEqual("true", useWinUi, ignoreCase: true);
                Assert.AreEqual("true", enableMsixTooling, ignoreCase: true);
                Assert.AreEqual("x64", GetProperty(project, "Platforms"));
                Assert.AreEqual("win-x64", GetProperty(project, "RuntimeIdentifier"));
            }
            else
            {
                Assert.IsNull(useWinUi, $"WinUI leaked into {rule.Name}.");
                Assert.IsNull(enableMsixTooling, $"MSIX tooling leaked into {rule.Name}.");
            }
        }

        using JsonDocument launchSettings = LoadJson(
            "apps/windows/src/IptvSuite.Windows/Properties/launchSettings.json");
        JsonProperty[] profiles = launchSettings.RootElement
            .GetProperty("profiles")
            .EnumerateObject()
            .ToArray();

        Assert.HasCount(1, profiles);
        Assert.AreEqual("IptvSuite.Windows (Package)", profiles[0].Name);
        Assert.HasCount(3, profiles[0].Value.EnumerateObject().ToArray());
        Assert.AreEqual("MsixPackage", profiles[0].Value.GetProperty("commandName").GetString());
        Assert.IsFalse(profiles[0].Value.GetProperty("alwaysReinstallApp").GetBoolean());
        Assert.IsFalse(profiles[0].Value.GetProperty("nativeDebugging").GetBoolean());

        string solution = File.ReadAllText(
            Path.Combine(RepositoryRoot, "apps", "windows", "IptvSuite.Windows.sln"));
        const string windowsProjectGuid = "{1D606C2E-0328-4C4C-9DFE-383651FC0CD1}";
        const string lifecycleHarnessProjectGuid = "{9F66D0D7-C578-4A79-BF47-4D5D8E0FB460}";

        foreach (string configuration in new[] { "Debug", "Release" })
        {
            StringAssert.Contains(
                solution,
                $"{windowsProjectGuid}.{configuration}|x64.Deploy.0 = {configuration}|x64");
            StringAssert.Contains(
                solution,
                $"{lifecycleHarnessProjectGuid}.{configuration}|x64.Build.0 = {configuration}|x64");
            Assert.IsFalse(
                solution.Contains(
                    $"{lifecycleHarnessProjectGuid}.{configuration}|x64.Deploy.0",
                    StringComparison.Ordinal),
                "The test-only lifecycle harness must never deploy as part of a solution build.");
        }
    }

    [TestMethod]
    public void ManifestHasDisposableIdentityAndOnlyRequiredCapability()
    {
        XDocument manifest = LoadXml("apps/windows/src/IptvSuite.Windows/Package.appxmanifest");
        XElement identity = manifest.Descendants().Single(element => element.Name.LocalName == "Identity");
        string[] capabilities = manifest.Descendants()
            .Where(element => element.Name.LocalName == "Capability")
            .Select(element => element.Attribute("Name")?.Value ?? string.Empty)
            .Order(StringComparer.Ordinal)
            .ToArray();
        XElement[] targetFamilies = manifest.Descendants()
            .Where(element => element.Name.LocalName == "TargetDeviceFamily")
            .ToArray();

        Assert.AreEqual(DevelopmentIdentity, identity.Attribute("Name")?.Value);
        Assert.AreEqual(DevelopmentPublisher, identity.Attribute("Publisher")?.Value);
        CollectionAssert.AreEqual(RequiredCapabilities, capabilities);
        Assert.HasCount(1, targetFamilies);
        Assert.AreEqual("Windows.Desktop", targetFamilies[0].Attribute("Name")?.Value);
        Assert.AreEqual("10.0.26100.0", targetFamilies[0].Attribute("MinVersion")?.Value);
        Assert.AreEqual("10.0.26100.0", targetFamilies[0].Attribute("MaxVersionTested")?.Value);

        string appsRoot = Path.Combine(RepositoryRoot, "apps");
        Assert.IsFalse(
            Directory.EnumerateFiles(appsRoot, "Package.StoreAssociation.xml", SearchOption.AllDirectories).Any(),
            "Store association is forbidden for the disposable M1 identity.");
    }

    [TestMethod]
    public void PackageLifecycleHarnessIsIsolatedNonPublishableTestInfrastructure()
    {
        XDocument project = LoadXml(
            "apps/windows/tests/IptvSuite.PackageLifecycleHarness/IptvSuite.PackageLifecycleHarness.csproj");
        Assert.AreEqual("WinExe", GetProperty(project, "OutputType"));
        Assert.AreEqual("false", GetProperty(project, "IsTestProject"));
        Assert.AreEqual("false", GetProperty(project, "IsPackable"));
        Assert.AreEqual("false", GetProperty(project, "IsPublishable"));
        Assert.AreEqual("x64", GetProperty(project, "Platforms"));
        Assert.AreEqual("x64", GetProperty(project, "PlatformTarget"));
        Assert.AreEqual("win-x64", GetProperty(project, "RuntimeIdentifier"));
        Assert.AreEqual("true", GetProperty(project, "UseWinUI"), ignoreCase: true);
        Assert.AreEqual("true", GetProperty(project, "EnableMsixTooling"), ignoreCase: true);
        Assert.AreEqual("Baseline", GetProperty(project, "LifecyclePackageFlavor"));

        XElement[] manifestSelections = project.Descendants()
            .Where(element => element.Name.LocalName == "AppxManifest")
            .ToArray();
        Assert.HasCount(2, manifestSelections);
        Assert.IsTrue(manifestSelections.Any(element =>
            element.Attribute("Include")?.Value == "Package.appxmanifest" &&
            element.Parent?.Attribute("Condition")?.Value ==
                "'$(LifecyclePackageFlavor)' == 'Baseline'"));
        Assert.IsTrue(manifestSelections.Any(element =>
            element.Attribute("Include")?.Value == "Package.Update.appxmanifest" &&
            element.Parent?.Attribute("Condition")?.Value ==
                "'$(LifecyclePackageFlavor)' == 'Update'"));

        XElement flavorValidation = project.Descendants()
            .Single(element =>
                element.Name.LocalName == "Target" &&
                element.Attribute("Name")?.Value == "ValidateLifecyclePackageFlavor");
        Assert.AreEqual("PrepareForBuild", flavorValidation.Attribute("BeforeTargets")?.Value);
        StringAssert.Contains(
            flavorValidation.Elements().Single(element => element.Name.LocalName == "Error")
                .Attribute("Condition")?.Value ?? string.Empty,
            "'$(LifecyclePackageFlavor)' != 'Baseline' and '$(LifecyclePackageFlavor)' != 'Update'");

        XDocument manifest = LoadXml(
            "apps/windows/tests/IptvSuite.PackageLifecycleHarness/Package.appxmanifest");
        XDocument updateManifest = LoadXml(
            "apps/windows/tests/IptvSuite.PackageLifecycleHarness/Package.Update.appxmanifest");
        XElement identity = manifest.Descendants().Single(element => element.Name.LocalName == "Identity");
        XElement updateIdentity = updateManifest.Descendants()
            .Single(element => element.Name.LocalName == "Identity");
        XElement[] applications = manifest.Descendants()
            .Where(element => element.Name.LocalName == "Application")
            .ToArray();
        string[] capabilities = manifest.Descendants()
            .Where(element => element.Name.LocalName == "Capability")
            .Select(element => element.Attribute("Name")?.Value ?? string.Empty)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.AreEqual(LifecycleHarnessIdentity, identity.Attribute("Name")?.Value);
        Assert.AreEqual(LifecycleHarnessPublisher, identity.Attribute("Publisher")?.Value);
        Assert.AreEqual("0.0.1.0", identity.Attribute("Version")?.Value);
        Assert.AreEqual(LifecycleHarnessIdentity, updateIdentity.Attribute("Name")?.Value);
        Assert.AreEqual(LifecycleHarnessPublisher, updateIdentity.Attribute("Publisher")?.Value);
        Assert.AreEqual("0.0.2.0", updateIdentity.Attribute("Version")?.Value);
        Assert.AreNotEqual(DevelopmentIdentity, identity.Attribute("Name")?.Value);
        Assert.AreNotEqual(DevelopmentPublisher, identity.Attribute("Publisher")?.Value);
        updateIdentity.SetAttributeValue("Version", "0.0.1.0");
        Assert.IsTrue(
            XNode.DeepEquals(manifest.Root!, updateManifest.Root!),
            "The disposable package manifests may differ only by their exact package version.");
        CollectionAssert.AreEqual(RequiredCapabilities, capabilities);
        Assert.HasCount(1, applications);
        Assert.AreEqual("Harness", applications[0].Attribute("Id")?.Value);

        XElement visualElements = applications[0].Elements()
            .Single(element => element.Name.LocalName == "VisualElements");
        Assert.AreEqual("none", visualElements.Attribute("AppListEntry")?.Value);

        string applicationSource = File.ReadAllText(
            Path.Combine(
                RepositoryRoot,
                "apps",
                "windows",
                "tests",
                "IptvSuite.PackageLifecycleHarness",
                "App.xaml.cs"));
        StringAssert.Contains(applicationSource, "AppInstance.GetCurrent().GetActivatedEventArgs()");
        StringAssert.Contains(applicationSource, "ExtendedActivationKind.Launch");
        StringAssert.Contains(applicationSource, "ILaunchActivatedEventArgs");
        Assert.IsFalse(
            applicationSource.Contains("args.Arguments", StringComparison.Ordinal),
            "WinUI desktop LaunchActivatedEventArgs.Arguments is always empty and cannot carry harness arguments.");
    }

    [TestMethod]
    public void WinUiWindowIconDoesNotDependOnTheProcessWorkingDirectory()
    {
        string mainWindow = File.ReadAllText(Path.Combine(
            RepositoryRoot,
            "apps",
            "windows",
            "src",
            "IptvSuite.Windows",
            "MainWindow.xaml.cs"));

        StringAssert.Contains(
            mainWindow,
            "Path.Combine(AppContext.BaseDirectory, \"Assets\", \"AppIcon.ico\")");
        Assert.IsFalse(
            mainWindow.Contains("SetIcon(\"Assets", StringComparison.Ordinal),
            "Packaged startup must not resolve the window icon from the process working directory.");
    }

    [TestMethod]
    public void WinUiCompositionRetainsThePackagedSecretStoreWithoutAStorageFallback()
    {
        string windowsRoot = Path.Combine(
            RepositoryRoot,
            "apps",
            "windows",
            "src",
            "IptvSuite.Windows");
        string app = File.ReadAllText(Path.Combine(windowsRoot, "App.xaml.cs"))
            .Replace("\r\n", "\n", StringComparison.Ordinal);
        string factory = File.ReadAllText(Path.Combine(windowsRoot, "WindowsSecretStoreFactory.cs"))
            .Replace("\r\n", "\n", StringComparison.Ordinal);
        string protectedEnvelopeCodec = File.ReadAllText(Path.Combine(
            RepositoryRoot,
            "apps",
            "windows",
            "src",
            "IptvSuite.Infrastructure",
            "DpapiProtectedEnvelopeCodec.cs"));

        const string compositionSequence =
            "SecretStoreInitializationResult secretStoreInitialization =\n" +
            "            WindowsSecretStoreFactory.Create();\n" +
            "        ISecretStore secretStore = secretStoreInitialization.Store ??\n" +
            "            throw new InvalidOperationException(\"Protected storage is unavailable.\");\n" +
            "        _secretStore = secretStore;\n" +
            "        _window = new MainWindow();";
        const string neutralProtectedStorePath =
            "Path.Combine(\n" +
            "                localCachePath,\n" +
            "                \"ProtectedStore\",\n" +
            "                \"v2\")";

        StringAssert.Contains(app, "private ISecretStore? _secretStore;");
        StringAssert.Contains(app, compositionSequence);
        Assert.AreEqual(
            1,
            Regex.Count(app, @"\bWindowsSecretStoreFactory\.Create\("),
            "The packaged secret-store factory must run exactly once during application launch.");
        Assert.IsFalse(
            app.Contains("new DpapiCurrentUserSecretStore", StringComparison.Ordinal) ||
            app.Contains("InMemorySecretStore", StringComparison.Ordinal),
            "The composition root must not create a fallback secret store.");
        StringAssert.Contains(factory, neutralProtectedStorePath);
        Assert.IsFalse(
            factory.Contains("localCachePath,\n                \"IptvSuite\"", StringComparison.Ordinal),
            "The unverified codename must not become part of the persisted protected-store path.");
        StringAssert.Contains(protectedEnvelopeCodec, "\"SRCSEC02\"u8");
        StringAssert.Contains(
            protectedEnvelopeCodec,
            "\"protected-source-store/dpapi-current-user/entropy/v2\"u8");
        StringAssert.Contains(
            protectedEnvelopeCodec,
            "\"protected-source-store/dpapi-current-user/file-name/v2\"u8");
        Assert.IsFalse(
            Regex.IsMatch(
                protectedEnvelopeCodec,
                "\"[^\"]*iptv[^\"]*\"u8",
                RegexOptions.IgnoreCase),
            "The unverified codename must not become part of the durable protected-record format.");
    }

    [TestMethod]
    public void TestInfrastructureCannotLeakIntoProduction()
    {
        string solution = File.ReadAllText(Path.Combine(RepositoryRoot, "apps", "windows", "IptvSuite.Windows.sln"));
        foreach (ProjectRule rule in ProjectRules)
        {
            const string solutionRoot = "apps/windows/";
            Assert.IsTrue(rule.RelativePath.StartsWith(solutionRoot, StringComparison.Ordinal));
            string solutionRelativePath = rule.RelativePath[solutionRoot.Length..].Replace('/', '\\');
            StringAssert.Contains(solution, solutionRelativePath);
        }

        string sourceRoot = Path.Combine(RepositoryRoot, "apps", "windows", "src");
        string[] sourceFiles = Directory.EnumerateFiles(sourceRoot, "*", SearchOption.AllDirectories)
            .Where(path => Path.GetExtension(path) is ".cs" or ".csproj" or ".xaml" or ".xml" or ".manifest")
            .ToArray();

        foreach (string sourceFile in sourceFiles)
        {
            string content = File.ReadAllText(sourceFile);
            Assert.IsFalse(
                content.Contains("IptvSuite.Testing", StringComparison.Ordinal) ||
                content.Contains("IptvSuite.UnitTests", StringComparison.Ordinal) ||
                content.Contains("IptvSuite.IntegrationTests", StringComparison.Ordinal) ||
                content.Contains("IptvSuite.SecretStoreSpike", StringComparison.Ordinal) ||
                content.Contains("IptvSuite.PackageLifecycleHarness", StringComparison.Ordinal) ||
                content.Contains("Microsoft.Extensions.TimeProvider.Testing", StringComparison.Ordinal) ||
                content.Contains("Microsoft.AspNetCore.App", StringComparison.Ordinal) ||
                content.Contains("IPTVSUITE_TEST_ONLY_CANARY_V1", StringComparison.Ordinal),
                $"Test infrastructure leaked into production source: {Path.GetRelativePath(RepositoryRoot, sourceFile)}");
        }

        XDocument testingProject = LoadXml("apps/windows/tests/IptvSuite.Testing/IptvSuite.Testing.csproj");
        Assert.AreEqual("false", GetProperty(testingProject, "IsPackable"));
        Assert.AreEqual("false", GetProperty(testingProject, "IsPublishable"));

        XDocument spikeProject = LoadXml(
            "apps/windows/tests/IptvSuite.SecretStoreSpike/IptvSuite.SecretStoreSpike.csproj");
        Assert.AreEqual("Exe", GetProperty(spikeProject, "OutputType"));
        Assert.AreEqual("false", GetProperty(spikeProject, "IsTestProject"));
        Assert.AreEqual("false", GetProperty(spikeProject, "IsPackable"));
        Assert.AreEqual("false", GetProperty(spikeProject, "IsPublishable"));
        Assert.AreEqual("x64", GetProperty(spikeProject, "Platforms"));
        Assert.AreEqual("x64", GetProperty(spikeProject, "PlatformTarget"));

        string packageSmoke = File.ReadAllText(
            Path.Combine(RepositoryRoot, "eng", "Invoke-WindowsPackageSmoke.ps1"));
        StringAssert.Contains(packageSmoke, "IptvSuite\\.SecretStoreSpike(?:\\..*)?");
        StringAssert.Contains(packageSmoke, "IptvSuite\\.PackageLifecycleHarness(?:\\..*)?");
        StringAssert.Contains(packageSmoke, "PackagedApplicationActivator]::Activate($aumid)");
        StringAssert.Contains(packageSmoke, "CoCreateInstance");
        StringAssert.Contains(packageSmoke, "LocalServer = 0x00000004");
        StringAssert.Contains(packageSmoke, "-Name \"EnableLUA\"");
        StringAssert.Contains(packageSmoke, "$launchedProcess.Refresh()");
        StringAssert.Contains(packageSmoke, "$null = $launchedProcess.Handle");
        StringAssert.Contains(packageSmoke, "IsWindowVisible($windowHandle)");
        StringAssert.Contains(packageSmoke, "GetWindowThreadProcessId");
        StringAssert.Contains(packageSmoke, "Start-Sleep -Seconds 2");
        StringAssert.Contains(packageSmoke, "$null -eq $exitCode");
        StringAssert.Contains(packageSmoke, "[int]$exitCode -ne 0");
        StringAssert.Contains(packageSmoke, "LocalCache\\ProtectedStore\\v2");
        StringAssert.Contains(
            packageSmoke,
            "ProtectedStoreDirectoryInitialized = $protectedStoreDirectoryInitialized");
        StringAssert.Contains(
            packageSmoke,
            "The packaged protected-store directory could not be inspected safely.");
        Assert.IsFalse(
            packageSmoke.Contains("Start-Process -FilePath \"explorer.exe\"", StringComparison.Ordinal),
            "The package smoke must retain the exact PID returned by the official activation API.");
        Assert.IsFalse(
            packageSmoke.Contains("remains after uninstall: $appDataPath", StringComparison.Ordinal),
            "Package-smoke diagnostics must not disclose the current user's app-data path.");

        string lifecycleSmoke = File.ReadAllText(
            Path.Combine(RepositoryRoot, "eng", "Invoke-WindowsPackageLifecycleSmoke.ps1"));
        StringAssert.Contains(lifecycleSmoke, LifecycleHarnessIdentity);
        StringAssert.Contains(lifecycleSmoke, LifecycleHarnessPublisher);
        StringAssert.Contains(lifecycleSmoke, "$baselineVersion = \"0.0.1.0\"");
        StringAssert.Contains(lifecycleSmoke, "$updatedVersion = \"0.0.2.0\"");
        StringAssert.Contains(lifecycleSmoke, "\"-p:AppxPackageDir=$baselinePackageOutput\"");
        StringAssert.Contains(lifecycleSmoke, "\"-p:AppxPackageDir=$updatedPackageOutput\"");
        StringAssert.Contains(lifecycleSmoke, "-p:LifecyclePackageFlavor=Baseline");
        StringAssert.Contains(lifecycleSmoke, "-p:LifecyclePackageFlavor=Update");
        StringAssert.Contains(lifecycleSmoke, "-Path $updatedArtifacts.Package.FullName");
        StringAssert.Contains(lifecycleSmoke, "ProtectedRecordReadAfterPackageUpdate");
        StringAssert.Contains(lifecycleSmoke, "PostUpdateOwnedSurfaceCanaryScanPassed = $true");
        StringAssert.Contains(lifecycleSmoke, "SamePackageFamily = $true");
        StringAssert.Contains(lifecycleSmoke, "PackageFullNameChanged = $true");
        Assert.HasCount(
            2,
            Regex.Matches(lifecycleSmoke, @"(?<![A-Za-z0-9])-t:Rebuild(?![A-Za-z0-9])"),
            "Both package versions require isolated rebuilds to prevent a stale generated manifest.");
        Assert.IsFalse(
            lifecycleSmoke.Contains("ForceUpdateFromAnyVersion", StringComparison.Ordinal),
            "The forward package-update proof must use the normal higher-version deployment path.");
        Assert.IsFalse(
            Regex.IsMatch(lifecycleSmoke, @"(?m)(?:^|\s)-Update(?:\s|`|$)"),
            "-Update is for dependency-package updates and must not drive the primary package update.");
        StringAssert.Contains(lifecycleSmoke, "CoCreateInstance");
        StringAssert.Contains(lifecycleSmoke, "LocalServer = 0x00000004");
        StringAssert.Contains(lifecycleSmoke, "--phase $Phase --run-id $runId");
        StringAssert.Contains(lifecycleSmoke, "PACKAGE_LIFECYCLE_CREATE");
        StringAssert.Contains(
            lifecycleSmoke,
            "foreach ($scanRoot in @($script:protectedStorePath, $script:runDirectory))");
        StringAssert.Contains(
            lifecycleSmoke,
            "scan-artifacts $scanRoot M4 PACKAGE_LIFECYCLE_CREATE");
        Assert.HasCount(
            1,
            Regex.Matches(lifecycleSmoke, @"\bscan-artifacts\b"),
            "The lifecycle smoke must scan only the two owned roots through one exact invocation.");
        Assert.IsFalse(
            lifecycleSmoke.Contains(
                "scan-artifacts $script:appDataPath M4 PACKAGE_LIFECYCLE_CREATE",
                StringComparison.Ordinal),
            "The lifecycle scanner must avoid mutable OS-managed package hive files.");
        StringAssert.Contains(lifecycleSmoke, "CanaryScannerOperationalFailure");
        StringAssert.Contains(lifecycleSmoke, "CanaryArtifactDetected");
        StringAssert.Contains(lifecycleSmoke, "CanaryScannerContractFailure");
        StringAssert.Contains(lifecycleSmoke, "InitialOwnedSurfaceCanaryScanPassed = $true");
        StringAssert.Contains(lifecycleSmoke, "FinalOwnedSurfaceCanaryScanPassed = $true");
        Assert.IsFalse(
            lifecycleSmoke.Contains("AppDataCanaryScanPassed", StringComparison.Ordinal),
            "Lifecycle evidence must not overstate an owned-surface scan as whole AppData coverage.");
        StringAssert.Contains(lifecycleSmoke, "record-v2-[0-9A-F]{64}\\.dpapi");
        StringAssert.Contains(lifecycleSmoke, "AppxSymbolPackageEnabled      = \"false\"");
        StringAssert.Contains(lifecycleSmoke, "$artifactsRoot = Join-Path $repositoryRoot \".artifacts\"");
        StringAssert.Contains(lifecycleSmoke, "$artifactRoot = Join-Path $artifactsRoot \"package-lifecycle\"");
        StringAssert.Contains(lifecycleSmoke, "last-success.json");
        StringAssert.Contains(lifecycleSmoke, "last-failure.json");
        Assert.IsFalse(
            lifecycleSmoke.Contains("Exception.Message", StringComparison.Ordinal),
            "Lifecycle evidence and diagnostics must use stable codes instead of raw exception messages.");
    }

    [TestMethod]
    public void DomainRemainsPureAndMvpScoped()
    {
        string domainRoot = Path.Combine(RepositoryRoot, "apps", "windows", "src", "IptvSuite.Domain");
        string[] sourceFiles = Directory.EnumerateFiles(domainRoot, "*.cs", SearchOption.AllDirectories)
            .Where(path => !IsBuildOutputPath(domainRoot, path))
            .ToArray();
        string combinedSource = string.Join('\n', sourceFiles.Select(File.ReadAllText));
        string[] forbiddenRuntimeSymbols =
        [
            "System.IO",
            "System.Net.Http",
            "HttpClient",
            "HttpRequestMessage",
            "WebRequest",
            "System.Net.Sockets",
            "Microsoft.Data.Sqlite",
            "ProtectedData",
            "PasswordVault",
            "LocalCache",
            "Microsoft.UI",
            "Windows.Storage",
            "ISecretStore",
            "IPlayer",
        ];

        foreach (string forbiddenSymbol in forbiddenRuntimeSymbols)
        {
            Assert.IsFalse(
                combinedSource.Contains(forbiddenSymbol, StringComparison.Ordinal),
                $"M3 Domain must not contain runtime/infrastructure symbol {forbiddenSymbol}.");
        }

        string[] futureTypes = ["Movie", "Series", "Season", "Episode", "EpgProgramme"];
        foreach (string futureType in futureTypes)
        {
            Assert.IsFalse(
                Regex.IsMatch(combinedSource, $@"\b(?:class|record|struct)\s+{futureType}\b"),
                $"Future type {futureType} must not be added before its milestone.");
        }
    }

    [TestMethod]
    public void PersistentFormatIdentifiersDoNotFreezeTheUnverifiedCodename()
    {
        string sourceRoot = Path.Combine(RepositoryRoot, "apps", "windows", "src");
        string[] forbiddenPersistentIdentifiers =
        [
            "\"IPTVSUITE-",
            "\"IPTVSEC",
            "\"IPTVCRED",
            "\"IPTVLOCR",
            "\"iptv-suite/",
        ];

        foreach (string sourceFile in Directory.EnumerateFiles(sourceRoot, "*.cs", SearchOption.AllDirectories))
        {
            string content = File.ReadAllText(sourceFile);
            foreach (string forbiddenIdentifier in forbiddenPersistentIdentifiers)
            {
                Assert.IsFalse(
                    content.Contains(forbiddenIdentifier, StringComparison.Ordinal),
                    $"Unverified codename reached a persistent format identifier: " +
                    $"{Path.GetRelativePath(RepositoryRoot, sourceFile)}");
            }
        }
    }

    [TestMethod]
    public void CiWorkflowIsLeastPrivilegePinnedAndAlwaysTriggered()
    {
        string workflowPath = Path.Combine(RepositoryRoot, ".github", "workflows", "windows-quality.yml");
        string workflow = File.ReadAllText(workflowPath).Replace("\r\n", "\n", StringComparison.Ordinal);

        StringAssert.Contains(workflow, "  pull_request:\n");
        StringAssert.Contains(workflow, "on:\n  merge_group:\n  pull_request:\n");
        StringAssert.Contains(workflow, "push:\n    branches:\n      - main\n");
        StringAssert.Contains(workflow, "workflow_dispatch:");
        StringAssert.Contains(workflow, "permissions:\n  contents: read\n");
        StringAssert.Contains(workflow, "runs-on: windows-2025-vs2026");
        StringAssert.Contains(workflow, "persist-credentials: false");
        StringAssert.Contains(workflow, "dotnet-version: \"10.0.302\"");
        StringAssert.Contains(workflow, "shell: powershell\n        run: .\\eng\\Invoke-WindowsPackageSmoke.ps1");
        StringAssert.Contains(
            workflow,
            "shell: powershell\n        run: .\\eng\\Invoke-WindowsPackageLifecycleSmoke.ps1");
        StringAssert.Contains(workflow, "name: windows-package-lifecycle-evidence");
        StringAssert.Contains(workflow, ".artifacts/package-lifecycle/last-success.json");
        StringAssert.Contains(
            workflow,
            "scan-artifacts .\\.artifacts\\package-lifecycle M4 PACKAGE_LIFECYCLE_EVIDENCE");
        StringAssert.Contains(workflow, "name: Required Windows gate");
        StringAssert.Contains(workflow, "if: ${{ always() }}");
        StringAssert.Contains(workflow, "scan-artifacts .\\.artifacts\\msix-smoke CI PACKAGE_EVIDENCE");
        StringAssert.Contains(workflow, "fixtures/LICENSES/LicenseRef-IPTVSuite-Synthetic-Test-Only.txt");
        Assert.IsFalse(workflow.Contains("test-results/**/*.trx", StringComparison.Ordinal));

        Assert.IsFalse(workflow.Contains("pull_request_target", StringComparison.Ordinal));
        Assert.IsFalse(workflow.Contains("secrets.", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(workflow.Contains("continue-on-error", StringComparison.Ordinal));
        Assert.IsFalse(
            Regex.IsMatch(workflow, @"(?m)^\s+paths(?:-ignore)?:"),
            "A required workflow must not be skipped by top-level path filters.");

        MatchCollection allUses = Regex.Matches(workflow, @"(?m)^\s*uses:\s*");
        MatchCollection pinnedUses = Regex.Matches(
            workflow,
            @"(?m)^\s*uses:\s*[^@\s]+@[0-9a-f]{40}(?:\s+#.*)?$");
        Assert.HasCount(7, allUses);
        Assert.AreEqual(allUses.Count, pinnedUses.Count, "Every action must use a full commit SHA.");
    }

    private static XDocument LoadXml(string relativePath)
    {
        string path = Path.Combine(RepositoryRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));
        return XDocument.Load(path, LoadOptions.PreserveWhitespace);
    }

    private static JsonDocument LoadJson(string relativePath)
    {
        string path = Path.Combine(RepositoryRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));
        return JsonDocument.Parse(File.ReadAllText(path));
    }

    private static bool IsBuildOutputPath(string projectRoot, string path)
    {
        string relativePath = Path.GetRelativePath(projectRoot, path);
        int separatorIndex = relativePath.IndexOfAny(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        ReadOnlySpan<char> firstSegment = separatorIndex < 0
            ? relativePath.AsSpan()
            : relativePath.AsSpan(0, separatorIndex);
        return firstSegment.SequenceEqual("bin") || firstSegment.SequenceEqual("obj");
    }

    private static string[] GetIncludes(XDocument project, string itemName)
    {
        return project.Descendants()
            .Where(element => element.Name.LocalName == itemName)
            .Select(GetRequiredInclude)
            .ToArray();
    }

    private static string GetRequiredInclude(XElement element)
    {
        return element.Attribute("Include")?.Value
            ?? throw new InvalidDataException($"{element.Name.LocalName} requires Include.");
    }

    private static string? GetProperty(XDocument project, string propertyName)
    {
        return project.Descendants()
            .LastOrDefault(element => element.Name.LocalName == propertyName)
            ?.Value;
    }

    private static void AssertNoPath(
        Dictionary<string, string[]> graph,
        string source,
        string forbiddenTarget)
    {
        HashSet<string> visited = new(StringComparer.Ordinal);
        Queue<string> pending = new(graph[source]);

        while (pending.TryDequeue(out string? current))
        {
            if (!visited.Add(current))
            {
                continue;
            }

            Assert.AreNotEqual(
                forbiddenTarget,
                current,
                $"Forbidden dependency path: {source} -> {forbiddenTarget}.");

            if (graph.TryGetValue(current, out string[]? next))
            {
                foreach (string project in next)
                {
                    pending.Enqueue(project);
                }
            }
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

    private sealed record ProjectRule(
        string Name,
        string RelativePath,
        string[] ProjectReferences,
        string[] FrameworkReferences,
        string[] PackageReferences);
}
