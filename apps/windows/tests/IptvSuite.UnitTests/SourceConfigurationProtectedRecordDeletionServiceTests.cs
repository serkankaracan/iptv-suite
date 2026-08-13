using System.Reflection;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace IptvSuite.UnitTests;

[TestClass]
public sealed class SourceConfigurationProtectedRecordDeletionServiceTests
{
    private static readonly DateTimeOffset FixedInstant =
        new(2026, 8, 14, 0, 0, 0, TimeSpan.Zero);

    [TestMethod]
    public void ConstructorRequiresASecretStore()
    {
        Assert.ThrowsExactly<ArgumentNullException>(() =>
            new SourceConfigurationProtectedRecordDeletionService(null!));
    }

    [TestMethod]
    public void PublicDeletionApiAcceptsOnlyTheAuthoritativeAggregateAndCancellation()
    {
        MethodInfo? delete = typeof(SourceConfigurationProtectedRecordDeletionService).GetMethod(
            nameof(SourceConfigurationProtectedRecordDeletionService.DeleteAsync),
            BindingFlags.Instance | BindingFlags.Public);
        Assert.IsNotNull(delete);

        Type[] parameterTypes = delete.GetParameters()
            .Select(parameter => parameter.ParameterType)
            .ToArray();
        CollectionAssert.AreEqual(
            new[] { typeof(ContentSource), typeof(CancellationToken) },
            parameterTypes);
        Assert.IsFalse(parameterTypes.Contains(typeof(SourceId)));
        Assert.IsFalse(parameterTypes.Contains(typeof(SourceConfigurationId)));
        Assert.IsFalse(parameterTypes.Contains(typeof(ProtectedRecordOwner)));
        Assert.IsFalse(parameterTypes.Contains(typeof(SecretReference)));
        Assert.IsFalse(parameterTypes.Contains(typeof(ProtectedLocatorReference)));
    }

    [TestMethod]
    public async Task MissingOrActiveSourceCannotReachTheSecretStore()
    {
        var store = new DeletionProbeSecretStore();
        var service = new SourceConfigurationProtectedRecordDeletionService(store);

        await Assert.ThrowsExactlyAsync<ArgumentNullException>(async () =>
            await service.DeleteAsync(null!));

        foreach (ContentSourceStatus status in
            Enum.GetValues<ContentSourceStatus>().Where(
                value => value is not ContentSourceStatus.DeletionPending))
        {
            ContentSource source = CreateSource(
                SourceDraftTestFixtures.CreateRemoteDraft(SourceId.Generate()),
                status);

            await Assert.ThrowsExactlyAsync<InvalidOperationException>(async () =>
                await service.DeleteAsync(source));
        }

        Assert.AreEqual(0, store.CredentialDeleteCalls.Count);
        Assert.AreEqual(0, store.LocatorDeleteCalls.Count);
    }

    [TestMethod]
    public async Task XtreamDeletionUsesTheAggregateOwnedCredentialKey()
    {
        var store = new DeletionProbeSecretStore();
        var service = new SourceConfigurationProtectedRecordDeletionService(store);
        ContentSource source = CreateSource(
            SourceDraftTestFixtures.CreateXtreamDraft(SourceId.Generate()),
            ContentSourceStatus.DeletionPending);
        var configuration = (XtreamSourceConfiguration)source.Configuration;

        SecretStoreOperationResult result = await service.DeleteAsync(source);

        Assert.IsTrue(result.IsSuccess);
        Assert.AreEqual(SecretStoreFailure.None, result.Failure);
        Assert.HasCount(1, store.CredentialDeleteCalls);
        Assert.IsEmpty(store.LocatorDeleteCalls);
        CredentialDeleteCall call = store.CredentialDeleteCalls.Single();
        Assert.AreEqual(source.Id, call.SourceId);
        Assert.AreEqual(
            ProtectedRecordOwner.ForSourceConfiguration(configuration.ConfigurationId),
            call.Owner);
        Assert.AreSame(configuration.CredentialsReference, call.Reference);
    }

    [TestMethod]
    public async Task RemoteDeletionUsesTheAggregateOwnedLocatorKeyAndPurpose()
    {
        var store = new DeletionProbeSecretStore();
        var service = new SourceConfigurationProtectedRecordDeletionService(store);
        ContentSource source = CreateSource(
            SourceDraftTestFixtures.CreateRemoteDraft(SourceId.Generate()),
            ContentSourceStatus.DeletionPending);
        var configuration = (RemotePlaylistSourceConfiguration)source.Configuration;

        SecretStoreOperationResult result = await service.DeleteAsync(source);

        Assert.IsTrue(result.IsSuccess);
        Assert.IsEmpty(store.CredentialDeleteCalls);
        Assert.HasCount(1, store.LocatorDeleteCalls);
        LocatorDeleteCall call = store.LocatorDeleteCalls.Single();
        Assert.AreEqual(source.Id, call.SourceId);
        Assert.AreEqual(ProtectedValuePurpose.RemotePlaylistLocator, call.Purpose);
        Assert.AreEqual(
            ProtectedRecordOwner.ForSourceConfiguration(configuration.ConfigurationId),
            call.Owner);
        Assert.AreSame(configuration.LocatorReference, call.Reference);
    }

