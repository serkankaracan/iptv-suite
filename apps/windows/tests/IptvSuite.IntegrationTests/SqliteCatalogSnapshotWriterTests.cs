using System.Text;
using System.Runtime.Versioning;
using System.Reflection;
using IptvSuite.Application;
using IptvSuite.Domain;
using IptvSuite.Infrastructure;
using Microsoft.Data.Sqlite;

namespace IptvSuite.IntegrationTests;

[TestClass]
[SupportedOSPlatform("windows")]
public sealed class SqliteCatalogSnapshotWriterTests
{
    private static readonly string[] ExpectedSingleChannelPage = ["Channel one"];

    [TestMethod]
    public async Task LogoProviderDecryptsExactActiveChannelLocatorAndAcceptsOnlyBoundedImageSignatures()
    {
        using TemporaryDirectory temporary = TemporaryDirectory.Create("m9-logo-provider");
        string databasePath = Path.Combine(temporary.FullPath, "catalog.db");
        await InitializeDatabaseAsync(databasePath);
        TestBatch test = await CreateBatchAsync(itemSuffix: "logo");
        await ActivateAsync(databasePath, test.Batch);
        byte[] png = CreatePngHeader(32, 24);
        var transport = new LogoTransport(png);
        var provider = new SqliteChannelLogoProvider(databasePath, transport);

        ChannelLogoImage? image = await provider.LoadAsync(test.Source.Id, test.ChannelId);

        Assert.IsNotNull(image);
        Assert.AreEqual(ChannelLogoFormat.Png, image.Format);
        Assert.AreEqual(32, image.PixelWidth);
        Assert.AreEqual(24, image.PixelHeight);
        CollectionAssert.AreEqual(png, image.Content.ToArray());
        Assert.AreEqual(SqliteChannelLogoProvider.MaximumLogoBytes, transport.MaximumBytes);
        Assert.AreEqual("https", transport.Scheme);
        Assert.IsNull(await provider.LoadAsync(test.Source.Id, ChannelId.Generate()));

        var rejectedProvider = new SqliteChannelLogoProvider(databasePath, new LogoTransport("not-image"u8.ToArray()));
        Assert.IsNull(await rejectedProvider.LoadAsync(test.Source.Id, test.ChannelId));

        TestBatch crossOrigin = await CreateBatchAsync(test.Source, "cross-origin", "https://images.invalid/logo.png");
        await ActivateAsync(databasePath, crossOrigin.Batch);
        var blockedTransport = new LogoTransport(png);
        var blockedProvider = new SqliteChannelLogoProvider(databasePath, blockedTransport);
        Assert.IsNull(await blockedProvider.LoadAsync(test.Source.Id, crossOrigin.ChannelId));
        Assert.AreEqual(0, blockedTransport.Calls);
    }

    [TestMethod]
    public void LogoMetadataValidationRequiresExactMediaTypeAndBoundedDimensions()
    {
        byte[] png = CreatePngHeader(2048, 2048);
        byte[] jpeg = CreateJpegHeader(640, 360);
        byte[] webP = CreateWebPHeader(320, 180);

        AssertLogoMetadata(png, HttpResponseMediaType.Png, ChannelLogoFormat.Png, 2048, 2048);
        AssertLogoMetadata(jpeg, HttpResponseMediaType.Jpeg, ChannelLogoFormat.Jpeg, 640, 360);
        AssertLogoMetadata(webP, HttpResponseMediaType.WebP, ChannelLogoFormat.WebP, 320, 180);

        Assert.IsFalse(ValidateLogo(png, HttpResponseMediaType.Jpeg).Accepted);
        Assert.IsFalse(ValidateLogo(png, HttpResponseMediaType.Unspecified).Accepted);
        Assert.IsFalse(ValidateLogo(png, HttpResponseMediaType.Other).Accepted);
        Assert.IsFalse(ValidateLogo(CreatePngHeader(4097, 1), HttpResponseMediaType.Png).Accepted);
        Assert.IsFalse(ValidateLogo(CreatePngHeader(3000, 2000), HttpResponseMediaType.Png).Accepted);
        Assert.IsFalse(ValidateLogo(
            CreatePngHeader(int.MaxValue, int.MaxValue),
            HttpResponseMediaType.Png,
            int.MaxValue).Accepted);
        Assert.IsFalse(ValidateLogo(png[..24], HttpResponseMediaType.Png).Accepted);
        Assert.IsFalse(ValidateLogo(jpeg[..8], HttpResponseMediaType.Jpeg).Accepted);
        Assert.IsFalse(ValidateLogo(webP[..24], HttpResponseMediaType.WebP).Accepted);
    }

