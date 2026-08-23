using System.Globalization;
using System.Text.Json.Serialization;
using IptvSuite.Domain;

namespace IptvSuite.Application;

public readonly record struct PlaybackSessionId
{
    private PlaybackSessionId(long value) => Value = value;

    public long Value { get; }

    public bool IsEmpty => Value <= 0;

    internal static PlaybackSessionId FromSequence(long value)
    {
        if (value <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(value),
                "Playback session identifiers must be positive.");
        }

        return new PlaybackSessionId(value);
    }

    public override string ToString() => Value.ToString(CultureInfo.InvariantCulture);
}

public sealed record PlaybackSelection
{
    public PlaybackSelection(SourceId sourceId, ChannelId channelId)
    {
        if (sourceId.IsEmpty)
        {
            throw new ArgumentException("A playback source identifier is required.", nameof(sourceId));
        }

        if (channelId.IsEmpty)
        {
            throw new ArgumentException("A playback channel identifier is required.", nameof(channelId));
        }

        SourceId = sourceId;
        ChannelId = channelId;
    }

    public SourceId SourceId { get; }

    public ChannelId ChannelId { get; }

    public override string ToString() => $"[PLAYBACK-SELECTION:{SourceId}:{ChannelId}]";
}

[JsonConverter(typeof(JsonStringEnumConverter<PlaybackState>))]
public enum PlaybackState
{
    Closed,
    Opening,
    Buffering,
    Playing,
    Paused,
    Stopping,
    Failed,
}

public sealed record PlaybackEngineSnapshot
{
    private PlaybackEngineSnapshot(
        PlaybackSessionId sessionId,
        PlaybackState state,
        DomainError? error)
    {
        SessionId = sessionId;
        State = state;
        Error = error;
    }

    public PlaybackSessionId SessionId { get; }

    public PlaybackState State { get; }

    public DomainError? Error { get; }

    public static PlaybackEngineSnapshot Closed() =>
        new(default, PlaybackState.Closed, null);

    public static PlaybackEngineSnapshot Closed(PlaybackSessionId sessionId)
    {
        if (sessionId.IsEmpty)
        {
            throw new ArgumentException("A closing playback session identifier is required.", nameof(sessionId));
        }

        return new PlaybackEngineSnapshot(sessionId, PlaybackState.Closed, null);
    }

    public static PlaybackEngineSnapshot Active(
        PlaybackSessionId sessionId,
        PlaybackState state)
    {
        if (sessionId.IsEmpty)
        {
            throw new ArgumentException("An active playback session identifier is required.", nameof(sessionId));
        }

        if (state is PlaybackState.Closed or PlaybackState.Failed || !Enum.IsDefined(state))
        {
            throw new ArgumentOutOfRangeException(
                nameof(state),
                state,
                "The state is not an active playback engine state.");
        }

        return new PlaybackEngineSnapshot(sessionId, state, null);
    }

    public static PlaybackEngineSnapshot Failed(
        PlaybackSessionId sessionId,
        DomainError error)
    {
        if (sessionId.IsEmpty)
        {
            throw new ArgumentException("A failed playback session identifier is required.", nameof(sessionId));
        }

        ArgumentNullException.ThrowIfNull(error);
        return new PlaybackEngineSnapshot(sessionId, PlaybackState.Failed, error);
    }

    public override string ToString() => Error is null
        ? $"[PLAYBACK-ENGINE:{State}:{SessionId}]"
        : $"[PLAYBACK-ENGINE:{State}:{SessionId}:{Error.Code}]";
}

public sealed class PlaybackEngineStateChangedEventArgs : EventArgs
{
    public PlaybackEngineStateChangedEventArgs(PlaybackEngineSnapshot snapshot)
    {
        Snapshot = snapshot ?? throw new ArgumentNullException(nameof(snapshot));
    }

    public PlaybackEngineSnapshot Snapshot { get; }

    public override string ToString() => Snapshot.ToString();
}

public sealed class PlaybackEngineOperationResult
{
    private PlaybackEngineOperationResult(bool isSuccess, DomainError? error)
    {
        IsSuccess = isSuccess;
        Error = error;
    }

    public bool IsSuccess { get; }

    public DomainError? Error { get; }

    public static PlaybackEngineOperationResult Succeeded() => new(true, null);

    public static PlaybackEngineOperationResult Failed(DomainErrorCode errorCode) =>
        Failed(DomainError.Create(errorCode));

    public static PlaybackEngineOperationResult Failed(DomainError error)
    {
        ArgumentNullException.ThrowIfNull(error);
        return new PlaybackEngineOperationResult(false, error);
    }

    public override string ToString() => IsSuccess
        ? "[PLAYBACK-OPERATION:SUCCESS]"
        : $"[PLAYBACK-OPERATION:{Error!.Code}]";
}

public interface IPlaybackEngine : IAsyncDisposable
{
    event EventHandler<PlaybackEngineStateChangedEventArgs>? StateChanged;

    PlaybackEngineSnapshot Current { get; }

    ValueTask<PlaybackEngineOperationResult> OpenAsync(
        PlaybackSessionId sessionId,
        PlaybackSelection selection,
        CancellationToken cancellationToken = default);

    ValueTask<PlaybackEngineOperationResult> PlayAsync(
        PlaybackSessionId sessionId,
        CancellationToken cancellationToken = default);

    ValueTask<PlaybackEngineOperationResult> PauseAsync(
        PlaybackSessionId sessionId,
        CancellationToken cancellationToken = default);

    ValueTask<PlaybackEngineOperationResult> StopAsync(
        PlaybackSessionId sessionId,
        CancellationToken cancellationToken = default);
}

public sealed record PlaybackSessionSnapshot
{
    private PlaybackSessionSnapshot(
        PlaybackSessionId sessionId,
        SourceId? sourceId,
        ChannelId? channelId,
        PlaybackState state,
        DomainError? error)
    {
        SessionId = sessionId;
        SourceId = sourceId;
        ChannelId = channelId;
        State = state;
        Error = error;
    }

    public PlaybackSessionId SessionId { get; }

    public SourceId? SourceId { get; }

    public ChannelId? ChannelId { get; }

    public PlaybackState State { get; }

    public DomainError? Error { get; }

    internal static PlaybackSessionSnapshot Closed() =>
        new(default, null, null, PlaybackState.Closed, null);

    internal static PlaybackSessionSnapshot Active(
        PlaybackSessionId sessionId,
        PlaybackSelection selection,
        PlaybackState state) =>
        new(sessionId, selection.SourceId, selection.ChannelId, state, null);

    internal static PlaybackSessionSnapshot Failed(
        PlaybackSessionId sessionId,
        PlaybackSelection selection,
        DomainError error) =>
        new(sessionId, selection.SourceId, selection.ChannelId, PlaybackState.Failed, error);

    public override string ToString() => Error is null
        ? $"[PLAYBACK-SESSION:{State}:{SessionId}]"
        : $"[PLAYBACK-SESSION:{State}:{SessionId}:{Error.Code}]";
}

public sealed class PlaybackSessionStateChangedEventArgs : EventArgs
{
    public PlaybackSessionStateChangedEventArgs(PlaybackSessionSnapshot snapshot)
    {
        Snapshot = snapshot ?? throw new ArgumentNullException(nameof(snapshot));
    }

    public PlaybackSessionSnapshot Snapshot { get; }

    public override string ToString() => Snapshot.ToString();
}
