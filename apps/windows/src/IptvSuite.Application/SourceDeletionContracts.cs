using IptvSuite.Domain;

namespace IptvSuite.Application;

/// <summary>
/// Identifies the bounded source-deletion stage that could not converge.
/// </summary>
public enum SourceDeletionFailureStage
{
    None = 0,
    MarkPending = 1,
    PlaybackRelease = 2,
    CompletePending = 3,
    PendingDiscovery = 4,
}

/// <summary>
/// Carries the sanitized outcome of one durable source lifecycle mutation.
/// </summary>
public sealed record SourceDeletionLifecycleOperationResult
{
    private SourceDeletionLifecycleOperationResult(bool isSuccess, DomainError? error)
    {
        IsSuccess = isSuccess;
        Error = error;
    }

    public bool IsSuccess { get; }

    public DomainError? Error { get; }

    public static SourceDeletionLifecycleOperationResult Succeeded() => new(true, null);

    public static SourceDeletionLifecycleOperationResult Failed(DomainErrorCode errorCode) =>
        Failed(DomainError.Create(errorCode));

    public static SourceDeletionLifecycleOperationResult Failed(DomainError error)
    {
        ArgumentNullException.ThrowIfNull(error);
        return new SourceDeletionLifecycleOperationResult(false, error);
    }

    public override string ToString() => IsSuccess
        ? "[SOURCE-DELETION-LIFECYCLE:SUCCESS]"
        : $"[SOURCE-DELETION-LIFECYCLE:{Error!.Code}]";
}

/// <summary>
/// Carries the sanitized, stage-bound outcome of source deletion orchestration.
/// </summary>
public sealed record SourceDeletionResult
{
    private SourceDeletionResult(
        bool isSuccess,
        SourceDeletionFailureStage failureStage,
        DomainError? error)
    {
        IsSuccess = isSuccess;
        FailureStage = failureStage;
        Error = error;
    }

    public bool IsSuccess { get; }

    public SourceDeletionFailureStage FailureStage { get; }

    public DomainError? Error { get; }

    public static SourceDeletionResult Succeeded() =>
        new(true, SourceDeletionFailureStage.None, null);

    public static SourceDeletionResult Failed(
        SourceDeletionFailureStage failureStage,
        DomainError error)
    {
        if (failureStage is SourceDeletionFailureStage.None || !Enum.IsDefined(failureStage))
        {
            throw new ArgumentOutOfRangeException(
                nameof(failureStage),
                failureStage,
                "A source-deletion failure stage is required.");
        }

        ArgumentNullException.ThrowIfNull(error);
        return new SourceDeletionResult(false, failureStage, error);
    }

    public override string ToString() => IsSuccess
        ? "[SOURCE-DELETION:SUCCESS]"
        : $"[SOURCE-DELETION:{FailureStage}:{Error!.Code}]";
}

/// <summary>
/// Carries one bounded, identifier-only page of actionable durable deletion records.
/// </summary>
public sealed record SourceDeletionPendingBatchReadResult
{
    public const int MaximumSourceCount = 32;

    private SourceDeletionPendingBatchReadResult(
        bool isSuccess,
        IReadOnlyList<SourceId> sourceIds,
        SourceId? nextAfterExclusive,
        DomainError? error)
    {
        IsSuccess = isSuccess;
        SourceIds = sourceIds;
        NextAfterExclusive = nextAfterExclusive;
        Error = error;
    }

    public bool IsSuccess { get; }

    public IReadOnlyList<SourceId> SourceIds { get; }

    /// <summary>
    /// Identifies the last returned item when another keyset page is available.
    /// </summary>
    public SourceId? NextAfterExclusive { get; }

    public DomainError? Error { get; }

    public static SourceDeletionPendingBatchReadResult Succeeded(
        IReadOnlyCollection<SourceId> sourceIds,
        SourceId? nextAfterExclusive = null)
    {
        ArgumentNullException.ThrowIfNull(sourceIds);
        SourceId[] copy = sourceIds
            .Take(MaximumSourceCount + 1)
            .ToArray();
        if (copy.Length > MaximumSourceCount)
        {
            throw new ArgumentOutOfRangeException(
                nameof(sourceIds),
                copy.Length,
                "A source-deletion discovery page exceeds its bounded size.");
        }

        if (copy.Any(static sourceId => sourceId.IsEmpty))
        {
            throw new ArgumentException(
                "A source-deletion discovery page cannot contain an empty identifier.",
                nameof(sourceIds));
        }

        if (nextAfterExclusive.HasValue &&
            (copy.Length == 0 || copy[^1] != nextAfterExclusive.Value))
        {
            throw new ArgumentException(
                "A source-deletion continuation must identify the last returned item.",
                nameof(nextAfterExclusive));
        }

        return new SourceDeletionPendingBatchReadResult(
            true,
            Array.AsReadOnly(copy),
            nextAfterExclusive,
            null);
    }

    public static SourceDeletionPendingBatchReadResult Failed(DomainErrorCode errorCode) =>
        Failed(DomainError.Create(errorCode));

    public static SourceDeletionPendingBatchReadResult Failed(DomainError error)
    {
        ArgumentNullException.ThrowIfNull(error);
        return new SourceDeletionPendingBatchReadResult(
            false,
            Array.Empty<SourceId>(),
            null,
            error);
    }

    public override string ToString() => IsSuccess
        ? $"[SOURCE-DELETION-PENDING-BATCH:SUCCESS:{SourceIds.Count}:{NextAfterExclusive.HasValue}]"
        : $"[SOURCE-DELETION-PENDING-BATCH:{Error!.Code}]";
}

