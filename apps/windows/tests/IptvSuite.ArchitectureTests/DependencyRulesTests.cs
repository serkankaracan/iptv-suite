using System.Diagnostics;
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
            ["Microsoft.Data.Sqlite", "System.Security.Cryptography.ProtectedData"]),
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
            ["IptvSuite.Application", "IptvSuite.CatalogCrashHarness", "IptvSuite.Infrastructure", "IptvSuite.Testing"],
            [],
            ["MSTest"]),
        new(
            "IptvSuite.CatalogCrashHarness",
            "apps/windows/tests/IptvSuite.CatalogCrashHarness/IptvSuite.CatalogCrashHarness.csproj",
            ["IptvSuite.Application", "IptvSuite.Domain", "IptvSuite.Infrastructure"],
            [],
            []),
        new(
            "IptvSuite.CatalogUiAcceptanceHarness",
            "apps/windows/tests/IptvSuite.CatalogUiAcceptanceHarness/IptvSuite.CatalogUiAcceptanceHarness.csproj",
            ["IptvSuite.Application", "IptvSuite.Infrastructure"],
            [],
            ["Microsoft.Data.Sqlite"]),
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
            "IptvSuite.PlaybackCompatibilitySpike",
            "apps/windows/tests/IptvSuite.PlaybackCompatibilitySpike/IptvSuite.PlaybackCompatibilitySpike.csproj",
            [],
            [],
            ["LibVLCSharp", "LibVLCSharp.WinUI", "Microsoft.Windows.SDK.BuildTools", "Microsoft.WindowsAppSDK", "VideoLAN.LibVLC.Windows"]),
        new(
            "IptvSuite.NativePlaybackCompatibilitySpike",
            "apps/windows/tests/IptvSuite.NativePlaybackCompatibilitySpike/IptvSuite.NativePlaybackCompatibilitySpike.csproj",
            [],
            [],
            ["Microsoft.Windows.SDK.BuildTools", "Microsoft.WindowsAppSDK"]),
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
        AssertNoPath(graph, "IptvSuite.Windows", "IptvSuite.PlaybackCompatibilitySpike");
        AssertNoPath(graph, "IptvSuite.Windows", "IptvSuite.NativePlaybackCompatibilitySpike");
        AssertNoPath(graph, "IptvSuite.Windows", "IptvSuite.PackageLifecycleHarness");
        AssertNoPath(graph, "IptvSuite.Windows", "IptvSuite.DpapiUserBoundaryHarness");
        AssertNoPath(graph, "IptvSuite.Windows", "IptvSuite.CatalogCrashHarness");
        AssertNoPath(graph, "IptvSuite.Windows", "IptvSuite.CatalogUiAcceptanceHarness");
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
            ["Microsoft.WindowsAppSDK"] = "2.4.0",
            ["Microsoft.Windows.SDK.BuildTools"] = "10.0.26100.8249",
            ["Microsoft.Extensions.TimeProvider.Testing"] = "10.8.0",
            ["Microsoft.Data.Sqlite"] = "10.0.11",
            ["LibVLCSharp"] = "3.10.0",
            ["LibVLCSharp.WinUI"] = "3.10.0",
            ["MSTest"] = "4.3.3",
            ["System.Security.Cryptography.ProtectedData"] = "10.0.10",
            ["VideoLAN.LibVLC.Windows"] = "3.0.23.1",
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

            if (rule.Name is "IptvSuite.Windows" or "IptvSuite.PackageLifecycleHarness" or "IptvSuite.PlaybackCompatibilitySpike" or "IptvSuite.NativePlaybackCompatibilitySpike")
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
        const string catalogCrashHarnessProjectGuid = "{A1C743E8-9472-45B9-9A34-8B73FCA4D120}";
        const string catalogUiAcceptanceHarnessProjectGuid = "{B70AA8A6-304A-4922-8365-EAA421A02726}";
        const string testsFolderGuid = "{0AB3BF05-4346-4AA6-1389-037BE0695223}";

        StringAssert.Contains(
            solution,
            $"{protectedCatalogSpikeProjectGuid} = {testsFolderGuid}");
        StringAssert.Contains(
            solution,
            $"{dpapiUserBoundaryHarnessProjectGuid} = {testsFolderGuid}");
        StringAssert.Contains(
            solution,
            $"{catalogCrashHarnessProjectGuid} = {testsFolderGuid}");
        StringAssert.Contains(
            solution,
            $"{catalogUiAcceptanceHarnessProjectGuid} = {testsFolderGuid}");

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
            StringAssert.Contains(
                solution,
                $"{catalogCrashHarnessProjectGuid}.{configuration}|x64.Build.0 = {configuration}|x64");
            StringAssert.Contains(
                solution,
                $"{catalogUiAcceptanceHarnessProjectGuid}.{configuration}|x64.Build.0 = {configuration}|x64");
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
            Assert.IsFalse(
                solution.Contains(
                    $"{catalogCrashHarnessProjectGuid}.{configuration}|x64.Deploy.0",
                    StringComparison.Ordinal),
                "The test-only catalog crash harness must never deploy as part of a solution build.");
            Assert.IsFalse(
                solution.Contains(
                    $"{catalogUiAcceptanceHarnessProjectGuid}.{configuration}|x64.Deploy.0",
                    StringComparison.Ordinal),
                "The test-only catalog UI acceptance harness must never deploy as part of a solution build.");
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
            "        _catalogServices = WindowsCatalogBrowserFactory.Create();\n" +
            "        _window = new MainWindow(_catalogServices.Browser, _catalogServices.LogoCache);";
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
    public void M9CatalogUiKeepsQueriesBoundedVirtualizedAndAccessible()
    {
        string windowsRoot = Path.Combine(
            RepositoryRoot,
            "apps",
            "windows",
            "src",
            "IptvSuite.Windows");
        string page = File.ReadAllText(Path.Combine(windowsRoot, "MainPage.xaml"));
        string codeBehind = File.ReadAllText(Path.Combine(windowsRoot, "MainPage.xaml.cs"));
        string factory = File.ReadAllText(Path.Combine(windowsRoot, "WindowsCatalogBrowserFactory.cs"));
        string acceptanceHarness = File.ReadAllText(Path.Combine(
            RepositoryRoot,
            "apps",
            "windows",
            "tests",
            "IptvSuite.CatalogUiAcceptanceHarness",
            "Program.cs"));
        string packageSmoke = File.ReadAllText(Path.Combine(
            RepositoryRoot,
            "eng",
            "Invoke-WindowsPackageSmoke.ps1"));

        StringAssert.Contains(page, "<ItemsStackPanel CacheLength=\"1\"");
        StringAssert.Contains(page, "AutomationProperties.AutomationId=\"CatalogSourceSelector\"");
        StringAssert.Contains(page, "AutomationProperties.AutomationId=\"CatalogCategorySelector\"");
        StringAssert.Contains(page, "AutomationProperties.AutomationId=\"CatalogSearchBox\"");
        StringAssert.Contains(page, "x:Name=\"SourceSelector\" TabIndex=\"0\"");
        StringAssert.Contains(page, "x:Name=\"CategorySelector\" Grid.Column=\"1\" TabIndex=\"1\"");
        StringAssert.Contains(page, "x:Name=\"SearchBox\" Grid.Column=\"2\" TabIndex=\"2\"");
        Assert.HasCount(3, Regex.Matches(page, "IsTabStop=\"True\""));
        StringAssert.Contains(codeBehind, "InitializeComponent();\n        AddHandler(");
        StringAssert.Contains(codeBehind, "SourceSelector.AddHandler(");
        Assert.HasCount(3, Regex.Matches(codeBehind, "UIElement.PreviewKeyDownEvent"));
        Assert.HasCount(3, Regex.Matches(codeBehind, @"new KeyEventHandler\(CatalogFilter_PreviewKeyDown\)"));
        StringAssert.Contains(codeBehind, "ReferenceEquals(sender, SourceSelector) && !shiftPressed => CategorySelector");
        StringAssert.Contains(codeBehind, "ReferenceEquals(sender, CategorySelector) && shiftPressed => SourceSelector");
        StringAssert.Contains(codeBehind, "ReferenceEquals(sender, CategorySelector) => SearchBox");
        StringAssert.Contains(codeBehind, "ReferenceEquals(sender, SearchBox) && shiftPressed => CategorySelector");
        StringAssert.Contains(codeBehind, "target is null || !target.IsEnabled || !target.IsTabStop");
        StringAssert.Contains(codeBehind, "args.Handled = target.Focus(FocusState.Keyboard)");
        StringAssert.Contains(codeBehind, "UIElement.LosingFocusEvent");
        StringAssert.Contains(codeBehind, "new TypedEventHandler<UIElement, LosingFocusEventArgs>(CatalogFilter_LosingFocus)");
        StringAssert.Contains(codeBehind, "args.OldFocusedElement as DependencyObject");
        StringAssert.Contains(codeBehind, "IsWithin(oldFocus, SourceSelector) && IsWithin(newFocus, SearchBox)");
        StringAssert.Contains(codeBehind, "IsWithin(oldFocus, SearchBox) && IsWithin(newFocus, SourceSelector)");
        StringAssert.Contains(codeBehind, "args.TrySetNewFocusedElement(CategorySelector)");
        StringAssert.Contains(codeBehind, "new KeyEventHandler(SourceSelector_KeyDown)");
        StringAssert.Contains(codeBehind, "new KeyEventHandler(CategorySelector_KeyDown)");
        StringAssert.Contains(codeBehind, "MoveForwardOnTab(args, SourceSelector, CategorySelector)");
        StringAssert.Contains(codeBehind, "MoveForwardOnTab(args, CategorySelector, SearchBox)");
        StringAssert.Contains(codeBehind, "args.OriginalSource is not DependencyObject origin || !IsWithin(origin, owner)");
        StringAssert.Contains(codeBehind, "InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift)");
        StringAssert.Contains(codeBehind, "args.Handled = true;");
        StringAssert.Contains(codeBehind, "if (_movingTabFocus ||");
        StringAssert.Contains(codeBehind, "try { target.Focus(FocusState.Keyboard); }");
        StringAssert.Contains(codeBehind, "finally { _movingTabFocus = false; }");
        StringAssert.Contains(codeBehind, "if (ReferenceEquals(candidate, ancestor)) return true;");
        StringAssert.Contains(codeBehind, "depth < 32");
        StringAssert.Contains(page, "AutomationProperties.AutomationId=\"CatalogPreviousPage\"");
        StringAssert.Contains(page, "AutomationProperties.AutomationId=\"CatalogNextPage\"");
        StringAssert.Contains(page, "AutomationProperties.LiveSetting=\"Polite\"");
        StringAssert.Contains(codeBehind, "private const int PageSize = 200;");
        StringAssert.Contains(codeBehind, "await BrowseAsync(debounce: true)");
        StringAssert.Contains(codeBehind, "new CatalogBrowseCoordinator(catalogBrowser)");
        StringAssert.Contains(factory, "ApplicationData.GetDefault().LocalCachePath");
        StringAssert.Contains(factory, "new SqliteCatalogQuery(databasePath)");
        StringAssert.Contains(factory, "new SqliteChannelLogoProvider(databasePath, transport)");
        StringAssert.Contains(page, "ContainerContentChanging=\"ChannelList_ContainerContentChanging\"");
        StringAssert.Contains(codeBehind, "_logoPageCancellation.Cancel()");
        StringAssert.Contains(codeBehind, "row.IsCurrentLogoLoad(generation)");
        StringAssert.Contains(acceptanceHarness, "private const int RequiredChannelCount = 50_000;");
        StringAssert.Contains(acceptanceHarness, "args is not [\"seed\", string databasePath, \"50000\"]");
        StringAssert.Contains(acceptanceHarness, "new SqliteCatalogQuery(path)");
        StringAssert.Contains(acceptanceHarness, "provider_item_kind");
        StringAssert.Contains(acceptanceHarness, "File.Exists(path)");
        Assert.IsFalse(
            acceptanceHarness.Contains("ProtectedData", StringComparison.Ordinal) ||
            acceptanceHarness.Contains("HttpClient", StringComparison.Ordinal) ||
            acceptanceHarness.Contains("ProtectedLocator", StringComparison.Ordinal),
            "The M9 UI seed must remain synthetic and avoid credentials, protected locators, and network.");
        StringAssert.Contains(packageSmoke, "$catalogUiHarnessAssemblyPath seed $catalogDatabasePath 50000");
        StringAssert.Contains(packageSmoke, "$catalogRealizedContainerCount -gt 300");
        StringAssert.Contains(packageSmoke, "$catalogInputResponseP95Milliseconds -gt 100.0");
        StringAssert.Contains(packageSmoke, "$categoryElement.Current.IsKeyboardFocusable");
        StringAssert.Contains(packageSmoke, "$focusStableWatch.ElapsedMilliseconds -ge 750");
        StringAssert.Contains(packageSmoke, "did not remain keyboard-focusable after the input-response probe");
        StringAssert.Contains(packageSmoke, "$deadline = (Get-Date).AddSeconds(5)");
        StringAssert.Contains(packageSmoke, "$depth -lt 32");
        StringAssert.Contains(packageSmoke, "Start-Sleep -Milliseconds 50");
        StringAssert.Contains(packageSmoke, "SetForegroundWindow($WindowHandle)");
        StringAssert.Contains(packageSmoke, "$ownerProcessId -eq $ExpectedProcessId");
        StringAssert.Contains(packageSmoke, "[System.Windows.Automation.Automation]::Compare($focused, $ExpectedElement)");
        StringAssert.Contains(packageSmoke, "[System.Windows.Automation.TreeWalker]::RawViewWalker.GetParent($focused)");
        StringAssert.Contains(
            packageSmoke,
            "The packaged catalog keyboard focus order is invalid at $ExpectedAutomationId (Observed$observedFocusTarget).");
        StringAssert.Contains(packageSmoke, "DwmGetCompositionTimingInfo(IntPtr.Zero, ref timing)");
        StringAssert.Contains(packageSmoke, "[StructLayout(LayoutKind.Sequential, Pack = 1)]");
        StringAssert.Contains(packageSmoke, "Marshal.SizeOf(typeof(DwmTimingInfo)) != 292");
        StringAssert.Contains(packageSmoke, "timing.Size = (uint)Marshal.SizeOf(typeof(DwmTimingInfo))");
        StringAssert.Contains(packageSmoke, "Timestamps.Add(timing.QpcVBlank)");
        StringAssert.Contains(packageSmoke, "timing.FramesLate - previousLate");
        StringAssert.Contains(packageSmoke, "failure.GetType().Name");
        StringAssert.Contains(packageSmoke, "unchecked((uint)failure.HResult)");
        StringAssert.Contains(packageSmoke, "for ($frameInput = 0; $frameInput -lt 240; $frameInput++)");
        StringAssert.Contains(packageSmoke, "[IptvSuite.PackageSmoke.KeyboardInspector]::PressPageDown()");
        StringAssert.Contains(packageSmoke, "[IptvSuite.PackageSmoke.KeyboardInspector]::PressPageUp()");
        StringAssert.Contains(packageSmoke, "private static extern uint SendInput(");
        StringAssert.Contains(packageSmoke, "[StructLayout(LayoutKind.Explicit, Size = 32)]");
        StringAssert.Contains(packageSmoke, "CreateKeyboardInput(virtualKey, KeyEventKeyUp)");
        StringAssert.Contains(packageSmoke, "SendInput((uint)inputs.Length, inputs, Marshal.SizeOf(typeof(Input))) != (uint)inputs.Length");
        Assert.IsFalse(packageSmoke.Contains("keybd_event", StringComparison.Ordinal));
        StringAssert.Contains(packageSmoke, "$scrollFocusItem = $channelListElement.FindFirst(");
        StringAssert.Contains(packageSmoke, "Assert-FocusedAutomationElement $scrollFocusItem \"CatalogChannelList\" -RequestFocus");
        StringAssert.Contains(packageSmoke, "Assert-FocusedAutomationElement $sourceElement \"CatalogSourceSelector\" -RequestFocus");
        StringAssert.Contains(packageSmoke, "if ($RequestFocus)");
        StringAssert.Contains(packageSmoke, "$ExpectedElement.SetFocus()");
        StringAssert.Contains(packageSmoke, "$observedFocusTarget = if ($null -eq $focused) { \"None\" } else { \"Other\" }");
        StringAssert.Contains(packageSmoke, "\"CatalogChannelList\"))");
        StringAssert.Contains(packageSmoke, "(Observed$observedFocusTarget).");
        Assert.IsTrue(
            packageSmoke.IndexOf("The packaged catalog did not settle after the input-response probe.", StringComparison.Ordinal) <
            packageSmoke.IndexOf("Assert-FocusedAutomationElement $sourceElement \"CatalogSourceSelector\" -RequestFocus", StringComparison.Ordinal),
            "The asynchronous input probe must settle before keyboard focus order is measured.");
        Assert.IsFalse(
            packageSmoke.Contains("$channelListElement.SetFocus()", StringComparison.Ordinal),
            "The composite ListView root is not the keyboard-focusable scroll target.");
        StringAssert.Contains(packageSmoke, "$catalogFrameP95Milliseconds -gt 33.3");
        StringAssert.Contains(packageSmoke, "$catalogDroppedFramePercent -ge 1.0");
        StringAssert.Contains(packageSmoke, "$catalogFrameMaximumMilliseconds -gt 200.0");
        StringAssert.Contains(packageSmoke, "CatalogDwmFrameP95Milliseconds =");
        StringAssert.Contains(packageSmoke, "CatalogDwmFrameMaximumMilliseconds =");
        StringAssert.Contains(packageSmoke, "CatalogDwmDroppedFramePercent =");
        StringAssert.Contains(packageSmoke, "CatalogDwmFrameIntervalCount =");
        StringAssert.Contains(packageSmoke, "Catalog50kSeedVerified = $catalog50kSeedVerified");
        StringAssert.Contains(packageSmoke, "CatalogRealizedContainerBoundVerified = $catalogRealizedContainerBoundVerified");
        Assert.IsFalse(
            page.Contains("MediaPlayer", StringComparison.Ordinal) ||
            codeBehind.Contains("IPlayer", StringComparison.Ordinal),
            "M9 catalog browsing must not pull M10 playback into the presentation graph.");
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
            if (rule.Name == "IptvSuite.PlaybackCompatibilitySpike")
            {
                Assert.IsFalse(
                    solution.Contains(solutionRelativePath, StringComparison.Ordinal),
                    "The rejected M10 native payload must remain outside the normal solution graph.");
            }
            else
            {
                StringAssert.Contains(solution, solutionRelativePath);
            }
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
                content.Contains("IptvSuite.PlaybackCompatibilitySpike", StringComparison.Ordinal) ||
                content.Contains("IptvSuite.NativePlaybackCompatibilitySpike", StringComparison.Ordinal) ||
                content.Contains("IptvSuite.PackageLifecycleHarness", StringComparison.Ordinal) ||
                content.Contains("IptvSuite.DpapiUserBoundaryHarness", StringComparison.Ordinal) ||
                content.Contains("IptvSuite.CatalogCrashHarness", StringComparison.Ordinal) ||
                content.Contains("LibVLCSharp", StringComparison.Ordinal) ||
                content.Contains("VideoLAN.LibVLC", StringComparison.Ordinal) ||
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

        XDocument playbackSpikeProject = LoadXml(
            "apps/windows/tests/IptvSuite.PlaybackCompatibilitySpike/IptvSuite.PlaybackCompatibilitySpike.csproj");
        Assert.AreEqual("WinExe", GetProperty(playbackSpikeProject, "OutputType"));
        Assert.AreEqual("false", GetProperty(playbackSpikeProject, "IsTestProject"));
        Assert.AreEqual("false", GetProperty(playbackSpikeProject, "IsPackable"));
        Assert.AreEqual("false", GetProperty(playbackSpikeProject, "IsPublishable"));
        Assert.AreEqual("x64", GetProperty(playbackSpikeProject, "Platforms"));
        Assert.AreEqual("x64", GetProperty(playbackSpikeProject, "PlatformTarget"));
        Assert.AreEqual("win-x64", GetProperty(playbackSpikeProject, "RuntimeIdentifier"));
        Assert.AreEqual("true", GetProperty(playbackSpikeProject, "UseWinUI"));

        string playbackLockPath = Path.Combine(
            RepositoryRoot,
            "apps",
            "windows",
            "tests",
            "IptvSuite.PlaybackCompatibilitySpike",
            "packages.lock.json");
        using JsonDocument playbackLock = JsonDocument.Parse(File.ReadAllText(playbackLockPath));
        JsonElement playbackDependencies = playbackLock.RootElement
            .GetProperty("dependencies")
            .GetProperty("net10.0-windows10.0.26100");
        Assert.AreEqual("3.10.0", playbackDependencies.GetProperty("LibVLCSharp").GetProperty("resolved").GetString());
        Assert.AreEqual("3.10.0", playbackDependencies.GetProperty("LibVLCSharp.WinUI").GetProperty("resolved").GetString());
        Assert.AreEqual("3.0.23.1", playbackDependencies.GetProperty("VideoLAN.LibVLC.Windows").GetProperty("resolved").GetString());
        Assert.IsFalse(
            playbackDependencies.TryGetProperty("VideoLAN.LibVLC.Windows.GPL", out _),
            "The M10 baseline must never resolve the GPL native package.");

        XDocument catalogCrashHarnessProject = LoadXml(
            "apps/windows/tests/IptvSuite.CatalogCrashHarness/IptvSuite.CatalogCrashHarness.csproj");
        Assert.AreEqual("Exe", GetProperty(catalogCrashHarnessProject, "OutputType"));
        Assert.AreEqual("false", GetProperty(catalogCrashHarnessProject, "IsTestProject"));
        Assert.AreEqual("false", GetProperty(catalogCrashHarnessProject, "IsPackable"));
        Assert.AreEqual("false", GetProperty(catalogCrashHarnessProject, "IsPublishable"));
        Assert.AreEqual("x64", GetProperty(catalogCrashHarnessProject, "Platforms"));
        Assert.AreEqual("x64", GetProperty(catalogCrashHarnessProject, "PlatformTarget"));
        Assert.AreEqual("false", GetProperty(catalogCrashHarnessProject, "Prefer32Bit"));

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
        StringAssert.Contains(
            packageSmoke,
            "$entry.Name -match '^(?i:IptvSuite\\.CatalogCrashHarness(?:\\..*)?)$'");
        StringAssert.Contains(
            packageSmoke,
            "$entry.Name -match '^(?i:IptvSuite\\.CatalogUiAcceptanceHarness(?:\\..*)?)$'");
        StringAssert.Contains(
            packageSmoke,
            "$entry.Name -match '^(?i:IptvSuite\\.PlaybackCompatibilitySpike(?:\\..*)?)$'");
        StringAssert.Contains(
            packageSmoke,
            "$entry.Name -match '^(?i:IptvSuite\\.NativePlaybackCompatibilitySpike(?:\\..*)?)$'");
        StringAssert.Contains(packageSmoke, "PackagedApplicationActivator]::Activate($aumid)");
        StringAssert.Contains(packageSmoke, "CoCreateInstance");
        StringAssert.Contains(packageSmoke, "LocalServer = 0x00000004");
        StringAssert.Contains(packageSmoke, "-Name \"EnableLUA\"");
        StringAssert.Contains(packageSmoke, "$launchedProcess.Refresh()");
        StringAssert.Contains(packageSmoke, "$null = $launchedProcess.Handle");
        StringAssert.Contains(packageSmoke, "IsWindowVisible($windowHandle)");
        StringAssert.Contains(packageSmoke, "GetWindowThreadProcessId");
        StringAssert.Contains(packageSmoke, "UIAutomationClient");
        StringAssert.Contains(packageSmoke, "CatalogSourceSelector");
        StringAssert.Contains(packageSmoke, "CatalogCategorySelector");
        StringAssert.Contains(packageSmoke, "CatalogSearchBox");
        StringAssert.Contains(packageSmoke, "CatalogChannelList");
        StringAssert.Contains(packageSmoke, "KeyboardInspector]::PressTab()");
        StringAssert.Contains(
            packageSmoke,
            "Assert-FocusedAutomationElement $searchElement \"CatalogSearchBox\"");
        StringAssert.Contains(packageSmoke, "CatalogUiaContractVerified = $catalogUiaContractVerified");
        StringAssert.Contains(packageSmoke, "CatalogKeyboardFocusOrderVerified = $catalogKeyboardFocusOrderVerified");
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
        string sqliteSinkSource = File.ReadAllText(Path.Combine(
            RepositoryRoot,
            "apps",
            "windows",
            "src",
            "IptvSuite.Infrastructure",
            "SqliteRemoteM3uImportSink.cs"));

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
        StringAssert.Contains(parserSource, "internal interface IRemoteM3uEntrySink");
        StringAssert.Contains(parserSource, "internal interface IRemoteM3uImportSink : IRemoteM3uEntrySink");
        StringAssert.Contains(parserSource, "ParseToSinkAsync(");
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
        StringAssert.Contains(loaderSource, "IRemoteM3uImportSink sink");
        StringAssert.Contains(loaderSource, "responseLease.EntityTag");
        StringAssert.Contains(loaderSource, "responseLease.LastModified");
        StringAssert.Contains(sqliteSinkSource, "http_etag");
        StringAssert.Contains(sqliteSinkSource, "http_last_modified_utc");
        StringAssert.Contains(loaderSource, "_sink.CompleteAsync(parsed.Value, cancellationToken)");
        StringAssert.Contains(loaderSource, "_sink.AbortAsync(CancellationToken.None)");
        StringAssert.Contains(loaderSource, "RemoteM3uPlaylistParser.ParseToSinkAsync(");
        Assert.IsFalse(loaderSource.Contains("RemoteM3uPlaylistParser.ParseAsync(", StringComparison.Ordinal));
        Assert.IsFalse(loaderSource.Contains("public sealed class RemotePlaylistCatalogLoader", StringComparison.Ordinal));
        StringAssert.Contains(sqliteSinkSource, "IRemoteM3uImportSink, IAsyncDisposable");
        StringAssert.Contains(sqliteSinkSource, "BeginTransactionAsync");
        StringAssert.Contains(sqliteSinkSource, "CommitAsync");
        StringAssert.Contains(sqliteSinkSource, "RollbackAsync");
        Assert.IsFalse(sqliteSinkSource.Contains("List<RemoteM3uEntry>", StringComparison.Ordinal));
    }

    [TestMethod]
    public void M8CatalogPerformanceDecisionIsExplicitCleanBoundAndExcludedFromNormalWorkflow()
    {
        string wrapper = File.ReadAllText(Path.Combine(
            RepositoryRoot,
            "eng",
            "Invoke-WindowsCatalogPersistenceDecision.ps1"));
        string decisionTest = File.ReadAllText(Path.Combine(
            RepositoryRoot,
            "apps",
            "windows",
            "tests",
            "IptvSuite.IntegrationTests",
            "SqliteCatalogPerformanceDecisionTests.cs"));
        string qualityGate = File.ReadAllText(Path.Combine(
            RepositoryRoot,
            "eng",
            "Invoke-WindowsQualityGate.ps1"));
        string workflow = File.ReadAllText(Path.Combine(
            RepositoryRoot,
            ".github",
            "workflows",
            "windows-quality.yml"));

        StringAssert.Contains(wrapper, "[switch]$AllowDecision");
        StringAssert.Contains(wrapper, "if (-not $AllowDecision)");
        StringAssert.Contains(wrapper, "status --porcelain=v1");
        StringAssert.Contains(wrapper, "$finalHead -ne $initialHead");
        StringAssert.Contains(wrapper, "$finalStatus.Count -ne 0");
        StringAssert.Contains(wrapper, "IPTVSUITE_M8_CATALOG_DECISION");
        StringAssert.Contains(wrapper, "SqliteCatalogPerformanceDecisionTests.MeasureParserToProtectedSqliteDecisionMatrix");
        StringAssert.Contains(decisionTest, "private const int Iterations = 20;");
        StringAssert.Contains(decisionTest, "[5_000, 10_000, 20_000, 50_000]");
        StringAssert.Contains(decisionTest, "Assert.IsFalse(File.Exists(databasePath + \"-wal\"))");
        StringAssert.Contains(decisionTest, "Assert.IsFalse(File.Exists(databasePath + \"-shm\"))");
        StringAssert.Contains(decisionTest, "ContainsLocatorCanaryAsync");
        StringAssert.Contains(decisionTest, "new CancellingDecisionTransport(cancellationPlaylist, cancellation)");
        StringAssert.Contains(decisionTest, "activeOrStagingRowsAfterCompletion = 0");
        StringAssert.Contains(decisionTest, "trigger = \"second-stream-read\"");
        StringAssert.Contains(decisionTest, "[databasePath, true]");
        StringAssert.Contains(decisionTest, "sinkWriteAllocatedBytes");
        Assert.IsFalse(
            qualityGate.Contains("Invoke-WindowsCatalogPersistenceDecision.ps1", StringComparison.Ordinal),
            "The normal quality gate must not run the opt-in M8 performance Decision.");
        Assert.IsFalse(
            workflow.Contains("Invoke-WindowsCatalogPersistenceDecision.ps1", StringComparison.Ordinal),
            "The normal hosted workflow must not run the opt-in M8 performance Decision.");
    }

    [TestMethod]
    public void M9CatalogQueryDecisionIsExplicitCleanBoundAndExcludedFromNormalWorkflow()
    {
        string wrapper = File.ReadAllText(Path.Combine(
            RepositoryRoot,
            "eng",
            "Invoke-WindowsCatalogQueryDecision.ps1"));
        string decisionTest = File.ReadAllText(Path.Combine(
            RepositoryRoot,
            "apps",
            "windows",
            "tests",
            "IptvSuite.IntegrationTests",
            "SqliteCatalogPerformanceDecisionTests.cs"));
        string qualityGate = File.ReadAllText(Path.Combine(
            RepositoryRoot,
            "eng",
            "Invoke-WindowsQualityGate.ps1"));
        string workflow = File.ReadAllText(Path.Combine(
            RepositoryRoot,
            ".github",
            "workflows",
            "windows-quality.yml"));

        StringAssert.Contains(wrapper, "[switch]$AllowDecision");
        StringAssert.Contains(wrapper, "status --porcelain=v1");
        StringAssert.Contains(wrapper, "$finalHead -ne $initialHead");
        StringAssert.Contains(wrapper, "IPTVSUITE_M9_QUERY_DECISION");
        StringAssert.Contains(wrapper, "Measure50kIndexedCatalogQueryDecision");
        StringAssert.Contains(decisionTest, "recordCount = 50_000");
        StringAssert.Contains(decisionTest, "iterations = Iterations");
        StringAssert.Contains(decisionTest, "indexedQueryBudgetMilliseconds = 100");
        StringAssert.Contains(decisionTest, "cachedFirstVisibleBudgetMilliseconds = 500");
        StringAssert.Contains(decisionTest, "Assert.IsLessThanOrEqualTo(100d");
        StringAssert.Contains(decisionTest, "Assert.IsLessThanOrEqualTo(500d");
        Assert.IsFalse(
            qualityGate.Contains("Invoke-WindowsCatalogQueryDecision.ps1", StringComparison.Ordinal),
            "The normal quality gate must not run the opt-in M9 query Decision.");
        Assert.IsFalse(
            workflow.Contains("Invoke-WindowsCatalogQueryDecision.ps1", StringComparison.Ordinal),
            "The normal hosted workflow must not run the opt-in M9 query Decision.");
    }

    [TestMethod]
    public void M10PlaybackCandidateDecisionPreservesTheExactLicenseBoundary()
    {
        string decision = File.ReadAllText(Path.Combine(
            RepositoryRoot,
            "eng",
            "Invoke-WindowsPlaybackCandidateDecision.ps1"));
        string rejectedAdr = File.ReadAllText(Path.Combine(
            RepositoryRoot,
            "docs",
            "adr",
            "ADR-002-windows-playback-engine.md"));
        string fallbackAdr = File.ReadAllText(Path.Combine(
            RepositoryRoot,
            "docs",
            "adr",
            "ADR-007-windows-native-tier-a-playback-fallback.md"));

        StringAssert.Contains(decision, "$expectedPackageVersion = '3.0.23.1'");
        StringAssert.Contains(decision, "$expectedBlocker = 'build/x64/plugins/codec/libx26410b_plugin.dll'");
        StringAssert.Contains(decision, "ExactBinaryLicenseBoundaryUnresolved");
        StringAssert.Contains(decision, "PackageLicenseExpression");
        StringAssert.Contains(decision, "EmbeddedLicenseOrNoticeEntries");
        StringAssert.Contains(decision, "Do not ship this candidate");
        StringAssert.Contains(rejectedAdr, "**Status:** Rejected — M10 exact binary/license hard gate");
        StringAssert.Contains(fallbackAdr, "**Status:** Proposed");
        StringAssert.Contains(fallbackAdr, "Windows `MediaPlayer` / Media Foundation");
        Assert.IsFalse(
            fallbackAdr.Contains("**Status:** Accepted", StringComparison.Ordinal),
            "The native fallback must not be accepted before its independent playback gate passes.");
    }

    [TestMethod]
    public void NativeTierAPlaybackCorpusIsSyntheticLicensedAndByteBound()
    {
        string fixtureRoot = Path.Combine(
            RepositoryRoot,
            "apps",
            "windows",
            "tests",
            "fixtures",
            "playback",
            "tier-a");
        using JsonDocument manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(fixtureRoot, "fixture-manifest.json")));
        JsonElement root = manifest.RootElement;

        Assert.AreEqual(1, root.GetProperty("SchemaVersion").GetInt32());
        Assert.AreEqual("iptvsuite-tier-a-synthetic-v1", root.GetProperty("FixtureId").GetString());
        Assert.AreEqual("CC0-1.0", root.GetProperty("Rights").GetProperty("License").GetString());
        StringAssert.Contains(
            root.GetProperty("Rights").GetProperty("Provenance").GetString()!,
            "no captured or third-party media");
        Assert.AreEqual("A", root.GetProperty("Capability").GetProperty("Tier").GetString());
        Assert.AreEqual("H.264 High, yuv420p, 640x360, 25fps", root.GetProperty("Capability").GetProperty("Video").GetString());
        Assert.AreEqual("AAC-LC, 48kHz, stereo", root.GetProperty("Capability").GetProperty("Audio").GetString());

        JsonElement[] files = root.GetProperty("Files").EnumerateArray().ToArray();
        Assert.HasCount(6, files);
        foreach (JsonElement file in files)
        {
            string relativePath = file.GetProperty("Path").GetString()!;
            string fullPath = Path.Combine(fixtureRoot, relativePath);
            Assert.IsTrue(File.Exists(fullPath), $"Missing Tier A fixture file: {relativePath}");
            Assert.AreEqual(new FileInfo(fullPath).Length, file.GetProperty("SizeBytes").GetInt64());
            string actualHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(fullPath))).ToLowerInvariant();
            Assert.AreEqual(file.GetProperty("Sha256").GetString(), actualHash, $"Tier A fixture hash drifted: {relativePath}");
        }

        string playlist = File.ReadAllText(Path.Combine(fixtureRoot, "hls.m3u8"));
        StringAssert.Contains(playlist, "#EXT-X-ENDLIST");
        Assert.HasCount(4, Regex.Matches(playlist, @"(?m)^hls-\d{3}\.ts$"));
        Assert.IsFalse(Regex.IsMatch(playlist, @"(?i)(https?://|file:|\\|\.\.)"));

        string generator = File.ReadAllText(Path.Combine(RepositoryRoot, "eng", "New-WindowsTierAPlaybackCorpus.ps1"));
        StringAssert.Contains(generator, "$expectedToolVersion = 'n9.0.1-6-g9d4ca21220-20260820'");
        StringAssert.Contains(generator, "$expectedArchiveSha256 = '73d64c702162aaa5eaa8f36c21921f95cb351d737bf89c0557d773cd8cf091a9'");
        StringAssert.Contains(generator, "Get-FileHash -LiteralPath $archivePath -Algorithm SHA256");
        StringAssert.Contains(generator, "testsrc2=size=640x360:rate=25");
        StringAssert.Contains(generator, "sine=frequency=1000:sample_rate=48000");
        StringAssert.Contains(generator, "-profile:v high");
        StringAssert.Contains(generator, "-profile:a aac_low");
    }

    [TestMethod]
    public void NativePlaybackSpikeIsLoopbackBoundAndWritesSanitizedEvidence()
    {
        string spikeRoot = Path.Combine(
            RepositoryRoot,
            "apps",
            "windows",
            "tests",
            "IptvSuite.NativePlaybackCompatibilitySpike");
        string app = File.ReadAllText(Path.Combine(spikeRoot, "App.xaml.cs"));
        string window = File.ReadAllText(Path.Combine(spikeRoot, "MainWindow.xaml.cs"));
        string normalizedApp = Regex.Replace(
            app,
            @"\s+",
            " ",
            RegexOptions.CultureInvariant);
        string normalizedWindow = Regex.Replace(
            window,
            @"\s+",
            " ",
            RegexOptions.CultureInvariant);
        string xaml = File.ReadAllText(Path.Combine(spikeRoot, "MainWindow.xaml"));

        StringAssert.Contains(xaml, "<MediaPlayerElement");
        StringAssert.Contains(xaml, "AutomationProperties.AutomationId=\"NativePlaybackSurface\"");
        StringAssert.Contains(app, "AppInstance.GetCurrent().GetActivatedEventArgs()");
        StringAssert.Contains(app, "ApplicationData.Current.LocalCacheFolder.Path");
        StringAssert.Contains(app, "M10NativePlayback");
        StringAssert.Contains(window, "Guid RunId,");
        StringAssert.Contains(window, "Guid.TryParseExact(runIdText, \"N\", out Guid runId)");
        StringAssert.Contains(window, "runId.ToString(\"N\") != runIdText");
        StringAssert.Contains(app, "new NativePlaybackProbeEnvelope(");
        StringAssert.Contains(
            normalizedApp,
            "new NativePlaybackProbeEnvelope( 5, request.RunId.ToString(\"N\"), runtimeDependency, result);");
        StringAssert.Contains(app, "request.RunId.ToString(\"N\")");
        StringAssert.Contains(app, "$\"result-{request.RunId:N}.json\"");
        StringAssert.Contains(app, "$\"result-{request.RunId:N}.pending\"");
        StringAssert.Contains(app, "FileMode.CreateNew");
        StringAssert.Contains(app, "stream.Flush(flushToDisk: true)");
        StringAssert.Contains(app, "File.Move(pendingEvidencePath, evidencePath, overwrite: false)");
        StringAssert.Contains(app, "Package.Current.Dependencies");
        StringAssert.Contains(app, "package.Id.Name == \"Microsoft.WindowsAppRuntime.2\"");
        StringAssert.Contains(app, "dependency.Id.Architecture.ToString() != \"X64\"");
        StringAssert.Contains(app, "dependency.Id.PublisherId != \"8wekyb3d8bbwe\"");
        StringAssert.Contains(app, "!dependency.IsFramework");
        StringAssert.Contains(window, "fixture.Scheme != Uri.UriSchemeHttps");
        StringAssert.Contains(window, "!fixture.IsLoopback");
        StringAssert.Contains(window, "!string.IsNullOrEmpty(fixture.UserInfo)");
        StringAssert.Contains(window, "!string.IsNullOrEmpty(fixture.Query)");
        StringAssert.Contains(window, "!string.IsNullOrEmpty(fixture.Fragment)");
        StringAssert.Contains(window, "\"/direct-h264-aac.ts\"");
        StringAssert.Contains(window, "\"/hls.m3u8\"");
        StringAssert.Contains(window, "switchCount is < 2 or > 100");
        StringAssert.Contains(window, "soakMinutes is < 0 or > 480");
        StringAssert.Contains(window, "soakMinutes > 0 && switchCount != 100");
        StringAssert.Contains(window, "cancellationProbeCount is < 0 or > 1");
        StringAssert.Contains(
            normalizedWindow,
            "cancellationProbeCount == 1 && (switchCount != 100 || soakMinutes != 0)");
        StringAssert.Contains(window, "RealTimePlayback = true,");
        Assert.AreEqual(
            1,
            Regex.Count(
                window,
                Regex.Escape("RealTimePlayback = true,"),
                RegexOptions.CultureInvariant),
            "The live Tier A probe must configure real-time playback exactly once.");
        Assert.AreEqual(
            0,
            Regex.Count(
                window,
                Regex.Escape("RealTimePlayback = false"),
                RegexOptions.CultureInvariant),
            "The live Tier A probe must not disable real-time playback.");
        StringAssert.Contains(window, "private readonly TaskCompletionSource _surfaceReady");
        StringAssert.Contains(window, "private readonly CancellationTokenSource _lifetimeCancellation");
        StringAssert.Contains(window, "PlaybackSurface.Loaded += PlaybackSurface_Loaded");
        StringAssert.Contains(
            window,
            "await _surfaceReady.Task.WaitAsync(TimeSpan.FromSeconds(5), probeCancellationToken);");
        StringAssert.Contains(window, "CancellationTokenSource.CreateLinkedTokenSource(");
        StringAssert.Contains(window, "_lifetimeCancellation.Cancel()");
        StringAssert.Contains(window, "NativePlaybackFailure.SurfaceReadinessTimeout");
        StringAssert.Contains(window, "PlaybackSurface.Loaded -= PlaybackSurface_Loaded");
        StringAssert.Contains(
            normalizedWindow,
            "private void PlaybackSurface_Loaded(object sender, RoutedEventArgs args) => _surfaceReady.TrySetResult();");
        Assert.AreEqual(
            1,
            Regex.Count(
                window,
                Regex.Escape("_surfaceReady.TrySetResult();"),
                RegexOptions.CultureInvariant),
            "Only the Loaded handler may complete surface readiness.");
        int surfaceSubscriptionIndex = window.IndexOf(
            "PlaybackSurface.Loaded += PlaybackSurface_Loaded",
            StringComparison.Ordinal);
        int mediaAttachmentIndex = window.IndexOf(
            "PlaybackSurface.SetMediaPlayer(_mediaPlayer)",
            StringComparison.Ordinal);
        int realTimeConfigurationIndex = window.IndexOf(
            "RealTimePlayback = true,",
            StringComparison.Ordinal);
        int surfaceWaitIndex = window.IndexOf(
            "await _surfaceReady.Task.WaitAsync(TimeSpan.FromSeconds(5)",
            StringComparison.Ordinal);
        int switchLoopIndex = window.IndexOf(
            "for (int index = 0; index < request.SwitchCount; index++)",
            StringComparison.Ordinal);
        int firstSourceAssignmentIndex = window.IndexOf(
            "_mediaPlayer.Source = source;",
            StringComparison.Ordinal);
        int firstStartupTimestampIndex = switchLoopIndex < 0
            ? -1
            : window.IndexOf(
                "activeStartupStarted = Stopwatch.GetTimestamp();",
                switchLoopIndex,
                StringComparison.Ordinal);
        int firstSourceCreationIndex = window.IndexOf(
            "MediaSource source = MediaSource.CreateFromUri(fixture);",
            StringComparison.Ordinal);
        int startupMeasurementBindingIndex = firstStartupTimestampIndex < 0
            ? -1
            : window.IndexOf(
                "long startupStarted = activeStartupStarted;",
                firstStartupTimestampIndex,
                StringComparison.Ordinal);
        int firstPlayInvocationIndex = window.IndexOf(
            "_mediaPlayer.Play();",
            StringComparison.Ordinal);
        int firstMediaOpenWaitIndex = firstPlayInvocationIndex < 0
            ? -1
            : window.IndexOf(
                "Task firstCompletion = await Task.WhenAny(",
                firstPlayInvocationIndex,
                StringComparison.Ordinal);
        int disposeSurfaceDetachIndex = window.IndexOf(
            "PlaybackSurface.SetMediaPlayer(null)",
            StringComparison.Ordinal);
        int mediaPlayerDisposeIndex = window.IndexOf(
            "_mediaPlayer.Dispose();",
            StringComparison.Ordinal);
        Assert.IsTrue(
            realTimeConfigurationIndex >= 0 && realTimeConfigurationIndex < mediaAttachmentIndex &&
            surfaceSubscriptionIndex >= 0 && surfaceSubscriptionIndex < mediaAttachmentIndex &&
            mediaAttachmentIndex < surfaceWaitIndex &&
            surfaceWaitIndex >= 0 && surfaceWaitIndex < switchLoopIndex &&
            switchLoopIndex < firstStartupTimestampIndex &&
            firstStartupTimestampIndex < startupMeasurementBindingIndex &&
            startupMeasurementBindingIndex < firstSourceCreationIndex &&
            firstSourceCreationIndex < firstSourceAssignmentIndex &&
            firstSourceAssignmentIndex < firstPlayInvocationIndex &&
            firstPlayInvocationIndex < firstMediaOpenWaitIndex,
            "The Loaded surface boundary must precede the unchanged source-to-open startup measurement.");
        Assert.IsTrue(
            disposeSurfaceDetachIndex >= 0 && disposeSurfaceDetachIndex < mediaPlayerDisposeIndex,
            "Disposal must detach the playback surface before disposing the player.");
        Assert.IsFalse(
            window.Contains("CompositionTarget.Rendered", StringComparison.Ordinal),
            "The rejected first-frame hypothesis must not leave a global rendered handler behind.");
        Assert.AreEqual(
            2,
            Regex.Count(
                window,
                Regex.Escape("ObjectDisposedException.ThrowIf(_disposed, this);"),
                RegexOptions.CultureInvariant),
            "The probe must reject disposal both before and after the readiness boundary.");
        StringAssert.Contains(window, "AppWindow.Resize(new SizeInt32(960, 540))");
        StringAssert.Contains(window, "presenter.Minimize()");
        StringAssert.Contains(window, "presenter.Restore()");
        StringAssert.Contains(window, "AppWindowPresenterKind.FullScreen");
        StringAssert.Contains(window, "AppWindowPresenterKind.Overlapped");
        StringAssert.Contains(window, "NativePlaybackFailure.SurfaceLifecycleFailed");
        StringAssert.Contains(window, "while (_mediaPlayer.Source is not null)");
        StringAssert.Contains(window, "timeout.Elapsed >= TimeSpan.FromSeconds(5)");
        StringAssert.Contains(window, "sourceDetachSamples.Add(await DetachSourceAsync(");
        StringAssert.Contains(window, "NativePlaybackFailure.SourceDetachmentTimeout");
        StringAssert.Contains(window, "NativePlaybackFailure.SourceDetachmentFailed");
        StringAssert.Contains(window, "if (canPauseBeforeDetach)");
        StringAssert.Contains(window, "_mediaPlayer.Source is not null && _mediaPlayer.PlaybackSession.CanPause");
        StringAssert.Contains(window, "BestEffortResetAfterProbe()");
        StringAssert.Contains(window, "Do not mask the probe's typed result.");
        StringAssert.Contains(window, "NativePlaybackTeardownStage.PlaybackSessionInspection");
        StringAssert.Contains(window, "NativePlaybackTeardownStage.SourceClear");
        StringAssert.Contains(window, "NativePlaybackTeardownStage.SourceInspection");
        StringAssert.Contains(window, "COMException => NativePlaybackExceptionCategory.Com");
        StringAssert.Contains(window, "for (int attempt = 0; attempt < 2; attempt++)");
        StringAssert.Contains(window, "_mediaFailure == NativePlaybackFailure.MediaFailed && attempt == 0");
        StringAssert.Contains(window, "MediaSource source = MediaSource.CreateFromUri(fixture)");
        StringAssert.Contains(window, "MediaSourceOpenOperationCompletedEventArgs args");
        StringAssert.Contains(window, "source.OpenOperationCompleted += Source_OpenOperationCompleted;");
        StringAssert.Contains(window, "source.OpenOperationCompleted -= Source_OpenOperationCompleted;");
        StringAssert.Contains(window, "args.Error is not null");
        StringAssert.Contains(window, "NativePlaybackStartupStage.MediaSourceOpenWait");
        StringAssert.Contains(window, "Task firstCompletion = await Task.WhenAny(");
        StringAssert.Contains(window, "mediaOpenTimeout - Stopwatch.GetElapsedTime(mediaOpenWaitStarted)");
        StringAssert.Contains(window, "completion.Timestamp > sourceOpenDeadline");
        StringAssert.Contains(
            window,
            "sourceOpenDeadline = mediaOpenWaitStarted + (Stopwatch.Frequency * 5);");
        StringAssert.Contains(
            normalizedWindow,
            "activeStartupStageStarted = Math.Max( sourceOpenWaitStarted, completion.Timestamp);");
        int cancellationProbeBoundaryIndex = window.IndexOf(
            "private async Task<NativePlaybackCancellationMetrics> RunCancellationProbeAsync(",
            StringComparison.Ordinal);
        Assert.IsTrue(cancellationProbeBoundaryIndex > 0);
        string measuredPlaybackHelper = window[..cancellationProbeBoundaryIndex];
        Assert.AreEqual(
            1,
            Regex.Count(
                measuredPlaybackHelper,
                Regex.Escape("source.OpenOperationCompleted += Source_OpenOperationCompleted;"),
                RegexOptions.CultureInvariant),
            "Each attempt must bind one source-open diagnostic handler.");
        Assert.AreEqual(
            1,
            Regex.Count(
                measuredPlaybackHelper,
                Regex.Escape("source.OpenOperationCompleted -= Source_OpenOperationCompleted;"),
                RegexOptions.CultureInvariant),
            "The idempotent helper must own the source-open event removal.");
        Assert.AreEqual(
            3,
            Regex.Count(
                measuredPlaybackHelper,
                Regex.Escape("UnsubscribeSourceOpenHandler();"),
                RegexOptions.CultureInvariant),
            "Success, retry, and finally paths must all close the attempt-owned handler.");
        int sourceOpenSubscribeIndex = measuredPlaybackHelper.IndexOf(
            "source.OpenOperationCompleted += Source_OpenOperationCompleted;",
            StringComparison.Ordinal);
        int firstSourceOpenUnsubscribeCallIndex = measuredPlaybackHelper.IndexOf(
            "UnsubscribeSourceOpenHandler();",
            sourceOpenSubscribeIndex,
            StringComparison.Ordinal);
        int firstSourceDetachIndex = measuredPlaybackHelper.IndexOf(
            "sourceDetachSamples.Add(await DetachSourceAsync(",
            firstSourceOpenUnsubscribeCallIndex,
            StringComparison.Ordinal);
        int retrySourceOpenUnsubscribeCallIndex = measuredPlaybackHelper.IndexOf(
            "UnsubscribeSourceOpenHandler();",
            firstSourceDetachIndex,
            StringComparison.Ordinal);
        int retrySourceDetachIndex = measuredPlaybackHelper.IndexOf(
            "sourceDetachSamples.Add(await DetachSourceAsync(",
            retrySourceOpenUnsubscribeCallIndex,
            StringComparison.Ordinal);
        int finalSourceOpenUnsubscribeCallIndex = measuredPlaybackHelper.LastIndexOf(
            "UnsubscribeSourceOpenHandler();",
            StringComparison.Ordinal);
        int attemptResetIndex = measuredPlaybackHelper.IndexOf(
            "BestEffortResetAfterProbe();",
            finalSourceOpenUnsubscribeCallIndex,
            StringComparison.Ordinal);
        Assert.IsTrue(
            firstSourceCreationIndex < sourceOpenSubscribeIndex &&
            sourceOpenSubscribeIndex < firstSourceAssignmentIndex &&
            firstSourceAssignmentIndex < firstSourceOpenUnsubscribeCallIndex &&
            firstSourceOpenUnsubscribeCallIndex < firstSourceDetachIndex &&
            firstSourceDetachIndex < retrySourceOpenUnsubscribeCallIndex &&
            retrySourceOpenUnsubscribeCallIndex < retrySourceDetachIndex &&
            retrySourceDetachIndex < finalSourceOpenUnsubscribeCallIndex &&
            finalSourceOpenUnsubscribeCallIndex < attemptResetIndex,
            "The attempt-owned source-open handler must bind before Source assignment and close before success/retry detach and reset.");
        Assert.IsFalse(
            window.Contains("AdaptiveMediaSource", StringComparison.Ordinal) ||
            window.Contains("source.OpenAsync", StringComparison.Ordinal) ||
            window.Contains("ExtendedError", StringComparison.Ordinal),
            "The attribution checkpoint must not change HLS behavior or expose native error details.");
        StringAssert.Contains(window, "NativePlaybackTeardownStage.MediaSourceDispose");
        StringAssert.Contains(window, "DisposeMediaSource(source)");
        StringAssert.Contains(window, "BestEffortDisposeMediaSource(source)");
        StringAssert.Contains(window, "Preserve the primary typed probe failure");
        StringAssert.Contains(window, "private TaskCompletionSource<long>? _opened;");
        StringAssert.Contains(window, "long activeStartupStarted = Stopwatch.GetTimestamp();");
        StringAssert.Contains(window, "long startupStarted = activeStartupStarted;");
        Assert.AreEqual(
            2,
            Regex.Count(
                window,
                Regex.Escape("startupSourceOpenDiagnostic = default;"),
                RegexOptions.CultureInvariant),
            "The switch diagnostic must be initialized and reset for every retry attempt.");
        StringAssert.Contains(window, "NativePlaybackStartupStage.SourceCreation");
        StringAssert.Contains(window, "NativePlaybackStartupStage.SourceAssignment");
        StringAssert.Contains(window, "NativePlaybackStartupStage.PlayInvocation");
        StringAssert.Contains(window, "NativePlaybackStartupStage.MediaSourceOpenWait");
        StringAssert.Contains(window, "NativePlaybackStartupStage.MediaOpenWait");
        StringAssert.Contains(window, "NativePlaybackStartupStage.PlaybackAdvanceWait");
        StringAssert.Contains(window, "startupFailureDiagnostic = CaptureStartupFailureDiagnostic();");
        int startupFailureSnapshotIndex = window.IndexOf(
            "startupFailureDiagnostic = CaptureStartupFailureDiagnostic();",
            StringComparison.Ordinal);
        int startupAttemptFinallyIndex = window.IndexOf(
            "finally",
            startupFailureSnapshotIndex,
            StringComparison.Ordinal);
        Assert.IsTrue(
            startupFailureSnapshotIndex >= 0 &&
            startupAttemptFinallyIndex > startupFailureSnapshotIndex,
            "A startup timeout must be snapshotted before reset and source disposal.");
        StringAssert.Contains(window, "NativePlaybackFixture.HlsH264AacMpegTs");
        StringAssert.Contains(window, "NativePlaybackFixture.DirectH264AacMpegTs");
        StringAssert.Contains(
            normalizedWindow,
            "return new NativePlaybackProbeRequest( runId, [hlsUri, directUri], switchCount, TimeSpan.FromMinutes(soakMinutes), cancellationProbeCount);");
        StringAssert.Contains(window, "if (startupMilliseconds > startupMaximumMilliseconds)");
        StringAssert.Contains(window, "startupMaximumSwitchOrdinal = index + 1;");
        StringAssert.Contains(window, "startupMaximumAttemptCount = startupAttemptCount;");
        StringAssert.Contains(
            window,
            "startupMaximumSurfaceTransitionCount = switchSurfaceTransitionCount;");
        StringAssert.Contains(
            window,
            "startupMaximumPreWaitMilliseconds = startupPreWaitMilliseconds;");
        StringAssert.Contains(
            window,
            "startupMaximumMediaOpenWaitMilliseconds = startupMediaOpenWaitMilliseconds;");
        StringAssert.Contains(window, "hlsStartupSamples.Max(),");
        StringAssert.Contains(window, "directStartupSamples.Max(),");
        StringAssert.Contains(window, "TimeSpan mediaOpenTimeout = TimeSpan.FromSeconds(5);");
        StringAssert.Contains(window, ".WaitAsync(mediaOpenTimeout, probeCancellationToken);");
        StringAssert.Contains(window, "mediaOpenedTask.WaitAsync(");
        StringAssert.Contains(window, "Stopwatch.GetElapsedTime(startupStarted, openedTimestamp).TotalMilliseconds");
        StringAssert.Contains(window, "_opened?.TrySetResult(Stopwatch.GetTimestamp())");
        StringAssert.Contains(window, "await _advanced.Task.WaitAsync(TimeSpan.FromSeconds(3)");
        StringAssert.Contains(window, "TimeSpan sampleInterval = TimeSpan.FromMinutes(5)");
        StringAssert.Contains(window, "sample.Elapsed >= TimeSpan.FromMinutes(30)");
        StringAssert.Contains(window, "growthBytes <= 100L * 1024 * 1024 && growthPercent <= 10d && !monotonic");
        StringAssert.Contains(window, "right.PrivateBytes > left.PrivateBytes");
        StringAssert.Contains(window, "sender.Position >= TimeSpan.FromMilliseconds(500)");
        StringAssert.Contains(window, "RunCancellationProbeAsync(");
        StringAssert.Contains(window, "const int controlledObservationMilliseconds = 1000;");
        StringAssert.Contains(
            window,
            "Task cancellationWait = Task.Delay(Timeout.InfiniteTimeSpan, localCancellation.Token);");
        StringAssert.Contains(window, "localCancellation.Cancel();");
        StringAssert.Contains(window, "!cancellationToken.IsCancellationRequested");
        StringAssert.Contains(window, "DisposeMediaSource(cancellationSource);");
        StringAssert.Contains(window, "MediaSource recoverySource = MediaSource.CreateFromUri(fixture);");
        StringAssert.Contains(window, "!ReferenceEquals(cancellationSource, recoverySource)");
        StringAssert.Contains(window, "DisposeMediaSource(recoverySource);");
        StringAssert.Contains(window, "CancellationNoAutomaticRestart");
        Assert.AreEqual(
            1,
            Regex.Count(
                window,
                @"\bnew\s+MediaPlayer\b",
                RegexOptions.CultureInvariant),
            "The cancellation/recovery probe must keep the single native MediaPlayer boundary.");
        int cancellationProbeHelperStart = cancellationProbeBoundaryIndex;
        int cancellationOperationHelperStart = window.IndexOf(
            "private async Task<NativePlaybackCancellationOperationResult> RunCancellationOperationAsync(",
            cancellationProbeHelperStart,
            StringComparison.Ordinal);
        int cancellationRecoveryHelperStart = window.IndexOf(
            "private async Task<NativePlaybackCancellationRecoveryResult> RunCancellationRecoveryAsync(",
            cancellationOperationHelperStart,
            StringComparison.Ordinal);
        int cancellationRecoveryGuardHelperStart = window.IndexOf(
            "private void ThrowIfCancellationRecoveryFailedOrChanged(MediaSource recoverySource)",
            cancellationRecoveryHelperStart,
            StringComparison.Ordinal);
        int soakHelperStart = window.IndexOf(
            "private async Task<NativePlaybackSoakMetrics> RunSoakAsync(",
            cancellationRecoveryGuardHelperStart,
            StringComparison.Ordinal);
        Assert.IsTrue(
            cancellationProbeHelperStart >= 0 &&
            cancellationProbeHelperStart < cancellationOperationHelperStart &&
            cancellationOperationHelperStart < cancellationRecoveryHelperStart &&
            cancellationRecoveryHelperStart < cancellationRecoveryGuardHelperStart &&
            cancellationRecoveryGuardHelperStart < soakHelperStart,
            "Cancellation, recovery, and source-identity guards must remain distinct helpers.");
        string cancellationProbeHelper =
            window[cancellationProbeHelperStart..cancellationOperationHelperStart];
        string cancellationOperationHelper =
            window[cancellationOperationHelperStart..cancellationRecoveryHelperStart];
        string cancellationRecoveryHelper =
            window[cancellationRecoveryHelperStart..cancellationRecoveryGuardHelperStart];
        string normalizedCancellationOperationHelper = Regex.Replace(
            cancellationOperationHelper,
            @"\s+",
            " ",
            RegexOptions.CultureInvariant);

        Assert.IsFalse(
            cancellationProbeHelper.Contains("_opened.Task", StringComparison.Ordinal) ||
            cancellationProbeHelper.Contains("_advanced.Task", StringComparison.Ordinal) ||
            Regex.IsMatch(
                cancellationOperationHelper,
                @"\b_(?:opened|advanced)\b",
                RegexOptions.CultureInvariant) ||
            Regex.IsMatch(
                cancellationRecoveryHelper,
                @"\b_(?:opened|advanced)\b",
                RegexOptions.CultureInvariant),
            "Cancellation and recovery must not consume the switch loop's global completion TCSs.");
        Assert.IsFalse(
            cancellationProbeHelper.Contains(
                "controlledObservationMilliseconds = 250",
                StringComparison.Ordinal),
            "The obsolete 250 ms cancellation observation must not return.");

        StringAssert.Contains(
            cancellationOperationHelper,
            "CancellationTokenSource.CreateLinkedTokenSource(cancellationToken)");
        StringAssert.Contains(
            normalizedCancellationOperationHelper,
            "if (!ReferenceEquals(_mediaPlayer.Source, cancellationSource) || sourceAssignmentCount != 1 || playInvocationCount != 1)");
        StringAssert.Contains(
            normalizedCancellationOperationHelper,
            "exception.CancellationToken == localCancellation.Token && localCancellation.IsCancellationRequested && !cancellationToken.IsCancellationRequested");
        int localCancellationCreationIndex = cancellationOperationHelper.IndexOf(
            "CancellationTokenSource.CreateLinkedTokenSource(cancellationToken)",
            StringComparison.Ordinal);
        int cancellationWaitIndex = cancellationOperationHelper.IndexOf(
            "Task cancellationWait = Task.Delay(Timeout.InfiniteTimeSpan, localCancellation.Token);",
            localCancellationCreationIndex,
            StringComparison.Ordinal);
        int cancellationOuterPreconditionIndex = cancellationOperationHelper.IndexOf(
            "cancellationToken.ThrowIfCancellationRequested();",
            cancellationWaitIndex,
            StringComparison.Ordinal);
        int cancellationSourceAssignmentIndex = cancellationOperationHelper.IndexOf(
            "_mediaPlayer.Source = cancellationSource;",
            cancellationOuterPreconditionIndex,
            StringComparison.Ordinal);
        int sourceAssignmentCountIndex = cancellationOperationHelper.IndexOf(
            "sourceAssignmentCount++;",
            cancellationSourceAssignmentIndex,
            StringComparison.Ordinal);
        int cancellationPlayIndex = cancellationOperationHelper.IndexOf(
            "_mediaPlayer.Play();",
            sourceAssignmentCountIndex,
            StringComparison.Ordinal);
        int playInvocationCountIndex = cancellationOperationHelper.IndexOf(
            "playInvocationCount++;",
            cancellationPlayIndex,
            StringComparison.Ordinal);
        int cancellationPreconditionIndex = cancellationOperationHelper.IndexOf(
            "if (!ReferenceEquals(_mediaPlayer.Source, cancellationSource)",
            playInvocationCountIndex,
            StringComparison.Ordinal);
        int sourceAssignmentSnapshotIndex = cancellationOperationHelper.IndexOf(
            "int sourceAssignmentCountAtCancellation = sourceAssignmentCount;",
            cancellationPreconditionIndex,
            StringComparison.Ordinal);
        int playInvocationSnapshotIndex = cancellationOperationHelper.IndexOf(
            "int playInvocationCountAtCancellation = playInvocationCount;",
            sourceAssignmentSnapshotIndex,
            StringComparison.Ordinal);
        int cancellationTimestampIndex = cancellationOperationHelper.IndexOf(
            "long cancellationRequested = Stopwatch.GetTimestamp();",
            playInvocationSnapshotIndex,
            StringComparison.Ordinal);
        int cancellationRequestIndex = cancellationOperationHelper.IndexOf(
            "localCancellation.Cancel();",
            cancellationTimestampIndex,
            StringComparison.Ordinal);
        int cancellationAwaitIndex = cancellationOperationHelper.IndexOf(
            "await cancellationWait;",
            cancellationRequestIndex,
            StringComparison.Ordinal);
        int cancellationDetachIndex = cancellationOperationHelper.IndexOf(
            "double sourceDetachMilliseconds = await DetachSourceAsync(",
            cancellationAwaitIndex,
            StringComparison.Ordinal);
        int cancellationDisposeIndex = cancellationOperationHelper.IndexOf(
            "DisposeMediaSource(cancellationSource);",
            cancellationDetachIndex,
            StringComparison.Ordinal);
        int cancellationObservationIndex = cancellationOperationHelper.IndexOf(
            "var observation = Stopwatch.StartNew();",
            cancellationDisposeIndex,
            StringComparison.Ordinal);
        int cancellationObservationLoopIndex = cancellationOperationHelper.IndexOf(
            "while (observation.Elapsed < observationTarget)",
            cancellationObservationIndex,
            StringComparison.Ordinal);
        int cancellationNoRestartIndex = cancellationOperationHelper.IndexOf(
            "bool noAutomaticRestart = sourceNullAfterObservation && operationCountsUnchanged;",
            cancellationObservationLoopIndex,
            StringComparison.Ordinal);
        Assert.IsTrue(
            localCancellationCreationIndex >= 0 &&
            localCancellationCreationIndex < cancellationWaitIndex &&
            cancellationWaitIndex < cancellationOuterPreconditionIndex &&
            cancellationOuterPreconditionIndex < cancellationSourceAssignmentIndex &&
            cancellationSourceAssignmentIndex < sourceAssignmentCountIndex &&
            sourceAssignmentCountIndex < cancellationPlayIndex &&
            cancellationPlayIndex < playInvocationCountIndex &&
            playInvocationCountIndex < cancellationPreconditionIndex &&
            cancellationPreconditionIndex < sourceAssignmentSnapshotIndex &&
            sourceAssignmentSnapshotIndex < playInvocationSnapshotIndex &&
            playInvocationSnapshotIndex < cancellationTimestampIndex &&
            cancellationTimestampIndex < cancellationRequestIndex &&
            cancellationRequestIndex < cancellationAwaitIndex &&
            cancellationRequestIndex < cancellationDetachIndex &&
            cancellationDetachIndex < cancellationDisposeIndex &&
            cancellationDisposeIndex < cancellationObservationIndex &&
            cancellationObservationIndex < cancellationObservationLoopIndex &&
            cancellationObservationLoopIndex < cancellationNoRestartIndex,
            "App-owned cancellation must be preconditioned and timestamped before exact teardown and observation.");
        int cancellationDetachCallEnd = cancellationOperationHelper.IndexOf(
            "sourceDetached = true;",
            cancellationDetachIndex,
            StringComparison.Ordinal);
        Assert.IsTrue(cancellationDetachCallEnd > cancellationDetachIndex);
        string cancellationDetachCall =
            cancellationOperationHelper[cancellationDetachIndex..cancellationDetachCallEnd];
        StringAssert.Contains(cancellationDetachCall, "cancellationToken);");
        Assert.IsFalse(
            cancellationDetachCall.Contains("localCancellation.Token", StringComparison.Ordinal),
            "The cancelled operation token must not control exact source teardown.");

        const string operationCountsInvariantPattern =
            @"sourceAssignmentCount\s*==\s*sourceAssignmentCountAtCancellation\s*&&\s*" +
            @"playInvocationCount\s*==\s*playInvocationCountAtCancellation";
        MatchCollection operationCountsInvariantMatches = Regex.Matches(
            cancellationOperationHelper,
            operationCountsInvariantPattern,
            RegexOptions.CultureInvariant);
        MatchCollection sourceNullObservationUpdates = Regex.Matches(
            cancellationOperationHelper,
            Regex.Escape("sourceRemainedNull &= _mediaPlayer.Source is null;"),
            RegexOptions.CultureInvariant);
        Assert.AreEqual(
            3,
            operationCountsInvariantMatches.Count,
            "Assignment and Play counts must be sampled before, during, and after observation.");
        Assert.AreEqual(
            2,
            sourceNullObservationUpdates.Count,
            "Source nullness must be sampled inside and after the observation loop.");
        Assert.IsTrue(
            operationCountsInvariantMatches[0].Index < cancellationObservationLoopIndex &&
            cancellationObservationLoopIndex < sourceNullObservationUpdates[0].Index &&
            sourceNullObservationUpdates[0].Index < operationCountsInvariantMatches[1].Index &&
            operationCountsInvariantMatches[1].Index < sourceNullObservationUpdates[1].Index &&
            sourceNullObservationUpdates[1].Index < operationCountsInvariantMatches[2].Index &&
            operationCountsInvariantMatches[2].Index < cancellationNoRestartIndex,
            "The no-restart proof must retain invariant samples across the complete observation window.");

        Assert.IsFalse(
            Regex.IsMatch(
                cancellationRecoveryHelper,
                @"\b_(?:opened|advanced)\b",
                RegexOptions.CultureInvariant),
            "Recovery must use source-owned completion and position observations.");
        StringAssert.Contains(
            cancellationRecoveryHelper,
            "new TaskCompletionSource<NativePlaybackSourceOpenCompletion>(");
        StringAssert.Contains(
            cancellationRecoveryHelper,
            "if (ReferenceEquals(sender, recoverySource))");
        StringAssert.Contains(
            cancellationRecoveryHelper,
            "args.Error is not null");
        int recoverySourceIndex = cancellationRecoveryHelper.IndexOf(
            "MediaSource recoverySource = MediaSource.CreateFromUri(fixture);",
            StringComparison.Ordinal);
        int recoveryHandlerIndex = cancellationRecoveryHelper.IndexOf(
            "void RecoverySource_OpenOperationCompleted(",
            recoverySourceIndex,
            StringComparison.Ordinal);
        int recoverySourceBoundIndex = cancellationRecoveryHelper.IndexOf(
            "if (ReferenceEquals(sender, recoverySource))",
            recoveryHandlerIndex,
            StringComparison.Ordinal);
        int recoveryOpenTimestampIndex = cancellationRecoveryHelper.IndexOf(
            "Stopwatch.GetTimestamp(),",
            recoverySourceBoundIndex,
            StringComparison.Ordinal);
        int recoveryOpenErrorCaptureIndex = cancellationRecoveryHelper.IndexOf(
            "args.Error is not null",
            recoveryOpenTimestampIndex,
            StringComparison.Ordinal);
        int recoveryOpenSubscribeIndex = cancellationRecoveryHelper.IndexOf(
            "recoverySource.OpenOperationCompleted += RecoverySource_OpenOperationCompleted;",
            recoveryOpenErrorCaptureIndex,
            StringComparison.Ordinal);
        int recoverySourceAssignmentIndex = cancellationRecoveryHelper.IndexOf(
            "_mediaPlayer.Source = recoverySource;",
            recoveryOpenSubscribeIndex,
            StringComparison.Ordinal);
        int recoveryPlayIndex = cancellationRecoveryHelper.IndexOf(
            "_mediaPlayer.Play();",
            recoverySourceAssignmentIndex,
            StringComparison.Ordinal);
        int recoveryOpenWaitIndex = cancellationRecoveryHelper.IndexOf(
            "openCompletion = await sourceOpenCompletion.Task.WaitAsync(",
            recoveryPlayIndex,
            StringComparison.Ordinal);
        int recoveryOpenErrorCheckIndex = cancellationRecoveryHelper.IndexOf(
            "if (openCompletion.ErrorPresent)",
            recoveryOpenWaitIndex,
            StringComparison.Ordinal);
        int recoveryCurrentSourceCheckIndex = cancellationRecoveryHelper.IndexOf(
            "ThrowIfCancellationRecoveryFailedOrChanged(recoverySource);",
            recoveryOpenErrorCheckIndex,
            StringComparison.Ordinal);
        int recoveryPositionBaselineIndex = cancellationRecoveryHelper.IndexOf(
            "positionBaseline = _mediaPlayer.PlaybackSession.Position;",
            recoveryCurrentSourceCheckIndex,
            StringComparison.Ordinal);
        int recoveryAdvanceLoopIndex = cancellationRecoveryHelper.IndexOf(
            "while (true)",
            recoveryPositionBaselineIndex,
            StringComparison.Ordinal);
        int recoveryLoopCurrentSourceCheckIndex = cancellationRecoveryHelper.IndexOf(
            "ThrowIfCancellationRecoveryFailedOrChanged(recoverySource);",
            recoveryAdvanceLoopIndex,
            StringComparison.Ordinal);
        int recoveryPositionIndex = cancellationRecoveryHelper.IndexOf(
            "position = _mediaPlayer.PlaybackSession.Position;",
            recoveryLoopCurrentSourceCheckIndex,
            StringComparison.Ordinal);
        int recoveryProgressIndex = cancellationRecoveryHelper.IndexOf(
            "position - positionBaseline >= TimeSpan.FromMilliseconds(500)",
            recoveryPositionIndex,
            StringComparison.Ordinal);
        int recoveryOpenUnsubscribeIndex = cancellationRecoveryHelper.IndexOf(
            "UnsubscribeSourceOpenHandler();",
            recoveryProgressIndex,
            StringComparison.Ordinal);
        int recoveryDetachIndex = cancellationRecoveryHelper.IndexOf(
            "double sourceDetachMilliseconds = await DetachSourceAsync(",
            recoveryOpenUnsubscribeIndex,
            StringComparison.Ordinal);
        int recoveryDisposeIndex = cancellationRecoveryHelper.IndexOf(
            "DisposeMediaSource(recoverySource);",
            recoveryDetachIndex,
            StringComparison.Ordinal);
        int recoveryFinallyUnsubscribeIndex = cancellationRecoveryHelper.LastIndexOf(
            "UnsubscribeSourceOpenHandler();",
            StringComparison.Ordinal);
        int recoveryBestEffortResetIndex = cancellationRecoveryHelper.IndexOf(
            "BestEffortResetAfterProbe();",
            recoveryFinallyUnsubscribeIndex,
            StringComparison.Ordinal);
        int recoveryBestEffortDisposeIndex = cancellationRecoveryHelper.IndexOf(
            "BestEffortDisposeMediaSource(recoverySource);",
            recoveryFinallyUnsubscribeIndex,
            StringComparison.Ordinal);
        Assert.AreEqual(
            2,
            Regex.Count(
                cancellationRecoveryHelper,
                Regex.Escape("UnsubscribeSourceOpenHandler();"),
                RegexOptions.CultureInvariant),
            "Success and finally paths must both unbind the recovery source-open handler.");
        Assert.AreEqual(
            2,
            Regex.Count(
                cancellationRecoveryHelper,
                Regex.Escape("ThrowIfCancellationRecoveryFailedOrChanged(recoverySource);"),
                RegexOptions.CultureInvariant),
            "Recovery must prove current-source identity before baseline and every progress sample.");
        Assert.IsTrue(
            recoverySourceIndex >= 0 &&
            recoverySourceIndex < recoveryHandlerIndex &&
            recoveryHandlerIndex < recoverySourceBoundIndex &&
            recoverySourceBoundIndex < recoveryOpenTimestampIndex &&
            recoveryOpenTimestampIndex < recoveryOpenErrorCaptureIndex &&
            recoveryOpenErrorCaptureIndex < recoveryOpenSubscribeIndex &&
            recoveryOpenSubscribeIndex < recoverySourceAssignmentIndex &&
            recoverySourceAssignmentIndex < recoveryPlayIndex &&
            recoveryPlayIndex < recoveryOpenWaitIndex &&
            recoveryOpenWaitIndex < recoveryOpenErrorCheckIndex &&
            recoveryOpenErrorCheckIndex < recoveryCurrentSourceCheckIndex &&
            recoveryCurrentSourceCheckIndex < recoveryPositionBaselineIndex &&
            recoveryPositionBaselineIndex < recoveryAdvanceLoopIndex &&
            recoveryAdvanceLoopIndex < recoveryLoopCurrentSourceCheckIndex &&
            recoveryLoopCurrentSourceCheckIndex < recoveryPositionIndex &&
            recoveryPositionIndex < recoveryProgressIndex &&
            recoveryProgressIndex < recoveryOpenUnsubscribeIndex &&
            recoveryOpenUnsubscribeIndex < recoveryDetachIndex &&
            recoveryDetachIndex < recoveryDisposeIndex &&
            recoveryDisposeIndex < recoveryFinallyUnsubscribeIndex &&
            recoveryFinallyUnsubscribeIndex < recoveryBestEffortResetIndex &&
            recoveryFinallyUnsubscribeIndex < recoveryBestEffortDisposeIndex,
            "Recovery must bind source-local open evidence before assignment, prove exact-source progress, and unbind before teardown.");

        StringAssert.Contains(
            window,
            "internal readonly record struct NativePlaybackCancellationOperationResult(");
        StringAssert.Contains(
            window,
            "internal readonly record struct NativePlaybackCancellationRecoveryResult(");
        StringAssert.Contains(
            window,
            "internal readonly record struct NativePlaybackCancellationMetrics(");
        StringAssert.Contains(
            cancellationProbeHelper,
            "result => cancellationOperation = result,");
        StringAssert.Contains(
            cancellationProbeHelper,
            "result => recovery = result,");
        StringAssert.Contains(
            cancellationProbeHelper,
            "CreateCancellationMetrics(cancellationOperation, recovery));");
        StringAssert.Contains(
            window,
            "NativePlaybackCancellationMetrics metrics = default) : Exception");
        StringAssert.Contains(
            window,
            "internal NativePlaybackCancellationMetrics Metrics { get; } = metrics;");
        Assert.IsFalse(
            window.Contains("NativePlaybackCancellationMetrics Metrics { get; set; }", StringComparison.Ordinal),
            "Partial cancellation diagnostics must remain immutable once attached to the typed exception.");
        int cancellationFailureCatchIndex = window.IndexOf(
            "catch (NativePlaybackCancellationException exception)",
            StringComparison.Ordinal);
        int cancellationFailureCatchEnd = window.IndexOf(
            "catch (InvalidOperationException)",
            cancellationFailureCatchIndex,
            StringComparison.Ordinal);
        Assert.IsTrue(
            cancellationFailureCatchIndex >= 0 &&
            cancellationFailureCatchIndex < cancellationFailureCatchEnd &&
            cancellationFailureCatchEnd < cancellationProbeBoundaryIndex,
            "The top-level probe must preserve typed cancellation evidence before helper declarations.");
        string normalizedCancellationFailureCatch = Regex.Replace(
            window[cancellationFailureCatchIndex..cancellationFailureCatchEnd],
            @"\s+",
            " ",
            RegexOptions.CultureInvariant);
        StringAssert.Contains(
            normalizedCancellationFailureCatch,
            "cancellationMetrics = exception.Metrics; detachedSourceCount += cancellationMetrics.CancellationSourceDetachCount + cancellationMetrics.CancellationRecoverySourceDetachCount;");
        StringAssert.Contains(
            normalizedCancellationFailureCatch,
            "detachedSourceCount: detachedSourceCount");

        int cancellationInvocationIndex = window.IndexOf(
            "cancellationMetrics = await RunCancellationProbeAsync(",
            switchLoopIndex,
            StringComparison.Ordinal);
        int optionalSoakIndex = window.IndexOf(
            "NativePlaybackSoakMetrics soakMetrics = NativePlaybackSoakMetrics.None;",
            cancellationInvocationIndex,
            StringComparison.Ordinal);
        Assert.IsTrue(
            cancellationInvocationIndex > switchLoopIndex && optionalSoakIndex > cancellationInvocationIndex,
            "Cancellation/recovery must run after the measured switch loop and before optional soak.");
        StringAssert.Contains(app, "JsonStringEnumConverter");
        Assert.IsFalse(
            window.Contains("fixture.ToString()", StringComparison.Ordinal) ||
            window.Contains("AbsoluteUri", StringComparison.Ordinal) ||
            window.Contains("args.ErrorMessage", StringComparison.Ordinal) ||
            window.Contains("args.Error.ExtendedError", StringComparison.Ordinal),
            "The native playback evidence path must not serialize a locator or native diagnostic text.");
    }

    [TestMethod]
    public void NativePlaybackSmokeUsesDisposableSignedPackageAndTlsLoopbackAllowlist()
    {
        string controller = File.ReadAllText(Path.Combine(
            RepositoryRoot,
            "eng",
            "Invoke-WindowsNativePlaybackSmoke.ps1"));

        StringAssert.Contains(controller, "NativePlaybackCompatibilitySpike.Local.a47d1387");
        StringAssert.Contains(controller, "PackageCertificateThumbprint");
        StringAssert.Contains(controller, "Cert:\\LocalMachine\\TrustedPeople");
        StringAssert.Contains(controller, "New-SelfSignedCertificate -DnsName \"localhost\"");
        StringAssert.Contains(controller, "Import-Certificate -FilePath $tlsCertificatePath -CertStoreLocation \"Cert:\\LocalMachine\\Root\"");
        StringAssert.Contains(controller, "Tls12LoopbackAllowlist");
        StringAssert.Contains(controller, "new TcpListener(IPAddress.Loopback, 0)");
        StringAssert.Contains(controller, "case \"/direct-h264-aac.ts\"");
        StringAssert.Contains(controller, "case \"/hls.m3u8\"");
        StringAssert.Contains(controller, "Range: bytes=");
        StringAssert.Contains(controller, "{62CE7E72-4C71-4D20-B15D-452831A87D9D}");
        StringAssert.Contains(controller, "{32D186A7-218F-4C75-8876-DD77273A8999}");
        StringAssert.Contains(controller, "StartupP95Milliseconds -gt 3000");
        StringAssert.Contains(controller, "StartupMaximumMilliseconds -gt 5000");
        StringAssert.Contains(controller, "[ValidateRange(0, 480)]");
        StringAssert.Contains(controller, "$SwitchCount -ne 100");
        StringAssert.Contains(controller, "MemoryNetGrowthBytes -gt 104857600");
        StringAssert.Contains(controller, "MemoryNetGrowthPercent -gt 10");
        StringAssert.Contains(controller, "$expectedSurfaceTransitions = if ($SwitchCount -ge 25) { 6 } else { 0 }");
        StringAssert.Contains(controller, "SurfaceTransitionCount = [int]$probe.SurfaceTransitionCount");
        StringAssert.Contains(controller, "[ValidateRange(0, 7)]");
        StringAssert.Contains(controller, "$NetworkInterruptionCount -gt 0 -and $SwitchCount -ne 100");
        StringAssert.Contains(controller, "ArmNextMediaRequestFailure");
        StringAssert.Contains(controller, "Interlocked.Exchange(ref armedMediaFailure, 0) == 1");
        StringAssert.Contains(controller, "int requestOrdinal = Interlocked.Increment(ref requestCount)");
        StringAssert.Contains(controller, "requestOrdinal > injectedRequestOrdinal");
        StringAssert.Contains(controller, "Interlocked.CompareExchange(ref pendingRecovery, 0, 1) == 1");
        StringAssert.Contains(controller, "$tlsServer.Dispose()");
        StringAssert.Contains(controller, "$tlsRequestCount = $tlsServer.RequestCount");
        StringAssert.Contains(controller, "$tlsServer = $null");
        StringAssert.Contains(controller, "$tlsLastRecoveryRequestOrdinal -le $tlsLastInjectedRequestOrdinal");
        StringAssert.Contains(controller, "NetworkInterruptionCount = $tlsInjectedFailureCount");
        StringAssert.Contains(controller, "NetworkRecoveryCount = $tlsRecoveryCount");
        StringAssert.Contains(controller, "$expectedDetachedSourceCount =");
        StringAssert.Contains(controller, "$cancellationSourceDetachCount +");
        StringAssert.Contains(controller, "$cancellationRecoverySourceDetachCount");
        StringAssert.Contains(controller, "[int]$probe.DetachedSourceCount -ne $expectedDetachedSourceCount");
        StringAssert.Contains(controller, "DetachedSourceCount = [int]$probe.DetachedSourceCount");
        StringAssert.Contains(controller, "SourceDetachP95Milliseconds -gt 3000");
        StringAssert.Contains(controller, "SourceDetachMaximumMilliseconds -gt 5000");
        StringAssert.Contains(controller, "[int]$probe.PlaybackRetryCount -gt $NetworkInterruptionCount");
        StringAssert.Contains(controller, "PlaybackRetryCount = [int]$probe.PlaybackRetryCount");
        StringAssert.Contains(controller, "SchemaVersion = 10");
        StringAssert.Contains(controller, "[int]$probeEnvelope.SchemaVersion -ne 5");
        StringAssert.Contains(controller, "$probeEnvelopeSchemaVersion = 5");
        StringAssert.Contains(controller, "[ValidateRange(0, 1)]");
        StringAssert.Contains(controller, "$CancellationProbeCount -gt 0 -and");
        StringAssert.Contains(controller, "$cancellationProbeCount -ne $CancellationProbeCount");
        StringAssert.Contains(controller, "$cancellationObservedCount -ne 1");
        StringAssert.Contains(controller, "$cancellationQuiescenceMilliseconds -gt 1000");
        StringAssert.Contains(controller, "$cancellationObservationMilliseconds -lt 1000");
        StringAssert.Contains(controller, "$cancellationObservationMilliseconds -gt 1500");
        StringAssert.Contains(
            controller,
            "$cancellationLatencyMilliseconds + $cancellationSourceDetachMilliseconds");
        StringAssert.Contains(
            controller,
            "$cancellationQuiescenceMilliseconds + $cancellationObservationMilliseconds");
        StringAssert.Contains(controller, "$cancellationRecoveryStartupMilliseconds -gt 5000");
        StringAssert.Contains(controller, "$cancellationRecoveryAdvanceMilliseconds -gt 3000");
        StringAssert.Contains(controller, "-not $cancellationSourceNullAfterObservation");
        StringAssert.Contains(controller, "-not $cancellationRecoveryUsedFreshSource");
        StringAssert.Contains(controller, "-not $cancellationNoAutomaticRestart");
        StringAssert.Contains(controller, "\"StartupMaximumSourceOpen\"");
        StringAssert.Contains(controller, "\"StartupFailureSourceOpen\"");
        StringAssert.Contains(controller, "\"CompletionObserved\"");
        StringAssert.Contains(controller, "\"PostCompletionElapsedMilliseconds\"");
        StringAssert.Contains(controller, "$startupFailureStage -notin $allowedStartupFailureStages");
        StringAssert.Contains(controller, "\"MediaSourceOpenWait\"");
        StringAssert.Contains(controller, "$startupFailureSourceOpenObserved");
        StringAssert.Contains(controller, "$startupMaximumSourceOpenObserved");
        StringAssert.Contains(controller, "$startupFailureSwitchOrdinal -ne $expectedFailureSwitchOrdinal");
        StringAssert.Contains(controller, "$startupFailureAttemptCount -gt (1 + [int]$probe.PlaybackRetryCount)");
        StringAssert.Contains(controller, "$startupFailureSurfaceTransitionCount -ne $expectedFailureSurfaceTransitionCount");
        StringAssert.Contains(controller, "$startupFailureStage -notin $allowedMediaOpenFailureStages");
        StringAssert.Contains(controller, "$startupFailureStage -ne \"PlaybackAdvanceWait\"");
        StringAssert.Contains(controller, "$startupMaximumSwitchOrdinal -lt 1");
        StringAssert.Contains(controller, "$startupMaximumSwitchOrdinal -gt $SwitchCount");
        StringAssert.Contains(controller, "($startupMaximumSwitchOrdinal % 2) -eq 1");
        StringAssert.Contains(
            controller,
            "$expectedMaximumFixture,");
        StringAssert.Contains(
            controller,
            "[System.StringComparison]::Ordinal) -or");
        StringAssert.Contains(controller, "$startupMaximumAttemptCount -lt 1");
        StringAssert.Contains(controller, "$startupMaximumAttemptCount -gt 2");
        StringAssert.Contains(
            controller,
            "$startupMaximumAttemptCount -gt (1 + [int]$probe.PlaybackRetryCount)");
        StringAssert.Contains(
            controller,
            "$startupMaximumSurfaceTransitionCount -ne $expectedMaximumSurfaceTransitionCount");
        StringAssert.Contains(controller, "$maximumSwitchIndex = $startupMaximumSwitchOrdinal - 1");
        StringAssert.Contains(
            controller,
            "$maximumSwitchIndex -eq [Math]::Floor($SwitchCount / 5.0)");
        StringAssert.Contains(
            controller,
            "$maximumSwitchIndex -eq [Math]::Floor(($SwitchCount * 2.0) / 5.0)");
        StringAssert.Contains(
            controller,
            "$maximumSwitchIndex -eq [Math]::Floor(($SwitchCount * 3.0) / 5.0)");
        StringAssert.Contains(
            controller,
            "$maximumSwitchIndex -eq [Math]::Floor(($SwitchCount * 4.0) / 5.0)");
        StringAssert.Contains(controller, "$startupMaximumMilliseconds) -gt 0.002");
        StringAssert.Contains(
            controller,
            "$hlsStartupMaximumMilliseconds -lt $hlsStartupP95Milliseconds");
        StringAssert.Contains(
            controller,
            "$directStartupMaximumMilliseconds -lt $directStartupP95Milliseconds");
        StringAssert.Contains(
            controller,
            "$hlsStartupMaximumMilliseconds -gt $startupMaximumMilliseconds");
        StringAssert.Contains(
            controller,
            "$directStartupMaximumMilliseconds -gt $startupMaximumMilliseconds");
        StringAssert.Contains(
            controller,
            "[Math]::Max($hlsStartupMaximumMilliseconds, $directStartupMaximumMilliseconds)");
        StringAssert.Contains(
            controller,
            "[Math]::Abs($hlsStartupMaximumMilliseconds - $startupMaximumMilliseconds) -gt 0.001");
        StringAssert.Contains(
            controller,
            "[Math]::Abs($directStartupMaximumMilliseconds - $startupMaximumMilliseconds) -gt 0.001");
        string probeFailureLog = controller
            .Split('\n')
            .Single(line => line.Contains(
                "Native playback probe failed with category",
                StringComparison.Ordinal));
        string startupBudgetFailureLog = controller
            .Split('\n')
            .Single(line => line.Contains(
                "Native playback startup budget failed",
                StringComparison.Ordinal));
        string startupDiagnosticLog = controller
            .Split('\n')
            .Single(line => line.Contains(
                "Native playback startup diagnostic:",
                StringComparison.Ordinal));
        string[] probeFailureDiagnosticLabels =
        [
            "startupFailureStage=",
            "startupFailureOrdinal=",
            "startupFailureFixture=",
            "startupFailureAttempts=",
            "startupFailureTransitions=",
            "startupFailureTotal=",
            "startupFailureSourceCreation=",
            "startupFailureSourceAssignment=",
            "startupFailurePlayInvocation=",
            "startupFailureSourceOpenObserved=",
            "startupFailureSourceOpenError=",
            "startupFailureSourceOpenCompletion=",
            "startupFailurePostSourceOpenElapsed=",
            "startupFailureActiveStageElapsed=",
            "startupMaximumOrdinal=",
            "startupMaximumFixture=",
            "startupMaximumAttempts=",
            "startupMaximumTransitions=",
            "startupMaximumPreWait=",
            "startupMaximumMediaOpenWait=",
            "startupMaximumSourceOpenObserved=",
            "startupMaximumSourceOpenError=",
            "startupMaximumSourceOpenCompletion=",
            "startupMaximumPostSourceOpenMediaOpened=",
            "hlsMaximum=",
            "directMaximum=",
        ];
        foreach (string diagnosticLabel in probeFailureDiagnosticLabels)
        {
            StringAssert.Contains(probeFailureLog, diagnosticLabel);
        }

        string[] startupBudgetDiagnosticLabels =
        [
            "maximumOrdinal=",
            "maximumFixture=",
            "maximumAttempts=",
            "maximumSurfaceTransitions=",
            "maximumPreWait=",
            "maximumMediaOpenWait=",
            "maximumSourceOpenObserved=",
            "maximumSourceOpenError=",
            "maximumSourceOpenCompletion=",
            "maximumPostSourceOpenMediaOpened=",
            "hlsMaximum=",
            "directMaximum=",
        ];
        foreach (string diagnosticLabel in startupBudgetDiagnosticLabels)
        {
            StringAssert.Contains(startupBudgetFailureLog, diagnosticLabel);
        }

        string[] startupDiagnosticLabels =
        [
            "ordinal=",
            "fixture=",
            "attempts=",
            "surfaceTransitions=",
            "preWait=",
            "mediaOpenWait=",
            "sourceOpenObserved=",
            "sourceOpenError=",
            "sourceOpenCompletion=",
            "postSourceOpenMediaOpened=",
            "hlsMaximum=",
            "directMaximum=",
        ];
        foreach (string diagnosticLabel in startupDiagnosticLabels)
        {
            StringAssert.Contains(startupDiagnosticLog, diagnosticLabel);
        }
        StringAssert.Contains(
            controller,
            "foreach ($staleEvidence in @($evidencePath, $failureEvidencePath, $packageInventoryEvidencePath))");
        StringAssert.Contains(controller, "if (@(Get-RepositoryStatus).Count -ne 0)");
        StringAssert.Contains(controller, "$repositoryHead = Get-RepositoryHead");
        StringAssert.Contains(controller, "$controllerScriptSha256 = Get-RegularFileSha256 -Path $PSCommandPath");
        StringAssert.Contains(controller, "Assert-FixtureCorpus");
        StringAssert.Contains(controller, "$fixtureManifestSha256 = Get-RegularFileSha256 -Path $fixtureManifestPath");
        StringAssert.Contains(controller, "ProbeEnvelopeSchemaVersion = $probeEnvelopeSchemaVersion");
        StringAssert.Contains(controller, "ProbeRunIdBound = $probeRunIdBound");
        StringAssert.Contains(controller, "Get-PackageEntrySha256");
        StringAssert.Contains(controller, "-EntryName \"IptvSuite.NativePlaybackCompatibilitySpike.dll\"");
        StringAssert.Contains(controller, "The native playback probe exited before the required normal close.");
        StringAssert.Contains(controller, "Close-TrackedProcessNormally -Process $launchedProcess");
        StringAssert.Contains(controller, "$script:forcedProcessTerminationUsed = $true");
        StringAssert.Contains(controller, "Get-AppxPackage -Name $script:expectedName -ErrorAction Stop");
        StringAssert.Contains(controller, "$dependencySignature.Status.ToString() -ne \"Valid\"");
        StringAssert.Contains(controller, "Task.WaitAll(handlers, TimeSpan.FromSeconds(5))");
        StringAssert.Contains(controller, "RemoveExactEmptyDirectory(");
        StringAssert.Contains(controller, "Test-RuntimeDependencyPackageGraph");
        StringAssert.Contains(controller, "Validate-RuntimeDependencyPackageState");
        StringAssert.Contains(controller, "$passiveDeadline = (Get-Date).AddSeconds(5)");
        StringAssert.Contains(controller, "$script:runtimeDependencyCleanupDiagnostic = \"PassiveGraphConvergence\"");
        StringAssert.Contains(controller, "$expectedRuntimeDependencyArchitectures = @(\"X64\", \"X86\")");
        StringAssert.Contains(controller, "$architectureAllowed = @($script:expectedRuntimeDependencyArchitectures | Where-Object");
        StringAssert.Contains(controller, "-not $architectureAllowed");
        StringAssert.Contains(controller, "$validatedAddedPackages = @()");
        StringAssert.Contains(controller, "$missingBaselineNames = @($beforeNames | Where-Object");
        StringAssert.Contains(controller, "MissingBaseline(count=$($missingBaselineNames.Count))");
        StringAssert.Contains(controller, "$x64SiblingMatches = @($currentPackages | Where-Object");
        StringAssert.Contains(controller, "[string]::Equals($_.Architecture.ToString(), \"X64\", [System.StringComparison]::Ordinal)");
        StringAssert.Contains(controller, "-not $x64SiblingMatches");
        StringAssert.Contains(controller, "$validatedAddedPackages += $package");
        StringAssert.Contains(controller, "$package.IsFramework -ne $true");
        StringAssert.Contains(controller, "$runtimeVersion.Major -ne 2");
        StringAssert.Contains(controller, "$runtimeVersion -lt [version]$script:expectedRuntimeDependencyVersion");
        StringAssert.Contains(controller, "Native runtime cleanup diagnostic: $($script:runtimeDependencyCleanupDiagnostic).");
        StringAssert.Contains(controller, "AddedPackage(version=$versionText;architecture=$architectureText;framework=$frameworkText;familyMatch=$familyMatches;x64Sibling=$x64SiblingMatches)");
        StringAssert.Contains(controller, "\"SharedAdditionsPreserved\"");
        StringAssert.Contains(controller, "RuntimePackageBaselinePreserved = $false");
        StringAssert.Contains(controller, "RuntimePackageGraphDisposition = \"NotValidated\"");
        StringAssert.Contains(controller, "RuntimePackageSharedAdditionCount = -1");
        StringAssert.Contains(controller, "Invoke-CleanupStep -Code \"RuntimeDependencyValidationFailed\"");
        StringAssert.Contains(controller, "PackageAppDataEmptyRootCleanupUsed = $false");
        StringAssert.Contains(controller, "Remove-ExactCertificate");
        StringAssert.Contains(controller, "Remove-ExactOwnedTree -Path $script:packageOutput -ExpectedParent $script:packagesRoot");
        StringAssert.Contains(controller, "[System.IO.FileMode]::CreateNew");
        StringAssert.Contains(controller, "$stream.Flush($true)");
        StringAssert.Contains(controller, "[System.IO.File]::Move($temporaryPath, $DestinationPath)");
        StringAssert.Contains(controller, "Write-JsonAtomically -Value $successCandidate -DestinationPath $evidencePath");
        StringAssert.Contains(controller, "@(Get-RepositoryStatus).Count -ne 0 -or (Get-RepositoryHead) -ne $repositoryHead");
        Assert.IsFalse(
            controller.Contains(
                "Remove-ExactOwnedTree -Path $script:packageAppDataPath",
                StringComparison.Ordinal) ||
            controller.Contains("Move-Item -LiteralPath $temporaryPath", StringComparison.Ordinal),
            "Package app-data cleanup must not recurse and evidence publication must not overwrite.");

        int runtimeGraphValidationStart = controller.IndexOf(
            "function Validate-RuntimeDependencyPackageState",
            StringComparison.Ordinal);
        int trackedProcessCloseStart = controller.IndexOf(
            "function Close-TrackedProcessNormally",
            runtimeGraphValidationStart,
            StringComparison.Ordinal);
        Assert.IsTrue(
            runtimeGraphValidationStart >= 0 && trackedProcessCloseStart > runtimeGraphValidationStart);
        string runtimeGraphValidation =
            controller[runtimeGraphValidationStart..trackedProcessCloseStart];
        StringAssert.Contains(runtimeGraphValidation, "$beforeNames.Count -gt 64");
        StringAssert.Contains(runtimeGraphValidation, "$currentPackages.Count -gt 64");
        StringAssert.Contains(runtimeGraphValidation, "$baselineNameSet.Add($baselineFullName)");
        StringAssert.Contains(
            runtimeGraphValidation,
            "$currentNameSet.Add([string]$currentPackage.PackageFullName)");
        StringAssert.Contains(
            runtimeGraphValidation,
            "$script:runtimePackageSharedAdditionCount = $validatedAddedPackages.Count");
        Assert.IsFalse(
            runtimeGraphValidation.Contains("Remove-AppxPackage", StringComparison.Ordinal) ||
            runtimeGraphValidation.Contains("FindPackagesForUser", StringComparison.Ordinal) ||
            runtimeGraphValidation.Contains("FindProvisionedPackages", StringComparison.Ordinal) ||
            runtimeGraphValidation.Contains("-AllUsers", StringComparison.Ordinal),
            "Shared runtime validation must remain bounded and non-mutating.");
        Assert.AreEqual(
            1,
            Regex.Count(controller, @"\bRemove-AppxPackage\b"),
            "Only the exact disposable native playback package may be removed.");

        int successCandidateIndex = controller.IndexOf("$successCandidate = [ordered]@{", StringComparison.Ordinal);
        int cleanupIndex = controller.LastIndexOf("\nfinally {", StringComparison.Ordinal);
        int forcedTerminationIndex = controller.IndexOf("$script:launchedProcess.Kill()", StringComparison.Ordinal);
        int successPublicationIndex = controller.IndexOf(
            "Write-JsonAtomically -Value $successCandidate -DestinationPath $evidencePath",
            StringComparison.Ordinal);
        Assert.IsTrue(successCandidateIndex >= 0 && cleanupIndex > successCandidateIndex);
        Assert.IsTrue(forcedTerminationIndex > cleanupIndex && successPublicationIndex > forcedTerminationIndex);

        int successCandidateEnd = controller.IndexOf("\n    }\n}", successCandidateIndex, StringComparison.Ordinal);
        Assert.IsTrue(successCandidateEnd > successCandidateIndex);
        string successEvidence = controller[successCandidateIndex..successCandidateEnd];
        string[] expectedEvidenceKeys =
        [
            "SchemaVersion",
            "Stage",
            "Result",
            "RunId",
            "CompletedAtUtc",
            "Configuration",
            "Platform",
            "DotNetSdk",
            "CleanHeadBound",
            "CommitSha",
            "ControllerScriptSha256",
            "HarnessAssemblySha256",
            "FixtureManifestSha256",
            "FixtureCorpusVerified",
            "ProbeEnvelopeSchemaVersion",
            "ProbeRunIdBound",
            "SwitchCount",
            "StartupP95Milliseconds",
            "StartupMaximumMilliseconds",
            "HlsStartupP95Milliseconds",
            "DirectStartupP95Milliseconds",
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
            "NetworkInterruptionCount",
            "NetworkRecoveryCount",
            "LastInjectedRequestOrdinal",
            "LastRecoveryRequestOrdinal",
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
            "InitialPrivateBytes",
            "FinalPrivateBytes",
            "InitialHandleCount",
            "FinalHandleCount",
            "LoopbackRequestCount",
            "H264DecoderRegistered",
            "AacDecoderRegistered",
            "Transport",
            "Fixtures",
            "PackageSha256",
            "PackageSignatureStatus",
            "RuntimeDependencyPackageSha256",
            "RuntimeDependencyPackageSignatureStatus",
            "ResolvedWindowsAppRuntimeName",
            "ResolvedWindowsAppRuntimeVersion",
            "ResolvedWindowsAppRuntimeArchitecture",
            "ResolvedWindowsAppRuntimePublisherId",
            "ResolvedWindowsAppRuntimeIsFramework",
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
            "RepositoryCleanAfterRun",
        ];
        string[] actualEvidenceKeys = Regex.Matches(
                successEvidence,
                @"(?m)^\s{8}([A-Za-z][A-Za-z0-9]*)\s*=")
            .Select(match => match.Groups[1].Value)
            .ToArray();
        CollectionAssert.AreEqual(
            expectedEvidenceKeys,
            actualEvidenceKeys,
            "Native playback success evidence must remain an exact allowlist.");

        foreach (string sensitiveToken in new[]
                 {
                     "$authority",
                     "$aumid",
                     "$installedPackage",
                     "$packageAppDataPath",
                     "$signingCertificate",
                     "$tlsCertificate",
                     "$packageEvidencePath",
                 })
        {
            Assert.IsFalse(
                successEvidence.Contains(sensitiveToken, StringComparison.Ordinal),
                $"Native playback success evidence must not contain sensitive token {sensitiveToken}.");
        }

        int failureEvidenceIndex = controller.IndexOf("$failureEvidence = [ordered]@{", StringComparison.Ordinal);
        int failureEvidenceEnd = controller.IndexOf("\n    }", failureEvidenceIndex, StringComparison.Ordinal);
        Assert.IsTrue(failureEvidenceIndex >= 0 && failureEvidenceEnd > failureEvidenceIndex);
        string[] failureEvidenceKeys = Regex.Matches(
                controller[failureEvidenceIndex..failureEvidenceEnd],
                @"(?m)^\s{8}([A-Za-z][A-Za-z0-9]*)\s*=")
            .Select(match => match.Groups[1].Value)
            .ToArray();
        string[] expectedFailureEvidenceKeys = ["Stage", "Code"];
        CollectionAssert.AreEqual(expectedFailureEvidenceKeys, failureEvidenceKeys);
        string readme = File.ReadAllText(Path.Combine(RepositoryRoot, "apps", "windows", "README.md"));
        StringAssert.Contains(
            readme,
            "-SwitchCount 100 -SoakMinutes 480 -NetworkInterruptionCount 7");
        StringAssert.Contains(controller, "Assert-PackagePayload");
        StringAssert.Contains(controller, "Add-Type -AssemblyName System.IO.Compression.FileSystem -ErrorAction Stop");
        StringAssert.Contains(controller, "Remove-ExactPackage");
        StringAssert.Contains(controller, "Cert:\\LocalMachine\\Root\\$($script:tlsCertificate.Thumbprint)");
        Assert.IsFalse(
            controller.Contains("continue-on-error", StringComparison.OrdinalIgnoreCase) ||
            controller.Contains("http://localhost", StringComparison.OrdinalIgnoreCase),
            "The native playback smoke must fail closed and keep TLS on loopback.");

        string contractProbe = Path.Combine(
            RepositoryRoot,
            "apps",
            "windows",
            "tests",
            "IptvSuite.ArchitectureTests",
            "Test-NativePlaybackEvidenceContract.ps1");
        string windowsPowerShell = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            "WindowsPowerShell",
            "v1.0",
            "powershell.exe");
        Assert.IsTrue(File.Exists(windowsPowerShell), "Windows PowerShell 5.1 is required for the controller contract.");
        ProcessStartInfo startInfo = new()
        {
            FileName = windowsPowerShell,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        startInfo.ArgumentList.Add("-NoLogo");
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-NonInteractive");
        startInfo.ArgumentList.Add("-ExecutionPolicy");
        startInfo.ArgumentList.Add("Bypass");
        startInfo.ArgumentList.Add("-File");
        startInfo.ArgumentList.Add(contractProbe);
        startInfo.ArgumentList.Add("-ControllerPath");
        startInfo.ArgumentList.Add(Path.Combine(RepositoryRoot, "eng", "Invoke-WindowsNativePlaybackSmoke.ps1"));

        using Process contractProcess = Process.Start(startInfo)
            ?? throw new AssertFailedException("The PowerShell 5.1 contract probe could not start.");
        bool contractCompleted = contractProcess.WaitForExit(30_000);
        if (!contractCompleted)
        {
            contractProcess.Kill(entireProcessTree: true);
            contractProcess.WaitForExit();
        }

        string contractOutput = contractProcess.StandardOutput.ReadToEnd();
        string contractError = contractProcess.StandardError.ReadToEnd();
        Assert.IsTrue(
            contractCompleted && contractProcess.ExitCode == 0,
            $"Native playback evidence AST contract failed.{Environment.NewLine}{contractOutput}{contractError}");
    }

    [TestMethod]
    public void NativePlaybackPackageInventoryIsExactAndFailsClosed()
    {
        string controller = File.ReadAllText(Path.Combine(
            RepositoryRoot,
            "eng",
            "Invoke-WindowsNativePlaybackSmoke.ps1"));
        string workflow = File.ReadAllText(Path.Combine(
            RepositoryRoot,
            ".github",
            "workflows",
            "windows-quality.yml"));
        string validator = Path.Combine(
            RepositoryRoot,
            "eng",
            "Test-WindowsNativePlaybackPackageInventory.ps1");
        string specification = Path.Combine(
            RepositoryRoot,
            "apps",
            "windows",
            "tests",
            "IptvSuite.NativePlaybackCompatibilitySpike",
            "package-inventory.json");

        Assert.IsTrue(File.Exists(validator));
        Assert.IsTrue(File.Exists(specification));
        using JsonDocument inventorySpecification = JsonDocument.Parse(File.ReadAllText(specification));
        string[] allowedPackageEntries = inventorySpecification.RootElement
            .GetProperty("AllowedPackageEntries")
            .EnumerateArray()
            .Select(entry => entry.GetString() ?? string.Empty)
            .ToArray();
        CollectionAssert.Contains(allowedPackageEntries, "AppxMetadata/CodeIntegrity.cat");
        CollectionAssert.Contains(allowedPackageEntries, "AppxSignature.p7x");
        StringAssert.Contains(controller, "Set-FailurePoint -Stage \"PackageInventory\" -Code \"PackageInventoryMismatch\"");
        StringAssert.Contains(controller, "& $inventoryValidatorPath `");
        StringAssert.Contains(controller, "-SpecificationPath $inventorySpecificationPath `");
        StringAssert.Contains(controller, "-EvidencePath $packageInventoryEvidencePath");
        StringAssert.Contains(workflow, ".artifacts/native-playback-smoke/package-inventory.json");

        string selfTest = Path.Combine(
            RepositoryRoot,
            "apps",
            "windows",
            "tests",
            "IptvSuite.ArchitectureTests",
            "Test-NativePlaybackPackageInventory.ps1");
        string windowsPowerShell = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            "WindowsPowerShell",
            "v1.0",
            "powershell.exe");
        Assert.IsTrue(File.Exists(windowsPowerShell), "Windows PowerShell 5.1 is required for the inventory contract.");
        ProcessStartInfo startInfo = new()
        {
            FileName = windowsPowerShell,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        startInfo.ArgumentList.Add("-NoLogo");
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-NonInteractive");
        startInfo.ArgumentList.Add("-ExecutionPolicy");
        startInfo.ArgumentList.Add("Bypass");
        startInfo.ArgumentList.Add("-File");
        startInfo.ArgumentList.Add(selfTest);

        using Process contractProcess = Process.Start(startInfo)
            ?? throw new AssertFailedException("The package inventory self-test could not start.");
        bool contractCompleted = contractProcess.WaitForExit(60_000);
        if (!contractCompleted)
        {
            contractProcess.Kill(entireProcessTree: true);
            contractProcess.WaitForExit();
        }

        string contractOutput = contractProcess.StandardOutput.ReadToEnd();
        string contractError = contractProcess.StandardError.ReadToEnd();
        Assert.IsTrue(
            contractCompleted && contractProcess.ExitCode == 0,
            $"Native playback package inventory contract failed.{Environment.NewLine}{contractOutput}{contractError}");
    }

    [TestMethod]
    public void M8CatalogCrashHarnessIsIsolatedAndKillsOnlyItsTrackedProcess()
    {
        string harness = File.ReadAllText(Path.Combine(
            RepositoryRoot,
            "apps",
            "windows",
            "tests",
            "IptvSuite.CatalogCrashHarness",
            "Program.cs"));
        string crashTest = File.ReadAllText(Path.Combine(
            RepositoryRoot,
            "apps",
            "windows",
            "tests",
            "IptvSuite.IntegrationTests",
            "SqliteCatalogCrashRecoveryTests.cs"));

        StringAssert.Contains(harness, "new BlockingTransport(replacement");
        StringAssert.Contains(harness, "Task.Delay(Timeout.InfiniteTimeSpan");
        StringAssert.Contains(crashTest, "process.Kill(entireProcessTree: true)");
        StringAssert.Contains(crashTest, "Assert.AreEqual(\"Old channel\", reader.GetString(4))");
        StringAssert.Contains(crashTest, "AssertNoHotRollbackJournal");
        StringAssert.Contains(crashTest, "File.Exists(databasePath + \"-wal\")");
        StringAssert.Contains(crashTest, "File.Exists(databasePath + \"-shm\")");
        Assert.IsFalse(crashTest.Contains("GetProcesses", StringComparison.Ordinal));
        Assert.IsFalse(crashTest.Contains("ProcessName", StringComparison.Ordinal));
    }

    [TestMethod]
    public void M9CatalogBrowserContractIsBoundedAndDoesNotExposeSqlite()
    {
        string contract = File.ReadAllText(Path.Combine(
            RepositoryRoot,
            "apps",
            "windows",
            "src",
            "IptvSuite.Application",
            "CatalogBrowserContracts.cs"));
        string adapter = File.ReadAllText(Path.Combine(
            RepositoryRoot,
            "apps",
            "windows",
            "src",
            "IptvSuite.Infrastructure",
            "SqliteCatalogQuery.cs"));

        StringAssert.Contains(contract, "public interface ICatalogBrowser");
        StringAssert.Contains(contract, "ValueTask<CatalogChannelPage> ReadChannelsAsync(");
        Assert.IsFalse(contract.Contains("Sqlite", StringComparison.Ordinal));
        Assert.IsFalse(contract.Contains("IptvSuite.Infrastructure", StringComparison.Ordinal));
        StringAssert.Contains(adapter, "public const int MaximumPageSize = 200;");
        StringAssert.Contains(adapter, "public const int MaximumSearchLength = 100;");
        StringAssert.Contains(adapter, "BeginTransactionAsync(cancellationToken)");
        Assert.IsFalse(adapter.Contains("SELECT *", StringComparison.OrdinalIgnoreCase));
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
            "      - quality\n      - package-smoke\n      - native-playback\n      - dpapi-user-boundary\n");
        StringAssert.Contains(
            workflow,
            "NATIVE_PLAYBACK_RESULT: ${{ needs.native-playback.result }}");
        StringAssert.Contains(workflow, "test \"$NATIVE_PLAYBACK_RESULT\" = \"success\"");
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
        StringAssert.Contains(workflow, "runs-on: windows-2025");
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
            "shell: powershell\n        run: >-\n          .\\eng\\Invoke-WindowsNativePlaybackSmoke.ps1\n          -Configuration Release");
        StringAssert.Contains(
            workflow,
            "  native-playback:\n    name: Native Tier A packaged playback smoke\n    needs: quality\n");
        StringAssert.Contains(workflow, "name: windows-native-playback-evidence");
        StringAssert.Contains(workflow, ".artifacts/native-playback-smoke/last-success.json");
        StringAssert.Contains(workflow, "timeout-minutes: 30");
        StringAssert.Contains(workflow, "-SwitchCount 100");
        StringAssert.Contains(workflow, "-SoakMinutes 0");
        StringAssert.Contains(workflow, "-NetworkInterruptionCount 1");
        StringAssert.Contains(workflow, "-CancellationProbeCount 1");
        StringAssert.Contains(workflow, "validate-native-playback-evidence `");
        StringAssert.Contains(workflow, ".\\eng\\Invoke-WindowsNativePlaybackSmoke.ps1 `");
        StringAssert.Contains(workflow, "$env:GITHUB_SHA.ToLowerInvariant() `");
        StringAssert.Contains(workflow, "The native playback evidence contract validation failed.");
        StringAssert.Contains(
            workflow,
            "scan-artifacts .\\.artifacts\\package-lifecycle M4 PACKAGE_LIFECYCLE_EVIDENCE");
        StringAssert.Contains(
            workflow,
            "scan-artifacts .\\.artifacts\\native-playback-smoke M10 NATIVE_PLAYBACK_EVIDENCE");
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
        Assert.HasCount(13, allUses);
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
