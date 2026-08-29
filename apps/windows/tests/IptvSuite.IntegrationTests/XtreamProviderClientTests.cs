using System.Reflection;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using IptvSuite.Application;
using IptvSuite.Domain;
using IptvSuite.Infrastructure;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace IptvSuite.IntegrationTests;

[TestClass]
public sealed class XtreamProviderClientTests
{
    private static readonly string[] AccountLiveCatalogActions =
        ["get_account_info", "get_live_categories", "get_live_streams"];
    private static readonly string[] ContentCatalogActions =
    [
        "get_account_info",
        "get_live_categories",
        "get_live_streams",
        "get_vod_categories",
        "get_vod_streams",
        "get_series_categories",
        "get_series",
    ];
    private static readonly string[] ContentCatalogAndSeriesDetailsActions =
        [.. ContentCatalogActions, "get_series_info"];
    private static readonly string[] AccountOnlyActions = ["get_account_info"];
    private static readonly string[] AccountAndLiveCategoryActions =
        ["get_account_info", "get_live_categories"];

    [TestMethod]
    public async Task ProtectedCredentialsProduceOnlyAccountAndLiveCatalogRequests()
    {
        using var store = new CredentialMemoryStore();
        ContentSource source = await CreateSourceAsync(store);
        var transport = new ScriptedTransport(
            """{"user_info":{"auth":1}}""",
            """[{"category_id":"1","category_name":"News"}]""",
            """[{"stream_id":"7","name":"Synthetic","category_id":"1","direct_source":"https://ignored.invalid"}]""");
        var client = new XtreamProviderClient(store, transport);

        DomainResult<XtreamLiveCatalog> result = await client.LoadLiveCatalogAsync(source);

        Assert.IsTrue(result.IsSuccess);
        Assert.HasCount(1, result.Value!.Categories.Items);
        Assert.HasCount(1, result.Value.Streams.Items);
        CollectionAssert.AreEqual(
            AccountLiveCatalogActions,
            transport.Actions);
        Assert.IsTrue(transport.AllRequestsUsedHttps);
        Assert.IsTrue(transport.AllRequestsUsedPlayerApi);
        Assert.IsTrue(transport.AllRequestsContainedEncodedSyntheticCredentials);
        Assert.IsTrue(transport.AllRequestsUsedExplicitPrivateSourcePolicy);
        Assert.IsTrue(transport.AllRequestsRejectRedirects);
        Assert.IsTrue(store.LastIssuedLeaseMemory.Span.IndexOfAnyExcept((byte)0) < 0);
        Assert.AreEqual("[XTREAM-LIVE-CATALOG]", result.Value.ToString());
    }

    [TestMethod]
    public async Task ExplicitContentLoadUsesSeparateLiveVodAndSeriesEndpoints()
    {
        using var store = new CredentialMemoryStore();
        ContentSource source = await CreateSourceAsync(store);
        var transport = new ScriptedTransport(
            """{"user_info":{"auth":1}}""",
            """[{"category_id":"1","category_name":"Live"}]""",
            """[{"stream_id":"2","name":"Channel"}]""",
            """[{"category_id":"3","category_name":"Movies"}]""",
            """[{"stream_id":"4","name":"Movie","category_id":"3","container_extension":"mp4"}]""",
            """[{"category_id":"5","category_name":"Series"}]""",
            """[{"series_id":"6","name":"Series","category_id":"5"}]"""
        );
        var client = new XtreamProviderClient(store, transport);

        DomainResult<XtreamContentCatalog> result = await client.LoadContentCatalogAsync(source);

        Assert.IsTrue(result.IsSuccess);
        CollectionAssert.AreEqual(
            ContentCatalogActions,
            transport.Actions);
        Assert.AreEqual(ContentKind.LiveTv, result.Value!.LiveCategories.Items.Single().ContentKind);
        Assert.AreEqual(ContentKind.Movie, result.Value.MovieCategories.Items.Single().ContentKind);
        Assert.AreEqual(ContentKind.Series, result.Value.SeriesCategories.Items.Single().ContentKind);
        Assert.HasCount(1, result.Value.Movies.Items);
        Assert.HasCount(1, result.Value.Series.Items);
        Assert.AreEqual("[XTREAM-CONTENT-CATALOG]", result.Value.ToString());
    }

    [TestMethod]
    public async Task ContentEndpointsUseDistinctHardResponseBudgets()
    {
        using var store = new CredentialMemoryStore();
        ContentSource source = await CreateSourceAsync(store);
        var transport = new ScriptedTransport(
            """{"user_info":{"auth":1}}""",
            "[]",
            "[]",
            "[]",
            "[]",
            "[]",
            "[]",
            """{"seasons":[],"episodes":{}}""");
        var client = new XtreamProviderClient(store, transport);

        DomainResult<XtreamContentCatalog> catalog = await client.LoadContentCatalogAsync(source);
        DomainResult<XtreamSeriesDetails> details = await client.LoadSeriesDetailsAsync(
            source,
            ProviderItemKey.Create("synthetic-series").Value);

        Assert.IsTrue(catalog.IsSuccess);
        Assert.IsTrue(details.IsSuccess);
        CollectionAssert.AreEqual(
            new[]
            {
                64 * 1024,
                1024 * 1024,
                64 * 1024 * 1024,
                1024 * 1024,
                64 * 1024 * 1024,
                1024 * 1024,
                64 * 1024 * 1024,
                16 * 1024 * 1024,
            },
            transport.MaximumResponseBytes);
        CollectionAssert.AreEqual(
            ContentCatalogAndSeriesDetailsActions,
            transport.Actions);
    }

