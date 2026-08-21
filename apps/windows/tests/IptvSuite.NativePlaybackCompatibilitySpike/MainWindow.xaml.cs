using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.UI.Xaml;
using Windows.Media.Core;
using Windows.Media.Playback;

namespace IptvSuite.NativePlaybackCompatibilitySpike;

public sealed partial class MainWindow : Window, IDisposable
{
    private readonly MediaPlayer _mediaPlayer;
    private TaskCompletionSource? _opened;
    private TaskCompletionSource? _advanced;
    private NativePlaybackFailure _mediaFailure;
    private bool _disposed;

    public MainWindow()
    {
        InitializeComponent();
        _mediaPlayer = new MediaPlayer
        {
            AutoPlay = false,
            AudioCategory = MediaPlayerAudioCategory.Media,
        };
        _mediaPlayer.MediaOpened += MediaPlayer_MediaOpened;
        _mediaPlayer.MediaFailed += MediaPlayer_MediaFailed;
        _mediaPlayer.PlaybackSession.PositionChanged += PlaybackSession_PositionChanged;
        PlaybackSurface.SetMediaPlayer(_mediaPlayer);
        Closed += MainWindow_Closed;
    }

    internal async Task<NativePlaybackProbeResult> RunProbeAsync(
        NativePlaybackProbeRequest request,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(request);
        var startupSamples = new List<double>(request.SwitchCount);
        using Process process = Process.GetCurrentProcess();
        long initialPrivateBytes = process.PrivateMemorySize64;
        int initialHandles = process.HandleCount;
        NativePlaybackFailure timeoutFailure = NativePlaybackFailure.MediaOpenTimeout;
        int completedSwitchCount = 0;

        try
        {
            for (int index = 0; index < request.SwitchCount; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Uri fixture = request.Fixtures[index % request.Fixtures.Count];
                _mediaFailure = NativePlaybackFailure.None;
                _opened = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _advanced = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var stopwatch = Stopwatch.StartNew();
                _mediaPlayer.Source = MediaSource.CreateFromUri(fixture);
                _mediaPlayer.Play();

                await _opened.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
                startupSamples.Add(stopwatch.Elapsed.TotalMilliseconds);
                timeoutFailure = NativePlaybackFailure.PlaybackAdvanceTimeout;
                await _advanced.Task.WaitAsync(TimeSpan.FromSeconds(3), cancellationToken);
                _mediaPlayer.Pause();
                _mediaPlayer.Source = null;
                completedSwitchCount++;
                timeoutFailure = NativePlaybackFailure.MediaOpenTimeout;
            }

            process.Refresh();
            return NativePlaybackProbeResult.Passed(
                request.SwitchCount,
                startupSamples,
                initialPrivateBytes,
                process.PrivateMemorySize64,
                initialHandles,
                process.HandleCount);
        }
        catch (TimeoutException)
        {
            return NativePlaybackProbeResult.Failed(
                _mediaFailure == NativePlaybackFailure.None ? timeoutFailure : _mediaFailure,
                completedSwitchCount);
        }
        catch (OperationCanceledException)
        {
            return NativePlaybackProbeResult.Failed(NativePlaybackFailure.Cancelled, completedSwitchCount);
        }
        catch (InvalidOperationException) when (_mediaFailure == NativePlaybackFailure.MediaFailed)
        {
            return NativePlaybackProbeResult.Failed(NativePlaybackFailure.MediaFailed, completedSwitchCount);
        }
        finally
        {
            _mediaPlayer.Pause();
            _mediaPlayer.Source = null;
            _opened = null;
            _advanced = null;
        }

    }

    internal void ShowResult(NativePlaybackProbeResult result) =>
        DispatcherQueue.TryEnqueue(() => StateText.Text = result.Success ? "Passed" : $"Failed: {result.Failure}");

    private void MediaPlayer_MediaOpened(MediaPlayer sender, object args)
    {
        _opened?.TrySetResult();
        DispatcherQueue.TryEnqueue(() => StateText.Text = "Opened");
    }

    private void MediaPlayer_MediaFailed(MediaPlayer sender, MediaPlayerFailedEventArgs args)
    {
        _mediaFailure = NativePlaybackFailure.MediaFailed;
        _opened?.TrySetException(new InvalidOperationException(nameof(NativePlaybackFailure.MediaFailed)));
        _advanced?.TrySetException(new InvalidOperationException(nameof(NativePlaybackFailure.MediaFailed)));
        DispatcherQueue.TryEnqueue(() => StateText.Text = "Failed: MediaFailed");
    }

