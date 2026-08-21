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
    private TaskCompletionSource? _failureSignal;
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
        var hlsStartupSamples = new List<double>((request.SwitchCount + 1) / 2);
        var directStartupSamples = new List<double>(request.SwitchCount / 2);
        using Process process = Process.GetCurrentProcess();
        long initialPrivateBytes = process.PrivateMemorySize64;
        int initialHandles = process.HandleCount;
        NativePlaybackFailure timeoutFailure = NativePlaybackFailure.MediaOpenTimeout;
        int completedSwitchCount = 0;
        var resourceSamples = new List<NativePlaybackResourceSample>();
        var soakStopwatch = Stopwatch.StartNew();
        CaptureResourceSample(process, soakStopwatch, resourceSamples);

        try
        {
            for (int index = 0; index < request.SwitchCount; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Uri fixture = request.Fixtures[index % request.Fixtures.Count];
                _mediaFailure = NativePlaybackFailure.None;
                _opened = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _advanced = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _failureSignal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var stopwatch = Stopwatch.StartNew();
                _mediaPlayer.Source = MediaSource.CreateFromUri(fixture);
                _mediaPlayer.Play();

                await _opened.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
                double startupMilliseconds = stopwatch.Elapsed.TotalMilliseconds;
                startupSamples.Add(startupMilliseconds);
                (index % request.Fixtures.Count == 0 ? hlsStartupSamples : directStartupSamples)
                    .Add(startupMilliseconds);
                timeoutFailure = NativePlaybackFailure.PlaybackAdvanceTimeout;
                await _advanced.Task.WaitAsync(TimeSpan.FromSeconds(3), cancellationToken);
                _mediaPlayer.Pause();
                _mediaPlayer.Source = null;
                completedSwitchCount++;
                timeoutFailure = NativePlaybackFailure.MediaOpenTimeout;
            }

            NativePlaybackSoakMetrics soakMetrics = NativePlaybackSoakMetrics.None;
            if (request.SoakDuration > TimeSpan.Zero)
            {
                soakMetrics = await RunSoakAsync(
                    request.Fixtures[0],
                    request.SoakDuration,
                    process,
                    soakStopwatch,
                    resourceSamples,
                    cancellationToken);
                if (!soakMetrics.ResourceBudgetPassed)
                {
                    return NativePlaybackProbeResult.Failed(
                        NativePlaybackFailure.ResourceBudgetExceeded,
                        completedSwitchCount,
                        request.SoakDuration,
                        soakMetrics);
                }
            }

            process.Refresh();
            return NativePlaybackProbeResult.Passed(
                request.SwitchCount,
                startupSamples,
                hlsStartupSamples,
                directStartupSamples,
                request.SoakDuration,
                soakMetrics,
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
            _mediaPlayer.IsLoopingEnabled = false;
            _mediaPlayer.Source = null;
            _opened = null;
            _advanced = null;
            _failureSignal = null;
        }

    }

    private async Task<NativePlaybackSoakMetrics> RunSoakAsync(
        Uri fixture,
        TimeSpan soakDuration,
        Process process,
        Stopwatch soakStopwatch,
        List<NativePlaybackResourceSample> resourceSamples,
        CancellationToken cancellationToken)
    {
        _mediaFailure = NativePlaybackFailure.None;
        _opened = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _advanced = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _failureSignal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _mediaPlayer.IsLoopingEnabled = true;
        _mediaPlayer.Source = MediaSource.CreateFromUri(fixture);
        _mediaPlayer.Play();
        await _opened.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
        await _advanced.Task.WaitAsync(TimeSpan.FromSeconds(3), cancellationToken);

        TimeSpan sampleInterval = TimeSpan.FromMinutes(5);
        while (soakStopwatch.Elapsed < soakDuration)
        {
            TimeSpan remaining = soakDuration - soakStopwatch.Elapsed;
            Task delay = Task.Delay(remaining < sampleInterval ? remaining : sampleInterval, cancellationToken);
            Task completed = await Task.WhenAny(delay, _failureSignal.Task);
            if (ReferenceEquals(completed, _failureSignal.Task))
            {
                throw new InvalidOperationException(nameof(NativePlaybackFailure.MediaFailed));
            }

            await delay;
            CaptureResourceSample(process, soakStopwatch, resourceSamples);
        }

        _mediaPlayer.IsLoopingEnabled = false;
        return NativePlaybackSoakMetrics.From(resourceSamples);
    }

    private static void CaptureResourceSample(
        Process process,
        Stopwatch stopwatch,
        List<NativePlaybackResourceSample> samples)
    {
        process.Refresh();
        samples.Add(new NativePlaybackResourceSample(
            stopwatch.Elapsed,
            process.PrivateMemorySize64,
            process.HandleCount));
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
        _failureSignal?.TrySetResult();
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

internal sealed record NativePlaybackProbeRequest(
    IReadOnlyList<Uri> Fixtures,
    int SwitchCount,
    TimeSpan SoakDuration)
{
    private static readonly HashSet<string> AllowedPaths =
    [
        "/direct-h264-aac.ts",
        "/hls.m3u8",
    ];

    internal static NativePlaybackProbeRequest Parse(string? arguments)
    {
        string[] parts = arguments?.Split(' ', StringSplitOptions.RemoveEmptyEntries) ?? [];
        if (parts is not ["probe", string direct, string hls, string switchText, string soakText] ||
            !int.TryParse(switchText, out int switchCount) || switchCount is < 2 or > 100 ||
            !int.TryParse(soakText, out int soakMinutes) || soakMinutes is < 0 or > 480 ||
            (soakMinutes > 0 && switchCount != 100) ||
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

        return new NativePlaybackProbeRequest([hlsUri, directUri], switchCount, TimeSpan.FromMinutes(soakMinutes));
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
    ResourceBudgetExceeded,
    Cancelled,
    UnexpectedFailure,
}

internal sealed record NativePlaybackProbeResult(
    bool Success,
    NativePlaybackFailure Failure,
    int SwitchCount,
    double StartupP95Milliseconds,
    double StartupMaximumMilliseconds,
    double HlsStartupP95Milliseconds,
    double DirectStartupP95Milliseconds,
    int SoakMinutes,
    int ResourceSampleCount,
    long WarmupPrivateBytes,
    long MemoryNetGrowthBytes,
    double MemoryNetGrowthPercent,
    bool MemoryMonotonicIncrease,
    int WarmupHandleCount,
    int HandleNetGrowth,
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
        IReadOnlyList<double> hlsStartupSamples,
        IReadOnlyList<double> directStartupSamples,
        TimeSpan soakDuration,
        NativePlaybackSoakMetrics soakMetrics,
        long initialPrivateBytes,
        long finalPrivateBytes,
        int initialHandleCount,
        int finalHandleCount)
    {
        double[] ordered = startupSamples.Order().ToArray();
        return new NativePlaybackProbeResult(
            true,
            NativePlaybackFailure.None,
            switchCount,
            Percentile95(startupSamples),
            ordered[^1],
            Percentile95(hlsStartupSamples),
            Percentile95(directStartupSamples),
            (int)soakDuration.TotalMinutes,
            soakMetrics.ResourceSampleCount,
            soakMetrics.WarmupPrivateBytes,
            soakMetrics.MemoryNetGrowthBytes,
            soakMetrics.MemoryNetGrowthPercent,
            soakMetrics.MemoryMonotonicIncrease,
            soakMetrics.WarmupHandleCount,
            soakMetrics.HandleNetGrowth,
            initialPrivateBytes,
            finalPrivateBytes,
            initialHandleCount,
            finalHandleCount);
    }

    internal static NativePlaybackProbeResult Failed(
        NativePlaybackFailure failure,
        int completedSwitchCount = 0,
        TimeSpan soakDuration = default,
        NativePlaybackSoakMetrics soakMetrics = default) =>
        new(false, failure, completedSwitchCount, 0, 0, 0, 0,
            (int)soakDuration.TotalMinutes,
            soakMetrics.ResourceSampleCount,
            soakMetrics.WarmupPrivateBytes,
            soakMetrics.MemoryNetGrowthBytes,
            soakMetrics.MemoryNetGrowthPercent,
            soakMetrics.MemoryMonotonicIncrease,
            soakMetrics.WarmupHandleCount,
            soakMetrics.HandleNetGrowth,
            0, 0, 0, 0);

    private static double Percentile95(IReadOnlyList<double> samples)
    {
        double[] ordered = samples.Order().ToArray();
        int percentileIndex = Math.Max(0, (int)Math.Ceiling(ordered.Length * 0.95) - 1);
        return ordered[percentileIndex];
    }

    internal string ToJson() => JsonSerializer.Serialize(this, SerializerOptions);
}

internal readonly record struct NativePlaybackResourceSample(
    TimeSpan Elapsed,
    long PrivateBytes,
    int HandleCount);

internal readonly record struct NativePlaybackSoakMetrics(
    int ResourceSampleCount,
    long WarmupPrivateBytes,
    long MemoryNetGrowthBytes,
    double MemoryNetGrowthPercent,
    bool MemoryMonotonicIncrease,
    int WarmupHandleCount,
    int HandleNetGrowth,
    bool ResourceBudgetPassed)
{
    internal static NativePlaybackSoakMetrics None => new(0, 0, 0, 0, false, 0, 0, true);

    internal static NativePlaybackSoakMetrics From(IReadOnlyList<NativePlaybackResourceSample> samples)
    {
        NativePlaybackResourceSample[] postWarmup = samples
            .Where(sample => sample.Elapsed >= TimeSpan.FromMinutes(30))
            .ToArray();
        if (postWarmup.Length < 2) return new(samples.Count, 0, 0, 0, true, 0, 0, false);

        NativePlaybackResourceSample warmup = postWarmup[0];
        NativePlaybackResourceSample final = postWarmup[^1];
        long growthBytes = final.PrivateBytes - warmup.PrivateBytes;
        double growthPercent = warmup.PrivateBytes == 0 ? double.PositiveInfinity :
            growthBytes * 100d / warmup.PrivateBytes;
        bool monotonic = postWarmup.Zip(postWarmup.Skip(1),
            (left, right) => right.PrivateBytes > left.PrivateBytes).All(value => value);
        bool budgetPassed = growthBytes <= 100L * 1024 * 1024 && growthPercent <= 10d && !monotonic;
        return new NativePlaybackSoakMetrics(
            samples.Count,
            warmup.PrivateBytes,
            growthBytes,
            growthPercent,
            monotonic,
            warmup.HandleCount,
            final.HandleCount - warmup.HandleCount,
            budgetPassed);
    }
}