    [TestMethod]
    public async Task ExplicitHttpXtreamGrantUsesBoundedPlayerApiRequests()
    {
        using var store = new CredentialMemoryStore();
        ContentSource source = await CreateSourceAsync(
            store,
            allowInsecureHttp: true,
            locator: "http://fixture.invalid/get.php?type=m3u_plus&output=ts");
        var transport = new ScriptedTransport(
            """{"user_info":{"auth":1}}""",
            "[]",
            "[]");
        var client = new XtreamProviderClient(store, transport);

        DomainResult<XtreamLiveCatalog> result = await client.LoadLiveCatalogAsync(source);

        Assert.IsTrue(result.IsSuccess);
        Assert.IsTrue(transport.Schemes.All(scheme => scheme == Uri.UriSchemeHttp));
        Assert.IsTrue(transport.Paths.All(path => path == "/player_api.php"));
        Assert.IsTrue(transport.AllRequestsUsedExplicitPrivateSourcePolicy);
        Assert.IsTrue(transport.AllRequestsRejectRedirects);
    }

    [TestMethod]
    [Timeout(15_000)]
    public async Task ExplicitHttpXtreamGrantReachesOnlyTheSyntheticLoopbackPlayerApi()
    {
        Dictionary<string, FixtureHttpResponse> routes = new(StringComparer.Ordinal)
        {
            ["/player_api.php"] = new FixtureHttpResponse(
                200,
                "application/json",
                Encoding.UTF8.GetBytes("""{"user_info":{"auth":1}}""")),
        };
        await using LocalHttpFixtureServer server = await LocalHttpFixtureServer.StartAsync(routes);
        using var store = new CredentialMemoryStore();
        ContentSource source = await CreateSourceAsync(
            store,
            allowInsecureHttp: true,
            locator: new Uri(server.BaseAddress, "player_api.php").AbsoluteUri);
        using var transport = new BoundedHttpTransport();
        var client = new XtreamProviderClient(store, transport);

        DomainResult<XtreamLiveCatalog> result = await client.LoadLiveCatalogAsync(source);

        Assert.IsFalse(result.IsSuccess);
        Assert.AreEqual(DomainErrorCode.XtreamLiveCatalogResponseUnsupported, result.Error!.Code);
        Assert.HasCount(2, server.Requests);
        Assert.IsTrue(server.Requests.All(request => request.Path == "/player_api.php"));
    }

    [TestMethod]
    [Timeout(15_000)]
    public async Task XtreamClientRejectsSyntheticLoopbackRedirectBeforeCredentialReplay()
    {
        Dictionary<string, FixtureHttpResponse> routes = new(StringComparer.Ordinal)
        {
            ["/player_api.php"] = new FixtureHttpResponse(
                302,
                "application/json",
                ReadOnlyMemory<byte>.Empty,
                RedirectLocation: "/redirected"),
            ["/redirected"] = new FixtureHttpResponse(
                200,
                "application/json",
                Encoding.UTF8.GetBytes("""{"user_info":{"auth":1}}""")),
        };
        await using LocalHttpFixtureServer server = await LocalHttpFixtureServer.StartAsync(routes);
        using var store = new CredentialMemoryStore();
        ContentSource source = await CreateSourceAsync(
            store,
            allowInsecureHttp: true,
            locator: new Uri(server.BaseAddress, "player_api.php").AbsoluteUri);
        using var transport = new BoundedHttpTransport();
        var client = new XtreamProviderClient(store, transport);

        DomainResult<XtreamLiveCatalog> result = await client.LoadLiveCatalogAsync(source);

        Assert.IsFalse(result.IsSuccess);
        Assert.AreEqual(DomainErrorCode.RemoteRequestRejected, result.Error!.Code);
        Assert.HasCount(1, server.Requests);
        Assert.AreEqual("/player_api.php", server.Requests[0].Path);
        Assert.IsTrue(store.LastIssuedLeaseMemory.Span.IndexOfAnyExcept((byte)0) < 0);
    }

