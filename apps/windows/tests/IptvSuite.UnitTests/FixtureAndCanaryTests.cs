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
