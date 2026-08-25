using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace IptvSuite.Testing;

public static class M14CatalogCorpusGenerator
{
    public const string GeneratorName = "IptvSuite.Testing.M14CatalogCorpusGenerator";
    public const string GeneratorVersion = "1.0.0";

    private const int AlgorithmVersion = 1;
    private const long ExpectedSeed = 20260825;
    private const string ExpectedFixtureSetId = "m14-catalog-corpus-v1";
    private const string ExpectedProvenance = "synthetic";
    private const int MalformedInsertionAfterOrdinal = 10;
    private const int DuplicateFirstOrdinal = 1;
    private const int DuplicateSecondOrdinal = 2;
    private const int MissingGroupOrdinal = 3;
    private const int UnicodeOrdinal = 4;
    private const int LongNameOrdinal = 5;
    private const int TokenLocatorOrdinal = 6;
    private const int InvalidLogoOrdinal = 7;
    private const int StableCollisionFirstOrdinal = 8;
    private const int StableCollisionSecondOrdinal = 9;

    private static readonly MatrixEntry[] ExpectedMatrix =
    [
        new("small", "small-100.m3u", 100, 10, 50, M14CatalogCorpusExpectedOutcome.Success),
        new("medium", "medium-5000.m3u", 5_000, 100, 70, M14CatalogCorpusExpectedOutcome.Success),
        new("large", "large-10000.m3u", 10_000, 200, 80, M14CatalogCorpusExpectedOutcome.Success),
        new("very-large", "very-large-20000.m3u", 20_000, 300, 90, M14CatalogCorpusExpectedOutcome.Success),
        new("mvp-gate", "mvp-gate-50000.m3u", 50_000, 500, 100, M14CatalogCorpusExpectedOutcome.Success),
        new("stress", "stress-100000.m3u", 100_000, 1_000, 100, M14CatalogCorpusExpectedOutcome.EntryLimitFailClosed),
    ];

