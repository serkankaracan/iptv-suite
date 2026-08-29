using IptvSuite.Application;
using IptvSuite.Domain;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Security.Cryptography;
using System.Text;

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

    [TestMethod]
    public async Task ExplicitM3uBootstrapUsesXtreamApiSourceAndProtectsExtractedCredentials()
    {
        var store = new OnboardingSecretStore
        {
            ExpectedServerLocator = "https://fixture.invalid",
            ExpectedUsername = "synthetic+user",
            ExpectedPassword = "synthetic password",
        };
        var importer = new OnboardingImporter(XtreamCatalogImportResult.Committed(
            new ContentCatalogCounts(7, 5, 3, 0)));
        var service = new XtreamSourceOnboardingService(
            store,
            importer,
            new FixedTimeProvider(FixedInstant));

        DomainResult<XtreamSourceOnboardingResult> result =
            await service.AddFromM3uUrlAsync(
                "Synthetic bootstrap source",
                "https://fixture.invalid/get.php?username=synthetic%2Buser&password=synthetic%20password&type=m3u_plus&output=ts");

        Assert.IsTrue(result.IsSuccess);
        Assert.AreEqual(new ContentCatalogCounts(7, 5, 3, 0), result.Value!.Counts);
        Assert.IsNotNull(importer.Source);
        Assert.AreEqual(SourceKind.XtreamCompatible, importer.Source.Kind);
        Assert.AreEqual("https", importer.Source.SafeEndpoint.Scheme);
        Assert.AreEqual("fixture.invalid", importer.Source.SafeEndpoint.Host);
        Assert.IsTrue(store.CredentialPayloadMatched);
    }

    [TestMethod]
    public async Task ExplicitM3uBootstrapRejectsAmbiguousCredentialsBeforeProtectionOrImport()
    {
        var store = new OnboardingSecretStore();
        var importer = new OnboardingImporter(XtreamCatalogImportResult.Committed(
            new ContentCatalogCounts(1, 1, 1, 0)));
        var service = new XtreamSourceOnboardingService(
            store,
            importer,
            new FixedTimeProvider(FixedInstant));

        DomainResult<XtreamSourceOnboardingResult> result =
            await service.AddFromM3uUrlAsync(
                "Synthetic bootstrap source",
                "https://fixture.invalid/get.php?username=first&username=second&password=synthetic");

        Assert.IsFalse(result.IsSuccess);
        Assert.AreEqual(DomainErrorCode.EndpointMalformed, result.Error!.Code);
        Assert.AreEqual(0, store.CreateCount);
        Assert.IsNull(importer.Source);
    }

    [TestMethod]
    [DataRow("https://fixture.invalid/path/get.php?username=user&password=password")]
    [DataRow("https://user:password@fixture.invalid/get.php?username=user&password=password")]
    [DataRow("https://fixture.invalid/get.php?username=user&password=password#fragment")]
    [DataRow("https://fixture.invalid/get.php?username=user%ZZ&password=password")]
    [DataRow("https://fixture.invalid/GET.PHP?username=user&password=password")]
    [DataRow("https://fixture.invalid/a/../get.php?username=user&password=password")]
    [DataRow("https://fixture.invalid/get.php??username=user&password=password")]
    public async Task ExplicitM3uBootstrapRejectsNonExactOrMalformedLocator(string locator)
    {
        var store = new OnboardingSecretStore();
        var importer = new OnboardingImporter(XtreamCatalogImportResult.Committed(
            new ContentCatalogCounts(1, 1, 1, 0)));
        var service = new XtreamSourceOnboardingService(
            store,
            importer,
            new FixedTimeProvider(FixedInstant));

        DomainResult<XtreamSourceOnboardingResult> result =
            await service.AddFromM3uUrlAsync("Synthetic bootstrap source", locator);

        Assert.IsFalse(result.IsSuccess);
        Assert.AreEqual(0, store.CreateCount);
        Assert.IsNull(importer.Source);
    }

    [TestMethod]
    public async Task RemotePlaylistCanConvertToXtreamUnderTheSameSourceIdentity()
    {
        ContentSource existing = CreateExistingRemoteSource();
        var store = new OnboardingSecretStore();
        var importer = new OnboardingImporter(XtreamCatalogImportResult.Committed(
            new ContentCatalogCounts(4, 2, 1, 0)));
        var service = new XtreamSourceOnboardingService(
            store,
            importer,
            new FixedTimeProvider(FixedInstant));

        DomainResult<XtreamSourceOnboardingResult> result =
            await service.ReplaceFromM3uUrlAsync(
                existing,
                "Converted source",
                "https://fixture.invalid/get.php?username=synthetic-user&password=synthetic-password&type=m3u_plus");

        Assert.IsTrue(result.IsSuccess);
        Assert.AreEqual(existing.Id, result.Value!.SourceId);
        Assert.AreEqual(existing.Id, importer.Source!.Id);
        Assert.AreEqual(SourceKind.XtreamCompatible, importer.Source.Kind);
        Assert.AreEqual(1, store.DeleteCount);
        Assert.AreEqual(existing.Id, store.DeletedSourceId);
        Assert.AreEqual(
            ((RemotePlaylistSourceConfiguration)existing.Configuration).LocatorReference,
            store.DeletedLocatorReference);
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

    private static ContentSource CreateExistingRemoteSource()
    {
        ValidatedSourceDraft draft = SourceDraftTestFixtures.CreateRemoteDraft(
            SourceId.Generate(),
            "Existing Remote M3U source",
            "https://existing.fixture.invalid/catalog.m3u");
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

        internal int CreateCount { get; private set; }

        internal string? ExpectedServerLocator { get; init; }

        internal string? ExpectedUsername { get; init; }

        internal string? ExpectedPassword { get; init; }

        internal bool CredentialPayloadMatched { get; private set; }

        internal ProtectedRecordOwner CreatedOwner { get; private set; }

        internal int DeleteCount { get; private set; }

        internal SourceId DeletedSourceId { get; private set; }

        internal ProtectedRecordOwner DeletedOwner { get; private set; }

        internal SecretReference? DeletedReference { get; private set; }

        internal ProtectedLocatorReference? DeletedLocatorReference { get; private set; }

        internal SecretStoreOperationResult DeleteResult { get; init; } =
            SecretStoreOperationResult.Succeeded();

        public ValueTask<SecretReferenceCreationResult> CreateCredentialsAsync(
            SourceId sourceId,
            ProtectedRecordOwner owner,
            ReadOnlyMemory<byte> value,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CreateCount++;
            CreatedSourceId = sourceId;
            CreatedOwner = owner;
            if (ExpectedServerLocator is not null &&
                ExpectedUsername is not null &&
                ExpectedPassword is not null)
            {
                byte[] expectedSuffix = Encoding.UTF8.GetBytes(
                    ExpectedServerLocator + ExpectedUsername + ExpectedPassword);
                try
                {
                    CredentialPayloadMatched =
                        value.Length >= expectedSuffix.Length &&
                        value.Span[^expectedSuffix.Length..].SequenceEqual(expectedSuffix);
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(expectedSuffix);
                }
            }

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
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.AreEqual(ProtectedValuePurpose.RemotePlaylistLocator, purpose);
            DeleteCount++;
            DeletedSourceId = sourceId;
            DeletedOwner = owner;
            DeletedLocatorReference = reference;
            return ValueTask.FromResult(DeleteResult);
        }
    }

    private static InvalidOperationException Unused() =>
        new("The synthetic test does not permit this operation.");
}
