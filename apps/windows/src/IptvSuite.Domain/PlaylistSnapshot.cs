namespace IptvSuite.Domain;

public enum PlaylistSnapshotState
{
    Importing,
    Complete,
    Rejected,
}

public sealed class PlaylistSnapshot
{
    public const int MaximumEntityTagLength = 512;

    private PlaylistSnapshot(
        SnapshotId id,
        SourceId sourceId,
        DateTimeOffset retrievedAt,
        string contentHash,
        string? entityTag,
        DateTimeOffset? lastModified,
        int parserVersion,
        int normalizationVersion,
        int schemaVersion,
        int itemCount,
        int warningCount,
        PlaylistSnapshotState state)
    {
        Id = id;
        SourceId = sourceId;
        RetrievedAt = retrievedAt;
        ContentHash = contentHash;
        EntityTag = entityTag;
        LastModified = lastModified;
        ParserVersion = parserVersion;
        NormalizationVersion = normalizationVersion;
        SchemaVersion = schemaVersion;
        ItemCount = itemCount;
        WarningCount = warningCount;
        State = state;
    }

    public SnapshotId Id { get; }

    public SourceId SourceId { get; }

    public DateTimeOffset RetrievedAt { get; }

    public string ContentHash { get; }

    public string? EntityTag { get; }

    public DateTimeOffset? LastModified { get; }

    public int ParserVersion { get; }

    public int NormalizationVersion { get; }

    public int SchemaVersion { get; }

    public int ItemCount { get; }

    public int WarningCount { get; }

    public PlaylistSnapshotState State { get; }

    public static DomainResult<PlaylistSnapshot> Create(
        SnapshotId id,
        SourceId sourceId,
        DateTimeOffset retrievedAt,
        string? contentHash,
        int parserVersion,
        int normalizationVersion,
        int schemaVersion,
        int itemCount,
        int warningCount,
        PlaylistSnapshotState state,
        string? entityTag = null,
        DateTimeOffset? lastModified = null)
    {
        if (id.IsEmpty || sourceId.IsEmpty || retrievedAt == default ||
            parserVersion <= 0 || normalizationVersion <= 0 || schemaVersion <= 0 ||
            itemCount < 0 || warningCount < 0 || !Enum.IsDefined(state) ||
            (lastModified.HasValue && lastModified.Value == default) ||
            !DomainText.TryNormalizeSha256(contentHash, out string normalizedContentHash) ||
            !TryValidateEntityTag(entityTag, out string? validatedEntityTag))
        {
            return DomainResult.Failure<PlaylistSnapshot>(DomainErrorCode.DomainInvariantViolation);
        }

        return DomainResult.Success(
            new PlaylistSnapshot(
                id,
                sourceId,
                retrievedAt.ToUniversalTime(),
                normalizedContentHash,
                validatedEntityTag,
                lastModified?.ToUniversalTime(),
                parserVersion,
                normalizationVersion,
                schemaVersion,
                itemCount,
                warningCount,
                state));
    }

    public override string ToString() => $"[PLAYLIST-SNAPSHOT:{Id}]";

    private static bool TryValidateEntityTag(string? value, out string? validated)
    {
        validated = null;
        if (value is null)
        {
            return true;
        }

        if (value.Length is < 2 or > MaximumEntityTagLength)
        {
            return false;
        }

        ReadOnlySpan<char> entityTag = value.AsSpan();
        if (entityTag.StartsWith("W/", StringComparison.Ordinal))
        {
            entityTag = entityTag[2..];
        }

        if (entityTag.Length < 2 || entityTag[0] != '"' || entityTag[^1] != '"')
        {
            return false;
        }

        foreach (char character in entityTag[1..^1])
        {
            bool isAllowed = character == '\u0021' ||
                character is >= '\u0023' and <= '\u007E' or >= '\u00A0' and <= '\u00FF';
            if (!isAllowed)
            {
                return false;
            }
        }

        validated = value;
        return true;
    }
}
