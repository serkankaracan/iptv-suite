using IptvSuite.Application;
using IptvSuite.Domain;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace IptvSuite.UnitTests;

[TestClass]
public sealed class XtreamSourceOnboardingTests
{
    private static readonly DateTimeOffset FixedInstant =
        new(2026, 8, 29, 12, 0, 0, TimeSpan.Zero);

    [TestMethod]
    public async Task KnownNotCommittedImportDeletesTheExactProtectedConfiguration()
    {
        var store = new OnboardingSecretStore();
        var importer = new OnboardingImporter(XtreamCatalogImportResult.NotCommitted(
            DomainError.Create(DomainErrorCode.AuthenticationRejected)));
        var service = new XtreamSourceOnboardingService(
            store,
            importer,
            TimeProvider.System);

        DomainResult<XtreamSourceOnboardingResult> result = await service.AddAsync(
            "Synthetic source",
            "https://fixture.invalid/provider",
            "synthetic-user",
            "synthetic-password");

        Assert.IsFalse(result.IsSuccess);
        Assert.AreEqual(DomainErrorCode.AuthenticationRejected, result.Error!.Code);
        Assert.AreEqual(1, store.DeleteCount);
        Assert.AreEqual(store.CreatedSourceId, store.DeletedSourceId);
        Assert.AreEqual(store.CreatedOwner, store.DeletedOwner);
        Assert.AreEqual(store.Reference, store.DeletedReference);
        Assert.AreEqual(store.CreatedSourceId, importer.Source!.Id);
    }

    [TestMethod]
    public async Task IndeterminateImportRetainsProtectedConfiguration()
    {
        var store = new OnboardingSecretStore();
        var importer = new OnboardingImporter(XtreamCatalogImportResult.Indeterminate(
            DomainError.Create(DomainErrorCode.StorageUnavailable)));
        var service = new XtreamSourceOnboardingService(
            store,
            importer,
            TimeProvider.System);

        DomainResult<XtreamSourceOnboardingResult> result = await service.AddAsync(
            "Synthetic source",
            "https://fixture.invalid/provider",
            "synthetic-user",
            "synthetic-password");

        Assert.IsFalse(result.IsSuccess);
        Assert.AreEqual(DomainErrorCode.StorageUnavailable, result.Error!.Code);
        Assert.AreEqual(0, store.DeleteCount);
    }

    [TestMethod]
    public async Task ReplacementKeepsSourceIdentityAndRetiresThePreviousConfiguration()
    {
        ContentSource existing = CreateExistingSource();
        var store = new OnboardingSecretStore();
        var importer = new OnboardingImporter(XtreamCatalogImportResult.Committed(
            new ContentCatalogCounts(3, 2, 1, 0)));
        var service = new XtreamSourceOnboardingService(
            store,
            importer,
            new FixedTimeProvider(FixedInstant));

        DomainResult<XtreamSourceOnboardingResult> result = await service.ReplaceAsync(
            existing,
            "Replacement source",
            "https://replacement.fixture.invalid/provider",
            "replacement-user",
            "replacement-password");

        Assert.IsTrue(result.IsSuccess);
        Assert.AreEqual(existing.Id, result.Value!.SourceId);
        Assert.IsFalse(result.Value.PreviousConfigurationCleanupPending);
        Assert.AreEqual(existing.Id, importer.Source!.Id);
        Assert.AreNotEqual(
            existing.Configuration.ConfigurationId,
            importer.Source.Configuration.ConfigurationId);
        Assert.AreEqual(1, store.DeleteCount);
        Assert.AreEqual(existing.Id, store.DeletedSourceId);
        Assert.AreEqual(
            ProtectedRecordOwner.ForSourceConfiguration(
                existing.Configuration.ConfigurationId),
            store.DeletedOwner);
        Assert.AreEqual(
            ((XtreamSourceConfiguration)existing.Configuration).CredentialsReference,
            store.DeletedReference);
    }

    [TestMethod]
    public async Task FailedReplacementCleanupIsReportedWithoutHidingCommittedResult()
    {
        ContentSource existing = CreateExistingSource();
        var store = new OnboardingSecretStore
        {
            DeleteResult = SecretStoreOperationResult.Failed(
                SecretStoreFailure.StorageUnavailable),
        };
        var importer = new OnboardingImporter(XtreamCatalogImportResult.Committed(
            new ContentCatalogCounts(1, 0, 0, 0)));
        var service = new XtreamSourceOnboardingService(
            store,
            importer,
            new FixedTimeProvider(FixedInstant));

        DomainResult<XtreamSourceOnboardingResult> result = await service.ReplaceAsync(
            existing,
            "Replacement source",
            "https://replacement.fixture.invalid/provider",
            "replacement-user",
            "replacement-password");

        Assert.IsTrue(result.IsSuccess);
        Assert.AreEqual(existing.Id, result.Value!.SourceId);
        Assert.IsTrue(result.Value.PreviousConfigurationCleanupPending);
        Assert.AreEqual(1, store.DeleteCount);
    }

