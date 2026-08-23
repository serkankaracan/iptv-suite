using System.Reflection;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using IptvSuite.Application;
using IptvSuite.Domain;
using IptvSuite.Infrastructure;
using Microsoft.Data.Sqlite;

namespace IptvSuite.IntegrationTests;

[TestClass]
[SupportedOSPlatform("windows")]
public sealed class SqlitePlaybackSourceResolverTests
{
    [TestMethod]
    [Timeout(30_000)]
    public async Task ActiveRemoteChannelResolvesExactOwnedHttpsLease()
    {
        using TemporaryDirectory temporary = TemporaryDirectory.Create("m11-playback-resolver");
        string databasePath = Path.Combine(temporary.FullPath, "catalog.db");
        await InitializeDatabaseAsync(databasePath);
        byte[] locator =
            "https://synthetic-user:synthetic-password@fixtures.invalid/live/channel.m3u8?opaque=canary-value"u8.ToArray();
        ResolverBatch batch = await CreateBatchAsync(locator, "active");
        await ActivateAsync(databasePath, batch.Batch);

        ResolvedSource resolved = await ResolveAsync(
            databasePath,
            new PlaybackSelection(batch.Source.Id, batch.ChannelId));

        Assert.AreEqual("None", resolved.Failure);
        Assert.IsNotNull(resolved.Lease);
        Assert.AreEqual("[PLAYBACK-SOURCE-RESOLUTION:SUCCESS]", resolved.RawResult.ToString());
        SecretLease lease = resolved.Lease;
        using (lease)
        {
            string json = JsonSerializer.Serialize(resolved.RawResult, resolved.RawResult.GetType());
            Assert.DoesNotContain("fixtures.invalid", json, StringComparison.Ordinal);
            Assert.DoesNotContain("canary-value", json, StringComparison.Ordinal);
            Assert.DoesNotContain("synthetic-password", json, StringComparison.Ordinal);
            CollectionAssert.AreEqual(locator, lease.Value.ToArray());
        }

        Assert.ThrowsExactly<ObjectDisposedException>(() => _ = lease.Value);
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task WrongAndRetiredBindingsAreUnavailable()
    {
        using TemporaryDirectory temporary = TemporaryDirectory.Create("m11-playback-binding");
        string databasePath = Path.Combine(temporary.FullPath, "catalog.db");
        await InitializeDatabaseAsync(databasePath);
        ResolverBatch initial = await CreateBatchAsync(
            "https://fixtures.invalid/live/initial.m3u8"u8.ToArray(),
            "initial");
        await ActivateAsync(databasePath, initial.Batch);

        await AssertFailureAsync(
            databasePath,
            new PlaybackSelection(SourceId.Generate(), initial.ChannelId),
            "Unavailable");
        await AssertFailureAsync(
            databasePath,
            new PlaybackSelection(initial.Source.Id, ChannelId.Generate()),
            "Unavailable");

        ResolverBatch replacement = await CreateBatchAsync(
            "https://fixtures.invalid/live/replacement.m3u8"u8.ToArray(),
            "replacement",
            initial.Source);
        await ActivateAsync(databasePath, replacement.Batch);

        await AssertFailureAsync(
            databasePath,
            new PlaybackSelection(initial.Source.Id, initial.ChannelId),
            "Unavailable");
        ResolvedSource current = await ResolveAsync(
            databasePath,
            new PlaybackSelection(replacement.Source.Id, replacement.ChannelId));
        Assert.AreEqual("None", current.Failure);
        SecretLease currentLease = current.Lease!;
        using (currentLease)
        {
            Assert.IsGreaterThan(0, currentLease.Value.Length);
        }
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task ProviderItemBranchIsExplicitlyUnsupportedWithoutProducingALease()
    {
        using TemporaryDirectory temporary = TemporaryDirectory.Create("m11-playback-provider");
        string databasePath = Path.Combine(temporary.FullPath, "catalog.db");
        await InitializeDatabaseAsync(databasePath);
        ResolverBatch batch = await CreateBatchAsync(null, "provider", providerItem: true);
        await ActivateAsync(databasePath, batch.Batch);

        await AssertFailureAsync(
            databasePath,
            new PlaybackSelection(batch.Source.Id, batch.ChannelId),
            "UnsupportedSource");

        await using SqliteConnection connection = await OpenAsync(databasePath);
        Assert.AreEqual(
            0L,
            Convert.ToInt64(
                await ScalarAsync(connection, "SELECT count(*) FROM protected_locators;"),
                System.Globalization.CultureInfo.InvariantCulture));
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task PartialProviderColumnsBesideAStreamReferenceFailClosed()
    {
        using TemporaryDirectory temporary = TemporaryDirectory.Create("m11-playback-contradiction");
        string databasePath = Path.Combine(temporary.FullPath, "catalog.db");
        await InitializeDatabaseAsync(databasePath);
        ResolverBatch batch = await CreateBatchAsync(
            "https://fixtures.invalid/live/contradiction.m3u8"u8.ToArray(),
            "contradiction");
        await ActivateAsync(databasePath, batch.Batch);
        PlaybackSelection selection = new(batch.Source.Id, batch.ChannelId);

        await ExecuteAsync(
            databasePath,
            "UPDATE channels SET provider_item_kind = 1 WHERE channel_id = $channel;",
            batch.ChannelId);
        await AssertFailureAsync(databasePath, selection, "StorageUnavailable");

        await ExecuteAsync(
            databasePath,
            """
            UPDATE channels
            SET provider_item_kind = NULL, provider_item_id = 'partial-id'
            WHERE channel_id = $channel;
            """,
            batch.ChannelId);
        await AssertFailureAsync(databasePath, selection, "StorageUnavailable");
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task TamperedAndMalformedLocatorsFailClosedWithoutObservablePlaintext()
    {
        byte[][] rejectedLocators =
        [
            [0xc3, 0x28],
            "http://fixtures.invalid/live/channel.m3u8"u8.ToArray(),
            "https://fixtures.invalid/live/channel.m3u8#fragment"u8.ToArray(),
            Encoding.UTF8.GetBytes(
                $"https://fixtures.invalid/{new string('a', SourceConfigurationValidator.MaxLocatorUnicodeScalars)}"),
        ];

        for (int index = 0; index < rejectedLocators.Length; index++)
        {
            using TemporaryDirectory temporary = TemporaryDirectory.Create($"m11-playback-invalid-{index}");
            string databasePath = Path.Combine(temporary.FullPath, "catalog.db");
            await InitializeDatabaseAsync(databasePath);
            ResolverBatch batch = await CreateBatchAsync(
                rejectedLocators[index],
                $"invalid-{index}");
            await ActivateAsync(databasePath, batch.Batch);

            ResolvedSource result = await ResolveAsync(
                databasePath,
                new PlaybackSelection(batch.Source.Id, batch.ChannelId));

            Assert.AreEqual("InvalidLocator", result.Failure, $"case {index}");
            Assert.IsNull(result.Lease, $"case {index}");
            string representation = result.RawResult.ToString()!;
            Assert.DoesNotContain("fixtures.invalid", representation, StringComparison.Ordinal);
            Assert.DoesNotContain("password", representation, StringComparison.Ordinal);
        }

        using TemporaryDirectory tampered = TemporaryDirectory.Create("m11-playback-tamper");
        string tamperedDatabase = Path.Combine(tampered.FullPath, "catalog.db");
        await InitializeDatabaseAsync(tamperedDatabase);
        ResolverBatch valid = await CreateBatchAsync(
            "https://fixtures.invalid/live/tampered.m3u8"u8.ToArray(),
            "tampered");
        await ActivateAsync(tamperedDatabase, valid.Batch);
        await using (SqliteConnection connection = await OpenAsync(tamperedDatabase))
        {
            await using SqliteCommand command = connection.CreateCommand();
            command.CommandText = """
                UPDATE protected_locators
                SET authentication_tag = zeroblob(16)
                WHERE purpose = $purpose;
                """;
            command.Parameters.AddWithValue(
                "$purpose",
                (int)ProtectedValuePurpose.ChannelStreamLocator);
            await command.ExecuteNonQueryAsync();
        }

        await AssertFailureAsync(
            tamperedDatabase,
            new PlaybackSelection(valid.Source.Id, valid.ChannelId),
            "InvalidLocator");
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task PreCancelledResolutionRemainsCancellationAndProducesNoLease()
    {
        using TemporaryDirectory temporary = TemporaryDirectory.Create("m11-playback-cancel");
        string databasePath = Path.Combine(temporary.FullPath, "catalog.db");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsExactlyAsync<OperationCanceledException>(async () =>
            await ResolveAsync(
                databasePath,
                new PlaybackSelection(SourceId.Generate(), ChannelId.Generate()),
                cancellation.Token));

        Assert.IsFalse(File.Exists(databasePath));
    }

    private static async Task AssertFailureAsync(
        string databasePath,
        PlaybackSelection selection,
        string expectedFailure)
    {
        ResolvedSource result = await ResolveAsync(databasePath, selection);
        try
        {
            Assert.AreEqual(expectedFailure, result.Failure);
            Assert.IsNull(result.Lease);
            Assert.AreEqual(
                $"[PLAYBACK-SOURCE-RESOLUTION:{expectedFailure}]",
                result.RawResult.ToString());
        }
        finally
        {
            result.Lease?.Dispose();
        }
    }

    private static async Task<ResolvedSource> ResolveAsync(
        string databasePath,
        PlaybackSelection selection,
        CancellationToken cancellationToken = default)
    {
        Assembly assembly = typeof(IptvSuite.Infrastructure.AssemblyMarker).Assembly;
        Type resolverType = assembly.GetType(
            "IptvSuite.Infrastructure.SqlitePlaybackSourceResolver",
            throwOnError: true)!;
        object resolver = Activator.CreateInstance(
            resolverType,
            BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null,
            [databasePath],
            culture: null)!;
        MethodInfo method = resolverType.GetMethod(
            "ResolveAsync",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        object valueTask = method.Invoke(
            resolver,
            [selection, cancellationToken])!;
        Task task = (Task)valueTask.GetType().GetMethod("AsTask")!.Invoke(valueTask, null)!;
        await task;
        object result = task.GetType().GetProperty("Result")!.GetValue(task)!;
        SecretLease? lease = (SecretLease?)result.GetType().GetProperty("Lease")!.GetValue(result);
        string failure = result.GetType().GetProperty("Failure")!.GetValue(result)!.ToString()!;
        return new ResolvedSource(result, lease, failure);
    }

    private static async Task<ResolverBatch> CreateBatchAsync(
        byte[]? locator,
        string suffix,
        ContentSource? existingSource = null,
        bool providerItem = false)
    {
        var store = new M4InMemorySecretStore();
        ContentSource source = existingSource ?? await CreateSourceAsync(store, suffix);
        SnapshotId snapshotId = SnapshotId.Generate();
        DomainResult<PlaylistSnapshot> snapshot = PlaylistSnapshot.Create(
            snapshotId,
            source.Id,
            new DateTimeOffset(2026, 8, 24, 0, 0, 0, TimeSpan.Zero),
            new string('A', 64),
            parserVersion: 1,
            normalizationVersion: 1,
            schemaVersion: 2,
            itemCount: 1,
            warningCount: 0,
            PlaylistSnapshotState.Complete);
        Assert.IsTrue(snapshot.IsSuccess);
        CategoryId categoryId = CategoryId.Generate();
        DomainResult<ChannelCategory> category = ChannelCategory.Create(
            categoryId,
            snapshotId,
            $"group-{suffix}",
            $"Group {suffix}",
            sortOrder: 0,
            isSynthetic: false);
        Assert.IsTrue(category.IsSuccess);
        ChannelId channelId = ChannelId.Generate();
        ProtectedLocatorReference? streamReference = null;
        ProviderItemKey? providerPlaybackKey = null;
        DomainResult<ChannelStableKey> stableKey;
        if (providerItem)
        {
            DomainResult<ProviderItemKey> providerKey = ProviderItemKey.Create($"stream-{suffix}");
            Assert.IsTrue(providerKey.IsSuccess);
            providerPlaybackKey = providerKey.Value;
            stableKey = ChannelStableKeyBuilder.FromProviderStreamId(
                source.Id,
                "xtream",
                $"stream-{suffix}");
        }
        else
        {
            Assert.IsNotNull(locator);
            ProtectedLocatorReferenceCreationResult created = await store.CreateLocatorAsync(
                source.Id,
                ProtectedValuePurpose.ChannelStreamLocator,
                ProtectedRecordOwner.ForChannel(channelId),
                locator);
            Assert.IsNotNull(created.Reference);
            streamReference = created.Reference;
            stableKey = ChannelStableKeyBuilder.FromM3uTvgId(source.Id, $"channel-{suffix}");
        }

        Assert.IsTrue(stableKey.IsSuccess);
        DomainResult<LiveChannel> channel = LiveChannel.Create(
            channelId,
            stableKey.Value,
            snapshotId,
            categoryId,
            providerKey: null,
            providerPlaybackKey,
            $"Channel {suffix}",
            number: 1,
            logoReference: null,
            streamReference,
            ChannelContainerHint.Hls,
            isAdultHint: false,
            ChannelNormalizationWarnings.None);
        Assert.IsTrue(channel.IsSuccess);

        Assembly assembly = typeof(IptvSuite.Infrastructure.AssemblyMarker).Assembly;
        Type locatorType = assembly.GetType(
            "IptvSuite.Infrastructure.CatalogLocatorPlaintext",
            throwOnError: true)!;
        Array locators = Array.CreateInstance(locatorType, providerItem ? 0 : 1);
        if (!providerItem)
        {
            object streamLocator = Activator.CreateInstance(
                locatorType,
                [
                    channelId,
                    ProtectedValuePurpose.ChannelStreamLocator,
                    streamReference!,
                    new ReadOnlyMemory<byte>(locator!),
                ])!;
            locators.SetValue(streamLocator, 0);
        }

        Type batchType = assembly.GetType(
            "IptvSuite.Infrastructure.CatalogSnapshotBatch",
            throwOnError: true)!;
        object batch = Activator.CreateInstance(
            batchType,
            [source, snapshot.Value!, new[] { category.Value! }, new[] { channel.Value! }, locators])!;
        return new(batch, source, channelId);
    }

    private static async Task<ContentSource> CreateSourceAsync(
        M4InMemorySecretStore store,
        string suffix)
    {
        DomainResult<ValidatedSourceDraft> draft = await new SourceDraftProtectionService(store)
            .ProtectRemotePlaylistAsync(
                SourceId.Generate(),
                $"Synthetic {suffix}",
                $"https://fixtures.invalid/catalog/{suffix}.m3u");
        Assert.IsTrue(draft.IsSuccess);
        DateTimeOffset now = new(2026, 8, 24, 0, 0, 0, TimeSpan.Zero);
        DomainResult<ContentSource> source = ContentSource.Create(
            draft.Value,
            ContentSourceStatus.Testing,
            now,
            now);
        Assert.IsTrue(source.IsSuccess);
        return source.Value!;
    }

    private static async Task InitializeDatabaseAsync(string databasePath)
    {
        object database = CreateInfrastructureInstance(
            "IptvSuite.Infrastructure.SqliteCatalogDatabase",
            databasePath);
        await InvokeValueTaskAsync(
            database.GetType().GetMethod(
                "InitializeAsync",
                BindingFlags.Instance | BindingFlags.NonPublic)!,
            database,
            [CancellationToken.None]);
    }

    private static async Task ActivateAsync(string databasePath, object batch)
    {
        Assembly assembly = typeof(IptvSuite.Infrastructure.AssemblyMarker).Assembly;
        object writer = CreateInfrastructureInstance(
            "IptvSuite.Infrastructure.SqliteCatalogSnapshotWriter",
            databasePath);
        Type faultType = assembly.GetType(
            "IptvSuite.Infrastructure.CatalogActivationFaultPoint",
            throwOnError: true)!;
        await InvokeValueTaskAsync(
            writer.GetType().GetMethod(
                "ActivateAsync",
                BindingFlags.Instance | BindingFlags.NonPublic)!,
            writer,
            [batch, Enum.ToObject(faultType, 0), CancellationToken.None]);
    }

    private static object CreateInfrastructureInstance(
        string typeName,
        string databasePath)
    {
        Type type = typeof(IptvSuite.Infrastructure.AssemblyMarker).Assembly.GetType(
            typeName,
            throwOnError: true)!;
        return Activator.CreateInstance(
            type,
            BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null,
            [databasePath],
            culture: null)!;
    }

    private static async Task InvokeValueTaskAsync(
        MethodInfo method,
        object instance,
        object?[] arguments)
    {
        object valueTask = method.Invoke(instance, arguments)!;
        await (Task)valueTask.GetType().GetMethod("AsTask")!.Invoke(valueTask, null)!;
    }

    private static async Task<SqliteConnection> OpenAsync(string databasePath)
    {
        var connection = new SqliteConnection($"Data Source={databasePath};Pooling=False");
        await connection.OpenAsync();
        return connection;
    }

    private static async Task<object?> ScalarAsync(
        SqliteConnection connection,
        string sql)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        return await command.ExecuteScalarAsync();
    }

    private static async Task ExecuteAsync(
        string databasePath,
        string sql,
        ChannelId channelId)
    {
        await using SqliteConnection connection = await OpenAsync(databasePath);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue(
            "$channel",
            channelId.Value.ToString("N"));
        await command.ExecuteNonQueryAsync();
    }

    private sealed record ResolverBatch(
        object Batch,
        ContentSource Source,
        ChannelId ChannelId);

    private sealed record ResolvedSource(
        object RawResult,
        SecretLease? Lease,
        string Failure);
}
