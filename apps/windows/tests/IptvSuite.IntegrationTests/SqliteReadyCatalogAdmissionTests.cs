using System.Reflection;
using System.Runtime.Versioning;
using IptvSuite.Application;
using IptvSuite.Domain;
using IptvSuite.Infrastructure;
using Microsoft.Data.Sqlite;

namespace IptvSuite.IntegrationTests;

[TestClass]
[SupportedOSPlatform("windows")]
public sealed class SqliteReadyCatalogAdmissionTests
{
    [TestMethod]
    public async Task CatalogBrowseReturnsOnlyReadySourceGraphs()
    {
        using TemporaryDirectory temporary = TemporaryDirectory.Create("m12-ready-catalog-query");
        string databasePath = Path.Combine(temporary.FullPath, "catalog.db");
        await InitializeDatabaseAsync(databasePath);
        CatalogGraph ready = CatalogGraph.Create("Ready source", ContentSourceStatus.Ready);
        CatalogGraph pending = CatalogGraph.Create("Pending source", ContentSourceStatus.DeletionPending);
        await InsertGraphAsync(databasePath, ready);
        await InsertGraphAsync(databasePath, pending);
        var query = new SqliteCatalogQuery(databasePath);

        IReadOnlyList<CatalogSourceItem> sources = await query.ReadSourcesAsync();
        IReadOnlyList<CatalogCategoryItem> readyCategories = await query.ReadCategoriesAsync(ready.SourceId);
        CatalogChannelPage readyChannels = await query.ReadChannelsAsync(
            ready.SourceId,
            ready.CategoryId,
            null,
            0,
            50);
        IReadOnlyList<CatalogCategoryItem> pendingCategories = await query.ReadCategoriesAsync(pending.SourceId);
        CatalogChannelPage pendingChannels = await query.ReadChannelsAsync(
            pending.SourceId,
            pending.CategoryId,
            null,
            0,
            50);

        Assert.AreEqual(1, sources.Count);
        Assert.AreEqual(ready.SourceId, sources[0].SourceId);
        Assert.AreEqual(1, readyCategories.Count);
        Assert.AreEqual(ready.CategoryId, readyCategories[0].CategoryId);
        Assert.AreEqual(1, readyChannels.TotalCount);
        Assert.AreEqual(ready.ChannelId, readyChannels.Items[0].ChannelId);
        Assert.AreEqual(0, pendingCategories.Count);
        Assert.AreEqual(0, pendingChannels.TotalCount);
        Assert.AreEqual(0, pendingChannels.Items.Count);
    }

    [TestMethod]
    public async Task LogoBindingRejectsADeletionPendingSourceBeforeTransport()
    {
        using TemporaryDirectory temporary = TemporaryDirectory.Create("m12-ready-logo-binding");
        string databasePath = Path.Combine(temporary.FullPath, "catalog.db");
        await InitializeDatabaseAsync(databasePath);
        CatalogGraph ready = CatalogGraph.Create("Ready source", ContentSourceStatus.Ready);
        CatalogGraph pending = CatalogGraph.Create("Pending source", ContentSourceStatus.DeletionPending);
        await InsertGraphAsync(databasePath, ready);
        await InsertGraphAsync(databasePath, pending);
        var transport = new RejectingTransport();
        var provider = new SqliteChannelLogoProvider(databasePath, transport);

        object? readyBinding = await ReadLogoBindingAsync(provider, ready.SourceId, ready.ChannelId);
        object? pendingBinding = await ReadLogoBindingAsync(provider, pending.SourceId, pending.ChannelId);
        ChannelLogoImage? pendingImage = await provider.LoadAsync(pending.SourceId, pending.ChannelId);

        Assert.IsNotNull(readyBinding);
        Assert.IsNull(pendingBinding);
        Assert.IsNull(pendingImage);
        Assert.AreEqual(0, transport.Calls);
    }

    [TestMethod]
    public async Task LocatorReaderTreatsADeletionPendingSourceAsUnavailable()
    {
        using TemporaryDirectory temporary = TemporaryDirectory.Create("m12-ready-locator");
        string databasePath = Path.Combine(temporary.FullPath, "catalog.db");
        await InitializeDatabaseAsync(databasePath);
        CatalogGraph pending = CatalogGraph.Create("Pending source", ContentSourceStatus.DeletionPending);
        await InsertGraphAsync(databasePath, pending);
        DomainResult<ProtectedLocatorReference> reference = ProtectedLocatorReference.Parse(
            pending.LogoReference);
        Assert.IsTrue(reference.IsSuccess);

        (SecretLease? lease, string failure) = await ReadLocatorAsync(
            databasePath,
            pending.SourceId,
            pending.ChannelId,
            reference.Value!);

        Assert.IsNull(lease);
        Assert.AreEqual("Unavailable", failure);
    }

