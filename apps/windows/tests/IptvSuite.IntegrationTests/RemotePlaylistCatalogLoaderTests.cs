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
    public async Task AuthoritativeProtectedLocatorDownloadsAndParsesCatalog()
    {
        var store = new M4InMemorySecretStore();
        ContentSource source = await CreateSourceAsync(store);
        var transport = new SingleResponseTransport(
            "#EXTM3U\n#EXTINF:-1 group-title=\"News\",Synthetic\nstream/news.ts\n");

        LoaderSnapshot result = await InvokeLoaderAsync(store, transport, source);

        Assert.IsTrue(result.IsSuccess);
        Assert.AreEqual(1, result.EntryCount);
        Assert.AreEqual("https://fixtures.invalid/catalog/stream/news.ts", result.FirstLocator);
    }

    [TestMethod]
    public async Task TransportFailureMapsWithoutParsingOrExposingLocator()
    {
        var store = new M4InMemorySecretStore();
        ContentSource source = await CreateSourceAsync(store);
        var transport = new SingleResponseTransport(HttpTransportResult.Failed(
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
        IHttpTransport transport,
        ContentSource source)
    {
        Type type = typeof(BoundedHttpTransport).Assembly.GetType(
            "IptvSuite.Infrastructure.RemotePlaylistCatalogLoader", true)!;
        object loader = Activator.CreateInstance(
            type,
            BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null,
            args: [store, transport],
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
        var entries = (System.Collections.IList)parsed.GetType().GetProperty(
            "Entries", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(parsed)!;
        object first = entries[0]!;
        string locator = (string)first.GetType().GetProperty(
            "Locator", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(first)!;
        return new(true, null, entries.Count, locator);
    }

    private sealed record LoaderSnapshot(
        bool IsSuccess,
        DomainErrorCode? ErrorCode,
        int EntryCount,
        string? FirstLocator);

    private sealed class SingleResponseTransport : IHttpTransport
    {
        private readonly HttpTransportResult _result;

        internal SingleResponseTransport(string body)
            : this(HttpTransportResult.Success(200, HttpResponseLease.CopyFrom(Encoding.UTF8.GetBytes(body))))
        {
        }

        internal SingleResponseTransport(HttpTransportResult result) => _result = result;

        internal string? RequestText { get; private set; }

        public ValueTask<HttpTransportResult> GetAsync(
            HttpTransportRequest request,
            CancellationToken cancellationToken = default)
        {
            RequestText = request.ToString();
            return ValueTask.FromResult(_result);
        }
    }
}