    [TestMethod]
    public async Task CatalogBrowserRejectsUnboundedOrControlBearingRequestsBeforeDatabaseAccess()
    {
        using TemporaryDirectory temporary = TemporaryDirectory.Create("m9-query-bounds");
        var browser = new SqliteCatalogQuery(Path.Combine(temporary.FullPath, "catalog.db"));
        SourceId sourceId = SourceId.Generate();

        await Assert.ThrowsExactlyAsync<ArgumentOutOfRangeException>(async () =>
            await browser.ReadChannelsAsync(sourceId, null, null, 0, 201));
        await Assert.ThrowsExactlyAsync<ArgumentOutOfRangeException>(async () =>
            await browser.ReadChannelsAsync(sourceId, null, new string('a', 101), 0, 50));
        await Assert.ThrowsExactlyAsync<ArgumentOutOfRangeException>(async () =>
            await browser.ReadChannelsAsync(sourceId, null, "bad\nsearch", 0, 50));
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task CompleteSnapshotActivatesAtomicallyWithEncryptedLocatorRows()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using TemporaryDirectory temporary = TemporaryDirectory.Create("m8-sqlite-activate");
        string databasePath = Path.Combine(temporary.FullPath, "catalog.db");
        await InitializeDatabaseAsync(databasePath);
        TestBatch test = await CreateBatchAsync(itemSuffix: "one");

        await ActivateAsync(databasePath, test.Batch);
        await SetFavoriteAsync(databasePath, test.Source.Id, test.StableKey, true);
        await SetFavoriteAsync(databasePath, test.Source.Id, test.StableKey, true);
        Assert.IsTrue(await IsFavoriteAsync(databasePath, test.Source.Id, test.StableKey));

        string[] firstPage = await ReadChannelNamesAsync(databasePath, test.Source.Id, 0, 200);
        string[] reopenedPage = await ReadChannelNamesAsync(databasePath, test.Source.Id, 0, 200);
        CollectionAssert.AreEqual(ExpectedSingleChannelPage, firstPage);
        CollectionAssert.AreEqual(firstPage, reopenedPage);

        var browser = new SqliteCatalogQuery(databasePath);
        IReadOnlyList<CatalogSourceItem> sources = await browser.ReadSourcesAsync();
        Assert.HasCount(1, sources);
        Assert.AreEqual(test.Source.Id, sources[0].SourceId);
        Assert.AreEqual(test.Source.DisplayName, sources[0].Name);
        IReadOnlyList<CatalogCategoryItem> categories = await browser.ReadCategoriesAsync(test.Source.Id);
        Assert.HasCount(1, categories);
        Assert.AreEqual(test.CategoryId, categories[0].CategoryId);
        Assert.AreEqual("Group one", categories[0].Name);
        CatalogChannelPage matchingPage = await browser.ReadChannelsAsync(
            test.Source.Id,
            test.CategoryId,
            "  channel ONE  ",
            0,
            50);
        Assert.AreEqual(0, matchingPage.Offset);
        Assert.AreEqual(1, matchingPage.TotalCount);
        Assert.HasCount(1, matchingPage.Items);
        Assert.AreEqual(test.ChannelId, matchingPage.Items[0].ChannelId);
        Assert.IsTrue(matchingPage.Items[0].HasLogo);
        CatalogChannelPage emptyPage = await browser.ReadChannelsAsync(
            test.Source.Id,
            test.CategoryId,
            "missing",
            0,
            50);
        Assert.AreEqual(0, emptyPage.TotalCount);
        Assert.IsEmpty(emptyPage.Items);
        CatalogChannelPage beyondEnd = await browser.ReadChannelsAsync(
            test.Source.Id,
            null,
            null,
            200,
            50);
        Assert.AreEqual(1, beyondEnd.TotalCount);
        Assert.IsEmpty(beyondEnd.Items);

        (SecretLease? lease, string failure) = await ReadLocatorAsync(
            databasePath,
            test.Source.Id,
            test.ChannelId,
            ProtectedValuePurpose.ChannelStreamLocator,
            test.StreamReference);
        Assert.AreEqual("None", failure);
        Assert.IsNotNull(lease);
        using (lease)
        {
            CollectionAssert.AreEqual(test.StreamPlaintext, lease.Value.ToArray());
        }

        await using SqliteConnection connection = await OpenAsync(databasePath);
        Assert.AreEqual(Id(test.Snapshot.Id.Value), await ScalarStringAsync(
            connection,
            "SELECT active_snapshot_id FROM sources WHERE source_id = $source;",
            ("$source", Id(test.Source.Id.Value))));
        Assert.AreEqual(1L, await ScalarInt64Async(connection, "SELECT count(*) FROM channels;"));
        Assert.AreEqual(2L, await ScalarInt64Async(connection, "SELECT count(*) FROM protected_locators;"));
        Assert.AreEqual(1L, await ScalarInt64Async(connection, "SELECT count(*) FROM snapshot_keys WHERE wrapped_dek IS NOT NULL;"));
        byte[] ciphertext = await ScalarBytesAsync(connection, "SELECT ciphertext FROM protected_locators ORDER BY purpose LIMIT 1;");
        CollectionAssert.AreNotEqual(test.StreamPlaintext, ciphertext);
        Assert.AreEqual(test.StreamPlaintext.Length, ciphertext.Length);
        string schema = await ScalarStringAsync(connection, "SELECT lower(group_concat(sql, ' ')) FROM sqlite_master WHERE sql IS NOT NULL;");
        Assert.DoesNotContain(Encoding.UTF8.GetString(test.StreamPlaintext), schema, StringComparison.Ordinal);

        await ExecuteAsync(connection, "UPDATE protected_locators SET authentication_tag = zeroblob(16) WHERE purpose = $purpose;",
            ("$purpose", (int)ProtectedValuePurpose.ChannelStreamLocator));
        (SecretLease? tamperedLease, string tamperedFailure) = await ReadLocatorAsync(
            databasePath,
            test.Source.Id,
            test.ChannelId,
            ProtectedValuePurpose.ChannelStreamLocator,
            test.StreamReference);
        Assert.IsNull(tamperedLease);
        Assert.AreEqual("AuthenticationFailed", tamperedFailure);
    }

    [TestMethod]
    [Timeout(90_000)]
    public async Task LocatorAeadBindingRejectsTamperAndContextReplayWithSanitizedFailure()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using TemporaryDirectory temporary = TemporaryDirectory.Create("m16-r27-aead-binding");
        foreach (AeadBindingMutation mutation in Enum.GetValues<AeadBindingMutation>())
        {
            string databasePath = Path.Combine(temporary.FullPath, $"catalog-{(int)mutation}.db");
            await InitializeDatabaseAsync(databasePath);
            TestBatch test = await CreateBatchAsync(itemSuffix: $"binding-{(int)mutation}");
            await ActivateAsync(databasePath, test.Batch);

            LocatorReadTuple readTuple = new(
                test.Source.Id,
                test.ChannelId,
                ProtectedValuePurpose.ChannelStreamLocator,
                test.StreamReference);
            (SecretLease? baselineLease, string baselineFailure) = await ReadLocatorAsync(
                databasePath,
                readTuple.SourceId,
                readTuple.ChannelId,
                readTuple.Purpose,
                readTuple.Reference);
            Assert.AreEqual("None", baselineFailure, $"Baseline failed for {mutation}.");
            Assert.IsNotNull(baselineLease, $"Baseline lease missing for {mutation}.");
            using (baselineLease)
            {
                CollectionAssert.AreEqual(test.StreamPlaintext, baselineLease.Value.ToArray());
            }

            readTuple = await ApplyAeadBindingMutationAsync(
                databasePath,
                test,
                mutation,
                readTuple);
            (SecretLease? rejectedLease, string rejectedFailure) = await ReadLocatorAsync(
                databasePath,
                readTuple.SourceId,
                readTuple.ChannelId,
                readTuple.Purpose,
                readTuple.Reference);

            Assert.IsNull(rejectedLease, $"Tampered locator returned plaintext for {mutation}.");
            Assert.AreEqual("AuthenticationFailed", rejectedFailure, $"Unexpected failure for {mutation}.");
            Assert.DoesNotContain(
                Encoding.UTF8.GetString(test.StreamPlaintext),
                rejectedFailure,
                StringComparison.Ordinal);
            Assert.DoesNotContain(Id(readTuple.SourceId.Value), rejectedFailure, StringComparison.Ordinal);
            Assert.DoesNotContain(Id(readTuple.ChannelId.Value), rejectedFailure, StringComparison.Ordinal);
            Assert.DoesNotContain(readTuple.Reference.ToString(), rejectedFailure, StringComparison.Ordinal);
        }
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task FaultBeforePointerSwitchRollsBackNewSnapshotAndPreservesActiveSnapshot()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using TemporaryDirectory temporary = TemporaryDirectory.Create("m8-sqlite-rollback");
        string databasePath = Path.Combine(temporary.FullPath, "catalog.db");
        await InitializeDatabaseAsync(databasePath);
        TestBatch initial = await CreateBatchAsync(itemSuffix: "initial");
        await ActivateAsync(databasePath, initial.Batch);
        TestBatch replacement = await CreateBatchAsync(initial.Source, itemSuffix: "replacement");

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(async () =>
            await ActivateAsync(databasePath, replacement.Batch, injectFault: true));

