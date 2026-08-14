using System.Text.Json;
using System.Text.Json.Serialization;

namespace IptvSuite.ProtectedCatalogSpike;

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

    public required string BaselineGeneratorName { get; init; }

    public required BaselineEvidenceSpecification BaselineEvidence { get; init; }

    public required int AlgorithmVersion { get; init; }

    public required int Seed { get; init; }

    public required int PayloadByteLength { get; init; }

    public required string Provenance { get; init; }

    public required bool ContainsThirdPartyContent { get; init; }

    public required bool ContainsPersonalData { get; init; }

    public required bool ContainsRealCredentials { get; init; }

    public required bool ContainsUnauthorizedMedia { get; init; }

    public required SpikeLicenseSpecification License { get; init; }

    public required CatalogFormatSpecification Format { get; init; }

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
            !string.Equals(specification.FixtureSetId, "m4-protected-catalog-spike-v1", StringComparison.Ordinal) ||
            !string.Equals(
                specification.GeneratorName,
                "IptvSuite.ProtectedCatalogSpike.DeterministicPayloadGenerator",
                StringComparison.Ordinal) ||
            !string.Equals(
                specification.BaselineGeneratorName,
                "IptvSuite.SecretStoreSpike.DeterministicPayloadGenerator",
                StringComparison.Ordinal) ||
            !IsBaselineEvidenceContract(specification.BaselineEvidence) ||
            specification.AlgorithmVersion != 1 ||
            specification.Seed != 20260813 ||
            specification.PayloadByteLength != DeterministicPayloadGenerator.PayloadByteLength ||
            !string.Equals(specification.Provenance, "synthetic", StringComparison.Ordinal) ||
            specification.ContainsThirdPartyContent ||
            specification.ContainsPersonalData ||
            specification.ContainsRealCredentials ||
            specification.ContainsUnauthorizedMedia ||
            !IsLicenseContract(specification.License) ||
            !IsFormatContract(specification.Format) ||
            !IsSmokeContract(specification.Smoke) ||
            !IsDecisionContract(specification.Decision))
        {
            throw new InvalidDataException("The protected-catalog specification does not match the fixed M4 contract.");
        }

        return specification;
    }

    internal SpikeModeSpecification GetMode(SpikeMode mode) => mode switch
    {
        SpikeMode.Smoke => Smoke,
        SpikeMode.Decision => Decision,
        _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unsupported spike mode."),
    };

    private static bool IsFormatContract(CatalogFormatSpecification format) =>
        format.Version == ProtectedCatalogFormat.Version &&
        format.MaximumRecordsPerDek == ProtectedCatalogFormat.MaximumRecordCount &&
        format.DekByteLength == ProtectedCatalogFormat.DekSize &&
        format.NonceByteLength == ProtectedCatalogFormat.NonceSize &&
        format.TagByteLength == ProtectedCatalogFormat.TagSize &&
        format.ReadProbeCount == 256 &&
        string.Equals(format.DpapiScope, "CurrentUser", StringComparison.Ordinal) &&
        string.Equals(
            format.DpapiEntropyContext,
            ProtectedCatalogFormat.EntropyContext,
            StringComparison.Ordinal);

    private static bool IsSmokeContract(SpikeModeSpecification mode) =>
        mode.Iterations == 1 &&
        mode.CancellationSamples == 1 &&
        mode.RecordCounts is [1_000] &&
        HasHashes(
            mode,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["1000"] = "d330726e2e886b1d61585c3fc276c6d5f20a1dfad85561749230ba35e99a40af",
            },
            "d330726e2e886b1d61585c3fc276c6d5f20a1dfad85561749230ba35e99a40af");

    private static bool IsDecisionContract(SpikeModeSpecification mode) =>
        mode.Iterations == 20 &&
        mode.CancellationSamples == 20 &&
        mode.RecordCounts.SequenceEqual([5_000, 10_000, 20_000, 50_000]) &&
        HasHashes(
            mode,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["5000"] = "80f110a11351dd95b3489f0a8973cc826f096334da0b4363e1d4b24e98082fe1",
                ["10000"] = "c4084013d6205597e412d47ec65329b8d671b9e0edb551a6d29c54cf34cd1512",
                ["20000"] = "94bb81ddc7d2afe6fc4b2935dd9d2dec5f1bf8e80b5444cf90e8e860b9512c86",
                ["50000"] = "88b5fad60d89e2fb6c16e9dac1a3372abb0779cdd216424833555b8f906ab232",
            },
            "eb6a4eaaecf437e80ef01feb00c6d1453e41994682a76ed08f81c1808a372f3f");

    private static bool HasHashes(
        SpikeModeSpecification mode,
        Dictionary<string, string> expectedScales,
        string expectedGlobal) =>
        mode.ExpectedScaleWorkloadSha256.Count == expectedScales.Count &&
        expectedScales.All(pair =>
            mode.ExpectedScaleWorkloadSha256.TryGetValue(pair.Key, out string? value) &&
            string.Equals(value, pair.Value, StringComparison.Ordinal)) &&
        string.Equals(mode.ExpectedWorkloadSha256, expectedGlobal, StringComparison.Ordinal);

    private static bool IsLicenseContract(SpikeLicenseSpecification license) =>
        string.Equals(license.Expression, "LicenseRef-IPTVSuite-Synthetic-Test-Only", StringComparison.Ordinal) &&
        string.Equals(license.Status, "UNVERIFIED", StringComparison.Ordinal) &&
        string.Equals(
            license.File,
            "../LICENSES/LicenseRef-IPTVSuite-Synthetic-Test-Only.txt",
            StringComparison.Ordinal);

    private static bool IsBaselineEvidenceContract(BaselineEvidenceSpecification baseline) =>
        string.Equals(
            baseline.WorkloadCommit,
            "fc96a211171d1e4f5e5f02174da6c565ef2d59bb",
            StringComparison.Ordinal) &&
        string.Equals(
            baseline.SpecificationSha256,
            "0447355215f8c744340a39640e55bc798916638b48e5386b213e7d3f06c7a568",
            StringComparison.Ordinal) &&
        string.Equals(
            baseline.RunnerAssemblySha256,
            "3df0676151a906f815bd0881994ffd3f7f347f2f7121a494409f85afcdeca119",
            StringComparison.Ordinal) &&
        string.Equals(
            baseline.DecisionSummarySha256,
            "8cd4c6d86b813fd07794217a71a824e7368694363f89a16be36cb8a311d67460",
            StringComparison.Ordinal) &&
        string.Equals(
            baseline.DecisionWorkloadSha256,
            "eb6a4eaaecf437e80ef01feb00c6d1453e41994682a76ed08f81c1808a372f3f",
            StringComparison.Ordinal) &&
        string.Equals(
            baseline.EvidenceRecordCommit,
            "207455a54d2d7ac9b6b5c1ce8eb5e29bbee0c383",
            StringComparison.Ordinal);
}

