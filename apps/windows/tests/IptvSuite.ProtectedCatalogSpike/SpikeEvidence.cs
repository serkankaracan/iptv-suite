using System.Text.Json;
using System.Text.Json.Serialization;
using IptvSuite.Testing;

namespace IptvSuite.ProtectedCatalogSpike;

internal sealed record PayloadEvidence(
    int ByteLength,
    string Generator,
    string BaselineGenerator,
    int AlgorithmVersion,
    int Seed,
    string Provenance,
    bool ContainsThirdPartyContent,
    bool ContainsPersonalData,
    bool ContainsRealCredentials,
    bool ContainsUnauthorizedMedia);

internal sealed record FormatEvidence(
    int Version,
    string ByteOrder,
    string ContainerModel,
    int MaximumRecordsPerDek,
    int DekByteLength,
    int NonceByteLength,
    int TagByteLength,
    string AeadAlgorithm,
    string KeyWrapAlgorithm,
    string KeyGeneration,
    string NonceStrategy,
    string AadBindings,
    string DpapiEntropyBindings,
    string Activation,
    string PreActivationValidation,
    string DurabilityClaim);

internal sealed record BaselineEvidence(
    string Generator,
    int AlgorithmVersion,
    string WorkloadCommit,
    string SpecificationSha256,
    string RunnerAssemblySha256,
    string DecisionSummarySha256,
    string DecisionWorkloadSha256,
    string EvidenceRecordCommit);

internal sealed record ScaleEvidence(
    int RecordCount,
    int Iterations,
    int ReadProbeCount,
    string WorkloadSequenceSha256,
    PhaseEvidence CreateAndActivate,
    PhaseEvidence AdapterReopenAndUnwrap,
    PhaseEvidence ReadProbe,
    PhaseEvidence DeleteSnapshot,
    int DeleteRecordsCoveredPerSample,
    int DpapiWrapCount,
    int PreActivationDpapiUnwrapCount,
    int PreActivationTagProbeCount,
    int PostActivationDpapiUnwrapCount,
    int DpapiUnwrapCount,
    int NonceCount,
    int NonceCollisionRetryCount,
    IReadOnlyList<long> RawDiskBytes,
    NumberSummary DiskBytes,
    int ProtectedStoreCanaryScans);

internal sealed record FaultAndCancellationEvidence(
    int Samples,
    bool PreCancelledNoMutation,
    bool CancellationBeforeActivationPreservedPriorActive,
    bool FaultBeforeActivationPreservedPriorActive,
    bool PostActivationOutcomeClassifiedCommitted,
    bool PostActivationCancellationReturnedCommitted,
    IReadOnlyList<double> RawCancellationCompletionLatencyMilliseconds,
    NumberSummary CancellationCompletionLatencyMilliseconds,
    int PostCancellationActivationCount,
    int TemporaryArtifactCount,
    int ProtectedStoreCanaryScans,
    string ControlledFaultScope,
    string Cleanup);

internal sealed record StagingCancellationEvidence(
    int Samples,
    int WorkloadRecordCount,
    int TriggerAfterEncryptedRecords,
    IReadOnlyList<int> RawEncryptedAtCancellationLowerBound,
    NumberSummary EncryptedAtCancellationLowerBound,
    IReadOnlyList<double> RawCompletionLatencyMilliseconds,
    NumberSummary CompletionLatencyMilliseconds,
    int PostCancellationEncryptedRecordUpperBound,
    int PostCancellationActivationCount,
    int PostCompletionMutationCount,
    int TemporaryArtifactCount,
    int ProtectedStoreCanaryScans,
    string ProgressUnit,
    string Cleanup);

internal sealed record ReaderValidationEvidence(
    bool WrongSourceBindingFailedClosed,
    bool WrongSnapshotBindingFailedClosed,
    bool WrongKeyGenerationBindingFailedClosed,
    bool WrongPurposeBindingFailedClosed,
    bool WrongRecordBindingFailedClosed,
    bool WrongReferenceBindingFailedClosed,
    bool MagicCorruptionFailedClosed,
    bool VersionCorruptionFailedClosed,
    bool HeaderLengthCorruptionFailedClosed,
    bool CountCorruptionFailedClosed,
    bool RecordLengthCorruptionFailedClosed,
    bool OffsetCorruptionFailedClosed,
    bool OverlappingCiphertextFailedClosed,
    bool DuplicateOwnerFailedClosed,
    bool DuplicateReferenceFailedClosed,
    bool DuplicateNonceFailedClosed,
    bool IndexTupleAuthenticationFailedClosed,
    bool CrossContainerWrappedDekSwapFailedClosed,
    bool TagOrCiphertextCorruptionFailedClosed,
    bool TrailingBytesFailedClosed,
    bool InjectedNonceCollisionRetryPassed,
    int InjectedNonceCollisionRetryCount,
    bool IdempotentDeletePassed);

internal sealed record SpikeEvidence(
    int SchemaVersion,
    string Milestone,
    string EvidenceKind,
    string Mode,
    string Configuration,
    string Platform,
    string ProtectionScope,
    string CandidateId,
    string CandidateScope,
    string ProductionReadiness,
    SpikeEnvironmentEvidence Environment,
    PayloadEvidence Payload,
    BaselineEvidence Baseline,
    FormatEvidence Format,
    string Warmup,
    IReadOnlyList<ScaleEvidence> Scales,
    FaultAndCancellationEvidence FaultAndCancellation,
    StagingCancellationEvidence StagingCancellation,
    ReaderValidationEvidence ReaderValidation,
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
        string fileName = mode is SpikeMode.Smoke ? "smoke-summary.json" : "decision-summary.json";
        string summaryPath = GetContainedPath(evidenceRoot, fileName);
        string stagingRoot = GetContainedPath(evidenceRoot, $"staging-{Guid.NewGuid():N}");
        Directory.CreateDirectory(stagingRoot);
        string stagedPath = GetContainedPath(stagingRoot, fileName);

        try
        {
            await using (var stream = new FileStream(
                stagedPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 4_096,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, evidence, JsonOptions, cancellationToken)
                    .ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            if (ArtifactCanaryScanner.Scan(stagingRoot, canary).Count != 0)
            {
                throw new InvalidDataException("The protected-catalog evidence failed its canary scan.");
            }

            File.Move(stagedPath, summaryPath, overwrite: true);
        }
        finally
        {
            TryDeleteFile(stagedPath);
            try
            {
                Directory.Delete(stagingRoot, recursive: false);
            }
            catch (DirectoryNotFoundException)
            {
            }
        }
    }

    private static string GetContainedPath(string parent, string name)
    {
        string root = Path.GetFullPath(parent);
        string candidate = Path.GetFullPath(Path.Combine(root, name));
        string prefix = Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar;
        if (!candidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new IOException("The evidence path escaped its fixed root.");
        }

        return candidate;
    }

    private static void TryDeleteFile(string path)
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
}
