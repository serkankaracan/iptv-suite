using System.Reflection;
using System.Text;
using IptvSuite.Application;
using IptvSuite.Domain;
using IptvSuite.Infrastructure;

namespace IptvSuite.IntegrationTests;

[TestClass]
public sealed class RemotePlaylistCatalogLoaderTests
{
    private static readonly string[] OldChannelPage = ["Old channel"];
    private static readonly string[] NewChannelPage = ["New channel"];

    [TestMethod]
    [Timeout(30_000)]
    public async Task StreamingLoaderCommitsDirectlyIntoEncryptedSqliteCatalog()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using TemporaryDirectory temporary = TemporaryDirectory.Create("m8-streaming-sink");
        string databasePath = Path.Combine(temporary.FullPath, "catalog.db");
        var store = new M4InMemorySecretStore();
        ContentSource source = await CreateSourceAsync(store);
        const string locator = "https://fixtures.invalid/catalog/final/live/direct.m3u8";
        var transport = new SingleResponseTransport(
            $"#EXTM3U\n#EXTINF:-1 tvg-id=\"direct\" group-title=\"News\",Direct\n{locator}\n" +
            "#EXTINF:-1 tvg-id=\"direct\" group-title=\"News\",Direct duplicate\n" +
            "https://fixtures.invalid/catalog/final/live/direct-duplicate.m3u8\n");
        Assembly assembly = typeof(BoundedHttpTransport).Assembly;
        Type sinkType = assembly.GetType("IptvSuite.Infrastructure.SqliteRemoteM3uImportSink", true)!;
        object sink = Activator.CreateInstance(
            sinkType,
            BindingFlags.Instance | BindingFlags.NonPublic,
            null,
            [databasePath],
            null)!;
        Type loaderType = assembly.GetType("IptvSuite.Infrastructure.RemotePlaylistCatalogLoader", true)!;
        object loader = Activator.CreateInstance(
            loaderType,
            BindingFlags.Instance | BindingFlags.NonPublic,
            null,
            [store, transport, sink],
            null)!;
        MethodInfo load = loaderType.GetMethod("LoadAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        object valueTask = load.Invoke(loader, [source, CancellationToken.None])!;
        Task task = (Task)valueTask.GetType().GetMethod("AsTask")!.Invoke(valueTask, null)!;
        await task;
        object result = task.GetType().GetProperty("Result")!.GetValue(task)!;
        Assert.IsTrue((bool)result.GetType().GetProperty("IsSuccess")!.GetValue(result)!);

        await using var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={databasePath};Pooling=False");
        await connection.OpenAsync();
        await using Microsoft.Data.Sqlite.SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                (SELECT count(*) FROM channels WHERE snapshot_id = (SELECT active_snapshot_id FROM sources)),
                (SELECT count(*) FROM sync_runs WHERE completed_utc IS NOT NULL AND result_code = 0
                    AND parsed_count = 2 AND persisted_count = 2 AND warning_count = 1
                    AND failure_code IS NULL),
                (SELECT count(*) FROM snapshots WHERE http_etag = '"catalog-v1"'
                    AND http_last_modified_utc = '2026-08-21T12:34:56.0000000+00:00'
                    AND length(cache_key) = 32);
            """;
        await using Microsoft.Data.Sqlite.SqliteDataReader reader = await command.ExecuteReaderAsync();
        Assert.IsTrue(await reader.ReadAsync());
        Assert.AreEqual(2L, reader.GetInt64(0));
        Assert.AreEqual(1L, reader.GetInt64(1));
        Assert.AreEqual(1L, reader.GetInt64(2));
        await connection.CloseAsync();

        object history = Activator.CreateInstance(
            assembly.GetType("IptvSuite.Infrastructure.SqliteCatalogSyncHistory", true)!,
            BindingFlags.Instance | BindingFlags.NonPublic,
            null,
            [databasePath],
            null)!;
        MethodInfo readRecent = history.GetType().GetMethod("ReadRecentAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        object historyValueTask = readRecent.Invoke(history, [source.Id, 10, CancellationToken.None])!;
        Task historyTask = (Task)historyValueTask.GetType().GetMethod("AsTask")!.Invoke(historyValueTask, null)!;
        await historyTask;
        object historyResult = historyTask.GetType().GetProperty("Result")!.GetValue(historyTask)!;
        Assert.AreEqual(1, (int)historyResult.GetType().GetProperty("Count")!.GetValue(historyResult)!);
        object summary = historyResult.GetType().GetProperty("Item")!.GetValue(historyResult, [0])!;
        Assert.AreEqual(0, (int?)summary.GetType().GetProperty("ResultCode")!.GetValue(summary));
        Assert.AreEqual(2, (int)summary.GetType().GetProperty("ParsedCount")!.GetValue(summary)!);
        Assert.AreEqual(2, (int)summary.GetType().GetProperty("PersistedCount")!.GetValue(summary)!);
        Assert.AreEqual(1, (int)summary.GetType().GetProperty("WarningCount")!.GetValue(summary)!);
        Assert.IsNull(summary.GetType().GetProperty("FailureCode")!.GetValue(summary));
        byte[] databaseBytes = await File.ReadAllBytesAsync(databasePath);
        Assert.IsFalse(databaseBytes.AsSpan().IndexOf(Encoding.UTF8.GetBytes(locator)) >= 0);
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task StreamingSqliteSinkAbortRollsBackImportSession()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using TemporaryDirectory temporary = TemporaryDirectory.Create("m8-streaming-abort");
        string databasePath = Path.Combine(temporary.FullPath, "catalog.db");
        var store = new M4InMemorySecretStore();
        ContentSource source = await CreateSourceAsync(store);
        Assembly assembly = typeof(BoundedHttpTransport).Assembly;
        Type sinkType = assembly.GetType("IptvSuite.Infrastructure.SqliteRemoteM3uImportSink", true)!;
        object sink = Activator.CreateInstance(
            sinkType,
            BindingFlags.Instance | BindingFlags.NonPublic,
            null,
            [databasePath],
            null)!;
        MethodInfo begin = sinkType.GetMethod("BeginAsync")!;
        object beginValueTask = begin.Invoke(sink, [source, null, null, CancellationToken.None])!;
        Task beginTask = (Task)beginValueTask.GetType().GetMethod("AsTask")!.Invoke(beginValueTask, null)!;
        await beginTask;
        object beginResult = beginTask.GetType().GetProperty("Result")!.GetValue(beginTask)!;
        Assert.IsTrue((bool)beginResult.GetType().GetProperty("IsSuccess")!.GetValue(beginResult)!);

        MethodInfo abort = sinkType.GetMethod("AbortAsync")!;
        object abortValueTask = abort.Invoke(sink, [CancellationToken.None])!;
        await (Task)abortValueTask.GetType().GetMethod("AsTask")!.Invoke(abortValueTask, null)!;

        await using var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={databasePath};Pooling=False");
        await connection.OpenAsync();
        await using Microsoft.Data.Sqlite.SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT (SELECT count(*) FROM sources), (SELECT count(*) FROM sync_runs);";
        await using Microsoft.Data.Sqlite.SqliteDataReader reader = await command.ExecuteReaderAsync();
        Assert.IsTrue(await reader.ReadAsync());
        Assert.AreEqual(0L, reader.GetInt64(0));
        Assert.AreEqual(0L, reader.GetInt64(1));
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task PersistedDeletionPendingSourceRejectsStaleStreamingImportAndPreservesCatalog()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using TemporaryDirectory temporary = TemporaryDirectory.Create("m8-streaming-delete-guard");
        string databasePath = Path.Combine(temporary.FullPath, "catalog.db");
        var store = new M4InMemorySecretStore();
        ContentSource pending = await CreateSourceAsync(store);
        ContentSource sibling = await CreateSourceAsync(store);
        DomainResultSnapshot pendingInitial = await InvokeSqliteLoaderAsync(
            store,
            new SingleResponseTransport(
                "#EXTM3U\n#EXTINF:-1 tvg-id=\"pending-original\",Pending original\nhttps://fixtures.invalid/pending-original.m3u8\n"),
            pending,
            databasePath);
        DomainResultSnapshot siblingInitial = await InvokeSqliteLoaderAsync(
            store,
            new SingleResponseTransport(
                "#EXTM3U\n#EXTINF:-1 tvg-id=\"sibling\",Sibling\nhttps://fixtures.invalid/sibling.m3u8\n"),
            sibling,
            databasePath);
        Assert.IsTrue(pendingInitial.IsSuccess);
        Assert.IsTrue(siblingInitial.IsSuccess);

        ISourceDeletionLifecycle lifecycle = CreateDeletionLifecycle(databasePath, store);
        SourceDeletionLifecycleOperationResult marked = await lifecycle.MarkPendingAsync(pending.Id);
        Assert.IsTrue(marked.IsSuccess);

        DomainResultSnapshot rejected = await InvokeSqliteLoaderAsync(
            store,
            new SingleResponseTransport(
                "#EXTM3U\n#EXTINF:-1 tvg-id=\"stale\",Stale replacement\nhttps://fixtures.invalid/stale.m3u8\n"),
            pending,
            databasePath);

        Assert.IsFalse(rejected.IsSuccess);
        Assert.AreEqual(DomainErrorCode.DomainInvariantViolation, rejected.ErrorCode);
        await using var reopened = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={databasePath};Pooling=False");
        await reopened.OpenAsync();
        await using Microsoft.Data.Sqlite.SqliteCommand state = reopened.CreateCommand();
        state.CommandText = """
            SELECT
                (SELECT status FROM sources WHERE source_id = $pendingSource),
                (SELECT active_snapshot_id FROM sources WHERE source_id = $pendingSource),
                (SELECT status FROM sources WHERE source_id = $siblingSource),
                (SELECT active_snapshot_id FROM sources WHERE source_id = $siblingSource),
                (SELECT count(*) FROM snapshots WHERE source_id = $pendingSource),
                (SELECT count(*) FROM channels c JOIN snapshots s ON s.snapshot_id = c.snapshot_id
                    WHERE s.source_id = $pendingSource),
                (SELECT count(*) FROM protected_locators l JOIN snapshots s ON s.snapshot_id = l.snapshot_id
                    WHERE s.source_id = $pendingSource),
                (SELECT count(*) FROM sources),
                (SELECT count(*) FROM snapshots),
                (SELECT count(*) FROM channels),
                (SELECT count(*) FROM sync_runs),
                (SELECT count(*) FROM channels WHERE display_name = 'Stale replacement');
            """;
        state.Parameters.AddWithValue("$pendingSource", pending.Id.Value.ToString("N"));
        state.Parameters.AddWithValue("$siblingSource", sibling.Id.Value.ToString("N"));
        await using Microsoft.Data.Sqlite.SqliteDataReader reader = await state.ExecuteReaderAsync();
        Assert.IsTrue(await reader.ReadAsync());
        Assert.AreEqual((long)ContentSourceStatus.DeletionPending, reader.GetInt64(0));
        Assert.IsFalse(reader.IsDBNull(1));
        Assert.AreEqual((long)ContentSourceStatus.Ready, reader.GetInt64(2));
        Assert.IsFalse(reader.IsDBNull(3));
        Assert.AreEqual(1L, reader.GetInt64(4));
        Assert.AreEqual(1L, reader.GetInt64(5));
        Assert.AreEqual(1L, reader.GetInt64(6));
        Assert.AreEqual(2L, reader.GetInt64(7));
        Assert.AreEqual(2L, reader.GetInt64(8));
        Assert.AreEqual(2L, reader.GetInt64(9));
        Assert.AreEqual(2L, reader.GetInt64(10));
        Assert.AreEqual(0L, reader.GetInt64(11));
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task ParserFailureAfterStreamingWritePreservesPreviousActiveSnapshot()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using TemporaryDirectory temporary = TemporaryDirectory.Create("m8-streaming-fault");
        string databasePath = Path.Combine(temporary.FullPath, "catalog.db");
        var store = new M4InMemorySecretStore();
        ContentSource source = await CreateSourceAsync(store);
        DomainResultSnapshot initial = await InvokeSqliteLoaderAsync(
            store,
            new SingleResponseTransport(
                "#EXTM3U\n#EXTINF:-1 tvg-id=\"stable\",Stable\nhttps://fixtures.invalid/stable.m3u8\n"),
            source,
            databasePath);
        Assert.IsTrue(initial.IsSuccess);
        byte[] validPrefix = Encoding.UTF8.GetBytes(
            "#EXTM3U\n#EXTINF:-1 tvg-id=\"replacement\",Replacement\nhttps://fixtures.invalid/replacement.m3u8\n");
        byte[] malformed = [.. validPrefix, 0xFF, 0x0A];

        DomainResultSnapshot failed = await InvokeSqliteLoaderAsync(
            store,
            new ByteResponseTransport(malformed),
            source,
            databasePath);

        Assert.IsFalse(failed.IsSuccess);
        await using var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={databasePath};Pooling=False");
        await connection.OpenAsync();
        await using Microsoft.Data.Sqlite.SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT (SELECT count(*) FROM snapshots), (SELECT count(*) FROM channels), (SELECT count(*) FROM sync_runs);";
        await using Microsoft.Data.Sqlite.SqliteDataReader reader = await command.ExecuteReaderAsync();
        Assert.IsTrue(await reader.ReadAsync());
        Assert.AreEqual(1L, reader.GetInt64(0));
        Assert.AreEqual(1L, reader.GetInt64(1));
        Assert.AreEqual(1L, reader.GetInt64(2));
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task ConcurrentReaderSeesOldSnapshotUntilStreamingRefreshCommits()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using TemporaryDirectory temporary = TemporaryDirectory.Create("m8-streaming-concurrent-read");
        string databasePath = Path.Combine(temporary.FullPath, "catalog.db");
        var store = new M4InMemorySecretStore();
        ContentSource source = await CreateSourceAsync(store);
        DomainResultSnapshot initial = await InvokeSqliteLoaderAsync(
            store,
            new SingleResponseTransport(
                "#EXTM3U\n#EXTINF:-1 tvg-id=\"old\",Old channel\nhttps://fixtures.invalid/old.m3u8\n"),
            source,
            databasePath);
        Assert.IsTrue(initial.IsSuccess);

        Assembly assembly = typeof(BoundedHttpTransport).Assembly;
        Type sinkType = assembly.GetType("IptvSuite.Infrastructure.SqliteRemoteM3uImportSink", true)!;
        object sink = Activator.CreateInstance(
            sinkType,
            BindingFlags.Instance | BindingFlags.NonPublic,
            null,
            [databasePath],
            null)!;
        await using IAsyncDisposable sinkScope = (IAsyncDisposable)sink;
        await InvokeDomainValueTaskAsync(
            sinkType.GetMethod("BeginAsync")!,
            sink,
            [source, null, null, CancellationToken.None]);
        Type entryType = assembly.GetType("IptvSuite.Infrastructure.RemoteM3uEntry", true)!;
        object entry = Activator.CreateInstance(
            entryType,
            BindingFlags.Instance | BindingFlags.NonPublic,
            null,
            [
                "https://fixtures.invalid/new.m3u8",
                "New channel",
                "new",
                null,
                null,
                "News",
                null,
                ChannelNormalizationWarnings.None,
            ],
            null)!;
        await InvokeDomainValueTaskAsync(sinkType.GetMethod("WriteAsync")!, sink, [entry, CancellationToken.None]);

        CollectionAssert.AreEqual(OldChannelPage, await ReadActiveChannelNamesAsync(databasePath));

        Type parseResultType = assembly.GetType("IptvSuite.Infrastructure.RemoteM3uParseResult", true)!;
        Type entryListType = typeof(List<>).MakeGenericType(entryType);
        object entries = Activator.CreateInstance(entryListType)!;
        entryListType.GetMethod("Add")!.Invoke(entries, [entry]);
        object parseResult = Activator.CreateInstance(
            parseResultType,
            BindingFlags.Instance | BindingFlags.NonPublic,
            null,
            [PlaylistContentKind.ExtendedM3uCatalog, entries, 1, 0, null],
            null)!;
        await InvokeDomainValueTaskAsync(
            sinkType.GetMethod("CompleteAsync")!,
            sink,
            [parseResult, CancellationToken.None]);

        CollectionAssert.AreEqual(NewChannelPage, await ReadActiveChannelNamesAsync(databasePath));
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
    }

    [TestMethod]
    public async Task AuthoritativeProtectedLocatorDownloadsAndParsesCatalog()
    {
        var store = new M4InMemorySecretStore();
        ContentSource source = await CreateSourceAsync(store);
        var transport = new SingleResponseTransport(
            "#EXTM3U\n#EXTINF:-1 group-title=\"News\",Synthetic\nstream/news.ts\n");

        LoaderSnapshot result = await InvokeLoaderAsync(store, transport, source);

        Assert.IsTrue(result.IsSuccess);
        Assert.AreEqual(1, result.EntryCount);
        Assert.AreEqual("https://fixtures.invalid/catalog/final/stream/news.ts", result.FirstLocator);
    }

    [TestMethod]
    [DataRow(HttpTransportFailure.AuthenticationRejected, HttpTransportRetryability.Never,
        DomainErrorCode.AuthenticationRejected)]
    [DataRow(HttpTransportFailure.ResourceNotFound, HttpTransportRetryability.Never,
        DomainErrorCode.RemoteResourceNotFound)]
    [DataRow(HttpTransportFailure.RequestRejected, HttpTransportRetryability.Never,
        DomainErrorCode.RemoteRequestRejected)]
    [DataRow(HttpTransportFailure.ResponseTooLarge, HttpTransportRetryability.Never,
        DomainErrorCode.RemoteResponseTooLarge)]
    [DataRow(HttpTransportFailure.RequestTimedOut, HttpTransportRetryability.BoundedTransient,
        DomainErrorCode.RequestTimedOut)]
    [DataRow(HttpTransportFailure.NetworkUnavailable, HttpTransportRetryability.Manual,
        DomainErrorCode.NetworkUnreachable)]
    [DataRow(HttpTransportFailure.TlsValidationFailed, HttpTransportRetryability.Never,
        DomainErrorCode.TlsValidationFailed)]
    [DataRow(HttpTransportFailure.RateLimited, HttpTransportRetryability.BoundedTransient,
        DomainErrorCode.RequestRateLimited)]
    [DataRow(HttpTransportFailure.RemoteServiceUnavailable, HttpTransportRetryability.BoundedTransient,
        DomainErrorCode.RemoteServiceUnavailable)]
    [DataRow(HttpTransportFailure.EndpointAddressRejected, HttpTransportRetryability.Never,
        DomainErrorCode.RemoteRequestRejected)]
    public async Task TransportFailureMapsWithoutParsingOrExposingLocator(
        HttpTransportFailure failure,
        HttpTransportRetryability retryability,
        DomainErrorCode expectedError)
    {
        var store = new M4InMemorySecretStore();
        ContentSource source = await CreateSourceAsync(store);
        var transport = new SingleResponseTransport(HttpStreamingResult.Failed(
            failure,
            retryability));

        LoaderSnapshot result = await InvokeLoaderAsync(store, transport, source);

        Assert.IsFalse(result.IsSuccess);
        Assert.AreEqual(expectedError, result.ErrorCode);
        Assert.AreEqual("[HTTP-TRANSPORT-REQUEST]", transport.RequestText);
    }

    private static async Task<ContentSource> CreateSourceAsync(M4InMemorySecretStore store)
    {
        var protection = new SourceDraftProtectionService(store);
        DomainResult<ValidatedSourceDraft> draft = await protection.ProtectRemotePlaylistAsync(
            SourceId.Generate(),
            "Synthetic remote",
            "https://fixtures.invalid/catalog/list.m3u");
        Assert.IsTrue(draft.IsSuccess);
        DateTimeOffset now = new(2026, 8, 20, 0, 0, 0, TimeSpan.Zero);
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

    private static async Task<LoaderSnapshot> InvokeLoaderAsync(
        ISecretStore store,
        IStreamingHttpTransport transport,
        ContentSource source)
    {
        Type type = typeof(BoundedHttpTransport).Assembly.GetType(
            "IptvSuite.Infrastructure.RemotePlaylistCatalogLoader", true)!;
        Type sinkType = typeof(BoundedHttpTransport).Assembly.GetType(
            "IptvSuite.Infrastructure.IRemoteM3uImportSink", true)!;
        object sink = DispatchProxy.Create(sinkType, typeof(RemoteM3uSinkProxy));
        var sinkProxy = (RemoteM3uSinkProxy)sink;
        object loader = Activator.CreateInstance(
            type,
            BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null,
            args: [store, transport, sink],
            culture: null)!;
        MethodInfo method = type.GetMethod("LoadAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        object valueTask = method.Invoke(loader, [source, CancellationToken.None])!;
        Task task = (Task)valueTask.GetType().GetMethod("AsTask")!.Invoke(valueTask, null)!;
        await task;
        object result = task.GetType().GetProperty("Result")!.GetValue(task)!;
        bool success = (bool)result.GetType().GetProperty("IsSuccess")!.GetValue(result)!;
        if (!success)
        {
            object error = result.GetType().GetProperty("Error")!.GetValue(result)!;
            return new(false, (DomainErrorCode)error.GetType().GetProperty("Code")!.GetValue(error)!, 0, null);
        }

        object parsed = result.GetType().GetProperty("Value")!.GetValue(result)!;
        int processedEntryCount = (int)parsed.GetType().GetProperty(
            "ProcessedEntryCount", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(parsed)!;
        return new(true, null, processedEntryCount, sinkProxy.FirstLocator);
    }

    private static async Task<DomainResultSnapshot> InvokeSqliteLoaderAsync(
        ISecretStore store,
        IStreamingHttpTransport transport,
        ContentSource source,
        string databasePath)
    {
        Assembly assembly = typeof(BoundedHttpTransport).Assembly;
        Type sinkType = assembly.GetType("IptvSuite.Infrastructure.SqliteRemoteM3uImportSink", true)!;
        object sink = Activator.CreateInstance(
            sinkType,
            BindingFlags.Instance | BindingFlags.NonPublic,
            null,
            [databasePath],
            null)!;
        Type loaderType = assembly.GetType("IptvSuite.Infrastructure.RemotePlaylistCatalogLoader", true)!;
        object loader = Activator.CreateInstance(
            loaderType,
            BindingFlags.Instance | BindingFlags.NonPublic,
            null,
            [store, transport, sink],
            null)!;
        MethodInfo method = loaderType.GetMethod("LoadAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        object valueTask = method.Invoke(loader, [source, CancellationToken.None])!;
        Task task = (Task)valueTask.GetType().GetMethod("AsTask")!.Invoke(valueTask, null)!;
        await task;
        object result = task.GetType().GetProperty("Result")!.GetValue(task)!;
        bool success = (bool)result.GetType().GetProperty("IsSuccess")!.GetValue(result)!;
        DomainErrorCode? errorCode = null;
        if (!success)
        {
            object error = result.GetType().GetProperty("Error")!.GetValue(result)!;
            errorCode = (DomainErrorCode)error.GetType().GetProperty("Code")!.GetValue(error)!;
        }

        return new(success, errorCode);
    }

    private static async Task InvokeDomainValueTaskAsync(
        MethodInfo method,
        object instance,
        object?[] arguments)
    {
        object valueTask = method.Invoke(instance, arguments)!;
        Task task = (Task)valueTask.GetType().GetMethod("AsTask")!.Invoke(valueTask, null)!;
        await task;
        object result = task.GetType().GetProperty("Result")!.GetValue(task)!;
        Assert.IsTrue((bool)result.GetType().GetProperty("IsSuccess")!.GetValue(result)!);
    }

    private static async Task<string[]> ReadActiveChannelNamesAsync(string databasePath)
    {
        await using var connection = new Microsoft.Data.Sqlite.SqliteConnection(
            new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
            {
                DataSource = databasePath,
                Mode = Microsoft.Data.Sqlite.SqliteOpenMode.ReadOnly,
                Pooling = false,
            }.ToString());
        await connection.OpenAsync();
        await using Microsoft.Data.Sqlite.SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT c.display_name
            FROM channels c
            JOIN sources s ON s.active_snapshot_id = c.snapshot_id
            ORDER BY c.display_name;
            """;
        var names = new List<string>();
        await using Microsoft.Data.Sqlite.SqliteDataReader reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            names.Add(reader.GetString(0));
        }

