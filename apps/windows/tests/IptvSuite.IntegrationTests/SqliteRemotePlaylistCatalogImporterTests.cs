using System.Reflection;
using System.Text;
using IptvSuite.Application;
using IptvSuite.Domain;
using IptvSuite.Infrastructure;
using Microsoft.Data.Sqlite;

namespace IptvSuite.IntegrationTests;

[TestClass]
public sealed class SqliteRemotePlaylistCatalogImporterTests
{
    [TestMethod]
    [Timeout(30_000)]
    public async Task FreshDatabaseImportCommitsCatalogWithoutPersistingRawLocators()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using TemporaryDirectory temporary = TemporaryDirectory.Create("m16-remote-import-success");
        string databasePath = Path.Combine(temporary.FullPath, "catalog.db");
        using var store = new M4InMemorySecretStore();
        const string sourceLocator = "https://fixtures.invalid/catalog/list.m3u";
        const string firstChannelLocator = "https://fixtures.invalid/live/first.m3u8";
        ContentSource source = await CreateSourceAsync(store, sourceLocator);
        var transport = new QueueResponseTransport(
            $"#EXTM3U\n#EXTINF:-1 tvg-id=\"duplicate\" group-title=\"News\",First\n{firstChannelLocator}\n" +
            "#EXTINF:-1 tvg-id=\"duplicate\" group-title=\"News\",Second\n" +
            "https://fixtures.invalid/live/second.m3u8\n");
        var importer = new SqliteRemotePlaylistCatalogImporter(databasePath, store, transport);

        RemotePlaylistCatalogImportResult result = await importer.ImportAsync(source);

        Assert.AreEqual(CatalogImportCommitDisposition.Committed, result.Disposition);
        Assert.AreEqual(2, result.ImportedChannelCount);
        Assert.AreEqual(1, result.WarningCount);
        Assert.IsNull(result.Error);
        Assert.AreEqual(2L, await ReadScalarAsync(
            databasePath,
            "SELECT count(*) FROM channels WHERE snapshot_id = (SELECT active_snapshot_id FROM sources);"));

        await AssertDirectoryDoesNotContainAsync(temporary.FullPath, sourceLocator);
        await AssertDirectoryDoesNotContainAsync(temporary.FullPath, firstChannelLocator);
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task HlsResponseRollsBackBeforeCommitAndNextImportUsesFreshSink()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using TemporaryDirectory temporary = TemporaryDirectory.Create("m16-remote-import-hls");
        string databasePath = Path.Combine(temporary.FullPath, "catalog.db");
        using var store = new M4InMemorySecretStore();
        ContentSource source = await CreateSourceAsync(
            store,
            "https://fixtures.invalid/catalog/list.m3u");
        var transport = new QueueResponseTransport(
            "#EXTM3U\n#EXT-X-VERSION:3\n#EXT-X-TARGETDURATION:10\n" +
            "#EXTINF:10,\nsegment-1.ts\n#EXT-X-ENDLIST\n",
            "#EXTM3U\n#EXTINF:-1 tvg-id=\"recovered\" group-title=\"News\",Recovered\n" +
            "https://fixtures.invalid/live/recovered.m3u8\n");
        var importer = new SqliteRemotePlaylistCatalogImporter(databasePath, store, transport);

        RemotePlaylistCatalogImportResult rejected = await importer.ImportAsync(source);

        Assert.AreEqual(CatalogImportCommitDisposition.NotCommitted, rejected.Disposition);
        Assert.AreEqual(DomainErrorCode.UnsupportedPlaylistFormat, rejected.Error?.Code);
        Assert.IsNull(rejected.ImportedChannelCount);
        Assert.AreEqual(0L, await ReadScalarAsync(databasePath, "SELECT count(*) FROM sources;"));
        Assert.AreEqual(0L, await ReadScalarAsync(databasePath, "SELECT count(*) FROM snapshots;"));

        RemotePlaylistCatalogImportResult recovered = await importer.ImportAsync(source);