    private static readonly JsonSerializerOptions SpecificationOptions = new(JsonSerializerDefaults.Web)
    {
        ReadCommentHandling = JsonCommentHandling.Disallow,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter<M14CatalogCorpusExpectedOutcome>() },
    };

    public static M14CatalogCorpusSpecification LoadSpecification(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        M14CatalogCorpusSpecification specification = JsonSerializer.Deserialize<M14CatalogCorpusSpecification>(
            File.ReadAllBytes(path),
            SpecificationOptions) ?? throw new InvalidDataException("M14 catalog corpus specification is empty.");

        ValidateSpecification(specification);
        return specification;
    }

    public static async Task<GeneratedM14CatalogCorpusSet> GenerateAsync(
        M14CatalogCorpusSpecification specification,
        string outputDirectory,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(specification);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        ValidateSpecification(specification);

        string outputRoot = Path.GetFullPath(outputDirectory);
        Directory.CreateDirectory(outputRoot);
        EnsureDirectoryIsEmpty(outputRoot);
        cancellationToken.ThrowIfCancellationRequested();

        string stagingRoot = Path.Combine(outputRoot, $".m14-staging-{Guid.NewGuid():N}");
        Directory.CreateDirectory(stagingRoot);
        var drafts = new List<GeneratedCorpusDraft>(ExpectedMatrix.Length);
        var committedPaths = new List<string>(ExpectedMatrix.Length + 1);
        try
        {
            foreach (MatrixEntry matrix in ExpectedMatrix)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string stagingPath = Path.Combine(stagingRoot, matrix.FileName);
                (long byteLength, string sha256, int logoReferenceCount) = await WriteCorpusAsync(
                    specification.Seed,
                    matrix,
                    stagingPath,
                    cancellationToken).ConfigureAwait(false);
                drafts.Add(new GeneratedCorpusDraft(matrix, stagingPath, byteLength, sha256, logoReferenceCount));
            }

            byte[] manifest = GenerateManifest(specification, drafts);
            string stagingManifestPath = Path.Combine(stagingRoot, "fixture-manifest.json");
            await File.WriteAllBytesAsync(stagingManifestPath, manifest, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            var generated = new List<GeneratedM14CatalogCorpus>(drafts.Count);
            foreach (GeneratedCorpusDraft draft in drafts)
            {
                string finalPath = Path.Combine(outputRoot, draft.Matrix.FileName);
                File.Move(draft.StagingPath, finalPath);
                committedPaths.Add(finalPath);
                generated.Add(new GeneratedM14CatalogCorpus(
                    draft.Matrix.Id,
                    finalPath,
                    draft.Sha256,
                    draft.ByteLength,
                    draft.Matrix.ChannelCount,
                    draft.Matrix.CategoryCount,
                    draft.LogoReferenceCount,
                    draft.Matrix.ExpectedOutcome));
            }

            string manifestPath = Path.Combine(outputRoot, "fixture-manifest.json");
            File.Move(stagingManifestPath, manifestPath);
            committedPaths.Add(manifestPath);
            return new GeneratedM14CatalogCorpusSet(manifestPath, generated);
        }
        catch
        {
            foreach (string committedPath in committedPaths)
            {
                if (File.Exists(committedPath))
                {
                    File.Delete(committedPath);
                }
            }

            throw;
        }
        finally
        {
            if (Directory.Exists(stagingRoot))
            {
                Directory.Delete(stagingRoot, recursive: true);
            }
        }
    }

    private static async Task<(long ByteLength, string Sha256, int LogoReferenceCount)> WriteCorpusAsync(
        long seed,
        MatrixEntry matrix,
        string path,
        CancellationToken cancellationToken)
    {
        int logoReferenceCount = 0;
        await using (var stream = new FileStream(
            path,
            new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                Share = FileShare.None,
                BufferSize = 64 * 1024,
                Options = FileOptions.Asynchronous | FileOptions.SequentialScan,
            }))
        await using (var writer = new StreamWriter(
            stream,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true),
            bufferSize: 64 * 1024,
            leaveOpen: false))
        {
            await WriteAsync(writer, "#EXTM3U\n", cancellationToken).ConfigureAwait(false);
            for (int ordinal = 0; ordinal < matrix.ChannelCount; ordinal++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                CorpusEntry entry = CreateEntry(seed, matrix, ordinal);
                if (entry.Logo is not null)
                {
                    logoReferenceCount++;
                }

                await WriteEntryAsync(writer, entry, cancellationToken).ConfigureAwait(false);
                if (ordinal == MalformedInsertionAfterOrdinal)
                {
                    await WriteAsync(
                        writer,
                        "#EXTINF:-1 tvg-id=\"synthetic-malformed-metadata\"\n",
                        cancellationToken).ConfigureAwait(false);
                }
            }
        }

        long byteLength = new FileInfo(path).Length;
        await using var content = new FileStream(
            path,
            new FileStreamOptions
            {
                Mode = FileMode.Open,
                Access = FileAccess.Read,
                Share = FileShare.Read,
                BufferSize = 64 * 1024,
                Options = FileOptions.Asynchronous | FileOptions.SequentialScan,
            });
        byte[] digest = await SHA256.HashDataAsync(content, cancellationToken).ConfigureAwait(false);
        return (byteLength, Convert.ToHexString(digest).ToLowerInvariant(), logoReferenceCount);
    }

    private static CorpusEntry CreateEntry(long seed, MatrixEntry matrix, int ordinal)
    {
        string suffix = ordinal.ToString("D6", CultureInfo.InvariantCulture);
        string? tvgId = $"synthetic-{suffix}";
        string name = $"Synthetic Channel {suffix}";
        string? group = $"Group {(ordinal % (matrix.CategoryCount - 1)).ToString("D4", CultureInfo.InvariantCulture)}";
        string locator = $"streams/{suffix}.ts";
        bool hasLogo = ordinal % 100 < matrix.LogoPercent;
        string? logo = hasLogo ? $"https://fixtures.invalid/logos/{suffix}.png" : null;

        if (ordinal is DuplicateFirstOrdinal or DuplicateSecondOrdinal)
        {
            tvgId = "synthetic-duplicate-provider-id";
        }
        else if (ordinal == MissingGroupOrdinal)
        {
            group = null;
        }
        else if (ordinal == UnicodeOrdinal)
        {
            name = "Synthetic Cafe\u0301 İstanbul 日本語";
        }
        else if (ordinal == LongNameOrdinal)
        {
            name = new string('L', 256);
        }
        else if (ordinal == TokenLocatorOrdinal)
        {
            locator = $"streams/{suffix}.ts?access_token=synthetic-test-only-{CreateSyntheticMarker(seed, matrix.Id, ordinal)}";
        }
        else if (ordinal == InvalidLogoOrdinal)
        {
            logo = "http://fixtures.invalid/logos/invalid-scheme.png";
        }
        else if (ordinal is StableCollisionFirstOrdinal or StableCollisionSecondOrdinal)
        {
            tvgId = null;
            name = "Synthetic Stable Collision";
            group = "Group 0000";
            locator = "streams/stable-collision.ts";
        }

        return new CorpusEntry(tvgId, name, group, logo, locator);
    }

    private static string CreateSyntheticMarker(long seed, string corpusId, int ordinal)
    {
        byte[] material = Encoding.UTF8.GetBytes(FormattableString.Invariant(
            $"m14-synthetic-marker-v1|{seed}|{corpusId}|{ordinal}"));
        byte[] digest = SHA256.HashData(material);
        return Convert.ToHexString(digest.AsSpan(0, 8)).ToLowerInvariant();
    }

    private static async Task WriteEntryAsync(
        StreamWriter writer,
        CorpusEntry entry,
        CancellationToken cancellationToken)
    {
        var metadata = new StringBuilder(512);
        metadata.Append("#EXTINF:-1");
        if (entry.TvgId is not null)
        {
            metadata.Append(" tvg-id=\"").Append(entry.TvgId).Append('"');
        }

        if (entry.Logo is not null)
        {
            metadata.Append(" tvg-logo=\"").Append(entry.Logo).Append('"');
        }

        if (entry.Group is not null)
        {
            metadata.Append(" group-title=\"").Append(entry.Group).Append('"');
        }

        metadata.Append(',').Append(entry.Name).Append('\n');
        metadata.Append(entry.Locator).Append('\n');
        await WriteAsync(writer, metadata.ToString(), cancellationToken).ConfigureAwait(false);
    }

    private static async Task WriteAsync(
        StreamWriter writer,
        string value,
        CancellationToken cancellationToken) =>
        await writer.WriteAsync(value.AsMemory(), cancellationToken).ConfigureAwait(false);

    private static byte[] GenerateManifest(
        M14CatalogCorpusSpecification specification,
        IReadOnlyList<GeneratedCorpusDraft> drafts)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schemaVersion", 1);
            writer.WriteString("fixtureSetId", specification.FixtureSetId);
            writer.WriteStartObject("generator");
            writer.WriteString("name", GeneratorName);
            writer.WriteString("version", GeneratorVersion);
            writer.WriteNumber("algorithmVersion", AlgorithmVersion);
            writer.WriteNumber("seed", specification.Seed);
            writer.WriteEndObject();
            writer.WriteStartObject("provenance");
            writer.WriteString("kind", specification.Provenance);
            writer.WriteBoolean("containsThirdPartyContent", specification.ContainsThirdPartyContent);
            writer.WriteBoolean("containsPersonalData", specification.ContainsPersonalData);
            writer.WriteBoolean("containsRealCredentials", specification.ContainsRealCredentials);
            writer.WriteBoolean("containsUnauthorizedMedia", specification.ContainsUnauthorizedMedia);
            writer.WriteEndObject();
            writer.WriteStartObject("license");
            writer.WriteString("expression", specification.License.Expression);
            writer.WriteString("status", specification.License.Status);
            writer.WriteString("file", specification.License.File);
            writer.WriteEndObject();
            writer.WriteStartArray("corpora");
            foreach (GeneratedCorpusDraft draft in drafts)
            {
                MatrixEntry matrix = draft.Matrix;
                writer.WriteStartObject();
                writer.WriteString("id", matrix.Id);
                writer.WriteString("path", matrix.FileName);
                writer.WriteNumber("byteLength", draft.ByteLength);
                writer.WriteString("sha256", draft.Sha256);
                writer.WriteNumber("channelCount", matrix.ChannelCount);
                writer.WriteNumber("categoryCount", matrix.CategoryCount);
                writer.WriteNumber("logoReferenceCount", draft.LogoReferenceCount);
                writer.WriteString("expectedOutcome", matrix.ExpectedOutcome.ToString());
                if (matrix.ExpectedOutcome == M14CatalogCorpusExpectedOutcome.Success)
                {
                    writer.WriteNumber("expectedParsedEntryCount", matrix.ChannelCount);
                    writer.WriteNumber("expectedSkippedEntryCount", 1);
                }
                else
                {
                    writer.WriteNumber("entryLimit", 50_000);
                    writer.WriteString("expectedFailureCode", "UnsupportedPlaylistFormat");
                }

                writer.WriteStartObject("variationCounts");
                writer.WriteNumber("duplicateProviderIdentifierEntries", 2);
                writer.WriteNumber("missingGroupEntries", 1);
                writer.WriteNumber("malformedMetadataEntries", 1);
                writer.WriteNumber("unicodeEntries", 1);
                writer.WriteNumber("maximumLengthNameEntries", 1);
                writer.WriteNumber("tokenBearingLocatorEntries", 1);
                writer.WriteNumber("invalidLogoEntries", 1);
                writer.WriteNumber("stableKeyCollisionEntries", 2);
                writer.WriteEndObject();
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return stream.ToArray();
    }

    private static void ValidateSpecification(M14CatalogCorpusSpecification specification)
    {
        if (specification.SchemaVersion != 1 ||
            specification.AlgorithmVersion != AlgorithmVersion ||
            specification.Seed != ExpectedSeed ||
            !string.Equals(specification.FixtureSetId, ExpectedFixtureSetId, StringComparison.Ordinal) ||
            !string.Equals(specification.GeneratorName, GeneratorName, StringComparison.Ordinal) ||
            !string.Equals(specification.GeneratorVersion, GeneratorVersion, StringComparison.Ordinal) ||
            !string.Equals(specification.Provenance, ExpectedProvenance, StringComparison.Ordinal))
        {
            throw new InvalidDataException("M14 catalog corpus identity or generator contract changed.");
        }

        if (specification.ContainsThirdPartyContent ||
            specification.ContainsPersonalData ||
            specification.ContainsRealCredentials ||
            specification.ContainsUnauthorizedMedia)
        {
            throw new InvalidDataException("M14 catalog corpora must be wholly synthetic and contain no protected or third-party data.");
        }

        if (!string.Equals(specification.License.Expression, "LicenseRef-IPTVSuite-Synthetic-Test-Only", StringComparison.Ordinal) ||
            !string.Equals(specification.License.Status, "UNVERIFIED", StringComparison.Ordinal) ||
            !string.Equals(
                specification.License.File,
                "apps/windows/testdata/LICENSES/LicenseRef-IPTVSuite-Synthetic-Test-Only.txt",
                StringComparison.Ordinal))
        {
            throw new InvalidDataException("M14 catalog corpus license metadata changed.");
        }

        if (specification.Corpora is null || specification.Corpora.Count != ExpectedMatrix.Length)
        {
            throw new InvalidDataException("M14 catalog corpus matrix must contain the exact six definitions.");
        }

        for (int index = 0; index < ExpectedMatrix.Length; index++)
        {
            M14CatalogCorpusDefinition actual = specification.Corpora[index];
            MatrixEntry expected = ExpectedMatrix[index];
            if (actual is null ||
                !string.Equals(actual.Id, expected.Id, StringComparison.Ordinal) ||
                actual.ChannelCount != expected.ChannelCount ||
                actual.CategoryCount != expected.CategoryCount ||
                actual.LogoPercent != expected.LogoPercent ||
                actual.ExpectedOutcome != expected.ExpectedOutcome)
            {
                throw new InvalidDataException("M14 catalog corpus matrix changed.");
            }
        }
    }

    private static void EnsureDirectoryIsEmpty(string outputRoot)
    {
        if (Directory.EnumerateFileSystemEntries(outputRoot).Any())
        {
            throw new IOException("M14 catalog corpus output directory must be empty.");
        }
    }

    private sealed record MatrixEntry(
        string Id,
        string FileName,
        int ChannelCount,
        int CategoryCount,
        int LogoPercent,
        M14CatalogCorpusExpectedOutcome ExpectedOutcome);

    private sealed record CorpusEntry(
        string? TvgId,
        string Name,
        string? Group,
        string? Logo,
        string Locator);

    private sealed record GeneratedCorpusDraft(
        MatrixEntry Matrix,
        string StagingPath,
        long ByteLength,
        string Sha256,
        int LogoReferenceCount);
}

