using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace IptvSuite.UnitTests;

[TestClass]
public sealed class FixtureAndCanaryTests
{
    private static readonly string[] SanitizedCliReportPropertyNames =
    [
        "schemaVersion",
        "profile",
        "result",
        "fileCount",
        "directoryCount",
        "totalFileBytes",
        "inventorySha256",
        "findingCount",
    ];

    [TestMethod]
    [Timeout(10_000)]
    public async Task SameFixtureSpecificationProducesByteIdenticalOutputs()
    {
        string specificationPath = Path.Combine(
            FindRepositoryRoot(),
            "apps",
            "windows",
            "testdata",
            "m2",
            "fixture-spec.json");
        FixtureSpecification specification = SyntheticFixtureGenerator.LoadSpecification(specificationPath);

        using TemporaryDirectory first = TemporaryDirectory.Create("fixture-first");
        using TemporaryDirectory second = TemporaryDirectory.Create("fixture-second");
        GeneratedFixtureSet firstResult = await SyntheticFixtureGenerator.GenerateAsync(specification, first.FullPath);
        GeneratedFixtureSet secondResult = await SyntheticFixtureGenerator.GenerateAsync(specification, second.FullPath);

        CollectionAssert.AreEqual(
            await File.ReadAllBytesAsync(firstResult.RecordsPath),
            await File.ReadAllBytesAsync(secondResult.RecordsPath));
        CollectionAssert.AreEqual(
            await File.ReadAllBytesAsync(firstResult.ManifestPath),
            await File.ReadAllBytesAsync(secondResult.ManifestPath));
        Assert.AreEqual(firstResult.RecordsSha256, secondResult.RecordsSha256);
    }

    [TestMethod]
    public void CanaryScannerDetectsSupportedEncodingsAndChunkBoundaries()
    {
        using TemporaryDirectory temporary = TemporaryDirectory.Create("canary-positive");
        TestCanary canary = TestCanary.Create("UNIT", "ENCODINGS");
        TestCanaryEncoding[] encodings =
        [
            TestCanaryEncoding.Utf8,
            TestCanaryEncoding.Utf16LittleEndian,
            TestCanaryEncoding.Utf16BigEndian,
            TestCanaryEncoding.UriEscapedUtf8,
            TestCanaryEncoding.Base64Utf8,
        ];

        foreach (TestCanaryEncoding encoding in encodings)
        {
            string path = Path.Combine(temporary.FullPath, $"artifact-{encoding}.bin");
            using FileStream stream = File.Create(path);
            if (encoding == TestCanaryEncoding.Utf8)
            {
                stream.Write(new byte[8191]);
            }

            canary.WriteTo(stream, encoding);
        }

        IReadOnlyList<CanaryFinding> findings = ArtifactCanaryScanner.Scan(temporary.FullPath, canary);

        foreach (TestCanaryEncoding encoding in encodings)
        {
            Assert.IsTrue(findings.Any(finding => finding.Encoding == encoding), $"Missing {encoding} finding.");
        }

        Assert.IsTrue(findings.SequenceEqual(
            findings.OrderBy(finding => finding.RelativePath, StringComparer.Ordinal)
                .ThenBy(finding => finding.Encoding)
                .ThenBy(finding => finding.ByteOffset)));
        Assert.AreEqual("[TEST-CANARY]", canary.ToString());
    }