    private void PlaybackSession_PositionChanged(MediaPlaybackSession sender, object args)
    {
        if (sender.Position >= TimeSpan.FromMilliseconds(500)) _advanced?.TrySetResult();
    }

    private void MainWindow_Closed(object sender, WindowEventArgs args) => Dispose();

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Closed -= MainWindow_Closed;
        _mediaPlayer.MediaOpened -= MediaPlayer_MediaOpened;
        _mediaPlayer.MediaFailed -= MediaPlayer_MediaFailed;
        _mediaPlayer.PlaybackSession.PositionChanged -= PlaybackSession_PositionChanged;
        PlaybackSurface.SetMediaPlayer(null);
        _mediaPlayer.Source = null;
        _mediaPlayer.Dispose();
        GC.SuppressFinalize(this);
    }
}

internal sealed record NativePlaybackProbeRequest(IReadOnlyList<Uri> Fixtures, int SwitchCount)
{
    private static readonly HashSet<string> AllowedPaths =
    [
        "/direct-h264-aac.ts",
        "/hls.m3u8",
    ];

    internal static NativePlaybackProbeRequest Parse(string? arguments)
    {
        string[] parts = arguments?.Split(' ', StringSplitOptions.RemoveEmptyEntries) ?? [];
        if (parts is not ["probe", string direct, string hls, string switchText] ||
            !int.TryParse(switchText, out int switchCount) || switchCount is < 2 or > 100 ||
            !Uri.TryCreate(direct, UriKind.Absolute, out Uri? directUri) ||
            !Uri.TryCreate(hls, UriKind.Absolute, out Uri? hlsUri))
        {
            throw new ArgumentException("Invalid native playback probe arguments.", nameof(arguments));
        }

        ValidateFixture(directUri);
        ValidateFixture(hlsUri);
        if (directUri.Authority != hlsUri.Authority || directUri.AbsolutePath == hlsUri.AbsolutePath)
        {
            throw new ArgumentException("Native playback fixtures must share one loopback authority and use distinct paths.", nameof(arguments));
        }

        return new NativePlaybackProbeRequest([hlsUri, directUri], switchCount);
    }

    private static void ValidateFixture(Uri fixture)
    {
        if (fixture.Scheme != Uri.UriSchemeHttps || !fixture.IsLoopback ||
            !string.IsNullOrEmpty(fixture.UserInfo) || !string.IsNullOrEmpty(fixture.Query) ||
            !string.IsNullOrEmpty(fixture.Fragment) || !AllowedPaths.Contains(fixture.AbsolutePath))
        {
            throw new ArgumentException("Native playback fixture URI violates the loopback boundary.", nameof(fixture));
        }
    }
}

internal enum NativePlaybackFailure
{
    None,
    InvalidArguments,
    MediaFailed,
    MediaOpenTimeout,
    PlaybackAdvanceTimeout,
    Cancelled,
    UnexpectedFailure,
}

internal sealed record NativePlaybackProbeResult(
    bool Success,
    NativePlaybackFailure Failure,
    int SwitchCount,
    double StartupP95Milliseconds,
    double StartupMaximumMilliseconds,
    long InitialPrivateBytes,
    long FinalPrivateBytes,
    int InitialHandleCount,
    int FinalHandleCount)
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    internal static NativePlaybackProbeResult Passed(
        int switchCount,
        IReadOnlyList<double> startupSamples,
        long initialPrivateBytes,
        long finalPrivateBytes,
        int initialHandleCount,
        int finalHandleCount)
    {
        double[] ordered = startupSamples.Order().ToArray();
        int percentileIndex = Math.Max(0, (int)Math.Ceiling(ordered.Length * 0.95) - 1);
        return new NativePlaybackProbeResult(
            true,
            NativePlaybackFailure.None,
            switchCount,
            ordered[percentileIndex],
            ordered[^1],
            initialPrivateBytes,
            finalPrivateBytes,
            initialHandleCount,
            finalHandleCount);
    }

    internal static NativePlaybackProbeResult Failed(
        NativePlaybackFailure failure,
        int completedSwitchCount = 0) =>
        new(false, failure, completedSwitchCount, 0, 0, 0, 0, 0, 0);

    internal string ToJson() => JsonSerializer.Serialize(this, SerializerOptions);
}
