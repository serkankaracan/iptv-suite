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
    private static readonly int[] ProtectedCatalogSmokeRecordCounts = [1_000];
    private static readonly int[] ProtectedCatalogDecisionRecordCounts = [5_000, 10_000, 20_000, 50_000];

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
            "IptvSuite.ProtectedCatalogSpike",
            "apps/windows/tests/IptvSuite.ProtectedCatalogSpike/IptvSuite.ProtectedCatalogSpike.csproj",
            ["IptvSuite.Testing"],
            [],
            ["System.Security.Cryptography.ProtectedData"]),
        new(
            "IptvSuite.PackageLifecycleHarness",
            "apps/windows/tests/IptvSuite.PackageLifecycleHarness/IptvSuite.PackageLifecycleHarness.csproj",
            ["IptvSuite.Application", "IptvSuite.Domain", "IptvSuite.Infrastructure"],
            [],
            ["Microsoft.Windows.SDK.BuildTools", "Microsoft.WindowsAppSDK"]),
        new(
            "IptvSuite.DpapiUserBoundaryHarness",
            "apps/windows/tests/IptvSuite.DpapiUserBoundaryHarness/IptvSuite.DpapiUserBoundaryHarness.csproj",
            ["IptvSuite.Application", "IptvSuite.Domain", "IptvSuite.Infrastructure", "IptvSuite.Testing"],
            [],
            ["System.Security.Cryptography.ProtectedData"]),
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
        AssertNoPath(graph, "IptvSuite.Windows", "IptvSuite.ProtectedCatalogSpike");
        AssertNoPath(graph, "IptvSuite.Windows", "IptvSuite.PackageLifecycleHarness");
        AssertNoPath(graph, "IptvSuite.Windows", "IptvSuite.DpapiUserBoundaryHarness");
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
        const string protectedCatalogSpikeProjectGuid = "{E7CD0B28-6FCC-4E20-86AF-7D7BD4FC7E6E}";
        const string lifecycleHarnessProjectGuid = "{9F66D0D7-C578-4A79-BF47-4D5D8E0FB460}";
        const string dpapiUserBoundaryHarnessProjectGuid = "{9E610CDF-2461-4D7B-A289-84B41BB4F55A}";
        const string testsFolderGuid = "{0AB3BF05-4346-4AA6-1389-037BE0695223}";

        StringAssert.Contains(
            solution,
            $"{protectedCatalogSpikeProjectGuid} = {testsFolderGuid}");
        StringAssert.Contains(
            solution,
            $"{dpapiUserBoundaryHarnessProjectGuid} = {testsFolderGuid}");

        foreach (string configuration in new[] { "Debug", "Release" })
        {
            StringAssert.Contains(
                solution,
                $"{windowsProjectGuid}.{configuration}|x64.Deploy.0 = {configuration}|x64");
            StringAssert.Contains(
                solution,
                $"{lifecycleHarnessProjectGuid}.{configuration}|x64.Build.0 = {configuration}|x64");
            StringAssert.Contains(
                solution,
                $"{protectedCatalogSpikeProjectGuid}.{configuration}|x64.Build.0 = {configuration}|x64");
            StringAssert.Contains(
                solution,
                $"{dpapiUserBoundaryHarnessProjectGuid}.{configuration}|x64.Build.0 = {configuration}|x64");
            Assert.IsFalse(
                solution.Contains(
                    $"{protectedCatalogSpikeProjectGuid}.{configuration}|x64.Deploy.0",
                    StringComparison.Ordinal),
                "The test-only protected-catalog spike must never deploy as part of a solution build.");
            Assert.IsFalse(
                solution.Contains(
                    $"{lifecycleHarnessProjectGuid}.{configuration}|x64.Deploy.0",
                    StringComparison.Ordinal),
                "The test-only lifecycle harness must never deploy as part of a solution build.");
            Assert.IsFalse(
                solution.Contains(
                    $"{dpapiUserBoundaryHarnessProjectGuid}.{configuration}|x64.Deploy.0",
                    StringComparison.Ordinal),
                "The test-only DPAPI user-boundary harness must never deploy as part of a solution build.");
        }
    }

    [TestMethod]
    public void ManifestHasDisposableIdentityAndOnlyRequiredCapability()
    {
        XDocument manifest = LoadXml("apps/windows/src/IptvSuite.Windows/Package.appxmanifest");
        XDocument applicationManifest = LoadXml("apps/windows/src/IptvSuite.Windows/app.manifest");
        XElement identity = manifest.Descendants().Single(element => element.Name.LocalName == "Identity");
        XElement requestedExecutionLevel = applicationManifest.Descendants()
            .Single(element => element.Name.LocalName == "requestedExecutionLevel");
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
        Assert.AreEqual("asInvoker", requestedExecutionLevel.Attribute("level")?.Value);
        Assert.AreEqual("false", requestedExecutionLevel.Attribute("uiAccess")?.Value);
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
    public void DpapiUserBoundaryHarnessIsIsolatedNonPublishableTestInfrastructure()
    {
        XDocument project = LoadXml(
            "apps/windows/tests/IptvSuite.DpapiUserBoundaryHarness/IptvSuite.DpapiUserBoundaryHarness.csproj");

        Assert.AreEqual("Exe", GetProperty(project, "OutputType"));
        Assert.AreEqual("net10.0", GetProperty(project, "TargetFramework"));
        Assert.AreEqual("false", GetProperty(project, "IsTestProject"));
        Assert.AreEqual("false", GetProperty(project, "IsPackable"));
        Assert.AreEqual("false", GetProperty(project, "IsPublishable"));
        Assert.AreEqual("x64", GetProperty(project, "Platforms"));
        Assert.AreEqual("x64", GetProperty(project, "PlatformTarget"));
        Assert.AreEqual("false", GetProperty(project, "Prefer32Bit"));
        Assert.AreEqual("false", GetProperty(project, "SelfContained"));
        Assert.AreEqual("true", GetProperty(project, "UseAppHost"));
        Assert.AreEqual(
            "IptvSuite.DpapiUserBoundaryHarness",
            GetProperty(project, "RootNamespace"));
        Assert.AreEqual(
            "IptvSuite.DpapiUserBoundaryHarness",
            GetProperty(project, "AssemblyName"));
        Assert.IsNull(GetProperty(project, "UseWinUI"));
        Assert.IsNull(GetProperty(project, "EnableMsixTooling"));
        Assert.IsNull(GetProperty(project, "RuntimeIdentifier"));
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
    public void ProtectedCatalogSpikeRequiresExplicitIsolatedExecutionAndFixedWorkload()
    {
        string wrapper = File.ReadAllText(
            Path.Combine(RepositoryRoot, "eng", "Invoke-WindowsProtectedCatalogSpike.ps1"));
        string qualityGate = File.ReadAllText(
            Path.Combine(RepositoryRoot, "eng", "Invoke-WindowsQualityGate.ps1"));
        string workflow = File.ReadAllText(
            Path.Combine(RepositoryRoot, ".github", "workflows", "windows-quality.yml"));
        string invocation = File.ReadAllText(Path.Combine(
            RepositoryRoot,
            "apps",
            "windows",
            "tests",
            "IptvSuite.ProtectedCatalogSpike",
            "SpikeInvocation.cs"));
        string environmentEvidence = File.ReadAllText(Path.Combine(
            RepositoryRoot,
            "apps",
            "windows",
            "tests",
            "IptvSuite.ProtectedCatalogSpike",
            "SpikeEnvironmentEvidence.cs"));
        string runner = File.ReadAllText(Path.Combine(
            RepositoryRoot,
            "apps",
            "windows",
            "tests",
            "IptvSuite.ProtectedCatalogSpike",
            "ProtectedCatalogSpikeRunner.cs"));
        string safeWorkspace = File.ReadAllText(Path.Combine(
            RepositoryRoot,
            "apps",
            "windows",
            "tests",
            "IptvSuite.ProtectedCatalogSpike",
            "SafeSpikeWorkspace.cs"));
        string store = File.ReadAllText(Path.Combine(
            RepositoryRoot,
            "apps",
            "windows",
            "tests",
            "IptvSuite.ProtectedCatalogSpike",
            "ProtectedCatalogStore.cs"));
        string spikeEvidence = File.ReadAllText(Path.Combine(
            RepositoryRoot,
            "apps",
            "windows",
            "tests",
            "IptvSuite.ProtectedCatalogSpike",
            "SpikeEvidence.cs"));

        StringAssert.Contains(wrapper, "[ValidateSet(\"Smoke\", \"Decision\")]");
        StringAssert.Contains(wrapper, "[switch]$AllowDecision");
        StringAssert.Contains(wrapper, "if ($Mode -eq \"Decision\" -and -not $AllowDecision)");
        StringAssert.Contains(wrapper, "--acknowledge-long-running-decision");
        Assert.AreEqual(
            1,
            Regex.Count(wrapper, "--acknowledge-long-running-decision"),
            "The protected-catalog runner acknowledgement must only be added by the guarded Decision branch.");
        StringAssert.Contains(
            wrapper,
            "apps\\windows\\tests\\IptvSuite.ProtectedCatalogSpike\\IptvSuite.ProtectedCatalogSpike.csproj");
        StringAssert.Contains(
            wrapper,
            "apps\\windows\\tests\\IptvSuite.ProtectedCatalogSpike\\bin\\x64\\Release\\net10.0\\" +
            "IptvSuite.ProtectedCatalogSpike.dll");
        StringAssert.Contains(wrapper, "IPTVSUITE_PROTECTED_CATALOG_SPIKE_VALIDATED_SDK");
        StringAssert.Contains(wrapper, "IPTVSUITE_PROTECTED_CATALOG_SPIKE_RUNNER_ASSEMBLY_SHA256");
        Assert.IsFalse(wrapper.Contains("IPTVSUITE_SPIKE_VALIDATED_SDK", StringComparison.Ordinal));
        Assert.IsFalse(wrapper.Contains("IPTVSUITE_SPIKE_RUNNER_ASSEMBLY_SHA256", StringComparison.Ordinal));
        StringAssert.Contains(wrapper, "$env:DOTNET_CLI_USE_MSBUILD_SERVER = \"0\"");
        StringAssert.Contains(wrapper, "$env:MSBUILDDISABLENODEREUSE = \"1\"");
        StringAssert.Contains(wrapper, "$maximumBuildNodes = 1");
        StringAssert.Contains(wrapper, "\"--locked-mode\"");
        StringAssert.Contains(wrapper, "\"--disable-parallel\"");
        StringAssert.Contains(wrapper, "\"-c\", \"Release\"");
        StringAssert.Contains(wrapper, "\"-p:Platform=x64\"");
        StringAssert.Contains(wrapper, "\"-maxcpucount:$maximumBuildNodes\"");
        StringAssert.Contains(wrapper, "\"-p:UseSharedCompilation=false\"");
        StringAssert.Contains(wrapper, "\"--no-incremental\"");
        StringAssert.Contains(
            invocation,
            "arguments is [\"--mode\", string decision, DecisionAcknowledgement]");
        StringAssert.Contains(
            environmentEvidence,
            "bool decisionEligible = !isDirty && OperatingSystem.IsWindows() && Environment.Is64BitProcess;");
        StringAssert.Contains(environmentEvidence, "mode is SpikeMode.Decision && !decisionEligible");
        StringAssert.Contains(
            environmentEvidence,
            "[\"status\", \"--porcelain=v1\", \"--untracked-files=normal\"]");
        StringAssert.Contains(environmentEvidence, "AssertRepositoryStateUnchangedAsync");
        StringAssert.Contains(environmentEvidence, "dirty || !initial.DecisionEligible");
        StringAssert.Contains(environmentEvidence, "string PackageLockSha256,");
        StringAssert.Contains(environmentEvidence, "string TestingAssemblySha256,");
        StringAssert.Contains(environmentEvidence, "string RunnerDepsJsonSha256,");
        StringAssert.Contains(
            environmentEvidence,
            "ComputeSha256Async(workspace.PackageLockPath, cancellationToken)");
        StringAssert.Contains(
            environmentEvidence,
            "Path.Combine(assemblyDirectory, \"IptvSuite.Testing.dll\")");
        StringAssert.Contains(
            environmentEvidence,
            "Path.Combine(assemblyDirectory, \"IptvSuite.ProtectedCatalogSpike.deps.json\")");
        StringAssert.Contains(
            environmentEvidence,
            "if (!File.Exists(testingAssemblyPath) || !File.Exists(runnerDepsPath))");
        StringAssert.Contains(
            environmentEvidence,
            "ComputeSha256Async(testingAssemblyPath, cancellationToken)");
        StringAssert.Contains(
            environmentEvidence,
            "ComputeSha256Async(runnerDepsPath, cancellationToken)");
        StringAssert.Contains(
            runner,
            "await SpikeEnvironmentEvidenceCollector.AssertRepositoryStateUnchangedAsync(");
        StringAssert.Contains(safeWorkspace, "GetContainedPath(_artifactsRoot, \"m4-protected-catalog-spike\")");
        StringAssert.Contains(safeWorkspace, "\"IptvSuite.ProtectedCatalogSpike\",");
        StringAssert.Contains(safeWorkspace, "\"packages.lock.json\"));");
        Assert.IsFalse(safeWorkspace.Contains("m4-secret-store-spike", StringComparison.Ordinal));
        StringAssert.Contains(store, "internal const uint AeadAlgorithmId = 1;");
        StringAssert.Contains(store, "internal const uint KeyWrapAlgorithmId = 1;");
        StringAssert.Contains(store, "internal const string EntropyContext =");
        StringAssert.Contains(store, "BinaryPrimitives.WriteUInt32BigEndian");
        StringAssert.Contains(store, "BinaryPrimitives.ReadUInt32BigEndian");
        StringAssert.Contains(store, "bigEndian: true");
        StringAssert.Contains(store, "int validationProbeCount = Math.Min(recordCount, 16);");
        StringAssert.Contains(
            store,
            "using (ProtectedCatalogReader stagedReader = ProtectedCatalogReader.Open(_stagedPath, binding))");
        StringAssert.Contains(store, "if (nextOffset != stream.Length)");
        Assert.IsFalse(
            store.Contains("footer", StringComparison.OrdinalIgnoreCase),
            "The bounded candidate format must remain footerless; exact file length closes the structure.");
        StringAssert.Contains(spikeEvidence, "string ByteOrder,");
        StringAssert.Contains(spikeEvidence, "string WorkloadCommit,");
        StringAssert.Contains(spikeEvidence, "string SpecificationSha256,");
        StringAssert.Contains(spikeEvidence, "string RunnerAssemblySha256,");
        StringAssert.Contains(spikeEvidence, "string DecisionSummarySha256,");
        StringAssert.Contains(spikeEvidence, "string DecisionWorkloadSha256,");
        StringAssert.Contains(spikeEvidence, "string EvidenceRecordCommit);");
        StringAssert.Contains(spikeEvidence, "PhaseEvidence AdapterReopenAndUnwrap,");
        StringAssert.Contains(spikeEvidence, "int DeleteRecordsCoveredPerSample,");
        StringAssert.Contains(spikeEvidence, "int PreActivationTagProbeCount,");
        StringAssert.Contains(spikeEvidence, "internal sealed record StagingCancellationEvidence(");
        StringAssert.Contains(spikeEvidence, "StagingCancellationEvidence StagingCancellation,");
        StringAssert.Contains(spikeEvidence, "bool DuplicateNonceFailedClosed,");
        StringAssert.Contains(spikeEvidence, "bool IndexTupleAuthenticationFailedClosed,");
        StringAssert.Contains(spikeEvidence, "bool CrossContainerWrappedDekSwapFailedClosed,");
        StringAssert.Contains(spikeEvidence, "bool TrailingBytesFailedClosed,");
        StringAssert.Contains(spikeEvidence, "bool InjectedNonceCollisionRetryPassed,");
        StringAssert.Contains(spikeEvidence, "\"smoke-summary.json\" : \"decision-summary.json\"");
        StringAssert.Contains(runner, "CandidateId: \"immutable-protected-catalog-container-v1\"");
        StringAssert.Contains(runner, "specification.BaselineEvidence.WorkloadCommit");
        StringAssert.Contains(runner, "specification.BaselineEvidence.SpecificationSha256");
        StringAssert.Contains(runner, "specification.BaselineEvidence.RunnerAssemblySha256");
        StringAssert.Contains(runner, "specification.BaselineEvidence.DecisionSummarySha256");
        StringAssert.Contains(runner, "specification.BaselineEvidence.DecisionWorkloadSha256");
        StringAssert.Contains(runner, "specification.BaselineEvidence.EvidenceRecordCommit");
        StringAssert.Contains(runner, "StagingCancellation: stagingCancellation");
        StringAssert.Contains(runner, "\"big-endian\"");
        StringAssert.Contains(runner, "\"AES-256-GCM-algorithm-id-1\"");
        StringAssert.Contains(runner, "\"DPAPI-CurrentUser-key-wrap-id-1\"");
        StringAssert.Contains(
            runner,
            "\"fresh-rng-256-bit-dek-and-key-generation-id-per-staging-attempt\"");
        StringAssert.Contains(
            runner,
            "\"strict-structural-reopen-dpapi-unwrap-and-up-to-16-evenly-spaced-tag-probes\"");
        StringAssert.Contains(runner, "\"controlled-fault-only-not-power-loss-durability\"");

        Assert.IsFalse(
            qualityGate.Contains("Invoke-WindowsProtectedCatalogSpike.ps1", StringComparison.Ordinal),
            "The normal quality gate must never invoke the opt-in protected-catalog spike.");
        Assert.IsFalse(
            workflow.Contains("Invoke-WindowsProtectedCatalogSpike.ps1", StringComparison.Ordinal),
            "The required hosted workflow must never invoke the opt-in protected-catalog spike.");

        using JsonDocument specification = LoadJson(
            "apps/windows/testdata/m4/protected-catalog-spike-spec.json");
        JsonElement root = specification.RootElement;
        JsonElement baselineEvidence = root.GetProperty("baselineEvidence");
        JsonElement format = root.GetProperty("format");
        JsonElement smoke = root.GetProperty("smoke");
        JsonElement decision = root.GetProperty("decision");

        Assert.AreEqual(1, root.GetProperty("schemaVersion").GetInt32());
        Assert.AreEqual("m4-protected-catalog-spike-v1", root.GetProperty("fixtureSetId").GetString());
        Assert.AreEqual(
            "IptvSuite.ProtectedCatalogSpike.DeterministicPayloadGenerator",
            root.GetProperty("generatorName").GetString());
        Assert.AreEqual(
            "IptvSuite.SecretStoreSpike.DeterministicPayloadGenerator",
            root.GetProperty("baselineGeneratorName").GetString());
        Assert.AreEqual(
            "fc96a211171d1e4f5e5f02174da6c565ef2d59bb",
            baselineEvidence.GetProperty("workloadCommit").GetString());
        Assert.AreEqual(
            "0447355215f8c744340a39640e55bc798916638b48e5386b213e7d3f06c7a568",
            baselineEvidence.GetProperty("specificationSha256").GetString());
        Assert.AreEqual(
            "3df0676151a906f815bd0881994ffd3f7f347f2f7121a494409f85afcdeca119",
            baselineEvidence.GetProperty("runnerAssemblySha256").GetString());
        Assert.AreEqual(
            "8cd4c6d86b813fd07794217a71a824e7368694363f89a16be36cb8a311d67460",
            baselineEvidence.GetProperty("decisionSummarySha256").GetString());
        Assert.AreEqual(
            "eb6a4eaaecf437e80ef01feb00c6d1453e41994682a76ed08f81c1808a372f3f",
            baselineEvidence.GetProperty("decisionWorkloadSha256").GetString());
        Assert.AreEqual(
            "207455a54d2d7ac9b6b5c1ce8eb5e29bbee0c383",
            baselineEvidence.GetProperty("evidenceRecordCommit").GetString());
        Assert.AreEqual(1, root.GetProperty("algorithmVersion").GetInt32());
        Assert.AreEqual(20260813, root.GetProperty("seed").GetInt32());
        Assert.AreEqual(256, root.GetProperty("payloadByteLength").GetInt32());
        Assert.AreEqual(1, format.GetProperty("version").GetInt32());
        Assert.AreEqual(50_000, format.GetProperty("maximumRecordsPerDek").GetInt32());
        Assert.AreEqual(32, format.GetProperty("dekByteLength").GetInt32());
        Assert.AreEqual(12, format.GetProperty("nonceByteLength").GetInt32());
        Assert.AreEqual(16, format.GetProperty("tagByteLength").GetInt32());
        Assert.AreEqual(256, format.GetProperty("readProbeCount").GetInt32());
        Assert.AreEqual("CurrentUser", format.GetProperty("dpapiScope").GetString());
        Assert.AreEqual(
            "protected-catalog-spike/v1/current-user/dek",
            format.GetProperty("dpapiEntropyContext").GetString());

        CollectionAssert.AreEqual(
            ProtectedCatalogSmokeRecordCounts,
            smoke.GetProperty("recordCounts").EnumerateArray().Select(value => value.GetInt32()).ToArray());
        Assert.AreEqual(1, smoke.GetProperty("iterations").GetInt32());
        Assert.AreEqual(1, smoke.GetProperty("cancellationSamples").GetInt32());
        Assert.AreEqual(
            "d330726e2e886b1d61585c3fc276c6d5f20a1dfad85561749230ba35e99a40af",
            smoke.GetProperty("expectedWorkloadSha256").GetString());
        CollectionAssert.AreEqual(
            ProtectedCatalogDecisionRecordCounts,
            decision.GetProperty("recordCounts").EnumerateArray().Select(value => value.GetInt32()).ToArray());
        Assert.AreEqual(20, decision.GetProperty("iterations").GetInt32());
        Assert.AreEqual(20, decision.GetProperty("cancellationSamples").GetInt32());
        Dictionary<string, string?> decisionScaleHashes = decision
            .GetProperty("expectedScaleWorkloadSha256")
            .EnumerateObject()
            .ToDictionary(property => property.Name, property => property.Value.GetString(), StringComparer.Ordinal);
        Dictionary<string, string?> expectedDecisionScaleHashes = new(StringComparer.Ordinal)
        {
            ["5000"] = "80f110a11351dd95b3489f0a8973cc826f096334da0b4363e1d4b24e98082fe1",
            ["10000"] = "c4084013d6205597e412d47ec65329b8d671b9e0edb551a6d29c54cf34cd1512",
            ["20000"] = "94bb81ddc7d2afe6fc4b2935dd9d2dec5f1bf8e80b5444cf90e8e860b9512c86",
            ["50000"] = "88b5fad60d89e2fb6c16e9dac1a3372abb0779cdd216424833555b8f906ab232",
        };
        CollectionAssert.AreEquivalent(
            expectedDecisionScaleHashes.ToArray(),
            decisionScaleHashes.ToArray(),
            "The candidate Decision workload must remain byte-for-byte comparable with the baseline.");
        Assert.AreEqual(
            "eb6a4eaaecf437e80ef01feb00c6d1453e41994682a76ed08f81c1808a372f3f",
            decision.GetProperty("expectedWorkloadSha256").GetString());
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
                content.Contains("IptvSuite.ProtectedCatalogSpike", StringComparison.Ordinal) ||
                content.Contains("IptvSuite.PackageLifecycleHarness", StringComparison.Ordinal) ||
                content.Contains("IptvSuite.DpapiUserBoundaryHarness", StringComparison.Ordinal) ||
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

        XDocument protectedCatalogSpikeProject = LoadXml(
            "apps/windows/tests/IptvSuite.ProtectedCatalogSpike/IptvSuite.ProtectedCatalogSpike.csproj");
        Assert.AreEqual("Exe", GetProperty(protectedCatalogSpikeProject, "OutputType"));
        Assert.AreEqual("false", GetProperty(protectedCatalogSpikeProject, "IsTestProject"));
        Assert.AreEqual("false", GetProperty(protectedCatalogSpikeProject, "IsPackable"));
        Assert.AreEqual("false", GetProperty(protectedCatalogSpikeProject, "IsPublishable"));
        Assert.AreEqual("x64", GetProperty(protectedCatalogSpikeProject, "Platforms"));
        Assert.AreEqual("x64", GetProperty(protectedCatalogSpikeProject, "PlatformTarget"));
        Assert.AreEqual("false", GetProperty(protectedCatalogSpikeProject, "Prefer32Bit"));

        string packageSmoke = File.ReadAllText(
            Path.Combine(RepositoryRoot, "eng", "Invoke-WindowsPackageSmoke.ps1"));
        StringAssert.Contains(packageSmoke, "IptvSuite\\.SecretStoreSpike(?:\\..*)?");
        StringAssert.Contains(
            packageSmoke,
            "$entry.Name -match '^(?i:IptvSuite\\.ProtectedCatalogSpike(?:\\..*)?)$'");
        StringAssert.Contains(packageSmoke, "IptvSuite\\.PackageLifecycleHarness(?:\\..*)?");
        StringAssert.Contains(
            packageSmoke,
            "$entry.Name -match '^(?i:IptvSuite\\.DpapiUserBoundaryHarness(?:\\..*)?)$'");
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
        StringAssert.Contains(lifecycleSmoke, "\"-p:AppxPackageDir=$baselinePackageOutput/\"");
        StringAssert.Contains(lifecycleSmoke, "\"-p:AppxPackageDir=$updatedPackageOutput/\"");
        StringAssert.Contains(lifecycleSmoke, "-p:LifecyclePackageFlavor=Baseline");
        StringAssert.Contains(lifecycleSmoke, "-p:LifecyclePackageFlavor=Update");
        StringAssert.Contains(lifecycleSmoke, "-Path $updatedArtifacts.Package.FullName");
        StringAssert.Contains(lifecycleSmoke, "ProtectedRecordReadAfterPackageUpdate");
        StringAssert.Contains(lifecycleSmoke, "PostUpdateOwnedSurfaceCanaryScanPassed = $true");
        StringAssert.Contains(lifecycleSmoke, "SamePackageFamily = $true");
        StringAssert.Contains(lifecycleSmoke, "PackageFullNameChanged = $true");
        StringAssert.Contains(lifecycleSmoke, "SchemaVersion = 3");
        StringAssert.Contains(
            lifecycleSmoke,
            "Invoke-ExactPackageReset -ExpectedPackageFullName $updatedPackageFullName");
        StringAssert.Contains(lifecycleSmoke, "PackageReset = $true");
        StringAssert.Contains(lifecycleSmoke, "PackageIdentityPreservedAfterReset = $true");
        StringAssert.Contains(lifecycleSmoke, "ResetOwnedStateRemoved = $true");
        StringAssert.Contains(
            lifecycleSmoke,
            "FreshCreateAfterReset = [bool]$postResetResult.CreateCommitted");
        StringAssert.Contains(lifecycleSmoke, "ResetRecordIdentityChanged = $true");
        StringAssert.Contains(
            lifecycleSmoke,
            "if ($postResetRecordLeaf -eq $preResetRecordLeaf)");
        StringAssert.Contains(lifecycleSmoke, "PackageUninstalledWithOwnedState = $true");
        StringAssert.Contains(lifecycleSmoke, "UninstallAppDataRemoved = $true");
        StringAssert.Contains(lifecycleSmoke, "PackageReinstalled = $true");
        StringAssert.Contains(lifecycleSmoke, "PackageIdentityPreservedAfterReinstall = $true");
        StringAssert.Contains(
            lifecycleSmoke,
            "FreshCreateAfterReinstall = [bool]$postReinstallResult.CreateCommitted");
        StringAssert.Contains(lifecycleSmoke, "ReinstallRecordIdentityChanged = $true");
        StringAssert.Contains(
            lifecycleSmoke,
            "if ($postReinstallRecordLeaf -eq $postResetRecordLeaf)");
        Assert.IsFalse(
            lifecycleSmoke.Contains("PreserveApplicationData", StringComparison.Ordinal) ||
            lifecycleSmoke.Contains("PreserveRoamableApplicationData", StringComparison.Ordinal) ||
            lifecycleSmoke.Contains("-AllUsers", StringComparison.Ordinal),
            "The current-user fresh-state proof must not preserve app data or broaden package removal.");

        int packageReset = lifecycleSmoke.IndexOf(
            "Set-FailurePoint -Stage \"PackageResetInvocation\"",
            StringComparison.Ordinal);
        int packageResetRegistration = lifecycleSmoke.IndexOf(
            "Set-FailurePoint -Stage \"PackageResetRegistrationValidation\"",
            packageReset,
            StringComparison.Ordinal);
        int packageResetManifest = lifecycleSmoke.IndexOf(
            "Set-FailurePoint -Stage \"PackageResetManifestValidation\"",
            packageResetRegistration,
            StringComparison.Ordinal);
        int packageResetState = lifecycleSmoke.IndexOf(
            "Set-FailurePoint -Stage \"ResetStateValidation\"",
            packageResetManifest,
            StringComparison.Ordinal);
        int postResetCreate = lifecycleSmoke.IndexOf(
            "Set-FailurePoint -Stage \"PostResetCreateLaunch\"",
            StringComparison.Ordinal);
        int liveStateRemoval = lifecycleSmoke.IndexOf(
            "Set-FailurePoint -Stage \"LiveStatePackageRemoval\"",
            StringComparison.Ordinal);
        int packageReinstall = lifecycleSmoke.IndexOf(
            "Set-FailurePoint -Stage \"PackageReinstall\"",
            StringComparison.Ordinal);
        int postReinstallCreate = lifecycleSmoke.IndexOf(
            "Set-FailurePoint -Stage \"PostReinstallCreateLaunch\"",
            StringComparison.Ordinal);
        Assert.IsTrue(
            packageReset >= 0 &&
            packageResetRegistration > packageReset &&
            packageResetManifest > packageResetRegistration &&
            packageResetState > packageResetManifest &&
            postResetCreate > packageResetState &&
            liveStateRemoval > postResetCreate &&
            packageReinstall > liveStateRemoval &&
            postReinstallCreate > packageReinstall,
            "The lifecycle fresh-state proof must validate reset invocation, registration, manifest, state, uninstall, and reinstall in order.");

        string resetProof = lifecycleSmoke[packageReset..postResetCreate];
        StringAssert.Contains(
            resetProof,
            "Invoke-ExactPackageReset -ExpectedPackageFullName $updatedPackageFullName");
        StringAssert.Contains(
            resetProof,
            "Wait-ExactLifecyclePackageRegistration `");
        StringAssert.Contains(
            lifecycleSmoke[packageResetManifest..packageResetState],
            "Assert-ManifestPolicy -Manifest $resetInstalledManifest -ExpectedVersion $updatedVersion -Built");
        StringAssert.Contains(
            lifecycleSmoke[packageResetState..postResetCreate],
            "Assert-OwnedLifecycleStateAbsent");
        StringAssert.Contains(resetProof, "Assert-OwnedLifecycleStateAbsent");
        Assert.IsFalse(
            resetProof.Contains("Remove-ExactAppData", StringComparison.Ordinal) ||
            resetProof.Contains("Remove-Item", StringComparison.Ordinal),
            "Reset state must be inspected before any manual app-data cleanup.");

        string liveStateProof = lifecycleSmoke[postResetCreate..liveStateRemoval];
        StringAssert.Contains(liveStateProof, "$postResetRecordLeaf = Get-ExactProtectedRecordLeaf");
        StringAssert.Contains(liveStateProof, "Assert-RegularFile -Path $ticketPath");
        Assert.IsFalse(
            liveStateProof.Contains("verify-delete", StringComparison.Ordinal) ||
            liveStateProof.Contains("Assert-ProtectedStoreClean", StringComparison.Ordinal) ||
            liveStateProof.Contains("Remove-ExactAppData", StringComparison.Ordinal) ||
            liveStateProof.Contains("Remove-Item", StringComparison.Ordinal),
            "The uninstall proof must begin while the post-reset protected record and ticket are live.");

        string uninstallProof = lifecycleSmoke[liveStateRemoval..packageReinstall];
        StringAssert.Contains(uninstallProof, "Remove-ExactLifecyclePackage");
        StringAssert.Contains(uninstallProof, "Assert-ExactAppDataAbsent");
        Assert.IsFalse(
            uninstallProof.Contains("Remove-ExactAppData", StringComparison.Ordinal) ||
            uninstallProof.Contains("Remove-Item", StringComparison.Ordinal),
            "Uninstall state must be inspected before any manual app-data cleanup.");

        int ownedStateHelperStart = lifecycleSmoke.IndexOf(
            "function Assert-OwnedLifecycleStateAbsent",
            StringComparison.Ordinal);
        int appDataAbsenceHelperStart = lifecycleSmoke.IndexOf(
            "function Assert-ExactAppDataAbsent",
            ownedStateHelperStart,
            StringComparison.Ordinal);
        int deploymentFailureClassifierStart = lifecycleSmoke.IndexOf(
            "function Get-AppxDeploymentFailureClass",
            appDataAbsenceHelperStart,
            StringComparison.Ordinal);
        int processQuiescenceHelperStart = lifecycleSmoke.IndexOf(
            "function Wait-ExactHarnessProcessQuiescence",
            deploymentFailureClassifierStart,
            StringComparison.Ordinal);
        int packageResetHelperStart = lifecycleSmoke.IndexOf(
            "function Invoke-ExactPackageReset",
            processQuiescenceHelperStart,
            StringComparison.Ordinal);
        int packageRegistrationHelperStart = lifecycleSmoke.IndexOf(
            "function Wait-ExactLifecyclePackageRegistration",
            packageResetHelperStart,
            StringComparison.Ordinal);
        int ownedCanaryScanHelperStart = lifecycleSmoke.IndexOf(
            "function Invoke-OwnedCanaryScan",
            packageRegistrationHelperStart,
            StringComparison.Ordinal);
        Assert.IsTrue(
            ownedStateHelperStart >= 0 &&
            appDataAbsenceHelperStart > ownedStateHelperStart &&
            deploymentFailureClassifierStart > appDataAbsenceHelperStart &&
            processQuiescenceHelperStart > deploymentFailureClassifierStart &&
            packageResetHelperStart > processQuiescenceHelperStart &&
            packageRegistrationHelperStart > packageResetHelperStart &&
            ownedCanaryScanHelperStart > packageRegistrationHelperStart);

        string ownedStateHelper = lifecycleSmoke[ownedStateHelperStart..appDataAbsenceHelperStart];
        string appDataAbsenceHelper = lifecycleSmoke[appDataAbsenceHelperStart..deploymentFailureClassifierStart];
        foreach (string observationHelper in new[] { ownedStateHelper, appDataAbsenceHelper })
        {
            Assert.IsFalse(
                observationHelper.Contains("Remove-Item", StringComparison.Ordinal) ||
                observationHelper.Contains("Remove-ExactAppData", StringComparison.Ordinal) ||
                observationHelper.Contains("Remove-AppxPackage", StringComparison.Ordinal) ||
                observationHelper.Contains("Reset-AppxPackage", StringComparison.Ordinal),
                "Lifecycle state observation helpers must never mutate package registration or app data.");
        }

        string deploymentFailureClassifier =
            lifecycleSmoke[deploymentFailureClassifierStart..processQuiescenceHelperStart];
        string processQuiescenceHelper =
            lifecycleSmoke[processQuiescenceHelperStart..packageResetHelperStart];
        string packageResetHelper =
            lifecycleSmoke[packageResetHelperStart..packageRegistrationHelperStart];
        string packageRegistrationHelper =
            lifecycleSmoke[packageRegistrationHelperStart..ownedCanaryScanHelperStart];

        StringAssert.Contains(
            deploymentFailureClassifier,
            "([int64]$current.HResult -band 0xFFFFFFFFL)");
        Assert.IsFalse(
            deploymentFailureClassifier.Contains("[int64]0xFFFFFFFF", StringComparison.Ordinal),
            "PowerShell 5.1 must parse the HRESULT mask as an unsigned long literal.");
        StringAssert.Contains(deploymentFailureClassifier, "$depth -lt 8");
        string[] expectedDeploymentHResults =
        [
            "0x80004001",
            "0x80070032",
            "0x80073D00",
            "0x80073D01",
            "0x80073D02",
            "0x80073D05",
            "0x80073D1D",
            "0x80073D23",
            "0x80073CF1",
            "0x80073CF9",
            "0x80073CFE",
            "0x80070005",
        ];
        string[] actualDeploymentHResults = Regex.Matches(
                deploymentFailureClassifier,
                @"0x(?!FFFFFFFF)[0-9A-F]{8}")
            .Select(match => match.Value)
            .ToArray();
        CollectionAssert.AreEqual(
            expectedDeploymentHResults,
            actualDeploymentHResults,
            "The reset classifier must expose only the reviewed HRESULT allowlist.");
        StringAssert.Contains(
            deploymentFailureClassifier,
            "\"0x80004001\" { return \"NotImplemented\" }");
        StringAssert.Contains(
            deploymentFailureClassifier,
            "\"0x80070032\" { return \"NotSupported\" }");
        StringAssert.Contains(
            deploymentFailureClassifier,
            "\"0x80073D1D\" { return \"DeploymentOptionNotSupported\" }");
        StringAssert.Contains(
            deploymentFailureClassifier,
            "\"0x80073D23\" { return \"DeploymentBlockedByProfilePolicy\" }");
        StringAssert.Contains(
            deploymentFailureClassifier,
            "\"0x80073D00\" { return \"PackageUpdating\" }");
        StringAssert.Contains(
            deploymentFailureClassifier,
            "\"0x80073D02\" { return \"PackagesInUse\" }");
        StringAssert.Contains(
            deploymentFailureClassifier,
            "\"0x80073D05\" { return \"ApplicationDataDeleteFailed\" }");

        StringAssert.Contains(processQuiescenceHelper, "(Get-Date).AddSeconds(5)");
        StringAssert.Contains(
            processQuiescenceHelper,
            "[System.Diagnostics.Process]::GetProcessesByName($expectedProcessName)");
        StringAssert.Contains(processQuiescenceHelper, "$consecutiveAbsentObservations -ge 3");
        StringAssert.Contains(processQuiescenceHelper, "$harnessProcess.Dispose()");
        StringAssert.Contains(processQuiescenceHelper, "Start-Sleep -Milliseconds 250");
        Assert.IsFalse(
            processQuiescenceHelper.Contains("Get-Process", StringComparison.Ordinal) ||
            processQuiescenceHelper.Contains("Stop-Process", StringComparison.Ordinal) ||
            processQuiescenceHelper.Contains(".Kill(", StringComparison.Ordinal),
            "Reset quiescence must observe the exact harness process without broad lookup or termination.");

        StringAssert.Contains(packageResetHelper, "$maximumAttempts = 3");
        StringAssert.Contains(
            packageResetHelper,
            "for ($attempt = 1; $attempt -le $maximumAttempts; $attempt++)");
        StringAssert.Contains(packageResetHelper, "Wait-ExactHarnessProcessQuiescence");
        StringAssert.Contains(
            packageResetHelper,
            "Reset-AppxPackage -Package $ExpectedPackageFullName -Confirm:$false -ErrorAction Stop");
        Assert.HasCount(
            1,
            Regex.Matches(packageResetHelper, @"\bReset-AppxPackage\b"),
            "The bounded retry loop must have one exact reset invocation site.");
        int quiescenceCall = packageResetHelper.IndexOf(
            "Wait-ExactHarnessProcessQuiescence",
            StringComparison.Ordinal);
        int resetInvocation = packageResetHelper.IndexOf(
            "Reset-AppxPackage -Package $ExpectedPackageFullName -Confirm:$false -ErrorAction Stop",
            StringComparison.Ordinal);
        Assert.IsTrue(
            quiescenceCall >= 0 && resetInvocation > quiescenceCall,
            "Every reset attempt must first establish exact process quiescence.");
        int retryPolicyStart = packageResetHelper.IndexOf("$retryable = $failureClass -in @(", StringComparison.Ordinal);
        int retryPolicyEnd = packageResetHelper.IndexOf("if (-not $retryable", retryPolicyStart, StringComparison.Ordinal);
        Assert.IsTrue(retryPolicyStart >= 0 && retryPolicyEnd > retryPolicyStart);
        string[] retryableFailureClasses = Regex.Matches(
                packageResetHelper[retryPolicyStart..retryPolicyEnd],
                "\"([A-Za-z]+)\"")
            .Select(match => match.Groups[1].Value)
            .ToArray();
        string[] expectedRetryableFailureClasses =
        [
            "PackageUpdating",
            "PackagesInUse",
            "ApplicationDataDeleteFailed",
        ];
        CollectionAssert.AreEqual(
            expectedRetryableFailureClasses,
            retryableFailureClasses,
            "Only 0x80073D00, 0x80073D02, and 0x80073D05 may be retried.");
        StringAssert.Contains(
            packageResetHelper,
            "if (-not $retryable -or $attempt -eq $maximumAttempts)");
        StringAssert.Contains(
            packageResetHelper,
            "$retryDelayMilliseconds = if ($attempt -eq 1) { 500 } else { 1500 }");

        foreach (string forbiddenResetDiagnostic in new[]
                 {
                     ".Message",
                     "Get-AppxLog",
                     "Get-AppxLastError",
                     "EventLog",
                     "Get-WinEvent",
                     "InvocationInfo",
                     "ScriptStackTrace",
                     "TargetObject",
                     "InstallLocation",
                     "Write-Host",
                     "Write-Output",
                     "Write-Error",
                     "Write-Warning",
                     "Write-Verbose",
                     "Write-Information",
                     "Remove-Item",
                     "Remove-ExactAppData",
                     "Remove-AppxPackage",
                 })
        {
            Assert.IsFalse(
                deploymentFailureClassifier.Contains(forbiddenResetDiagnostic, StringComparison.Ordinal) ||
                packageResetHelper.Contains(forbiddenResetDiagnostic, StringComparison.Ordinal),
                $"Reset classification and invocation must not disclose diagnostics or mutate adjacent state: {forbiddenResetDiagnostic}.");
        }

        StringAssert.Contains(
            packageRegistrationHelper,
            "$candidate.PackageFullName -ne $ExpectedPackageFullName");
        StringAssert.Contains(
            packageRegistrationHelper,
            "$candidate.PackageFamilyName -ne $ExpectedPackageFamilyName");
        StringAssert.Contains(
            packageRegistrationHelper,
            "$candidate.Version.ToString() -ne $ExpectedVersion");
        StringAssert.Contains(packageRegistrationHelper, "(Get-Date).AddSeconds(15)");
        StringAssert.Contains(packageRegistrationHelper, "Start-Sleep -Milliseconds 250");
        StringAssert.Contains(packageRegistrationHelper, "$packages.Count -gt 1");
        StringAssert.Contains(packageRegistrationHelper, "$candidate.Status.ToString() -eq \"Ok\"");

        int removePackageHelperStart = lifecycleSmoke.IndexOf(
            "function Remove-ExactLifecyclePackage",
            StringComparison.Ordinal);
        int removeAppDataHelperStart = lifecycleSmoke.IndexOf(
            "function Remove-ExactAppData",
            removePackageHelperStart,
            StringComparison.Ordinal);
        Assert.IsTrue(removePackageHelperStart >= 0 && removeAppDataHelperStart > removePackageHelperStart);
        string removePackageHelper = lifecycleSmoke[removePackageHelperStart..removeAppDataHelperStart];
        StringAssert.Contains(
            removePackageHelper,
            "Remove-AppxPackage -Package $packages[0].PackageFullName -ErrorAction Stop");
        StringAssert.Contains(
            removePackageHelper,
            "$packages[0].PackageFullName -ne $ExpectedPackageFullName");
        StringAssert.Contains(
            removePackageHelper,
            "(-not [string]::IsNullOrWhiteSpace($ExpectedPackageFullName) -and $packages.Count -ne 1)");
        Assert.IsFalse(
            removePackageHelper.Contains("Remove-ExactAppData", StringComparison.Ordinal) ||
            removePackageHelper.Contains("Remove-Item", StringComparison.Ordinal),
            "Exact package removal must not manually erase app data before OS-removal evidence is observed.");
        Assert.HasCount(
            2,
            Regex.Matches(
                lifecycleSmoke,
                @"Remove-ExactLifecyclePackage -ExpectedPackageFullName \$updatedPackageFullName"),
            "Both proof-stage removals must target the exact updated package full name.");

        string reinstallProof = lifecycleSmoke[packageReinstall..postReinstallCreate];
        StringAssert.Contains(reinstallProof, "-Path $updatedArtifacts.Package.FullName");
        StringAssert.Contains(reinstallProof, "Assert-OwnedLifecycleStateAbsent");
        Assert.HasCount(
            1,
            Regex.Matches(
                lifecycleSmoke,
                @"\$installedPackage\.PackageFullName -ne \$updatedPackageFullName"),
            "Reinstall must preserve the exact updated package full name after reset registration is checked by its helper.");

        int evidenceStart = lifecycleSmoke.IndexOf("$successEvidence = [ordered]@{", StringComparison.Ordinal);
        int evidenceEnd = lifecycleSmoke.IndexOf("$githubSha =", evidenceStart, StringComparison.Ordinal);
        Assert.IsTrue(evidenceStart >= 0 && evidenceEnd > evidenceStart);
        string lifecycleEvidence = lifecycleSmoke[evidenceStart..evidenceEnd];
        string[] expectedEvidenceKeys =
        [
            "SchemaVersion",
            "CompletedAt",
            "Configuration",
            "DotNetSdk",
            "BaselinePackageFile",
            "BaselinePackageSha256",
            "BaselinePackageVersion",
            "UpdatedPackageFile",
            "UpdatedPackageSha256",
            "UpdatedPackageVersion",
            "PackageName",
            "PackagePublisher",
            "Architecture",
            "Capabilities",
            "BaselineSignatureStatus",
            "UpdatedSignatureStatus",
            "SameSigner",
            "SamePackageFamily",
            "PackageFullNameChanged",
            "UpdateInstalled",
            "ProtectedRecordReadAfterPackageUpdate",
            "PostUpdateOwnedSurfaceCanaryScanPassed",
            "PackageReset",
            "PackageIdentityPreservedAfterReset",
            "ResetOwnedStateRemoved",
            "FreshCreateAfterReset",
            "ResetRecordIdentityChanged",
            "PackageUninstalledWithOwnedState",
            "UninstallAppDataRemoved",
            "PackageReinstalled",
            "PackageIdentityPreservedAfterReinstall",
            "FreshCreateAfterReinstall",
            "ReinstallRecordIdentityChanged",
            "ProtectedStoreVersion",
            "DataProtectionScope",
            "CreatePersistedAcrossProcessRestart",
            "DuplicateCreateSuppressed",
            "InitialReadVerified",
            "WrongOwnerReadRejected",
            "WrongOwnerDeleteIdempotent",
            "CorrectRecordSurvivedWrongOwnerDelete",
            "UpdateCommitted",
            "UpdatedReadVerified",
            "DeleteCommitted",
            "PostDeleteUnavailable",
            "InitialOwnedSurfaceCanaryScanPassed",
            "FinalOwnedSurfaceCanaryScanPassed",
            "RecordCleanupPassed",
            "TicketCleanupPassed",
            "PackageRemoved",
            "AppDataRemoved",
            "CertificateRemoved",
            "PackageOutputRemoved",
        ];
        string[] actualEvidenceKeys = Regex.Matches(
                lifecycleEvidence,
                @"(?m)^\s{8}([A-Za-z][A-Za-z0-9]*)\s*=")
            .Select(match => match.Groups[1].Value)
            .ToArray();
        CollectionAssert.AreEqual(
            expectedEvidenceKeys,
            actualEvidenceKeys,
            "Lifecycle success evidence must remain an exact allowlist.");
        foreach (string sensitiveToken in new[]
                 {
                     "$appDataPath",
                     "$script:appDataPath",
                     "$packageFamilyName",
                     "$script:packageFamilyName",
                     "$baselinePackageFullName",
                     "$updatedPackageFullName",
                     "$ticketPath",
                     "$script:ticketPath",
                     "$script:runDirectory",
                     "$script:protectedStorePath",
                     "$runId",
                     "$preResetRecordLeaf",
                     "$postResetRecordLeaf",
                     "$postReinstallRecordLeaf",
                     "$secretReference",
                     "$sourceId",
                     "$sourceConfigurationId",
                     "$owner",
                 })
        {
            Assert.IsFalse(
                lifecycleEvidence.Contains(sensitiveToken, StringComparison.Ordinal),
                $"Lifecycle success evidence must not contain sensitive token {sensitiveToken}.");
        }
        string[] assignedEvidenceKeys = Regex.Matches(
                lifecycleSmoke,
                @"(?m)^\s*\$successEvidence\.([A-Za-z][A-Za-z0-9]*)\s*=")
            .Select(match => match.Groups[1].Value)
            .ToArray();
        string[] expectedAssignedEvidenceKeys =
        [
            "CommitSha",
            "CertificateRemoved",
            "PackageOutputRemoved",
            "CompletedAt",
        ];
        CollectionAssert.AreEqual(
            expectedAssignedEvidenceKeys,
            assignedEvidenceKeys,
            "Lifecycle success evidence may only update its four post-block allowlisted fields.");
        StringAssert.Contains(lifecycleSmoke, "$successEvidence.CommitSha = $githubSha.ToLowerInvariant()");
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
    public void M5HttpTransportIsBoundedAndInfrastructureOwned()
    {
        string sourceRoot = Path.Combine(RepositoryRoot, "apps", "windows", "src");
        string applicationSource = File.ReadAllText(Path.Combine(
            sourceRoot,
            "IptvSuite.Application",
            "HttpTransportContracts.cs"));
        string infrastructureSource = File.ReadAllText(Path.Combine(
            sourceRoot,
            "IptvSuite.Infrastructure",
            "BoundedHttpTransport.cs"));
        string domainSource = string.Join(
            '\n',
            Directory.EnumerateFiles(
                    Path.Combine(sourceRoot, "IptvSuite.Domain"),
                    "*.cs",
                    SearchOption.AllDirectories)
                .Where(path => !IsBuildOutputPath(Path.Combine(sourceRoot, "IptvSuite.Domain"), path))
                .Select(File.ReadAllText));

        StringAssert.Contains(applicationSource, "public interface IHttpTransport");
        StringAssert.Contains(applicationSource, "MaximumAllowedResponseBytes = 4 * 1024 * 1024");
        StringAssert.Contains(applicationSource, "MaximumRedirects = 5");
        StringAssert.Contains(applicationSource, "CryptographicOperations.ZeroMemory(content)");
        StringAssert.Contains(applicationSource, "CryptographicOperations.ZeroMemory(authorizationValue)");
        StringAssert.Contains(applicationSource, "public readonly record struct HttpTransportObservation(");
        StringAssert.Contains(applicationSource, "int AttemptCount,");
        StringAssert.Contains(applicationSource, "int ResponseBytes,");
        StringAssert.Contains(applicationSource, "HttpTransportFailure? Failure);");
        Assert.IsFalse(applicationSource.Contains("HttpTransportObservation(Uri", StringComparison.Ordinal));
        Assert.IsFalse(applicationSource.Contains("HttpTransportObservation(string", StringComparison.Ordinal));
        StringAssert.Contains(infrastructureSource, "AllowAutoRedirect = false");
        StringAssert.Contains(infrastructureSource, "UseCookies = false");
        StringAssert.Contains(infrastructureSource, "public BoundedHttpTransport(IHttpTransportObserver? observer)");
        StringAssert.Contains(infrastructureSource, "HttpCompletionOption.ResponseHeadersRead");
        StringAssert.Contains(infrastructureSource, "RedirectTargetPolicy.Evaluate");
        StringAssert.Contains(infrastructureSource, "OriginRelation == RedirectOriginRelation.CrossOrigin");
        StringAssert.Contains(infrastructureSource, "ArrayPool<byte>.Shared.Rent(maximumBytes)");
        StringAssert.Contains(infrastructureSource, "Array.Clear(contentBuffer, 0, contentBuffer.Length)");
        Assert.IsFalse(domainSource.Contains("HttpClient", StringComparison.Ordinal));
        Assert.IsFalse(domainSource.Contains("HttpRequestMessage", StringComparison.Ordinal));
    }

    [TestMethod]
    public void M6XtreamParserIsLiveOnlyBoundedAndDoesNotRetainDirectLocators()
    {
        string parserSource = File.ReadAllText(Path.Combine(
            RepositoryRoot,
            "apps",
            "windows",
            "src",
            "IptvSuite.Infrastructure",
            "XtreamProviderJsonParser.cs"));
        string contractSource = File.ReadAllText(Path.Combine(
            RepositoryRoot,
            "apps",
            "windows",
            "src",
            "IptvSuite.Application",
            "XtreamProviderContracts.cs"));
        string clientSource = File.ReadAllText(Path.Combine(
            RepositoryRoot,
            "apps",
            "windows",
            "src",
            "IptvSuite.Infrastructure",
            "XtreamProviderClient.cs"));

        StringAssert.Contains(parserSource, "MaximumCategoryCount = 10_000");
        StringAssert.Contains(parserSource, "MaximumStreamCount = 50_000");
        StringAssert.Contains(parserSource, "document.RootElement.ValueKind != JsonValueKind.Array");
        StringAssert.Contains(parserSource, "HashSet<string> identifiers = new(StringComparer.Ordinal)");
        StringAssert.Contains(contractSource, "sealed record XtreamStreamInput(");
        StringAssert.Contains(contractSource, "ProviderItemKey ProviderPlaybackKey");
        Assert.IsFalse(contractSource.Contains("DirectSource", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(contractSource.Contains("StreamUrl", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(parserSource.Contains("get_vod", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(parserSource.Contains("get_series", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(parserSource.Contains("get_epg", StringComparison.OrdinalIgnoreCase));
        StringAssert.Contains(clientSource, "action=get_live_categories");
        StringAssert.Contains(clientSource, "action=get_live_streams");
        StringAssert.Contains(clientSource, "ReadCredentialsAsync");
        StringAssert.Contains(clientSource, "ProtectedRecordOwner.ForSourceConfiguration");
        Assert.IsFalse(clientSource.Contains("get_vod", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(clientSource.Contains("get_series", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(clientSource.Contains("get_epg", StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public void M7RemoteM3uParserIsInternalBoundedAndRedactsLocatorStringification()
    {
        string parserSource = File.ReadAllText(Path.Combine(
            RepositoryRoot,
            "apps",
            "windows",
            "src",
            "IptvSuite.Infrastructure",
            "RemoteM3uPlaylistParser.cs"));
        string loaderSource = File.ReadAllText(Path.Combine(
            RepositoryRoot,
            "apps",
            "windows",
            "src",
            "IptvSuite.Infrastructure",
            "RemotePlaylistCatalogLoader.cs"));

        StringAssert.Contains(parserSource, "internal static class RemoteM3uPlaylistParser");
        StringAssert.Contains(parserSource, "MaximumEntries = 50_000");
        StringAssert.Contains(parserSource, "MaximumLineCharacters = 8_192");
        StringAssert.Contains(parserSource, "MaximumTotalCharacters = 32 * 1024 * 1024");
        StringAssert.Contains(parserSource, "new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true)");
        StringAssert.Contains(parserSource, "trimmed.StartsWith(\"#EXT-X-\"");
        StringAssert.Contains(parserSource, "uri.Scheme.Equals(Uri.UriSchemeHttps");
        StringAssert.Contains(parserSource, "string.IsNullOrEmpty(uri.UserInfo)");
        StringAssert.Contains(parserSource, "string.IsNullOrEmpty(uri.Fragment)");
        StringAssert.Contains(parserSource, "public override string ToString() => \"[REMOTE-M3U-ENTRY]\"");
        Assert.IsFalse(parserSource.Contains("public sealed class RemoteM3uEntry", StringComparison.Ordinal));
        Assert.IsFalse(parserSource.Contains("HttpClient", StringComparison.Ordinal));
        StringAssert.Contains(loaderSource, "ReadLocatorAsync(");
        StringAssert.Contains(loaderSource, "ProtectedValuePurpose.RemotePlaylistLocator");
        StringAssert.Contains(loaderSource, "ProtectedRecordOwner.ForSourceConfiguration");
        StringAssert.Contains(loaderSource, "ProtectedSourcePayloadDecoder.TryDecodeRemotePlaylist");
        StringAssert.Contains(loaderSource, "IStreamingHttpTransport");
        StringAssert.Contains(loaderSource, "GetStreamAsync(request");
        StringAssert.Contains(loaderSource, "responseLease.EffectiveUri");
        StringAssert.Contains(loaderSource, "responseLease.Content");
        Assert.IsFalse(loaderSource.Contains("public sealed class RemotePlaylistCatalogLoader", StringComparison.Ordinal));
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
    public void DpapiUserBoundaryLaneIsSanitizedCleanupBoundAndRequired()
    {
        string smoke = File.ReadAllText(
                Path.Combine(RepositoryRoot, "eng", "Invoke-WindowsDpapiUserBoundarySmoke.ps1"))
            .Replace("\r\n", "\n", StringComparison.Ordinal);
        string invocation = File.ReadAllText(
            Path.Combine(
                RepositoryRoot,
                "apps",
                "windows",
                "tests",
                "IptvSuite.DpapiUserBoundaryHarness",
                "HarnessInvocation.cs"));
        string runner = File.ReadAllText(
            Path.Combine(
                RepositoryRoot,
                "apps",
                "windows",
                "tests",
                "IptvSuite.DpapiUserBoundaryHarness",
                "DpapiUserBoundaryRunner.cs"));
        string workflow = File.ReadAllText(
                Path.Combine(RepositoryRoot, ".github", "workflows", "windows-quality.yml"))
            .Replace("\r\n", "\n", StringComparison.Ordinal);

        foreach (string mode in new[]
                 {
                     "prepare-primary",
                     "probe-secondary",
                     "verify-primary",
                     "protocol-self-test",
                 })
        {
            StringAssert.Contains(invocation, $"\"{mode}\"");
        }

        StringAssert.Contains(runner, "DataProtectionScope.CurrentUser");
        StringAssert.Contains(runner, "SecretStoreFailure.ProtectedRecordUnavailable");
        StringAssert.Contains(runner, "CreatorRecordLeaseAbsent");
        StringAssert.Contains(runner, "CreatorRecordImmutable");
        StringAssert.Contains(runner, "SecondaryIsNonAdministrator");

        StringAssert.Contains(smoke, "private const uint LogonWithProfile = 0x00000001;");
        StringAssert.Contains(smoke, "private const uint LogonNetCredentialsOnly = 0x00000002;");
        StringAssert.Contains(smoke, "uint logonFlags = LogonWithProfile;");
        StringAssert.Contains(smoke, "Network-credentials-only logon is forbidden.");
        StringAssert.Contains(smoke, "CreateProcessWithLogonW(");
        StringAssert.Contains(
            smoke,
            "$dotNetExecutable,\n        @($stagedHarnessPath, \"probe-secondary\"");
        StringAssert.Contains(smoke, "IntPtr.Zero,\n                    workingDirectory,");
        StringAssert.Contains(smoke, "Marshal.ZeroFreeGlobalAllocUnicode(passwordPointer)");
        StringAssert.Contains(smoke, "[Array]::Reverse($runBytes, 0, 4)");
        StringAssert.Contains(smoke, "[Array]::Reverse($runBytes, 4, 2)");
        StringAssert.Contains(smoke, "[Array]::Reverse($runBytes, 6, 2)");
        StringAssert.Contains(smoke, "$actualRunId = [Guid]::new($runBytes)");
        StringAssert.Contains(
            smoke,
            "if (processCreated && !processHandleTransferred && processInformation.hProcess != IntPtr.Zero)");
        StringAssert.Contains(
            smoke,
            "uint initialWait = WaitForSingleObject(processInformation.hProcess, 0);");
        StringAssert.Contains(smoke, "if (initialWait != WaitObject0)");
        StringAssert.Contains(smoke, "TerminateProcess(processInformation.hProcess, 18)");
        StringAssert.Contains(
            smoke,
            "if (WaitForSingleObject(processInformation.hProcess, 0) != WaitObject0)");
        StringAssert.Contains(
            smoke,
            "else if (WaitForSingleObject(processInformation.hProcess, 10000) != WaitObject0)");
        int retainedProcessIndex = smoke.IndexOf(
            "RetainedProcess retainedProcess = new RetainedProcess(",
            StringComparison.Ordinal);
        int processHandleTransferIndex = smoke.IndexOf(
            "processHandleTransferred = true;",
            StringComparison.Ordinal);
        Assert.IsTrue(
            retainedProcessIndex >= 0 && processHandleTransferIndex > retainedProcessIndex,
            "The alternate-user process handle may transfer only after a retained process owns it.");
        StringAssert.Contains(smoke, "Get-LocalGroup -SID $usersSid");
        StringAssert.Contains(smoke, "Get-LocalGroup -SID $administratorsSid");
        StringAssert.Contains(
            smoke,
            "Add-NumericAccessRule -Security $security -Sid $script:administratorsSid -Rights ([System.Security.AccessControl.FileSystemRights]::FullControl)");
        StringAssert.Contains(smoke, "function Set-NumericFileAcl");
        StringAssert.Contains(smoke, "$security = [System.Security.AccessControl.FileSecurity]::new()");
        StringAssert.Contains(smoke, "$security.SetAccessRuleProtection($true, $false)");
        StringAssert.Contains(
            smoke,
            "[pscustomobject]@{ Sid = $PrimarySid; Rights = [System.Security.AccessControl.FileSystemRights]::FullControl }");
        StringAssert.Contains(
            smoke,
            "[pscustomobject]@{ Sid = $script:systemSid; Rights = [System.Security.AccessControl.FileSystemRights]::FullControl }");
        StringAssert.Contains(
            smoke,
            "[pscustomobject]@{ Sid = $script:administratorsSid; Rights = [System.Security.AccessControl.FileSystemRights]::FullControl }");
        StringAssert.Contains(
            smoke,
            "[pscustomobject]@{ Sid = $SecondarySid; Rights = $SecondaryRights }");
        StringAssert.Contains(
            smoke,
            "[System.IO.File]::SetAccessControl([System.IO.Path]::GetFullPath($Path), $security)");
        StringAssert.Contains(smoke, "function Get-BoundedRegularTree");
        StringAssert.Contains(smoke, "[int]$MaxEntries = 512");
        StringAssert.Contains(smoke, "[int]$MaxDepth = 12");
        StringAssert.Contains(smoke, "[long]$MaxTotalBytes = 268435456");
        StringAssert.Contains(smoke, "[System.IO.FileAttributes]::ReparsePoint");
        StringAssert.Contains(smoke, "The staged tool tree contains an escaped entry.");
        StringAssert.Contains(smoke, "The staged tool tree contains an unsupported entry.");
        StringAssert.Contains(smoke, "function Assert-EquivalentRegularTrees");
        StringAssert.Contains(smoke, "$actualEntry.RelativePath -cne $expectedEntry.RelativePath");
        StringAssert.Contains(smoke, "$actualEntry.IsDirectory -ne $expectedEntry.IsDirectory");
        StringAssert.Contains(smoke, "$actualEntry.Length -ne $expectedEntry.Length");
        StringAssert.Contains(smoke, "$actualEntry.Sha256 -cne $expectedEntry.Sha256");
        StringAssert.Contains(
            smoke,
            "if (@(Get-ChildItem -LiteralPath $Destination -Force -ErrorAction Stop).Count -ne 0)");
        StringAssert.Contains(
            smoke,
            "Assert-EquivalentRegularTrees -Expected $sourceEntries -Actual $destinationEntries");
        StringAssert.Contains(smoke, "function Set-StagedToolTreeAcl");
        StringAssert.Contains(smoke, "function Assert-ExactNumericAcl");
        StringAssert.Contains(smoke, "$security.AreAccessRulesProtected");
        StringAssert.Contains(smoke, "$normalizedSecondaryRights = $SecondaryRights -bor");
        StringAssert.Contains(
            smoke,
            "[System.Security.AccessControl.FileSystemRights]::Synchronize");
        StringAssert.Contains(
            smoke,
            "$expectedRules.Add($SecondarySid.Value, [int]$normalizedSecondaryRights)");
        StringAssert.Contains(smoke, "if ($rules.Count -ne $expectedRules.Count)");
        StringAssert.Contains(smoke, "$rule.IsInherited");
        StringAssert.Contains(smoke, "$rule.InheritanceFlags -ne $expectedInheritance");
        StringAssert.Contains(smoke, "$seenRules.Count -ne $expectedRules.Count");
        StringAssert.Contains(
            smoke,
            "foreach ($entry in @($before | Where-Object { $_.IsDirectory }))");
        StringAssert.Contains(
            smoke,
            "foreach ($entry in @($before | Where-Object { -not $_.IsDirectory }))");
        StringAssert.Contains(smoke, "Assert-EquivalentRegularTrees -Expected $before -Actual $after");
        StringAssert.Contains(
            smoke,
            "Copy-RegularTree -Source $harnessOutputDirectory -Destination $toolRoot\n    Set-FailurePoint -Stage \"ToolStaging\" -Code \"StagedToolAclFailed\"\n    Set-StagedToolTreeAcl `");
        StringAssert.Contains(smoke, "$boundaryTicketPath = Join-Path $inputPath \"boundary-ticket.bin\"");
        StringAssert.Contains(smoke, "$primaryRawPath = Join-Path $inputPath \"primary-raw.dpapi\"");
        StringAssert.Contains(
            smoke,
            "$primaryStoreEntries = @(Get-ChildItem -LiteralPath $primaryStorePath -Force -ErrorAction Stop)");
        StringAssert.Contains(smoke, "$primaryStoreEntries.Count -ne 1");
        StringAssert.Contains(smoke, "$primaryStoreEntries[0] -isnot [System.IO.FileInfo]");
        StringAssert.Contains(
            smoke,
            "$primaryStoreEntries[0].Name -cnotmatch '\\Arecord-v2-[0-9A-F]{64}\\.dpapi\\z'");
        StringAssert.Contains(
            smoke,
            "foreach ($preparedInputPath in @($boundaryTicketPath, $primaryRawPath, $primaryRecordPath))");
        StringAssert.Contains(
            smoke,
            "-Path $preparedInputPath `\n            -PrimarySid $primarySid `\n            -SecondarySid $createdUserSid `\n            -SecondaryRights ([System.Security.AccessControl.FileSystemRights]::ReadAndExecute)");
        StringAssert.Contains(smoke, "New-LocalUser `");
        StringAssert.Contains(smoke, "Remove-LocalUser -InputObject $candidate");
        StringAssert.Contains(smoke, "NativeBoundaryHost]::DeleteProfile(");
        StringAssert.Contains(smoke, "$accountDescription = \"DPAPI-BOUNDARY:\" + $runIdText");
        StringAssert.Contains(smoke, "-not $candidate.SID.Equals($script:createdUserSid)");
        StringAssert.Contains(smoke, "$script:accountDescription,");
        StringAssert.Contains(smoke, "$absentProfilePath = Assert-ProfilePathAbsent");
        StringAssert.Contains(
            smoke,
            "Get-ChildItem `\n        -LiteralPath $profileParent.FullName `\n        -Force `\n        -ErrorAction Stop");
        Assert.IsTrue(
            Regex.Count(smoke, @"Get-LocalUser -ErrorAction Stop") >= 3,
            "Account discovery, exact removal, and absence checks must fail closed.");
        Assert.IsFalse(
            Regex.IsMatch(smoke, @"Get-LocalUser[^\r\n]*-ErrorAction\s+SilentlyContinue"),
            "A failed local-account query must not be mistaken for account absence.");
        StringAssert.Contains(smoke, "if ($remainingAccounts.Count -ne 0)");
        StringAssert.Contains(smoke, "$securePassword.Dispose()");
        StringAssert.Contains(smoke, "$cleanupFailures.Count -ne 0");
        StringAssert.Contains(smoke, "$failureCode = \"CleanupEvidenceIncomplete\"");
        StringAssert.Contains(smoke, "scan-artifacts $evidenceStagingRoot M4_DPAPI_USER_BOUNDARY $caseId");
        Assert.AreEqual(
            2,
            Regex.Count(smoke, @"@\(Get-RepositoryStatus\)\.Count"),
            "Both repository cleanliness checks must preserve an empty result under Windows PowerShell StrictMode.");
        Assert.AreEqual(
            0,
            Regex.Count(smoke, @"(?<!@)\(Get-RepositoryStatus\)\.Count"),
            "An empty clean-tree result must not be dereferenced as null under Windows PowerShell StrictMode.");

        string[] cleanupSteps =
        [
            "Invoke-CleanupStep -Code \"ProcessCleanupFailed\"",
            "Invoke-CleanupStep -Code \"ProfileCleanupFailed\"",
            "Invoke-CleanupStep -Code \"GroupCleanupFailed\"",
            "Invoke-CleanupStep -Code \"AccountCleanupFailed\"",
            "Invoke-CleanupStep -Code \"WorkspaceCleanupFailed\"",
        ];
        int previousCleanupStepIndex = -1;
        foreach (string cleanupStep in cleanupSteps)
        {
            int cleanupStepIndex = smoke.IndexOf(cleanupStep, StringComparison.Ordinal);
            Assert.IsTrue(
                cleanupStepIndex > previousCleanupStepIndex,
                $"The cleanup step is missing or out of order: {cleanupStep}.");
            previousCleanupStepIndex = cleanupStepIndex;
        }
        StringAssert.Contains(smoke, "$script:failureCode = \"MultipleCleanupFailures\"");

        int evidenceStart = smoke.IndexOf("$successCandidate = [ordered]@{", StringComparison.Ordinal);
        int evidenceEnd = smoke.IndexOf("\n    }\n}", evidenceStart, StringComparison.Ordinal);
        Assert.IsTrue(evidenceStart >= 0 && evidenceEnd > evidenceStart);
        string successEvidence = smoke[evidenceStart..evidenceEnd];
        string[] expectedEvidenceKeys =
        [
            "SchemaVersion",
            "Milestone",
            "EvidenceKind",
            "Configuration",
            "Platform",
            "DataProtectionScope",
            "ExactSdkVerified",
            "DotNetSdk",
            "CleanHeadBound",
            "CommitSha",
            "ControllerScriptSha256",
            "HarnessAssemblySha256",
            "DistinctWindowsAccountVerified",
            "StandardUsersMembershipVerified",
            "SecondaryTokenNonAdministrator",
            "NumericSidAclApplied",
            "LogonWithProfileUsed",
            "NetCredentialsOnlyForbidden",
            "CreateNoWindowUsed",
            "ProbeProcessOwnerVerified",
            "ProbeProcessStartVerified",
            "ProfileLoadedForProbe",
            "RawInputDigestMatched",
            "RecordInputDigestMatched",
            "SecondaryRawRoundTripPassed",
            "CreatorRawRejectedCryptographically",
            "SecondaryAdapterRoundTripPassed",
            "SecondaryStoreClean",
            "CreatorRecordUnavailable",
            "CreatorRecordLeaseAbsent",
            "CreatorRecordImmutable",
            "OwnedDataCanaryScanPassed",
            "PrimaryVerificationPassed",
            "ProbeExitedSuccessfully",
        ];
        string[] actualEvidenceKeys = Regex.Matches(
                successEvidence,
                @"(?m)^\s{8}([A-Za-z][A-Za-z0-9]*)\s*=")
            .Select(match => match.Groups[1].Value)
            .ToArray();
        CollectionAssert.AreEqual(
            expectedEvidenceKeys,
            actualEvidenceKeys,
            "DPAPI user-boundary success evidence must remain an exact allowlist.");

        StringAssert.Contains(successEvidence, "DotNetSdk = $actualSdk");
        StringAssert.Contains(successEvidence, "CommitSha = $repositoryHead");
        StringAssert.Contains(successEvidence, "ControllerScriptSha256 = $controllerScriptSha256");
        StringAssert.Contains(successEvidence, "HarnessAssemblySha256 = $stagedHarnessSha256");
        StringAssert.Contains(smoke, "$repositoryHead = Get-RepositoryHead");
        StringAssert.Contains(smoke, "$controllerScriptSha256 = Get-RegularFileSha256 -Path $PSCommandPath");
        StringAssert.Contains(smoke, "$stagedHarnessSha256 = Get-RegularFileSha256 -Path $stagedHarnessPath");
        StringAssert.Contains(smoke, "$head[0] -notmatch '\\A[0-9a-fA-F]{40}\\z'");
        StringAssert.Contains(smoke, "$hash -notmatch '\\A[0-9a-f]{64}\\z'");

        foreach (string sensitiveToken in new[]
                 {
                     "$securePassword",
                     "$createdUserName",
                     "$createdUserSid",
                     "$primarySid",
                     "$runId",
                      "$runRoot",
                      "$workspaceBase",
                      "$stagedHarnessPath",
                      "$dotNetExecutable",
                  })
        {
            Assert.IsFalse(
                successEvidence.Contains(sensitiveToken, StringComparison.Ordinal),
                $"DPAPI user-boundary success evidence must not contain sensitive token {sensitiveToken}.");
        }

        string[] expectedAssignedEvidenceKeys =
        [
            "ProcessCleanupPassed",
            "StandardUsersMembershipRemoved",
            "LocalAccountRemoved",
            "ProfileRemoved",
            "RunWorkspaceRemoved",
            "ToolWorkspaceRemoved",
            "RepositoryCleanAfterRun",
            "EvidenceCanaryScanPassed",
        ];
        string[] assignedEvidenceKeys = Regex.Matches(
                smoke,
                @"(?m)^\s*\$successCandidate\[""([A-Za-z][A-Za-z0-9]*)""\]\s*=")
            .Select(match => match.Groups[1].Value)
            .ToArray();
        CollectionAssert.AreEqual(
            expectedAssignedEvidenceKeys,
            assignedEvidenceKeys,
            "Only reviewed post-cleanup evidence fields may be assigned.");

        int failureEvidenceStart = smoke.IndexOf("$failureEvidence = [ordered]@{", StringComparison.Ordinal);
        int failureEvidenceEnd = smoke.IndexOf("\n    }", failureEvidenceStart, StringComparison.Ordinal);
        Assert.IsTrue(failureEvidenceStart >= 0 && failureEvidenceEnd > failureEvidenceStart);
        string[] failureEvidenceKeys = Regex.Matches(
                smoke[failureEvidenceStart..failureEvidenceEnd],
                @"(?m)^\s{8}([A-Za-z][A-Za-z0-9]*)\s*=")
            .Select(match => match.Groups[1].Value)
            .ToArray();
        string[] expectedFailureEvidenceKeys = ["Stage", "Code"];
        CollectionAssert.AreEqual(expectedFailureEvidenceKeys, failureEvidenceKeys);

        StringAssert.Contains(
            workflow,
            "  dpapi-user-boundary:\n    name: DPAPI real-user boundary smoke\n    needs: quality\n");
        StringAssert.Contains(
            workflow,
            "shell: powershell\n        run: .\\eng\\Invoke-WindowsDpapiUserBoundarySmoke.ps1 -Configuration Release");
        StringAssert.Contains(
            workflow,
            "- name: Upload sanitized DPAPI user-boundary evidence\n        if: ${{ success() }}\n        uses:");
        StringAssert.Contains(workflow, "name: windows-dpapi-user-boundary-evidence");
        StringAssert.Contains(workflow, ".artifacts/dpapi-user-boundary/last-success.json");
        StringAssert.Contains(
            workflow,
            "      - quality\n      - package-smoke\n      - dpapi-user-boundary\n");
        StringAssert.Contains(
            workflow,
            "DPAPI_USER_BOUNDARY_RESULT: ${{ needs.dpapi-user-boundary.result }}");
        StringAssert.Contains(workflow, "test \"$DPAPI_USER_BOUNDARY_RESULT\" = \"success\"");
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
        Assert.HasCount(10, allUses);
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