    [TestMethod]
    public void CanaryScannerDoesNotMatchCleanOrNearMatchArtifacts()
    {
        using TemporaryDirectory temporary = TemporaryDirectory.Create("canary-negative");
        File.WriteAllText(
            Path.Combine(temporary.FullPath, "clean.txt"),
            "[REDACTED] IPTVSUITE_TEST_ONLY_CANARY_V NOT_A_REAL_CREDENTIAL",
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        TestCanary canary = TestCanary.Create("UNIT", "NEGATIVE");

        IReadOnlyList<CanaryFinding> findings = ArtifactCanaryScanner.Scan(temporary.FullPath, canary);

        Assert.IsEmpty(findings);
    }

    [TestMethod]
    public void CanaryScannerDetectsEncodedMarkersAcrossScopesAndRedactsCanaryPaths()
    {
        using TemporaryDirectory temporary = TemporaryDirectory.Create("canary-cross-scope");
        TestCanary foreignCanary = TestCanary.Create("UNIT", "FOREIGN_SCOPE");
        TestCanaryEncoding[] foreignEncodings =
        [
            TestCanaryEncoding.Utf16LittleEndian,
            TestCanaryEncoding.Utf16BigEndian,
            TestCanaryEncoding.Base64Utf8,
        ];

        foreach (TestCanaryEncoding encoding in foreignEncodings)
        {
            using FileStream stream = File.Create(Path.Combine(temporary.FullPath, $"foreign-{encoding}.bin"));
            foreignCanary.WriteTo(stream, encoding);
        }

        string canaryPath = Path.Combine(temporary.FullPath, TestCanary.Marker, "clean.bin");
        Directory.CreateDirectory(Path.GetDirectoryName(canaryPath)!);
        File.WriteAllBytes(canaryPath, [1, 2, 3]);

        IReadOnlyList<CanaryFinding> findings = ArtifactCanaryScanner.Scan(
            temporary.FullPath,
            TestCanary.Create("CI", "QUALITY_ARTIFACTS"));

        Assert.IsTrue(findings.Any(finding => finding.Encoding == TestCanaryEncoding.MarkerUtf16LittleEndian));
        Assert.IsTrue(findings.Any(finding => finding.Encoding == TestCanaryEncoding.MarkerUtf16BigEndian));
        Assert.IsTrue(findings.Any(finding => finding.Encoding == TestCanaryEncoding.MarkerBase64Utf8Prefix));
        CanaryFinding[] pathFindings = findings
            .Where(finding => finding.Encoding == TestCanaryEncoding.Path)
            .ToArray();
        Assert.IsNotEmpty(pathFindings);
        Assert.IsTrue(pathFindings.All(finding =>
            finding.RelativePath.StartsWith("[REDACTED-CANARY-PATH:", StringComparison.Ordinal) &&
            !finding.RelativePath.Contains(TestCanary.Marker, StringComparison.Ordinal)));
    }

    [TestMethod]
    public void ReleaseCandidateCanaryScannerUsesOnlyTheFixedProfileAndFingerprintsPaths()
    {
        using TemporaryDirectory temporary = TemporaryDirectory.Create("m16-canary-profile");
        TestCanary canary = TestCanary.Create("M16", "FINAL_ARTIFACTS");
        string contaminatedPath = Path.Combine(temporary.FullPath, "operator-visible-name.log");
        using (FileStream stream = File.Create(contaminatedPath))
        {
            stream.Write(new byte[8191]);
            canary.WriteTo(stream, TestCanaryEncoding.Utf8);
        }

        IReadOnlyList<CanaryFinding> findings = ArtifactCanaryScanner.Scan(
            temporary.FullPath,
            canary,
            ArtifactCanaryScanProfile.M16ReleaseCandidate);

        Assert.IsNotEmpty(findings);
        Assert.IsTrue(findings.Any(finding => finding.ByteOffset == 8191));
        Assert.IsTrue(findings.All(finding =>
            finding.RelativePath.StartsWith(
                "[REDACTED-ARTIFACT-PATH:",
                StringComparison.Ordinal) &&
            !finding.RelativePath.Contains("operator-visible-name", StringComparison.Ordinal) &&
            !finding.RelativePath.Contains(TestCanary.Marker, StringComparison.Ordinal)));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => ArtifactCanaryScanner.Scan(
            temporary.FullPath,
            canary,
            (ArtifactCanaryScanProfile)int.MaxValue));
    }

