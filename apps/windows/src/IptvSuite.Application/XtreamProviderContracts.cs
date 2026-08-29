using System.Diagnostics;

namespace IptvSuite.Application;

[DebuggerDisplay("[XTREAM-ACCOUNT-STATUS]")]
public sealed record XtreamAccountStatus(bool IsAuthenticated)
{
    public override string ToString() => "[XTREAM-ACCOUNT-STATUS]";
}

[DebuggerDisplay("[XTREAM-PROVIDER-PAGE]")]
public sealed class XtreamProviderPage<T>
{
    internal XtreamProviderPage(
        IReadOnlyList<T> items,
        int skippedItemCount,
        int duplicateIdentifierCount,
        bool isCompatibilityEmptySentinel = false)
    {
        Items = items ?? throw new ArgumentNullException(nameof(items));
        SkippedItemCount = skippedItemCount;
        DuplicateIdentifierCount = duplicateIdentifierCount;
        IsCompatibilityEmptySentinel = isCompatibilityEmptySentinel;
    }

    public IReadOnlyList<T> Items { get; }

    public int SkippedItemCount { get; }

    public int DuplicateIdentifierCount { get; }

    public bool IsCompatibilityEmptySentinel { get; }

    public override string ToString() => "[XTREAM-PROVIDER-PAGE]";
}

[DebuggerDisplay("[XTREAM-CATEGORY-INPUT]")]
public sealed record XtreamCategoryInput(
    string ProviderIdentifier,
    string Name,
    ContentKind ContentKind = ContentKind.LiveTv)
{
    public override string ToString() => "[XTREAM-CATEGORY-INPUT]";
}

[DebuggerDisplay("[XTREAM-STREAM-INPUT]")]
public sealed record XtreamStreamInput(
    ProviderItemKey ProviderPlaybackKey,
    string Name,
    string? CategoryIdentifier,
    int? Number,
    string? ContainerExtension,
    bool? IsAdultHint)
{
    public override string ToString() => "[XTREAM-STREAM-INPUT]";
}

[DebuggerDisplay("[XTREAM-MOVIE-INPUT]")]
public sealed record XtreamMovieInput(
    ProviderItemKey ProviderPlaybackKey,
    string Name,
    string? CategoryIdentifier,
    string? ContainerExtension,
    bool? IsAdultHint)
{
    public override string ToString() => "[XTREAM-MOVIE-INPUT]";
}

[DebuggerDisplay("[XTREAM-SERIES-INPUT]")]
public sealed record XtreamSeriesInput(
    ProviderItemKey ProviderKey,
    string Name,
    string? CategoryIdentifier,
    bool? IsAdultHint)
{
    public override string ToString() => "[XTREAM-SERIES-INPUT]";
}

[DebuggerDisplay("[XTREAM-SEASON-INPUT]")]
public sealed record XtreamSeasonInput(
    ProviderItemKey? ProviderKey,
    int Number,
    string Name)
{
    public override string ToString() => "[XTREAM-SEASON-INPUT]";
}

[DebuggerDisplay("[XTREAM-EPISODE-INPUT]")]
public sealed record XtreamEpisodeInput(
    ProviderItemKey ProviderPlaybackKey,
    int SeasonNumber,
    int EpisodeNumber,
    string Name,
    string? ContainerExtension,
    TimeSpan? Duration)
{
    public override string ToString() => "[XTREAM-EPISODE-INPUT]";
}

[DebuggerDisplay("[XTREAM-SERIES-DETAILS]")]
public sealed class XtreamSeriesDetails
{
    internal XtreamSeriesDetails(
        IReadOnlyList<XtreamSeasonInput> seasons,
        IReadOnlyList<XtreamEpisodeInput> episodes)
    {
        Seasons = seasons ?? throw new ArgumentNullException(nameof(seasons));
        Episodes = episodes ?? throw new ArgumentNullException(nameof(episodes));
    }

    public IReadOnlyList<XtreamSeasonInput> Seasons { get; }

    public IReadOnlyList<XtreamEpisodeInput> Episodes { get; }

    public override string ToString() => "[XTREAM-SERIES-DETAILS]";
}

[DebuggerDisplay("[XTREAM-LIVE-CATALOG]")]
public sealed class XtreamLiveCatalog
{
    internal XtreamLiveCatalog(
        XtreamProviderPage<XtreamCategoryInput> categories,
        XtreamProviderPage<XtreamStreamInput> streams)
    {
        Categories = categories ?? throw new ArgumentNullException(nameof(categories));
        Streams = streams ?? throw new ArgumentNullException(nameof(streams));
    }

    public XtreamProviderPage<XtreamCategoryInput> Categories { get; }

    public XtreamProviderPage<XtreamStreamInput> Streams { get; }

    public override string ToString() => "[XTREAM-LIVE-CATALOG]";
}

[DebuggerDisplay("[XTREAM-CONTENT-CATALOG]")]
public sealed class XtreamContentCatalog
{
    internal XtreamContentCatalog(
        XtreamAccountStatus account,
        XtreamProviderPage<XtreamCategoryInput> liveCategories,
        XtreamProviderPage<XtreamStreamInput> liveStreams,
        XtreamProviderPage<XtreamCategoryInput> movieCategories,
        XtreamProviderPage<XtreamMovieInput> movies,
        XtreamProviderPage<XtreamCategoryInput> seriesCategories,
        XtreamProviderPage<XtreamSeriesInput> series)
    {
        Account = account ?? throw new ArgumentNullException(nameof(account));
        LiveCategories = liveCategories ?? throw new ArgumentNullException(nameof(liveCategories));
        LiveStreams = liveStreams ?? throw new ArgumentNullException(nameof(liveStreams));
        MovieCategories = movieCategories ?? throw new ArgumentNullException(nameof(movieCategories));
        Movies = movies ?? throw new ArgumentNullException(nameof(movies));
        SeriesCategories = seriesCategories ?? throw new ArgumentNullException(nameof(seriesCategories));
        Series = series ?? throw new ArgumentNullException(nameof(series));
    }

    public XtreamAccountStatus Account { get; }

    public XtreamProviderPage<XtreamCategoryInput> LiveCategories { get; }

    public XtreamProviderPage<XtreamStreamInput> LiveStreams { get; }

    public XtreamProviderPage<XtreamCategoryInput> MovieCategories { get; }

    public XtreamProviderPage<XtreamMovieInput> Movies { get; }

    public XtreamProviderPage<XtreamCategoryInput> SeriesCategories { get; }

    public XtreamProviderPage<XtreamSeriesInput> Series { get; }

    public override string ToString() => "[XTREAM-CONTENT-CATALOG]";
}

public interface IXtreamProviderClient
{
    ValueTask<DomainResult<XtreamLiveCatalog>> LoadLiveCatalogAsync(
        ContentSource source,
        CancellationToken cancellationToken = default);

    ValueTask<DomainResult<XtreamContentCatalog>> LoadContentCatalogAsync(
        ContentSource source,
        CancellationToken cancellationToken = default);

    ValueTask<DomainResult<XtreamSeriesDetails>> LoadSeriesDetailsAsync(
        ContentSource source,
        ProviderItemKey seriesKey,
        CancellationToken cancellationToken = default);
}
