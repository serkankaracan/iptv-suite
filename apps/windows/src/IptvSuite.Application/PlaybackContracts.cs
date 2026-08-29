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

[JsonConverter(typeof(JsonStringEnumConverter<PlaybackTargetKind>))]
public enum PlaybackTargetKind
{
    Live,
    Movie,
    Episode,
}

public sealed record PlaybackTarget
{
    private PlaybackTarget(
        PlaybackTargetKind kind,
        ChannelId? channelId,
        MovieId? movieId,
        EpisodeId? episodeId)
    {
        Kind = kind;
        ChannelId = channelId;
        MovieId = movieId;
        EpisodeId = episodeId;
    }

    public PlaybackTargetKind Kind { get; }

    public ChannelId? ChannelId { get; }

    public MovieId? MovieId { get; }

    public EpisodeId? EpisodeId { get; }

    public static PlaybackTarget Live(ChannelId channelId)
    {
        if (channelId.IsEmpty)
        {
            throw new ArgumentException("A live channel identifier is required.", nameof(channelId));
        }

        return new PlaybackTarget(
            PlaybackTargetKind.Live,
            channelId,
            movieId: null,
            episodeId: null);
    }

    public static PlaybackTarget Movie(MovieId movieId)
    {
        if (movieId.IsEmpty)
        {
            throw new ArgumentException("A movie identifier is required.", nameof(movieId));
        }

        return new PlaybackTarget(
            PlaybackTargetKind.Movie,
            channelId: null,
            movieId,
            episodeId: null);
    }

    public static PlaybackTarget Episode(EpisodeId episodeId)
    {
        if (episodeId.IsEmpty)
        {
            throw new ArgumentException("An episode identifier is required.", nameof(episodeId));
        }

        return new PlaybackTarget(
            PlaybackTargetKind.Episode,
            channelId: null,
            movieId: null,
            episodeId);
    }

    public override string ToString() => $"[PLAYBACK-TARGET:{Kind}]";
}

public sealed record PlaybackSelection
{
    public PlaybackSelection(SourceId sourceId, ChannelId channelId)
        : this(sourceId, PlaybackTarget.Live(channelId))
    {
    }

    private PlaybackSelection(SourceId sourceId, PlaybackTarget target)
    {
        if (sourceId.IsEmpty)
        {
            throw new ArgumentException("A playback source identifier is required.", nameof(sourceId));
        }

        SourceId = sourceId;
        Target = target ?? throw new ArgumentNullException(nameof(target));
    }

    public static PlaybackSelection ForTarget(SourceId sourceId, PlaybackTarget target) =>
        new(sourceId, target);

    public SourceId SourceId { get; }

    public PlaybackTarget Target { get; }

    public ChannelId ChannelId => Target.ChannelId.GetValueOrDefault();

    public override string ToString() => $"[PLAYBACK-SELECTION:{SourceId}:{Target.Kind}]";
}

[JsonConverter(typeof(JsonStringEnumConverter<PlaybackContentIntent>))]
public enum PlaybackContentIntent
{
    Live,
    OnDemand,
}

[JsonConverter(typeof(JsonStringEnumConverter<PlaybackState>))]
public enum PlaybackState
{
    Closed,
    Opening,
    Buffering,
    Playing,
    Paused,
    Completed,
    Reconnecting,
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

        if (state is PlaybackState.Closed or PlaybackState.Reconnecting or PlaybackState.Failed ||
            !Enum.IsDefined(state))
        {
            throw new ArgumentOutOfRangeException(
                nameof(state),
                state,
                "The state is not an engine-owned active playback state.");
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

    PlaybackControlSnapshot CurrentControls { get; }

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

    ValueTask<PlaybackEngineOperationResult> SetVolumeAsync(
        PlaybackSessionId sessionId,
        PlaybackVolume volume,
        CancellationToken cancellationToken = default);

    ValueTask<PlaybackEngineOperationResult> SetMutedAsync(
        PlaybackSessionId sessionId,
        bool isMuted,
        CancellationToken cancellationToken = default);

    ValueTask<PlaybackEngineOperationResult> SetAspectModeAsync(
        PlaybackSessionId sessionId,
        PlaybackAspectMode aspectMode,
        CancellationToken cancellationToken = default);

    ValueTask<DomainResult<PlaybackTrackSnapshot>> GetTracksAsync(
        PlaybackSessionId sessionId,
        CancellationToken cancellationToken = default);

    ValueTask<PlaybackEngineOperationResult> SelectTrackAsync(
        PlaybackSessionId sessionId,
        PlaybackTrackId trackId,
        CancellationToken cancellationToken = default);
}

public sealed record PlaybackTimelineSnapshot
{
    private PlaybackTimelineSnapshot(
        PlaybackSessionId sessionId,
        TimeSpan position,
        TimeSpan duration,
        bool canSeek)
    {
        SessionId = sessionId;
        Position = position;
        Duration = duration;
        CanSeek = canSeek;
    }