/// <summary>
/// Carries the sanitized durable starting point for one bounded reconciliation pass.
/// </summary>
public sealed record SourceDeletionPendingCursorReadResult
{
    private SourceDeletionPendingCursorReadResult(
        bool isSuccess,
        SourceId? afterExclusive,
        DomainError? error)
    {
        IsSuccess = isSuccess;
        AfterExclusive = afterExclusive;
        Error = error;
    }

    public bool IsSuccess { get; }

    public SourceId? AfterExclusive { get; }

    public DomainError? Error { get; }

    public static SourceDeletionPendingCursorReadResult Succeeded(
        SourceId? afterExclusive = null)
    {
        if (afterExclusive.HasValue && afterExclusive.Value.IsEmpty)
        {
            throw new ArgumentException(
                "A source-deletion reconciliation cursor cannot be empty.",
                nameof(afterExclusive));
        }

        return new SourceDeletionPendingCursorReadResult(
            true,
            afterExclusive,
            null);
    }

    public static SourceDeletionPendingCursorReadResult Failed(DomainErrorCode errorCode) =>
        Failed(DomainError.Create(errorCode));

    public static SourceDeletionPendingCursorReadResult Failed(DomainError error)
    {
        ArgumentNullException.ThrowIfNull(error);
        return new SourceDeletionPendingCursorReadResult(false, null, error);
    }

    public override string ToString() => IsSuccess
        ? $"[SOURCE-DELETION-PENDING-CURSOR:SUCCESS:{AfterExclusive.HasValue}]"
        : $"[SOURCE-DELETION-PENDING-CURSOR:{Error!.Code}]";
}

/// <summary>
/// Summarizes one bounded startup reconciliation pass without exposing source identifiers.
/// </summary>
public sealed record SourceDeletionReconciliationResult
{
    public const int MaximumAttemptCount = 100;

    private SourceDeletionReconciliationResult(
        int attemptedCount,
        int completedCount,
        int failedCount,
        bool hasRemaining,
        SourceDeletionFailureStage firstFailureStage,
        DomainError? firstError)
    {
        AttemptedCount = attemptedCount;
        CompletedCount = completedCount;
        FailedCount = failedCount;
        HasRemaining = hasRemaining;
        FirstFailureStage = firstFailureStage;
        FirstError = firstError;
    }

    public bool IsSuccess =>
        FailedCount == 0 && !HasRemaining && FirstError is null;

    public int AttemptedCount { get; }

    public int CompletedCount { get; }

    public int FailedCount { get; }

    public bool HasRemaining { get; }

    public SourceDeletionFailureStage FirstFailureStage { get; }

    public DomainError? FirstError { get; }

    internal static SourceDeletionReconciliationResult Create(
        int attemptedCount,
        int completedCount,
        int failedCount,
        bool hasRemaining,
        SourceDeletionFailureStage firstFailureStage,
        DomainError? firstError)
    {
        if (attemptedCount < 0 || attemptedCount > MaximumAttemptCount)
        {
            throw new ArgumentOutOfRangeException(nameof(attemptedCount));
        }

        if (completedCount < 0 || failedCount < 0 ||
            completedCount + failedCount != attemptedCount)
        {
            throw new ArgumentOutOfRangeException(
                nameof(completedCount),
                "Reconciliation counts are inconsistent.");
        }

        bool hasFailure = firstFailureStage != SourceDeletionFailureStage.None;
        if (hasFailure != (firstError is not null) ||
            (hasFailure && !Enum.IsDefined(firstFailureStage)) ||
            (failedCount > 0 && !hasFailure) ||
            ((failedCount > 0 || firstError is not null) && !hasRemaining))
        {
            throw new ArgumentException(
                "Reconciliation failure metadata is inconsistent.",
                nameof(firstFailureStage));
        }

        return new SourceDeletionReconciliationResult(
            attemptedCount,
            completedCount,
            failedCount,
            hasRemaining,
            firstFailureStage,
            firstError);
    }

    public override string ToString()
    {
        string outcome = IsSuccess ? "SUCCESS" : "INCOMPLETE";
        string failure = FirstError is null
            ? "NONE"
            : $"{FirstFailureStage}:{FirstError.Code}";
        return $"[SOURCE-DELETION-RECONCILIATION:{outcome}:{AttemptedCount}:{CompletedCount}:{FailedCount}:{HasRemaining}:{failure}]";
    }
}

/// <summary>
/// Persists the durable admission boundary and terminal commit for source deletion.
/// </summary>
/// <remarks>
/// Both operations must be idempotent. A successful mark is the durable commit that blocks
/// new source work; completion must retain that pending state until it succeeds.
/// </remarks>
public interface ISourceDeletionLifecycle
{
    ValueTask<SourceDeletionPendingCursorReadResult> ReadPendingCursorAsync(
        CancellationToken cancellationToken = default);

    ValueTask<SourceDeletionLifecycleOperationResult> AdvancePendingCursorAsync(
        SourceId sourceId,
        CancellationToken cancellationToken = default);

    ValueTask<SourceDeletionPendingBatchReadResult> ReadPendingBatchAsync(
        SourceId? afterExclusive = null,
        CancellationToken cancellationToken = default);

    ValueTask<SourceDeletionLifecycleOperationResult> MarkPendingAsync(
        SourceId sourceId,
        CancellationToken cancellationToken = default);

    ValueTask<SourceDeletionLifecycleOperationResult> CompletePendingAsync(
        SourceId sourceId,
        CancellationToken cancellationToken = default);
}
