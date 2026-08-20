using System.Reflection;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace IptvSuite.UnitTests;

[TestClass]
public sealed class SourceChannelProtectedRecordDeletionServiceTests
{
    private static readonly DateTimeOffset FixedInstant =
        new(2026, 8, 14, 0, 0, 0, TimeSpan.Zero);

    [TestMethod]
    public void ConstructorRequiresASecretStore()
    {
        Assert.ThrowsExactly<ArgumentNullException>(() =>
            new SourceChannelProtectedRecordDeletionService(null!));
    }

    [TestMethod]
    public void PublicDeletionApiAcceptsOnlyTheAggregateChainAndCancellation()
    {
        MethodInfo? delete = typeof(SourceChannelProtectedRecordDeletionService).GetMethod(
            nameof(SourceChannelProtectedRecordDeletionService.DeleteAsync),
            BindingFlags.Instance | BindingFlags.Public);
        Assert.IsNotNull(delete);

        Type[] parameterTypes = delete.GetParameters()
            .Select(parameter => parameter.ParameterType)
            .ToArray();
        CollectionAssert.AreEqual(
            new[]
            {
                typeof(ContentSource),
                typeof(PlaylistSnapshot),
                typeof(LiveChannel),
                typeof(CancellationToken),
            },
            parameterTypes);
        Assert.IsFalse(parameterTypes.Contains(typeof(SourceId)));
        Assert.IsFalse(parameterTypes.Contains(typeof(SnapshotId)));
        Assert.IsFalse(parameterTypes.Contains(typeof(ChannelId)));
        Assert.IsFalse(parameterTypes.Contains(typeof(ProtectedRecordOwner)));
        Assert.IsFalse(parameterTypes.Contains(typeof(ProtectedLocatorReference)));
        Assert.IsFalse(parameterTypes.Contains(typeof(ProtectedValuePurpose)));
    }

    [TestMethod]
    public async Task MissingAggregateMemberCannotReachTheSecretStore()
    {
        var store = new ChannelDeletionProbeSecretStore();
        var service = new SourceChannelProtectedRecordDeletionService(store);
        SourceId sourceId = SourceId.Generate();
        ContentSource source = CreateSource(sourceId, ContentSourceStatus.DeletionPending);
        PlaylistSnapshot snapshot = CreateSnapshot(sourceId);
        LiveChannel channel = CreateRemoteChannel(sourceId, snapshot.Id);

        await Assert.ThrowsExactlyAsync<ArgumentNullException>(async () =>
            await service.DeleteAsync(null!, snapshot, channel));
        await Assert.ThrowsExactlyAsync<ArgumentNullException>(async () =>
            await service.DeleteAsync(source, null!, channel));
        await Assert.ThrowsExactlyAsync<ArgumentNullException>(async () =>
            await service.DeleteAsync(source, snapshot, null!));

        Assert.IsEmpty(store.LocatorDeleteCalls);
    }

    [TestMethod]
    public async Task NonDeletionPendingSourceCannotReachTheSecretStore()
    {
        var store = new ChannelDeletionProbeSecretStore();
        var service = new SourceChannelProtectedRecordDeletionService(store);

        foreach (ContentSourceStatus status in
            Enum.GetValues<ContentSourceStatus>().Where(
                value => value is not ContentSourceStatus.DeletionPending))
        {
            SourceId sourceId = SourceId.Generate();
            ContentSource source = CreateSource(sourceId, status);
            PlaylistSnapshot snapshot = CreateSnapshot(sourceId);
            LiveChannel channel = CreateRemoteChannel(sourceId, snapshot.Id);

            await Assert.ThrowsExactlyAsync<InvalidOperationException>(async () =>
                await service.DeleteAsync(source, snapshot, channel));
        }

        Assert.IsEmpty(store.LocatorDeleteCalls);
    }