    [TestMethod]
    [Timeout(30_000)]
    [SupportedOSPlatform("windows")]
    public async Task XtreamImportAndStoredRefreshPopulateExplicitTopLevelCatalogs()
    {
        using TemporaryDirectory temporary = TemporaryDirectory.Create("post-mvp-xtream-import");
        string databasePath = Path.Combine(temporary.FullPath, "catalog.db");
        using var store = new CredentialMemoryStore();
        ContentSource source = await CreateSourceAsync(store);
        string[] cycle =
        [
            """{"user_info":{"auth":1}}""",
            """[{"category_id":"1","category_name":"Live"}]""",
            """[{"stream_id":"2","name":"Channel","category_id":"1"}]""",
            """[{"category_id":"3","category_name":"Movies"}]""",
            """[{"stream_id":"4","name":"Movie","category_id":"3","container_extension":"mp4"}]""",
            """[{"category_id":"5","category_name":"Series"}]""",
            """[{"series_id":"6","name":"Series","category_id":"5"}]""",
        ];
        var transport = new ScriptedTransport([.. cycle, .. cycle]);
        var importer = new XtreamCatalogImportService(databasePath, store, transport);

        DomainResult<ContentCatalogCounts> imported = await importer.ImportAsync(source);
        DomainResult<ContentCatalogCounts> refreshed =
            await importer.RefreshFromStoredConfigurationAsync(source.Id);
        var liveBrowser = new SqliteCatalogQuery(databasePath);
        var contentBrowser = new SqliteContentCatalog(databasePath);
        IReadOnlyList<CatalogCategoryItem> liveCategories =
            await liveBrowser.ReadCategoriesAsync(source.Id);
        IReadOnlyList<CatalogCategoryItem> movieCategories =
            await contentBrowser.ReadCategoriesAsync(source.Id, ContentKind.Movie);
        IReadOnlyList<CatalogCategoryItem> seriesCategories =
            await contentBrowser.ReadCategoriesAsync(source.Id, ContentKind.Series);

        Assert.IsTrue(imported.IsSuccess);
        Assert.IsTrue(refreshed.IsSuccess);
        Assert.AreEqual(new ContentCatalogCounts(1, 1, 1, 0), imported.Value);
        Assert.AreEqual(imported.Value, refreshed.Value);
        Assert.HasCount(1, liveCategories);
        Assert.AreEqual("Live", liveCategories[0].Name);
        Assert.HasCount(1, movieCategories);
        Assert.AreEqual("Movies", movieCategories[0].Name);
        Assert.HasCount(1, seriesCategories);
        Assert.AreEqual("Series", seriesCategories[0].Name);
        CollectionAssert.AreEqual(
            Enumerable.Repeat(
                    ContentCatalogActions,
                    2)
                .SelectMany(actions => actions)
                .ToArray(),
            transport.Actions);
    }

    [TestMethod]
    [Timeout(30_000)]
    [SupportedOSPlatform("windows")]
    public async Task SuspiciousEmptyRefreshKeepsLastGoodSnapshotWhileCanonicalEmptyCanReplaceIt()
    {
        using TemporaryDirectory temporary = TemporaryDirectory.Create(
            "post-mvp-xtream-last-good");
        string databasePath = Path.Combine(temporary.FullPath, "catalog.db");
        using var store = new CredentialMemoryStore();
        ContentSource source = await CreateSourceAsync(store);
        string[] populated =
        [
            """{"user_info":{"auth":1}}""",
            """[{"category_id":"1","category_name":"Live"}]""",
            """[{"stream_id":"2","name":"Channel","category_id":"1"}]""",
            """[{"category_id":"3","category_name":"Movies"}]""",
            """[{"stream_id":"4","name":"Movie","category_id":"3"}]""",
            """[{"category_id":"5","category_name":"Series"}]""",
            """[{"series_id":"6","name":"Series","category_id":"5"}]""",
        ];
        string[] liveSentinel =
        [
            """{"user_info":{"auth":1}}""",
            "[]",
            "false",
            "[]",
            "[]",
            "[]",
            "[]",
        ];
        string[] allInvalidMovies =
        [
            """{"user_info":{"auth":1}}""",
            "[]",
            """[{"stream_id":"2","name":"Channel"}]""",
            "[]",
            "[{}]",
            "[]",
            "[]",
        ];
        string[] canonicalEmpty =
        [
            """{"user_info":{"auth":1}}""",
            "[]",
            "[]",
            "[]",
            "[]",
            "[]",
            "[]",
        ];
        var transport = new ScriptedTransport(
            [.. populated, .. liveSentinel, .. allInvalidMovies, .. canonicalEmpty]);
        var importer = new XtreamCatalogImportService(databasePath, store, transport);
        var catalog = new SqliteContentCatalog(databasePath);

        DomainResult<ContentCatalogCounts> imported = await importer.ImportAsync(source);
        DomainResult<ContentCatalogCounts> rejectedSentinel =
            await importer.RefreshFromStoredConfigurationAsync(source.Id);
        ContentCatalogCounts afterSentinel = await catalog.ReadCountsAsync(source.Id);
        DomainResult<ContentCatalogCounts> rejectedInvalid =
            await importer.RefreshFromStoredConfigurationAsync(source.Id);
        ContentCatalogCounts afterInvalid = await catalog.ReadCountsAsync(source.Id);
        DomainResult<ContentCatalogCounts> emptied =
            await importer.RefreshFromStoredConfigurationAsync(source.Id);

        Assert.AreEqual(new ContentCatalogCounts(1, 1, 1, 0), imported.Value);
        Assert.IsFalse(rejectedSentinel.IsSuccess);
        Assert.AreEqual(
            DomainErrorCode.XtreamLiveCatalogResponseUnsupported,
            rejectedSentinel.Error!.Code);
        Assert.AreEqual(imported.Value, afterSentinel);
        Assert.IsFalse(rejectedInvalid.IsSuccess);
        Assert.AreEqual(
            DomainErrorCode.XtreamMovieCatalogResponseUnsupported,
            rejectedInvalid.Error!.Code);
        Assert.AreEqual(imported.Value, afterInvalid);
        Assert.IsTrue(emptied.IsSuccess);
        Assert.AreEqual(new ContentCatalogCounts(0, 0, 0, 0), emptied.Value);
    }

