using System.Globalization;
using System.Text.Json.Serialization;
using IptvSuite.Domain;

namespace IptvSuite.Application;

public readonly record struct PlaybackReconnectCorrelationId
{
    private PlaybackReconnectCorrelationId(long value) => Value = value;

    public long Value { get; }

    public bool IsEmpty => Value <= 0;

    public static PlaybackReconnectCorrelationId FromSequence(long value)
    {
        if (value <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(value),
                "Playback reconnect correlation identifiers must be positive.");
        }

        return new PlaybackReconnectCorrelationId(value);
    }

    public override string ToString() => Value.ToString(CultureInfo.InvariantCulture);
}

[JsonConverter(typeof(JsonStringEnumConverter<PlaybackReconnectPhase>))]
public enum PlaybackReconnectPhase
{
    Idle,
    Evaluating,
    Waiting,
    Attempting,
    Succeeded,
    DoNotRetry,
    Exhausted,
    Cancelled,
}

public sealed record PlaybackReconnectSnapshot
{
    private PlaybackReconnectSnapshot(
        PlaybackReconnectPhase phase,
        PlaybackReconnectCorrelationId correlationId,
        int attemptNumber,
        int maximumAttempts,
        TimeSpan remainingDelay,
        TimeSpan remainingBudget,
        DomainErrorCode? terminalErrorCode)
    {
        if (!Enum.IsDefined(phase))
        {
            throw new ArgumentOutOfRangeException(nameof(phase));
        }

        bool isIdle = phase == PlaybackReconnectPhase.Idle;
        bool isTerminal = phase is PlaybackReconnectPhase.Succeeded or
            PlaybackReconnectPhase.DoNotRetry or
            PlaybackReconnectPhase.Exhausted or
            PlaybackReconnectPhase.Cancelled;
        bool valid = remainingDelay >= TimeSpan.Zero &&
            remainingBudget >= TimeSpan.Zero &&
            remainingBudget <= PlaybackReconnectPolicyOptions.MaximumAllowedTotalBudget &&
            (isIdle ||
                phase is PlaybackReconnectPhase.DoNotRetry or
                    PlaybackReconnectPhase.Exhausted or
                    PlaybackReconnectPhase.Cancelled ||
                remainingBudget > TimeSpan.Zero) &&
            (isIdle
                ? correlationId.IsEmpty &&
                    attemptNumber == 0 &&
                    maximumAttempts == 0 &&
                    remainingDelay == TimeSpan.Zero &&
                    remainingBudget == TimeSpan.Zero &&
                    !terminalErrorCode.HasValue
                : !correlationId.IsEmpty &&
                    maximumAttempts == PlaybackReconnectPolicyOptions.MaximumAllowedAttempts &&
                    attemptNumber >= 0 &&
                    attemptNumber <= maximumAttempts &&
                    IsValidPhaseShape(
                        phase,
                        attemptNumber,
                        remainingDelay,
                        remainingBudget,
                        terminalErrorCode,
                        isTerminal));
        if (!valid)
        {
            throw new ArgumentException("The playback reconnect snapshot invariant is invalid.");
        }

        Phase = phase;
        CorrelationId = correlationId;
        AttemptNumber = attemptNumber;
        MaximumAttempts = maximumAttempts;
        RemainingDelay = remainingDelay;
        RemainingBudget = remainingBudget;
        TerminalErrorCode = terminalErrorCode;
    }

    public PlaybackReconnectPhase Phase { get; }

    public PlaybackReconnectCorrelationId CorrelationId { get; }

    public int AttemptNumber { get; }

    public int MaximumAttempts { get; }

    public TimeSpan RemainingDelay { get; }

    public TimeSpan RemainingBudget { get; }

    public DomainErrorCode? TerminalErrorCode { get; }

    public bool IsTerminal => Phase is PlaybackReconnectPhase.Succeeded or
        PlaybackReconnectPhase.DoNotRetry or
        PlaybackReconnectPhase.Exhausted or
        PlaybackReconnectPhase.Cancelled;

    public static PlaybackReconnectSnapshot Idle() => new(
        PlaybackReconnectPhase.Idle,
        default,
        attemptNumber: 0,
        maximumAttempts: 0,
        TimeSpan.Zero,
        TimeSpan.Zero,
        terminalErrorCode: null);

    internal static PlaybackReconnectSnapshot Active(
        PlaybackReconnectPhase phase,
        PlaybackReconnectCorrelationId correlationId,
        int attemptNumber,
        TimeSpan remainingDelay,
        TimeSpan remainingBudget) => new(
            phase,
            correlationId,
            attemptNumber,
            PlaybackReconnectPolicyOptions.MaximumAllowedAttempts,
            remainingDelay,
            remainingBudget,
            terminalErrorCode: null);

