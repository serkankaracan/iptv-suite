using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json.Serialization;

namespace IptvSuite.Application;

public readonly record struct PlaybackVolume
{
    public const int MinimumPercent = 0;
    public const int MaximumPercent = 100;

    private PlaybackVolume(int percent) => Percent = percent;

    public int Percent { get; }

    public static PlaybackVolume FromPercent(int percent)
    {
        if (percent is < MinimumPercent or > MaximumPercent)
        {
            throw new ArgumentOutOfRangeException(
                nameof(percent),
                percent,
                $"Playback volume must be between {MinimumPercent} and {MaximumPercent} percent.");
        }

        return new PlaybackVolume(percent);
    }

    public override string ToString() =>
        $"[PLAYBACK-VOLUME:{Percent.ToString(CultureInfo.InvariantCulture)}]";
}

[JsonConverter(typeof(JsonStringEnumConverter<PlaybackAspectMode>))]
public enum PlaybackAspectMode
{
    Fit,
    Fill,
}

public sealed record PlaybackControlSnapshot
{
    private PlaybackControlSnapshot(
        PlaybackSessionId sessionId,
        PlaybackVolume volume,
        bool isMuted,
        PlaybackAspectMode aspectMode)
    {
        SessionId = sessionId;
        Volume = volume;
        IsMuted = isMuted;
        AspectMode = aspectMode;
    }

    public PlaybackSessionId SessionId { get; }

    public PlaybackVolume Volume { get; }

    public bool IsMuted { get; }

    public PlaybackAspectMode AspectMode { get; }

    public static PlaybackControlSnapshot Idle(
        PlaybackVolume volume,
        bool isMuted,
        PlaybackAspectMode aspectMode) =>
        Create(default, volume, isMuted, aspectMode, requireSession: false);

    public static PlaybackControlSnapshot Active(
        PlaybackSessionId sessionId,
        PlaybackVolume volume,
        bool isMuted,
        PlaybackAspectMode aspectMode) =>
        Create(sessionId, volume, isMuted, aspectMode, requireSession: true);

    public override string ToString() =>
        $"[PLAYBACK-CONTROLS:{SessionId}:{Volume.Percent.ToString(CultureInfo.InvariantCulture)}:" +
        $"{(IsMuted ? "MUTED" : "AUDIBLE")}:{AspectMode}]";

    private static PlaybackControlSnapshot Create(
        PlaybackSessionId sessionId,
        PlaybackVolume volume,
        bool isMuted,
        PlaybackAspectMode aspectMode,
        bool requireSession)
    {
        if (requireSession && sessionId.IsEmpty)
        {
            throw new ArgumentException("A playback session identifier is required.", nameof(sessionId));
        }

        if (!Enum.IsDefined(aspectMode))
        {
            throw new ArgumentOutOfRangeException(
                nameof(aspectMode),
                aspectMode,
                "Unknown playback aspect mode.");
        }

        return new PlaybackControlSnapshot(sessionId, volume, isMuted, aspectMode);
    }
}

[JsonConverter(typeof(JsonStringEnumConverter<PlaybackTrackKind>))]
public enum PlaybackTrackKind
{
    Audio,
    Subtitle,
}

[Flags]
[JsonConverter(typeof(JsonStringEnumConverter<PlaybackTrackCapabilities>))]
public enum PlaybackTrackCapabilities
{
    None = 0,
    AudioSelection = 1,
    SubtitleSelection = 2,
}

public readonly record struct PlaybackTrackId
{
    private PlaybackTrackId(
        PlaybackSessionId sessionId,
        PlaybackTrackKind kind,
        int ordinal)
    {
        SessionId = sessionId;
        Kind = kind;
        Ordinal = ordinal;
    }

    public PlaybackSessionId SessionId { get; }

    public PlaybackTrackKind Kind { get; }

    public int Ordinal { get; }

    public bool IsEmpty => SessionId.IsEmpty || Ordinal <= 0 || !Enum.IsDefined(Kind);

    public static PlaybackTrackId Create(
        PlaybackSessionId sessionId,
        PlaybackTrackKind kind,
        int ordinal)
    {
        if (sessionId.IsEmpty)
        {
            throw new ArgumentException("A playback session identifier is required.", nameof(sessionId));
        }

        if (!Enum.IsDefined(kind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown playback track kind.");
        }

        if (ordinal <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(ordinal),
                ordinal,
                "Playback track ordinals must be positive.");
        }

        return new PlaybackTrackId(sessionId, kind, ordinal);
    }

    public override string ToString() =>
        $"[PLAYBACK-TRACK-ID:{Kind}:{Ordinal.ToString(CultureInfo.InvariantCulture)}:{SessionId}]";
}