    [TestMethod]
    [Timeout(30_000)]
    [SupportedOSPlatform("windows")]
    public async Task ProviderFailureBeforeCatalogWriteIsKnownNotCommitted()
    {
        using TemporaryDirectory temporary = TemporaryDirectory.Create("post-mvp-xtream-reject");
        using var store = new CredentialMemoryStore();
        ContentSource source = await CreateSourceAsync(store);
        var transport = new ScriptedTransport("""{"user_info":{"auth":0}}""");
        var importer = new XtreamCatalogImportService(
            Path.Combine(temporary.FullPath, "catalog.db"),
            store,
            transport);

        XtreamCatalogImportResult result = await importer.ImportWithDispositionAsync(source);

        Assert.AreEqual(CatalogImportCommitDisposition.NotCommitted, result.Disposition);
        Assert.AreEqual(DomainErrorCode.AuthenticationRejected, result.Error!.Code);
        Assert.IsNull(result.Counts);
    }

    [TestMethod]
    [Timeout(30_000)]
    [SupportedOSPlatform("windows")]
    public async Task CancellationBeforeCatalogActivationIsKnownNotCommitted()
    {
        using TemporaryDirectory temporary = TemporaryDirectory.Create(
            "post-mvp-xtream-cancel");
        using var store = new CredentialMemoryStore();
        ContentSource source = await CreateSourceAsync(store);
        var importer = new XtreamCatalogImportService(
            Path.Combine(temporary.FullPath, "catalog.db"),
            store,
            new ScriptedTransport("{}"));
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();

        XtreamCatalogImportResult result = await importer.ImportWithDispositionAsync(
            source,
            cancellation.Token);

        Assert.AreEqual(CatalogImportCommitDisposition.NotCommitted, result.Disposition);
        Assert.AreEqual(DomainErrorCode.OperationCancelled, result.Error!.Code);
        Assert.IsNull(result.Counts);
    }

    [TestMethod]
    public async Task SeriesHierarchyIsFetchedLazilyForOnlyTheSelectedSeries()
    {
        using var store = new CredentialMemoryStore();
        ContentSource source = await CreateSourceAsync(store);
        var transport = new ScriptedTransport("""
            {
              "seasons":[{"id":"season-1","season_number":1,"name":"Season 1"}],
              "episodes":{"1":[{"id":"episode-1","episode_num":1,"title":"Episode 1"}]}
            }
            """);
        var client = new XtreamProviderClient(store, transport);

        DomainResult<XtreamSeriesDetails> result = await client.LoadSeriesDetailsAsync(
            source,
            ProviderItemKey.Create("series-42").Value);

        Assert.IsTrue(result.IsSuccess);
        Assert.HasCount(1, transport.Actions);
        Assert.AreEqual("get_series_info", transport.Actions[0]);
        Assert.HasCount(1, result.Value!.Episodes);
    }