    [TestMethod]
    public async Task BrokenSourceSnapshotOrSnapshotChannelChainCannotReachTheSecretStore()
    {
        var store = new ChannelDeletionProbeSecretStore();
        var service = new SourceChannelProtectedRecordDeletionService(store);
        SourceId sourceId = SourceId.Generate();
        ContentSource source = CreateSource(sourceId, ContentSourceStatus.DeletionPending);
        PlaylistSnapshot wrongSourceSnapshot = CreateSnapshot(SourceId.Generate());
        LiveChannel wrongSourceChannel = CreateRemoteChannel(sourceId, wrongSourceSnapshot.Id);

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(async () =>
            await service.DeleteAsync(source, wrongSourceSnapshot, wrongSourceChannel));

        PlaylistSnapshot snapshot = CreateSnapshot(sourceId);
        LiveChannel wrongSnapshotChannel = CreateRemoteChannel(sourceId, SnapshotId.Generate());
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(async () =>
            await service.DeleteAsync(source, snapshot, wrongSnapshotChannel));

        Assert.IsEmpty(store.LocatorDeleteCalls);
    }

    [TestMethod]
    public async Task ChannelStableKeyFromAnotherSourceCannotReachTheSecretStore()
    {
        var store = new ChannelDeletionProbeSecretStore();
        var service = new SourceChannelProtectedRecordDeletionService(store);
        SourceId sourceId = SourceId.Generate();
        ContentSource source = CreateSource(sourceId, ContentSourceStatus.DeletionPending);
        PlaylistSnapshot snapshot = CreateSnapshot(sourceId);
        LiveChannel channel = CreateRemoteChannel(
            sourceId,
            snapshot.Id,
            stableKeySourceId: SourceId.Generate());

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(async () =>
            await service.DeleteAsync(source, snapshot, channel));

        Assert.IsEmpty(store.LocatorDeleteCalls);
    }

    [TestMethod]
    public async Task RemoteChannelDeletionUsesExactStreamThenLogoKeys()
    {
        var store = new ChannelDeletionProbeSecretStore();
        var service = new SourceChannelProtectedRecordDeletionService(store);
        SourceId sourceId = SourceId.Generate();
        ContentSource source = CreateSource(sourceId, ContentSourceStatus.DeletionPending);
        PlaylistSnapshot snapshot = CreateSnapshot(sourceId);
        LiveChannel channel = CreateRemoteChannel(sourceId, snapshot.Id);

        SecretStoreOperationResult result = await service.DeleteAsync(source, snapshot, channel);

        Assert.IsTrue(result.IsSuccess);
        Assert.HasCount(2, store.LocatorDeleteCalls);
        AssertDeleteCall(
            store.LocatorDeleteCalls[0],
            source,
            channel,
            ProtectedValuePurpose.ChannelStreamLocator,
            channel.StreamReference!,
            expectedCanBeCanceled: false);
        AssertDeleteCall(
            store.LocatorDeleteCalls[1],
            source,
            channel,
            ProtectedValuePurpose.ChannelLogoLocator,
            channel.LogoReference!,
            expectedCanBeCanceled: false);
    }

    [TestMethod]
    public async Task RemoteChannelWithoutLogoDeletesOnlyItsStream()
    {
        var store = new ChannelDeletionProbeSecretStore();
        var service = new SourceChannelProtectedRecordDeletionService(store);
        SourceId sourceId = SourceId.Generate();
        ContentSource source = CreateSource(sourceId, ContentSourceStatus.DeletionPending);
        PlaylistSnapshot snapshot = CreateSnapshot(sourceId);
        LiveChannel channel = CreateRemoteChannel(sourceId, snapshot.Id, includeLogo: false);

        SecretStoreOperationResult result = await service.DeleteAsync(source, snapshot, channel);

        Assert.IsTrue(result.IsSuccess);
        Assert.HasCount(1, store.LocatorDeleteCalls);
        AssertDeleteCall(
            store.LocatorDeleteCalls.Single(),
            source,
            channel,
            ProtectedValuePurpose.ChannelStreamLocator,
            channel.StreamReference!,
            expectedCanBeCanceled: false);
    }

    [TestMethod]
    [DataRow(PlaylistSnapshotState.Importing)]
    [DataRow(PlaylistSnapshotState.Rejected)]
    public async Task DeletionAcceptsRetainedOrIncompleteSnapshotRecords(
        PlaylistSnapshotState snapshotState)
    {
        var store = new ChannelDeletionProbeSecretStore();
        var service = new SourceChannelProtectedRecordDeletionService(store);
        SourceId sourceId = SourceId.Generate();
        SnapshotId activeSnapshotId = SnapshotId.Generate();
        ContentSource source = CreateSource(
            sourceId,
            ContentSourceStatus.DeletionPending,
            activeSnapshotId);
        PlaylistSnapshot snapshot = CreateSnapshot(
            sourceId,
            snapshotId: SnapshotId.Generate(),
            state: snapshotState);
        Assert.AreNotEqual(source.ActiveSnapshotId, snapshot.Id);
        LiveChannel channel = CreateRemoteChannel(sourceId, snapshot.Id);

        SecretStoreOperationResult result = await service.DeleteAsync(source, snapshot, channel);

        Assert.IsTrue(result.IsSuccess);
        Assert.HasCount(2, store.LocatorDeleteCalls);
    }