[JsonConverter(typeof(JsonStringEnumConverter<M14CatalogCorpusExpectedOutcome>))]
public enum M14CatalogCorpusExpectedOutcome
{
    Success,
    EntryLimitFailClosed,
}

public sealed record M14CatalogCorpusSpecification(
    int SchemaVersion,
    string FixtureSetId,
    string GeneratorName,
    string GeneratorVersion,
    int AlgorithmVersion,
    long Seed,
    string Provenance,
    bool ContainsThirdPartyContent,
    bool ContainsPersonalData,
    bool ContainsRealCredentials,
    bool ContainsUnauthorizedMedia,
    FixtureLicense License,
    IReadOnlyList<M14CatalogCorpusDefinition> Corpora);

public sealed record M14CatalogCorpusDefinition(
    string Id,
    int ChannelCount,
    int CategoryCount,
    int LogoPercent,
    M14CatalogCorpusExpectedOutcome ExpectedOutcome);

public sealed record GeneratedM14CatalogCorpusSet(
    string ManifestPath,
    IReadOnlyList<GeneratedM14CatalogCorpus> Corpora);

public sealed record GeneratedM14CatalogCorpus(
    string Id,
    string Path,
    string Sha256,
    long ByteLength,
    int ChannelCount,
    int CategoryCount,
    int LogoReferenceCount,
    M14CatalogCorpusExpectedOutcome ExpectedOutcome);
