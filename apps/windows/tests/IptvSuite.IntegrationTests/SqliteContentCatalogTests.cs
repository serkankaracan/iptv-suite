using IptvSuite.Application;
using IptvSuite.Domain;
using IptvSuite.Infrastructure;
using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace IptvSuite.IntegrationTests;

[TestClass]
public sealed class SqliteContentCatalogTests
{
    [TestMethod]
    [Timeout(30_000)]
    public async Task ActiveSnapshotContentReplaceQueryCountsAndRenameAreSourceScoped()
    {
        using TemporaryDirectory temporary = TemporaryDirectory.Create("post-mvp-content-catalog");
        string databasePath = Path.Combine(temporary.FullPath, "catalog.db");
        var catalog = new SqliteContentCatalog(databasePath);
        SourceId sourceId = SourceId.Generate();
        SnapshotId snapshotId = SnapshotId.Generate();
        _ = await catalog.ReadCountsAsync(sourceId);
        await SeedReadySourceAsync(databasePath, sourceId, snapshotId);

        CategoryId movieCategoryId = CategoryId.Generate();
        CategoryId seriesCategoryId = CategoryId.Generate();
        ChannelCategory movieCategory = ChannelCategory.Create(
            movieCategoryId,
            snapshotId,
            "xtream:movie:synthetic",
            "Movies",
            0,
            false).Value!;
        ChannelCategory seriesCategory = ChannelCategory.Create(
            seriesCategoryId,
            snapshotId,
            "xtream:series:synthetic",
            "Series",
            1,
            false).Value!;
        ProviderItemKey movieKey = ProviderItemKey.Create("movie-1").Value;
        ProviderItemKey seriesKey = ProviderItemKey.Create("series-1").Value;
        ProviderItemKey episodeKey = ProviderItemKey.Create("episode-1").Value;
        Movie movie = Movie.Create(
            MovieId.Generate(), snapshotId, movieCategoryId, movieKey,
            "Synthetic Movie", "mp4", false).Value!;
        Series series = Series.Create(
            SeriesId.Generate(), snapshotId, seriesCategoryId, seriesKey,
            "Synthetic Series", false).Value!;
        Season season = Season.Create(
            SeasonId.Generate(), snapshotId, series.Id, 1, "Season 1").Value!;
        Episode episode = Episode.Create(
            EpisodeId.Generate(), snapshotId, season.Id, episodeKey, 1,
            "Episode 1", "mkv", TimeSpan.FromMinutes(42)).Value!;

        await catalog.ReplaceActiveSnapshotContentAsync(new ContentCatalogMutation(
            sourceId,
            snapshotId,
            [movieCategory, seriesCategory],
            [movie],
            [series],
            [season],
            [episode]));

        ContentCatalogCounts counts = await catalog.ReadCountsAsync(sourceId);
        ContentPage<ContentMovieItem> movies = await catalog.ReadMoviesAsync(
            sourceId, movieCategoryId, "movie", 0, 20);
        ContentPage<ContentSeriesItem> seriesPage = await catalog.ReadSeriesAsync(
            sourceId, seriesCategoryId, null, 0, 20);
        IReadOnlyList<ContentSeasonItem> seasons = await catalog.ReadSeasonsAsync(sourceId, series.Id);
        IReadOnlyList<ContentEpisodeItem> episodes = await catalog.ReadEpisodesAsync(sourceId, season.Id);
        var management = new SqliteSourceManagementCatalog(databasePath);
        DomainResult<SourceManagementSummary> renamed = await management.RenameAsync(
            sourceId,
            "  Renamed Source  ",
            new DateTimeOffset(2026, 8, 29, 12, 0, 0, TimeSpan.Zero));

        Assert.AreEqual(0, counts.LiveTvCount);
        Assert.AreEqual(1, counts.MovieCount);
        Assert.AreEqual(1, counts.SeriesCount);
        Assert.AreEqual(1, counts.EpisodeCount);
        Assert.AreEqual(2, counts.TotalTopLevelCount);
        Assert.HasCount(1, movies.Items);
        Assert.HasCount(1, seriesPage.Items);
        Assert.HasCount(1, seasons);
        Assert.HasCount(1, episodes);
        Assert.AreEqual(TimeSpan.FromMinutes(42), episodes[0].Duration);
        Assert.IsTrue(renamed.IsSuccess);
        Assert.AreEqual("Renamed Source", renamed.Value!.Name);
        Assert.AreEqual(counts, renamed.Value.Counts);
        Assert.IsTrue(renamed.Value.UsesInsecureHttp);
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task NonActiveSnapshotCannotMutateVisibleContent()
    {
        using TemporaryDirectory temporary = TemporaryDirectory.Create("post-mvp-content-snapshot-scope");
        string databasePath = Path.Combine(temporary.FullPath, "catalog.db");
        var catalog = new SqliteContentCatalog(databasePath);
        SourceId sourceId = SourceId.Generate();
        SnapshotId active = SnapshotId.Generate();
        _ = await catalog.ReadCountsAsync(sourceId);
        await SeedReadySourceAsync(databasePath, sourceId, active);

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(async () =>
            await catalog.ReplaceActiveSnapshotContentAsync(new ContentCatalogMutation(
                sourceId,
                SnapshotId.Generate(),
                [],
                [],
                [],
                [],
                [])));

        Assert.AreEqual(0, (await catalog.ReadCountsAsync(sourceId)).TotalTopLevelCount);
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task ContentQueriesCannotCrossSourceOrActiveSnapshotBoundaries()
    {
        using TemporaryDirectory temporary = TemporaryDirectory.Create("post-mvp-content-source-boundary");
        string databasePath = Path.Combine(temporary.FullPath, "catalog.db");
        var catalog = new SqliteContentCatalog(databasePath);
        SourceId firstSource = SourceId.Generate();
        SourceId secondSource = SourceId.Generate();
        SnapshotId firstSnapshot = SnapshotId.Generate();
        SnapshotId secondSnapshot = SnapshotId.Generate();
        _ = await catalog.ReadCountsAsync(firstSource);
        await SeedReadySourceAsync(databasePath, firstSource, firstSnapshot);
        await SeedReadySourceAsync(databasePath, secondSource, secondSnapshot);

        CategoryId categoryId = CategoryId.Generate();
        ChannelCategory category = ChannelCategory.Create(
            categoryId,
            firstSnapshot,
            "xtream:movie:source-boundary",
            "Movies",
            0,
            false).Value!;
        Movie movie = Movie.Create(
            MovieId.Generate(),
            firstSnapshot,
            categoryId,
            ProviderItemKey.Create("movie-source-boundary").Value,
            "Synthetic Source-Bound Movie",
            "mp4",
            false).Value!;
        await catalog.ReplaceActiveSnapshotContentAsync(new ContentCatalogMutation(
            firstSource,
            firstSnapshot,
            [category],
            [movie],
            [],
            [],
            []));

        ContentPage<ContentMovieItem> first = await catalog.ReadMoviesAsync(
            firstSource, categoryId, null, 0, 20);
        ContentPage<ContentMovieItem> second = await catalog.ReadMoviesAsync(
            secondSource, categoryId, null, 0, 20);

        Assert.HasCount(1, first.Items);
        Assert.IsEmpty(second.Items);
        Assert.AreEqual(0, second.TotalCount);
        Assert.AreEqual(0, (await catalog.ReadCountsAsync(secondSource)).TotalTopLevelCount);
    }

    private static async Task SeedReadySourceAsync(
        string databasePath,
        SourceId sourceId,
        SnapshotId snapshotId)
    {
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false,
        }.ToString());
        await connection.OpenAsync();
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA foreign_keys = ON;
            INSERT INTO sources(
                source_id, configuration_id, source_kind, display_name, endpoint_scheme,
                endpoint_host, endpoint_port, configuration_reference, status,
                active_snapshot_id, created_utc, updated_utc, last_error_code)
            VALUES (
                $source, $configuration, $kind, 'Synthetic Source', 'http',
                '127.0.0.1', 18080, $reference, $ready,
                $snapshot, $created, $created, NULL);
            INSERT INTO snapshots(
                snapshot_id, source_id, retrieved_utc, content_hash, http_etag,
                http_last_modified_utc, parser_version, normalization_version, schema_version,
                item_count, warning_count, state, cache_key)
            VALUES (
                $snapshot, $source, $created, zeroblob(32), NULL,
                NULL, 1, 1, 1, 0, 0, 1, NULL);
            """;
        command.Parameters.AddWithValue("$source", sourceId.Value.ToString("N"));
        command.Parameters.AddWithValue("$configuration", Guid.NewGuid().ToString("N"));
        command.Parameters.AddWithValue("$kind", (int)SourceKind.XtreamCompatible);
        command.Parameters.AddWithValue("$reference", $"secret-ref-v1:{Guid.NewGuid():N}");
        command.Parameters.AddWithValue("$ready", (int)ContentSourceStatus.Ready);
        command.Parameters.AddWithValue("$snapshot", snapshotId.Value.ToString("N"));
        command.Parameters.AddWithValue("$created", "2026-08-29T10:00:00.0000000+00:00");
        await command.ExecuteNonQueryAsync();
    }
}