    [TestMethod]
    public async Task ProviderChannelDeletesOnlyItsOptionalLogo()
    {
        var store = new ChannelDeletionProbeSecretStore();
        var service = new SourceChannelProtectedRecordDeletionService(store);
        SourceId sourceId = SourceId.Generate();
        ContentSource source = CreateSource(sourceId, ContentSourceStatus.DeletionPending);
        PlaylistSnapshot snapshot = CreateSnapshot(sourceId);
        LiveChannel channel = CreateProviderChannel(sourceId, snapshot.Id, includeLogo: true);

        SecretStoreOperationResult result = await service.DeleteAsync(source, snapshot, channel);

        Assert.IsTrue(result.IsSuccess);
        Assert.HasCount(1, store.LocatorDeleteCalls);
        AssertDeleteCall(
            store.LocatorDeleteCalls[0],
            source,
            channel,
            ProtectedValuePurpose.ChannelLogoLocator,
            channel.LogoReference!,
            expectedCanBeCanceled: false);
    }

    [TestMethod]
    public async Task ProviderChannelWithoutProtectedLocatorsIsAnIdempotentNoOp()
    {
        var store = new ChannelDeletionProbeSecretStore();
        var service = new SourceChannelProtectedRecordDeletionService(store);
        SourceId sourceId = SourceId.Generate();
        ContentSource source = CreateSource(sourceId, ContentSourceStatus.DeletionPending);
        PlaylistSnapshot snapshot = CreateSnapshot(sourceId);
        LiveChannel channel = CreateProviderChannel(sourceId, snapshot.Id, includeLogo: false);

        SecretStoreOperationResult first = await service.DeleteAsync(source, snapshot, channel);
        SecretStoreOperationResult second = await service.DeleteAsync(source, snapshot, channel);

        Assert.IsTrue(first.IsSuccess);
        Assert.IsTrue(second.IsSuccess);
        Assert.IsEmpty(store.LocatorDeleteCalls);
    }

    [TestMethod]
    public async Task PreCancelledDeletionDoesNotReachTheSecretStore()
    {
        var store = new ChannelDeletionProbeSecretStore();
        var service = new SourceChannelProtectedRecordDeletionService(store);
        SourceId sourceId = SourceId.Generate();
        ContentSource source = CreateSource(sourceId, ContentSourceStatus.DeletionPending);
        PlaylistSnapshot snapshot = CreateSnapshot(sourceId);
        LiveChannel channel = CreateRemoteChannel(sourceId, snapshot.Id);
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();

        await Assert.ThrowsExactlyAsync<OperationCanceledException>(async () =>
            await service.DeleteAsync(source, snapshot, channel, cancellation.Token));

        Assert.IsEmpty(store.LocatorDeleteCalls);
    }

    [TestMethod]
    public async Task StreamFailureStopsBeforeLogoDelete()
    {
        var store = new ChannelDeletionProbeSecretStore();
        store.LocatorDeleteResults.Enqueue(
            SecretStoreOperationResult.Failed(SecretStoreFailure.StorageUnavailable));
        var service = new SourceChannelProtectedRecordDeletionService(store);
        SourceId sourceId = SourceId.Generate();
        ContentSource source = CreateSource(sourceId, ContentSourceStatus.DeletionPending);
        PlaylistSnapshot snapshot = CreateSnapshot(sourceId);
        LiveChannel channel = CreateRemoteChannel(sourceId, snapshot.Id);

        SecretStoreOperationResult result = await service.DeleteAsync(source, snapshot, channel);

        Assert.IsFalse(result.IsSuccess);
        Assert.AreEqual(SecretStoreFailure.StorageUnavailable, result.Failure);
        Assert.HasCount(1, store.LocatorDeleteCalls);
        Assert.AreEqual(
            ProtectedValuePurpose.ChannelStreamLocator,
            store.LocatorDeleteCalls.Single().Purpose);
    }

