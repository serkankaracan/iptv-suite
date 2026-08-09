using System.Text.Json.Serialization;

namespace IptvSuite.Domain;

[JsonConverter(typeof(JsonStringEnumConverter<ContentSourceStatus>))]
public enum ContentSourceStatus
{
    Draft,
    Testing,
    Syncing,
    Ready,
    Failed,
    Disabled,
    DeletionPending,
}

public sealed class ContentSource
{
    public const int MaximumDisplayNameLength = 100;

    private ContentSource(
        SourceId id,
        string displayName,
        SourceConfiguration configuration,
        ContentSourceStatus status,
        SnapshotId? activeSnapshotId,
        DateTimeOffset createdAt,
        DateTimeOffset updatedAt,
        DateTimeOffset? lastSuccessfulSyncAt,
        DomainErrorCode? lastErrorCode)
    {
        Id = id;
        DisplayName = displayName;
        Configuration = configuration;
        Status = status;
        ActiveSnapshotId = activeSnapshotId;
        CreatedAt = createdAt;
        UpdatedAt = updatedAt;
        LastSuccessfulSyncAt = lastSuccessfulSyncAt;
        LastErrorCode = lastErrorCode;
    }

    public SourceId Id { get; }

    public string DisplayName { get; }

    public SourceConfiguration Configuration { get; }

    public SourceKind Kind => Configuration.Kind;

    public SafeEndpoint SafeEndpoint => Configuration.SafeEndpoint;

    public ContentSourceStatus Status { get; }

    public SnapshotId? ActiveSnapshotId { get; }

    public DateTimeOffset CreatedAt { get; }

    public DateTimeOffset UpdatedAt { get; }

    public DateTimeOffset? LastSuccessfulSyncAt { get; }

    public DomainErrorCode? LastErrorCode { get; }

    public static DomainResult<ContentSource> Create(
        SourceId id,
        string? displayName,
        SourceConfiguration? configuration,
        ContentSourceStatus status,
        DateTimeOffset createdAt,
        DateTimeOffset updatedAt,
        SnapshotId? activeSnapshotId = null,
        DateTimeOffset? lastSuccessfulSyncAt = null,
        DomainErrorCode? lastErrorCode = null)
    {
        bool hasActiveSnapshot = activeSnapshotId.HasValue && !activeSnapshotId.Value.IsEmpty;
        bool hasSuccessfulSync = lastSuccessfulSyncAt.HasValue;
        bool hasLastError = lastErrorCode.HasValue;

        if (id.IsEmpty || configuration is null ||
            (configuration is not XtreamSourceConfiguration &&
                configuration is not RemotePlaylistSourceConfiguration) ||
            !Enum.IsDefined(status) ||
            createdAt == default || updatedAt == default || updatedAt < createdAt ||
            !DomainText.TryNormalizeRequired(
                displayName,
                MaximumDisplayNameLength,
                out string normalizedDisplayName) ||
            (activeSnapshotId.HasValue && activeSnapshotId.Value.IsEmpty) ||
            hasActiveSnapshot != hasSuccessfulSync ||
            (status == ContentSourceStatus.Ready && !hasActiveSnapshot) ||
            (status == ContentSourceStatus.Failed) != hasLastError ||
            (lastErrorCode.HasValue && !Enum.IsDefined(lastErrorCode.Value)) ||
            (lastSuccessfulSyncAt.HasValue &&
                (lastSuccessfulSyncAt.Value < createdAt || lastSuccessfulSyncAt.Value > updatedAt)))
        {
            return DomainResult.Failure<ContentSource>(DomainErrorCode.DomainInvariantViolation);
        }

        return DomainResult.Success(
            new ContentSource(
                id,
                normalizedDisplayName,
                configuration,
                status,
                activeSnapshotId,
                createdAt.ToUniversalTime(),
                updatedAt.ToUniversalTime(),
                lastSuccessfulSyncAt?.ToUniversalTime(),
                lastErrorCode));
    }

    public override string ToString() => $"[CONTENT-SOURCE:{Id}]";
}
