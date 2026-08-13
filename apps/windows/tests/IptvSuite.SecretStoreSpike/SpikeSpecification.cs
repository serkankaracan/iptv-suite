using System.Text.Json;
using System.Text.Json.Serialization;

namespace IptvSuite.SecretStoreSpike;

internal sealed class SpikeSpecification
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    public required int SchemaVersion { get; init; }

    public required string FixtureSetId { get; init; }

    public required string GeneratorName { get; init; }

    public required int AlgorithmVersion { get; init; }

    public required int Seed { get; init; }

    public required int PayloadByteLength { get; init; }

    public required string Provenance { get; init; }

    public required bool ContainsThirdPartyContent { get; init; }

    public required bool ContainsPersonalData { get; init; }

    public required bool ContainsRealCredentials { get; init; }

    public required bool ContainsUnauthorizedMedia { get; init; }

    public required SpikeLicenseSpecification License { get; init; }

    public required SpikeModeSpecification Smoke { get; init; }

    public required SpikeModeSpecification Decision { get; init; }

    internal static async Task<SpikeSpecification> LoadAndValidateAsync(
        string path,
        CancellationToken cancellationToken)
    {
        await using FileStream stream = File.OpenRead(path);
        SpikeSpecification? specification = await JsonSerializer.DeserializeAsync<SpikeSpecification>(
            stream,
            JsonOptions,
            cancellationToken).ConfigureAwait(false);

        if (specification is null ||
            specification.SchemaVersion != 1 ||
            !string.Equals(specification.FixtureSetId, "m4-secret-store-spike-v1", StringComparison.Ordinal) ||
            !string.Equals(
                specification.GeneratorName,
                "IptvSuite.SecretStoreSpike.DeterministicPayloadGenerator",
                StringComparison.Ordinal) ||
            specification.AlgorithmVersion != 1 ||
            specification.Seed != 20260813 ||
            specification.PayloadByteLength != DeterministicPayloadGenerator.PayloadByteLength ||
            !string.Equals(specification.Provenance, "synthetic", StringComparison.Ordinal) ||
            specification.ContainsThirdPartyContent ||
            specification.ContainsPersonalData ||
            specification.ContainsRealCredentials ||
            specification.ContainsUnauthorizedMedia ||
            !IsLicenseContract(specification.License) ||
            !IsSmokeContract(specification.Smoke) ||
            !IsDecisionContract(specification.Decision))
        {
            throw new InvalidDataException("The spike specification does not match the fixed M4 contract.");
        }

        return specification;
    }

    internal SpikeModeSpecification GetMode(SpikeMode mode) => mode switch
    {
        SpikeMode.Smoke => Smoke,
        SpikeMode.Decision => Decision,
        _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unsupported spike mode."),
    };

    private static bool IsSmokeContract(SpikeModeSpecification mode) =>
        mode.Iterations == 1 &&
        mode.CancellationSamples == 1 &&
        mode.RecordCounts is [> 0 and <= 1_000];

    private static bool IsDecisionContract(SpikeModeSpecification mode) =>
        mode.Iterations == 20 &&
        mode.CancellationSamples == 20 &&
        mode.RecordCounts.SequenceEqual([5_000, 10_000, 20_000, 50_000]);

    private static bool IsLicenseContract(SpikeLicenseSpecification license) =>
        string.Equals(
            license.Expression,
            "LicenseRef-IPTVSuite-Synthetic-Test-Only",
            StringComparison.Ordinal) &&
        string.Equals(license.Status, "UNVERIFIED", StringComparison.Ordinal) &&
        string.Equals(
            license.File,
            "../LICENSES/LicenseRef-IPTVSuite-Synthetic-Test-Only.txt",
            StringComparison.Ordinal);
}

internal sealed class SpikeModeSpecification
{
    public required int[] RecordCounts { get; init; }

    public required int Iterations { get; init; }

    public required int CancellationSamples { get; init; }
}

internal sealed class SpikeLicenseSpecification
{
    public required string Expression { get; init; }

    public required string Status { get; init; }

    public required string File { get; init; }
}
