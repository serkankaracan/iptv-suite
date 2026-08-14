using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using IptvSuite.Testing;

namespace IptvSuite.ProtectedCatalogSpike;

[SupportedOSPlatform("windows")]
internal static class ProtectedCatalogSpikeRunner
{
    private const int WarmupRecordCount = 16;
    private const int CorrectnessRecordCount = 8;

    internal static async Task RunAsync(SpikeInvocation invocation, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(invocation);
        SafeSpikeWorkspace workspace = SafeSpikeWorkspace.OpenFromCurrentDirectory();
        using FileStream runLock = workspace.AcquireExclusiveRunLock();
        workspace.Prepare();
        workspace.DeleteExactModeEvidenceSummary(invocation.Mode);

        try
        {
            SpikeSpecification specification = await SpikeSpecification.LoadAndValidateAsync(
                workspace.SpecificationPath,
                cancellationToken).ConfigureAwait(false);
            SpikeEnvironmentEvidence environment = await SpikeEnvironmentEvidenceCollector.CollectAsync(
                workspace,
                specification,
                invocation.Mode,
                cancellationToken).ConfigureAwait(false);
            SpikeModeSpecification mode = specification.GetMode(invocation.Mode);
            TestCanary canary = TestCanary.Create("M4-SPIKE", "PAYLOAD-V1");

            await RunWarmupAsync(workspace, specification, canary, cancellationToken).ConfigureAwait(false);
            FaultAndCancellationEvidence faultEvidence = await RunFaultAndCancellationMatrixAsync(
                workspace,
                specification,
                canary,
                mode.CancellationSamples,
                cancellationToken).ConfigureAwait(false);
            StagingCancellationEvidence stagingCancellation = await RunStagingCancellationProbeAsync(
                workspace,
                specification,
                canary,
                mode.CancellationSamples,
                cancellationToken).ConfigureAwait(false);
            ReaderValidationEvidence readerEvidence = await RunReaderValidationMatrixAsync(
                workspace,
                specification,
                canary,
                cancellationToken).ConfigureAwait(false);

            var scales = new List<ScaleEvidence>(mode.RecordCounts.Length);
            using IncrementalHash globalHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            foreach (int recordCount in mode.RecordCounts)
            {
                scales.Add(await RunScaleAsync(
                    workspace,
                    specification,
                    mode,
                    canary,
                    globalHash,
                    recordCount,
                    cancellationToken).ConfigureAwait(false));
            }

            string workloadHash = GetLowerHexDigest(globalHash);
            if (!string.Equals(workloadHash, mode.ExpectedWorkloadSha256, StringComparison.Ordinal))
            {
                throw new InvalidDataException("The protected-catalog workload sequence changed.");
            }

            workspace.Complete();
            await SpikeEnvironmentEvidenceCollector.AssertRepositoryStateUnchangedAsync(
                workspace,
                environment.Repository,
                invocation.Mode,
                cancellationToken).ConfigureAwait(false);

            var evidence = new SpikeEvidence(
                SchemaVersion: 1,
                Milestone: "M4",
                EvidenceKind: "protected-catalog-comparative-spike",
                Mode: invocation.Mode.ToString(),
                Configuration: "Release",
                Platform: "x64",
                ProtectionScope: "CurrentUser",
                CandidateId: "immutable-protected-catalog-container-v1",
                CandidateScope: "test-only-immutable-one-container-per-source-snapshot",
                ProductionReadiness: "not-production-ready-and-not-the-preferred-sqlite-transaction-design",
                Environment: environment,
                Payload: new PayloadEvidence(
                    specification.PayloadByteLength,
                    specification.GeneratorName,
                    specification.BaselineGeneratorName,
                    specification.AlgorithmVersion,
                    specification.Seed,
                    specification.Provenance,
                    specification.ContainsThirdPartyContent,
                    specification.ContainsPersonalData,
                    specification.ContainsRealCredentials,
                    specification.ContainsUnauthorizedMedia),
                Baseline: new BaselineEvidence(
                    specification.BaselineGeneratorName,
                    specification.AlgorithmVersion,
                    specification.BaselineEvidence.WorkloadCommit,
                    specification.BaselineEvidence.SpecificationSha256,
                    specification.BaselineEvidence.RunnerAssemblySha256,
                    specification.BaselineEvidence.DecisionSummarySha256,
                    specification.BaselineEvidence.DecisionWorkloadSha256,
                    specification.BaselineEvidence.EvidenceRecordCommit),
                Format: new FormatEvidence(
                    ProtectedCatalogFormat.Version,
                    "big-endian",
                    "immutable-one-container-per-source-snapshot",
                    ProtectedCatalogFormat.MaximumRecordCount,
                    ProtectedCatalogFormat.DekSize,
                    ProtectedCatalogFormat.NonceSize,
                    ProtectedCatalogFormat.TagSize,
                    "AES-256-GCM-algorithm-id-1",
                    "DPAPI-CurrentUser-key-wrap-id-1",
                    "fresh-rng-256-bit-dek-and-key-generation-id-per-staging-attempt",
                    "rng-96-bit-with-exact-in-memory-duplicate-rejection-per-fresh-dek",
                    "version-source-snapshot-key-generation-purpose-channel-owner-reference-ordinal-plaintext-length-record-count-algorithm",
                    "sha256-domain-version-source-snapshot-key-generation-purpose",
                    "flush-to-disk-then-file-move",
                    "strict-structural-reopen-dpapi-unwrap-and-up-to-16-evenly-spaced-tag-probes",
                    "controlled-fault-only-not-power-loss-durability"),
                Warmup: "passed",
                Scales: scales,
                FaultAndCancellation: faultEvidence,
                StagingCancellation: stagingCancellation,
                ReaderValidation: readerEvidence,
                WorkloadSequenceSha256: workloadHash,
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

    private static async Task<StagingCancellationEvidence> RunStagingCancellationProbeAsync(
        SafeSpikeWorkspace workspace,
        SpikeSpecification specification,
        TestCanary canary,
        int samples,
        CancellationToken cancellationToken)
    {
        const int workloadRecordCount = 50_000;
        const int triggerAfterEncryptedRecords = 10;
        var encryptedAtCancellation = new List<int>(samples);
        var completionLatencies = new List<double>(samples);
        int postCancellationEncryptedUpperBound = 0;
        int postCancellationActivationCount = 0;
        int postCompletionMutationCount = 0;
        int temporaryArtifactCount = 0;
        int canaryScans = 0;

        for (int sample = 1; sample <= samples; sample++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string directory = workspace.CreateCaseDirectory($"staging-cancellation-{sample}");
            var store = new ProtectedCatalogStore(directory);
            Guid sourceId = Guid.NewGuid();
            SnapshotBinding initialBinding = SnapshotBinding.Create(sourceId);
            byte[]? initialBytes = null;
            try
            {
                _ = await store.StageAndActivateAsync(
                    initialBinding,
                    CorrectnessRecordCount,
                    ordinal => CreatePayload(specification, CorrectnessRecordCount, sample, ordinal),
                    controlledHook: null,
                    cancellationToken).ConfigureAwait(false);
                initialBytes = store.ReadActiveSnapshotForControlledTest();

                using var probeCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                int encryptedRecordCount = 0;
                int encryptedAtRequest = 0;
                long cancellationRequested = 0;
                bool cancellationObserved = await ExpectCancellationAsync(() =>
                    store.StageAndActivateAsync(
                        SnapshotBinding.Create(sourceId),
                        workloadRecordCount,
                        ordinal => CreatePayload(
                            specification,
                            workloadRecordCount,
                            sample,
                            ordinal),
                        (checkpoint, ordinal) =>
                        {
                            if (checkpoint is not CatalogWriteCheckpoint.AfterRecordEncrypted)
                            {
                                return;
                            }

                            int completed = ordinal + 1;
                            Volatile.Write(ref encryptedRecordCount, completed);
                            if (completed == triggerAfterEncryptedRecords)
                            {
                                encryptedAtRequest = completed;
                                cancellationRequested = Stopwatch.GetTimestamp();
                                probeCancellation.Cancel();
                            }
                        },
                        probeCancellation.Token)).ConfigureAwait(false);

                if (!cancellationObserved ||
                    cancellationRequested == 0 ||
                    encryptedAtRequest < triggerAfterEncryptedRecords)
                {
                    throw new InvalidOperationException("The staging cancellation probe did not reach its trigger.");
                }

                int encryptedAtCompletion = Volatile.Read(ref encryptedRecordCount);
                encryptedAtCancellation.Add(encryptedAtRequest);
                completionLatencies.Add(Math.Round(
                    Stopwatch.GetElapsedTime(cancellationRequested).TotalMilliseconds,
                    3,
                    MidpointRounding.AwayFromZero));
                postCancellationEncryptedUpperBound += Math.Max(
                    0,
                    encryptedAtCompletion - encryptedAtRequest);
                bool activePreservedAtCompletion = ActiveEquals(store, initialBytes);
                if (!activePreservedAtCompletion)
                {
                    postCancellationActivationCount++;
                }

                int temporaryAtCompletion = store.TemporaryArtifactCount;
                await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken).ConfigureAwait(false);
                bool activePreservedAfterDelay = ActiveEquals(store, initialBytes);
                int temporaryAfterDelay = store.TemporaryArtifactCount;
                if (!activePreservedAtCompletion ||
                    !activePreservedAfterDelay ||
                    temporaryAtCompletion != temporaryAfterDelay)
                {
                    postCompletionMutationCount++;
                }

                temporaryArtifactCount += temporaryAfterDelay;
                AssertCanaryClean(directory, canary);
                canaryScans++;
                store.DeleteActiveSnapshot();
                store.DeleteActiveSnapshot();
            }
            finally
            {
                if (initialBytes is not null)
                {
                    CryptographicOperations.ZeroMemory(initialBytes);
                }

                workspace.DeleteCaseDirectory(directory);
            }
        }

        if (encryptedAtCancellation.Any(value => value != triggerAfterEncryptedRecords) ||
            postCancellationEncryptedUpperBound != 0 ||
            postCancellationActivationCount != 0 ||
            postCompletionMutationCount != 0 ||
            temporaryArtifactCount != 0)
        {
            throw new InvalidOperationException("The staging cancellation probe failed.");
        }

        return new StagingCancellationEvidence(
            samples,
            workloadRecordCount,
            triggerAfterEncryptedRecords,
            encryptedAtCancellation,
            NumberSummary.From(encryptedAtCancellation.Select(value => (double)value)),
            completionLatencies,
            NumberSummary.From(completionLatencies),
            postCancellationEncryptedUpperBound,
            postCancellationActivationCount,
            postCompletionMutationCount,
            temporaryArtifactCount,
            canaryScans,
            "encrypted-staged-records-not-committed-records",
            "passed");
    }

    private static async Task RunWarmupAsync(
        SafeSpikeWorkspace workspace,
        SpikeSpecification specification,
        TestCanary canary,
        CancellationToken cancellationToken)
    {
        string directory = workspace.CreateCaseDirectory("warmup");
        var store = new ProtectedCatalogStore(directory);
        SnapshotBinding binding = SnapshotBinding.Create(Guid.NewGuid());
        try
        {
            _ = await store.StageAndActivateAsync(
                binding,
                WarmupRecordCount,
                ordinal => CreatePayload(specification, WarmupRecordCount, 1, ordinal),
                controlledHook: null,
                cancellationToken).ConfigureAwait(false);
            using ProtectedCatalogReader reader = store.OpenReader(binding);
            foreach (int ordinal in new[] { 0, WarmupRecordCount - 1 })
            {
                AssertPayload(reader, specification, binding, WarmupRecordCount, 1, ordinal);
            }

            AssertCanaryClean(directory, canary);
            reader.Dispose();
            store.DeleteActiveSnapshot();
            store.DeleteActiveSnapshot();
            if (store.HasActiveSnapshot || store.TemporaryArtifactCount != 0)
            {
                throw new IOException("Warmup cleanup failed.");
            }
        }
        finally
        {
            workspace.DeleteCaseDirectory(directory);
        }
    }

    private static async Task<FaultAndCancellationEvidence> RunFaultAndCancellationMatrixAsync(
        SafeSpikeWorkspace workspace,
        SpikeSpecification specification,
        TestCanary canary,
        int samples,
        CancellationToken cancellationToken)
    {
        bool preCancelledNoMutation = true;
        bool cancellationPreserved = true;
        bool faultPreserved = true;
        bool postActivationCommitted = true;
        bool postActivationCancellationCommitted = true;
        var cancellationLatencies = new List<double>(samples);
        int postCancellationActivationCount = 0;
        int temporaryArtifacts = 0;
        int canaryScans = 0;

        for (int sample = 1; sample <= samples; sample++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string directory = workspace.CreateCaseDirectory($"fault-sample-{sample}");
            var store = new ProtectedCatalogStore(directory);
            Guid sourceId = Guid.NewGuid();
            SnapshotBinding initialBinding = SnapshotBinding.Create(sourceId);
            try
            {
                _ = await store.StageAndActivateAsync(
                    initialBinding,
                    CorrectnessRecordCount,
                    ordinal => CreatePayload(specification, CorrectnessRecordCount, sample, ordinal),
                    controlledHook: null,
                    cancellationToken).ConfigureAwait(false);
                byte[] initialBytes = store.ReadActiveSnapshotForControlledTest();
                try
                {
                    using (var preCancelled = new CancellationTokenSource())
                    {
                        preCancelled.Cancel();
                        bool observed = await ExpectCancellationAsync(() => store.StageAndActivateAsync(
                            SnapshotBinding.Create(sourceId),
                            CorrectnessRecordCount,
                            ordinal => CreatePayload(specification, CorrectnessRecordCount, sample, ordinal),
                            controlledHook: null,
                            preCancelled.Token)).ConfigureAwait(false);
                        preCancelledNoMutation &= observed && ActiveEquals(store, initialBytes);
                    }

                    using (var cancelledBeforeActivation = new CancellationTokenSource())
                    {
                        long cancellationRequested = 0;
                        bool observed = await ExpectCancellationAsync(() => store.StageAndActivateAsync(
                            SnapshotBinding.Create(sourceId),
                            CorrectnessRecordCount,
                            ordinal => CreatePayload(specification, CorrectnessRecordCount, sample, ordinal),
                            (checkpoint, ordinal) =>
                            {
                                if (checkpoint is CatalogWriteCheckpoint.BeforeActivation)
                                {
                                    cancellationRequested = Stopwatch.GetTimestamp();
                                    cancelledBeforeActivation.Cancel();
                                }
                            },
                            cancelledBeforeActivation.Token)).ConfigureAwait(false);
                        if (cancellationRequested == 0)
                        {
                            throw new InvalidOperationException("The cancellation hook was not reached.");
                        }

                        cancellationLatencies.Add(Math.Round(
                            Stopwatch.GetElapsedTime(cancellationRequested).TotalMilliseconds,
                            3,
                            MidpointRounding.AwayFromZero));
                        bool activePreserved = ActiveEquals(store, initialBytes);
                        if (!activePreserved)
                        {
                            postCancellationActivationCount++;
                        }

                        cancellationPreserved &= observed && activePreserved;
                    }

                    bool faultObserved = await ExpectExceptionAsync<InjectedCatalogFaultException>(() =>
                        store.StageAndActivateAsync(
                            SnapshotBinding.Create(sourceId),
                            CorrectnessRecordCount,
                            ordinal => CreatePayload(specification, CorrectnessRecordCount, sample, ordinal),
                            (checkpoint, _) =>
                            {
                                if (checkpoint is CatalogWriteCheckpoint.BeforeActivation)
                                {
                                    throw new InjectedCatalogFaultException();
                                }
                            },
                            cancellationToken)).ConfigureAwait(false);
                    faultPreserved &= faultObserved && ActiveEquals(store, initialBytes);

                    SnapshotBinding committedBinding = SnapshotBinding.Create(sourceId);
                    bool committedObserved = false;
                    try
                    {
                        _ = await store.StageAndActivateAsync(
                            committedBinding,
                            CorrectnessRecordCount,
                            ordinal => CreatePayload(specification, CorrectnessRecordCount, sample, ordinal),
                            (checkpoint, _) =>
                            {
                                if (checkpoint is CatalogWriteCheckpoint.AfterActivation)
                                {
                                    throw new InjectedCatalogFaultException();
                                }
                            },
                            cancellationToken).ConfigureAwait(false);
                    }
                    catch (CatalogCommitOutcomeException exception) when (exception.Committed)
                    {
                        committedObserved = true;
                    }

                    using ProtectedCatalogReader reader = store.OpenReader(committedBinding);
                    AssertPayload(reader, specification, committedBinding, CorrectnessRecordCount, sample, 0);
                    reader.Dispose();
                    postActivationCommitted &= committedObserved;

                    SnapshotBinding cancellationCommittedBinding = SnapshotBinding.Create(sourceId);
                    using (var cancelledAfterActivation = new CancellationTokenSource())
                    {
                        CatalogWriteResult result = await store.StageAndActivateAsync(
                            cancellationCommittedBinding,
                            CorrectnessRecordCount,
                            ordinal => CreatePayload(specification, CorrectnessRecordCount, sample, ordinal),
                            (checkpoint, _) =>
                            {
                                if (checkpoint is CatalogWriteCheckpoint.AfterActivation)
                                {
                                    cancelledAfterActivation.Cancel();
                                }
                            },
                            cancelledAfterActivation.Token).ConfigureAwait(false);
                        using ProtectedCatalogReader cancellationCommittedReader =
                            store.OpenReader(cancellationCommittedBinding);
                        AssertPayload(
                            cancellationCommittedReader,
                            specification,
                            cancellationCommittedBinding,
                            CorrectnessRecordCount,
                            sample,
                            0);
                        postActivationCancellationCommitted &=
                            cancelledAfterActivation.IsCancellationRequested &&
                            result.DpapiWrapCount == 1;
                    }

                    AssertCanaryClean(directory, canary);
                    canaryScans++;
                    temporaryArtifacts += store.TemporaryArtifactCount;
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(initialBytes);
                }

                store.DeleteActiveSnapshot();
                store.DeleteActiveSnapshot();
            }
            finally
            {
                workspace.DeleteCaseDirectory(directory);
            }
        }

        if (!preCancelledNoMutation ||
            !cancellationPreserved ||
            !faultPreserved ||
            !postActivationCommitted ||
            !postActivationCancellationCommitted ||
            postCancellationActivationCount != 0 ||
            temporaryArtifacts != 0)
        {
            throw new InvalidOperationException("The controlled fault and cancellation matrix failed.");
        }

        return new FaultAndCancellationEvidence(
            samples,
            preCancelledNoMutation,
            cancellationPreserved,
            faultPreserved,
            postActivationCommitted,
            postActivationCancellationCommitted,
            cancellationLatencies,
            NumberSummary.From(cancellationLatencies),
            postCancellationActivationCount,
            temporaryArtifacts,
            canaryScans,
            "in-process-controlled-hooks-not-power-loss-or-os-crash",
            "passed");
    }

    private static async Task<ReaderValidationEvidence> RunReaderValidationMatrixAsync(
        SafeSpikeWorkspace workspace,
        SpikeSpecification specification,
        TestCanary canary,
        CancellationToken cancellationToken)
    {
        string directory = workspace.CreateCaseDirectory("reader-validation");
        string crossContainerDirectory = workspace.CreateCaseDirectory("reader-cross-container");
        var store = new ProtectedCatalogStore(directory);
        SnapshotBinding binding = SnapshotBinding.Create(Guid.NewGuid());
        byte[]? validBytes = null;
        try
        {
            _ = await store.StageAndActivateAsync(
                binding,
                CorrectnessRecordCount,
                ordinal => CreatePayload(specification, CorrectnessRecordCount, 1, ordinal),
                controlledHook: null,
                cancellationToken).ConfigureAwait(false);
            validBytes = store.ReadActiveSnapshotForControlledTest();

            bool wrongSource = ExpectInvalid(() => store.OpenReader(
                binding with { SourceId = Guid.NewGuid() }).Dispose());
            bool wrongSnapshot = ExpectInvalid(() => store.OpenReader(
                binding with { SnapshotId = Guid.NewGuid() }).Dispose());
            bool wrongKeyGeneration = ExpectInvalid(() => store.OpenReader(
                binding with { KeyGenerationId = Guid.NewGuid() }).Dispose());
            bool wrongPurpose = ExpectInvalid(() => store.OpenReader(
                binding with { Purpose = (CatalogPurpose)2 }).Dispose());
            bool wrongRecord;
            bool wrongReference;
            using (ProtectedCatalogReader reader = store.OpenReader(binding))
            {
                RecordBinding expected = RecordBinding.Create(0);
                wrongRecord = ExpectInvalid(() => reader.Read(
                    expected with { ChannelOwnerId = Guid.NewGuid() }).Dispose());
                wrongReference = ExpectInvalid(() => reader.Read(
                    expected with { ProtectedReferenceId = Guid.NewGuid() }).Dispose());
            }

            bool magic = MutateAndExpectOpenFailure(store, binding, validBytes, bytes => bytes[0] ^= 0x01);
            bool version = MutateAndExpectOpenFailure(store, binding, validBytes, bytes =>
                BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(8), uint.MaxValue));
            bool headerLength = MutateAndExpectOpenFailure(store, binding, validBytes, bytes =>
                BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(12), ProtectedCatalogFormat.FixedHeaderSize));
            bool count = MutateAndExpectOpenFailure(store, binding, validBytes, bytes =>
                BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(16), int.MaxValue));
            bool recordLength = MutateAndExpectOpenFailure(store, binding, validBytes, bytes =>
            {
                int actualHeaderLength = BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(12));
                BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(actualHeaderLength + 36), 0);
            });
            bool offset = MutateAndExpectOpenFailure(store, binding, validBytes, bytes =>
            {
                int headerLength = BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(12));
                long current = BinaryPrimitives.ReadInt64BigEndian(bytes.AsSpan(headerLength + 40));
                BinaryPrimitives.WriteInt64BigEndian(bytes.AsSpan(headerLength + 40), current + 1);
            });
            bool overlap = MutateAndExpectOpenFailure(store, binding, validBytes, bytes =>
            {
                int actualHeaderLength = BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(12));
                long firstOffset = BinaryPrimitives.ReadInt64BigEndian(bytes.AsSpan(actualHeaderLength + 40));
                BinaryPrimitives.WriteInt64BigEndian(
                    bytes.AsSpan(actualHeaderLength + ProtectedCatalogFormat.IndexEntrySize + 40),
                    firstOffset);
            });
            bool duplicateOwner = MutateAndExpectOpenFailure(store, binding, validBytes, bytes =>
            {
                int actualHeaderLength = BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(12));
                bytes.AsSpan(actualHeaderLength, 16).CopyTo(
                    bytes.AsSpan(actualHeaderLength + ProtectedCatalogFormat.IndexEntrySize, 16));
            });
            bool duplicateReference = MutateAndExpectOpenFailure(store, binding, validBytes, bytes =>
            {
                int actualHeaderLength = BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(12));
                bytes.AsSpan(actualHeaderLength + 16, 16).CopyTo(
                    bytes.AsSpan(actualHeaderLength + ProtectedCatalogFormat.IndexEntrySize + 16, 16));
            });
            bool duplicateNonce = MutateAndExpectOpenFailure(store, binding, validBytes, bytes =>
            {
                int actualHeaderLength = BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(12));
                bytes.AsSpan(actualHeaderLength + 52, ProtectedCatalogFormat.NonceSize).CopyTo(
                    bytes.AsSpan(
                        actualHeaderLength + ProtectedCatalogFormat.IndexEntrySize + 52,
                        ProtectedCatalogFormat.NonceSize));
            });
            Guid tamperedOwner = Guid.NewGuid();
            byte[] tupleTampered = validBytes.ToArray();
            int tupleHeaderLength = BinaryPrimitives.ReadInt32BigEndian(tupleTampered.AsSpan(12));
            ProtectedCatalogStore.WriteGuid(
                tupleTampered.AsSpan(tupleHeaderLength, 16),
                tamperedOwner);
            store.ReplaceActiveSnapshotForControlledTest(tupleTampered);
            bool indexTupleAuthentication;
            try
            {
                using ProtectedCatalogReader reader = store.OpenReader(binding);
                RecordBinding tamperedBinding = RecordBinding.Create(0) with
                {
                    ChannelOwnerId = tamperedOwner,
                };
                indexTupleAuthentication = ExpectInvalid(() => reader.Read(tamperedBinding).Dispose());
            }
            finally
            {
                CryptographicOperations.ZeroMemory(tupleTampered);
                store.ReplaceActiveSnapshotForControlledTest(validBytes);
            }

            var crossContainerStore = new ProtectedCatalogStore(crossContainerDirectory);
            SnapshotBinding crossContainerBinding = SnapshotBinding.Create(binding.SourceId);
            _ = await crossContainerStore.StageAndActivateAsync(
                crossContainerBinding,
                CorrectnessRecordCount,
                ordinal => CreatePayload(specification, CorrectnessRecordCount, 2, ordinal),
                controlledHook: null,
                cancellationToken).ConfigureAwait(false);
            AssertCanaryClean(crossContainerDirectory, canary);
            byte[] crossContainerBytes = crossContainerStore.ReadActiveSnapshotForControlledTest();
            byte[] swappedWrappedDek = validBytes.ToArray();
            bool wrappedDekSwap;
            try
            {
                int targetWrappedLength = BinaryPrimitives.ReadInt32BigEndian(swappedWrappedDek.AsSpan(20));
                int sourceWrappedLength = BinaryPrimitives.ReadInt32BigEndian(crossContainerBytes.AsSpan(20));
                if (targetWrappedLength != sourceWrappedLength)
                {
                    throw new InvalidOperationException("The controlled wrapped-key lengths differ.");
                }

                crossContainerBytes.AsSpan(
                    ProtectedCatalogFormat.FixedHeaderSize,
                    sourceWrappedLength).CopyTo(swappedWrappedDek.AsSpan(
                        ProtectedCatalogFormat.FixedHeaderSize,
                        targetWrappedLength));
                store.ReplaceActiveSnapshotForControlledTest(swappedWrappedDek);
                wrappedDekSwap = ExpectInvalid(() => store.OpenReader(binding).Dispose());
            }
            finally
            {
                CryptographicOperations.ZeroMemory(crossContainerBytes);
                CryptographicOperations.ZeroMemory(swappedWrappedDek);
                store.ReplaceActiveSnapshotForControlledTest(validBytes);
                crossContainerStore.DeleteActiveSnapshot();
                crossContainerStore.DeleteActiveSnapshot();
            }
            byte[] withTrailingByte = GC.AllocateUninitializedArray<byte>(validBytes.Length + 1);
            validBytes.CopyTo(withTrailingByte, 0);
            withTrailingByte[^1] = 0;
            store.ReplaceActiveSnapshotForControlledTest(withTrailingByte);
            bool trailing;
            try
            {
                trailing = ExpectInvalid(() => store.OpenReader(binding).Dispose());
            }
            finally
            {
                CryptographicOperations.ZeroMemory(withTrailingByte);
                store.ReplaceActiveSnapshotForControlledTest(validBytes);
            }

            byte[] corruptCiphertext = validBytes.ToArray();
            corruptCiphertext[^1] ^= 0x01;
            store.ReplaceActiveSnapshotForControlledTest(corruptCiphertext);
            bool tagOrCiphertext;
            try
            {
                using ProtectedCatalogReader reader = store.OpenReader(binding);
                tagOrCiphertext = ExpectInvalid(() => reader.Read(RecordBinding.Create(
                    CorrectnessRecordCount - 1)).Dispose());
            }
            finally
            {
                CryptographicOperations.ZeroMemory(corruptCiphertext);
                store.ReplaceActiveSnapshotForControlledTest(validBytes);
            }

            AssertCanaryClean(directory, canary);
            store.DeleteActiveSnapshot();
            store.DeleteActiveSnapshot();
            var collisionGenerator = new CollisionOnceNonceGenerator();
            var collisionStore = new ProtectedCatalogStore(directory, collisionGenerator);
            SnapshotBinding collisionBinding = SnapshotBinding.Create(Guid.NewGuid());
            CatalogWriteResult collisionResult = await collisionStore.StageAndActivateAsync(
                collisionBinding,
                CorrectnessRecordCount,
                ordinal => CreatePayload(specification, CorrectnessRecordCount, 3, ordinal),
                controlledHook: null,
                cancellationToken).ConfigureAwait(false);
            using (ProtectedCatalogReader collisionReader = collisionStore.OpenReader(collisionBinding))
            {
                AssertPayload(
                    collisionReader,
                    specification,
                    collisionBinding,
                    CorrectnessRecordCount,
                    3,
                    CorrectnessRecordCount - 1);
            }

            AssertCanaryClean(directory, canary);

            bool injectedCollisionRetry =
                collisionResult.NonceCount == CorrectnessRecordCount &&
                collisionResult.NonceCollisionRetryCount >= 1 &&
                collisionGenerator.InjectedCollisionCount == 1;
            collisionStore.DeleteActiveSnapshot();
            collisionStore.DeleteActiveSnapshot();
            bool idempotentDelete = !collisionStore.HasActiveSnapshot &&
                collisionStore.TemporaryArtifactCount == 0;

            var evidence = new ReaderValidationEvidence(
                wrongSource,
                wrongSnapshot,
                wrongKeyGeneration,
                wrongPurpose,
                wrongRecord,
                wrongReference,
                magic,
                version,
                headerLength,
                count,
                recordLength,
                offset,
                overlap,
                duplicateOwner,
                duplicateReference,
                duplicateNonce,
                indexTupleAuthentication,
                wrappedDekSwap,
                tagOrCiphertext,
                trailing,
                injectedCollisionRetry,
                collisionResult.NonceCollisionRetryCount,
                idempotentDelete);
            if (evidence is not
                {
                    WrongSourceBindingFailedClosed: true,
                    WrongSnapshotBindingFailedClosed: true,
                    WrongKeyGenerationBindingFailedClosed: true,
                    WrongPurposeBindingFailedClosed: true,
                    WrongRecordBindingFailedClosed: true,
                    WrongReferenceBindingFailedClosed: true,
                    MagicCorruptionFailedClosed: true,
                    VersionCorruptionFailedClosed: true,
                    HeaderLengthCorruptionFailedClosed: true,
                    CountCorruptionFailedClosed: true,
                    RecordLengthCorruptionFailedClosed: true,
                    OffsetCorruptionFailedClosed: true,
                    OverlappingCiphertextFailedClosed: true,
                    DuplicateOwnerFailedClosed: true,
                    DuplicateReferenceFailedClosed: true,
                    DuplicateNonceFailedClosed: true,
                    IndexTupleAuthenticationFailedClosed: true,
                    CrossContainerWrappedDekSwapFailedClosed: true,
                    TagOrCiphertextCorruptionFailedClosed: true,
                    TrailingBytesFailedClosed: true,
                    InjectedNonceCollisionRetryPassed: true,
                    IdempotentDeletePassed: true,
                })
            {
                throw new InvalidOperationException("The strict reader matrix failed.");
            }

            return evidence;
        }
        finally
        {
            if (validBytes is not null)
            {
                CryptographicOperations.ZeroMemory(validBytes);
            }

            workspace.DeleteCaseDirectory(directory);
            workspace.DeleteCaseDirectory(crossContainerDirectory);
        }
    }

    private sealed class CollisionOnceNonceGenerator : ICatalogNonceGenerator
    {
        private int _fillCount;

        internal int InjectedCollisionCount { get; private set; }

        public void Fill(Span<byte> destination)
        {
            int fillCount = Interlocked.Increment(ref _fillCount);
            if (fillCount is 1 or 2)
            {
                destination.Fill(0xA5);
                if (fillCount == 2)
                {
                    InjectedCollisionCount++;
                }

                return;
            }

            RandomNumberGenerator.Fill(destination);
        }
    }

    private static async Task<ScaleEvidence> RunScaleAsync(
        SafeSpikeWorkspace workspace,
        SpikeSpecification specification,
        SpikeModeSpecification mode,
        TestCanary canary,
        IncrementalHash globalHash,
        int recordCount,
        CancellationToken cancellationToken)
    {
        var createSamples = new List<PhaseSample>(mode.Iterations);
        var adapterReopenSamples = new List<PhaseSample>(mode.Iterations);
        var readSamples = new List<PhaseSample>(mode.Iterations);
        var deleteSamples = new List<PhaseSample>(mode.Iterations);
        var diskBytes = new List<long>(mode.Iterations);
        int wrapCount = 0;
        int preActivationUnwrapCount = 0;
        int preActivationTagProbeCount = 0;
        int postActivationUnwrapCount = 0;
        int nonceCount = 0;
        int collisionRetries = 0;
        int canaryScans = 0;
        int readProbeCount = Math.Min(specification.Format.ReadProbeCount, recordCount);
        using IncrementalHash scaleHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        for (int iteration = 1; iteration <= mode.Iterations; iteration++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string directory = workspace.CreateCaseDirectory($"scale-{recordCount}-iteration-{iteration}");
            var store = new ProtectedCatalogStore(directory);
            SnapshotBinding binding = SnapshotBinding.Create(Guid.NewGuid());
            ProtectedCatalogReader? reader = null;
            CatalogWriteResult? writeResult = null;
            byte[]? payload = null;
            try
            {
                payload = GC.AllocateUninitializedArray<byte>(
                    DeterministicPayloadGenerator.PayloadByteLength);
                createSamples.Add(await PhaseSample.MeasureAsync(recordCount, async () =>
                {
                    writeResult = await store.StageAndActivateAsync(
                        binding,
                        recordCount,
                        ordinal =>
                        {
                            DeterministicPayloadGenerator.Fill(
                                payload,
                                specification,
                                recordCount,
                                iteration,
                                ordinal);
                            scaleHash.AppendData(payload);
                            globalHash.AppendData(payload);
                            return payload;
                        },
                        controlledHook: null,
                        cancellationToken).ConfigureAwait(false);
                }).ConfigureAwait(false));
                CatalogWriteResult completedWrite = writeResult ??
                    throw new InvalidOperationException("The protected-catalog write did not complete.");
                wrapCount += completedWrite.DpapiWrapCount;
                preActivationUnwrapCount += completedWrite.PreActivationDpapiUnwrapCount;
                preActivationTagProbeCount += completedWrite.PreActivationTagProbeCount;
                nonceCount += completedWrite.NonceCount;
                collisionRetries += completedWrite.NonceCollisionRetryCount;
                diskBytes.Add(completedWrite.DiskBytes);

                AssertCanaryClean(directory, canary);
                canaryScans++;
                adapterReopenSamples.Add(await PhaseSample.MeasureAsync(1, () =>
                {
                    var reopenedStore = new ProtectedCatalogStore(directory);
                    reader = reopenedStore.OpenReader(binding);
                    return ValueTask.CompletedTask;
                }).ConfigureAwait(false));
                ProtectedCatalogReader activeReader = reader ??
                    throw new InvalidOperationException("The protected-catalog reader did not open.");
                postActivationUnwrapCount += ProtectedCatalogReader.DpapiUnwrapCount;

                int[] indexes = CreateProbeIndexes(recordCount, readProbeCount);
                readSamples.Add(await PhaseSample.MeasureAsync(readProbeCount, () =>
                {
                    foreach (int ordinal in indexes)
                    {
                        AssertPayload(
                            activeReader,
                            specification,
                            binding,
                            recordCount,
                            iteration,
                            ordinal,
                            payload);
                    }

                    return ValueTask.CompletedTask;
                }).ConfigureAwait(false));

                deleteSamples.Add(await PhaseSample.MeasureAsync(1, () =>
                {
                    activeReader.Dispose();
                    reader = null;
                    store.DeleteActiveSnapshot();
                    store.DeleteActiveSnapshot();
                    return ValueTask.CompletedTask;
                }).ConfigureAwait(false));
                if (store.HasActiveSnapshot || store.TemporaryArtifactCount != 0)
                {
                    throw new IOException("The protected-catalog scale cleanup failed.");
                }
            }
            finally
            {
                reader?.Dispose();
                if (payload is not null)
                {
                    CryptographicOperations.ZeroMemory(payload);
                }

                workspace.DeleteCaseDirectory(directory);
            }
        }

        string scaleWorkloadHash = GetLowerHexDigest(scaleHash);
        if (!mode.ExpectedScaleWorkloadSha256.TryGetValue(
                recordCount.ToString(System.Globalization.CultureInfo.InvariantCulture),
                out string? expectedHash) ||
            !string.Equals(scaleWorkloadHash, expectedHash, StringComparison.Ordinal))
        {
            throw new InvalidDataException("A protected-catalog scale workload changed.");
        }

        if (wrapCount != mode.Iterations ||
            preActivationUnwrapCount != mode.Iterations ||
            preActivationTagProbeCount != checked(Math.Min(recordCount, 16) * mode.Iterations) ||
            postActivationUnwrapCount != mode.Iterations ||
            nonceCount != checked(recordCount * mode.Iterations))
        {
            throw new InvalidOperationException("The protected-catalog cryptographic operation counts changed.");
        }

        return new ScaleEvidence(
            recordCount,
            mode.Iterations,
            readProbeCount,
            scaleWorkloadHash,
            PhaseEvidence.From(createSamples),
            PhaseEvidence.From(adapterReopenSamples),
            PhaseEvidence.From(readSamples),
            PhaseEvidence.From(deleteSamples),
            recordCount,
            wrapCount,
            preActivationUnwrapCount,
            preActivationTagProbeCount,
            postActivationUnwrapCount,
            checked(preActivationUnwrapCount + postActivationUnwrapCount),
            nonceCount,
            collisionRetries,
            diskBytes,
            NumberSummary.From(diskBytes.Select(value => (double)value)),
            canaryScans);
    }

    private static byte[] CreatePayload(
        SpikeSpecification specification,
        int recordCount,
        int iteration,
        int ordinal)
    {
        byte[] payload = GC.AllocateUninitializedArray<byte>(DeterministicPayloadGenerator.PayloadByteLength);
        try
        {
            DeterministicPayloadGenerator.Fill(payload, specification, recordCount, iteration, ordinal);
            return payload;
        }
        catch
        {
            CryptographicOperations.ZeroMemory(payload);
            throw;
        }
    }

    private static void AssertPayload(
        ProtectedCatalogReader reader,
        SpikeSpecification specification,
        SnapshotBinding binding,
        int recordCount,
        int iteration,
        int ordinal,
        byte[]? reusableBuffer = null)
    {
        _ = binding;
        byte[] expected = reusableBuffer ??
            GC.AllocateUninitializedArray<byte>(DeterministicPayloadGenerator.PayloadByteLength);
        try
        {
            DeterministicPayloadGenerator.Fill(expected, specification, recordCount, iteration, ordinal);
            using SecretBuffer actual = reader.Read(RecordBinding.Create(ordinal));
            if (!CryptographicOperations.FixedTimeEquals(expected, actual.Value))
            {
                throw new InvalidDataException("A protected-catalog read probe failed.");
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(expected);
        }
    }

    private static bool ActiveEquals(ProtectedCatalogStore store, ReadOnlySpan<byte> expected)
    {
        byte[] actual = store.ReadActiveSnapshotForControlledTest();
        try
        {
            return actual.AsSpan().SequenceEqual(expected) && store.TemporaryArtifactCount == 0;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(actual);
        }
    }

    private static async Task<bool> ExpectCancellationAsync(Func<Task<CatalogWriteResult>> action)
    {
        try
        {
            _ = await action().ConfigureAwait(false);
            return false;
        }
        catch (OperationCanceledException)
        {
            return true;
        }
    }

    private static async Task<bool> ExpectExceptionAsync<TException>(Func<Task<CatalogWriteResult>> action)
        where TException : Exception
    {
        try
        {
            _ = await action().ConfigureAwait(false);
            return false;
        }
        catch (TException)
        {
            return true;
        }
    }

    private static bool ExpectInvalid(Action action)
    {
        try
        {
            action();
            return false;
        }
        catch (InvalidDataException)
        {
            return true;
        }
    }

    private static bool MutateAndExpectOpenFailure(
        ProtectedCatalogStore store,
        SnapshotBinding binding,
        byte[] validBytes,
        Action<byte[]> mutate)
    {
        byte[] mutated = validBytes.ToArray();
        try
        {
            mutate(mutated);
            store.ReplaceActiveSnapshotForControlledTest(mutated);
            return ExpectInvalid(() => store.OpenReader(binding).Dispose());
        }
        finally
        {
            CryptographicOperations.ZeroMemory(mutated);
            store.ReplaceActiveSnapshotForControlledTest(validBytes);
        }
    }

    private static void AssertCanaryClean(string directory, TestCanary canary)
    {
        if (ArtifactCanaryScanner.Scan(directory, canary).Count != 0)
        {
            throw new InvalidDataException("A protected-catalog artifact failed its canary scan.");
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
}