internal sealed class BaselineEvidenceSpecification
{
    public required string WorkloadCommit { get; init; }

    public required string SpecificationSha256 { get; init; }

    public required string RunnerAssemblySha256 { get; init; }

    public required string DecisionSummarySha256 { get; init; }

    public required string DecisionWorkloadSha256 { get; init; }

    public required string EvidenceRecordCommit { get; init; }
}

internal sealed class SpikeModeSpecification
{
    public required int[] RecordCounts { get; init; }

    public required int Iterations { get; init; }

    public required int CancellationSamples { get; init; }

    public required Dictionary<string, string> ExpectedScaleWorkloadSha256 { get; init; }

    public required string ExpectedWorkloadSha256 { get; init; }
}

internal sealed class CatalogFormatSpecification
{
    public required int Version { get; init; }

    public required int MaximumRecordsPerDek { get; init; }

    public required int DekByteLength { get; init; }

    public required int NonceByteLength { get; init; }

    public required int TagByteLength { get; init; }

    public required int ReadProbeCount { get; init; }

    public required string DpapiScope { get; init; }

    public required string DpapiEntropyContext { get; init; }
}

internal sealed class SpikeLicenseSpecification
{
    public required string Expression { get; init; }

    public required string Status { get; init; }

    public required string File { get; init; }
}
