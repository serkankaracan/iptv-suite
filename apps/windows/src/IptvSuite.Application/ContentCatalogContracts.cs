using System.Diagnostics;

namespace IptvSuite.Application;

public sealed record ContentCatalogCounts
{
    public ContentCatalogCounts(
        int liveTvCount,
        int movieCount,
        int seriesCount,
        int episodeCount)
    {
        if (liveTvCount < 0 || movieCount < 0 || seriesCount < 0 || episodeCount < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(liveTvCount),
                "Catalog counts cannot be negative.");
        }

        LiveTvCount = liveTvCount;
        MovieCount = movieCount;
        SeriesCount = seriesCount;
        EpisodeCount = episodeCount;
        TotalTopLevelCount = checked(liveTvCount + movieCount + seriesCount);
    }

    public int LiveTvCount { get; }

    public int MovieCount { get; }

    public int SeriesCount { get; }

    public int EpisodeCount { get; }

    public int TotalTopLevelCount { get; }
}

public sealed record ContentMovieItem(
    MovieId MovieId,
    CategoryId? CategoryId,
    string Name,
    bool IsAdult);

public sealed record ContentSeriesItem(
    SeriesId SeriesId,
    CategoryId? CategoryId,
    string Name,
    bool IsAdult);

public sealed record ContentSeasonItem(
    SeasonId SeasonId,
    SeriesId SeriesId,
    int Number,
    string Name);

public sealed record ContentEpisodeItem(
    EpisodeId EpisodeId,
    SeasonId SeasonId,
    int Number,
    string Name,
    TimeSpan? Duration);

public sealed record ContentPage<T>(
    IReadOnlyList<T> Items,
    int Offset,
    int TotalCount);

public interface IContentCatalogBrowser
{
    ValueTask<IReadOnlyList<CatalogCategoryItem>> ReadCategoriesAsync(
        SourceId sourceId,
        ContentKind contentKind,
        CancellationToken cancellationToken = default);

    ValueTask<ContentCatalogCounts> ReadCountsAsync(
        SourceId sourceId,
        CancellationToken cancellationToken = default);

    ValueTask<ContentPage<ContentMovieItem>> ReadMoviesAsync(
        SourceId sourceId,
        CategoryId? categoryId,
        string? searchText,
        int offset,
        int limit,
        CancellationToken cancellationToken = default);

    ValueTask<ContentPage<ContentSeriesItem>> ReadSeriesAsync(
        SourceId sourceId,
        CategoryId? categoryId,
        string? searchText,
        int offset,
        int limit,
        CancellationToken cancellationToken = default);

    ValueTask<IReadOnlyList<ContentSeasonItem>> ReadSeasonsAsync(
        SourceId sourceId,
        SeriesId seriesId,
        CancellationToken cancellationToken = default);

    ValueTask<IReadOnlyList<ContentEpisodeItem>> ReadEpisodesAsync(
        SourceId sourceId,
        SeasonId seasonId,
        CancellationToken cancellationToken = default);
}

[DebuggerDisplay("[CONTENT-CATALOG-MUTATION]")]
public sealed class ContentCatalogMutation
{
    public ContentCatalogMutation(
        SourceId sourceId,
        SnapshotId snapshotId,
        IReadOnlyList<ChannelCategory> categories,
        IReadOnlyList<Movie> movies,
        IReadOnlyList<Series> series,
        IReadOnlyList<Season> seasons,
        IReadOnlyList<Episode> episodes)
    {
        SourceId = sourceId;
        SnapshotId = snapshotId;
        Categories = categories ?? throw new ArgumentNullException(nameof(categories));
        Movies = movies ?? throw new ArgumentNullException(nameof(movies));
        Series = series ?? throw new ArgumentNullException(nameof(series));
        Seasons = seasons ?? throw new ArgumentNullException(nameof(seasons));
        Episodes = episodes ?? throw new ArgumentNullException(nameof(episodes));
    }

    public SourceId SourceId { get; }

    public SnapshotId SnapshotId { get; }

    public IReadOnlyList<ChannelCategory> Categories { get; }

    public IReadOnlyList<Movie> Movies { get; }

    public IReadOnlyList<Series> Series { get; }

    public IReadOnlyList<Season> Seasons { get; }

    public IReadOnlyList<Episode> Episodes { get; }

    public override string ToString() => "[CONTENT-CATALOG-MUTATION]";
}

public interface IContentCatalogStore
{
    ValueTask ReplaceActiveSnapshotContentAsync(
        ContentCatalogMutation mutation,
        CancellationToken cancellationToken = default);
}

