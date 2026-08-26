using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace IptvSuite.UnitTests;

[TestClass]
public sealed class RemotePlaylistSourceOnboardingServiceTests
{
    private static readonly DateTimeOffset FixedInstant =
        new(2026, 8, 26, 12, 0, 0, TimeSpan.Zero);
    private static readonly string[] ProbeOnlyEvents = ["probe"];
    private static readonly string[] SuccessfulEvents = ["probe", "create", "import"];
    private static readonly string[] CleanupEvents = ["probe", "create", "import", "delete"];

    [TestMethod]
    public void ConstructorRequiresEveryDependency()
    {
        var store = new OnboardingSecretStore();
        var transport = new OnboardingTransport();
        var importer = new OnboardingImporter(RemotePlaylistCatalogImportResult.Committed(1, 0));
        var time = new FixedTimeProvider(FixedInstant);

        Assert.ThrowsExactly<ArgumentNullException>(() =>
            new RemotePlaylistSourceOnboardingService(null!, transport, importer, time));
        Assert.ThrowsExactly<ArgumentNullException>(() =>
            new RemotePlaylistSourceOnboardingService(store, null!, importer, time));
        Assert.ThrowsExactly<ArgumentNullException>(() =>
            new RemotePlaylistSourceOnboardingService(store, transport, null!, time));
        Assert.ThrowsExactly<ArgumentNullException>(() =>
            new RemotePlaylistSourceOnboardingService(store, transport, importer, null!));
    }

