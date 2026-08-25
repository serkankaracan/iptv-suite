using System.Security.Cryptography;
using System.Text;

namespace IptvSuite.UnitTests;

[TestClass]
public sealed class M14CatalogCorpusGeneratorTests
{
    [TestMethod]
    public void SpecificationDefinesExactClosedSyntheticMatrix()
    {
        M14CatalogCorpusSpecification specification = LoadSpecification();

        Assert.AreEqual(1, specification.SchemaVersion);
        Assert.AreEqual("m14-catalog-corpus-v1", specification.FixtureSetId);
        Assert.AreEqual(M14CatalogCorpusGenerator.GeneratorName, specification.GeneratorName);
        Assert.AreEqual(M14CatalogCorpusGenerator.GeneratorVersion, specification.GeneratorVersion);
        Assert.AreEqual(1, specification.AlgorithmVersion);
        Assert.AreEqual(20260825L, specification.Seed);
        Assert.AreEqual("synthetic", specification.Provenance);
        Assert.IsFalse(specification.ContainsThirdPartyContent);
        Assert.IsFalse(specification.ContainsPersonalData);
        Assert.IsFalse(specification.ContainsRealCredentials);
        Assert.IsFalse(specification.ContainsUnauthorizedMedia);
        Assert.AreEqual(
            "apps/windows/testdata/LICENSES/LicenseRef-IPTVSuite-Synthetic-Test-Only.txt",
            specification.License.File);
        Assert.IsTrue(File.Exists(Path.Combine(FindRepositoryRoot(), specification.License.File)));
        Assert.HasCount(6, specification.Corpora);

        string[] actual = specification.Corpora.Select(corpus =>
            $"{corpus.Id}|{corpus.ChannelCount}|{corpus.CategoryCount}|{corpus.LogoPercent}|{corpus.ExpectedOutcome}")
            .ToArray();
        string[] expected =
        [
            "small|100|10|50|Success",
            "medium|5000|100|70|Success",
            "large|10000|200|80|Success",
            "very-large|20000|300|90|Success",
            "mvp-gate|50000|500|100|Success",
            "stress|100000|1000|100|EntryLimitFailClosed",
        ];
        CollectionAssert.AreEqual(expected, actual);
    }