    [TestMethod]
    [Timeout(30_000)]
    [SupportedOSPlatform("windows")]
    public async Task SelectedSeriesDetailRefreshPersistsOnlyTheRequestedHierarchy()
    {
        using TemporaryDirectory temporary = TemporaryDirectory.Create("post-mvp-series-detail");
        string databasePath = Path.Combine(temporary.FullPath, "catalog.db");
        using var store = new CredentialMemoryStore();
        ContentSource source = await CreateSourceAsync(store);
        var transport = new ScriptedTransport(
            """{"user_info":{"auth":1}}""",
            "[]",
            "[]",
            "[]",
            "[]",
            """[{"category_id":"5","category_name":"Series"}]""",
            """
            [
              {"series_id":"series-1","name":"Alpha","category_id":"5"},
              {"series_id":"series-2","name":"Beta","category_id":"5"}
            ]
            """,
            """
            {
              "episodes":{
                "2":[{
                  "id":"episode-201",
                  "episode_num":1,
                  "title":"Synthetic episode",
                  "container_extension":"mkv",
                  "info":{"duration_secs":3600}
                }]
              }
            }
            """);
        var importer = new XtreamCatalogImportService(databasePath, store, transport);
        DomainResult<ContentCatalogCounts> imported = await importer.ImportAsync(source);
        var browser = new SqliteContentCatalog(databasePath);
        ContentPage<ContentSeriesItem> series = await browser.ReadSeriesAsync(
            source.Id,
            categoryId: null,
            searchText: null,
            offset: 0,
            limit: 10);
        var detailService = new XtreamSeriesDetailService(databasePath, store, transport);

        DomainResult<SeriesDetailRefreshResult> refreshed = await detailService.RefreshAsync(
            source.Id,
            series.Items[0].SeriesId);
        IReadOnlyList<ContentSeasonItem> selectedSeasons = await browser.ReadSeasonsAsync(
            source.Id,
            series.Items[0].SeriesId);
        IReadOnlyList<ContentSeasonItem> unselectedSeasons = await browser.ReadSeasonsAsync(
            source.Id,
            series.Items[1].SeriesId);
        IReadOnlyList<ContentEpisodeItem> episodes = await browser.ReadEpisodesAsync(
            source.Id,
            selectedSeasons[0].SeasonId);

        Assert.IsTrue(imported.IsSuccess);
        Assert.IsTrue(refreshed.IsSuccess);
        Assert.AreEqual(1, refreshed.Value!.SeasonCount);
        Assert.AreEqual(1, refreshed.Value.EpisodeCount);
        Assert.HasCount(1, selectedSeasons);
        Assert.AreEqual(2, selectedSeasons[0].Number);
        Assert.IsEmpty(unselectedSeasons);
        Assert.HasCount(1, episodes);
        Assert.AreEqual(TimeSpan.FromHours(1), episodes[0].Duration);
        Assert.AreEqual("get_series_info", transport.Actions[^1]);
        Assert.AreEqual("series-1", transport.SeriesIdentifiers[^1]);
    }

    [TestMethod]
    public async Task BodyLevelAuthenticationFailureStopsBeforeLiveEndpoints()
    {
        using var store = new CredentialMemoryStore();
        ContentSource source = await CreateSourceAsync(store);
        var transport = new ScriptedTransport("""{"user_info":{"auth":"0"}}""");
        var client = new XtreamProviderClient(store, transport);

        DomainResult<XtreamLiveCatalog> result = await client.LoadLiveCatalogAsync(source);

        Assert.IsFalse(result.IsSuccess);
        Assert.AreEqual(DomainErrorCode.AuthenticationRejected, result.Error!.Code);
        CollectionAssert.AreEqual(AccountOnlyActions, transport.Actions);
    }

    [TestMethod]
    public async Task AccountCompatibilityProfileFallsBackInABoundedExactOrder()
    {
        using var store = new CredentialMemoryStore();
        ContentSource source = await CreateSourceAsync(store);
        const string unsupported = """{"provider_message":"synthetic"}""";
        var transport = new ScriptedTransport(
            unsupported,
            unsupported,
            """{"user_info":{"auth":1}}""",
            "[]",
            "[]");
        var client = new XtreamProviderClient(store, transport);

        DomainResult<XtreamLiveCatalog> result = await client.LoadLiveCatalogAsync(source);

        Assert.IsTrue(result.IsSuccess);
        CollectionAssert.AreEqual(
            new[]
            {
                "get_account_info",
                string.Empty,
                "get_profile",
                "get_live_categories",
                "get_live_streams",
            },
            transport.Actions);
        Assert.IsTrue(transport.AllRequestsUsedExplicitPrivateSourcePolicy);
        Assert.IsTrue(transport.AllRequestsRejectRedirects);
    }

    [TestMethod]
    public async Task HttpAuthenticationFailureMapsSafelyAndStopsBeforeParsing()
    {
        using var store = new CredentialMemoryStore();
        ContentSource source = await CreateSourceAsync(store);
        var transport = new ScriptedTransport(HttpTransportResult.Failed(
            HttpTransportFailure.AuthenticationRejected,
            HttpTransportRetryability.Never,
            401));
        var client = new XtreamProviderClient(store, transport);

        DomainResult<XtreamLiveCatalog> result = await client.LoadLiveCatalogAsync(source);

        Assert.IsFalse(result.IsSuccess);
        Assert.AreEqual(DomainErrorCode.AuthenticationRejected, result.Error!.Code);
        Assert.AreEqual("[DOMAIN-RESULT:AuthenticationRejected]", result.ToString());
    }

