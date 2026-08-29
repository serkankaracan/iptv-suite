using System.Globalization;

namespace IptvSuite.Domain;

public readonly record struct SourceId
{
    private SourceId(Guid value) => Value = value;

    public Guid Value { get; }

    public bool IsEmpty => Value == Guid.Empty;

    public static DomainResult<SourceId> Create(Guid value) => value == Guid.Empty
        ? DomainResult.Failure<SourceId>(DomainErrorCode.DomainInvariantViolation)
        : DomainResult.Success(new SourceId(value));

    public static SourceId Generate() => new(Guid.NewGuid());

    public override string ToString() => Value.ToString("D", CultureInfo.InvariantCulture);
}

public readonly record struct SourceConfigurationId
{
    private SourceConfigurationId(Guid value) => Value = value;

    public Guid Value { get; }

    public bool IsEmpty => Value == Guid.Empty;

    public static DomainResult<SourceConfigurationId> Create(Guid value) => value == Guid.Empty
        ? DomainResult.Failure<SourceConfigurationId>(DomainErrorCode.DomainInvariantViolation)
        : DomainResult.Success(new SourceConfigurationId(value));

    public static SourceConfigurationId Generate() => new(Guid.NewGuid());

    public override string ToString() => Value.ToString("D", CultureInfo.InvariantCulture);
}

public readonly record struct SnapshotId
{
    private SnapshotId(Guid value) => Value = value;

    public Guid Value { get; }

    public bool IsEmpty => Value == Guid.Empty;

    public static DomainResult<SnapshotId> Create(Guid value) => value == Guid.Empty
        ? DomainResult.Failure<SnapshotId>(DomainErrorCode.DomainInvariantViolation)
        : DomainResult.Success(new SnapshotId(value));

    public static SnapshotId Generate() => new(Guid.NewGuid());

    public override string ToString() => Value.ToString("D", CultureInfo.InvariantCulture);
}

public readonly record struct CategoryId
{
    private CategoryId(Guid value) => Value = value;

    public Guid Value { get; }

    public bool IsEmpty => Value == Guid.Empty;

    public static DomainResult<CategoryId> Create(Guid value) => value == Guid.Empty
        ? DomainResult.Failure<CategoryId>(DomainErrorCode.DomainInvariantViolation)
        : DomainResult.Success(new CategoryId(value));

    public static CategoryId Generate() => new(Guid.NewGuid());

    public override string ToString() => Value.ToString("D", CultureInfo.InvariantCulture);
}

public readonly record struct ChannelId
{
    private ChannelId(Guid value) => Value = value;

    public Guid Value { get; }

    public bool IsEmpty => Value == Guid.Empty;

    public static DomainResult<ChannelId> Create(Guid value) => value == Guid.Empty
        ? DomainResult.Failure<ChannelId>(DomainErrorCode.DomainInvariantViolation)
        : DomainResult.Success(new ChannelId(value));

    public static ChannelId Generate() => new(Guid.NewGuid());

    public override string ToString() => Value.ToString("D", CultureInfo.InvariantCulture);
}

public readonly record struct MovieId
{
    private MovieId(Guid value) => Value = value;

    public Guid Value { get; }

    public bool IsEmpty => Value == Guid.Empty;

    public static DomainResult<MovieId> Create(Guid value) => value == Guid.Empty
        ? DomainResult.Failure<MovieId>(DomainErrorCode.DomainInvariantViolation)
        : DomainResult.Success(new MovieId(value));

    public static MovieId Generate() => new(Guid.NewGuid());

    public override string ToString() => Value.ToString("D", CultureInfo.InvariantCulture);
}

public readonly record struct SeriesId
{
    private SeriesId(Guid value) => Value = value;

    public Guid Value { get; }

    public bool IsEmpty => Value == Guid.Empty;

    public static DomainResult<SeriesId> Create(Guid value) => value == Guid.Empty
        ? DomainResult.Failure<SeriesId>(DomainErrorCode.DomainInvariantViolation)
        : DomainResult.Success(new SeriesId(value));

    public static SeriesId Generate() => new(Guid.NewGuid());

    public override string ToString() => Value.ToString("D", CultureInfo.InvariantCulture);
}

public readonly record struct SeasonId
{
    private SeasonId(Guid value) => Value = value;

    public Guid Value { get; }

    public bool IsEmpty => Value == Guid.Empty;

    public static DomainResult<SeasonId> Create(Guid value) => value == Guid.Empty
        ? DomainResult.Failure<SeasonId>(DomainErrorCode.DomainInvariantViolation)
        : DomainResult.Success(new SeasonId(value));

    public static SeasonId Generate() => new(Guid.NewGuid());

    public override string ToString() => Value.ToString("D", CultureInfo.InvariantCulture);
}

public readonly record struct EpisodeId
{
    private EpisodeId(Guid value) => Value = value;

    public Guid Value { get; }

    public bool IsEmpty => Value == Guid.Empty;

    public static DomainResult<EpisodeId> Create(Guid value) => value == Guid.Empty
        ? DomainResult.Failure<EpisodeId>(DomainErrorCode.DomainInvariantViolation)
        : DomainResult.Success(new EpisodeId(value));

    public static EpisodeId Generate() => new(Guid.NewGuid());

    public override string ToString() => Value.ToString("D", CultureInfo.InvariantCulture);
}