        return names.ToArray();
    }

    private sealed record LoaderSnapshot(
        bool IsSuccess,
        DomainErrorCode? ErrorCode,
        int EntryCount,
        string? FirstLocator);

    private sealed record DomainResultSnapshot(bool IsSuccess, DomainErrorCode? ErrorCode);

    public class RemoteM3uSinkProxy : DispatchProxy
    {
        internal string? FirstLocator { get; private set; }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            switch (targetMethod!.Name)
            {
                case "BeginAsync":
                case "CompleteAsync":
                    return new ValueTask<DomainResult<bool>>(DomainResult.Success(true));
                case "AbortAsync":
                    return ValueTask.CompletedTask;
                case "WriteAsync":
                    object entry = args![0]!;
                    FirstLocator ??= (string)entry.GetType().GetProperty(
                        "Locator", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(entry)!;
                    return new ValueTask<DomainResult<bool>>(DomainResult.Success(true));
                default:
                    throw new InvalidOperationException("Unexpected import-sink method.");
            }
        }
    }

    private sealed class SingleResponseTransport : IStreamingHttpTransport
    {
        private readonly string? _body;
        private readonly HttpStreamingResult? _failure;

        internal SingleResponseTransport(string body)
        {
            _body = body;
        }

        internal SingleResponseTransport(HttpStreamingResult failure) => _failure = failure;

        internal string? RequestText { get; private set; }

        public ValueTask<HttpStreamingResult> GetStreamAsync(
            HttpTransportRequest request,
            CancellationToken cancellationToken = default)
        {
            RequestText = request.ToString();
            if (_failure is not null) return ValueTask.FromResult(_failure);
            var stream = new MemoryStream(Encoding.UTF8.GetBytes(_body!), writable: false);
            ConstructorInfo constructor = typeof(HttpStreamingResponseLease).GetConstructors(
                BindingFlags.Instance | BindingFlags.NonPublic).Single();
            var lease = (HttpStreamingResponseLease)constructor.Invoke(
                [
                    stream,
                    new Uri("https://fixtures.invalid/catalog/final/list.m3u"),
                    new EmptyResponseOwner(),
                    "\"catalog-v1\"",
                    new DateTimeOffset(2026, 8, 21, 12, 34, 56, TimeSpan.Zero),
                ]);
            return ValueTask.FromResult(HttpStreamingResult.Success(200, lease));
        }
    }

    private sealed class ByteResponseTransport(byte[] body) : IStreamingHttpTransport
    {
        public ValueTask<HttpStreamingResult> GetStreamAsync(
            HttpTransportRequest request,
            CancellationToken cancellationToken = default)
        {
            var stream = new MemoryStream(body, writable: false);
            ConstructorInfo constructor = typeof(HttpStreamingResponseLease).GetConstructors(
                BindingFlags.Instance | BindingFlags.NonPublic).Single();
            var lease = (HttpStreamingResponseLease)constructor.Invoke(
                [stream, new Uri("https://fixtures.invalid/catalog/final/list.m3u"), new EmptyResponseOwner(), null, null]);
            return ValueTask.FromResult(HttpStreamingResult.Success(200, lease));
        }
    }

    private sealed class EmptyResponseOwner : IDisposable
    {
        public void Dispose()
        {
        }
    }
}
