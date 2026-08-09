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
    public void WinUiAndMsixSettingsStayInWindowsCompositionRoot()
    {
        foreach (ProjectRule rule in ProjectRules)
        {
            XDocument project = LoadXml(rule.RelativePath);
            string? useWinUi = GetProperty(project, "UseWinUI");
            string? enableMsixTooling = GetProperty(project, "EnableMsixTooling");

            if (rule.Name == "IptvSuite.Windows")
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

        foreach (string configuration in new[] { "Debug", "Release" })
        {
            StringAssert.Contains(
                solution,
                $"{windowsProjectGuid}.{configuration}|x64.Deploy.0 = {configuration}|x64");
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
                content.Contains("Microsoft.Extensions.TimeProvider.Testing", StringComparison.Ordinal) ||
                content.Contains("Microsoft.AspNetCore.App", StringComparison.Ordinal) ||
                content.Contains("IPTVSUITE_TEST_ONLY_CANARY_V1", StringComparison.Ordinal),
                $"Test infrastructure leaked into production source: {Path.GetRelativePath(RepositoryRoot, sourceFile)}");
        }

        XDocument testingProject = LoadXml("apps/windows/tests/IptvSuite.Testing/IptvSuite.Testing.csproj");
        Assert.AreEqual("false", GetProperty(testingProject, "IsPackable"));
        Assert.AreEqual("false", GetProperty(testingProject, "IsPublishable"));
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
        Assert.HasCount(6, allUses);
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
