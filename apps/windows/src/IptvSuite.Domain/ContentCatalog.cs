using System.Text.Json.Serialization;

namespace IptvSuite.Domain;

[JsonConverter(typeof(JsonStringEnumConverter<ContentKind>))]
public enum ContentKind
{
    LiveTv = 1,
    Movie = 2,
    Series = 3,
    Episode = 4,
}

public sealed class Movie
{
    private Movie(
        MovieId id,
        SnapshotId snapshotId,
        CategoryId? categoryId,
        ProviderItemKey providerPlaybackKey,
        string name,
        string? containerExtension,
        bool? isAdultHint)
    {
        Id = id;
        SnapshotId = snapshotId;
        CategoryId = categoryId;
        ProviderPlaybackKey = providerPlaybackKey;
        Name = name;
        ContainerExtension = containerExtension;
        IsAdultHint = isAdultHint;
    }

    public MovieId Id { get; }

    public SnapshotId SnapshotId { get; }

    public CategoryId? CategoryId { get; }

    public ProviderItemKey ProviderPlaybackKey { get; }

    public string Name { get; }

    public string? ContainerExtension { get; }

    public bool? IsAdultHint { get; }

    public static DomainResult<Movie> Create(
        MovieId id,
        SnapshotId snapshotId,
        CategoryId? categoryId,
        ProviderItemKey providerPlaybackKey,
        string? name,
        string? containerExtension,
        bool? isAdultHint)
    {
        if (id.IsEmpty || snapshotId.IsEmpty || categoryId?.IsEmpty == true ||
            providerPlaybackKey.IsEmpty ||
            !DomainText.TryNormalizeRequired(name, 256, out string normalizedName) ||
            !DomainText.TryNormalizeOptional(containerExtension, 32, out string? normalizedContainer))
        {
            return DomainResult.Failure<Movie>(DomainErrorCode.DomainInvariantViolation);
        }

        return DomainResult.Success(new Movie(
            id,
            snapshotId,
            categoryId,
            providerPlaybackKey,
            normalizedName,
            normalizedContainer,
            isAdultHint));
    }

    public override string ToString() => $"[MOVIE:{Id}]";
}

public sealed class Series
{
    private Series(
        SeriesId id,
        SnapshotId snapshotId,
        CategoryId? categoryId,
        ProviderItemKey providerKey,
        string name,
        bool? isAdultHint)
    {
        Id = id;
        SnapshotId = snapshotId;
        CategoryId = categoryId;
        ProviderKey = providerKey;
        Name = name;
        IsAdultHint = isAdultHint;
    }

    public SeriesId Id { get; }

    public SnapshotId SnapshotId { get; }

    public CategoryId? CategoryId { get; }

    public ProviderItemKey ProviderKey { get; }

    public string Name { get; }

    public bool? IsAdultHint { get; }

    public static DomainResult<Series> Create(
        SeriesId id,
        SnapshotId snapshotId,
        CategoryId? categoryId,
        ProviderItemKey providerKey,
        string? name,
        bool? isAdultHint)
    {
        if (id.IsEmpty || snapshotId.IsEmpty || categoryId?.IsEmpty == true ||
            providerKey.IsEmpty ||
            !DomainText.TryNormalizeRequired(name, 256, out string normalizedName))
        {
            return DomainResult.Failure<Series>(DomainErrorCode.DomainInvariantViolation);
        }

        return DomainResult.Success(new Series(
            id,
            snapshotId,
            categoryId,
            providerKey,
            normalizedName,
            isAdultHint));
    }

    public override string ToString() => $"[SERIES:{Id}]";
}

public sealed class Season
{
    private Season(SeasonId id, SnapshotId snapshotId, SeriesId seriesId, int number, string name)
    {
        Id = id;
        SnapshotId = snapshotId;
        SeriesId = seriesId;
        Number = number;
        Name = name;
    }

    public SeasonId Id { get; }

    public SnapshotId SnapshotId { get; }

    public SeriesId SeriesId { get; }

    public int Number { get; }

    public string Name { get; }

    public static DomainResult<Season> Create(
        SeasonId id,
        SnapshotId snapshotId,
        SeriesId seriesId,
        int number,
        string? name)
    {
        if (id.IsEmpty || snapshotId.IsEmpty || seriesId.IsEmpty || number < 0 ||
            !DomainText.TryNormalizeRequired(name, 256, out string normalizedName))
        {
            return DomainResult.Failure<Season>(DomainErrorCode.DomainInvariantViolation);
        }

        return DomainResult.Success(new Season(id, snapshotId, seriesId, number, normalizedName));
    }

    public override string ToString() => $"[SEASON:{Id}]";
}

public sealed class Episode
{
    private Episode(
        EpisodeId id,
        SnapshotId snapshotId,
        SeasonId seasonId,
        ProviderItemKey providerPlaybackKey,
        int number,
        string name,
        string? containerExtension,
        TimeSpan? duration)
    {
        Id = id;
        SnapshotId = snapshotId;
        SeasonId = seasonId;
        ProviderPlaybackKey = providerPlaybackKey;
        Number = number;
        Name = name;
        ContainerExtension = containerExtension;
        Duration = duration;
    }

    public EpisodeId Id { get; }

    public SnapshotId SnapshotId { get; }

    public SeasonId SeasonId { get; }

    public ProviderItemKey ProviderPlaybackKey { get; }

    public int Number { get; }

    public string Name { get; }

    public string? ContainerExtension { get; }

    public TimeSpan? Duration { get; }

    public static DomainResult<Episode> Create(
        EpisodeId id,
        SnapshotId snapshotId,
        SeasonId seasonId,
        ProviderItemKey providerPlaybackKey,
        int number,
        string? name,
        string? containerExtension,
        TimeSpan? duration)
    {
        if (id.IsEmpty || snapshotId.IsEmpty || seasonId.IsEmpty ||
            providerPlaybackKey.IsEmpty || number <= 0 ||
            duration is { } value && (value <= TimeSpan.Zero || value > TimeSpan.FromDays(2)) ||
            !DomainText.TryNormalizeRequired(name, 256, out string normalizedName) ||
            !DomainText.TryNormalizeOptional(containerExtension, 32, out string? normalizedContainer))
        {
            return DomainResult.Failure<Episode>(DomainErrorCode.DomainInvariantViolation);
        }

        return DomainResult.Success(new Episode(
            id,
            snapshotId,
            seasonId,
            providerPlaybackKey,
            number,
            normalizedName,
            normalizedContainer,
            duration));
    }

    public override string ToString() => $"[EPISODE:{Id}]";
}
