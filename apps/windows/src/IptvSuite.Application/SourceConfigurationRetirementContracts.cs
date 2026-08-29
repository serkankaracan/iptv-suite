using System.Diagnostics;

namespace IptvSuite.Application;

[DebuggerDisplay("[SOURCE-CONFIGURATION-RETIREMENT-RECONCILIATION]")]
public sealed class SourceConfigurationRetirementReconciliationResult
{
    private SourceConfigurationRetirementReconciliationResult(
        int attemptedCount,
        int completedCount,
        bool hasRemaining,
        DomainError? error)
    {
        if (attemptedCount < 0 || completedCount < 0 || completedCount > attemptedCount ||
            (error is not null && !hasRemaining))
        {
            throw new ArgumentException("Retirement reconciliation counts are inconsistent.");
        }

        AttemptedCount = attemptedCount;
        CompletedCount = completedCount;
        HasRemaining = hasRemaining;
        Error = error;
    }

    public int AttemptedCount { get; }

    public int CompletedCount { get; }

    public bool HasRemaining { get; }

    public DomainError? Error { get; }

    public bool IsSuccess => Error is null && !HasRemaining;

    public static SourceConfigurationRetirementReconciliationResult Completed(
        int attemptedCount,
        int completedCount,
        bool hasRemaining = false) =>
        new(attemptedCount, completedCount, hasRemaining, null);

    public static SourceConfigurationRetirementReconciliationResult Failed(
        int attemptedCount,
        int completedCount,
        DomainError error) =>
        new(
            attemptedCount,
            completedCount,
            hasRemaining: true,
            error ?? throw new ArgumentNullException(nameof(error)));

    public override string ToString() =>
        "[SOURCE-CONFIGURATION-RETIREMENT-RECONCILIATION]";
}

public interface ISourceConfigurationRetirementReconciler
{
    ValueTask<SourceConfigurationRetirementReconciliationResult> ReconcileAsync(
        CancellationToken cancellationToken = default);
}