    [TestMethod]
    public async Task TransportFailuresMapToStableDomainErrors()
    {
        (HttpTransportFailure Transport, HttpTransportRetryability Retry, int Status, DomainErrorCode Expected)[] cases =
        [
            (HttpTransportFailure.AuthenticationRejected, HttpTransportRetryability.Never, 403,
                DomainErrorCode.AuthenticationRejected),
            (HttpTransportFailure.ResourceNotFound, HttpTransportRetryability.Never, 404,
                DomainErrorCode.RemoteResourceNotFound),
            (HttpTransportFailure.RequestRejected, HttpTransportRetryability.Never, 400,
                DomainErrorCode.RemoteRequestRejected),
            (HttpTransportFailure.RequestTimedOut, HttpTransportRetryability.BoundedTransient, 0,
                DomainErrorCode.RequestTimedOut),
            (HttpTransportFailure.RateLimited, HttpTransportRetryability.BoundedTransient, 429,
                DomainErrorCode.RequestRateLimited),
            (HttpTransportFailure.RemoteServiceUnavailable, HttpTransportRetryability.BoundedTransient, 503,
                DomainErrorCode.RemoteServiceUnavailable),
            (HttpTransportFailure.ResponseTooLarge, HttpTransportRetryability.Never, 200,
                DomainErrorCode.RemoteResponseTooLarge),
        ];

        foreach ((HttpTransportFailure transportFailure, HttpTransportRetryability retry, int status,
                     DomainErrorCode expected) in cases)
        {
            using var store = new CredentialMemoryStore();
            ContentSource source = await CreateSourceAsync(store);
            var transport = new ScriptedTransport(HttpTransportResult.Failed(
                transportFailure,
                retry,
                status));
            var client = new XtreamProviderClient(store, transport);

            DomainResult<XtreamLiveCatalog> result = await client.LoadLiveCatalogAsync(source);

            Assert.IsFalse(result.IsSuccess);
            Assert.AreEqual(expected, result.Error!.Code);
        }
    }

    [TestMethod]
    public async Task EmptyCatalogIsValidAndMalformedPartialResponseStopsDeterministically()
    {
        using var emptyStore = new CredentialMemoryStore();
        ContentSource emptySource = await CreateSourceAsync(emptyStore);
        var emptyClient = new XtreamProviderClient(
            emptyStore,
            new ScriptedTransport("""{"user_info":{"auth":true}}""", "[]", "[]"));
        DomainResult<XtreamLiveCatalog> empty = await emptyClient.LoadLiveCatalogAsync(emptySource);
        Assert.IsTrue(empty.IsSuccess);
        Assert.IsEmpty(empty.Value!.Categories.Items);
        Assert.IsEmpty(empty.Value.Streams.Items);

        using var malformedStore = new CredentialMemoryStore();
        ContentSource malformedSource = await CreateSourceAsync(malformedStore);
        var malformedTransport = new ScriptedTransport(
            """{"user_info":{"auth":true}}""",
            "{");
        var malformedClient = new XtreamProviderClient(malformedStore, malformedTransport);
        DomainResult<XtreamLiveCatalog> malformed =
            await malformedClient.LoadLiveCatalogAsync(malformedSource);
        Assert.IsFalse(malformed.IsSuccess);
        Assert.AreEqual(
            DomainErrorCode.XtreamLiveCatalogResponseUnsupported,
            malformed.Error!.Code);
        CollectionAssert.AreEqual(
            AccountAndLiveCategoryActions,
            malformedTransport.Actions);
    }

    [TestMethod]
    public async Task AuthenticatedEmptyProviderSentinelsDoNotHideAvailableTypedFamilies()
    {
        using var store = new CredentialMemoryStore();
        ContentSource source = await CreateSourceAsync(store);
        var transport = new ScriptedTransport(
            """{"user_info":{"auth":true}}""",
            "{}",
            "false",
            "[]",
            """[{"stream_id":"movie-4","name":"Synthetic movie"}]""",
            "null",
            "[]");
        var client = new XtreamProviderClient(store, transport);

        DomainResult<XtreamContentCatalog> result =
            await client.LoadContentCatalogAsync(source);

        Assert.IsTrue(result.IsSuccess);
        Assert.IsEmpty(result.Value!.LiveCategories.Items);
        Assert.IsEmpty(result.Value.LiveStreams.Items);
        Assert.IsEmpty(result.Value.MovieCategories.Items);
        Assert.HasCount(1, result.Value.Movies.Items);
        Assert.IsEmpty(result.Value.SeriesCategories.Items);
        Assert.IsEmpty(result.Value.Series.Items);
        CollectionAssert.AreEqual(
            ContentCatalogActions,
            transport.Actions);
    }

    [TestMethod]
    public async Task UnsupportedResponsesExposeOnlyTheSafeXtreamCatalogStage()
    {
        const string account = """{"user_info":{"auth":true}}""";
        const string unsupported = """{"provider_message":"credential-canary"}""";
        (string[] Responses, DomainErrorCode Expected)[] cases =
        [
            ([unsupported, unsupported, unsupported],
                DomainErrorCode.XtreamAccountResponseUnsupported),
            ([account, unsupported], DomainErrorCode.XtreamLiveCatalogResponseUnsupported),
            ([account, "[]", "[]", unsupported],
                DomainErrorCode.XtreamMovieCatalogResponseUnsupported),
            ([account, "[]", "[]", "[]", "[]", unsupported],
                DomainErrorCode.XtreamSeriesCatalogResponseUnsupported),
        ];

        foreach ((string[] responses, DomainErrorCode expected) in cases)
        {
            using var store = new CredentialMemoryStore();
            ContentSource source = await CreateSourceAsync(store);
            var client = new XtreamProviderClient(store, new ScriptedTransport(responses));

            DomainResult<XtreamContentCatalog> result =
                await client.LoadContentCatalogAsync(source);

            Assert.IsFalse(result.IsSuccess);
            Assert.AreEqual(expected, result.Error!.Code);
            Assert.IsFalse(
                result.ToString().Contains("credential-canary", StringComparison.Ordinal));
            Assert.IsFalse(
                result.Error.ResourceKey.Contains("credential-canary", StringComparison.Ordinal));
        }
    }