    [TestMethod]
    public async Task CancelledNotCommittedImportDeletesTheStagedConfiguration()
    {
        var store = new OnboardingSecretStore();
        var importer = new OnboardingImporter(XtreamCatalogImportResult.NotCommitted(
            DomainError.Create(DomainErrorCode.OperationCancelled)));
        var service = new XtreamSourceOnboardingService(
            store,
            importer,
            new FixedTimeProvider(FixedInstant));

        DomainResult<XtreamSourceOnboardingResult> result = await service.AddAsync(
            "Synthetic source",
            "https://fixture.invalid/provider",
            "synthetic-user",
            "synthetic-password");

        Assert.IsFalse(result.IsSuccess);
        Assert.AreEqual(DomainErrorCode.OperationCancelled, result.Error!.Code);
        Assert.AreEqual(1, store.DeleteCount);
        Assert.AreEqual(store.CreatedOwner, store.DeletedOwner);
        Assert.AreEqual(store.Reference, store.DeletedReference);
    }

    private static ContentSource CreateExistingSource()
    {
        ValidatedSourceDraft draft = SourceDraftTestFixtures.CreateXtreamDraft(
            SourceId.Generate(),
            "Existing source",
            "https://existing.fixture.invalid/provider");
        DomainResult<ContentSource> source = ContentSource.Create(
            draft,
            ContentSourceStatus.Testing,
            FixedInstant,
            FixedInstant);
        return source.IsSuccess
            ? source.Value!
            : throw new InvalidOperationException("The synthetic source is invalid.");
    }

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }

    private sealed class OnboardingImporter(XtreamCatalogImportResult result)
        : IXtreamCatalogImportService
    {
        internal ContentSource? Source { get; private set; }

        public ValueTask<XtreamCatalogImportResult> ImportWithDispositionAsync(
            ContentSource source,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Source = source;
            return ValueTask.FromResult(result);
        }

        public ValueTask<DomainResult<ContentCatalogCounts>> ImportAsync(
            ContentSource source,
            CancellationToken cancellationToken = default) => throw Unused();

        public ValueTask<DomainResult<ContentCatalogCounts>> RefreshFromStoredConfigurationAsync(
            SourceId sourceId,
            CancellationToken cancellationToken = default) => throw Unused();
    }

    private sealed class OnboardingSecretStore : ISecretStore
    {
        internal SecretReference Reference { get; } =
            SecretReference.Parse("secret-ref-v1:11111111111111111111111111111111").Value!;

        internal SourceId CreatedSourceId { get; private set; }

        internal ProtectedRecordOwner CreatedOwner { get; private set; }

        internal int DeleteCount { get; private set; }

        internal SourceId DeletedSourceId { get; private set; }

        internal ProtectedRecordOwner DeletedOwner { get; private set; }

        internal SecretReference? DeletedReference { get; private set; }

        internal SecretStoreOperationResult DeleteResult { get; init; } =
            SecretStoreOperationResult.Succeeded();

        public ValueTask<SecretReferenceCreationResult> CreateCredentialsAsync(
            SourceId sourceId,
            ProtectedRecordOwner owner,
            ReadOnlyMemory<byte> value,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CreatedSourceId = sourceId;
            CreatedOwner = owner;
            return ValueTask.FromResult(SecretReferenceCreationResult.Succeeded(Reference));
        }

        public ValueTask<SecretStoreOperationResult> DeleteCredentialsAsync(
            SourceId sourceId,
            ProtectedRecordOwner owner,
            SecretReference reference,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            DeleteCount++;
            DeletedSourceId = sourceId;
            DeletedOwner = owner;
            DeletedReference = reference;
            return ValueTask.FromResult(DeleteResult);
        }

        public ValueTask<ProtectedLocatorReferenceCreationResult> CreateLocatorAsync(
            SourceId sourceId,
            ProtectedValuePurpose purpose,
            ProtectedRecordOwner owner,
            ReadOnlyMemory<byte> value,
            CancellationToken cancellationToken = default) => throw Unused();

        public ValueTask<SecretStoreReadResult> ReadCredentialsAsync(
            SourceId sourceId,
            ProtectedRecordOwner owner,
            SecretReference reference,
            CancellationToken cancellationToken = default) => throw Unused();

        public ValueTask<SecretStoreReadResult> ReadLocatorAsync(
            SourceId sourceId,
            ProtectedValuePurpose purpose,
            ProtectedRecordOwner owner,
            ProtectedLocatorReference reference,
            CancellationToken cancellationToken = default) => throw Unused();

        public ValueTask<SecretStoreOperationResult> UpdateCredentialsAsync(
            SourceId sourceId,
            ProtectedRecordOwner owner,
            SecretReference reference,
            ReadOnlyMemory<byte> value,
            CancellationToken cancellationToken = default) => throw Unused();

        public ValueTask<SecretStoreOperationResult> UpdateLocatorAsync(
            SourceId sourceId,
            ProtectedValuePurpose purpose,
            ProtectedRecordOwner owner,
            ProtectedLocatorReference reference,
            ReadOnlyMemory<byte> value,
            CancellationToken cancellationToken = default) => throw Unused();

        public ValueTask<SecretStoreOperationResult> DeleteLocatorAsync(
            SourceId sourceId,
            ProtectedValuePurpose purpose,
            ProtectedRecordOwner owner,
            ProtectedLocatorReference reference,
            CancellationToken cancellationToken = default) => throw Unused();
    }

    private static InvalidOperationException Unused() =>
        new("The synthetic test does not permit this operation.");
}