    [TestMethod]
    public void CatalogImportResultFactoriesEnforceCommitStateAndHideDetails()
    {
        DomainError error = DomainError.Create(DomainErrorCode.UnsupportedPlaylistFormat);
        RemotePlaylistCatalogImportResult committed =
            RemotePlaylistCatalogImportResult.Committed(12, 3);
        RemotePlaylistCatalogImportResult notCommitted =
            RemotePlaylistCatalogImportResult.NotCommitted(error);
        RemotePlaylistCatalogImportResult indeterminate =
            RemotePlaylistCatalogImportResult.Indeterminate(error);

        Assert.AreEqual(CatalogImportCommitDisposition.Committed, committed.Disposition);
        Assert.AreEqual(12, committed.ImportedChannelCount);
        Assert.AreEqual(3, committed.WarningCount);
        Assert.IsNull(committed.Error);
        Assert.AreEqual(CatalogImportCommitDisposition.NotCommitted, notCommitted.Disposition);
        Assert.IsNull(notCommitted.ImportedChannelCount);
        Assert.IsNull(notCommitted.WarningCount);
        Assert.AreSame(error, notCommitted.Error);
        Assert.AreEqual(CatalogImportCommitDisposition.Indeterminate, indeterminate.Disposition);
        Assert.IsNull(indeterminate.ImportedChannelCount);
        Assert.IsNull(indeterminate.WarningCount);
        Assert.AreSame(error, indeterminate.Error);
        Assert.AreEqual("[REMOTE-PLAYLIST-CATALOG-IMPORT-RESULT]", committed.ToString());
        Assert.AreEqual("[REMOTE-PLAYLIST-CATALOG-IMPORT-RESULT]", notCommitted.ToString());
        Assert.AreEqual("[REMOTE-PLAYLIST-CATALOG-IMPORT-RESULT]", indeterminate.ToString());

        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            RemotePlaylistCatalogImportResult.Committed(-1, 0));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            RemotePlaylistCatalogImportResult.Committed(0, -1));
        Assert.ThrowsExactly<ArgumentNullException>(() =>
            RemotePlaylistCatalogImportResult.NotCommitted(null!));
        Assert.ThrowsExactly<ArgumentNullException>(() =>
            RemotePlaylistCatalogImportResult.Indeterminate(null!));
    }

    [TestMethod]
    public async Task InvalidUserInfoIsRejectedBeforeProbeOrStoreMutation()
    {
        string sensitive = SecurityTestAssertions.CreateSensitiveValue("ONBOARDING-USERINFO");
        var events = new List<string>();
        var store = new OnboardingSecretStore(events);
        var transport = new OnboardingTransport(events);
        var importer = new OnboardingImporter(
            RemotePlaylistCatalogImportResult.Committed(1, 0),
            events);
        RemotePlaylistSourceOnboardingService service = CreateService(store, transport, importer);

        DomainResult<RemotePlaylistSourceOnboardingResult> result = await service.AddAsync(
            "Synthetic Source",
            $"https://user:{sensitive}@example.test/list.m3u");

        SecurityTestAssertions.IsFailure(result, DomainErrorCode.EndpointUserInfoNotAllowed);
        Assert.IsEmpty(events);
        SecurityTestAssertions.DoesNotContainSensitive(result.ToString(), sensitive);
    }

    [TestMethod]
    public async Task ProbeFailurePrecedesAndPreventsEveryStoreMutation()
    {
        var events = new List<string>();
        var store = new OnboardingSecretStore(events);
        var transport = new OnboardingTransport(events)
        {
            Failure = HttpTransportFailure.TlsValidationFailed,
        };
        var importer = new OnboardingImporter(
            RemotePlaylistCatalogImportResult.Committed(1, 0),
            events);
        RemotePlaylistSourceOnboardingService service = CreateService(store, transport, importer);

        DomainResult<RemotePlaylistSourceOnboardingResult> result = await service.AddAsync(
            "Synthetic Source",
            "https://example.test/list.m3u");

        SecurityTestAssertions.IsFailure(result, DomainErrorCode.TlsValidationFailed);
        CollectionAssert.AreEqual(ProbeOnlyEvents, events);
        Assert.AreEqual(HttpTransportLimits.MaximumAllowedResponseBytes, transport.MaximumResponseBytes);
        Assert.AreEqual(0, store.CreateLocatorCount);
        Assert.AreEqual(0, store.DeleteLocatorCount);
        Assert.AreEqual(0, importer.CallCount);
    }

    [TestMethod]
    public async Task SuccessfulAddProbesProtectsAndCommitsInOrder()
    {
        string sensitive = SecurityTestAssertions.CreateSensitiveValue("ONBOARDING-LOCATOR");
        string locator = $"https://example.test/private/list.m3u?token={sensitive}";
        var events = new List<string>();
        var store = new OnboardingSecretStore(events);
        var transport = new OnboardingTransport(events);
        var importer = new OnboardingImporter(
            RemotePlaylistCatalogImportResult.Committed(42, 5),
            events);
        RemotePlaylistSourceOnboardingService service = CreateService(store, transport, importer);

        DomainResult<RemotePlaylistSourceOnboardingResult> result = await service.AddAsync(
            "  Synthetic Source  ",
            locator);

        Assert.IsTrue(result.IsSuccess);
        Assert.IsFalse(result.Value!.SourceId.IsEmpty);
        Assert.AreEqual(42, result.Value.ImportedChannelCount);
        Assert.AreEqual(5, result.Value.WarningCount);
        Assert.AreEqual("[REMOTE-PLAYLIST-SOURCE-ONBOARDING-RESULT]", result.Value.ToString());
        CollectionAssert.AreEqual(SuccessfulEvents, events);
        Assert.AreEqual(HttpTransportLimits.MaximumAllowedResponseBytes, transport.MaximumResponseBytes);
        Assert.AreEqual(1, store.CreateLocatorCount);
        Assert.AreEqual(0, store.DeleteLocatorCount);
        Assert.AreEqual(1, importer.CallCount);
        Assert.AreEqual(ContentSourceStatus.Testing, importer.Source!.Status);
        Assert.AreEqual(SourceKind.RemotePlaylist, importer.Source.Kind);
        Assert.AreEqual("Synthetic Source", importer.Source.DisplayName);
        Assert.AreEqual(result.Value.SourceId, importer.Source.Id);
        Assert.AreEqual(result.Value.SourceId, store.CreatedSourceId);
        Assert.AreEqual(
            ProtectedValuePurpose.RemotePlaylistLocator,
            store.CreatedPurpose);

        string observable = string.Join('|', result, result.Value, JsonSerializer.Serialize(result));
        SecurityTestAssertions.DoesNotContainSensitive(observable, sensitive, locator);
    }

    [TestMethod]
    public async Task NotCommittedImportDeletesExactProtectedRecordWithoutCallerCancellation()
    {
        var events = new List<string>();
        using CancellationTokenSource cancellation = new();
        var store = new OnboardingSecretStore(events);
        var transport = new OnboardingTransport(events);
        DomainError importError = DomainError.Create(DomainErrorCode.UnsupportedPlaylistFormat);
        var importer = new OnboardingImporter(
            RemotePlaylistCatalogImportResult.NotCommitted(importError),
            events)
        {
            CancellationToSignal = cancellation,
        };
        RemotePlaylistSourceOnboardingService service = CreateService(store, transport, importer);

        DomainResult<RemotePlaylistSourceOnboardingResult> result = await service.AddAsync(
            "Synthetic Source",
            "https://example.test/list.m3u",
            cancellation.Token);

        Assert.IsTrue(cancellation.IsCancellationRequested);
        SecurityTestAssertions.IsFailure(result, DomainErrorCode.UnsupportedPlaylistFormat);
        CollectionAssert.AreEqual(CleanupEvents, events);
        Assert.AreEqual(1, store.DeleteLocatorCount);
        Assert.IsFalse(store.DeleteCancellationCanBeCanceled);
        Assert.AreEqual(store.CreatedSourceId, store.DeletedSourceId);
        Assert.AreEqual(store.CreatedPurpose, store.DeletedPurpose);
        Assert.AreEqual(store.CreatedOwner, store.DeletedOwner);
        Assert.AreSame(store.IssuedReference, store.DeletedReference);
        Assert.AreEqual(ContentSourceStatus.Testing, importer.Source!.Status);
    }

    [TestMethod]
    public async Task FailedCleanupReplacesImportErrorWithStorageUnavailable()
    {
        var store = new OnboardingSecretStore
        {
            DeleteResult = SecretStoreOperationResult.Failed(SecretStoreFailure.StorageUnavailable),
        };
        var importer = new OnboardingImporter(
            RemotePlaylistCatalogImportResult.NotCommitted(
                DomainError.Create(DomainErrorCode.UnsupportedPlaylistFormat)));
        RemotePlaylistSourceOnboardingService service = CreateService(
            store,
            new OnboardingTransport(),
            importer);

        DomainResult<RemotePlaylistSourceOnboardingResult> result = await service.AddAsync(
            "Synthetic Source",
            "https://example.test/list.m3u");

        SecurityTestAssertions.IsFailure(result, DomainErrorCode.StorageUnavailable);
        Assert.AreEqual(1, store.DeleteLocatorCount);
    }

    [TestMethod]
    public async Task IndeterminateImportRetainsProtectedRecordAndFailsClosed()
    {
        var store = new OnboardingSecretStore();
        var importer = new OnboardingImporter(
            RemotePlaylistCatalogImportResult.Indeterminate(
                DomainError.Create(DomainErrorCode.PlaylistDownloadFailed)));
        RemotePlaylistSourceOnboardingService service = CreateService(
            store,
            new OnboardingTransport(),
            importer);

        DomainResult<RemotePlaylistSourceOnboardingResult> result = await service.AddAsync(
            "Synthetic Source",
            "https://example.test/list.m3u");

        SecurityTestAssertions.IsFailure(result, DomainErrorCode.StorageUnavailable);
        Assert.AreEqual(1, store.CreateLocatorCount);
        Assert.AreEqual(0, store.DeleteLocatorCount);
    }

    [TestMethod]
    public async Task ImportExceptionRetainsProtectedRecordAndFailsClosed()
    {
        var store = new OnboardingSecretStore();
        var importer = new OnboardingImporter(
            RemotePlaylistCatalogImportResult.Committed(1, 0))
        {
            ExceptionToThrow = new InvalidOperationException("untrusted provider detail"),
        };
        RemotePlaylistSourceOnboardingService service = CreateService(
            store,
            new OnboardingTransport(),
            importer);

        DomainResult<RemotePlaylistSourceOnboardingResult> result = await service.AddAsync(
            "Synthetic Source",
            "https://example.test/list.m3u");

        SecurityTestAssertions.IsFailure(result, DomainErrorCode.StorageUnavailable);
        Assert.AreEqual(1, store.CreateLocatorCount);
        Assert.AreEqual(0, store.DeleteLocatorCount);
    }

    [TestMethod]
    public async Task NullImportResultRetainsProtectedRecordAndFailsClosed()
    {
        var store = new OnboardingSecretStore();
        var importer = new OnboardingImporter(null);
        RemotePlaylistSourceOnboardingService service = CreateService(
            store,
            new OnboardingTransport(),
            importer);

        DomainResult<RemotePlaylistSourceOnboardingResult> result = await service.AddAsync(
            "Synthetic Source",
            "https://example.test/list.m3u");

        SecurityTestAssertions.IsFailure(result, DomainErrorCode.StorageUnavailable);
        Assert.AreEqual(1, store.CreateLocatorCount);
        Assert.AreEqual(0, store.DeleteLocatorCount);
    }

    [TestMethod]
    public async Task CatastrophicImportExceptionIsNotNormalized()
    {
        var store = new OnboardingSecretStore();
        var importer = new OnboardingImporter(
            RemotePlaylistCatalogImportResult.Committed(1, 0))
        {
            ExceptionToThrow = new CatastrophicTestException(),
        };
        RemotePlaylistSourceOnboardingService service = CreateService(
            store,
            new OnboardingTransport(),
            importer);

        await Assert.ThrowsExactlyAsync<CatastrophicTestException>(async () =>
            await service.AddAsync(
                "Synthetic Source",
                "https://example.test/list.m3u"));

        Assert.AreEqual(1, store.CreateLocatorCount);
        Assert.AreEqual(0, store.DeleteLocatorCount);
    }

    [TestMethod]
    public async Task CancellationSignalledAtCatalogCommitDoesNotReplaceSuccess()
    {
        using CancellationTokenSource cancellation = new();
        var store = new OnboardingSecretStore();
        var importer = new OnboardingImporter(
            RemotePlaylistCatalogImportResult.Committed(7, 1))
        {
            CancellationToSignal = cancellation,
        };
        RemotePlaylistSourceOnboardingService service = CreateService(
            store,
            new OnboardingTransport(),
            importer);

        DomainResult<RemotePlaylistSourceOnboardingResult> result = await service.AddAsync(
            "Synthetic Source",
            "https://example.test/list.m3u",
            cancellation.Token);

        Assert.IsTrue(cancellation.IsCancellationRequested);
        Assert.IsTrue(result.IsSuccess);
        Assert.AreEqual(7, result.Value!.ImportedChannelCount);
        Assert.AreEqual(0, store.DeleteLocatorCount);
    }

    [TestMethod]
    public async Task PreCancelledAddDoesNotReachAnyDependency()
    {
        var events = new List<string>();
        var store = new OnboardingSecretStore(events);
        var transport = new OnboardingTransport(events);
        var importer = new OnboardingImporter(
            RemotePlaylistCatalogImportResult.Committed(1, 0),
            events);
        RemotePlaylistSourceOnboardingService service = CreateService(store, transport, importer);
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();

        await Assert.ThrowsExactlyAsync<OperationCanceledException>(async () =>
            await service.AddAsync(
                "Synthetic Source",
                "https://example.test/list.m3u",
                cancellation.Token));

        Assert.IsEmpty(events);
    }

    private static RemotePlaylistSourceOnboardingService CreateService(
        OnboardingSecretStore store,
        OnboardingTransport transport,
        OnboardingImporter importer) => new(
            store,
            transport,
            importer,
            new FixedTimeProvider(FixedInstant));

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }

    private sealed class CatastrophicTestException : OutOfMemoryException
    {
    }

    private sealed class OnboardingTransport(List<string>? events = null) : IHttpTransport
    {
        internal HttpTransportFailure? Failure { get; init; }

        internal int MaximumResponseBytes { get; private set; }

        public ValueTask<HttpTransportResult> GetAsync(
            HttpTransportRequest request,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(request);
            cancellationToken.ThrowIfCancellationRequested();
            events?.Add("probe");
            MaximumResponseBytes = request.MaximumResponseBytes;
            return ValueTask.FromResult(Failure.HasValue
                ? HttpTransportResult.Failed(Failure.Value, HttpTransportRetryability.Never)
                : HttpTransportResult.Success(
                    200,
                    HttpResponseLease.CopyFrom("#EXTM3U"u8)));
        }
    }

    private sealed class OnboardingImporter(
        RemotePlaylistCatalogImportResult? result,
        List<string>? events = null) : IRemotePlaylistCatalogImporter
    {
        internal int CallCount { get; private set; }

        internal ContentSource? Source { get; private set; }

        internal CancellationTokenSource? CancellationToSignal { get; init; }

        internal Exception? ExceptionToThrow { get; init; }

        public ValueTask<RemotePlaylistCatalogImportResult> ImportAsync(
            ContentSource source,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(source);
            CallCount++;
            Source = source;
            events?.Add("import");
            CancellationToSignal?.Cancel();
            if (ExceptionToThrow is not null)
            {
                throw ExceptionToThrow;
            }

            return ValueTask.FromResult(result!);
        }
    }

    private sealed class OnboardingSecretStore(List<string>? events = null) : ISecretStore
    {
        internal int CreateLocatorCount { get; private set; }

        internal int DeleteLocatorCount { get; private set; }

        internal SourceId CreatedSourceId { get; private set; }

        internal ProtectedValuePurpose CreatedPurpose { get; private set; }

        internal ProtectedRecordOwner CreatedOwner { get; private set; }

        internal ProtectedLocatorReference IssuedReference { get; } =
            SourceDraftTestFixtures.CreateLocatorReference();

        internal SourceId DeletedSourceId { get; private set; }

        internal ProtectedValuePurpose DeletedPurpose { get; private set; }

        internal ProtectedRecordOwner DeletedOwner { get; private set; }

        internal ProtectedLocatorReference? DeletedReference { get; private set; }

        internal bool DeleteCancellationCanBeCanceled { get; private set; }

        internal SecretStoreOperationResult DeleteResult { get; init; } =
            SecretStoreOperationResult.Succeeded();

        public ValueTask<ProtectedLocatorReferenceCreationResult> CreateLocatorAsync(
            SourceId sourceId,
            ProtectedValuePurpose purpose,
            ProtectedRecordOwner owner,
            ReadOnlyMemory<byte> value,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.IsTrue(value.Length > 0);
            events?.Add("create");
            CreateLocatorCount++;
            CreatedSourceId = sourceId;
            CreatedPurpose = purpose;
            CreatedOwner = owner;
            return ValueTask.FromResult(
                ProtectedLocatorReferenceCreationResult.Succeeded(IssuedReference));
        }

        public ValueTask<SecretStoreOperationResult> DeleteLocatorAsync(
            SourceId sourceId,
            ProtectedValuePurpose purpose,
            ProtectedRecordOwner owner,
            ProtectedLocatorReference reference,
            CancellationToken cancellationToken = default)
        {
            events?.Add("delete");
            DeleteLocatorCount++;
            DeletedSourceId = sourceId;
            DeletedPurpose = purpose;
            DeletedOwner = owner;
            DeletedReference = reference;
            DeleteCancellationCanBeCanceled = cancellationToken.CanBeCanceled;
            return ValueTask.FromResult(DeleteResult);
        }

        public ValueTask<SecretReferenceCreationResult> CreateCredentialsAsync(
            SourceId sourceId,
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
            new("The onboarding service called an unexpected secret-store operation.");
    }
}