public sealed record PlaybackTrackInfo
{
    public PlaybackTrackInfo(
        PlaybackTrackId id,
        bool isSelected,
        bool isSelectable)
    {
        if (id.IsEmpty)
        {
            throw new ArgumentException("A session-bound playback track identifier is required.", nameof(id));
        }

        Id = id;
        IsSelected = isSelected;
        IsSelectable = isSelectable;
    }

    public PlaybackTrackId Id { get; }

    public bool IsSelected { get; }

    public bool IsSelectable { get; }

    public override string ToString() =>
        $"[PLAYBACK-TRACK:{Id.Kind}:{Id.Ordinal.ToString(CultureInfo.InvariantCulture)}:" +
        $"{(IsSelected ? "SELECTED" : "AVAILABLE")}:" +
        $"{(IsSelectable ? "SELECTABLE" : "FIXED")}]";
}

public sealed record PlaybackTrackSnapshot
{
    public const int MaximumTrackCount = 64;

    private readonly ReadOnlyCollection<PlaybackTrackInfo> _tracks;

    private PlaybackTrackSnapshot(
        PlaybackSessionId sessionId,
        PlaybackTrackCapabilities capabilities,
        ReadOnlyCollection<PlaybackTrackInfo> tracks)
    {
        SessionId = sessionId;
        Capabilities = capabilities;
        _tracks = tracks;
    }

    public PlaybackSessionId SessionId { get; }

    public PlaybackTrackCapabilities Capabilities { get; }

    public IReadOnlyList<PlaybackTrackInfo> Tracks => _tracks;

    public static PlaybackTrackSnapshot Create(
        PlaybackSessionId sessionId,
        PlaybackTrackCapabilities capabilities,
        IEnumerable<PlaybackTrackInfo> tracks)
    {
        if (sessionId.IsEmpty)
        {
            throw new ArgumentException("A playback session identifier is required.", nameof(sessionId));
        }

        const PlaybackTrackCapabilities allCapabilities =
            PlaybackTrackCapabilities.AudioSelection |
            PlaybackTrackCapabilities.SubtitleSelection;
        if ((capabilities & ~allCapabilities) != 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(capabilities),
                capabilities,
                "Playback track capabilities are contradictory or unknown.");
        }

        ArgumentNullException.ThrowIfNull(tracks);
        PlaybackTrackInfo[] copied = tracks.Take(MaximumTrackCount + 1).ToArray();
        if (copied.Length > MaximumTrackCount)
        {
            throw new ArgumentOutOfRangeException(
                nameof(tracks),
                $"At most {MaximumTrackCount} playback tracks may be exposed.");
        }

        if (copied.Any(track => track is null || track.Id.SessionId != sessionId))
        {
            throw new ArgumentException(
                "Every playback track must belong to the snapshot session.",
                nameof(tracks));
        }

        if (copied.Select(track => track.Id).Distinct().Count() != copied.Length)
        {
            throw new ArgumentException("Playback track identifiers must be unique.", nameof(tracks));
        }

        if (copied.GroupBy(track => track.Id.Kind).Any(group => group.Count(track => track.IsSelected) > 1))
        {
            throw new ArgumentException(
                "At most one playback track per kind may be selected.",
                nameof(tracks));
        }

        if (copied.Any(track => track.IsSelectable && !SupportsSelection(capabilities, track.Id.Kind)))
        {
            throw new ArgumentException(
                "Selectable tracks require the corresponding capability.",
                nameof(tracks));
        }

        return new PlaybackTrackSnapshot(
            sessionId,
            capabilities,
            Array.AsReadOnly(copied));
    }

    public bool CanSelect(PlaybackTrackId trackId) =>
        trackId.SessionId == SessionId &&
        _tracks.Any(track => track.Id == trackId && track.IsSelectable);

    public override string ToString() =>
        $"[PLAYBACK-TRACKS:{SessionId}:{Capabilities}:" +
        $"{_tracks.Count.ToString(CultureInfo.InvariantCulture)}]";

    private static bool SupportsSelection(
        PlaybackTrackCapabilities capabilities,
        PlaybackTrackKind kind) => kind switch
        {
            PlaybackTrackKind.Audio => capabilities.HasFlag(PlaybackTrackCapabilities.AudioSelection),
            PlaybackTrackKind.Subtitle => capabilities.HasFlag(PlaybackTrackCapabilities.SubtitleSelection),
            _ => false,
        };
}
