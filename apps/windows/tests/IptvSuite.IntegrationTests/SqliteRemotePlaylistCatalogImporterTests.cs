using System.Globalization;
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
    public async Task SequentialDistinctSourceImportsPreserveBothReadyCatalogGraphs()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using TemporaryDirectory temporary = TemporaryDirectory.Create(
            "m16-remote-import-two-sources");
        string databasePath = Path.Combine(temporary.FullPath, "catalog.db");
        using var store = new M4InMemorySecretStore();
        ContentSource firstSource = await CreateSourceAsync(
            store,
            "https://fixtures.invalid/catalog/first.m3u");
        ContentSource secondSource = await CreateSourceAsync(
            store,
            "https://fixtures.invalid/catalog/second.m3u");
        Assert.AreNotEqual(firstSource.Id, secondSource.Id);
        var transport = new QueueResponseTransport(
            "#EXTM3U\n#EXTINF:-1 tvg-id=\"first\" group-title=\"News\",First channel\n" +
            "https://fixtures.invalid/live/first.m3u8\n",
            "#EXTM3U\n#EXTINF:-1 tvg-id=\"second\" group-title=\"Sports\",Second channel\n" +
            "https://fixtures.invalid/live/second.m3u8\n");
        var importer = new SqliteRemotePlaylistCatalogImporter(databasePath, store, transport);
        var query = new SqliteCatalogQuery(databasePath);

        RemotePlaylistCatalogImportResult firstResult = await importer.ImportAsync(firstSource);

        Assert.AreEqual(CatalogImportCommitDisposition.Committed, firstResult.Disposition);
        Assert.AreEqual(1, firstResult.ImportedChannelCount);
        string firstActiveSnapshot = await ReadTextScalarAsync(
            databasePath,
            "SELECT active_snapshot_id FROM sources WHERE source_id = $source;",
            "$source",
            firstSource.Id.Value.ToString("N", CultureInfo.InvariantCulture));
        CatalogChannelPage firstChannelsBeforeSecondImport = await query.ReadChannelsAsync(
            firstSource.Id,
            categoryId: null,
            searchText: null,
            offset: 0,
            limit: 10);
        Assert.AreEqual(1, firstChannelsBeforeSecondImport.TotalCount);
        Assert.AreEqual("First channel", firstChannelsBeforeSecondImport.Items[0].Name);

        RemotePlaylistCatalogImportResult secondResult = await importer.ImportAsync(secondSource);

        Assert.AreEqual(CatalogImportCommitDisposition.Committed, secondResult.Disposition);
        Assert.AreEqual(1, secondResult.ImportedChannelCount);
        IReadOnlyList<CatalogSourceItem> sources = await query.ReadSourcesAsync();
        Assert.HasCount(2, sources);
        CollectionAssert.AreEquivalent(
            new[] { firstSource.Id, secondSource.Id },
            sources.Select(source => source.SourceId).ToArray());
        Assert.AreEqual(2L, await ReadScalarAsync(
            databasePath,
            "SELECT count(*) FROM snapshots;"));
        Assert.AreEqual(
            firstActiveSnapshot,
            await ReadTextScalarAsync(
                databasePath,
                "SELECT active_snapshot_id FROM sources WHERE source_id = $source;",
                "$source",
                firstSource.Id.Value.ToString("N", CultureInfo.InvariantCulture)));

        CatalogChannelPage firstChannelsAfterSecondImport = await query.ReadChannelsAsync(
            firstSource.Id,
            categoryId: null,
            searchText: null,
            offset: 0,
            limit: 10);
        CatalogChannelPage secondChannels = await query.ReadChannelsAsync(
            secondSource.Id,
            categoryId: null,
            searchText: null,
            offset: 0,
            limit: 10);
        Assert.AreEqual(1, firstChannelsAfterSecondImport.TotalCount);
        Assert.AreEqual("First channel", firstChannelsAfterSecondImport.Items[0].Name);
        Assert.AreEqual(1, secondChannels.TotalCount);
        Assert.AreEqual("Second channel", secondChannels.Items[0].Name);
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task HttpSourceImportPersistsOnlySafeEndpointAndProtectedLocators()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using TemporaryDirectory temporary = TemporaryDirectory.Create("http-remote-import-success");
        string databasePath = Path.Combine(temporary.FullPath, "catalog.db");
        using var store = new M4InMemorySecretStore();
        const string sourceLocator =
            "http://fixtures.invalid/catalog/list.m3u?token=synthetic-source-token";
        const string httpChannelLocator =
            "http://fixtures.invalid/live/first.ts?token=synthetic-channel-token";
        ContentSource source = await CreateSourceAsync(
            store,
            sourceLocator,
            allowInsecureHttp: true);
        var transport = new QueueResponseTransport(
            new Uri("http://fixtures.invalid/catalog/final/list.m3u"),
            $"#EXTM3U\n#EXTINF:-1 tvg-id=\"http\" tvg-logo=\"http://fixtures.invalid/logo.png\" group-title=\"News\",HTTP\n" +
            $"{httpChannelLocator}\n" +
            "#EXTINF:-1 tvg-id=\"https\" group-title=\"News\",HTTPS\n" +
            "https://media.invalid/live/secure.m3u8\n");
        var importer = new SqliteRemotePlaylistCatalogImporter(databasePath, store, transport);

        RemotePlaylistCatalogImportResult result = await importer.ImportAsync(source);

        Assert.AreEqual(CatalogImportCommitDisposition.Committed, result.Disposition);
        Assert.AreEqual(2, result.ImportedChannelCount);
        Assert.AreEqual(Uri.UriSchemeHttp, transport.RequestScheme);
        Assert.AreEqual(1L, await ReadScalarAsync(
            databasePath,
            "SELECT count(*) FROM sources WHERE endpoint_scheme = 'http' AND endpoint_port = 80;"));
        Assert.AreEqual(0L, await ReadScalarAsync(
            databasePath,
            "SELECT count(*) FROM channels WHERE logo_reference IS NOT NULL;"));
        IReadOnlyList<CatalogSourceItem> sources = await new SqliteCatalogQuery(databasePath)
            .ReadSourcesAsync();
        Assert.HasCount(1, sources);
        Assert.IsTrue(sources[0].UsesInsecureHttp);
        await AssertDirectoryDoesNotContainAsync(temporary.FullPath, sourceLocator);
        await AssertDirectoryDoesNotContainAsync(temporary.FullPath, httpChannelLocator);
    }

    [TestMethod]
    [Timeout(120_000)]
    public async Task HttpOnboardingStreamsPlaylistBeyond32MiBWithOneCredentialBearingRequest()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using TemporaryDirectory temporary = TemporaryDirectory.Create(
            "http-remote-onboarding-single-request");
        string databasePath = Path.Combine(temporary.FullPath, "catalog.db");
        using var store = new M4InMemorySecretStore();
        byte[] playlist = CreatePlaylistBeyond32MiB();
        Assert.IsGreaterThan(32 * 1024 * 1024, playlist.Length);
        Assert.IsLessThan(128 * 1024 * 1024, playlist.Length);
        var routes = new Dictionary<string, FixtureHttpResponse>(StringComparer.Ordinal)
        {
            ["/get.php"] = new(200, "audio/x-mpegurl", playlist),
        };
        await using LocalHttpFixtureServer server = await LocalHttpFixtureServer.StartAsync(routes);
        string sourceLocator = new Uri(
            server.BaseAddress,
            "get.php?username=synthetic-user&password=synthetic-password&type=m3u_plus&output=ts")
            .AbsoluteUri;
        using var transport = new BoundedHttpTransport();
        var importer = new SqliteRemotePlaylistCatalogImporter(databasePath, store, transport);
        var onboarding = new RemotePlaylistSourceOnboardingService(
            store,
            importer,
            TimeProvider.System);

        DomainResult<RemotePlaylistSourceOnboardingResult> result =
            await onboarding.AddAllowingInsecureHttpAsync(
                "Synthetic HTTP source",
                sourceLocator);

        Assert.IsTrue(result.IsSuccess, result.Error?.ToString());
        Assert.AreEqual(1, result.Value!.ImportedChannelCount);
        using (var responseCompletionTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
        {
            while (server.CompletedResponseCount < 1)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(10), responseCompletionTimeout.Token);
            }
        }

        Assert.AreEqual(1, server.RequestCount);
        Assert.AreEqual(1, server.CompletedResponseCount);
        Assert.AreEqual((long)playlist.Length, server.CompletedBodyBytes);
        Assert.HasCount(1, server.Requests);
        Assert.AreEqual("GET", server.Requests[0].Method);
        Assert.AreEqual("/get.php", server.Requests[0].Path);
        await AssertDirectoryDoesNotContainAsync(temporary.FullPath, sourceLocator);
    }

    private static byte[] CreatePlaylistBeyond32MiB()
    {
        const int minimumBodyBytes = 33 * 1024 * 1024;
        byte[] boundedCommentLine = Encoding.ASCII.GetBytes(
            $"#{new string('p', 8_191)}\n");
        ReadOnlySpan<byte> header = "#EXTM3U url-tvg=\"https://guide.invalid/epg.xml\"\n"u8;
        ReadOnlySpan<byte> channel =
            "#EXTINF:-1 tvg-id=\"single-get\" group-title=\"News\",Synthetic\n"u8;
        ReadOnlySpan<byte> locator = "/live/synthetic.ts\n"u8;
        using var playlist = new MemoryStream(minimumBodyBytes + 16_384);
        playlist.Write(header);
        while (playlist.Length < minimumBodyBytes)
        {
            playlist.Write(boundedCommentLine);
        }

        playlist.Write(channel);
        playlist.Write(locator);
        return playlist.ToArray();
    }

    [TestMethod]
    [Timeout(180_000)]
    public async Task HttpXtreamShapedEntryLimitRetainsCommittedWarningAfterTeardownFailure()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using TemporaryDirectory temporary = TemporaryDirectory.Create(
            "http-xtream-entry-limit");
        string databasePath = Path.Combine(temporary.FullPath, "catalog.db");
        using var store = new M4InMemorySecretStore();
        byte[] playlist = CreateXtreamShapedPlaylist(entryCount: 50_001);
        var routes = new Dictionary<string, FixtureHttpResponse>(StringComparer.Ordinal)
        {
            ["/get.php"] = new(200, "audio/x-mpegurl", playlist),
        };
        await using LocalHttpFixtureServer server = await LocalHttpFixtureServer.StartAsync(routes);
        string sourceLocator = new Uri(
            server.BaseAddress,
            "get.php?username=synthetic-user&password=synthetic-password&type=m3u_plus&output=ts")
            .AbsoluteUri;
        string firstChannelLocator = new Uri(
            server.BaseAddress,
            "/live/synthetic-user/synthetic-password/0.ts").AbsoluteUri;
        string omittedChannelLocator = new Uri(
            server.BaseAddress,
            "/live/synthetic-user/synthetic-password/50000.ts").AbsoluteUri;
        using var transport = new BoundedHttpTransport();
        IRemotePlaylistCatalogImporter importer = CreateFaultedImporter(
            databasePath,
            store,
            transport);
        var onboarding = new RemotePlaylistSourceOnboardingService(
            store,
            importer,
            TimeProvider.System);

        DomainResult<RemotePlaylistSourceOnboardingResult> result =
            await onboarding.AddAllowingInsecureHttpAsync(
                "Synthetic Xtream-shaped HTTP source",
                sourceLocator);

        Assert.IsTrue(result.IsSuccess, result.Error?.ToString());
        Assert.AreEqual(50_000, result.Value!.ImportedChannelCount);
        Assert.AreEqual(1, result.Value.WarningCount);
        Assert.IsTrue(result.Value.EntryLimitReached);
        Assert.AreEqual(50_000L, await ReadScalarAsync(
            databasePath,
            "SELECT count(*) FROM channels WHERE snapshot_id = (SELECT active_snapshot_id FROM sources);"));
        Assert.AreEqual(1L, await ReadScalarAsync(
            databasePath,
            "SELECT count(*) FROM snapshots WHERE item_count = 50000 AND warning_count = 1 AND state = 1;"));
        Assert.AreEqual(1L, await ReadScalarAsync(
            databasePath,
            "SELECT count(*) FROM sync_runs WHERE result_code = 0 AND parsed_count = 50000 " +
            "AND persisted_count = 50000 AND warning_count = 1 AND failure_code IS NULL;"));
        using (var responseCompletionTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
        {
            while (server.CompletedResponseCount < 1)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(10), responseCompletionTimeout.Token);
            }
        }

        Assert.AreEqual(1, server.RequestCount);
        Assert.AreEqual(1, server.CompletedResponseCount);
        Assert.AreEqual((long)playlist.Length, server.CompletedBodyBytes);
        await AssertDirectoryDoesNotContainAsync(
            temporary.FullPath,
            sourceLocator);
        await AssertDirectoryDoesNotContainAsync(
            temporary.FullPath,
            firstChannelLocator);
        await AssertDirectoryDoesNotContainAsync(
            temporary.FullPath,
            omittedChannelLocator);
    }

    private static byte[] CreateXtreamShapedPlaylist(int entryCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(entryCount);
        var playlist = new StringBuilder("#EXTM3U\n", entryCount * 192);
        for (int index = 0; index < entryCount; index++)
        {
            string identifier = index.ToString(CultureInfo.InvariantCulture);
            playlist.Append("#EXTINF:-1 tvg-id=\"live-")
                .Append(identifier)
                .Append("\" tvg-name=\"Synthetic Live ")
                .Append(identifier)
                .Append("\" group-title=\"Live\",Synthetic Live ")
                .Append(identifier)
                .Append("\n/live/synthetic-user/synthetic-password/")
                .Append(identifier)
                .Append(".ts\n");
        }

        return Encoding.UTF8.GetBytes(playlist.ToString());
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
        Assert.AreEqual(DomainErrorCode.PlaylistHlsManifestUnsupported, rejected.Error?.Code);
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
        string locator,
        bool allowInsecureHttp = false)
    {
        var protection = new SourceDraftProtectionService(store);
        DomainResult<ValidatedSourceDraft> draft = allowInsecureHttp
            ? await protection.ProtectRemotePlaylistAllowingInsecureHttpAsync(
                SourceId.Generate(),
                "Synthetic remote",
                locator)
            : await protection.ProtectRemotePlaylistAsync(
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

    private static async Task<string> ReadTextScalarAsync(
        string databasePath,
        string sql,
        string parameterName,
        string parameterValue)
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
        command.Parameters.AddWithValue(parameterName, parameterValue);
        object? value = await command.ExecuteScalarAsync();
        Assert.IsNotNull(value);
        return Convert.ToString(value, CultureInfo.InvariantCulture)!;
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

    private sealed class QueueResponseTransport : IStreamingHttpTransport
    {
        private readonly Queue<string> _bodies;
        private readonly Uri _effectiveUri;

        internal QueueResponseTransport(params string[] bodies)
            : this(new Uri("https://fixtures.invalid/catalog/final/list.m3u"), bodies)
        {
        }

        internal QueueResponseTransport(Uri effectiveUri, params string[] bodies)
        {
            _effectiveUri = effectiveUri ?? throw new ArgumentNullException(nameof(effectiveUri));
            _bodies = new Queue<string>(bodies);
        }

        internal string? RequestScheme { get; private set; }

        public ValueTask<HttpStreamingResult> GetStreamAsync(
            HttpTransportRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RequestScheme = ((Uri)request.GetType().GetProperty(
                "RequestUri",
                BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(request)!).Scheme;
            var stream = new MemoryStream(Encoding.UTF8.GetBytes(_bodies.Dequeue()), writable: false);
            ConstructorInfo constructor = typeof(HttpStreamingResponseLease).GetConstructors(
                BindingFlags.Instance | BindingFlags.NonPublic).Single();
            var lease = (HttpStreamingResponseLease)constructor.Invoke(
                [
                    stream,
                    _effectiveUri,
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