        await using SqliteConnection connection = await OpenAsync(databasePath);
        Assert.AreEqual(Id(initial.Snapshot.Id.Value), await ScalarStringAsync(
            connection,
            "SELECT active_snapshot_id FROM sources WHERE source_id = $source;",
            ("$source", Id(initial.Source.Id.Value))));
        Assert.AreEqual(1L, await ScalarInt64Async(connection, "SELECT count(*) FROM snapshots;"));
        Assert.AreEqual(0L, await ScalarInt64Async(
            connection,
            "SELECT count(*) FROM snapshots WHERE snapshot_id = $snapshot;",
            ("$snapshot", Id(replacement.Snapshot.Id.Value))));

        await connection.CloseAsync();
        await ActivateAsync(databasePath, replacement.Batch);
        await using SqliteConnection reopened = await OpenAsync(databasePath);
        Assert.AreEqual(Id(replacement.Snapshot.Id.Value), await ScalarStringAsync(
            reopened,
            "SELECT active_snapshot_id FROM sources WHERE source_id = $source;",
            ("$source", Id(initial.Source.Id.Value))));
        Assert.AreEqual(1L, await ScalarInt64Async(
            reopened,
            "SELECT count(*) FROM snapshot_keys WHERE key_state = 2 AND wrapped_dek IS NULL;"));
        Assert.AreEqual(1L, await ScalarInt64Async(
            reopened,
            "SELECT count(*) FROM snapshot_keys WHERE key_state = 1 AND wrapped_dek IS NOT NULL;"));
        await reopened.CloseAsync();
        await PruneSnapshotsAsync(databasePath, initial.Source.Id);
        await using SqliteConnection pruned = await OpenAsync(databasePath);
        Assert.AreEqual(1L, await ScalarInt64Async(pruned, "SELECT count(*) FROM snapshots;"));
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task PersistedDeletionPendingSourceRejectsStaleActivationAndPreservesCatalog()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using TemporaryDirectory temporary = TemporaryDirectory.Create("m8-sqlite-delete-activation-guard");
        string databasePath = Path.Combine(temporary.FullPath, "catalog.db");
        await InitializeDatabaseAsync(databasePath);
        using var store = new M4InMemorySecretStore();
        TestBatch pending = await CreateBatchAsync(
            itemSuffix: "pending-original",
            sourceStore: store);
        TestBatch sibling = await CreateBatchAsync(itemSuffix: "sibling");
        await ActivateAsync(databasePath, pending.Batch);
        await ActivateAsync(databasePath, sibling.Batch);
        ISourceDeletionLifecycle lifecycle = CreateDeletionLifecycle(databasePath, store);
        SourceDeletionLifecycleOperationResult marked =
            await lifecycle.MarkPendingAsync(pending.Source.Id);
        Assert.IsTrue(marked.IsSuccess);

        TestBatch staleReplacement = await CreateBatchAsync(pending.Source, itemSuffix: "stale-replacement");

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(async () =>
            await ActivateAsync(databasePath, staleReplacement.Batch));

        await using SqliteConnection reopened = await OpenAsync(databasePath);
        Assert.AreEqual((long)ContentSourceStatus.DeletionPending, await ScalarInt64Async(
            reopened,
            "SELECT status FROM sources WHERE source_id = $source;",
            ("$source", Id(pending.Source.Id.Value))));
        Assert.AreEqual(Id(pending.Snapshot.Id.Value), await ScalarStringAsync(
            reopened,
            "SELECT active_snapshot_id FROM sources WHERE source_id = $source;",
            ("$source", Id(pending.Source.Id.Value))));
        Assert.AreEqual((long)ContentSourceStatus.Ready, await ScalarInt64Async(
            reopened,
            "SELECT status FROM sources WHERE source_id = $source;",
            ("$source", Id(sibling.Source.Id.Value))));
        Assert.AreEqual(Id(sibling.Snapshot.Id.Value), await ScalarStringAsync(
            reopened,
            "SELECT active_snapshot_id FROM sources WHERE source_id = $source;",
            ("$source", Id(sibling.Source.Id.Value))));
        Assert.AreEqual(1L, await ScalarInt64Async(
            reopened,
            "SELECT count(*) FROM snapshots WHERE source_id = $source;",
            ("$source", Id(pending.Source.Id.Value))));
        Assert.AreEqual(1L, await ScalarInt64Async(
            reopened,
            "SELECT count(*) FROM channels WHERE snapshot_id = $snapshot;",
            ("$snapshot", Id(pending.Snapshot.Id.Value))));
        Assert.AreEqual(2L, await ScalarInt64Async(
            reopened,
            "SELECT count(*) FROM protected_locators WHERE snapshot_id = $snapshot;",
            ("$snapshot", Id(pending.Snapshot.Id.Value))));
        Assert.AreEqual(0L, await ScalarInt64Async(
            reopened,
            "SELECT count(*) FROM snapshots WHERE snapshot_id = $snapshot;",
            ("$snapshot", Id(staleReplacement.Snapshot.Id.Value))));
        Assert.AreEqual(2L, await ScalarInt64Async(reopened, "SELECT count(*) FROM sources;"));
        Assert.AreEqual(2L, await ScalarInt64Async(reopened, "SELECT count(*) FROM channels;"));
        Assert.AreEqual(4L, await ScalarInt64Async(reopened, "SELECT count(*) FROM protected_locators;"));
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task SameSourceReplacementCommitsConfigurationAndSnapshotAtomicallyAndJournalsCleanup()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using TemporaryDirectory temporary = TemporaryDirectory.Create(
            "m17-source-replacement-atomic");
        string databasePath = Path.Combine(temporary.FullPath, "catalog.db");
        await InitializeDatabaseAsync(databasePath);
        using var store = new M4InMemorySecretStore();
        ContentSource initialSource = await CreateSourceAsync(store);
        TestBatch initial = await CreateBatchAsync(initialSource, "initial");
        await ActivateAsync(databasePath, initial.Batch);
        ContentSource replacementSource = await CreateReplacementSourceAsync(
            store,
            initialSource.Id);
        TestBatch replacement = await CreateBatchAsync(replacementSource, "replacement");

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(async () =>
            await ActivateAsync(databasePath, replacement.Batch, injectFault: true));