    [TestMethod]
    [Timeout(60_000)]
    public async Task GenerationIsByteIdenticalAggregateOnlyAndContainsFixedVariations()
    {
        M14CatalogCorpusSpecification specification = LoadSpecification();
        using TemporaryDirectory first = TemporaryDirectory.Create("m14-corpus-first");
        using TemporaryDirectory second = TemporaryDirectory.Create("m14-corpus-second");

        GeneratedM14CatalogCorpusSet firstSet = await M14CatalogCorpusGenerator.GenerateAsync(
            specification,
            first.FullPath);
        GeneratedM14CatalogCorpusSet secondSet = await M14CatalogCorpusGenerator.GenerateAsync(
            specification,
            second.FullPath);

        Assert.HasCount(6, firstSet.Corpora);
        Assert.HasCount(6, secondSet.Corpora);
        for (int index = 0; index < firstSet.Corpora.Count; index++)
        {
            GeneratedM14CatalogCorpus firstCorpus = firstSet.Corpora[index];
            GeneratedM14CatalogCorpus secondCorpus = secondSet.Corpora[index];
            Assert.AreEqual(firstCorpus.Id, secondCorpus.Id);
            Assert.AreEqual(firstCorpus.Sha256, secondCorpus.Sha256);
            Assert.AreEqual(firstCorpus.ByteLength, secondCorpus.ByteLength);
            Assert.AreEqual(firstCorpus.Sha256, GetSha256(firstCorpus.Path));
            Assert.AreEqual(secondCorpus.Sha256, GetSha256(secondCorpus.Path));
            Assert.AreEqual(firstCorpus.ChannelCount, secondCorpus.ChannelCount);
            Assert.AreEqual(firstCorpus.CategoryCount, secondCorpus.CategoryCount);
            Assert.AreEqual(firstCorpus.LogoReferenceCount, secondCorpus.LogoReferenceCount);
            Assert.AreEqual(firstCorpus.ExpectedOutcome, secondCorpus.ExpectedOutcome);
        }

        CollectionAssert.AreEqual(
            await File.ReadAllBytesAsync(firstSet.ManifestPath),
            await File.ReadAllBytesAsync(secondSet.ManifestPath));

        string manifest = await File.ReadAllTextAsync(firstSet.ManifestPath, Encoding.UTF8);
        Assert.IsFalse(manifest.Contains("://", StringComparison.Ordinal));
        Assert.IsFalse(manifest.Contains("access_token", StringComparison.Ordinal));
        Assert.IsFalse(manifest.Contains("synthetic-test-only-", StringComparison.Ordinal));
        Assert.AreEqual(6, CountOccurrences(manifest, "\"variationCounts\""));
        Assert.AreEqual(6, CountOccurrences(manifest, "\"tokenBearingLocatorEntries\": 1"));

        GeneratedM14CatalogCorpus small = firstSet.Corpora.Single(corpus =>
            string.Equals(corpus.Id, "small", StringComparison.Ordinal));
        Assert.AreEqual(50, small.LogoReferenceCount);
        string playlist = await File.ReadAllTextAsync(small.Path, Encoding.UTF8);
        Assert.AreEqual(101, CountOccurrences(playlist, "#EXTINF:"));
        Assert.AreEqual(100, playlist.Split('\n').Count(line =>
            line.StartsWith("#EXTINF:", StringComparison.Ordinal) && line.Contains(',', StringComparison.Ordinal)));
        Assert.AreEqual(50, CountOccurrences(playlist, " tvg-logo=\""));
        Assert.AreEqual(2, CountOccurrences(playlist, "tvg-id=\"synthetic-duplicate-provider-id\""));
        Assert.AreEqual(2, CountOccurrences(playlist, "\nstreams/stable-collision.ts\n"));
        Assert.AreEqual(1, CountOccurrences(playlist, "access_token=synthetic-test-only-"));
        Assert.AreEqual(1, CountOccurrences(playlist, "http://fixtures.invalid/logos/invalid-scheme.png"));
        Assert.AreEqual(1, CountOccurrences(playlist, "#EXTINF:-1 tvg-id=\"synthetic-malformed-metadata\"\n"));
        StringAssert.Contains(playlist, "Synthetic Cafe\u0301 İstanbul 日本語");
        StringAssert.Contains(playlist, new string('L', 256));
        string missingGroupLine = playlist.Split('\n').Single(line =>
            line.Contains("tvg-id=\"synthetic-000003\"", StringComparison.Ordinal));
        Assert.IsFalse(missingGroupLine.Contains("group-title=", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task GeneratorRejectsUnsafeSpecificationAndNonEmptyOutput()
    {
        M14CatalogCorpusSpecification specification = LoadSpecification();
        using TemporaryDirectory unsafeOutput = TemporaryDirectory.Create("m14-corpus-unsafe");
        M14CatalogCorpusSpecification unsafeSpecification = specification with
        {
            ContainsRealCredentials = true,
        };

        await Assert.ThrowsExactlyAsync<InvalidDataException>(async () =>
            await M14CatalogCorpusGenerator.GenerateAsync(unsafeSpecification, unsafeOutput.FullPath));
        Assert.IsEmpty(Directory.GetFileSystemEntries(unsafeOutput.FullPath));

        using TemporaryDirectory nonEmptyOutput = TemporaryDirectory.Create("m14-corpus-nonempty");
        string markerPath = Path.Combine(nonEmptyOutput.FullPath, "owned-marker.txt");
        await File.WriteAllTextAsync(markerPath, "owned");
        await Assert.ThrowsExactlyAsync<IOException>(async () =>
            await M14CatalogCorpusGenerator.GenerateAsync(specification, nonEmptyOutput.FullPath));
        Assert.IsTrue(File.Exists(markerPath));
    }

    [TestMethod]
    public async Task PreCancelledGenerationLeavesOutputEmpty()
    {
        M14CatalogCorpusSpecification specification = LoadSpecification();
        using TemporaryDirectory output = TemporaryDirectory.Create("m14-corpus-cancelled");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsExactlyAsync<OperationCanceledException>(async () =>
            await M14CatalogCorpusGenerator.GenerateAsync(
                specification,
                output.FullPath,
                cancellation.Token));
        Assert.IsEmpty(Directory.GetFileSystemEntries(output.FullPath));
    }

    private static M14CatalogCorpusSpecification LoadSpecification() =>
        M14CatalogCorpusGenerator.LoadSpecification(Path.Combine(
            FindRepositoryRoot(),
            "apps",
            "windows",
            "testdata",
            "m14",
            "catalog-corpus-spec.json"));

    private static string GetSha256(string path)
    {
        using FileStream stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private static int CountOccurrences(string value, string expected)
    {
        int count = 0;
        int offset = 0;
        while ((offset = value.IndexOf(expected, offset, StringComparison.Ordinal)) >= 0)
        {
            count++;
            offset += expected.Length;
        }

        return count;
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
