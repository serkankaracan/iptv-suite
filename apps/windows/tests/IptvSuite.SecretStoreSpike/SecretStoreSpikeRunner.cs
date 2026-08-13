using System.Diagnostics;
using System.Security.Cryptography;
using System.Runtime.Versioning;
using IptvSuite.Application;
using IptvSuite.Domain;
using IptvSuite.Infrastructure;
using IptvSuite.Testing;

namespace IptvSuite.SecretStoreSpike;

[SupportedOSPlatform("windows")]
internal static class SecretStoreSpikeRunner
{
    private const int MaximumReadProbeCount = 256;
    private const int CancellationTriggerRecordCount = 10;
    private static readonly TimeSpan CancellationTriggerTimeout = TimeSpan.FromSeconds(30);

    internal static async Task RunAsync(SpikeInvocation invocation, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(invocation);
        SafeSpikeWorkspace workspace = SafeSpikeWorkspace.OpenFromCurrentDirectory();
        using FileStream runLock = workspace.AcquireExclusiveRunLock();
        workspace.Prepare();

        try
        {
            SpikeSpecification specification = await SpikeSpecification.LoadAndValidateAsync(
                workspace.SpecificationPath,
                cancellationToken).ConfigureAwait(false);
            SpikeModeSpecification mode = specification.GetMode(invocation.Mode);
            SpikeEnvironmentEvidence environment = await SpikeEnvironmentEvidenceCollector.CollectAsync(
                workspace,
                specification,
                invocation.Mode,
                cancellationToken).ConfigureAwait(false);
            TestCanary canary = TestCanary.Create("M4-SPIKE", "PAYLOAD-V1");
            await RunWarmupAsync(workspace, canary, specification, cancellationToken).ConfigureAwait(false);
            List<ScaleEvidence> scales = [];
            using IncrementalHash workloadSequenceHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

            foreach (int recordCount in mode.RecordCounts)
            {
                scales.Add(await RunScaleAsync(
                    workspace,
                    canary,
                    specification,
                    workloadSequenceHash,
                    recordCount,
                    mode.Iterations,
                    cancellationToken).ConfigureAwait(false));
            }

            CancellationEvidence cancellation = await RunCancellationProbeAsync(
                workspace,
                canary,
                specification,
                mode.CancellationSamples,
                cancellationToken).ConfigureAwait(false);

            string workloadSequenceSha256 = GetLowerHexDigest(workloadSequenceHash);
            workspace.Complete();
            await SpikeEnvironmentEvidenceCollector.AssertRepositoryStateUnchangedAsync(
                workspace,
                environment.Repository,
                invocation.Mode,
                cancellationToken).ConfigureAwait(false);
            var evidence = new SpikeEvidence(
                SchemaVersion: 1,
                Milestone: "M4",
                EvidenceKind: "secret-store-performance-spike",
                Mode: invocation.Mode.ToString(),
                Configuration: "Release",
                Platform: "x64",
                ProtectionScope: "CurrentUser",
                Runtime: environment.Runtime,
                Repository: environment.Repository,
                Inputs: environment.Inputs,
                Payload: new PayloadEvidence(
                    specification.PayloadByteLength,
                    specification.GeneratorName,
                    specification.AlgorithmVersion,
                    specification.Seed,
                    specification.Provenance,
                    specification.ContainsThirdPartyContent,
                    specification.ContainsPersonalData,
                    specification.ContainsRealCredentials,
                    specification.ContainsUnauthorizedMedia),
                Warmup: "passed",
                Scales: scales,
                Cancellation: cancellation,
                WorkloadSequenceSha256: workloadSequenceSha256,
                ProtectedStoreCanaryScan: "passed-before-delete",
                EvidenceCanaryScan: "passed-before-publish",
                Cleanup: "passed");

            await SpikeEvidenceWriter.WriteAndScanAsync(
                workspace.EvidenceRoot,
                invocation.Mode,
                evidence,
                canary,
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            workspace.Complete();
        }
    }

    private static async Task RunWarmupAsync(
        SafeSpikeWorkspace workspace,
        TestCanary canary,
        SpikeSpecification specification,
        CancellationToken cancellationToken)
    {
        const int warmupRecordCount = 16;
        string storeDirectory = workspace.CreateWarmupStoreDirectory();
        byte[] payload = GC.AllocateUninitializedArray<byte>(DeterministicPayloadGenerator.PayloadByteLength);

        try
        {
            SourceId sourceId = SourceId.Generate();
            ProtectedRecordOwner recordOwner =
                ProtectedRecordOwner.ForChannel(ChannelId.Generate());
            var references = new List<ProtectedLocatorReference>(warmupRecordCount);
            var store = new DpapiCurrentUserSecretStore(storeDirectory, cancellationToken);

            for (int ordinal = 0; ordinal < warmupRecordCount; ordinal++)
            {
                DeterministicPayloadGenerator.Fill(
                    payload,
                    specification,
                    warmupRecordCount,
                    iteration: 1,
                    ordinal);
                ProtectedLocatorReferenceCreationResult created = await store.CreateLocatorAsync(
                    sourceId,
                    ProtectedValuePurpose.ChannelStreamLocator,
                    recordOwner,
                    payload,
                    cancellationToken).ConfigureAwait(false);
                if (!created.IsSuccess || created.Reference is null)
                {
                    throw new InvalidOperationException("A warmup record could not be created.");
                }

                references.Add(created.Reference);
            }

            store = new DpapiCurrentUserSecretStore(storeDirectory, cancellationToken);
            foreach (int ordinal in new[] { 0, warmupRecordCount - 1 })
            {
                DeterministicPayloadGenerator.Fill(
                    payload,
                    specification,
                    warmupRecordCount,
                    iteration: 1,
                    ordinal);
                SecretStoreReadResult read = await store.ReadLocatorAsync(
                    sourceId,
                    ProtectedValuePurpose.ChannelStreamLocator,
                    recordOwner,
                    references[ordinal],
                    cancellationToken).ConfigureAwait(false);
                using SecretLease? lease = read.Lease;
                if (!read.IsSuccess ||
                    lease is null ||
                    !CryptographicOperations.FixedTimeEquals(lease.Value.Span, payload))
                {
                    throw new InvalidDataException("A warmup read probe failed.");
                }
            }

            if (ArtifactCanaryScanner.Scan(storeDirectory, canary).Count != 0)
            {
                throw new InvalidDataException("The warmup store failed the canary scan.");
            }

            foreach (ProtectedLocatorReference reference in references)
            {
                SecretStoreOperationResult deleted = await store.DeleteLocatorAsync(
                    sourceId,
                    ProtectedValuePurpose.ChannelStreamLocator,
                    recordOwner,
                    reference,
                    cancellationToken).ConfigureAwait(false);
                if (!deleted.IsSuccess)
                {
                    throw new InvalidOperationException("A warmup record could not be deleted.");
                }
            }

            if (CountProtectedRecords(storeDirectory) != 0)
            {
                throw new IOException("Warmup records remained after cleanup.");
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(payload);
            workspace.DeleteStoreDirectory(storeDirectory);
        }
    }

    private static async Task<CancellationEvidence> RunCancellationProbeAsync(
        SafeSpikeWorkspace workspace,
        TestCanary canary,
        SpikeSpecification specification,
        int samples,
        CancellationToken cancellationToken)
    {
        var latencySamples = new List<double>(samples);
        var committedAtCancellationSamples = new List<double>(samples);
        int postCancellationCommitUpperBound = 0;
        int postCompletionMutationCount = 0;
        int temporaryArtifactCount = 0;

        for (int sample = 1; sample <= samples; sample++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string storeDirectory = workspace.CreateCancellationStoreDirectory(sample);
            byte[] payload = GC.AllocateUninitializedArray<byte>(DeterministicPayloadGenerator.PayloadByteLength);
            var references = new List<ProtectedLocatorReference>(CancellationTriggerRecordCount + 2);
            using CancellationTokenSource probeCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken);
            var trigger = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Task? writer = null;
            int committedRecordCount = 0;

            try
            {
                SourceId sourceId = SourceId.Generate();
                ProtectedRecordOwner recordOwner =
                    ProtectedRecordOwner.ForChannel(ChannelId.Generate());
                var store = new DpapiCurrentUserSecretStore(storeDirectory, cancellationToken);
                bool cancellationObserved = false;
                writer = Task.Run(async () =>
                {
                    try
                    {
                        for (int ordinal = 0; ordinal < 50_000; ordinal++)
                        {
                            DeterministicPayloadGenerator.Fill(
                                payload,
                                specification,
                                50_000,
                                sample,
                                ordinal);
                            ProtectedLocatorReferenceCreationResult result = await store.CreateLocatorAsync(
                                sourceId,
                                ProtectedValuePurpose.ChannelStreamLocator,
                                recordOwner,
                                payload,
                                probeCancellation.Token).ConfigureAwait(false);
                            if (!result.IsSuccess || result.Reference is null)
                            {
                                throw new InvalidOperationException("A cancellation-probe record could not be created.");
                            }

                            references.Add(result.Reference);
                            int committedCount = Interlocked.Increment(ref committedRecordCount);
                            if (committedCount == CancellationTriggerRecordCount)
                            {
                                trigger.TrySetResult();
                            }
                        }
                    }
                    catch (OperationCanceledException) when (probeCancellation.IsCancellationRequested)
                    {
                        cancellationObserved = true;
                    }
                }, CancellationToken.None);

                await trigger.Task.WaitAsync(CancellationTriggerTimeout, cancellationToken).ConfigureAwait(false);
                int committedAtCancellationLowerBound = Volatile.Read(ref committedRecordCount);
                long cancellationStarted = Stopwatch.GetTimestamp();
                probeCancellation.Cancel();
                await writer.ConfigureAwait(false);
                latencySamples.Add(Stopwatch.GetElapsedTime(cancellationStarted).TotalMilliseconds);

                if (!cancellationObserved)
                {
                    throw new InvalidOperationException("The cancellation probe did not observe cancellation.");
                }

                int committedAtCompletion = Volatile.Read(ref committedRecordCount);
                if (committedAtCompletion != references.Count)
                {
                    throw new InvalidOperationException("The cancellation probe count became inconsistent.");
                }

                committedAtCancellationSamples.Add(committedAtCancellationLowerBound);
                postCancellationCommitUpperBound += Math.Max(
                    0,
                    committedAtCompletion - committedAtCancellationLowerBound);
                int countAtCompletion = CountProtectedRecords(storeDirectory);
                await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken).ConfigureAwait(false);
                int countAfterDelay = CountProtectedRecords(storeDirectory);
                postCompletionMutationCount += Math.Max(0, countAfterDelay - countAtCompletion);
                temporaryArtifactCount += Directory.EnumerateFiles(
                    storeDirectory,
                    "temporary-v2-*.tmp",
                    SearchOption.TopDirectoryOnly).Count();
                IReadOnlyList<CanaryFinding> findings = ArtifactCanaryScanner.Scan(storeDirectory, canary);
                if (findings.Count != 0)
                {
                    throw new InvalidDataException("A cancellation-probe store failed the canary scan.");
                }

                foreach (ProtectedLocatorReference reference in references)
                {
                    SecretStoreOperationResult deleted = await store.DeleteLocatorAsync(
                        sourceId,
                        ProtectedValuePurpose.ChannelStreamLocator,
                        recordOwner,
                        reference,
                        cancellationToken).ConfigureAwait(false);
                    if (!deleted.IsSuccess)
                    {
                        throw new InvalidOperationException("A cancellation-probe record could not be cleaned.");
                    }
                }

                if (CountProtectedRecords(storeDirectory) != 0)
                {
                    throw new IOException("Cancellation-probe records remained after cleanup.");
                }
            }
            finally
            {
                probeCancellation.Cancel();
                try
                {
                    if (writer is not null)
                    {
                        // Keep the exclusive run lock and the workspace intact until the writer
                        // has actually stopped. A timeout here could release the lock while an
                        // in-flight DPAPI write still owns the payload or store directory.
                        await writer.ConfigureAwait(false);
                    }
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(payload);
                    workspace.DeleteStoreDirectory(storeDirectory);
                }
            }
        }

        if (postCompletionMutationCount != 0 || temporaryArtifactCount != 0)
        {
            throw new InvalidOperationException("The cancellation probe left a mutation or temporary artifact.");
        }

        return new CancellationEvidence(
            samples,
            CancellationTriggerRecordCount,
            NumberSummary.From(latencySamples),
            NumberSummary.From(committedAtCancellationSamples),
            postCancellationCommitUpperBound,
            postCompletionMutationCount,
            temporaryArtifactCount,
            samples,
            "passed");
    }

