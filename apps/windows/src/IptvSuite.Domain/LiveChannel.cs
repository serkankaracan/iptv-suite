using System.Text.Json.Serialization;

namespace IptvSuite.Domain;

[JsonConverter(typeof(JsonStringEnumConverter<ChannelContainerHint>))]
public enum ChannelContainerHint
{
    Hls,
    MpegTs,
}

[Flags]
[JsonConverter(typeof(JsonStringEnumConverter<ChannelNormalizationWarnings>))]
public enum ChannelNormalizationWarnings
{
    None = 0,
    InvalidNumber = 1 << 0,
    MissingGroup = 1 << 1,
    DuplicateProviderIdentifier = 1 << 2,
}

public sealed class LiveChannel
{
    private const ChannelNormalizationWarnings KnownWarnings =
        ChannelNormalizationWarnings.InvalidNumber |
        ChannelNormalizationWarnings.MissingGroup |
        ChannelNormalizationWarnings.DuplicateProviderIdentifier;

    public const int MaximumProviderKeyLength = 512;
    public const int MaximumNameLength = 256;

    private LiveChannel(
        ChannelId id,
        ChannelStableKey stableKey,
        SnapshotId snapshotId,
        CategoryId categoryId,
        string? providerKey,
        ProviderItemKey? providerPlaybackKey,
        string name,
        int? number,
        ProtectedLocatorReference? logoReference,
        ProtectedLocatorReference? streamReference,
        ChannelContainerHint? containerHint,
        bool? isAdultHint,
        ChannelNormalizationWarnings normalizationWarnings)
    {
        Id = id;
        StableKey = stableKey;
        SnapshotId = snapshotId;
        CategoryId = categoryId;
        ProviderKey = providerKey;
        ProviderPlaybackKey = providerPlaybackKey;
        Name = name;
        Number = number;
        LogoReference = logoReference;
        StreamReference = streamReference;
        ContainerHint = containerHint;
        IsAdultHint = isAdultHint;
        NormalizationWarnings = normalizationWarnings;
    }

    public ChannelId Id { get; }

    public ChannelStableKey StableKey { get; }

    public SnapshotId SnapshotId { get; }

    public CategoryId CategoryId { get; }

    public string? ProviderKey { get; }

    public ProviderItemKey? ProviderPlaybackKey { get; }

    public string Name { get; }

    public int? Number { get; }

    public ProtectedLocatorReference? LogoReference { get; }

    public ProtectedLocatorReference? StreamReference { get; }

    public ChannelContainerHint? ContainerHint { get; }

    public bool? IsAdultHint { get; }

    public ChannelNormalizationWarnings NormalizationWarnings { get; }

    public static DomainResult<LiveChannel> Create(
        ChannelId id,
        ChannelStableKey stableKey,
        SnapshotId snapshotId,
        CategoryId categoryId,
        string? providerKey,
        ProviderItemKey? providerPlaybackKey,
        string? name,
        int? number,
        ProtectedLocatorReference? logoReference,
        ProtectedLocatorReference? streamReference,
        ChannelContainerHint? containerHint,
        bool? isAdultHint,
        ChannelNormalizationWarnings normalizationWarnings)
    {
        if (id.IsEmpty || stableKey.IsEmpty || snapshotId.IsEmpty || categoryId.IsEmpty ||
            !DomainText.TryNormalizeOptional(
                providerKey,
                MaximumProviderKeyLength,
                out string? normalizedProviderKey) ||
            !DomainText.TryNormalizeRequired(name, MaximumNameLength, out string normalizedName) ||
            (providerPlaybackKey.HasValue && providerPlaybackKey.Value.IsEmpty) ||
            (providerPlaybackKey.HasValue == (streamReference is not null)) ||
            (number.HasValue && number.Value <= 0) ||
            (containerHint.HasValue && !Enum.IsDefined(containerHint.Value)) ||
            (normalizationWarnings & ~KnownWarnings) != 0)
        {
            return DomainResult.Failure<LiveChannel>(DomainErrorCode.DomainInvariantViolation);
        }

        return DomainResult.Success(
            new LiveChannel(
                id,
                stableKey,
                snapshotId,
                categoryId,
                normalizedProviderKey,
                providerPlaybackKey,
                normalizedName,
                number,
                logoReference,
                streamReference,
                containerHint,
                isAdultHint,
                normalizationWarnings));
    }

    public override string ToString() => $"[LIVE-CHANNEL:{Id}]";
}
