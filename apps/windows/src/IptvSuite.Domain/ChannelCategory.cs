namespace IptvSuite.Domain;

public sealed class ChannelCategory
{
    public const int MaximumProviderKeyLength = 512;
    public const int MaximumNameLength = 256;

    private ChannelCategory(
        CategoryId id,
        SnapshotId snapshotId,
        string? providerKey,
        string normalizedName,
        int sortOrder,
        bool isSynthetic)
    {
        Id = id;
        SnapshotId = snapshotId;
        ProviderKey = providerKey;
        NormalizedName = normalizedName;
        SortOrder = sortOrder;
        IsSynthetic = isSynthetic;
    }

    public CategoryId Id { get; }

    public SnapshotId SnapshotId { get; }

    public string? ProviderKey { get; }

    public string NormalizedName { get; }

    public int SortOrder { get; }

    public bool IsSynthetic { get; }

    public static DomainResult<ChannelCategory> Create(
        CategoryId id,
        SnapshotId snapshotId,
        string? providerKey,
        string? name,
        int sortOrder,
        bool isSynthetic)
    {
        if (id.IsEmpty || snapshotId.IsEmpty ||
            !DomainText.TryNormalizeOptional(
                providerKey,
                MaximumProviderKeyLength,
                out string? normalizedProviderKey) ||
            !DomainText.TryNormalizeRequired(name, MaximumNameLength, out string normalizedName) ||
            (isSynthetic && normalizedProviderKey is not null))
        {
            return DomainResult.Failure<ChannelCategory>(DomainErrorCode.DomainInvariantViolation);
        }

        return DomainResult.Success(
            new ChannelCategory(
                id,
                snapshotId,
                normalizedProviderKey,
                normalizedName,
                sortOrder,
                isSynthetic));
    }

    public override string ToString() => $"[CHANNEL-CATEGORY:{Id}]";
}