        Assert.AreEqual(CatalogImportCommitDisposition.Committed, recovered.Disposition);
        Assert.AreEqual(1, recovered.ImportedChannelCount);
        Assert.AreEqual(0, recovered.WarningCount);
        Assert.AreEqual(1L, await ReadScalarAsync(databasePath, "SELECT count(*) FROM sources;"));
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task PostCommitTeardownFailurePreservesCommittedResultAndCatalog()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using TemporaryDirectory temporary = TemporaryDirectory.Create("m16-remote-import-teardown");
        string databasePath = Path.Combine(temporary.FullPath, "catalog.db");
        using var store = new M4InMemorySecretStore();
        ContentSource source = await CreateSourceAsync(
            store,
            "https://fixtures.invalid/catalog/list.m3u");
        var transport = new QueueResponseTransport(
            "#EXTM3U\n#EXTINF:-1 tvg-id=\"committed\" group-title=\"News\",Committed\n" +
            "https://fixtures.invalid/live/committed.m3u8\n");
        IRemotePlaylistCatalogImporter importer = CreateFaultedImporter(
            databasePath,
            store,
            transport);

        RemotePlaylistCatalogImportResult result = await importer.ImportAsync(source);

        Assert.AreEqual(CatalogImportCommitDisposition.Committed, result.Disposition);
        Assert.AreEqual(1, result.ImportedChannelCount);
        Assert.AreEqual(0, result.WarningCount);
        Assert.IsNull(result.Error);
        Assert.AreEqual(1L, await ReadScalarAsync(databasePath, "SELECT count(*) FROM sources;"));
        Assert.AreEqual(1L, await ReadScalarAsync(
            databasePath,
            "SELECT count(*) FROM channels WHERE snapshot_id = (SELECT active_snapshot_id FROM sources);"));
    }

    private static async Task<ContentSource> CreateSourceAsync(
        ISecretStore store,
        string locator)
    {
        var protection = new SourceDraftProtectionService(store);
        DomainResult<ValidatedSourceDraft> draft = await protection.ProtectRemotePlaylistAsync(
            SourceId.Generate(),
            "Synthetic remote",
            locator);
        Assert.IsTrue(draft.IsSuccess);
        DateTimeOffset now = new(2026, 8, 26, 0, 0, 0, TimeSpan.Zero);
        DomainResult<ContentSource> source = ContentSource.Create(
            draft.Value,
            ContentSourceStatus.Testing,
            now,
            now);
        Assert.IsTrue(source.IsSuccess);
        return source.Value!;
    }

    private static async Task<long> ReadScalarAsync(string databasePath, string sql)
    {
        await using var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder
            {
                DataSource = databasePath,
                Mode = SqliteOpenMode.ReadOnly,
                Pooling = false,
            }.ToString());
        await connection.OpenAsync();
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static IRemotePlaylistCatalogImporter CreateFaultedImporter(
        string databasePath,
        ISecretStore store,
        IStreamingHttpTransport transport)
    {
        Type importerType = typeof(SqliteRemotePlaylistCatalogImporter);
        Type faultType = importerType.Assembly.GetType(
            "IptvSuite.Infrastructure.SqliteRemoteImportFaultPoint",
            throwOnError: true)!;
        ConstructorInfo constructor = importerType.GetConstructor(
            BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null,
            [typeof(string), typeof(ISecretStore), typeof(IStreamingHttpTransport), faultType],
            modifiers: null)!;
        return (IRemotePlaylistCatalogImporter)constructor.Invoke(
            [databasePath, store, transport, Enum.Parse(faultType, "SessionTeardown")]);
    }

    private static async Task AssertDirectoryDoesNotContainAsync(string directory, string value)
    {
        byte[] marker = Encoding.UTF8.GetBytes(value);
        foreach (string path in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
        {
            byte[] content = await File.ReadAllBytesAsync(path);
            Assert.IsFalse(
                content.AsSpan().IndexOf(marker) >= 0,
                $"A raw locator marker was found in {Path.GetFileName(path)}.");
        }
    }

    private sealed class QueueResponseTransport(params string[] bodies) : IStreamingHttpTransport
    {
        private readonly Queue<string> _bodies = new(bodies);

        public ValueTask<HttpStreamingResult> GetStreamAsync(
            HttpTransportRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var stream = new MemoryStream(Encoding.UTF8.GetBytes(_bodies.Dequeue()), writable: false);
            ConstructorInfo constructor = typeof(HttpStreamingResponseLease).GetConstructors(
                BindingFlags.Instance | BindingFlags.NonPublic).Single();
            var lease = (HttpStreamingResponseLease)constructor.Invoke(
                [
                    stream,
                    new Uri("https://fixtures.invalid/catalog/final/list.m3u"),
                    new EmptyResponseOwner(),
                    null,
                    null,
                ]);
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