[DebuggerDisplay("[SERIES-DETAIL-MUTATION]")]
public sealed class SeriesDetailMutation
{
    public SeriesDetailMutation(
        SourceId sourceId,
        SnapshotId snapshotId,
        SeriesId seriesId,
        IReadOnlyList<Season> seasons,
        IReadOnlyList<Episode> episodes)
    {
        SourceId = sourceId;
        SnapshotId = snapshotId;
        SeriesId = seriesId;
        Seasons = seasons ?? throw new ArgumentNullException(nameof(seasons));
        Episodes = episodes ?? throw new ArgumentNullException(nameof(episodes));
    }

    public SourceId SourceId { get; }

    public SnapshotId SnapshotId { get; }

    public SeriesId SeriesId { get; }

    public IReadOnlyList<Season> Seasons { get; }

    public IReadOnlyList<Episode> Episodes { get; }

    public override string ToString() => "[SERIES-DETAIL-MUTATION]";
}

public interface ISeriesDetailStore
{
    ValueTask ReplaceSeriesDetailsAsync(
        SeriesDetailMutation mutation,
        CancellationToken cancellationToken = default);
}

public sealed record SeriesDetailRefreshResult(
    SeriesId SeriesId,
    int SeasonCount,
    int EpisodeCount);

public interface ISeriesDetailRefreshCoordinator
{
    ValueTask<DomainResult<SeriesDetailRefreshResult>> RefreshAsync(
        SourceId sourceId,
        SeriesId seriesId,
        CancellationToken cancellationToken = default);
}

public sealed record SourceManagementSummary(
    SourceId SourceId,
    string Name,
    SourceKind Kind,
    ContentSourceStatus Status,
    bool UsesInsecureHttp,
    ContentCatalogCounts Counts);

public interface ISourceManagementCatalog
{
    ValueTask<IReadOnlyList<SourceManagementSummary>> ReadAsync(
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Restores the exact protected configuration binding required for a refresh or replacement.
    /// </summary>
    /// <remarks>
    /// The returned aggregate never exposes locator or credential plaintext. It is a short-lived
    /// operation input and must not be cached by presentation code.
    /// </remarks>
    ValueTask<DomainResult<ContentSource>> ReadConfigurationAsync(
        SourceId sourceId,
        CancellationToken cancellationToken = default);

    ValueTask<DomainResult<SourceManagementSummary>> RenameAsync(
        SourceId sourceId,
        string? displayName,
        DateTimeOffset changedAt,
        CancellationToken cancellationToken = default);
}

public interface ISourceRefreshCoordinator
{
    ValueTask<DomainResult<ContentCatalogCounts>> RefreshFromStoredConfigurationAsync(
        SourceId sourceId,
        CancellationToken cancellationToken = default);
}

public interface IXtreamCatalogImportService : ISourceRefreshCoordinator
{
    ValueTask<DomainResult<ContentCatalogCounts>> ImportAsync(
        ContentSource source,
        CancellationToken cancellationToken = default);

    ValueTask<XtreamCatalogImportResult> ImportWithDispositionAsync(
        ContentSource source,
        CancellationToken cancellationToken = default);
}

[DebuggerDisplay("[XTREAM-CATALOG-IMPORT-RESULT]")]
public sealed class XtreamCatalogImportResult
{
    private XtreamCatalogImportResult(
        CatalogImportCommitDisposition disposition,
        ContentCatalogCounts? counts,
        DomainError? error)
    {
        Disposition = disposition;
        Counts = counts;
        Error = error;
    }

    public CatalogImportCommitDisposition Disposition { get; }

    public ContentCatalogCounts? Counts { get; }

    public DomainError? Error { get; }

    public static XtreamCatalogImportResult Committed(ContentCatalogCounts counts) =>
        new(
            CatalogImportCommitDisposition.Committed,
            counts ?? throw new ArgumentNullException(nameof(counts)),
            null);

    public static XtreamCatalogImportResult NotCommitted(DomainError error) =>
        new(
            CatalogImportCommitDisposition.NotCommitted,
            null,
            error ?? throw new ArgumentNullException(nameof(error)));

    public static XtreamCatalogImportResult Indeterminate(DomainError error) =>
        new(
            CatalogImportCommitDisposition.Indeterminate,
            null,
            error ?? throw new ArgumentNullException(nameof(error)));

    public override string ToString() => "[XTREAM-CATALOG-IMPORT-RESULT]";
}