    public PlaybackSessionId SessionId { get; }

    public TimeSpan Position { get; }

    public TimeSpan Duration { get; }

    public bool CanSeek { get; }

    public static PlaybackTimelineSnapshot Unavailable() =>
        new(default, TimeSpan.Zero, TimeSpan.Zero, canSeek: false);

    public static PlaybackTimelineSnapshot Unavailable(PlaybackSessionId sessionId)
    {
        if (sessionId.IsEmpty)
        {
            throw new ArgumentException(
                "An active playback session identifier is required.",
                nameof(sessionId));
        }

        return new PlaybackTimelineSnapshot(
            sessionId,
            TimeSpan.Zero,
            TimeSpan.Zero,
            canSeek: false);
    }

    public static PlaybackTimelineSnapshot Create(
        PlaybackSessionId sessionId,
        TimeSpan position,
        TimeSpan duration,
        bool canSeek)
    {
        if (sessionId.IsEmpty)
        {
            throw new ArgumentException(
                "An active playback session identifier is required.",
                nameof(sessionId));
        }

        if (position < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(position),
                "A playback position cannot be negative.");
        }

        if (duration < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(duration),
                "A playback duration cannot be negative.");
        }

        if (position > duration)
        {
            throw new ArgumentOutOfRangeException(
                nameof(position),
                "A playback position cannot exceed its duration.");
        }

        if (canSeek && duration == TimeSpan.Zero)
        {
            throw new ArgumentException(
                "A seekable timeline requires a positive duration.",
                nameof(canSeek));
        }

        return new PlaybackTimelineSnapshot(sessionId, position, duration, canSeek);
    }

    public override string ToString() => SessionId.IsEmpty
        ? "[PLAYBACK-TIMELINE:UNAVAILABLE]"
        : CanSeek
            ? $"[PLAYBACK-TIMELINE:{SessionId}:SEEKABLE]"
            : $"[PLAYBACK-TIMELINE:{SessionId}:READ-ONLY]";
}

public sealed class PlaybackTimelineChangedEventArgs : EventArgs
{
    public PlaybackTimelineChangedEventArgs(PlaybackTimelineSnapshot snapshot)
    {
        Snapshot = snapshot ?? throw new ArgumentNullException(nameof(snapshot));
    }

    public PlaybackTimelineSnapshot Snapshot { get; }

    public override string ToString() => Snapshot.ToString();
}

public interface IPlaybackTimelineEngine
{
    event EventHandler<PlaybackTimelineChangedEventArgs>? TimelineChanged;

    PlaybackTimelineSnapshot CurrentTimeline { get; }

    ValueTask<PlaybackEngineOperationResult> OpenAsync(
        PlaybackSessionId sessionId,
        PlaybackSelection selection,
        PlaybackContentIntent contentIntent,
        CancellationToken cancellationToken = default);

    ValueTask<PlaybackEngineOperationResult> SeekAsync(
        PlaybackSessionId sessionId,
        TimeSpan position,
        CancellationToken cancellationToken = default);
}

public sealed record PlaybackSessionSnapshot
{
    private PlaybackSessionSnapshot(
        PlaybackSessionId sessionId,
        SourceId? sourceId,
        ChannelId? channelId,
        PlaybackState state,
        DomainError? error,
        PlaybackReconnectSnapshot? reconnect)
        : this(
            sessionId,
            sourceId,
            channelId.HasValue ? PlaybackTarget.Live(channelId.Value) : null,
            channelId.HasValue ? PlaybackContentIntent.Live : null,
            state,
            error,
            reconnect)
    {
    }

