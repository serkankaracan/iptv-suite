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
            "https://fixtures.invalid/live/channel.m3u8?opaque=canary-value&token=synthetic-password"u8.ToArray();
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
    public async Task HttpRemoteSourceAllowsOnlySameOriginHttpOrHttpsChannelLocators()
    {
        (string Locator, bool ExpectedSuccess)[] cases =
        [
            ("http://fixtures.invalid/live/same-origin.m3u8?stream=synthetic", true),
            ("http://other.invalid/live/cross-origin.m3u8", false),
            ("https://media.invalid/live/secure-upgrade.m3u8", true),
        ];

        for (int index = 0; index < cases.Length; index++)
        {
            using TemporaryDirectory temporary = TemporaryDirectory.Create(
                $"http-playback-origin-{index}");
            string databasePath = Path.Combine(temporary.FullPath, "catalog.db");
            await InitializeDatabaseAsync(databasePath);
            using var sourceStore = new M4InMemorySecretStore();
            ContentSource source = await CreateSourceAsync(
                sourceStore,
                $"http-{index}",
                "http://fixtures.invalid/catalog/list.m3u?token=synthetic",
                allowInsecureHttp: true);
            ResolverBatch batch = await CreateBatchAsync(
                Encoding.UTF8.GetBytes(cases[index].Locator),
                $"http-{index}",
                source);
            await ActivateAsync(databasePath, batch.Batch);

            ResolvedSource resolved = await ResolveAsync(
                databasePath,
                new PlaybackSelection(source.Id, batch.ChannelId));

            if (!cases[index].ExpectedSuccess)
            {
                Assert.AreEqual("InvalidLocator", resolved.Failure);
                Assert.IsNull(resolved.Lease);
                continue;
            }

            Assert.AreEqual("None", resolved.Failure);
            using SecretLease lease = resolved.Lease!;
            Assert.AreEqual(cases[index].Locator, Encoding.UTF8.GetString(lease.Value.Span));
        }
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task ActiveXtreamChannelBuildsOnlyTheBoundedLiveRouteAndDisposesCredentials()
    {
        (ChannelContainerHint Hint, string Extension, string ProviderItem, string EscapedItem)[] containers =
        [
            (
                ChannelContainerHint.Hls,
                "m3u8",
                "stream/hls?reserved#item",
                "stream%2Fhls%3Freserved%23item"),
            (ChannelContainerHint.MpegTs, "ts", "stream-ts", "stream-ts"),
        ];

        foreach ((
            ChannelContainerHint hint,
            string extension,
            string providerItem,
            string escapedItem) in containers)
        {
            using TemporaryDirectory temporary = TemporaryDirectory.Create(
                $"m11-playback-xtream-{extension}");
            using var store = new TrackingSecretStore();
            string databasePath = Path.Combine(temporary.FullPath, "catalog.db");
            await InitializeDatabaseAsync(databasePath);
            ContentSource source = await CreateXtreamSourceAsync(
                store,
                extension,
                "https://fixtures.invalid/provider?discarded=query-canary",
                "synthetic user",
                "p/ass?word#credential-canary");
            ResolverBatch batch = await CreateBatchAsync(
                locator: null,
                suffix: extension,
                existingSource: source,
                providerItem: true,
                containerHint: hint,
                providerItemValue: providerItem);
            await ActivateAsync(databasePath, batch.Batch);

            ResolvedSource resolved = await ResolveAsync(
                databasePath,
                new PlaybackSelection(source.Id, batch.ChannelId),
                secretStore: store);

            Assert.AreEqual("None", resolved.Failure);
            Assert.AreEqual(1, store.CredentialsReadCount);
            Assert.IsNotNull(store.LastCredentialsLease);
            Assert.ThrowsExactly<ObjectDisposedException>(
                () => _ = store.LastCredentialsLease.Value);
            Assert.AreEqual("[PLAYBACK-SOURCE-RESOLUTION:SUCCESS]", resolved.RawResult.ToString());
            string serialized = JsonSerializer.Serialize(
                resolved.RawResult,
                resolved.RawResult.GetType());
            Assert.DoesNotContain("fixtures.invalid", serialized, StringComparison.Ordinal);
            Assert.DoesNotContain("synthetic user", serialized, StringComparison.Ordinal);
            Assert.DoesNotContain("credential-canary", serialized, StringComparison.Ordinal);

            SecretLease locatorLease = resolved.Lease!;
            using (locatorLease)
            {
                Assert.AreEqual(
                    $"https://fixtures.invalid/provider/live/synthetic%20user/" +
                    $"p%2Fass%3Fword%23credential-canary/{escapedItem}.{extension}",
                    Encoding.UTF8.GetString(locatorLease.Value.Span));
            }

            Assert.ThrowsExactly<ObjectDisposedException>(() => _ = locatorLease.Value);
        }
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task ExplicitMovieAndEpisodeTargetsBuildDistinctOnDemandRoutes()
    {
        using TemporaryDirectory temporary = TemporaryDirectory.Create("post-mvp-vod-resolver");
        using var store = new TrackingSecretStore();
        string databasePath = Path.Combine(temporary.FullPath, "catalog.db");
        await InitializeDatabaseAsync(databasePath);
        ContentSource source = await CreateXtreamSourceAsync(
            store,
            "vod",
            "https://fixtures.invalid/provider",
            "synthetic user",
            "p/ass");
        ResolverBatch batch = await CreateBatchAsync(
            locator: null,
            suffix: "vod",
            existingSource: source,
            providerItem: true);
        await ActivateAsync(databasePath, batch.Batch);
        CategoryId movieCategoryId = CategoryId.Generate();
        CategoryId seriesCategoryId = CategoryId.Generate();
        Movie movie = Movie.Create(
            MovieId.Generate(),
            batch.SnapshotId,
            movieCategoryId,
            ProviderItemKey.Create("movie/item").Value,
            "Movie",
            "mp4",
            false).Value!;
        Series series = Series.Create(
            SeriesId.Generate(),
            batch.SnapshotId,
            seriesCategoryId,
            ProviderItemKey.Create("series-item").Value,
            "Series",
            false).Value!;
        Season season = Season.Create(
            SeasonId.Generate(), batch.SnapshotId, series.Id, 1, "Season 1").Value!;
        Episode episode = Episode.Create(
            EpisodeId.Generate(),
            batch.SnapshotId,
            season.Id,
            ProviderItemKey.Create("episode/item").Value,
            1,
            "Episode 1",
            "mkv",
            TimeSpan.FromMinutes(42)).Value!;
        var content = new SqliteContentCatalog(databasePath);
        await content.ReplaceActiveSnapshotContentAsync(new ContentCatalogMutation(
            source.Id,
            batch.SnapshotId,
            [
                ChannelCategory.Create(
                    movieCategoryId, batch.SnapshotId, "xtream:movie:test", "Movies", 1, false).Value!,
                ChannelCategory.Create(
                    seriesCategoryId, batch.SnapshotId, "xtream:series:test", "Series", 2, false).Value!,
            ],
            [movie],
            [series],
            [season],
            [episode]));

        ResolvedSource resolvedMovie = await ResolveAsync(
            databasePath,
            PlaybackSelection.ForTarget(source.Id, PlaybackTarget.Movie(movie.Id)),
            store);
        ResolvedSource resolvedEpisode = await ResolveAsync(
            databasePath,
            PlaybackSelection.ForTarget(source.Id, PlaybackTarget.Episode(episode.Id)),
            store);

        Assert.AreEqual("None", resolvedMovie.Failure);
        Assert.AreEqual("None", resolvedEpisode.Failure);
        using (SecretLease movieLease = resolvedMovie.Lease!)
        {
            Assert.AreEqual(
                "https://fixtures.invalid/provider/movie/synthetic%20user/p%2Fass/movie%2Fitem.mp4",
                Encoding.UTF8.GetString(movieLease.Value.Span));
        }
        using (SecretLease episodeLease = resolvedEpisode.Lease!)
        {
            Assert.AreEqual(
                "https://fixtures.invalid/provider/series/synthetic%20user/p%2Fass/episode%2Fitem.mkv",
                Encoding.UTF8.GetString(episodeLease.Value.Span));
        }
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task ExplicitHttpXtreamGrantBuildsOnlySameOriginPlaybackRoute()
    {
        using TemporaryDirectory temporary = TemporaryDirectory.Create("post-mvp-http-xtream-resolver");
        using var store = new TrackingSecretStore();
        string databasePath = Path.Combine(temporary.FullPath, "catalog.db");
        await InitializeDatabaseAsync(databasePath);
        ContentSource source = await CreateXtreamSourceAsync(
            store,
            "http",
            "http://127.0.0.1:18080/get.php?discarded=synthetic",
            "synthetic-user",
            "synthetic-password",
            allowInsecureHttp: true);
        ResolverBatch batch = await CreateBatchAsync(
            locator: null,
            suffix: "http",
            existingSource: source,
            providerItem: true,
            containerHint: ChannelContainerHint.MpegTs,
            providerItemValue: "stream-7");
        await ActivateAsync(databasePath, batch.Batch);

        ResolvedSource resolved = await ResolveAsync(
            databasePath,
            new PlaybackSelection(source.Id, batch.ChannelId),
            store);

        Assert.AreEqual("None", resolved.Failure);
        using SecretLease lease = resolved.Lease!;
        Assert.AreEqual(
            "http://127.0.0.1:18080/live/synthetic-user/synthetic-password/stream-7.ts",
            Encoding.UTF8.GetString(lease.Value.Span));
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
    public async Task ProviderItemOnRemoteSourceIsExplicitlyUnsupportedWithoutProducingALease()
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
    public async Task XtreamProviderKindContainerAndCanonicalItemAreExact()
    {
        (string Mutation, string ExpectedFailure)[] cases =
        [
            ("provider_item_kind = 2", "UnsupportedSource"),
            ("container_hint = NULL", "UnsupportedSource"),
            ("container_hint = 'Mp4'", "UnsupportedSource"),
            ("provider_item_id = '  stream-contract  '", "InvalidLocator"),
        ];

        for (int index = 0; index < cases.Length; index++)
        {
            using TemporaryDirectory temporary = TemporaryDirectory.Create(
                $"m11-playback-xtream-contract-{index}");
            using var store = new TrackingSecretStore();
            string databasePath = Path.Combine(temporary.FullPath, "catalog.db");
            await InitializeDatabaseAsync(databasePath);
            ContentSource source = await CreateXtreamSourceAsync(
                store,
                $"contract-{index}",
                "https://fixtures.invalid/provider",
                "synthetic-user",
                "synthetic-password");
            ResolverBatch batch = await CreateBatchAsync(
                locator: null,
                suffix: "contract",
                existingSource: source,
                providerItem: true);
            await ActivateAsync(databasePath, batch.Batch);
            await ExecuteAsync(
                databasePath,
                $"UPDATE channels SET {cases[index].Mutation} WHERE channel_id = $channel;",
                batch.ChannelId);

            await AssertFailureAsync(
                databasePath,
                new PlaybackSelection(source.Id, batch.ChannelId),
                cases[index].ExpectedFailure,
                store);
            Assert.AreEqual(
                0,
                store.CredentialsReadCount,
                "An invalid provider-item contract must be rejected before protected credentials are read.");
        }
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task XtreamCredentialsRequireExactOwnerReferenceEndpointAndPayload()
    {
        (string SourceMutation, string ExpectedFailure, int ExpectedReadCount)[] cases =
        [
            ($"source_kind = {(int)SourceKind.RemotePlaylist}", "UnsupportedSource", 0),
            ($"configuration_id = '{Guid.NewGuid():N}'", "Unavailable", 1),
            ($"configuration_reference = 'locator-ref-v1:{Guid.NewGuid():N}'", "InvalidLocator", 0),
            ($"configuration_reference = 'secret-ref-v1:{Guid.NewGuid():N}'", "Unavailable", 1),
            ("endpoint_scheme = 'http'", "InvalidLocator", 1),
            ("endpoint_host = 'other.invalid'", "InvalidLocator", 1),
            ("endpoint_port = 444", "InvalidLocator", 1),
        ];

        for (int index = 0; index < cases.Length; index++)
        {
            using TemporaryDirectory temporary = TemporaryDirectory.Create(
                $"m11-playback-xtream-binding-{index}");
            using var store = new TrackingSecretStore();
            string databasePath = Path.Combine(temporary.FullPath, "catalog.db");
            await InitializeDatabaseAsync(databasePath);
            ContentSource source = await CreateXtreamSourceAsync(
                store,
                $"binding-{index}",
                "https://fixtures.invalid/provider",
                "binding-user",
                "binding-password");
            ResolverBatch batch = await CreateBatchAsync(
                locator: null,
                suffix: $"binding-{index}",
                existingSource: source,
                providerItem: true);
            await ActivateAsync(databasePath, batch.Batch);
            await ExecuteSourceAsync(
                databasePath,
                $"UPDATE sources SET {cases[index].SourceMutation} WHERE source_id = $source;",
                source.Id);

            await AssertFailureAsync(
                databasePath,
                new PlaybackSelection(source.Id, batch.ChannelId),
                cases[index].ExpectedFailure,
                store);
            Assert.AreEqual(cases[index].ExpectedReadCount, store.CredentialsReadCount);
            if (cases[index].ExpectedFailure == "Unavailable")
            {
                Assert.IsNull(store.LastCredentialsLease);
            }
            if (store.LastCredentialsLease is not null)
            {
                Assert.ThrowsExactly<ObjectDisposedException>(
                    () => _ = store.LastCredentialsLease.Value);
            }
        }

        using TemporaryDirectory malformed = TemporaryDirectory.Create(
            "m11-playback-xtream-payload");
        using var malformedStore = new TrackingSecretStore();
        string malformedDatabase = Path.Combine(malformed.FullPath, "catalog.db");
        await InitializeDatabaseAsync(malformedDatabase);
        ContentSource malformedSource = await CreateXtreamSourceAsync(
            malformedStore,
            "malformed",
            "https://fixtures.invalid/provider",
            "payload-user",
            "payload-password");
        ResolverBatch malformedBatch = await CreateBatchAsync(
            locator: null,
            suffix: "malformed",
            existingSource: malformedSource,
            providerItem: true);
        await ActivateAsync(malformedDatabase, malformedBatch.Batch);
        var configuration = (XtreamSourceConfiguration)malformedSource.Configuration;
        SecretStoreOperationResult updated = await malformedStore.UpdateCredentialsAsync(
            malformedSource.Id,
            ProtectedRecordOwner.ForSourceConfiguration(configuration.ConfigurationId),
            configuration.CredentialsReference,
            "malformed-credential-payload"u8.ToArray());
        Assert.IsTrue(updated.IsSuccess);

        await AssertFailureAsync(
            malformedDatabase,
            new PlaybackSelection(malformedSource.Id, malformedBatch.ChannelId),
            "InvalidLocator",
            malformedStore);
        Assert.IsNotNull(malformedStore.LastCredentialsLease);
        Assert.ThrowsExactly<ObjectDisposedException>(
            () => _ = malformedStore.LastCredentialsLease.Value);
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
            "https://synthetic-user:synthetic-password@fixtures.invalid/live/channel.m3u8"u8.ToArray(),
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
                cancellationToken: cancellation.Token));

        Assert.IsFalse(File.Exists(databasePath));
    }

    private static async Task AssertFailureAsync(
        string databasePath,
        PlaybackSelection selection,
        string expectedFailure,
        ISecretStore? secretStore = null)
    {
        ResolvedSource result = await ResolveAsync(
            databasePath,
            selection,
            secretStore: secretStore);
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
        ISecretStore? secretStore = null,
        CancellationToken cancellationToken = default)
    {
        bool ownsSecretStore = secretStore is null;
        secretStore ??= new M4InMemorySecretStore();
        Assembly assembly = typeof(IptvSuite.Infrastructure.AssemblyMarker).Assembly;
        Type resolverType = assembly.GetType(
            "IptvSuite.Infrastructure.SqlitePlaybackSourceResolver",
            throwOnError: true)!;
        try
        {
            object resolver = Activator.CreateInstance(
                resolverType,
                BindingFlags.Instance | BindingFlags.NonPublic,
                binder: null,
                [databasePath, secretStore],
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
        finally
        {
            if (ownsSecretStore && secretStore is IDisposable disposable)
            {
                disposable.Dispose();
            }
        }
    }

    private static async Task<ResolverBatch> CreateBatchAsync(
        byte[]? locator,
        string suffix,
        ContentSource? existingSource = null,
        bool providerItem = false,
        ChannelContainerHint? containerHint = ChannelContainerHint.Hls,
        string? providerItemValue = null)
    {
        using var store = new M4InMemorySecretStore();
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
            string providerItemIdentifier = providerItemValue ?? $"stream-{suffix}";
            DomainResult<ProviderItemKey> providerKey = ProviderItemKey.Create(providerItemIdentifier);
            Assert.IsTrue(providerKey.IsSuccess);
            providerPlaybackKey = providerKey.Value;
            stableKey = ChannelStableKeyBuilder.FromProviderStreamId(
                source.Id,
                "xtream",
                providerItemIdentifier);
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
            containerHint,
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
            [
                source,
                snapshot.Value!,
                new[] { category.Value! },
                new[] { channel.Value! },
                locators,
                null,
            ])!;
        return new(batch, source, snapshotId, channelId);
    }

    private static async Task<ContentSource> CreateSourceAsync(
        M4InMemorySecretStore store,
        string suffix,
        string? locator = null,
        bool allowInsecureHttp = false)
    {
        var protection = new SourceDraftProtectionService(store);
        DomainResult<ValidatedSourceDraft> draft = allowInsecureHttp
            ? await protection.ProtectRemotePlaylistAllowingInsecureHttpAsync(
                SourceId.Generate(),
                $"Synthetic {suffix}",
                locator ?? $"https://fixtures.invalid/catalog/{suffix}.m3u")
            : await protection.ProtectRemotePlaylistAsync(
                SourceId.Generate(),
                $"Synthetic {suffix}",
                locator ?? $"https://fixtures.invalid/catalog/{suffix}.m3u");
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

    private static async Task<ContentSource> CreateXtreamSourceAsync(
        ISecretStore store,
        string suffix,
        string locator,
        string username,
        string password,
        bool allowInsecureHttp = false)
    {
        var protection = new SourceDraftProtectionService(store);
        DomainResult<ValidatedSourceDraft> draft = allowInsecureHttp
            ? await protection.ProtectXtreamAllowingInsecureHttpAsync(
                SourceId.Generate(),
                $"Synthetic {suffix}",
                locator,
                username,
                password)
            : await protection.ProtectXtreamAsync(
                SourceId.Generate(),
                $"Synthetic {suffix}",
                locator,
                username,
                password);
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

    private static async Task ExecuteSourceAsync(
        string databasePath,
        string sql,
        SourceId sourceId)
    {
        await using SqliteConnection connection = await OpenAsync(databasePath);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue(
            "$source",
            sourceId.Value.ToString("N"));
        await command.ExecuteNonQueryAsync();
    }

    private sealed record ResolverBatch(
        object Batch,
        ContentSource Source,
        SnapshotId SnapshotId,
        ChannelId ChannelId);

    private sealed record ResolvedSource(
        object RawResult,
        SecretLease? Lease,
        string Failure);

    private sealed class TrackingSecretStore : ISecretStore, IDisposable
    {
        private readonly M4InMemorySecretStore _inner = new();

        internal int CredentialsReadCount { get; private set; }

        internal SecretLease? LastCredentialsLease { get; private set; }

        public ValueTask<SecretReferenceCreationResult> CreateCredentialsAsync(
            SourceId sourceId,
            ProtectedRecordOwner owner,
            ReadOnlyMemory<byte> value,
            CancellationToken cancellationToken = default) =>
            _inner.CreateCredentialsAsync(sourceId, owner, value, cancellationToken);

        public ValueTask<ProtectedLocatorReferenceCreationResult> CreateLocatorAsync(
            SourceId sourceId,
            ProtectedValuePurpose purpose,
            ProtectedRecordOwner owner,
            ReadOnlyMemory<byte> value,
            CancellationToken cancellationToken = default) =>
            _inner.CreateLocatorAsync(sourceId, purpose, owner, value, cancellationToken);

        public async ValueTask<SecretStoreReadResult> ReadCredentialsAsync(
            SourceId sourceId,
            ProtectedRecordOwner owner,
            SecretReference reference,
            CancellationToken cancellationToken = default)
        {
            SecretStoreReadResult result = await _inner.ReadCredentialsAsync(
                sourceId,
                owner,
                reference,
                cancellationToken);
            CredentialsReadCount++;
            LastCredentialsLease = result.Lease;
            return result;
        }

        public ValueTask<SecretStoreReadResult> ReadLocatorAsync(
            SourceId sourceId,
            ProtectedValuePurpose purpose,
            ProtectedRecordOwner owner,
            ProtectedLocatorReference reference,
            CancellationToken cancellationToken = default) =>
            _inner.ReadLocatorAsync(sourceId, purpose, owner, reference, cancellationToken);

        public ValueTask<SecretStoreOperationResult> UpdateCredentialsAsync(
            SourceId sourceId,
            ProtectedRecordOwner owner,
            SecretReference reference,
            ReadOnlyMemory<byte> value,
            CancellationToken cancellationToken = default) =>
            _inner.UpdateCredentialsAsync(sourceId, owner, reference, value, cancellationToken);

        public ValueTask<SecretStoreOperationResult> UpdateLocatorAsync(
            SourceId sourceId,
            ProtectedValuePurpose purpose,
            ProtectedRecordOwner owner,
            ProtectedLocatorReference reference,
            ReadOnlyMemory<byte> value,
            CancellationToken cancellationToken = default) =>
            _inner.UpdateLocatorAsync(
                sourceId,
                purpose,
                owner,
                reference,
                value,
                cancellationToken);

        public ValueTask<SecretStoreOperationResult> DeleteCredentialsAsync(
            SourceId sourceId,
            ProtectedRecordOwner owner,
            SecretReference reference,
            CancellationToken cancellationToken = default) =>
            _inner.DeleteCredentialsAsync(sourceId, owner, reference, cancellationToken);

        public ValueTask<SecretStoreOperationResult> DeleteLocatorAsync(
            SourceId sourceId,
            ProtectedValuePurpose purpose,
            ProtectedRecordOwner owner,
            ProtectedLocatorReference reference,
            CancellationToken cancellationToken = default) =>
            _inner.DeleteLocatorAsync(sourceId, purpose, owner, reference, cancellationToken);

        public void Dispose() => _inner.Dispose();

        public override string ToString() => "[TRACKING-SECRET-STORE]";
    }
}