    [TestMethod]
    public async Task PreCancelledDeletionDoesNotReachTheSecretStore()
    {
        var store = new DeletionProbeSecretStore();
        var service = new SourceConfigurationProtectedRecordDeletionService(store);
        ContentSource source = CreateSource(
            SourceDraftTestFixtures.CreateRemoteDraft(SourceId.Generate()),
            ContentSourceStatus.DeletionPending);
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();

        await Assert.ThrowsExactlyAsync<OperationCanceledException>(async () =>
            await service.DeleteAsync(source, cancellation.Token));

        Assert.IsEmpty(store.CredentialDeleteCalls);
        Assert.IsEmpty(store.LocatorDeleteCalls);
    }

    [TestMethod]
    public async Task CancellationSignalledAtDeleteCommitDoesNotReplaceSuccess()
    {
        using CancellationTokenSource cancellation = new();
        var store = new DeletionProbeSecretStore
        {
            CancellationToSignalAtDeleteCommit = cancellation,
        };
        var service = new SourceConfigurationProtectedRecordDeletionService(store);
        ContentSource source = CreateSource(
            SourceDraftTestFixtures.CreateRemoteDraft(SourceId.Generate()),
            ContentSourceStatus.DeletionPending);

        SecretStoreOperationResult result = await service.DeleteAsync(source, cancellation.Token);

        Assert.IsTrue(cancellation.IsCancellationRequested);
        Assert.IsTrue(result.IsSuccess);
        Assert.HasCount(1, store.LocatorDeleteCalls);
    }

    [TestMethod]
    public async Task CancellationSignalledAtCredentialDeleteCommitDoesNotReplaceSuccess()
    {
        using CancellationTokenSource cancellation = new();
        var store = new DeletionProbeSecretStore
        {
            CancellationToSignalAtDeleteCommit = cancellation,
        };
        var service = new SourceConfigurationProtectedRecordDeletionService(store);
        ContentSource source = CreateSource(
            SourceDraftTestFixtures.CreateXtreamDraft(SourceId.Generate()),
            ContentSourceStatus.DeletionPending);

        SecretStoreOperationResult result = await service.DeleteAsync(source, cancellation.Token);

        Assert.IsTrue(cancellation.IsCancellationRequested);
        Assert.IsTrue(result.IsSuccess);
        Assert.HasCount(1, store.CredentialDeleteCalls);
    }

    [TestMethod]
    [DataRow(SecretStoreFailure.ProtectedRecordUnavailable)]
    [DataRow(SecretStoreFailure.StorageUnavailable)]
    public async Task StoreFailureIsReturnedWithoutFallback(SecretStoreFailure failure)
    {
        var store = new DeletionProbeSecretStore
        {
            LocatorDeleteResult = SecretStoreOperationResult.Failed(failure),
        };
        var service = new SourceConfigurationProtectedRecordDeletionService(store);
        ContentSource source = CreateSource(
            SourceDraftTestFixtures.CreateRemoteDraft(SourceId.Generate()),
            ContentSourceStatus.DeletionPending);

        SecretStoreOperationResult result = await service.DeleteAsync(source);

        Assert.IsFalse(result.IsSuccess);
        Assert.AreEqual(failure, result.Failure);
        Assert.HasCount(1, store.LocatorDeleteCalls);
        Assert.IsEmpty(store.CredentialDeleteCalls);
    }

    private static ContentSource CreateSource(
        ValidatedSourceDraft draft,
        ContentSourceStatus status)
    {
        SnapshotId? activeSnapshotId = status is ContentSourceStatus.Ready
            ? SnapshotId.Generate()
            : null;
        DateTimeOffset? lastSuccessfulSyncAt = status is ContentSourceStatus.Ready
            ? FixedInstant
            : null;
        DomainErrorCode? lastErrorCode = status is ContentSourceStatus.Failed
            ? DomainErrorCode.StorageUnavailable
            : null;
        DomainResult<ContentSource> result = ContentSource.Create(
            draft,
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

    private sealed class DeletionProbeSecretStore : ISecretStore
    {
        internal SecretStoreOperationResult CredentialDeleteResult { get; init; } =
            SecretStoreOperationResult.Succeeded();

        internal SecretStoreOperationResult LocatorDeleteResult { get; init; } =
            SecretStoreOperationResult.Succeeded();

        internal CancellationTokenSource? CancellationToSignalAtDeleteCommit { get; init; }

        internal List<CredentialDeleteCall> CredentialDeleteCalls { get; } = [];

        internal List<LocatorDeleteCall> LocatorDeleteCalls { get; } = [];

        public ValueTask<SecretStoreOperationResult> DeleteCredentialsAsync(
            SourceId sourceId,
            ProtectedRecordOwner owner,
            SecretReference reference,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CredentialDeleteCalls.Add(new CredentialDeleteCall(sourceId, owner, reference));
            CancellationToSignalAtDeleteCommit?.Cancel();
            return ValueTask.FromResult(CredentialDeleteResult);
        }

        public ValueTask<SecretStoreOperationResult> DeleteLocatorAsync(
            SourceId sourceId,
            ProtectedValuePurpose purpose,
            ProtectedRecordOwner owner,
            ProtectedLocatorReference reference,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LocatorDeleteCalls.Add(new LocatorDeleteCall(sourceId, purpose, owner, reference));
            CancellationToSignalAtDeleteCommit?.Cancel();
            return ValueTask.FromResult(LocatorDeleteResult);
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

        private static InvalidOperationException UnexpectedOperation() =>
            new("The deletion service called an unexpected secret-store operation.");
    }

    private sealed record CredentialDeleteCall(
        SourceId SourceId,
        ProtectedRecordOwner Owner,
        SecretReference Reference);

    private sealed record LocatorDeleteCall(
        SourceId SourceId,
        ProtectedValuePurpose Purpose,
        ProtectedRecordOwner Owner,
        ProtectedLocatorReference Reference);
}
