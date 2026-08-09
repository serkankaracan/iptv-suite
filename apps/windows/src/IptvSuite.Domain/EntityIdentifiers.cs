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