    private PlaybackSessionSnapshot(
        PlaybackSessionId sessionId,
        SourceId? sourceId,
        PlaybackTarget? target,
        PlaybackContentIntent? contentIntent,
        PlaybackState state,
        DomainError? error,
        PlaybackReconnectSnapshot? reconnect)
    {
        if (!Enum.IsDefined(state))
        {
            throw new ArgumentOutOfRangeException(nameof(state));
        }

        if (contentIntent.HasValue && !Enum.IsDefined(contentIntent.Value))
        {
            throw new ArgumentOutOfRangeException(nameof(contentIntent));
        }

        if (sourceId.HasValue != (target is not null) ||
            sourceId.HasValue != contentIntent.HasValue)
        {
            throw new ArgumentException(
                "Playback content intent is required exactly when a selection is active.");
        }

        bool reconnecting = state == PlaybackState.Reconnecting;
        bool activeReconnect = reconnect?.Phase is PlaybackReconnectPhase.Evaluating or
            PlaybackReconnectPhase.Waiting or
            PlaybackReconnectPhase.Attempting;
        if (reconnecting != activeReconnect ||
            reconnecting && error is not null ||
            !reconnecting && reconnect is not null)
        {
            throw new ArgumentException(
                "Reconnect progress is required only for an active reconnecting session.");
        }

        SessionId = sessionId;
        SourceId = sourceId;
        Target = target;
        ContentIntent = contentIntent;
        State = state;
        Error = error;
        Reconnect = reconnect;
    }

    public PlaybackSessionId SessionId { get; }

    public SourceId? SourceId { get; }

    public PlaybackTarget? Target { get; }

    public ChannelId? ChannelId => Target?.ChannelId;

    public MovieId? MovieId => Target?.MovieId;

    public EpisodeId? EpisodeId => Target?.EpisodeId;

    public PlaybackContentIntent? ContentIntent { get; }

    public PlaybackState State { get; }

    public DomainError? Error { get; }

    public PlaybackReconnectSnapshot? Reconnect { get; }

    internal static PlaybackSessionSnapshot Closed() =>
        new(default, null, null, null, PlaybackState.Closed, null, reconnect: null);

    internal static PlaybackSessionSnapshot Active(
        PlaybackSessionId sessionId,
        PlaybackSelection selection,
        PlaybackState state,
        PlaybackContentIntent contentIntent = PlaybackContentIntent.Live)
    {
        if (sessionId.IsEmpty)
        {
            throw new ArgumentException(
                "An active playback session identifier is required.",
                nameof(sessionId));
        }

        ArgumentNullException.ThrowIfNull(selection);
        if (!Enum.IsDefined(contentIntent))
        {
            throw new ArgumentOutOfRangeException(nameof(contentIntent));
        }
        if (state is PlaybackState.Closed or PlaybackState.Reconnecting or PlaybackState.Failed ||
            !Enum.IsDefined(state))
        {
            throw new ArgumentOutOfRangeException(nameof(state));
        }

        return new PlaybackSessionSnapshot(
            sessionId,
            selection.SourceId,
            selection.Target,
            contentIntent,
            state,
            error: null,
            reconnect: null);
    }

    internal static PlaybackSessionSnapshot Reconnecting(
        PlaybackSessionId sessionId,
        PlaybackSelection selection,
        PlaybackReconnectSnapshot reconnect)
    {
        if (sessionId.IsEmpty)
        {
            throw new ArgumentException(
                "A reconnecting playback session identifier is required.",
                nameof(sessionId));
        }

        ArgumentNullException.ThrowIfNull(selection);
        ArgumentNullException.ThrowIfNull(reconnect);
        if (reconnect.Phase is not (
            PlaybackReconnectPhase.Evaluating or
            PlaybackReconnectPhase.Waiting or
            PlaybackReconnectPhase.Attempting))
        {
            throw new ArgumentException(
                "A reconnecting session requires active reconnect progress.",
                nameof(reconnect));
        }

        return new PlaybackSessionSnapshot(
            sessionId,
            selection.SourceId,
            selection.Target,
            PlaybackContentIntent.Live,
            PlaybackState.Reconnecting,
            error: null,
            reconnect);
    }

    internal static PlaybackSessionSnapshot Failed(
        PlaybackSessionId sessionId,
        PlaybackSelection selection,
        DomainError error,
        PlaybackContentIntent contentIntent = PlaybackContentIntent.Live)
    {
        if (sessionId.IsEmpty)
        {
            throw new ArgumentException(
                "A failed playback session identifier is required.",
                nameof(sessionId));
        }

        ArgumentNullException.ThrowIfNull(selection);
        ArgumentNullException.ThrowIfNull(error);
        if (!Enum.IsDefined(contentIntent))
        {
            throw new ArgumentOutOfRangeException(nameof(contentIntent));
        }
        return new PlaybackSessionSnapshot(
            sessionId,
            selection.SourceId,
            selection.Target,
            contentIntent,
            PlaybackState.Failed,
            error,
            reconnect: null);
    }

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