    [TestMethod]
    public void ReleaseCandidateCanaryScannerReportsStableSanitizedInventory()
    {
        using TemporaryDirectory temporary = TemporaryDirectory.Create("m16-canary-report");
        Directory.CreateDirectory(Path.Combine(temporary.FullPath, "empty"));
        string contentPath = Path.Combine(temporary.FullPath, "operator-visible-name.bin");
        File.WriteAllBytes(contentPath, [1, 2, 3, 4]);
        TestCanary canary = TestCanary.Create("M16", "FINAL_REPORT");

        ArtifactCanaryScanReport first = ArtifactCanaryScanner.ScanWithReport(
            temporary.FullPath,
            canary,
            ArtifactCanaryScanProfile.M16ReleaseCandidate);
        ArtifactCanaryScanReport second = ArtifactCanaryScanner.ScanWithReport(
            temporary.FullPath,
            canary,
            ArtifactCanaryScanProfile.M16ReleaseCandidate);

        Assert.AreEqual(1, first.SchemaVersion);
        Assert.AreEqual("M16ReleaseCandidate", first.Profile);
        Assert.AreEqual(1, first.FileCount);
        Assert.AreEqual(1, first.DirectoryCount);
        Assert.AreEqual(4L, first.TotalFileBytes);
        Assert.AreEqual(
            "9779c971de13c2e36c585768774b8290ec1baab4847951631f8ffaec1a230d98",
            first.InventorySha256);
        Assert.AreEqual(first.InventorySha256, second.InventorySha256);
        Assert.AreEqual(0, first.FindingCount);
        Assert.IsTrue(first.IsClean);
        Assert.IsEmpty(first.Findings);

        File.WriteAllBytes(contentPath, [4, 3, 2, 1]);
        ArtifactCanaryScanReport changed = ArtifactCanaryScanner.ScanWithReport(
            temporary.FullPath,
            canary,
            ArtifactCanaryScanProfile.M16ReleaseCandidate);
        Assert.AreNotEqual(first.InventorySha256, changed.InventorySha256);
    }

    [TestMethod]
    [DoNotParallelize]
    public async Task ReleaseCandidateCanaryCliUsesExactSanitizedExitAndJsonContract()
    {
        const string runScope = "M16";
        const string caseId = "CLI_CONTRACT";
        using TemporaryDirectory clean = TemporaryDirectory.Create("m16-cli-clean");
        File.WriteAllBytes(Path.Combine(clean.FullPath, "operator-visible-name.bin"), [1, 2, 3]);

        TestToolResult cleanResult = await RunTestToolAsync(
            "scan-release-artifacts",
            clean.FullPath,
            runScope,
            caseId);
        Assert.AreEqual(0, cleanResult.ExitCode);
        Assert.AreEqual(string.Empty, cleanResult.StandardError);
        AssertSanitizedCliReport(cleanResult.StandardOutput, expectedResult: "clean", expectedFindings: 0);
        AssertOutputIsSanitized(cleanResult, clean.FullPath, "operator-visible-name", TestCanary.Marker);

        using TemporaryDirectory contaminated = TemporaryDirectory.Create("m16-cli-finding");
        string contaminatedPath = Path.Combine(contaminated.FullPath, "operator-visible-name.bin");
        using (FileStream stream = File.Create(contaminatedPath))
        {
            TestCanary.Create(runScope, caseId).WriteTo(stream, TestCanaryEncoding.Utf8);
        }

        TestToolResult findingResult = await RunTestToolAsync(
            "scan-release-artifacts",
            contaminated.FullPath,
            runScope,
            caseId);
        Assert.AreEqual(2, findingResult.ExitCode);
        StringAssert.Contains(findingResult.StandardError, "[REDACTED-ARTIFACT-PATH:");
        AssertSanitizedCliReport(findingResult.StandardOutput, expectedResult: "finding", expectedFindings: null);
        AssertOutputIsSanitized(
            findingResult,
            contaminated.FullPath,
            "operator-visible-name",
            TestCanary.Marker);

        string missingRoot = Path.Combine(Path.GetTempPath(), $"m16-cli-missing-{Guid.NewGuid():N}");
        TestToolResult operationalFailure = await RunTestToolAsync(
            "scan-release-artifacts",
            missingRoot,
            runScope,
            caseId);
        Assert.AreEqual(1, operationalFailure.ExitCode);
        Assert.AreEqual(string.Empty, operationalFailure.StandardOutput);
        StringAssert.Contains(
            operationalFailure.StandardError,
            "M16ReleaseCandidateArtifactScan:RootMissing");
        AssertOutputIsSanitized(operationalFailure, missingRoot, TestCanary.Marker);

        TestToolResult usageFailure = await RunTestToolAsync("scan-release-artifacts");
        Assert.AreEqual(64, usageFailure.ExitCode);
        Assert.AreEqual(string.Empty, usageFailure.StandardOutput);
        StringAssert.Contains(usageFailure.StandardError, "Usage:");
    }

