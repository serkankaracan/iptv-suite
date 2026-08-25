using System.Collections.ObjectModel;
using System.Text.Json.Serialization;

namespace IptvSuite.Application;

public sealed class PlaybackReconnectPolicyOptions
{
    public const int MaximumAllowedAttempts = 3;

    public static readonly TimeSpan MaximumAllowedTotalBudget = TimeSpan.FromSeconds(30);

    public static readonly TimeSpan MaximumAllowedJitter = TimeSpan.FromMilliseconds(250);

    private static readonly TimeSpan[] AllowedBaseDelays =
    [
        TimeSpan.FromSeconds(1),
        TimeSpan.FromSeconds(2),
        TimeSpan.FromSeconds(4),
    ];

    public PlaybackReconnectPolicyOptions()
        : this(
            MaximumAllowedAttempts,
            MaximumAllowedTotalBudget,
            AllowedBaseDelays,
            MaximumAllowedJitter)
    {
    }

    public PlaybackReconnectPolicyOptions(
        int maximumAttempts,
        TimeSpan totalBudget,
        IReadOnlyList<TimeSpan> baseDelays,
        TimeSpan maximumJitter)
    {
        if (maximumAttempts is <= 0 or > MaximumAllowedAttempts)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumAttempts));
        }

        if (totalBudget <= TimeSpan.Zero || totalBudget > MaximumAllowedTotalBudget)
        {
            throw new ArgumentOutOfRangeException(nameof(totalBudget));
        }

        ArgumentNullException.ThrowIfNull(baseDelays);
        if (baseDelays.Count != maximumAttempts)
        {
            throw new ArgumentException(
                "The reconnect delay schedule must contain one entry per attempt.",
                nameof(baseDelays));
        }

        var copiedDelays = new TimeSpan[maximumAttempts];
        for (int ordinal = 0; ordinal < maximumAttempts; ordinal++)
        {
            TimeSpan delay = baseDelays[ordinal];
            if (delay != AllowedBaseDelays[ordinal])
            {
                throw new ArgumentOutOfRangeException(
                    nameof(baseDelays),
                    "The reconnect delay schedule must be the bounded 1s/2s/4s prefix.");
            }

            copiedDelays[ordinal] = delay;
        }

        if (maximumJitter < TimeSpan.Zero || maximumJitter > MaximumAllowedJitter)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumJitter));
        }

        MaximumAttempts = maximumAttempts;
        TotalBudget = totalBudget;
        BaseDelays = new ReadOnlyCollection<TimeSpan>(copiedDelays);
        MaximumJitter = maximumJitter;
    }

    public int MaximumAttempts { get; }

    public TimeSpan TotalBudget { get; }

    public IReadOnlyList<TimeSpan> BaseDelays { get; }

    public TimeSpan MaximumJitter { get; }

    public override string ToString() => "[PLAYBACK-RECONNECT-POLICY-OPTIONS]";
}

[JsonConverter(typeof(JsonStringEnumConverter<PlaybackReconnectDecisionKind>))]
public enum PlaybackReconnectDecisionKind
{
    DoNotRetry,
    RetryAfterDelay,
    Exhausted,
}

public sealed record PlaybackReconnectDecision
{
    private PlaybackReconnectDecision(
        PlaybackReconnectDecisionKind kind,
        int nextAttemptNumber,
        TimeSpan delay,
        DomainErrorCode? terminalErrorCode)
    {
        if (!Enum.IsDefined(kind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind));
        }

        bool valid = kind switch
        {
            PlaybackReconnectDecisionKind.DoNotRetry =>
                nextAttemptNumber == 0 &&
                delay == TimeSpan.Zero &&
                terminalErrorCode.HasValue &&
                Enum.IsDefined(terminalErrorCode.Value),
            PlaybackReconnectDecisionKind.RetryAfterDelay =>
                IsValidRetryDelay(nextAttemptNumber, delay) &&
                !terminalErrorCode.HasValue,
            PlaybackReconnectDecisionKind.Exhausted =>
                nextAttemptNumber == 0 &&
                delay == TimeSpan.Zero &&
                terminalErrorCode == DomainErrorCode.ReconnectExhausted,
            _ => false,
        };
        if (!valid)
        {
            throw new ArgumentException("The reconnect decision invariant is invalid.");
        }

