using System.Globalization;
using System.Text;
using IptvSuite.Application;
using IptvSuite.Domain;
using Microsoft.Data.Sqlite;

namespace IptvSuite.Infrastructure;

public sealed class SqliteContentCatalog :
    IContentCatalogStore,
    IContentCatalogBrowser,
    ISeriesDetailStore
{
    public const int MaximumPageSize = 200;
    public const int MaximumSearchLength = 100;
    private readonly string _databasePath;
    private readonly SqliteCatalogDatabase _database;

    public SqliteContentCatalog(string databasePath)
    {
        _database = new SqliteCatalogDatabase(databasePath);
        _databasePath = Path.GetFullPath(databasePath);
    }

    public async ValueTask ReplaceActiveSnapshotContentAsync(
        ContentCatalogMutation mutation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(mutation);
        ValidateMutation(mutation);
        await _database.InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteConnection connection = await OpenConnectionAsync(
            SqliteOpenMode.ReadWrite,
            cancellationToken).ConfigureAwait(false);
        await ConfigureWriteConnectionAsync(connection, cancellationToken).ConfigureAwait(false);
        await using SqliteTransaction transaction = (SqliteTransaction)await connection
            .BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        if (!await IsActiveSnapshotAsync(connection, transaction, mutation, cancellationToken)
                .ConfigureAwait(false))
        {
            throw new InvalidOperationException("Content can only replace a ready source's active snapshot.");
        }

        await ValidateCategoryBindingsAsync(connection, transaction, mutation, cancellationToken)
            .ConfigureAwait(false);
        await DeleteContentAsync(connection, transaction, mutation.SnapshotId, cancellationToken)
            .ConfigureAwait(false);
        await InsertSnapshotContentAsync(connection, transaction, mutation, cancellationToken)
            .ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask ReplaceSeriesDetailsAsync(
        SeriesDetailMutation mutation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(mutation);
        ValidateSeriesDetailMutation(mutation);
        await _database.InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteConnection connection = await OpenConnectionAsync(
            SqliteOpenMode.ReadWrite,
            cancellationToken).ConfigureAwait(false);
        await ConfigureWriteConnectionAsync(connection, cancellationToken).ConfigureAwait(false);
        await using SqliteTransaction transaction = (SqliteTransaction)await connection
            .BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        if (!await IsActiveSeriesAsync(connection, transaction, mutation, cancellationToken)
                .ConfigureAwait(false))
        {
            throw new InvalidOperationException(
                "Series details can only replace an active series snapshot.");
        }

        await ExecuteAsync(
            connection,
            transaction,
            "DELETE FROM seasons WHERE series_id = $series AND snapshot_id = $snapshot;",
            cancellationToken,
            ("$series", Id(mutation.SeriesId.Value)),
            ("$snapshot", Id(mutation.SnapshotId.Value))).ConfigureAwait(false);
        await InsertSeasonsAsync(connection, transaction, mutation.Seasons, cancellationToken)
            .ConfigureAwait(false);
        await InsertEpisodesAsync(connection, transaction, mutation.Episodes, cancellationToken)
            .ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    internal static async Task InsertSnapshotContentAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ContentCatalogMutation mutation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(transaction);
        ArgumentNullException.ThrowIfNull(mutation);
        ValidateMutation(mutation);
        await InsertCategoriesAsync(connection, transaction, mutation.Categories, cancellationToken)
            .ConfigureAwait(false);
        await InsertMoviesAsync(connection, transaction, mutation.Movies, cancellationToken)
            .ConfigureAwait(false);
        await InsertSeriesAsync(connection, transaction, mutation.Series, cancellationToken)
            .ConfigureAwait(false);
        await InsertSeasonsAsync(connection, transaction, mutation.Seasons, cancellationToken)
            .ConfigureAwait(false);
        await InsertEpisodesAsync(connection, transaction, mutation.Episodes, cancellationToken)
            .ConfigureAwait(false);
    }

    public async ValueTask<ContentCatalogCounts> ReadCountsAsync(
        SourceId sourceId,
        CancellationToken cancellationToken = default)
    {
        ValidateSourceId(sourceId);
        await _database.InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteConnection connection = await OpenConnectionAsync(
            SqliteOpenMode.ReadOnly,
            cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                (SELECT count(*) FROM channels AS item WHERE item.snapshot_id = source.active_snapshot_id),
                (SELECT count(*) FROM movies AS item WHERE item.snapshot_id = source.active_snapshot_id),
                (SELECT count(*) FROM series AS item WHERE item.snapshot_id = source.active_snapshot_id),
                (SELECT count(*) FROM episodes AS item WHERE item.snapshot_id = source.active_snapshot_id)
            FROM sources AS source
            WHERE source.source_id = $source
              AND source.status = $ready
              AND source.active_snapshot_id IS NOT NULL;
            """;
        command.Parameters.AddWithValue("$source", Id(sourceId.Value));
        command.Parameters.AddWithValue("$ready", (int)ContentSourceStatus.Ready);
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? new ContentCatalogCounts(
                reader.GetInt32(0),
                reader.GetInt32(1),
                reader.GetInt32(2),
                reader.GetInt32(3))
            : new ContentCatalogCounts(0, 0, 0, 0);
    }

    public async ValueTask<IReadOnlyList<CatalogCategoryItem>> ReadCategoriesAsync(
        SourceId sourceId,
        ContentKind contentKind,
        CancellationToken cancellationToken = default)
    {
        ValidateSourceId(sourceId);
        if (contentKind is not (ContentKind.Movie or ContentKind.Series))
        {
            throw new ArgumentOutOfRangeException(
                nameof(contentKind),
                "The VOD catalog exposes only movie and series categories.");
        }

        await _database.InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteConnection connection = await OpenConnectionAsync(
            SqliteOpenMode.ReadOnly,
            cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        string table = contentKind == ContentKind.Movie ? "movies" : "series";
        command.CommandText = $"""
            SELECT category.category_id, category.display_name, category.sort_order
            FROM categories AS category
            JOIN sources AS source ON source.active_snapshot_id = category.snapshot_id
            WHERE source.source_id = $source
              AND source.status = $ready
              AND EXISTS (
                  SELECT 1
                  FROM {table} AS item
                  WHERE item.snapshot_id = category.snapshot_id
                    AND item.category_id = category.category_id
              )
            ORDER BY category.sort_order, category.display_name COLLATE NOCASE, category.category_id;
            """;
        command.Parameters.AddWithValue("$source", Id(sourceId.Value));
        command.Parameters.AddWithValue("$ready", (int)ContentSourceStatus.Ready);
        var rows = new List<CatalogCategoryItem>();
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            rows.Add(new CatalogCategoryItem(
                ParseCategoryId(reader.GetString(0)),
                reader.GetString(1),
                reader.GetInt32(2)));
        }

        return rows;
    }

    public ValueTask<ContentPage<ContentMovieItem>> ReadMoviesAsync(
        SourceId sourceId,
        CategoryId? categoryId,
        string? searchText,
        int offset,
        int limit,
        CancellationToken cancellationToken = default) =>
        ReadMoviePageAsync(sourceId, categoryId, searchText, offset, limit, cancellationToken);

    public ValueTask<ContentPage<ContentSeriesItem>> ReadSeriesAsync(
        SourceId sourceId,
        CategoryId? categoryId,
        string? searchText,
        int offset,
        int limit,
        CancellationToken cancellationToken = default) =>
        ReadSeriesPageAsync(sourceId, categoryId, searchText, offset, limit, cancellationToken);

    public async ValueTask<IReadOnlyList<ContentSeasonItem>> ReadSeasonsAsync(
        SourceId sourceId,
        SeriesId seriesId,
        CancellationToken cancellationToken = default)
    {
        ValidateSourceId(sourceId);
        if (seriesId.IsEmpty)
        {
            throw new ArgumentException("A series identifier is required.", nameof(seriesId));
        }

        await _database.InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteConnection connection = await OpenConnectionAsync(
            SqliteOpenMode.ReadOnly,
            cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT season.season_id, season.series_id, season.season_number, season.display_name
            FROM seasons AS season
            JOIN sources AS source ON source.active_snapshot_id = season.snapshot_id
            WHERE source.source_id = $source
              AND source.status = $ready
              AND season.series_id = $series
            ORDER BY season.season_number, season.season_id;
            """;
        command.Parameters.AddWithValue("$source", Id(sourceId.Value));
        command.Parameters.AddWithValue("$ready", (int)ContentSourceStatus.Ready);
        command.Parameters.AddWithValue("$series", Id(seriesId.Value));
        var rows = new List<ContentSeasonItem>();
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            rows.Add(new ContentSeasonItem(
                ParseSeasonId(reader.GetString(0)),
                ParseSeriesId(reader.GetString(1)),
                reader.GetInt32(2),
                reader.GetString(3)));
        }

        return rows;
    }

    public async ValueTask<IReadOnlyList<ContentEpisodeItem>> ReadEpisodesAsync(
        SourceId sourceId,
        SeasonId seasonId,
        CancellationToken cancellationToken = default)
    {
        ValidateSourceId(sourceId);
        if (seasonId.IsEmpty)
        {
            throw new ArgumentException("A season identifier is required.", nameof(seasonId));
        }

        await _database.InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteConnection connection = await OpenConnectionAsync(
            SqliteOpenMode.ReadOnly,
            cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT episode.episode_id, episode.season_id, episode.episode_number,
                   episode.display_name, episode.duration_ms
            FROM episodes AS episode
            JOIN sources AS source ON source.active_snapshot_id = episode.snapshot_id
            WHERE source.source_id = $source
              AND source.status = $ready
              AND episode.season_id = $season
            ORDER BY episode.episode_number, episode.episode_id;
            """;
        command.Parameters.AddWithValue("$source", Id(sourceId.Value));
        command.Parameters.AddWithValue("$ready", (int)ContentSourceStatus.Ready);
        command.Parameters.AddWithValue("$season", Id(seasonId.Value));
        var rows = new List<ContentEpisodeItem>();
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            rows.Add(new ContentEpisodeItem(
                ParseEpisodeId(reader.GetString(0)),
                ParseSeasonId(reader.GetString(1)),
                reader.GetInt32(2),
                reader.GetString(3),
                reader.IsDBNull(4) ? null : TimeSpan.FromMilliseconds(reader.GetInt64(4))));
        }

        return rows;
    }

    private async ValueTask<ContentPage<ContentMovieItem>> ReadMoviePageAsync(
        SourceId sourceId,
        CategoryId? categoryId,
        string? searchText,
        int offset,
        int limit,
        CancellationToken cancellationToken)
    {
        string? search = ValidatePage(sourceId, categoryId, searchText, offset, limit);
        await _database.InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteConnection connection = await OpenConnectionAsync(
            SqliteOpenMode.ReadOnly,
            cancellationToken).ConfigureAwait(false);
        await using SqliteTransaction transaction = (SqliteTransaction)await connection
            .BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        int total = await CountPageAsync(
            connection,
            transaction,
            "movies",
            sourceId,
            categoryId,
            search,
            cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT item.movie_id, item.category_id, item.display_name, item.is_adult
            FROM movies AS item
            JOIN sources AS source ON source.active_snapshot_id = item.snapshot_id
            WHERE source.source_id = $source
              AND source.status = $ready
              AND ($category IS NULL OR item.category_id = $category)
              AND ($search IS NULL OR instr(lower(item.display_name), lower($search)) > 0)
            ORDER BY item.display_name COLLATE NOCASE, item.movie_id
            LIMIT $limit OFFSET $offset;
            """;
        AddPageParameters(command, sourceId, categoryId, search, offset, limit);
        var rows = new List<ContentMovieItem>(limit);
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            rows.Add(new ContentMovieItem(
                ParseMovieId(reader.GetString(0)),
                reader.IsDBNull(1) ? null : ParseCategoryId(reader.GetString(1)),
                reader.GetString(2),
                reader.GetInt32(3) == 1));
        }

        return new ContentPage<ContentMovieItem>(rows, offset, total);
    }

    private async ValueTask<ContentPage<ContentSeriesItem>> ReadSeriesPageAsync(
        SourceId sourceId,
        CategoryId? categoryId,
        string? searchText,
        int offset,
        int limit,
        CancellationToken cancellationToken)
    {
        string? search = ValidatePage(sourceId, categoryId, searchText, offset, limit);
        await _database.InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteConnection connection = await OpenConnectionAsync(
            SqliteOpenMode.ReadOnly,
            cancellationToken).ConfigureAwait(false);
        await using SqliteTransaction transaction = (SqliteTransaction)await connection
            .BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        int total = await CountPageAsync(
            connection,
            transaction,
            "series",
            sourceId,
            categoryId,
            search,
            cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT item.series_id, item.category_id, item.display_name, item.is_adult
            FROM series AS item
            JOIN sources AS source ON source.active_snapshot_id = item.snapshot_id
            WHERE source.source_id = $source
              AND source.status = $ready
              AND ($category IS NULL OR item.category_id = $category)
              AND ($search IS NULL OR instr(lower(item.display_name), lower($search)) > 0)
            ORDER BY item.display_name COLLATE NOCASE, item.series_id
            LIMIT $limit OFFSET $offset;
            """;
        AddPageParameters(command, sourceId, categoryId, search, offset, limit);
        var rows = new List<ContentSeriesItem>(limit);
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            rows.Add(new ContentSeriesItem(
                ParseSeriesId(reader.GetString(0)),
                reader.IsDBNull(1) ? null : ParseCategoryId(reader.GetString(1)),
                reader.GetString(2),
                reader.GetInt32(3) == 1));
        }

        return new ContentPage<ContentSeriesItem>(rows, offset, total);
    }

    private static void ValidateMutation(ContentCatalogMutation mutation)
    {
        if (mutation.SourceId.IsEmpty || mutation.SnapshotId.IsEmpty ||
            mutation.Categories.Any(item => item.SnapshotId != mutation.SnapshotId) ||
            mutation.Movies.Any(item => item.SnapshotId != mutation.SnapshotId) ||
            mutation.Series.Any(item => item.SnapshotId != mutation.SnapshotId) ||
            mutation.Seasons.Any(item => item.SnapshotId != mutation.SnapshotId) ||
            mutation.Episodes.Any(item => item.SnapshotId != mutation.SnapshotId) ||
            mutation.Categories.Select(item => item.Id).Distinct().Count() != mutation.Categories.Count ||
            mutation.Movies.Select(item => item.Id).Distinct().Count() != mutation.Movies.Count ||
            mutation.Series.Select(item => item.Id).Distinct().Count() != mutation.Series.Count ||
            mutation.Seasons.Select(item => item.Id).Distinct().Count() != mutation.Seasons.Count ||
            mutation.Episodes.Select(item => item.Id).Distinct().Count() != mutation.Episodes.Count)
        {
            throw new ArgumentException("The content catalog mutation is inconsistent.", nameof(mutation));
        }

        HashSet<CategoryId> categories = mutation.Categories.Select(item => item.Id).ToHashSet();
        HashSet<SeriesId> series = mutation.Series.Select(item => item.Id).ToHashSet();
        HashSet<SeasonId> seasons = mutation.Seasons.Select(item => item.Id).ToHashSet();
        if (mutation.Movies.Any(item => item.CategoryId.HasValue && !categories.Contains(item.CategoryId.Value)) ||
            mutation.Series.Any(item => item.CategoryId.HasValue && !categories.Contains(item.CategoryId.Value)) ||
            mutation.Seasons.Any(item => !series.Contains(item.SeriesId)) ||
            mutation.Episodes.Any(item => !seasons.Contains(item.SeasonId)))
        {
            throw new ArgumentException("The content hierarchy is incomplete.", nameof(mutation));
        }
    }

    private static void ValidateSeriesDetailMutation(SeriesDetailMutation mutation)
    {
        if (mutation.SourceId.IsEmpty || mutation.SnapshotId.IsEmpty || mutation.SeriesId.IsEmpty ||
            mutation.Seasons.Count > XtreamProviderJsonParser.MaximumSeasonCount ||
            mutation.Episodes.Count > XtreamProviderJsonParser.MaximumEpisodeCount ||
            mutation.Seasons.Any(item =>
                item.SnapshotId != mutation.SnapshotId || item.SeriesId != mutation.SeriesId) ||
            mutation.Episodes.Any(item => item.SnapshotId != mutation.SnapshotId) ||
            mutation.Seasons.Select(item => item.Id).Distinct().Count() != mutation.Seasons.Count ||
            mutation.Episodes.Select(item => item.Id).Distinct().Count() != mutation.Episodes.Count)
        {
            throw new ArgumentException(
                "The series detail mutation is inconsistent.",
                nameof(mutation));
        }

        HashSet<SeasonId> seasons = mutation.Seasons.Select(item => item.Id).ToHashSet();
        if (mutation.Episodes.Any(item => !seasons.Contains(item.SeasonId)))
        {
            throw new ArgumentException(
                "The series detail hierarchy is incomplete.",
                nameof(mutation));
        }
    }

    private static async Task<bool> IsActiveSnapshotAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ContentCatalogMutation mutation,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT count(*)
            FROM sources
            WHERE source_id = $source
              AND active_snapshot_id = $snapshot
              AND status = $ready;
            """;
        command.Parameters.AddWithValue("$source", Id(mutation.SourceId.Value));
        command.Parameters.AddWithValue("$snapshot", Id(mutation.SnapshotId.Value));
        command.Parameters.AddWithValue("$ready", (int)ContentSourceStatus.Ready);
        return Convert.ToInt32(
            await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
            CultureInfo.InvariantCulture) == 1;
    }

    private static async Task<bool> IsActiveSeriesAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        SeriesDetailMutation mutation,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT count(*)
            FROM series AS item
            JOIN sources AS source ON source.active_snapshot_id = item.snapshot_id
            WHERE source.source_id = $source
              AND source.status = $ready
              AND item.snapshot_id = $snapshot
              AND item.series_id = $series;
            """;
        command.Parameters.AddWithValue("$source", Id(mutation.SourceId.Value));
        command.Parameters.AddWithValue("$ready", (int)ContentSourceStatus.Ready);
        command.Parameters.AddWithValue("$snapshot", Id(mutation.SnapshotId.Value));
        command.Parameters.AddWithValue("$series", Id(mutation.SeriesId.Value));
        return Convert.ToInt32(
            await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
            CultureInfo.InvariantCulture) == 1;
    }

    private static async Task ValidateCategoryBindingsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ContentCatalogMutation mutation,
        CancellationToken cancellationToken)
    {
        HashSet<CategoryId> requested = mutation.Movies
            .Select(item => item.CategoryId)
            .Concat(mutation.Series.Select(item => item.CategoryId))
            .Where(item => item.HasValue)
            .Select(item => item!.Value)
            .ToHashSet();
        requested.ExceptWith(mutation.Categories.Select(item => item.Id));
        if (requested.Count == 0)
        {
            return;
        }

        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT category_id FROM categories WHERE snapshot_id = $snapshot;";
        command.Parameters.AddWithValue("$snapshot", Id(mutation.SnapshotId.Value));
        var actual = new HashSet<CategoryId>();
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            actual.Add(ParseCategoryId(reader.GetString(0)));
        }

        if (!requested.IsSubsetOf(actual))
        {
            throw new ArgumentException("A content category is outside the active snapshot.", nameof(mutation));
        }
    }

    private static Task DeleteContentAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        SnapshotId snapshotId,
        CancellationToken cancellationToken) => ExecuteAsync(
        connection,
        transaction,
        """
        DELETE FROM episodes WHERE snapshot_id = $snapshot;
        DELETE FROM seasons WHERE snapshot_id = $snapshot;
        DELETE FROM series WHERE snapshot_id = $snapshot;
        DELETE FROM movies WHERE snapshot_id = $snapshot;
        DELETE FROM categories
        WHERE snapshot_id = $snapshot
          AND (stable_key LIKE 'xtream:movie:%' OR stable_key LIKE 'xtream:series:%');
        """,
        cancellationToken,
        ("$snapshot", Id(snapshotId.Value)));

    private static async Task InsertCategoriesAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        IReadOnlyList<ChannelCategory> categories,
        CancellationToken cancellationToken)
    {
        foreach (ChannelCategory category in categories)
        {
            if (category.ProviderKey is null ||
                (!category.ProviderKey.StartsWith("xtream:movie:", StringComparison.Ordinal) &&
                 !category.ProviderKey.StartsWith("xtream:series:", StringComparison.Ordinal)))
            {
                throw new ArgumentException("A content category requires an explicit content-kind key.", nameof(categories));
            }

            await ExecuteAsync(connection, transaction, """
                INSERT INTO categories(category_id, snapshot_id, stable_key, display_name, sort_order)
                VALUES ($id, $snapshot, $key, $name, $sort);
                """, cancellationToken,
                ("$id", Id(category.Id.Value)),
                ("$snapshot", Id(category.SnapshotId.Value)),
                ("$key", category.ProviderKey),
                ("$name", category.NormalizedName),
                ("$sort", category.SortOrder)).ConfigureAwait(false);
        }
    }

    private static async Task InsertMoviesAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        IReadOnlyList<Movie> movies,
        CancellationToken cancellationToken)
    {
        foreach (Movie movie in movies)
        {
            await ExecuteAsync(connection, transaction, """
                INSERT INTO movies(movie_id, snapshot_id, category_id, provider_item_id,
                    display_name, container_extension, is_adult)
                VALUES ($id, $snapshot, $category, $provider, $name, $container, $adult);
                """, cancellationToken,
                ("$id", Id(movie.Id.Value)),
                ("$snapshot", Id(movie.SnapshotId.Value)),
                ("$category", movie.CategoryId.HasValue ? Id(movie.CategoryId.Value.Value) : DBNull.Value),
                ("$provider", movie.ProviderPlaybackKey.Value),
                ("$name", movie.Name),
                ("$container", movie.ContainerExtension ?? (object)DBNull.Value),
                ("$adult", movie.IsAdultHint == true ? 1 : 0)).ConfigureAwait(false);
        }
    }

    private static async Task InsertSeriesAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        IReadOnlyList<Series> series,
        CancellationToken cancellationToken)
    {
        foreach (Series item in series)
        {
            await ExecuteAsync(connection, transaction, """
                INSERT INTO series(series_id, snapshot_id, category_id, provider_item_id,
                    display_name, is_adult)
                VALUES ($id, $snapshot, $category, $provider, $name, $adult);
                """, cancellationToken,
                ("$id", Id(item.Id.Value)),
                ("$snapshot", Id(item.SnapshotId.Value)),
                ("$category", item.CategoryId.HasValue ? Id(item.CategoryId.Value.Value) : DBNull.Value),
                ("$provider", item.ProviderKey.Value),
                ("$name", item.Name),
                ("$adult", item.IsAdultHint == true ? 1 : 0)).ConfigureAwait(false);
        }
    }

    private static async Task InsertSeasonsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        IReadOnlyList<Season> seasons,
        CancellationToken cancellationToken)
    {
        foreach (Season item in seasons)
        {
            await ExecuteAsync(connection, transaction, """
                INSERT INTO seasons(season_id, snapshot_id, series_id, provider_item_id,
                    season_number, display_name)
                VALUES ($id, $snapshot, $series, NULL, $number, $name);
                """, cancellationToken,
                ("$id", Id(item.Id.Value)),
                ("$snapshot", Id(item.SnapshotId.Value)),
                ("$series", Id(item.SeriesId.Value)),
                ("$number", item.Number),
                ("$name", item.Name)).ConfigureAwait(false);
        }
    }

    private static async Task InsertEpisodesAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        IReadOnlyList<Episode> episodes,
        CancellationToken cancellationToken)
    {
        foreach (Episode item in episodes)
        {
            await ExecuteAsync(connection, transaction, """
                INSERT INTO episodes(episode_id, snapshot_id, season_id, provider_item_id,
                    episode_number, display_name, container_extension, duration_ms)
                VALUES ($id, $snapshot, $season, $provider, $number, $name, $container, $duration);
                """, cancellationToken,
                ("$id", Id(item.Id.Value)),
                ("$snapshot", Id(item.SnapshotId.Value)),
                ("$season", Id(item.SeasonId.Value)),
                ("$provider", item.ProviderPlaybackKey.Value),
                ("$number", item.Number),
                ("$name", item.Name),
                ("$container", item.ContainerExtension ?? (object)DBNull.Value),
                ("$duration", item.Duration.HasValue
                    ? checked((long)item.Duration.Value.TotalMilliseconds)
                    : DBNull.Value)).ConfigureAwait(false);
        }
    }

    private async ValueTask<SqliteConnection> OpenConnectionAsync(
        SqliteOpenMode mode,
        CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = _databasePath,
            Mode = mode,
            Cache = SqliteCacheMode.Private,
            Pooling = false,
        }.ToString());
        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static async Task ConfigureWriteConnectionAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await ExecuteAsync(connection, null, "PRAGMA foreign_keys = ON;", cancellationToken)
            .ConfigureAwait(false);
        await ExecuteAsync(connection, null, "PRAGMA synchronous = EXTRA;", cancellationToken)
            .ConfigureAwait(false);
        await ExecuteAsync(connection, null, "PRAGMA busy_timeout = 5000;", cancellationToken)
            .ConfigureAwait(false);
    }

    private static string? ValidatePage(
        SourceId sourceId,
        CategoryId? categoryId,
        string? searchText,
        int offset,
        int limit)
    {
        ValidateSourceId(sourceId);
        if (categoryId?.IsEmpty == true)
        {
            throw new ArgumentException("A category identifier must be non-empty.", nameof(categoryId));
        }

        if (offset < 0 || limit is < 1 or > MaximumPageSize)
        {
            throw new ArgumentOutOfRangeException(nameof(limit), "A bounded content page is required.");
        }

        if (string.IsNullOrWhiteSpace(searchText))
        {
            return null;
        }

        string normalized = searchText.Trim().Normalize(NormalizationForm.FormC);
        if (normalized.EnumerateRunes().Count() > MaximumSearchLength || normalized.Any(char.IsControl))
        {
            throw new ArgumentOutOfRangeException(nameof(searchText), "Search text is invalid.");
        }

        return normalized;
    }

    private static async Task<int> CountPageAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string table,
        SourceId sourceId,
        CategoryId? categoryId,
        string? search,
        CancellationToken cancellationToken)
    {
        if (table is not ("movies" or "series"))
        {
            throw new ArgumentOutOfRangeException(nameof(table));
        }

        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"""
            SELECT count(*)
            FROM {table} AS item
            JOIN sources AS source ON source.active_snapshot_id = item.snapshot_id
            WHERE source.source_id = $source
              AND source.status = $ready
              AND ($category IS NULL OR item.category_id = $category)
              AND ($search IS NULL OR instr(lower(item.display_name), lower($search)) > 0);
            """;
        AddPageParameters(command, sourceId, categoryId, search, offset: 0, limit: 1);
        return Convert.ToInt32(
            await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
            CultureInfo.InvariantCulture);
    }

    private static void AddPageParameters(
        SqliteCommand command,
        SourceId sourceId,
        CategoryId? categoryId,
        string? search,
        int offset,
        int limit)
    {
        command.Parameters.AddWithValue("$source", Id(sourceId.Value));
        command.Parameters.AddWithValue("$ready", (int)ContentSourceStatus.Ready);
        command.Parameters.AddWithValue("$category", categoryId.HasValue
            ? Id(categoryId.Value.Value)
            : DBNull.Value);
        command.Parameters.AddWithValue("$search", search ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$offset", offset);
        command.Parameters.AddWithValue("$limit", limit);
    }

    private static async Task ExecuteAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string sql,
        CancellationToken cancellationToken,
        params (string Name, object Value)[] parameters)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach ((string name, object value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static void ValidateSourceId(SourceId sourceId)
    {
        if (sourceId.IsEmpty)
        {
            throw new ArgumentException("A source identifier is required.", nameof(sourceId));
        }
    }

    private static MovieId ParseMovieId(string value) => ParseId(value, MovieId.Create);

    private static SeriesId ParseSeriesId(string value) => ParseId(value, SeriesId.Create);

    private static SeasonId ParseSeasonId(string value) => ParseId(value, SeasonId.Create);

    private static EpisodeId ParseEpisodeId(string value) => ParseId(value, EpisodeId.Create);

    private static CategoryId ParseCategoryId(string value) => ParseId(value, CategoryId.Create);

    private static T ParseId<T>(string value, Func<Guid, DomainResult<T>> create)
    {
        if (!Guid.TryParseExact(value, "N", out Guid guid))
        {
            throw new InvalidDataException("Catalog identifier is invalid.");
        }

        DomainResult<T> result = create(guid);
        return result.IsSuccess
            ? result.Value!
            : throw new InvalidDataException("Catalog identifier is invalid.");
    }

    private static string Id(Guid value) => value.ToString("N", CultureInfo.InvariantCulture);
}