        await using (SqliteConnection unchanged = await OpenAsync(databasePath))
        {
            Assert.AreEqual(
                Id(initialSource.Configuration.ConfigurationId.Value),
                await ScalarStringAsync(
                    unchanged,
                    "SELECT configuration_id FROM sources WHERE source_id = $source;",
                    ("$source", Id(initialSource.Id.Value))));
            Assert.AreEqual(
                Id(initial.Snapshot.Id.Value),
                await ScalarStringAsync(
                    unchanged,
                    "SELECT active_snapshot_id FROM sources WHERE source_id = $source;",
                    ("$source", Id(initialSource.Id.Value))));
            Assert.AreEqual(0L, await ScalarInt64Async(
                unchanged,
                "SELECT count(*) FROM source_configuration_retirements;"));
        }

        await ActivateAsync(databasePath, replacement.Batch);

        await using (SqliteConnection committed = await OpenAsync(databasePath))
        {
            Assert.AreEqual(1L, await ScalarInt64Async(committed, "SELECT count(*) FROM sources;"));
            Assert.AreEqual(
                Id(replacementSource.Configuration.ConfigurationId.Value),
                await ScalarStringAsync(
                    committed,
                    "SELECT configuration_id FROM sources WHERE source_id = $source;",
                    ("$source", Id(initialSource.Id.Value))));
            Assert.AreEqual(
                Id(replacement.Snapshot.Id.Value),
                await ScalarStringAsync(
                    committed,
                    "SELECT active_snapshot_id FROM sources WHERE source_id = $source;",
                    ("$source", Id(initialSource.Id.Value))));
            Assert.AreEqual(1L, await ScalarInt64Async(
                committed,
                """
                SELECT count(*)
                FROM source_configuration_retirements
                WHERE source_id = $source AND configuration_id = $configuration;
                """,
                ("$source", Id(initialSource.Id.Value)),
                ("$configuration", Id(initialSource.Configuration.ConfigurationId.Value))));
        }

        var reconciler = new SqliteSourceConfigurationRetirementReconciler(
            databasePath,
            store);
        SourceConfigurationRetirementReconciliationResult reconciliation =
            await reconciler.ReconcileAsync();
        Assert.IsTrue(reconciliation.IsSuccess);
        Assert.AreEqual(1, reconciliation.AttemptedCount);
        Assert.AreEqual(1, reconciliation.CompletedCount);
        Assert.IsTrue(store.RetiredBuffersAreZeroed);
        await using SqliteConnection reconciled = await OpenAsync(databasePath);
        Assert.AreEqual(0L, await ScalarInt64Async(
            reconciled,
            "SELECT count(*) FROM source_configuration_retirements;"));
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task PreCancelledActivationDoesNotMutateDatabase()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using TemporaryDirectory temporary = TemporaryDirectory.Create("m8-sqlite-cancel");
        string databasePath = Path.Combine(temporary.FullPath, "catalog.db");
        await InitializeDatabaseAsync(databasePath);
        TestBatch test = await CreateBatchAsync(itemSuffix: "cancel");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsExactlyAsync<OperationCanceledException>(async () =>
            await ActivateAsync(databasePath, test.Batch, cancellationToken: cancellation.Token));

        await using SqliteConnection connection = await OpenAsync(databasePath);
        Assert.AreEqual(0L, await ScalarInt64Async(connection, "SELECT count(*) FROM sources;"));
        Assert.AreEqual(0L, await ScalarInt64Async(connection, "SELECT count(*) FROM snapshots;"));
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task StartupReconciliationRemovesOnlyInactiveImportingSnapshots()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using TemporaryDirectory temporary = TemporaryDirectory.Create("m8-sqlite-reconcile");
        string databasePath = Path.Combine(temporary.FullPath, "catalog.db");
        await InitializeDatabaseAsync(databasePath);
        TestBatch active = await CreateBatchAsync(itemSuffix: "active");
        await ActivateAsync(databasePath, active.Batch);
        Guid abandonedSnapshot = Guid.NewGuid();
        Guid abandonedGeneration = Guid.NewGuid();
        await using (SqliteConnection connection = await OpenAsync(databasePath))
        {
            await ExecuteAsync(
                connection,
                """
                INSERT INTO snapshots(snapshot_id, source_id, retrieved_utc, content_hash,
                    parser_version, normalization_version, schema_version, item_count,
                    warning_count, state)
                VALUES ($snapshot, $source, $retrieved, zeroblob(32), 1, 1, 2, 0, 0, 0);
                INSERT INTO snapshot_keys(snapshot_id, key_generation_id, wrapped_dek, key_state)
                VALUES ($snapshot, $generation, randomblob(64), 0);
                """,
                ("$snapshot", Id(abandonedSnapshot)),
                ("$source", Id(active.Source.Id.Value)),
                ("$retrieved", new DateTimeOffset(2026, 8, 20, 15, 0, 0, TimeSpan.Zero).ToString("O")),
                ("$generation", Id(abandonedGeneration)));
        }

        await ReconcileAsync(databasePath);

