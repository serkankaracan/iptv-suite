using System.Reflection;
using System.Text;
using IptvSuite.Application;
using IptvSuite.Domain;
using IptvSuite.Infrastructure;

namespace IptvSuite.IntegrationTests;

[TestClass]
public sealed class RemotePlaylistCatalogLoaderTests
{
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
        command.CommandText = "SELECT count(*) FROM channels WHERE snapshot_id = (SELECT active_snapshot_id FROM sources);";
        Assert.AreEqual(2L, Convert.ToInt64(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture));
        await connection.CloseAsync();
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
        object beginValueTask = begin.Invoke(sink, [source, CancellationToken.None])!;
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
        command.CommandText = "SELECT count(*) FROM sources;";
        Assert.AreEqual(0L, Convert.ToInt64(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture));
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
    public async Task TransportFailureMapsWithoutParsingOrExposingLocator()
    {
        var store = new M4InMemorySecretStore();
        ContentSource source = await CreateSourceAsync(store);
        var transport = new SingleResponseTransport(HttpStreamingResult.Failed(
            HttpTransportFailure.TlsValidationFailed,
            HttpTransportRetryability.Never));

        LoaderSnapshot result = await InvokeLoaderAsync(store, transport, source);

        Assert.IsFalse(result.IsSuccess);
        Assert.AreEqual(DomainErrorCode.TlsValidationFailed, result.ErrorCode);
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

    private sealed record LoaderSnapshot(
        bool IsSuccess,
        DomainErrorCode? ErrorCode,
        int EntryCount,
        string? FirstLocator);

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
                [stream, new Uri("https://fixtures.invalid/catalog/final/list.m3u"), new EmptyOwner()]);
            return ValueTask.FromResult(HttpStreamingResult.Success(200, lease));
        }

        private sealed class EmptyOwner : IDisposable { public void Dispose() { } }
    }
}
