using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace IptvSuite.UnitTests;

[TestClass]
public sealed class FixtureAndCanaryTests
{
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
}