    internal static PlaybackReconnectSnapshot Terminal(
        PlaybackReconnectPhase phase,
        PlaybackReconnectCorrelationId correlationId,
        int attemptNumber,
        TimeSpan remainingBudget,
        DomainErrorCode? terminalErrorCode) => new(
            phase,
            correlationId,
            attemptNumber,
            PlaybackReconnectPolicyOptions.MaximumAllowedAttempts,
            TimeSpan.Zero,
            remainingBudget,
            terminalErrorCode);

    public override string ToString() => TerminalErrorCode.HasValue
        ? $"[PLAYBACK-RECONNECT:{Phase}:{CorrelationId}:{AttemptNumber}:{TerminalErrorCode}]"
        : $"[PLAYBACK-RECONNECT:{Phase}:{CorrelationId}:{AttemptNumber}]";

    private static bool IsValidPhaseShape(
        PlaybackReconnectPhase phase,
        int attemptNumber,
        TimeSpan remainingDelay,
        TimeSpan remainingBudget,
        DomainErrorCode? terminalErrorCode,
        bool isTerminal)
    {
        if (terminalErrorCode.HasValue && !Enum.IsDefined(terminalErrorCode.Value))
        {
            return false;
        }

        return phase switch
        {
            PlaybackReconnectPhase.Evaluating =>
                attemptNumber == 0 &&
                remainingDelay == TimeSpan.Zero &&
                !terminalErrorCode.HasValue,
            PlaybackReconnectPhase.Waiting =>
                attemptNumber is >= 1 and <= PlaybackReconnectPolicyOptions.MaximumAllowedAttempts &&
                remainingDelay > TimeSpan.Zero &&
                remainingDelay <= GetMaximumDelay(attemptNumber) &&
                remainingDelay < remainingBudget &&
                !terminalErrorCode.HasValue,
            PlaybackReconnectPhase.Attempting =>
                attemptNumber is >= 1 and <= PlaybackReconnectPolicyOptions.MaximumAllowedAttempts &&
                remainingDelay == TimeSpan.Zero &&
                !terminalErrorCode.HasValue,
            PlaybackReconnectPhase.Succeeded =>
                attemptNumber is >= 1 and <= PlaybackReconnectPolicyOptions.MaximumAllowedAttempts &&
                remainingDelay == TimeSpan.Zero &&
                !terminalErrorCode.HasValue,
            PlaybackReconnectPhase.DoNotRetry =>
                remainingDelay == TimeSpan.Zero &&
                terminalErrorCode.HasValue &&
                terminalErrorCode != DomainErrorCode.ReconnectExhausted &&
                terminalErrorCode != DomainErrorCode.OperationCancelled,
            PlaybackReconnectPhase.Exhausted =>
                remainingDelay == TimeSpan.Zero &&
                terminalErrorCode == DomainErrorCode.ReconnectExhausted,
            PlaybackReconnectPhase.Cancelled =>
                remainingDelay == TimeSpan.Zero &&
                terminalErrorCode == DomainErrorCode.OperationCancelled,
            _ => !isTerminal && false,
        };
    }

    private static TimeSpan GetMaximumDelay(int attemptNumber) => attemptNumber switch
    {
        1 => TimeSpan.FromSeconds(1) + PlaybackReconnectPolicyOptions.MaximumAllowedJitter,
        2 => TimeSpan.FromSeconds(2) + PlaybackReconnectPolicyOptions.MaximumAllowedJitter,
        3 => TimeSpan.FromSeconds(4) + PlaybackReconnectPolicyOptions.MaximumAllowedJitter,
        _ => TimeSpan.Zero,
    };
}

public sealed class PlaybackReconnectSnapshotChangedEventArgs : EventArgs
{
    public PlaybackReconnectSnapshotChangedEventArgs(PlaybackReconnectSnapshot snapshot)
    {
        Snapshot = snapshot ?? throw new ArgumentNullException(nameof(snapshot));
    }

    public PlaybackReconnectSnapshot Snapshot { get; }

    public override string ToString() => Snapshot.ToString();
}

public delegate TimeSpan PlaybackReconnectJitterSource(int nextAttemptNumber);

public delegate ValueTask<PlaybackEngineOperationResult> PlaybackReconnectAttemptExecutor(
    PlaybackReconnectCorrelationId correlationId,
    int attemptNumber,
    CancellationToken cancellationToken);