    [TestMethod]
    public async Task CancellationAtFirstCommitDoesNotStopTheLogoDelete()
    {
        using CancellationTokenSource cancellation = new();
        var store = new ChannelDeletionProbeSecretStore
        {
            CancellationToSignalAtSuccessfulCall = cancellation,
            SuccessfulCallToSignal = 1,
        };
        var service = new SourceChannelProtectedRecordDeletionService(store);
        SourceId sourceId = SourceId.Generate();
        ContentSource source = CreateSource(sourceId, ContentSourceStatus.DeletionPending);
        PlaylistSnapshot snapshot = CreateSnapshot(sourceId);
        LiveChannel channel = CreateRemoteChannel(sourceId, snapshot.Id);

        SecretStoreOperationResult result = await service.DeleteAsync(
            source,
            snapshot,
            channel,
            cancellation.Token);

        Assert.IsTrue(cancellation.IsCancellationRequested);
        Assert.IsTrue(result.IsSuccess);
        Assert.HasCount(2, store.LocatorDeleteCalls);
        Assert.AreEqual(cancellation.Token, store.LocatorDeleteCalls[0].CancellationToken);
        Assert.AreEqual(CancellationToken.None, store.LocatorDeleteCalls[1].CancellationToken);
    }

    [TestMethod]
    public async Task CancellationAtLogoOnlyCommitDoesNotReplaceSuccess()
    {
        using CancellationTokenSource cancellation = new();
        var store = new ChannelDeletionProbeSecretStore
        {
            CancellationToSignalAtSuccessfulCall = cancellation,
            SuccessfulCallToSignal = 1,
        };
        var service = new SourceChannelProtectedRecordDeletionService(store);
        SourceId sourceId = SourceId.Generate();
        ContentSource source = CreateSource(sourceId, ContentSourceStatus.DeletionPending);
        PlaylistSnapshot snapshot = CreateSnapshot(sourceId);
        LiveChannel channel = CreateProviderChannel(sourceId, snapshot.Id, includeLogo: true);

        SecretStoreOperationResult result = await service.DeleteAsync(
            source,
            snapshot,
            channel,
            cancellation.Token);

        Assert.IsTrue(cancellation.IsCancellationRequested);
        Assert.IsTrue(result.IsSuccess);
        Assert.HasCount(1, store.LocatorDeleteCalls);
        Assert.AreEqual(cancellation.Token, store.LocatorDeleteCalls[0].CancellationToken);
    }

    [TestMethod]
    public async Task PartialFailureConvergesOnIdempotentRetry()
    {
        var store = new ChannelDeletionProbeSecretStore();
        store.LocatorDeleteResults.Enqueue(SecretStoreOperationResult.Succeeded());
        store.LocatorDeleteResults.Enqueue(
            SecretStoreOperationResult.Failed(SecretStoreFailure.ProtectedRecordUnavailable));
        store.LocatorDeleteResults.Enqueue(SecretStoreOperationResult.Succeeded());
        store.LocatorDeleteResults.Enqueue(SecretStoreOperationResult.Succeeded());
        var service = new SourceChannelProtectedRecordDeletionService(store);
        SourceId sourceId = SourceId.Generate();
        ContentSource source = CreateSource(sourceId, ContentSourceStatus.DeletionPending);
        PlaylistSnapshot snapshot = CreateSnapshot(sourceId);
        LiveChannel channel = CreateRemoteChannel(sourceId, snapshot.Id);

        SecretStoreOperationResult first = await service.DeleteAsync(source, snapshot, channel);
        SecretStoreOperationResult second = await service.DeleteAsync(source, snapshot, channel);

        Assert.IsFalse(first.IsSuccess);
        Assert.AreEqual(SecretStoreFailure.ProtectedRecordUnavailable, first.Failure);
        Assert.IsTrue(second.IsSuccess);
        CollectionAssert.AreEqual(
            new[]
            {
                ProtectedValuePurpose.ChannelStreamLocator,
                ProtectedValuePurpose.ChannelLogoLocator,
                ProtectedValuePurpose.ChannelStreamLocator,
                ProtectedValuePurpose.ChannelLogoLocator,
            },
            store.LocatorDeleteCalls.Select(call => call.Purpose).ToArray());
    }