        Kind = kind;
        NextAttemptNumber = nextAttemptNumber;
        Delay = delay;
        TerminalErrorCode = terminalErrorCode;
    }

    private static bool IsValidRetryDelay(int nextAttemptNumber, TimeSpan delay)
    {
        TimeSpan baseDelay = nextAttemptNumber switch
        {
            1 => TimeSpan.FromSeconds(1),
            2 => TimeSpan.FromSeconds(2),
            3 => TimeSpan.FromSeconds(4),
            _ => TimeSpan.Zero,
        };
        return baseDelay > TimeSpan.Zero &&
            delay >= baseDelay &&
            delay <= baseDelay + PlaybackReconnectPolicyOptions.MaximumAllowedJitter;
    }

    public PlaybackReconnectDecisionKind Kind { get; }

    public int NextAttemptNumber { get; }

    public TimeSpan Delay { get; }

    public DomainErrorCode? TerminalErrorCode { get; }

    internal static PlaybackReconnectDecision DoNotRetry(DomainErrorCode terminalErrorCode) =>
        new(
            PlaybackReconnectDecisionKind.DoNotRetry,
            nextAttemptNumber: 0,
            TimeSpan.Zero,
            terminalErrorCode);

    internal static PlaybackReconnectDecision RetryAfterDelay(
        int nextAttemptNumber,
        TimeSpan delay) =>
        new(
            PlaybackReconnectDecisionKind.RetryAfterDelay,
            nextAttemptNumber,
            delay,
            terminalErrorCode: null);

    internal static PlaybackReconnectDecision Exhausted() =>
        new(
            PlaybackReconnectDecisionKind.Exhausted,
            nextAttemptNumber: 0,
            TimeSpan.Zero,
            DomainErrorCode.ReconnectExhausted);

    public override string ToString() => Kind switch
    {
        PlaybackReconnectDecisionKind.RetryAfterDelay =>
            $"[PLAYBACK-RECONNECT-DECISION:{Kind}:{NextAttemptNumber}]",
        _ => $"[PLAYBACK-RECONNECT-DECISION:{Kind}:{TerminalErrorCode}]",
    };
}

public sealed class PlaybackReconnectPolicy
{
    private readonly PlaybackReconnectPolicyOptions _options;

    public PlaybackReconnectPolicy(PlaybackReconnectPolicyOptions? options = null)
    {
        _options = options ?? new PlaybackReconnectPolicyOptions();
    }

    public PlaybackReconnectPolicyOptions Options => _options;

    public PlaybackReconnectDecision Evaluate(
        DomainError failure,
        int completedAttemptCount,
        TimeSpan elapsed,
        TimeSpan injectedJitter)
    {
        ArgumentNullException.ThrowIfNull(failure);
        ArgumentOutOfRangeException.ThrowIfNegative(completedAttemptCount);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(
            completedAttemptCount,
            _options.MaximumAttempts);
        ArgumentOutOfRangeException.ThrowIfLessThan(elapsed, TimeSpan.Zero);

        if (injectedJitter < TimeSpan.Zero || injectedJitter > _options.MaximumJitter)
        {
            throw new ArgumentOutOfRangeException(nameof(injectedJitter));
        }

        if (!Enum.IsDefined(failure.Code))
        {
            return PlaybackReconnectDecision.DoNotRetry(
                DomainErrorCode.DomainInvariantViolation);
        }

        DomainError canonical = DomainError.Create(failure.Code);
        if (failure.Retryability != canonical.Retryability ||
            !string.Equals(
                failure.ResourceKey,
                canonical.ResourceKey,
                StringComparison.Ordinal))
        {
            return PlaybackReconnectDecision.DoNotRetry(
                DomainErrorCode.DomainInvariantViolation);
        }

        if (canonical.Retryability != DomainRetryability.BoundedTransient)
        {
            return PlaybackReconnectDecision.DoNotRetry(canonical.Code);
        }

        if (completedAttemptCount >= _options.MaximumAttempts ||
            elapsed >= _options.TotalBudget)
        {
            return PlaybackReconnectDecision.Exhausted();
        }

        TimeSpan delay = _options.BaseDelays[completedAttemptCount] + injectedJitter;
        if (delay >= _options.TotalBudget - elapsed)
        {
            return PlaybackReconnectDecision.Exhausted();
        }

        return PlaybackReconnectDecision.RetryAfterDelay(
            completedAttemptCount + 1,
            delay);
    }

    public override string ToString() => "[PLAYBACK-RECONNECT-POLICY]";
}
