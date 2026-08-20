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

    private static async Task<TestBatch> CreateBatchAsync(ContentSource? existingSource = null, string itemSuffix = "item")
    {
        var store = new M4InMemorySecretStore();
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
        byte[] logo = Encoding.UTF8.GetBytes($"https://fixtures.invalid/logo/{itemSuffix}.png");
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
            [source, snapshot.Value!, new[] { category.Value! }, new[] { channel.Value! }, locators])!;
        return new(batch, source, snapshot.Value!, stream, logo);
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

    private sealed record TestBatch(
        object Batch,
        ContentSource Source,
        PlaylistSnapshot Snapshot,
        byte[] StreamPlaintext,
        byte[] LogoPlaintext);
}