    private static void AssertDeleteCall(
        LocatorDeleteCall call,
        ContentSource source,
        LiveChannel channel,
        ProtectedValuePurpose expectedPurpose,
        ProtectedLocatorReference expectedReference,
        bool expectedCanBeCanceled)
    {
        Assert.AreEqual(source.Id, call.SourceId);
        Assert.AreEqual(expectedPurpose, call.Purpose);
        Assert.AreEqual(ProtectedRecordOwner.ForChannel(channel.Id), call.Owner);
        Assert.AreSame(expectedReference, call.Reference);
        Assert.AreEqual(expectedCanBeCanceled, call.CancellationToken.CanBeCanceled);
    }

    private static ContentSource CreateSource(
        SourceId sourceId,
        ContentSourceStatus status,
        SnapshotId? activeSnapshotId = null)
    {
        activeSnapshotId ??= status is ContentSourceStatus.Ready
            ? SnapshotId.Generate()
            : null;
        DateTimeOffset? lastSuccessfulSyncAt = activeSnapshotId.HasValue
            ? FixedInstant
            : null;
        DomainErrorCode? lastErrorCode = status is ContentSourceStatus.Failed
            ? DomainErrorCode.StorageUnavailable
            : null;
        DomainResult<ContentSource> result = ContentSource.Create(
            SourceDraftTestFixtures.CreateRemoteDraft(sourceId),
            status,
            FixedInstant,
            FixedInstant,
            activeSnapshotId,
            lastSuccessfulSyncAt,
            lastErrorCode);
        return result.IsSuccess
            ? result.Value!
            : throw new InvalidOperationException("A synthetic source could not be created.");
    }

    private static PlaylistSnapshot CreateSnapshot(
        SourceId sourceId,
        SnapshotId? snapshotId = null,
        PlaylistSnapshotState state = PlaylistSnapshotState.Complete)
    {
        DomainResult<PlaylistSnapshot> result = PlaylistSnapshot.Create(
            snapshotId ?? SnapshotId.Generate(),
            sourceId,
            FixedInstant,
            new string('a', 64),
            parserVersion: 1,
            normalizationVersion: 1,
            schemaVersion: 1,
            itemCount: 1,
            warningCount: 0,
            state);
        return result.IsSuccess
            ? result.Value!
            : throw new InvalidOperationException("A synthetic playlist snapshot could not be created.");
    }

    private static LiveChannel CreateRemoteChannel(
        SourceId sourceId,
        SnapshotId snapshotId,
        SourceId? stableKeySourceId = null,
        bool includeLogo = true)
    {
        ChannelId channelId = ChannelId.Generate();
        DomainResult<LiveChannel> result = LiveChannel.Create(
            channelId,
            CreateStableKey(stableKeySourceId ?? sourceId, channelId),
            snapshotId,
            CategoryId.Generate(),
            providerKey: null,
            providerPlaybackKey: null,
            "Synthetic Remote Channel",
            number: 1,
            includeLogo ? SourceDraftTestFixtures.CreateLocatorReference() : null,
            SourceDraftTestFixtures.CreateLocatorReference(),
            ChannelContainerHint.Hls,
            isAdultHint: false,
            ChannelNormalizationWarnings.None);
        return result.IsSuccess
            ? result.Value!
            : throw new InvalidOperationException("A synthetic remote channel could not be created.");
    }

    private static LiveChannel CreateProviderChannel(
        SourceId sourceId,
        SnapshotId snapshotId,
        bool includeLogo)
    {
        ChannelId channelId = ChannelId.Generate();
        DomainResult<ProviderItemKey> playback = ProviderItemKey.Create("synthetic-provider-stream");
        if (!playback.IsSuccess)
        {
            throw new InvalidOperationException("A synthetic provider playback key could not be created.");
        }

        DomainResult<LiveChannel> result = LiveChannel.Create(
            channelId,
            CreateStableKey(sourceId, channelId),
            snapshotId,
            CategoryId.Generate(),
            "synthetic-provider-channel",
            playback.Value,
            "Synthetic Provider Channel",
            number: 2,
            includeLogo ? SourceDraftTestFixtures.CreateLocatorReference() : null,
            streamReference: null,
            ChannelContainerHint.MpegTs,
            isAdultHint: null,
            ChannelNormalizationWarnings.None);
        return result.IsSuccess
            ? result.Value!
            : throw new InvalidOperationException("A synthetic provider channel could not be created.");
    }