    [TestMethod]
    public void ReleaseCandidateCanaryScannerFailsClosedAtEveryBound()
    {
        TestCanary canary = TestCanary.Create("M16", "BOUNDS");

        using (TemporaryDirectory depth = TemporaryDirectory.Create("m16-canary-depth"))
        {
            Directory.CreateDirectory(Path.Combine(depth.FullPath, "child"));
            AssertLimitFailure(
                depth.FullPath,
                canary,
                Limits(maximumDirectoryDepth: 0),
                "M16ReleaseCandidateArtifactScan:DepthLimitExceeded");
        }

        using (TemporaryDirectory entries = TemporaryDirectory.Create("m16-canary-entries"))
        {
            File.WriteAllBytes(Path.Combine(entries.FullPath, "a.bin"), [1]);
            File.WriteAllBytes(Path.Combine(entries.FullPath, "b.bin"), [2]);
            AssertLimitFailure(
                entries.FullPath,
                canary,
                Limits(maximumEntryCount: 1),
                "M16ReleaseCandidateArtifactScan:EntryLimitExceeded");
        }

        using (TemporaryDirectory fileSize = TemporaryDirectory.Create("m16-canary-file-size"))
        {
            File.WriteAllBytes(Path.Combine(fileSize.FullPath, "a.bin"), [1, 2]);
            AssertLimitFailure(
                fileSize.FullPath,
                canary,
                Limits(maximumSingleFileBytes: 1),
                "M16ReleaseCandidateArtifactScan:FileSizeLimitExceeded");
        }

        using (TemporaryDirectory totalSize = TemporaryDirectory.Create("m16-canary-total-size"))
        {
            File.WriteAllBytes(Path.Combine(totalSize.FullPath, "a.bin"), [1]);
            File.WriteAllBytes(Path.Combine(totalSize.FullPath, "b.bin"), [2]);
            AssertLimitFailure(
                totalSize.FullPath,
                canary,
                Limits(maximumSingleFileBytes: 1, maximumTotalFileBytes: 1),
                "M16ReleaseCandidateArtifactScan:TotalSizeLimitExceeded");
        }

        using (TemporaryDirectory findings = TemporaryDirectory.Create("m16-canary-findings"))
        {
            using FileStream stream = File.Create(Path.Combine(findings.FullPath, "a.bin"));
            canary.WriteTo(stream, TestCanaryEncoding.Utf8);
            stream.Dispose();
            AssertLimitFailure(
                findings.FullPath,
                canary,
                Limits(maximumFindingCount: 1),
                "M16ReleaseCandidateArtifactScan:FindingLimitExceeded");
        }

        using (TemporaryDirectory path = TemporaryDirectory.Create("m16-canary-path"))
        {
            File.WriteAllBytes(Path.Combine(path.FullPath, "ab"), [1]);
            AssertLimitFailure(
                path.FullPath,
                canary,
                Limits(maximumRelativePathLength: 1),
                "M16ReleaseCandidateArtifactScan:PathLimitExceeded");
        }
    }