        await using SqliteConnection reopened = await OpenAsync(databasePath);
        Assert.AreEqual(1L, await ScalarInt64Async(reopened, "SELECT count(*) FROM snapshots;"));
        Assert.AreEqual(0L, await ScalarInt64Async(
            reopened,
            "SELECT count(*) FROM snapshots WHERE snapshot_id = $snapshot;",
            ("$snapshot", Id(abandonedSnapshot))));
        Assert.AreEqual(Id(active.Snapshot.Id.Value), await ScalarStringAsync(
            reopened,
            "SELECT active_snapshot_id FROM sources WHERE source_id = $source;",
            ("$source", Id(active.Source.Id.Value))));
        Assert.AreEqual(1L, await ScalarInt64Async(reopened, "SELECT count(*) FROM snapshot_keys;"));
    }

    private static async Task<TestBatch> CreateBatchAsync(
        ContentSource? existingSource = null,
        string itemSuffix = "item",
        string? logoUrl = null,
        M4InMemorySecretStore? sourceStore = null)
    {
        M4InMemorySecretStore store = sourceStore ?? new M4InMemorySecretStore();
        ContentSource source = existingSource ?? await CreateSourceAsync(store);
        SnapshotId snapshotId = SnapshotId.Generate();
        DomainResult<PlaylistSnapshot> snapshot = PlaylistSnapshot.Create(
            snapshotId,
            source.Id,
            new DateTimeOffset(2026, 8, 20, 12, 0, 0, TimeSpan.Zero),
            new string('A', 64),
            parserVersion: 1,
            normalizationVersion: 1,
            schemaVersion: 1,
            itemCount: 1,
            warningCount: 0,
            PlaylistSnapshotState.Complete);
        Assert.IsTrue(snapshot.IsSuccess);
        CategoryId categoryId = CategoryId.Generate();
        DomainResult<ChannelCategory> category = ChannelCategory.Create(
            categoryId,
            snapshotId,
            $"group-{itemSuffix}",
            $"Group {itemSuffix}",
            0,
            false);
        Assert.IsTrue(category.IsSuccess);
        ChannelId channelId = ChannelId.Generate();
        DomainResult<ChannelStableKey> stableKey = ChannelStableKeyBuilder.FromM3uTvgId(
            source.Id,
            $"channel-{itemSuffix}");
        Assert.IsTrue(stableKey.IsSuccess);
        byte[] stream = Encoding.UTF8.GetBytes($"https://fixtures.invalid/live/{itemSuffix}.m3u8");
        byte[] logo = Encoding.UTF8.GetBytes(logoUrl ?? $"https://fixtures.invalid/logo/{itemSuffix}.png");
        ProtectedRecordOwner owner = ProtectedRecordOwner.ForChannel(channelId);
        ProtectedLocatorReferenceCreationResult streamCreated = await store.CreateLocatorAsync(
            source.Id,
            ProtectedValuePurpose.ChannelStreamLocator,
            owner,
            stream);
        ProtectedLocatorReferenceCreationResult logoCreated = await store.CreateLocatorAsync(
            source.Id,
            ProtectedValuePurpose.ChannelLogoLocator,
            owner,
            logo);
        Assert.IsNotNull(streamCreated.Reference);
        Assert.IsNotNull(logoCreated.Reference);
        DomainResult<LiveChannel> channel = LiveChannel.Create(
            channelId,
            stableKey.Value,
            snapshotId,
            categoryId,
            providerKey: null,
            providerPlaybackKey: null,
            $"Channel {itemSuffix}",
            number: 1,
            logoCreated.Reference,
            streamCreated.Reference,
            ChannelContainerHint.Hls,
            isAdultHint: false,
            ChannelNormalizationWarnings.None);
        Assert.IsTrue(channel.IsSuccess);
        Type assemblyType = typeof(IptvSuite.Infrastructure.AssemblyMarker);
        Assembly assembly = assemblyType.Assembly;
        Type locatorType = assembly.GetType("IptvSuite.Infrastructure.CatalogLocatorPlaintext", true)!;
        object streamLocator = Activator.CreateInstance(
            locatorType,
            [channelId, ProtectedValuePurpose.ChannelStreamLocator, streamCreated.Reference!, new ReadOnlyMemory<byte>(stream)])!;
        object logoLocator = Activator.CreateInstance(
            locatorType,
            [channelId, ProtectedValuePurpose.ChannelLogoLocator, logoCreated.Reference!, new ReadOnlyMemory<byte>(logo)])!;
        Array locators = Array.CreateInstance(locatorType, 2);
        locators.SetValue(streamLocator, 0);
        locators.SetValue(logoLocator, 1);
        Type batchType = assembly.GetType("IptvSuite.Infrastructure.CatalogSnapshotBatch", true)!;
        object batch = Activator.CreateInstance(
            batchType,
            [
                source,
                snapshot.Value!,
                new[] { category.Value! },
                new[] { channel.Value! },
                locators,
                null,
            ])!;
        return new(
            batch,
            source,
            snapshot.Value!,
            categoryId,
            channelId,
            stableKey.Value.Value,
            streamCreated.Reference!,
            stream,
            logo);
    }

    private static async Task<LocatorReadTuple> ApplyAeadBindingMutationAsync(
        string databasePath,
        TestBatch test,
        AeadBindingMutation mutation,
        LocatorReadTuple readTuple)
    {
        await using SqliteConnection connection = await OpenAsync(databasePath);
        await ExecuteAsync(connection, "PRAGMA foreign_keys = OFF;");
        switch (mutation)
        {
            case AeadBindingMutation.CiphertextTamper:
                await FlipLocatorBlobAsync(connection, "ciphertext");
                break;
            case AeadBindingMutation.AuthenticationTagTamper:
                await FlipLocatorBlobAsync(connection, "authentication_tag");
                break;
            case AeadBindingMutation.NonceTamper:
                await FlipLocatorBlobAsync(connection, "nonce");
                break;
            case AeadBindingMutation.SourceContextReplay:
                SourceId replaySource = SourceId.Generate();
                await ExecuteAsync(
                    connection,
                    "UPDATE sources SET source_id = $newSource WHERE source_id = $source;",
                    ("$newSource", Id(replaySource.Value)),
                    ("$source", Id(test.Source.Id.Value)));
                readTuple = readTuple with { SourceId = replaySource };
                break;
            case AeadBindingMutation.SnapshotContextReplay:
                SnapshotId replaySnapshot = SnapshotId.Generate();
                await ExecuteAsync(
                    connection,
                    "UPDATE sources SET active_snapshot_id = $newSnapshot WHERE source_id = $source;",
                    ("$newSnapshot", Id(replaySnapshot.Value)),
                    ("$source", Id(test.Source.Id.Value)));
                await ExecuteAsync(
                    connection,
                    "UPDATE snapshot_keys SET snapshot_id = $newSnapshot WHERE snapshot_id = $snapshot;",
                    ("$newSnapshot", Id(replaySnapshot.Value)),
                    ("$snapshot", Id(test.Snapshot.Id.Value)));
                await ExecuteAsync(
                    connection,
                    "UPDATE protected_locators SET snapshot_id = $newSnapshot WHERE purpose = $purpose;",
                    ("$newSnapshot", Id(replaySnapshot.Value)),
                    ("$purpose", (int)ProtectedValuePurpose.ChannelStreamLocator));
                break;
            case AeadBindingMutation.GenerationContextReplay:
                Guid replayGeneration = Guid.NewGuid();
                string currentGeneration = await ScalarStringAsync(
                    connection,
                    "SELECT key_generation_id FROM snapshot_keys WHERE snapshot_id = $snapshot;",
                    ("$snapshot", Id(test.Snapshot.Id.Value)));
                await ExecuteAsync(
                    connection,
                    "UPDATE snapshot_keys SET key_generation_id = $newGeneration WHERE key_generation_id = $generation;",
                    ("$newGeneration", Id(replayGeneration)),
                    ("$generation", currentGeneration));
                await ExecuteAsync(
                    connection,
                    "UPDATE protected_locators SET key_generation_id = $newGeneration WHERE purpose = $purpose;",
                    ("$newGeneration", Id(replayGeneration)),
                    ("$purpose", (int)ProtectedValuePurpose.ChannelStreamLocator));
                break;
            case AeadBindingMutation.ChannelContextReplay:
                ChannelId replayChannel = ChannelId.Generate();
                await ExecuteAsync(
                    connection,
                    "UPDATE protected_locators SET owner_id = $newOwner WHERE purpose = $purpose;",
                    ("$newOwner", Id(replayChannel.Value)),
                    ("$purpose", (int)ProtectedValuePurpose.ChannelStreamLocator));
                readTuple = readTuple with { ChannelId = replayChannel };
                break;
            case AeadBindingMutation.PurposeContextReplay:
                await ExecuteAsync(
                    connection,
                    "DELETE FROM protected_locators WHERE purpose = $purpose;",
                    ("$purpose", (int)ProtectedValuePurpose.ChannelLogoLocator));
                await ExecuteAsync(
                    connection,
                    "UPDATE protected_locators SET purpose = $newPurpose WHERE purpose = $purpose;",
                    ("$newPurpose", (int)ProtectedValuePurpose.ChannelLogoLocator),
                    ("$purpose", (int)ProtectedValuePurpose.ChannelStreamLocator));
                readTuple = readTuple with { Purpose = ProtectedValuePurpose.ChannelLogoLocator };
                break;
            case AeadBindingMutation.ReferenceContextReplay:
                string replayReferenceText = $"locator-ref-v1:{Guid.NewGuid():N}";
                DomainResult<ProtectedLocatorReference> parsed =
                    ProtectedLocatorReference.Parse(replayReferenceText);
                Assert.IsTrue(parsed.IsSuccess);
                await ExecuteAsync(
                    connection,
                    "UPDATE protected_locators SET locator_reference = $newReference WHERE purpose = $purpose;",
                    ("$newReference", replayReferenceText),
                    ("$purpose", (int)ProtectedValuePurpose.ChannelStreamLocator));
                readTuple = readTuple with { Reference = parsed.Value! };
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(mutation), mutation, "Unknown AEAD binding mutation.");
        }

        return readTuple;
    }

    private static async Task FlipLocatorBlobAsync(SqliteConnection connection, string columnName)
    {
        string validatedColumn = columnName switch
        {
            "ciphertext" => "ciphertext",
            "authentication_tag" => "authentication_tag",
            "nonce" => "nonce",
            _ => throw new ArgumentOutOfRangeException(nameof(columnName)),
        };
        byte[] value = (byte[])(await ScalarAsync(
            connection,
            $"SELECT {validatedColumn} FROM protected_locators WHERE purpose = $purpose;",
            ("$purpose", (int)ProtectedValuePurpose.ChannelStreamLocator)))!;
        try
        {
            value[0] ^= 0x80;
            await ExecuteAsync(
                connection,
                $"UPDATE protected_locators SET {validatedColumn} = $value WHERE purpose = $purpose;",
                ("$value", value),
                ("$purpose", (int)ProtectedValuePurpose.ChannelStreamLocator));
        }
        finally
        {
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(value);
        }
    }

    private static async Task<ContentSource> CreateSourceAsync(M4InMemorySecretStore store)
    {
        DomainResult<ValidatedSourceDraft> draft = await new SourceDraftProtectionService(store)
            .ProtectRemotePlaylistAsync(
                SourceId.Generate(),
                "Synthetic catalog",
                "https://fixtures.invalid/catalog/list.m3u");
        Assert.IsTrue(draft.IsSuccess);
        DateTimeOffset now = new(2026, 8, 20, 11, 0, 0, TimeSpan.Zero);
        DomainResult<ContentSource> source = ContentSource.Create(
            draft.Value,
            ContentSourceStatus.Testing,
            now,
            now);
        Assert.IsTrue(source.IsSuccess);
        return source.Value!;
    }

    private static async Task<ContentSource> CreateReplacementSourceAsync(
        M4InMemorySecretStore store,
        SourceId sourceId)
    {
        DomainResult<ValidatedSourceDraft> draft = await new SourceDraftProtectionService(store)
            .ProtectRemotePlaylistAsync(
                sourceId,
                "Replacement catalog",
                "https://replacement.invalid/catalog/list.m3u");
        Assert.IsTrue(draft.IsSuccess);
        DateTimeOffset now = new(2026, 8, 20, 12, 0, 0, TimeSpan.Zero);
        DomainResult<ContentSource> source = ContentSource.Create(
            draft.Value,
            ContentSourceStatus.Testing,
            now,
            now);
        Assert.IsTrue(source.IsSuccess);
        return source.Value!;
    }

    private static ISourceDeletionLifecycle CreateDeletionLifecycle(
        string databasePath,
        ISecretStore store)
    {
        Type type = typeof(IptvSuite.Infrastructure.AssemblyMarker).Assembly.GetType(
            "IptvSuite.Infrastructure.SqliteSourceDeletionLifecycle",
            throwOnError: true)!;
        return (ISourceDeletionLifecycle)Activator.CreateInstance(
            type,
            BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null,
            args: [databasePath, store],
            culture: null)!;
    }

    private static async Task<SqliteConnection> OpenAsync(string databasePath)
    {
        var connection = new SqliteConnection($"Data Source={databasePath};Pooling=False");
        await connection.OpenAsync();
        return connection;
    }

    private static async Task<long> ScalarInt64Async(
        SqliteConnection connection,
        string sql,
        params (string Name, object Value)[] parameters) => Convert.ToInt64(
            await ScalarAsync(connection, sql, parameters),
            System.Globalization.CultureInfo.InvariantCulture);

    private static async Task<string> ScalarStringAsync(
        SqliteConnection connection,
        string sql,
        params (string Name, object Value)[] parameters) => Convert.ToString(
            await ScalarAsync(connection, sql, parameters),
            System.Globalization.CultureInfo.InvariantCulture)!;

    private static async Task<byte[]> ScalarBytesAsync(SqliteConnection connection, string sql) =>
        (byte[])(await ScalarAsync(connection, sql))!;

    private static async Task<object?> ScalarAsync(
        SqliteConnection connection,
        string sql,
        params (string Name, object Value)[] parameters)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        foreach ((string name, object value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        return await command.ExecuteScalarAsync();
    }

    private static async Task ExecuteAsync(
        SqliteConnection connection,
        string sql,
        params (string Name, object Value)[] parameters)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        foreach ((string name, object value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        await command.ExecuteNonQueryAsync();
    }

    private static string Id(Guid value) => value.ToString("N", System.Globalization.CultureInfo.InvariantCulture);

    private static async Task InitializeDatabaseAsync(string databasePath)
    {
        Type type = typeof(IptvSuite.Infrastructure.AssemblyMarker).Assembly.GetType(
            "IptvSuite.Infrastructure.SqliteCatalogDatabase", true)!;
        object database = Activator.CreateInstance(
            type,
            BindingFlags.Instance | BindingFlags.NonPublic,
            null,
            [databasePath],
            null)!;
        await InvokeValueTaskAsync(
            type.GetMethod("InitializeAsync", BindingFlags.Instance | BindingFlags.NonPublic)!,
            database,
            [CancellationToken.None]);
    }

    private static async Task ActivateAsync(
        string databasePath,
        object batch,
        bool injectFault = false,
        CancellationToken cancellationToken = default)
    {
        Assembly assembly = typeof(IptvSuite.Infrastructure.AssemblyMarker).Assembly;
        Type writerType = assembly.GetType("IptvSuite.Infrastructure.SqliteCatalogSnapshotWriter", true)!;
        object writer = Activator.CreateInstance(
            writerType,
            BindingFlags.Instance | BindingFlags.NonPublic,
            null,
            [databasePath],
            null)!;
        Type faultType = assembly.GetType("IptvSuite.Infrastructure.CatalogActivationFaultPoint", true)!;
        object fault = Enum.ToObject(faultType, injectFault ? 1 : 0);
        await InvokeValueTaskAsync(
            writerType.GetMethod("ActivateAsync", BindingFlags.Instance | BindingFlags.NonPublic)!,
            writer,
            [batch, fault, cancellationToken]);
    }

    private static async Task InvokeValueTaskAsync(MethodInfo method, object instance, object?[] arguments)
    {
        object valueTask = method.Invoke(instance, arguments)!;
        await (Task)valueTask.GetType().GetMethod("AsTask")!.Invoke(valueTask, null)!;
    }

    private static async Task<(SecretLease? Lease, string Failure)> ReadLocatorAsync(
        string databasePath,
        SourceId sourceId,
        ChannelId channelId,
        ProtectedValuePurpose purpose,
        ProtectedLocatorReference reference)
    {
        Assembly assembly = typeof(IptvSuite.Infrastructure.AssemblyMarker).Assembly;
        Type readerType = assembly.GetType("IptvSuite.Infrastructure.SqliteCatalogLocatorReader", true)!;
        object reader = Activator.CreateInstance(
            readerType,
            BindingFlags.Instance | BindingFlags.NonPublic,
            null,
            [databasePath],
            null)!;
        MethodInfo method = readerType.GetMethod("ReadAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        object valueTask = method.Invoke(reader, [sourceId, channelId, purpose, reference, CancellationToken.None])!;
        Task task = (Task)valueTask.GetType().GetMethod("AsTask")!.Invoke(valueTask, null)!;
        await task;
        object result = task.GetType().GetProperty("Result")!.GetValue(task)!;
        var lease = (SecretLease?)result.GetType().GetProperty("Lease")!.GetValue(result);
        string failure = result.GetType().GetProperty("Failure")!.GetValue(result)!.ToString()!;
        return (lease, failure);
    }

    private static async Task PruneSnapshotsAsync(string databasePath, SourceId sourceId)
    {
        object deletion = CreateInfrastructureInstance(
            "IptvSuite.Infrastructure.SqliteCatalogSourceDeletion",
            databasePath);
        await InvokeValueTaskAsync(
            deletion.GetType().GetMethod("PruneRetiredSnapshotsAsync", BindingFlags.Instance | BindingFlags.NonPublic)!,
            deletion,
            [sourceId, CancellationToken.None]);
    }

    private static async Task ReconcileAsync(string databasePath)
    {
        object deletion = CreateInfrastructureInstance(
            "IptvSuite.Infrastructure.SqliteCatalogSourceDeletion",
            databasePath);
        await InvokeValueTaskAsync(
            deletion.GetType().GetMethod("ReconcileAsync", BindingFlags.Instance | BindingFlags.NonPublic)!,
            deletion,
            [CancellationToken.None]);
    }

    private static async Task<string[]> ReadChannelNamesAsync(
        string databasePath,
        SourceId sourceId,
        int offset,
        int limit)
    {
        Assembly assembly = typeof(IptvSuite.Infrastructure.AssemblyMarker).Assembly;
        Type queryType = assembly.GetType("IptvSuite.Infrastructure.SqliteCatalogQuery", true)!;
        object query = Activator.CreateInstance(
            queryType,
            BindingFlags.Instance | BindingFlags.Public,
            null,
            [databasePath],
            null)!;
        MethodInfo method = queryType.GetMethod("ReadChannelPageAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        object valueTask = method.Invoke(query, [sourceId, offset, limit, CancellationToken.None])!;
        Task task = (Task)valueTask.GetType().GetMethod("AsTask")!.Invoke(valueTask, null)!;
        await task;
        var rows = (System.Collections.IEnumerable)task.GetType().GetProperty("Result")!.GetValue(task)!;
        return rows.Cast<object>()
            .Select(row => (string)row.GetType().GetProperty("Name")!.GetValue(row)!)
            .ToArray();
    }

    private static async Task SetFavoriteAsync(
        string databasePath,
        SourceId sourceId,
        string stableKey,
        bool value)
    {
        object favorites = CreateInfrastructureInstance("IptvSuite.Infrastructure.SqliteCatalogFavorites", databasePath);
        MethodInfo method = favorites.GetType().GetMethod("SetAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        await InvokeValueTaskAsync(method, favorites, [
            sourceId,
            ChannelStableKeyBuilder.AlgorithmVersion,
            stableKey,
            value,
            new DateTimeOffset(2026, 8, 20, 14, 0, 0, TimeSpan.Zero),
            CancellationToken.None,
        ]);
    }

    private static async Task<bool> IsFavoriteAsync(string databasePath, SourceId sourceId, string stableKey)
    {
        object favorites = CreateInfrastructureInstance("IptvSuite.Infrastructure.SqliteCatalogFavorites", databasePath);
        MethodInfo method = favorites.GetType().GetMethod("IsFavoriteAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        object valueTask = method.Invoke(favorites, [
            sourceId,
            ChannelStableKeyBuilder.AlgorithmVersion,
            stableKey,
            CancellationToken.None,
        ])!;
        Task task = (Task)valueTask.GetType().GetMethod("AsTask")!.Invoke(valueTask, null)!;
        await task;
        return (bool)task.GetType().GetProperty("Result")!.GetValue(task)!;
    }

    private static object CreateInfrastructureInstance(string typeName, string databasePath)
    {
        Type type = typeof(IptvSuite.Infrastructure.AssemblyMarker).Assembly.GetType(typeName, true)!;
        return Activator.CreateInstance(
            type,
            BindingFlags.Instance | BindingFlags.NonPublic,
            null,
            [databasePath],
            null)!;
    }

    private static void AssertLogoMetadata(
        byte[] content,
        HttpResponseMediaType mediaType,
        ChannelLogoFormat expectedFormat,
        int expectedWidth,
        int expectedHeight)
    {
        (bool accepted, ChannelLogoFormat format, int width, int height) =
            ValidateLogo(content, mediaType);
        Assert.IsTrue(accepted);
        Assert.AreEqual(expectedFormat, format);
        Assert.AreEqual(expectedWidth, width);
        Assert.AreEqual(expectedHeight, height);
    }

    private static (bool Accepted, ChannelLogoFormat Format, int Width, int Height) ValidateLogo(
        byte[] content,
        HttpResponseMediaType mediaType,
        int maximumDimension = SqliteChannelLogoProvider.MaximumLogoDimension)
    {
        Type validator = typeof(IptvSuite.Infrastructure.AssemblyMarker).Assembly.GetType(
            "IptvSuite.Infrastructure.ChannelLogoMetadataValidator",
            throwOnError: true)!;
        MethodInfo method = validator.GetMethod(
            "TryValidate",
            BindingFlags.Static | BindingFlags.NonPublic)!;
        object?[] arguments =
        [
            new ReadOnlyMemory<byte>(content),
            mediaType,
            maximumDimension,
            SqliteChannelLogoProvider.MaximumLogoPixels,
            ChannelLogoFormat.Png,
            0,
            0,
        ];
        bool accepted = (bool)method.Invoke(null, arguments)!;
        return (accepted, (ChannelLogoFormat)arguments[4]!, (int)arguments[5]!, (int)arguments[6]!);
    }

    private static byte[] CreatePngHeader(int width, int height)
    {
        byte[] value = new byte[33];
        byte[] signature = [0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a];
        signature.CopyTo(value, 0);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(value.AsSpan(8, 4), 13);
        "IHDR"u8.CopyTo(value.AsSpan(12, 4));
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(value.AsSpan(16, 4), (uint)width);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(value.AsSpan(20, 4), (uint)height);
        value[24] = 8;
        value[25] = 6;
        return value;
    }

    private static byte[] CreateJpegHeader(int width, int height) =>
    [
        0xff, 0xd8,
        0xff, 0xc0, 0x00, 0x11, 0x08,
        (byte)(height >> 8), (byte)height,
        (byte)(width >> 8), (byte)width,
        0x03,
        0x01, 0x11, 0x00,
        0x02, 0x11, 0x00,
        0x03, 0x11, 0x00,
        0xff, 0xd9,
    ];

    private static byte[] CreateWebPHeader(int width, int height)
    {
        byte[] value = new byte[30];
        "RIFF"u8.CopyTo(value.AsSpan(0, 4));
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(value.AsSpan(4, 4), 22);
        "WEBP"u8.CopyTo(value.AsSpan(8, 4));
        "VP8X"u8.CopyTo(value.AsSpan(12, 4));
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(value.AsSpan(16, 4), 10);
        WriteUInt24LittleEndian(value.AsSpan(24, 3), width - 1);
        WriteUInt24LittleEndian(value.AsSpan(27, 3), height - 1);
        return value;
    }

    private static void WriteUInt24LittleEndian(Span<byte> target, int value)
    {
        target[0] = (byte)value;
        target[1] = (byte)(value >> 8);
        target[2] = (byte)(value >> 16);
    }

    private sealed record TestBatch(
        object Batch,
        ContentSource Source,
        PlaylistSnapshot Snapshot,
        CategoryId CategoryId,
        ChannelId ChannelId,
        string StableKey,
        ProtectedLocatorReference StreamReference,
        byte[] StreamPlaintext,
        byte[] LogoPlaintext);

    private sealed record LocatorReadTuple(
        SourceId SourceId,
        ChannelId ChannelId,
        ProtectedValuePurpose Purpose,
        ProtectedLocatorReference Reference);

    private enum AeadBindingMutation
    {
        CiphertextTamper,
        AuthenticationTagTamper,
        NonceTamper,
        SourceContextReplay,
        SnapshotContextReplay,
        GenerationContextReplay,
        ChannelContextReplay,
        PurposeContextReplay,
        ReferenceContextReplay,
    }

    private sealed class LogoTransport(
        byte[] content,
        HttpResponseMediaType mediaType = HttpResponseMediaType.Png) : IHttpTransport
    {
        internal int MaximumBytes { get; private set; }
        internal string? Scheme { get; private set; }
        internal int Calls { get; private set; }

        public ValueTask<HttpTransportResult> GetAsync(HttpTransportRequest request, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;
            MaximumBytes = request.MaximumResponseBytes;
            Scheme = "https";
            return ValueTask.FromResult(HttpTransportResult.Success(
                200,
                HttpResponseLease.CopyFrom(content, mediaType)));
        }
    }
}