    [TestMethod]
    public async Task XtreamItemLimitFailureIsNotCollapsedIntoAFormatError()
    {
        string oversizedCatalog = string.Concat(
            "[",
            string.Join(
                ',',
                Enumerable.Repeat(
                    "{}",
                    XtreamProviderJsonParser.MaximumStreamCount + 1)),
            "]");
        using var store = new CredentialMemoryStore();
        ContentSource source = await CreateSourceAsync(store);
        var client = new XtreamProviderClient(
            store,
            new ScriptedTransport(
                """{"user_info":{"auth":true}}""",
                "[]",
                oversizedCatalog));

        DomainResult<XtreamLiveCatalog> result =
            await client.LoadLiveCatalogAsync(source);

        Assert.IsFalse(result.IsSuccess);
        Assert.AreEqual(DomainErrorCode.PlaylistEntryLimitExceeded, result.Error!.Code);
    }

    [TestMethod]
    public async Task CancellationIsPreservedAndCredentialLeaseIsZeroed()
    {
        using var store = new CredentialMemoryStore();
        ContentSource source = await CreateSourceAsync(store);
        var client = new XtreamProviderClient(store, new CancellingTransport());

        await Assert.ThrowsExactlyAsync<OperationCanceledException>(async () =>
            await client.LoadLiveCatalogAsync(source));
        Assert.IsFalse(store.LastIssuedLeaseMemory.IsEmpty);
        Assert.IsTrue(store.LastIssuedLeaseMemory.Span.IndexOfAnyExcept((byte)0) < 0);
    }

