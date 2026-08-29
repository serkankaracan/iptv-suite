using System.Diagnostics;
using IptvSuite.Application;
using IptvSuite.Domain;

namespace IptvSuite.Windows;

internal sealed record SourceManagerOperationResult(bool IsSuccess, string Message)
{
    internal static SourceManagerOperationResult Success(string message) => new(true, message);

    internal static SourceManagerOperationResult Failure(string message) => new(false, message);
}

[DebuggerDisplay("[XTREAM-SOURCE-INPUT]")]
internal sealed class XtreamSourceInput(
    string displayName,
    string serverLocator,
    string username,
    string password,
    bool allowInsecureHttp)
{
    [DebuggerBrowsable(DebuggerBrowsableState.Never)]
    internal string DisplayName { get; } = displayName;
    [DebuggerBrowsable(DebuggerBrowsableState.Never)]
    internal string ServerLocator { get; } = serverLocator;
    [DebuggerBrowsable(DebuggerBrowsableState.Never)]
    internal string Username { get; } = username;
    [DebuggerBrowsable(DebuggerBrowsableState.Never)]
    internal string Password { get; } = password;
    internal bool AllowInsecureHttp { get; } = allowInsecureHttp;

    public override string ToString() => "[XTREAM-SOURCE-INPUT]";
}

[DebuggerDisplay("[REMOTE-PLAYLIST-SOURCE-INPUT]")]
internal sealed class RemotePlaylistSourceInput(
    string displayName,
    string playlistLocator,
    bool allowInsecureHttp)
{
    [DebuggerBrowsable(DebuggerBrowsableState.Never)]
    internal string DisplayName { get; } = displayName;
    [DebuggerBrowsable(DebuggerBrowsableState.Never)]
    internal string PlaylistLocator { get; } = playlistLocator;
    internal bool AllowInsecureHttp { get; } = allowInsecureHttp;

    public override string ToString() => "[REMOTE-PLAYLIST-SOURCE-INPUT]";
}

internal sealed class SourceManagerOperations
{
    internal required Func<CancellationToken, ValueTask<IReadOnlyList<SourceManagementSummary>>> ReadSourcesAsync { get; init; }

    internal required Func<RemotePlaylistSourceInput, CancellationToken, ValueTask<SourceManagerOperationResult>> AddRemotePlaylistAsync { get; init; }

    internal required Func<XtreamSourceInput, CancellationToken, ValueTask<SourceManagerOperationResult>> AddXtreamAsync { get; init; }

    internal required Func<SourceId, string, CancellationToken, ValueTask<SourceManagerOperationResult>> RenameAsync { get; init; }

    internal required Func<SourceId, CancellationToken, ValueTask<SourceManagerOperationResult>> RefreshAsync { get; init; }

    internal required Func<SourceId, RemotePlaylistSourceInput, CancellationToken, ValueTask<SourceManagerOperationResult>> ReplaceRemotePlaylistAsync { get; init; }

    internal required Func<SourceId, XtreamSourceInput, CancellationToken, ValueTask<SourceManagerOperationResult>> ReplaceXtreamAsync { get; init; }

    internal required Func<SourceId, CancellationToken, ValueTask<SourceManagerOperationResult>> DeleteAsync { get; init; }
}
