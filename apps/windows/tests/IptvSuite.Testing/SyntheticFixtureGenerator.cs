using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace IptvSuite.Testing;

public static class SyntheticFixtureGenerator
{
    public const string GeneratorName = "IptvSuite.Testing.SyntheticFixtureGenerator";
    public const string GeneratorVersion = "1.0.0";

    private static readonly JsonSerializerOptions SpecificationOptions = new(JsonSerializerDefaults.Web)
    {
        ReadCommentHandling = JsonCommentHandling.Disallow,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    public static FixtureSpecification LoadSpecification(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        FixtureSpecification specification = JsonSerializer.Deserialize<FixtureSpecification>(
            File.ReadAllBytes(path),
            SpecificationOptions) ?? throw new InvalidDataException("Fixture specification is empty.");

        ValidateSpecification(specification);
        return specification;
    }

    public static async Task<GeneratedFixtureSet> GenerateAsync(
        FixtureSpecification specification,
        string outputDirectory,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(specification);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        ValidateSpecification(specification);

        string outputRoot = Path.GetFullPath(outputDirectory);
        Directory.CreateDirectory(outputRoot);
        EnsureDirectoryIsEmpty(outputRoot);

        byte[] records = GenerateRecords(specification);
        string recordsHash = Convert.ToHexString(SHA256.HashData(records)).ToLowerInvariant();
        string recordsPath = Path.Combine(outputRoot, "records.json");
        await File.WriteAllBytesAsync(recordsPath, records, cancellationToken).ConfigureAwait(false);

        byte[] manifest = GenerateManifest(specification, records.Length, recordsHash);
        string manifestPath = Path.Combine(outputRoot, "fixture-manifest.json");
        await File.WriteAllBytesAsync(manifestPath, manifest, cancellationToken).ConfigureAwait(false);

        return new GeneratedFixtureSet(recordsPath, manifestPath, recordsHash);
    }

    private static byte[] GenerateRecords(FixtureSpecification specification)
    {
        using MemoryStream stream = new();
        using (Utf8JsonWriter writer = new(stream, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schemaVersion", 1);
            writer.WriteString("fixtureSetId", specification.FixtureSetId);
            writer.WriteNumber("seed", specification.Seed);
            writer.WriteStartArray("records");

            for (int index = 0; index < specification.RecordCount; index++)
            {
                writer.WriteStartObject();
                writer.WriteString("id", $"synthetic-{index:D4}");
                writer.WriteString("label", $"Synthetic Record {index:D4}");
                writer.WriteNumber("value", (specification.Seed + (index * 37L)) % 1000L);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return stream.ToArray();
    }

    private static byte[] GenerateManifest(FixtureSpecification specification, int byteLength, string sha256)
    {
        using MemoryStream stream = new();
        using (Utf8JsonWriter writer = new(stream, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schemaVersion", 1);
            writer.WriteString("fixtureSetId", specification.FixtureSetId);
            writer.WriteStartObject("generator");
            writer.WriteString("name", GeneratorName);
            writer.WriteString("version", GeneratorVersion);
            writer.WriteNumber("algorithmVersion", specification.AlgorithmVersion);
            writer.WriteNumber("seed", specification.Seed);
            writer.WriteEndObject();
            writer.WriteStartObject("provenance");
            writer.WriteString("kind", "synthetic");
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
            writer.WriteStartArray("files");
            writer.WriteStartObject();
            writer.WriteString("path", "records.json");
            writer.WriteString("mediaType", "application/json");
            writer.WriteNumber("byteLength", byteLength);
            writer.WriteString("sha256", sha256);
            writer.WriteString("purpose", "M2 deterministic fixture harness smoke");
            writer.WriteString("expectedResultVersion", "1");
            writer.WriteEndObject();
            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return stream.ToArray();
    }

    private static void ValidateSpecification(FixtureSpecification specification)
    {
        if (specification.SchemaVersion != 1 ||
            specification.AlgorithmVersion != 1 ||
            specification.RecordCount is < 1 or > 1000 ||
            !string.Equals(specification.GeneratorName, GeneratorName, StringComparison.Ordinal) ||
            !string.Equals(specification.GeneratorVersion, GeneratorVersion, StringComparison.Ordinal))
        {
            throw new InvalidDataException("Fixture specification uses an unsupported schema, generator or record count.");
        }

        if (specification.ContainsThirdPartyContent ||
            specification.ContainsPersonalData ||
            specification.ContainsRealCredentials ||
            specification.ContainsUnauthorizedMedia)
        {
            throw new InvalidDataException("M2 fixtures must be wholly synthetic and contain no protected or third-party data.");
        }

        if (!string.Equals(specification.License.Expression, "LicenseRef-IPTVSuite-Synthetic-Test-Only", StringComparison.Ordinal) ||
            !string.Equals(specification.License.Status, "UNVERIFIED", StringComparison.Ordinal) ||
            !string.Equals(
                specification.License.File,
                "../LICENSES/LicenseRef-IPTVSuite-Synthetic-Test-Only.txt",
                StringComparison.Ordinal))
        {
            throw new InvalidDataException("Fixture license metadata does not match the M2 internal-only LicenseRef.");
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(specification.FixtureSetId);
        if (specification.FixtureSetId.Any(character =>
                !(char.IsAsciiLetterOrDigit(character) || character is '-' or '_')))
        {
            throw new InvalidDataException("Fixture set id contains unsupported characters.");
        }
    }

    private static void EnsureDirectoryIsEmpty(string outputRoot)
    {
        if (Directory.EnumerateFileSystemEntries(outputRoot).Any())
        {
            throw new IOException("Fixture output directory must be empty.");
        }
    }
}

public sealed record FixtureSpecification(
    int SchemaVersion,
    string FixtureSetId,
    string GeneratorName,
    string GeneratorVersion,
    int AlgorithmVersion,
    long Seed,
    int RecordCount,
    bool ContainsThirdPartyContent,
    bool ContainsPersonalData,
    bool ContainsRealCredentials,
    bool ContainsUnauthorizedMedia,
    FixtureLicense License);

public sealed record FixtureLicense(string Expression, string Status, string File);

public sealed record GeneratedFixtureSet(string RecordsPath, string ManifestPath, string RecordsSha256);