    private static async Task InitializeDatabaseAsync(string databasePath)
    {
        Type databaseType = typeof(IptvSuite.Infrastructure.AssemblyMarker).Assembly.GetType(
            "IptvSuite.Infrastructure.SqliteCatalogDatabase",
            throwOnError: true)!;
        object database = Activator.CreateInstance(
            databaseType,
            BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null,
            [databasePath],
            culture: null)!;
        await InvokeValueTaskAsync(
            databaseType.GetMethod("InitializeAsync", BindingFlags.Instance | BindingFlags.NonPublic)!,
            database,
            [CancellationToken.None]);
    }

    private static async Task InsertGraphAsync(string databasePath, CatalogGraph graph)
    {
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Pooling = false,
        }.ToString());
        await connection.OpenAsync();
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA foreign_keys = ON;
            INSERT INTO sources(
                source_id, configuration_id, source_kind, display_name, endpoint_scheme,
                endpoint_host, endpoint_port, configuration_reference, status, active_snapshot_id,
                created_utc, updated_utc, last_error_code)
            VALUES ($source, $configuration, $sourceKind, $displayName, 'https',
                'fixtures.invalid', 443, $configurationReference, $status, $snapshot,
                '2026-08-24T00:00:00.0000000+00:00', '2026-08-24T00:00:00.0000000+00:00', NULL);
            INSERT INTO snapshots(
                snapshot_id, source_id, retrieved_utc, content_hash, parser_version,
                normalization_version, schema_version, item_count, warning_count, state)
            VALUES ($snapshot, $source, '2026-08-24T00:00:00.0000000+00:00', randomblob(32),
                1, 1, 2, 1, 0, $snapshotState);
            INSERT INTO snapshot_keys(snapshot_id, key_generation_id, wrapped_dek, key_state)
            VALUES ($snapshot, $generation, randomblob(64), 1);
            INSERT INTO categories(category_id, snapshot_id, stable_key, display_name, sort_order)
            VALUES ($category, $snapshot, $categoryStableKey, $categoryName, 0);
            INSERT INTO channels(
                channel_id, snapshot_id, category_id, stable_key_version, stable_key,
                display_name, channel_number, stream_reference, logo_reference,
                provider_item_kind, provider_item_id, container_hint, is_adult, warning_flags)
            VALUES ($channel, $snapshot, $category, 1, $channelStableKey, $channelName,
                1, $streamReference, $logoReference, NULL, NULL, NULL, 0, 0);
            INSERT INTO protected_locators(
                locator_reference, snapshot_id, key_generation_id, owner_kind, owner_id,
                purpose, nonce, authentication_tag, ciphertext)
            VALUES
                ($streamReference, $snapshot, $generation, $channelOwner, $channel,
                    $streamPurpose, randomblob(12), randomblob(16), randomblob(32)),
                ($logoReference, $snapshot, $generation, $channelOwner, $channel,
                    $logoPurpose, randomblob(12), randomblob(16), randomblob(32));
            """;
        command.Parameters.AddWithValue("$source", Id(graph.SourceId.Value));
        command.Parameters.AddWithValue("$configuration", Id(Guid.NewGuid()));
        command.Parameters.AddWithValue("$sourceKind", (int)SourceKind.RemotePlaylist);
        command.Parameters.AddWithValue("$displayName", graph.DisplayName);
        command.Parameters.AddWithValue("$configurationReference", $"locator-ref-v1:{Guid.NewGuid():N}");
        command.Parameters.AddWithValue("$status", (int)ContentSourceStatus.Ready);
        command.Parameters.AddWithValue("$snapshot", Id(graph.SnapshotId.Value));
        command.Parameters.AddWithValue("$snapshotState", (int)PlaylistSnapshotState.Complete);
        command.Parameters.AddWithValue("$generation", Id(Guid.NewGuid()));
        command.Parameters.AddWithValue("$category", Id(graph.CategoryId.Value));
        command.Parameters.AddWithValue("$categoryStableKey", $"category-{graph.SourceId.Value:N}");
        command.Parameters.AddWithValue("$categoryName", $"{graph.DisplayName} category");
        command.Parameters.AddWithValue("$channel", Id(graph.ChannelId.Value));
        command.Parameters.AddWithValue("$channelStableKey", $"channel-{graph.SourceId.Value:N}");
        command.Parameters.AddWithValue("$channelName", $"{graph.DisplayName} channel");
        command.Parameters.AddWithValue("$streamReference", graph.StreamReference);
        command.Parameters.AddWithValue("$logoReference", graph.LogoReference);
        command.Parameters.AddWithValue("$channelOwner", (int)ProtectedRecordOwnerKind.Channel);
        command.Parameters.AddWithValue("$streamPurpose", (int)ProtectedValuePurpose.ChannelStreamLocator);
        command.Parameters.AddWithValue("$logoPurpose", (int)ProtectedValuePurpose.ChannelLogoLocator);
        await command.ExecuteNonQueryAsync();

        if (graph.Status == ContentSourceStatus.DeletionPending)
        {
            using var store = new M4InMemorySecretStore();
            ISourceDeletionLifecycle lifecycle = CreateDeletionLifecycle(databasePath, store);
            SourceDeletionLifecycleOperationResult marked =
                await lifecycle.MarkPendingAsync(graph.SourceId);
            Assert.IsTrue(marked.IsSuccess);
        }
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

    private static async Task<object?> ReadLogoBindingAsync(
        SqliteChannelLogoProvider provider,
        SourceId sourceId,
        ChannelId channelId)
    {
        MethodInfo method = typeof(SqliteChannelLogoProvider).GetMethod(
            "ReadBindingAsync",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        object valueTask = method.Invoke(provider, [sourceId, channelId, CancellationToken.None])!;
        Task task = (Task)valueTask.GetType().GetMethod("AsTask")!.Invoke(valueTask, null)!;
        await task;
        return task.GetType().GetProperty("Result")!.GetValue(task);
    }

    private static async Task<(SecretLease? Lease, string Failure)> ReadLocatorAsync(
        string databasePath,
        SourceId sourceId,
        ChannelId channelId,
        ProtectedLocatorReference reference)
    {
        Type readerType = typeof(IptvSuite.Infrastructure.AssemblyMarker).Assembly.GetType(
            "IptvSuite.Infrastructure.SqliteCatalogLocatorReader",
            throwOnError: true)!;
        object reader = Activator.CreateInstance(
            readerType,
            BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null,
            [databasePath],
            culture: null)!;
        MethodInfo method = readerType.GetMethod("ReadAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        object valueTask = method.Invoke(
            reader,
            [sourceId, channelId, ProtectedValuePurpose.ChannelLogoLocator, reference, CancellationToken.None])!;
        Task task = (Task)valueTask.GetType().GetMethod("AsTask")!.Invoke(valueTask, null)!;
        await task;
        object result = task.GetType().GetProperty("Result")!.GetValue(task)!;
        var lease = (SecretLease?)result.GetType().GetProperty("Lease")!.GetValue(result);
        string failure = result.GetType().GetProperty("Failure")!.GetValue(result)!.ToString()!;
        return (lease, failure);
    }

    private static async Task InvokeValueTaskAsync(
        MethodInfo method,
        object instance,
        object?[] arguments)
    {
        object valueTask = method.Invoke(instance, arguments)!;
        await (Task)valueTask.GetType().GetMethod("AsTask")!.Invoke(valueTask, null)!;
    }

    private static string Id(Guid value) =>
        value.ToString("N", System.Globalization.CultureInfo.InvariantCulture);

    private sealed record CatalogGraph(
        SourceId SourceId,
        SnapshotId SnapshotId,
        CategoryId CategoryId,
        ChannelId ChannelId,
        string StreamReference,
        string LogoReference,
        string DisplayName,
        ContentSourceStatus Status)
    {
        internal static CatalogGraph Create(string displayName, ContentSourceStatus status) =>
            new(
                SourceId.Generate(),
                SnapshotId.Generate(),
                CategoryId.Generate(),
                ChannelId.Generate(),
                $"locator-ref-v1:{Guid.NewGuid():N}",
                $"locator-ref-v1:{Guid.NewGuid():N}",
                displayName,
                status);
    }

    private sealed class RejectingTransport : IHttpTransport
    {
        internal int Calls { get; private set; }

        public ValueTask<HttpTransportResult> GetAsync(
            HttpTransportRequest request,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            throw new InvalidOperationException("Catalog admission must reject before transport.");
        }
    }
}