    private static ChannelStableKey CreateStableKey(SourceId sourceId, ChannelId channelId)
    {
        DomainResult<ChannelStableKey> result = ChannelStableKeyBuilder.FromProviderStreamId(
            sourceId,
            "synthetic-provider",
            channelId.Value.ToString("N"));
        return result.IsSuccess
            ? result.Value
            : throw new InvalidOperationException("A synthetic channel stable key could not be created.");
    }

    private sealed class ChannelDeletionProbeSecretStore : ISecretStore
    {
        internal Queue<SecretStoreOperationResult> LocatorDeleteResults { get; } = new();

        internal List<LocatorDeleteCall> LocatorDeleteCalls { get; } = [];

        internal CancellationTokenSource? CancellationToSignalAtSuccessfulCall { get; init; }

        internal int SuccessfulCallToSignal { get; init; }

        public ValueTask<SecretStoreOperationResult> DeleteLocatorAsync(
            SourceId sourceId,
            ProtectedValuePurpose purpose,
            ProtectedRecordOwner owner,
            ProtectedLocatorReference reference,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LocatorDeleteCalls.Add(
                new LocatorDeleteCall(
                    sourceId,
                    purpose,
                    owner,
                    reference,
                    cancellationToken));
            SecretStoreOperationResult result = LocatorDeleteResults.Count == 0
                ? SecretStoreOperationResult.Succeeded()
                : LocatorDeleteResults.Dequeue();
            if (result.IsSuccess && LocatorDeleteCalls.Count == SuccessfulCallToSignal)
            {
                CancellationToSignalAtSuccessfulCall?.Cancel();
            }

            return ValueTask.FromResult(result);
        }

        public ValueTask<SecretReferenceCreationResult> CreateCredentialsAsync(
            SourceId sourceId,
            ProtectedRecordOwner owner,
            ReadOnlyMemory<byte> value,
            CancellationToken cancellationToken = default) => throw UnexpectedOperation();

        public ValueTask<ProtectedLocatorReferenceCreationResult> CreateLocatorAsync(
            SourceId sourceId,
            ProtectedValuePurpose purpose,
            ProtectedRecordOwner owner,
            ReadOnlyMemory<byte> value,
            CancellationToken cancellationToken = default) => throw UnexpectedOperation();

        public ValueTask<SecretStoreReadResult> ReadCredentialsAsync(
            SourceId sourceId,
            ProtectedRecordOwner owner,
            SecretReference reference,
            CancellationToken cancellationToken = default) => throw UnexpectedOperation();

        public ValueTask<SecretStoreReadResult> ReadLocatorAsync(
            SourceId sourceId,
            ProtectedValuePurpose purpose,
            ProtectedRecordOwner owner,
            ProtectedLocatorReference reference,
            CancellationToken cancellationToken = default) => throw UnexpectedOperation();

        public ValueTask<SecretStoreOperationResult> UpdateCredentialsAsync(
            SourceId sourceId,
            ProtectedRecordOwner owner,
            SecretReference reference,
            ReadOnlyMemory<byte> value,
            CancellationToken cancellationToken = default) => throw UnexpectedOperation();

        public ValueTask<SecretStoreOperationResult> UpdateLocatorAsync(
            SourceId sourceId,
            ProtectedValuePurpose purpose,
            ProtectedRecordOwner owner,
            ProtectedLocatorReference reference,
            ReadOnlyMemory<byte> value,
            CancellationToken cancellationToken = default) => throw UnexpectedOperation();

        public ValueTask<SecretStoreOperationResult> DeleteCredentialsAsync(
            SourceId sourceId,
            ProtectedRecordOwner owner,
            SecretReference reference,
            CancellationToken cancellationToken = default) => throw UnexpectedOperation();

        private static InvalidOperationException UnexpectedOperation() =>
            new("The channel deletion service called an unexpected secret-store operation.");
    }

    private sealed record LocatorDeleteCall(
        SourceId SourceId,
        ProtectedValuePurpose Purpose,
        ProtectedRecordOwner Owner,
        ProtectedLocatorReference Reference,
        CancellationToken CancellationToken);
}