    [TestMethod]
    public void ReleaseCandidateCanaryScannerFailsClosedForMissingOrLockedInventory()
    {
        TestCanary canary = TestCanary.Create("M16", "INVENTORY");
        string missingRoot = Path.Combine(Path.GetTempPath(), $"m16-missing-{Guid.NewGuid():N}");
        DirectoryNotFoundException missing = Assert.ThrowsExactly<DirectoryNotFoundException>(() =>
            ArtifactCanaryScanner.Scan(
                missingRoot,
                canary,
                ArtifactCanaryScanProfile.M16ReleaseCandidate));
        Assert.AreEqual("M16ReleaseCandidateArtifactScan:RootMissing", missing.Message);

        using TemporaryDirectory temporary = TemporaryDirectory.Create("m16-canary-locked");
        string lockedPath = Path.Combine(temporary.FullPath, "locked.bin");
        File.WriteAllBytes(lockedPath, [1, 2, 3]);
        using FileStream locked = new(
            lockedPath,
            FileMode.Open,
            FileAccess.ReadWrite,
            FileShare.None);
        IOException failure = Assert.ThrowsExactly<IOException>(() =>
            ArtifactCanaryScanner.Scan(
                temporary.FullPath,
                canary,
                ArtifactCanaryScanProfile.M16ReleaseCandidate));
        Assert.AreEqual("M16ReleaseCandidateArtifactScan:FileReadFailed", failure.Message);
        Assert.IsFalse(failure.Message.Contains(lockedPath, StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public void ReleaseCandidateCanaryScannerRefusesFileAndDirectoryAlternateDataStreams()
    {
        using TemporaryDirectory fileSurface = TemporaryDirectory.Create("m16-canary-file-ads");
        string filePath = Path.Combine(fileSurface.FullPath, "artifact.bin");
        File.WriteAllBytes(filePath, [1, 2, 3]);
        File.WriteAllText(
            $"{filePath}:hidden",
            "not-visible-to-default-stream-enumeration",
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        AssertAlternateDataStreamFailure(fileSurface.FullPath);

        using TemporaryDirectory directorySurface = TemporaryDirectory.Create("m16-canary-directory-ads");
        File.WriteAllText(
            $"{directorySurface.FullPath}:hidden",
            "not-visible-to-default-stream-enumeration",
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        AssertAlternateDataStreamFailure(directorySurface.FullPath);
    }

    private static ArtifactCanaryScanLimits Limits(
        int maximumDirectoryDepth = 8,
        int maximumEntryCount = 32,
        long maximumSingleFileBytes = 1024,
        long maximumTotalFileBytes = 4096,
        int maximumFindingCount = 16,
        int maximumRelativePathLength = 256) =>
        new(
            maximumDirectoryDepth,
            maximumEntryCount,
            maximumSingleFileBytes,
            maximumTotalFileBytes,
            maximumFindingCount,
            maximumRelativePathLength);

    private static void AssertLimitFailure(
        string root,
        TestCanary canary,
        ArtifactCanaryScanLimits limits,
        string expectedMessage)
    {
        ArtifactCanaryScanLimitException failure =
            Assert.ThrowsExactly<ArtifactCanaryScanLimitException>(() =>
                ArtifactCanaryScanner.ScanBounded(root, canary, limits));
        Assert.AreEqual(expectedMessage, failure.Message);
    }

    private static void AssertAlternateDataStreamFailure(string root)
    {
        IOException failure = Assert.ThrowsExactly<IOException>(() =>
            ArtifactCanaryScanner.Scan(
                root,
                TestCanary.Create("M16", "ADS"),
                ArtifactCanaryScanProfile.M16ReleaseCandidate));
        Assert.AreEqual(
            "M16ReleaseCandidateArtifactScan:AlternateDataStreamRefused",
            failure.Message);
    }

    private static async Task<TestToolResult> RunTestToolAsync(params string[] arguments)
    {
        TextWriter originalOutput = Console.Out;
        TextWriter originalError = Console.Error;
        using StringWriter output = new(CultureInfo.InvariantCulture);
        using StringWriter error = new(CultureInfo.InvariantCulture);
        try
        {
            Console.SetOut(output);
            Console.SetError(error);
            int exitCode = await TestTool.RunAsync(arguments);
            return new TestToolResult(exitCode, output.ToString(), error.ToString());
        }
        finally
        {
            Console.SetOut(originalOutput);
            Console.SetError(originalError);
        }
    }

    private static void AssertSanitizedCliReport(
        string output,
        string expectedResult,
        int? expectedFindings)
    {
        using JsonDocument document = JsonDocument.Parse(output);
        JsonElement root = document.RootElement;
        CollectionAssert.AreEqual(
            SanitizedCliReportPropertyNames,
            root.EnumerateObject().Select(static property => property.Name).ToArray());
        Assert.AreEqual(1, root.GetProperty("schemaVersion").GetInt32());
        Assert.AreEqual("M16ReleaseCandidate", root.GetProperty("profile").GetString());
        Assert.AreEqual(expectedResult, root.GetProperty("result").GetString());
        StringAssert.Matches(
            root.GetProperty("inventorySha256").GetString()!,
            new System.Text.RegularExpressions.Regex("^[0-9a-f]{64}$", System.Text.RegularExpressions.RegexOptions.CultureInvariant));
        int findingCount = root.GetProperty("findingCount").GetInt32();
        if (expectedFindings.HasValue)
        {
            Assert.AreEqual(expectedFindings.Value, findingCount);
        }
        else
        {
            Assert.IsGreaterThan(0, findingCount);
        }
    }

    private static void AssertOutputIsSanitized(TestToolResult result, params string[] forbiddenValues)
    {
        string combined = result.StandardOutput + result.StandardError;
        foreach (string forbiddenValue in forbiddenValues)
        {
            Assert.DoesNotContain(forbiddenValue, combined, StringComparison.OrdinalIgnoreCase);
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

        throw new DirectoryNotFoundException("Repository root was not found.");
    }

    private sealed record TestToolResult(int ExitCode, string StandardOutput, string StandardError);
}
