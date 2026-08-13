using System.Text.Json;
using System.Text.Json.Serialization;
using IptvSuite.Testing;

namespace IptvSuite.SecretStoreSpike;

internal sealed record ScaleEvidence(
    int RecordCount,
    int Iterations,
    int ReadProbeCount,
    string WorkloadSequenceSha256,
    PhaseAggregate Create,
    PhaseAggregate Restart,
    PhaseAggregate ReadProbe,
    PhaseAggregate Delete,
    int ProtectedStoreCanaryScans);

internal sealed record PayloadEvidence(
    int ByteLength,
    string Generator,
    int AlgorithmVersion,
    int Seed,
    string Provenance,
    bool ContainsThirdPartyContent,
    bool ContainsPersonalData,
    bool ContainsRealCredentials,
    bool ContainsUnauthorizedMedia);

internal sealed record CancellationEvidence(
    int Samples,
    int TriggerAfterCommittedRecords,
    NumberSummary CompletionLatencyMilliseconds,
    NumberSummary CommittedAtCancellationLowerBound,
    int PostCancellationCommitUpperBound,
    int PostCompletionMutationCount,
    int TemporaryArtifactCount,
    int ProtectedStoreCanaryScans,
    string Cleanup);

internal sealed record SpikeEvidence(
    int SchemaVersion,
    string Milestone,
    string EvidenceKind,
    string Mode,
    string Configuration,
    string Platform,
    string ProtectionScope,
    RuntimeEvidence Runtime,
    RepositoryEvidence Repository,
    InputEvidence Inputs,
    PayloadEvidence Payload,
    string Warmup,
    IReadOnlyList<ScaleEvidence> Scales,
    CancellationEvidence Cancellation,
    string WorkloadSequenceSha256,
    string ProtectedStoreCanaryScan,
    string EvidenceCanaryScan,
    string Cleanup);

internal static class SpikeEvidenceWriter
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    internal static async Task WriteAndScanAsync(
        string evidenceRoot,
        SpikeMode mode,
        SpikeEvidence evidence,
        TestCanary canary,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        ArgumentNullException.ThrowIfNull(canary);
        string fileName = mode is SpikeMode.Smoke ? "smoke-summary.json" : "decision-summary.json";
        string summaryPath = GetContainedFile(evidenceRoot, fileName);
        string stagingRoot = GetContainedDirectory(evidenceRoot, $"staging-{Guid.NewGuid():N}");
        Directory.CreateDirectory(stagingRoot);
        string stagedSummaryPath = GetContainedFile(stagingRoot, fileName);

        try
        {
            await using (var stream = new FileStream(
                stagedSummaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 4096,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, evidence, JsonOptions, cancellationToken)
                    .ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            IReadOnlyList<CanaryFinding> findings = ArtifactCanaryScanner.Scan(stagingRoot, canary);
            if (findings.Count != 0)
            {
                throw new InvalidDataException("The spike evidence failed the canary scan.");
            }

            File.Move(stagedSummaryPath, summaryPath, overwrite: true);
        }
        finally
        {
            TryDeleteExactFile(stagedSummaryPath);
            TryDeleteEmptyStagingDirectory(stagingRoot);
        }
    }

    private static string GetContainedFile(string root, string fileName)
    {
        if (!string.Equals(Path.GetFileName(fileName), fileName, StringComparison.Ordinal))
        {
            throw new IOException("The spike evidence file name is invalid.");
        }

        string fullRoot = Path.GetFullPath(root);
        string candidate = Path.GetFullPath(Path.Combine(fullRoot, fileName));
        string prefix = Path.EndsInDirectorySeparator(fullRoot)
            ? fullRoot
            : fullRoot + Path.DirectorySeparatorChar;
        if (!candidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new IOException("The spike evidence path escaped its expected root.");
        }

        return candidate;
    }

    private static string GetContainedDirectory(string root, string directoryName)
    {
        string candidate = GetContainedFile(root, directoryName);
        if (!string.Equals(Path.GetFileName(candidate), directoryName, StringComparison.Ordinal))
        {
            throw new IOException("The spike evidence staging directory is invalid.");
        }

        return candidate;
    }

    private static void TryDeleteExactFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (FileNotFoundException)
        {
        }
        catch (DirectoryNotFoundException)
        {
        }
    }

    private static void TryDeleteEmptyStagingDirectory(string path)
    {
        try
        {
            Directory.Delete(path, recursive: false);
        }
        catch (DirectoryNotFoundException)
        {
        }
    }
}