    private static async Task<ContentSource> CreateSourceAsync(
        CredentialMemoryStore store,
        bool allowInsecureHttp = false,
        string locator = "https://fixture.invalid/provider")
    {
        SourceId sourceId = SourceId.Generate();
        var protection = new SourceDraftProtectionService(store);
        DomainResult<ValidatedSourceDraft> draft = allowInsecureHttp
            ? await protection.ProtectXtreamAllowingInsecureHttpAsync(
                sourceId,
                "Synthetic source",
                locator,
                "synthetic-user",
                "synthetic password")
            : await protection.ProtectXtreamAsync(
                sourceId,
                "Synthetic source",
                locator,
                "synthetic-user",
                "synthetic password");
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

    private sealed class ScriptedTransport : IHttpTransport
    {
        private static readonly PropertyInfo RequestUriProperty = typeof(HttpTransportRequest)
            .GetProperty("RequestUri", BindingFlags.Instance | BindingFlags.NonPublic)!;
        private static readonly PropertyInfo EndpointAddressPolicyProperty = typeof(HttpTransportRequest)
            .GetProperty("EndpointAddressPolicy", BindingFlags.Instance | BindingFlags.NonPublic)!;
        private static readonly PropertyInfo RedirectPolicyProperty = typeof(HttpTransportRequest)
            .GetProperty("RedirectPolicy", BindingFlags.Instance | BindingFlags.NonPublic)!;
        private readonly Queue<HttpTransportResult> _responses;

        internal ScriptedTransport(params string[] bodies)
            : this(bodies.Select(body => HttpTransportResult.Success(
                200,
                HttpResponseLease.CopyFrom(Encoding.UTF8.GetBytes(body)))).ToArray())
        {
        }

        internal ScriptedTransport(params HttpTransportResult[] responses)
        {
            _responses = new Queue<HttpTransportResult>(responses);
        }

        internal List<string> Actions { get; } = [];

        internal List<string?> SeriesIdentifiers { get; } = [];

        internal bool AllRequestsUsedHttps { get; private set; } = true;

        internal bool AllRequestsUsedPlayerApi { get; private set; } = true;

        internal bool AllRequestsContainedEncodedSyntheticCredentials { get; private set; } = true;

        internal bool AllRequestsUsedExplicitPrivateSourcePolicy { get; private set; } = true;

        internal bool AllRequestsRejectRedirects { get; private set; } = true;

        internal List<string> Schemes { get; } = [];

        internal List<string> Paths { get; } = [];

        internal List<int> MaximumResponseBytes { get; } = [];

        public ValueTask<HttpTransportResult> GetAsync(
            HttpTransportRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Uri uri = (Uri)RequestUriProperty.GetValue(request)!;
            MaximumResponseBytes.Add(request.MaximumResponseBytes);
            Schemes.Add(uri.Scheme);
            Paths.Add(uri.AbsolutePath);
            AllRequestsUsedExplicitPrivateSourcePolicy &= string.Equals(
                EndpointAddressPolicyProperty.GetValue(request)?.ToString(),
                "ExplicitPrivateSourceOrigin",
                StringComparison.Ordinal);
            AllRequestsRejectRedirects &= string.Equals(
                RedirectPolicyProperty.GetValue(request)?.ToString(),
                "RejectAll",
                StringComparison.Ordinal);
            AllRequestsUsedHttps &= uri.Scheme == Uri.UriSchemeHttps;
            AllRequestsUsedPlayerApi &= uri.AbsolutePath == "/provider/player_api.php";
            Dictionary<string, string> query = ParseQuery(uri.Query);
            Actions.Add(query.TryGetValue("action", out string? action) ? action : string.Empty);
            SeriesIdentifiers.Add(query.GetValueOrDefault("series_id"));
            AllRequestsContainedEncodedSyntheticCredentials &=
                query.GetValueOrDefault("username") == "synthetic-user" &&
                query.GetValueOrDefault("password") == "synthetic password";
            return ValueTask.FromResult(_responses.Dequeue());
        }

        private static Dictionary<string, string> ParseQuery(string query) => query
            .TrimStart('?')
            .Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(part => part.Split('=', 2))
            .ToDictionary(
                pair => Uri.UnescapeDataString(pair[0]),
                pair => Uri.UnescapeDataString(pair.Length == 2 ? pair[1] : string.Empty),
                StringComparer.Ordinal);
    }

    private sealed class CancellingTransport : IHttpTransport
    {
        public ValueTask<HttpTransportResult> GetAsync(
            HttpTransportRequest request,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromException<HttpTransportResult>(new OperationCanceledException(cancellationToken));
    }

    private sealed class CredentialMemoryStore : ISecretStore, IDisposable
    {
        private byte[]? _payload;
        private SecretReference? _reference;

        internal ReadOnlyMemory<byte> LastIssuedLeaseMemory { get; private set; }

        public ValueTask<SecretReferenceCreationResult> CreateCredentialsAsync(
            SourceId sourceId,
            ProtectedRecordOwner owner,
            ReadOnlyMemory<byte> value,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _payload = value.ToArray();
            _reference = SecretReference.Parse($"secret-ref-v1:{Guid.NewGuid():N}").Value!;
            return ValueTask.FromResult(SecretReferenceCreationResult.Succeeded(_reference));
        }

        public ValueTask<SecretStoreReadResult> ReadCredentialsAsync(
            SourceId sourceId,
            ProtectedRecordOwner owner,
            SecretReference reference,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_payload is null || !reference.Equals(_reference))
            {
                return ValueTask.FromResult(SecretStoreReadResult.Failed(
                    SecretStoreFailure.ProtectedRecordUnavailable));
            }

            SecretLease lease = SecretLease.CopyFrom(_payload);
            LastIssuedLeaseMemory = lease.Value;
            return ValueTask.FromResult(SecretStoreReadResult.Succeeded(lease));
        }

        public void Dispose()
        {
            if (_payload is not null)
            {
                CryptographicOperations.ZeroMemory(_payload);
                _payload = null;
            }
        }

        public ValueTask<ProtectedLocatorReferenceCreationResult> CreateLocatorAsync(
            SourceId sourceId, ProtectedValuePurpose purpose, ProtectedRecordOwner owner,
            ReadOnlyMemory<byte> value, CancellationToken cancellationToken = default) => throw Unused();

        public ValueTask<SecretStoreReadResult> ReadLocatorAsync(
            SourceId sourceId, ProtectedValuePurpose purpose, ProtectedRecordOwner owner,
            ProtectedLocatorReference reference, CancellationToken cancellationToken = default) => throw Unused();

        public ValueTask<SecretStoreOperationResult> UpdateCredentialsAsync(
            SourceId sourceId, ProtectedRecordOwner owner, SecretReference reference,
            ReadOnlyMemory<byte> value, CancellationToken cancellationToken = default) => throw Unused();

        public ValueTask<SecretStoreOperationResult> UpdateLocatorAsync(
            SourceId sourceId, ProtectedValuePurpose purpose, ProtectedRecordOwner owner,
            ProtectedLocatorReference reference, ReadOnlyMemory<byte> value,
            CancellationToken cancellationToken = default) => throw Unused();

        public ValueTask<SecretStoreOperationResult> DeleteCredentialsAsync(
            SourceId sourceId, ProtectedRecordOwner owner, SecretReference reference,
            CancellationToken cancellationToken = default) => throw Unused();

        public ValueTask<SecretStoreOperationResult> DeleteLocatorAsync(
            SourceId sourceId, ProtectedValuePurpose purpose, ProtectedRecordOwner owner,
            ProtectedLocatorReference reference, CancellationToken cancellationToken = default) => throw Unused();

        private static InvalidOperationException Unused() =>
            new("The test does not permit this secret-store operation.");
    }
}