    private static int CountProtectedRecords(string storeDirectory) =>
        Directory.EnumerateFiles(
            storeDirectory,
            "record-v2-*.dpapi",
            SearchOption.TopDirectoryOnly).Count();

    private static async Task<ScaleEvidence> RunScaleAsync(
        SafeSpikeWorkspace workspace,
        TestCanary canary,
        SpikeSpecification specification,
        IncrementalHash workloadSequenceHash,
        int recordCount,
        int iterations,
        CancellationToken cancellationToken)
    {
        var createSamples = new List<PhaseSample>(iterations);
        var restartSamples = new List<PhaseSample>(iterations);
        var readSamples = new List<PhaseSample>(iterations);
        var deleteSamples = new List<PhaseSample>(iterations);
        int readProbeCount = Math.Min(recordCount, MaximumReadProbeCount);
        using IncrementalHash scaleSequenceHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        for (int iteration = 1; iteration <= iterations; iteration++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string storeDirectory = workspace.CreateStoreDirectory(recordCount, iteration);
            byte[] payload = GC.AllocateUninitializedArray<byte>(DeterministicPayloadGenerator.PayloadByteLength);

            try
            {
                SourceId sourceId = SourceId.Generate();
                ProtectedRecordOwner recordOwner =
                    ProtectedRecordOwner.ForChannel(ChannelId.Generate());
                var references = new List<ProtectedLocatorReference>(recordCount);
                ISecretStore store = new DpapiCurrentUserSecretStore(storeDirectory, cancellationToken);

                createSamples.Add(await PhaseSample.MeasureAsync(recordCount, async () =>
                {
                    for (int index = 0; index < recordCount; index++)
                    {
                        DeterministicPayloadGenerator.Fill(
                            payload,
                            specification,
                            recordCount,
                            iteration,
                            index);
                        scaleSequenceHash.AppendData(payload);
                        workloadSequenceHash.AppendData(payload);
                        ProtectedLocatorReferenceCreationResult result = await store.CreateLocatorAsync(
                            sourceId,
                            ProtectedValuePurpose.ChannelStreamLocator,
                            recordOwner,
                            payload,
                            cancellationToken).ConfigureAwait(false);
                        if (!result.IsSuccess || result.Reference is null)
                        {
                            throw new InvalidOperationException("A protected record could not be created.");
                        }

                        references.Add(result.Reference);
                    }
                }).ConfigureAwait(false));

                IReadOnlyList<CanaryFinding> findings = ArtifactCanaryScanner.Scan(storeDirectory, canary);
                if (findings.Count != 0)
                {
                    throw new InvalidDataException("A protected store failed the canary scan.");
                }

                ISecretStore? restartedStore = null;
                restartSamples.Add(await PhaseSample.MeasureAsync(1, () =>
                {
                    restartedStore = new DpapiCurrentUserSecretStore(storeDirectory, cancellationToken);
                    return ValueTask.CompletedTask;
                }).ConfigureAwait(false));
                store = restartedStore ?? throw new InvalidOperationException("The protected store did not restart.");

                int[] probeIndexes = CreateProbeIndexes(recordCount, readProbeCount);
                readSamples.Add(await PhaseSample.MeasureAsync(readProbeCount, async () =>
                {
                    foreach (int index in probeIndexes)
                    {
                        DeterministicPayloadGenerator.Fill(
                            payload,
                            specification,
                            recordCount,
                            iteration,
                            index);
                        SecretStoreReadResult result = await store.ReadLocatorAsync(
                            sourceId,
                            ProtectedValuePurpose.ChannelStreamLocator,
                            recordOwner,
                            references[index],
                            cancellationToken).ConfigureAwait(false);
                        using SecretLease? lease = result.Lease;
                        if (!result.IsSuccess ||
                            lease is null ||
                            !CryptographicOperations.FixedTimeEquals(lease.Value.Span, payload))
                        {
                            throw new InvalidDataException("A protected record read probe failed.");
                        }
                    }
                }).ConfigureAwait(false));

                deleteSamples.Add(await PhaseSample.MeasureAsync(recordCount, async () =>
                {
                    foreach (ProtectedLocatorReference reference in references)
                    {
                        SecretStoreOperationResult result = await store.DeleteLocatorAsync(
                            sourceId,
                            ProtectedValuePurpose.ChannelStreamLocator,
                            recordOwner,
                            reference,
                            cancellationToken).ConfigureAwait(false);
                        if (!result.IsSuccess)
                        {
                            throw new InvalidOperationException("A protected record could not be deleted.");
                        }
                    }
                }).ConfigureAwait(false));

                if (Directory.EnumerateFiles(storeDirectory, "record-v2-*.dpapi", SearchOption.TopDirectoryOnly).Any())
                {
                    throw new IOException("Protected records remained after the delete phase.");
                }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(payload);
                workspace.DeleteStoreDirectory(storeDirectory);
            }
        }

        string scaleSequenceSha256 = GetLowerHexDigest(scaleSequenceHash);
        return new ScaleEvidence(
            recordCount,
            iterations,
            readProbeCount,
            scaleSequenceSha256,
            PhaseAggregate.From(createSamples),
            PhaseAggregate.From(restartSamples),
            PhaseAggregate.From(readSamples),
            PhaseAggregate.From(deleteSamples),
            iterations);
    }

    private static string GetLowerHexDigest(IncrementalHash hash)
    {
        byte[] digest = hash.GetHashAndReset();

        try
        {
            return Convert.ToHexStringLower(digest);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(digest);
        }
    }

    private static int[] CreateProbeIndexes(int recordCount, int probeCount)
    {
        var indexes = new int[probeCount];
        if (probeCount == 1)
        {
            return indexes;
        }

        for (int index = 0; index < probeCount; index++)
        {
            indexes[index] = checked((int)(((long)index * (recordCount - 1)) / (probeCount - 1)));
        }

        return indexes;
    }
}
