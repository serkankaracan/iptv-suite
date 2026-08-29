using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace IptvSuite.UnitTests;

[TestClass]
public sealed class RemotePlaylistSourceOnboardingServiceTests
{
    private static readonly DateTimeOffset FixedInstant =
        new(2026, 8, 26, 12, 0, 0, TimeSpan.Zero);
    private static readonly string[] SuccessfulEvents = ["create", "import"];
    private static readonly string[] CleanupEvents = ["create", "import", "delete"];

    [TestMethod]
    public void ConstructorRequiresEveryDependency()
    {
        var store = new OnboardingSecretStore();
        var importer = new OnboardingImporter(RemotePlaylistCatalogImportResult.Committed(1, 0));
        var time = new FixedTimeProvider(FixedInstant);

        Assert.ThrowsExactly<ArgumentNullException>(() =>
            new RemotePlaylistSourceOnboardingService(null!, importer, time));
        Assert.ThrowsExactly<ArgumentNullException>(() =>
            new RemotePlaylistSourceOnboardingService(store, null!, time));
        Assert.ThrowsExactly<ArgumentNullException>(() =>
            new RemotePlaylistSourceOnboardingService(store, importer, null!));
    }

    [TestMethod]
    public void CatalogImportResultFactoriesEnforceCommitStateAndHideDetails()
    {
        DomainError error = DomainError.Create(DomainErrorCode.UnsupportedPlaylistFormat);
        RemotePlaylistCatalogImportResult committed =
            RemotePlaylistCatalogImportResult.Committed(12, 3, entryLimitReached: true);
        RemotePlaylistCatalogImportResult completeCatalog =
            RemotePlaylistCatalogImportResult.Committed(1, 0);
        RemotePlaylistCatalogImportResult notCommitted =
            RemotePlaylistCatalogImportResult.NotCommitted(error);
        RemotePlaylistCatalogImportResult indeterminate =
            RemotePlaylistCatalogImportResult.Indeterminate(error);

        Assert.AreEqual(CatalogImportCommitDisposition.Committed, committed.Disposition);
        Assert.AreEqual(12, committed.ImportedChannelCount);
        Assert.AreEqual(3, committed.WarningCount);
        Assert.IsTrue(committed.EntryLimitReached);
        Assert.IsFalse(completeCatalog.EntryLimitReached);
        Assert.IsNull(committed.Error);
        Assert.AreEqual(CatalogImportCommitDisposition.NotCommitted, notCommitted.Disposition);
        Assert.IsNull(notCommitted.ImportedChannelCount);
        Assert.IsNull(notCommitted.WarningCount);
        Assert.IsFalse(notCommitted.EntryLimitReached);
        Assert.AreSame(error, notCommitted.Error);
        Assert.AreEqual(CatalogImportCommitDisposition.Indeterminate, indeterminate.Disposition);
        Assert.IsNull(indeterminate.ImportedChannelCount);
        Assert.IsNull(indeterminate.WarningCount);
        Assert.IsFalse(indeterminate.EntryLimitReached);
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
    public async Task InvalidUserInfoIsRejectedBeforeStoreMutation()
    {
        string sensitive = SecurityTestAssertions.CreateSensitiveValue("ONBOARDING-USERINFO");
        var events = new List<string>();
        var store = new OnboardingSecretStore(events);
        var importer = new OnboardingImporter(
            RemotePlaylistCatalogImportResult.Committed(1, 0),
            events);
        RemotePlaylistSourceOnboardingService service = CreateService(store, importer);

        DomainResult<RemotePlaylistSourceOnboardingResult> result = await service.AddAsync(
            "Synthetic Source",
            $"https://user:{sensitive}@example.test/list.m3u");

        SecurityTestAssertions.IsFailure(result, DomainErrorCode.EndpointUserInfoNotAllowed);
        Assert.IsEmpty(events);
        SecurityTestAssertions.DoesNotContainSensitive(result.ToString(), sensitive);
    }

    [TestMethod]
    public async Task SuccessfulHttpsAddProtectsThenStreamsOneImportWithoutPreliminaryProbe()
    {
        string sensitive = SecurityTestAssertions.CreateSensitiveValue("ONBOARDING-LOCATOR");
        string locator = $"https://example.test/private/list.m3u?token={sensitive}";
        var events = new List<string>();
        var store = new OnboardingSecretStore(events);
        var importer = new OnboardingImporter(
            RemotePlaylistCatalogImportResult.Committed(42, 5),
            events);
        RemotePlaylistSourceOnboardingService service = CreateService(store, importer);

        DomainResult<RemotePlaylistSourceOnboardingResult> result = await service.AddAsync(
            "  Synthetic Source  ",
            locator);

        Assert.IsTrue(result.IsSuccess);
        Assert.IsFalse(result.Value!.SourceId.IsEmpty);
        Assert.AreEqual(42, result.Value.ImportedChannelCount);
        Assert.AreEqual(5, result.Value.WarningCount);
        Assert.IsFalse(result.Value.EntryLimitReached);
        Assert.AreEqual("[REMOTE-PLAYLIST-SOURCE-ONBOARDING-RESULT]", result.Value.ToString());
        CollectionAssert.AreEqual(SuccessfulEvents, events);
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
    public async Task HttpAddRequiresExplicitOptInAndPreservesTheSafeWorkflow()
    {
        string sensitive = SecurityTestAssertions.CreateSensitiveValue(
            "ONBOARDING-HTTP-LOCATOR");
        string locator = $"http://example.test/private/list.m3u?token={sensitive}";
        var events = new List<string>();
        var store = new OnboardingSecretStore(events);
        var importer = new OnboardingImporter(
            RemotePlaylistCatalogImportResult.Committed(
                8,
                1,
                entryLimitReached: true),
            events);
        RemotePlaylistSourceOnboardingService service = CreateService(store, importer);

        DomainResult<RemotePlaylistSourceOnboardingResult> rejected = await service.AddAsync(
            "Synthetic HTTP Source",
            locator);

        SecurityTestAssertions.IsFailure(
            rejected,
            DomainErrorCode.InsecureTransportRejected);
        Assert.IsEmpty(events);

        DomainResult<RemotePlaylistSourceOnboardingResult> accepted =
            await service.AddAllowingInsecureHttpAsync(
            "Synthetic HTTP Source",
            locator);

        Assert.IsTrue(accepted.IsSuccess);
        Assert.IsTrue(accepted.Value!.EntryLimitReached);
        CollectionAssert.AreEqual(SuccessfulEvents, events);
        Assert.AreEqual(1, store.CreateLocatorCount);
        Assert.AreEqual(1, importer.CallCount);
        Assert.IsNotNull(importer.Source);
        Assert.AreEqual(Uri.UriSchemeHttp, importer.Source.SafeEndpoint.Scheme);
        Assert.AreEqual(80, importer.Source.SafeEndpoint.Port);
        SecurityTestAssertions.DoesNotContainSensitive(
            string.Join('|', accepted, JsonSerializer.Serialize(accepted)),
            sensitive,
            locator);
    }

    [TestMethod]
    public async Task HttpNotCommittedImportSkipsProbeAndDeletesTheStagedLocator()
    {
        var events = new List<string>();
        var store = new OnboardingSecretStore(events);
        var importer = new OnboardingImporter(
            RemotePlaylistCatalogImportResult.NotCommitted(
                DomainError.Create(DomainErrorCode.UnsupportedPlaylistFormat)),
            events);
        RemotePlaylistSourceOnboardingService service = CreateService(store, importer);

        DomainResult<RemotePlaylistSourceOnboardingResult> result =
            await service.AddAllowingInsecureHttpAsync(
                "Synthetic HTTP Source",
                "http://example.test/get.php?username=synthetic&password=synthetic&type=m3u_plus&output=ts");

        SecurityTestAssertions.IsFailure(result, DomainErrorCode.UnsupportedPlaylistFormat);
        string[] expectedEvents = ["create", "import", "delete"];
        CollectionAssert.AreEqual(expectedEvents, events);
        Assert.AreEqual(1, importer.CallCount);
        Assert.AreEqual(1, store.CreateLocatorCount);
        Assert.AreEqual(1, store.DeleteLocatorCount);
        Assert.IsFalse(store.DeleteCancellationCanBeCanceled);
    }

    [TestMethod]
    public async Task NotCommittedImportDeletesExactProtectedRecordWithoutCallerCancellation()
    {
        var events = new List<string>();
        using CancellationTokenSource cancellation = new();
        var store = new OnboardingSecretStore(events);
        DomainError importError = DomainError.Create(DomainErrorCode.UnsupportedPlaylistFormat);
        var importer = new OnboardingImporter(
            RemotePlaylistCatalogImportResult.NotCommitted(importError),
            events)
        {
            CancellationToSignal = cancellation,
        };
        RemotePlaylistSourceOnboardingService service = CreateService(store, importer);

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
        RemotePlaylistSourceOnboardingService service = CreateService(store, importer);

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
        RemotePlaylistSourceOnboardingService service = CreateService(store, importer);

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
        RemotePlaylistSourceOnboardingService service = CreateService(store, importer);

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
        RemotePlaylistSourceOnboardingService service = CreateService(store, importer);

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
        RemotePlaylistSourceOnboardingService service = CreateService(store, importer);

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
        RemotePlaylistSourceOnboardingService service = CreateService(store, importer);

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
    public async Task ReplacementKeepsSourceIdentityAndRetiresThePreviousLocator()
    {
        ContentSource existing = CreateExistingSource();
        var store = new OnboardingSecretStore();
        var importer = new OnboardingImporter(
            RemotePlaylistCatalogImportResult.Committed(6, 1));
        RemotePlaylistSourceOnboardingService service = CreateService(store, importer);

        DomainResult<RemotePlaylistSourceOnboardingResult> result = await service.ReplaceAsync(
            existing,
            "Replacement source",
            "https://replacement.fixture.invalid/list.m3u");

        Assert.IsTrue(result.IsSuccess);
        Assert.AreEqual(existing.Id, result.Value!.SourceId);
        Assert.IsFalse(result.Value.PreviousConfigurationCleanupPending);
        Assert.AreEqual(existing.Id, importer.Source!.Id);
        Assert.AreNotEqual(
            existing.Configuration.ConfigurationId,
            importer.Source.Configuration.ConfigurationId);
        Assert.AreEqual(1, store.DeleteLocatorCount);
        Assert.AreEqual(existing.Id, store.DeletedSourceId);
        Assert.AreEqual(
            ProtectedRecordOwner.ForSourceConfiguration(
                existing.Configuration.ConfigurationId),
            store.DeletedOwner);
        Assert.AreEqual(
            ((RemotePlaylistSourceConfiguration)existing.Configuration).LocatorReference,
            store.DeletedReference);
    }

    [TestMethod]
    public async Task XtreamSourceCannotBeConvertedToRemotePlaylistByTheAsymmetricSink()
    {
        ValidatedSourceDraft draft = SourceDraftTestFixtures.CreateXtreamDraft(
            SourceId.Generate(),
            "Existing Xtream source",
            "https://existing.fixture.invalid");
        DomainResult<ContentSource> source = ContentSource.Create(
            draft,
            ContentSourceStatus.Testing,
            FixedInstant,
            FixedInstant);
        Assert.IsTrue(source.IsSuccess);
        var store = new OnboardingSecretStore();
        var importer = new OnboardingImporter(
            RemotePlaylistCatalogImportResult.Committed(1, 0));
        RemotePlaylistSourceOnboardingService service = CreateService(store, importer);

        DomainResult<RemotePlaylistSourceOnboardingResult> result = await service.ReplaceAsync(
            source.Value!,
            "Rejected conversion",
            "https://replacement.fixture.invalid/list.m3u");

        SecurityTestAssertions.IsFailure(result, DomainErrorCode.DomainInvariantViolation);
        Assert.AreEqual(0, store.CreateLocatorCount);
        Assert.AreEqual(0, importer.CallCount);
    }

    [TestMethod]
    public async Task CancelledNotCommittedImportDeletesTheStagedLocator()
    {
        var store = new OnboardingSecretStore();
        var importer = new OnboardingImporter(
            RemotePlaylistCatalogImportResult.NotCommitted(
                DomainError.Create(DomainErrorCode.OperationCancelled)));
        RemotePlaylistSourceOnboardingService service = CreateService(store, importer);

        DomainResult<RemotePlaylistSourceOnboardingResult> result = await service.AddAsync(
            "Synthetic source",
            "https://fixture.invalid/list.m3u");

        SecurityTestAssertions.IsFailure(result, DomainErrorCode.OperationCancelled);
        Assert.AreEqual(1, store.DeleteLocatorCount);
        Assert.AreEqual(store.CreatedOwner, store.DeletedOwner);
        Assert.AreSame(store.IssuedReference, store.DeletedReference);
    }

    [TestMethod]
    public async Task PreCancelledAddDoesNotReachAnyDependency()
    {
        var events = new List<string>();
        var store = new OnboardingSecretStore(events);
        var importer = new OnboardingImporter(
            RemotePlaylistCatalogImportResult.Committed(1, 0),
            events);
        RemotePlaylistSourceOnboardingService service = CreateService(store, importer);
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();

        await Assert.ThrowsExactlyAsync<OperationCanceledException>(async () =>
            await service.AddAsync(
                "Synthetic Source",
                "https://example.test/list.m3u",
                cancellation.Token));

        Assert.IsEmpty(events);
    }

    private static ContentSource CreateExistingSource()
    {
        ValidatedSourceDraft draft = SourceDraftTestFixtures.CreateRemoteDraft(
            SourceId.Generate(),
            "Existing source",
            "https://existing.fixture.invalid/list.m3u");
        DomainResult<ContentSource> source = ContentSource.Create(
            draft,
            ContentSourceStatus.Testing,
            FixedInstant,
            FixedInstant);
        return source.IsSuccess
            ? source.Value!
            : throw new InvalidOperationException("The synthetic source is invalid.");
    }

    private static RemotePlaylistSourceOnboardingService CreateService(
        OnboardingSecretStore store,
        OnboardingImporter importer) => new(
            store,
            importer,
            new FixedTimeProvider(FixedInstant));

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }

    private sealed class CatastrophicTestException : OutOfMemoryException
    {
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
