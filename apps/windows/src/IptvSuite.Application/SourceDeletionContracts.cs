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
/// Persists the durable admission boundary and terminal commit for source deletion.
/// </summary>
/// <remarks>
/// Both operations must be idempotent. A successful mark is the durable commit that blocks
/// new source work; completion must retain that pending state until it succeeds.
/// </remarks>
public interface ISourceDeletionLifecycle
{
    ValueTask<SourceDeletionLifecycleOperationResult> MarkPendingAsync(
        SourceId sourceId,
        CancellationToken cancellationToken = default);

    ValueTask<SourceDeletionLifecycleOperationResult> CompletePendingAsync(
        SourceId sourceId,
        CancellationToken cancellationToken = default);
}
