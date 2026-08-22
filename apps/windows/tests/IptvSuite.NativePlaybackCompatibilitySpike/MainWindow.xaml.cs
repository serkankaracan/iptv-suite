using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using Windows.Media.Core;
using Windows.Media.Playback;

namespace IptvSuite.NativePlaybackCompatibilitySpike;

public sealed partial class MainWindow : Window, IDisposable
{
    private readonly MediaPlayer _mediaPlayer;
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private readonly TaskCompletionSource _surfaceReady =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private TaskCompletionSource<long>? _opened;
    private TaskCompletionSource? _advanced;
    private TaskCompletionSource? _failureSignal;
    private NativePlaybackFailure _mediaFailure;
    private bool _surfaceLoaded;
    private bool _disposed;

    public MainWindow()
    {
        InitializeComponent();
        _mediaPlayer = new MediaPlayer
        {
            AutoPlay = false,
            AudioCategory = MediaPlayerAudioCategory.Media,
            RealTimePlayback = true,
        };
        _mediaPlayer.MediaOpened += MediaPlayer_MediaOpened;
        _mediaPlayer.MediaFailed += MediaPlayer_MediaFailed;
        _mediaPlayer.PlaybackSession.PositionChanged += PlaybackSession_PositionChanged;
        PlaybackSurface.Loaded += PlaybackSurface_Loaded;
        CompositionTarget.Rendered += CompositionTarget_Rendered;
        PlaybackSurface.SetMediaPlayer(_mediaPlayer);
        Closed += MainWindow_Closed;
    }

    internal async Task<NativePlaybackProbeResult> RunProbeAsync(
        NativePlaybackProbeRequest request,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(request);
        using CancellationTokenSource probeCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _lifetimeCancellation.Token);
        CancellationToken probeCancellationToken = probeCancellation.Token;
        var startupSamples = new List<double>(request.SwitchCount);
        var hlsStartupSamples = new List<double>((request.SwitchCount + 1) / 2);
        var directStartupSamples = new List<double>(request.SwitchCount / 2);
        var sourceDetachSamples = new List<double>(request.SwitchCount + 1);
        using Process process = Process.GetCurrentProcess();
        long initialPrivateBytes = process.PrivateMemorySize64;
        int initialHandles = process.HandleCount;
        NativePlaybackFailure timeoutFailure = NativePlaybackFailure.SurfaceReadinessTimeout;
        int completedSwitchCount = 0;
        int surfaceTransitionCount = 0;
        int detachedSourceCount = 0;
        int playbackRetryCount = 0;
        double startupMaximumMilliseconds = -1;
        int startupMaximumSwitchOrdinal = 0;
        NativePlaybackFixture startupMaximumFixture = NativePlaybackFixture.None;
        int startupMaximumAttemptCount = 0;
        int startupMaximumSurfaceTransitionCount = 0;
        double startupMaximumPreWaitMilliseconds = 0;
        double startupMaximumMediaOpenWaitMilliseconds = 0;
        NativePlaybackStartupStage activeStartupStage = NativePlaybackStartupStage.SurfaceReadiness;
        long activeStartupStarted = Stopwatch.GetTimestamp();
        long activeStartupStageStarted = activeStartupStarted;
        int activeStartupSwitchOrdinal = 0;
        NativePlaybackFixture activeStartupFixture = NativePlaybackFixture.None;
        int activeStartupAttemptCount = 0;
        int activeStartupSurfaceTransitionCount = 0;
        double activeStartupSourceCreationMilliseconds = 0;
        double activeStartupSourceAssignmentMilliseconds = 0;
        double activeStartupPlayInvocationMilliseconds = 0;
        NativePlaybackStartupFailureDiagnostic startupFailureDiagnostic = default;
        var resourceSamples = new List<NativePlaybackResourceSample>();
        var soakStopwatch = Stopwatch.StartNew();
        CaptureResourceSample(process, soakStopwatch, resourceSamples);

        void BeginStartupStage(NativePlaybackStartupStage stage)
        {
            activeStartupStage = stage;
            activeStartupStageStarted = Stopwatch.GetTimestamp();
        }

        NativePlaybackStartupFailureDiagnostic CaptureStartupFailureDiagnostic()
        {
            if (activeStartupStage == NativePlaybackStartupStage.None)
            {
                return default;
            }

            long captured = Stopwatch.GetTimestamp();
            return new NativePlaybackStartupFailureDiagnostic(
                activeStartupStage,
                activeStartupSwitchOrdinal,
                activeStartupFixture,
                activeStartupAttemptCount,
                activeStartupSurfaceTransitionCount,
                Stopwatch.GetElapsedTime(activeStartupStarted, captured).TotalMilliseconds,
                activeStartupSourceCreationMilliseconds,
                activeStartupSourceAssignmentMilliseconds,
                activeStartupPlayInvocationMilliseconds,
                Stopwatch.GetElapsedTime(activeStartupStageStarted, captured).TotalMilliseconds);
        }

        try
        {
            await _surfaceReady.Task.WaitAsync(TimeSpan.FromSeconds(5), probeCancellationToken);
            ObjectDisposedException.ThrowIf(_disposed, this);
            activeStartupStage = NativePlaybackStartupStage.None;
            timeoutFailure = NativePlaybackFailure.MediaOpenTimeout;
            for (int index = 0; index < request.SwitchCount; index++)
            {
                probeCancellationToken.ThrowIfCancellationRequested();
                int switchSurfaceTransitionCount = await ApplySurfaceTransitionAsync(
                    index,
                    request.SwitchCount,
                    probeCancellationToken);
                surfaceTransitionCount += switchSurfaceTransitionCount;
                Uri fixture = request.Fixtures[index % request.Fixtures.Count];
                NativePlaybackFixture fixtureKind = index % request.Fixtures.Count == 0
                    ? NativePlaybackFixture.HlsH264AacMpegTs
                    : NativePlaybackFixture.DirectH264AacMpegTs;
                activeStartupStarted = Stopwatch.GetTimestamp();
                long startupStarted = activeStartupStarted;
                activeStartupSwitchOrdinal = index + 1;
                activeStartupFixture = fixtureKind;
                activeStartupAttemptCount = 0;
                activeStartupSurfaceTransitionCount = switchSurfaceTransitionCount;
                activeStartupSourceCreationMilliseconds = 0;
                activeStartupSourceAssignmentMilliseconds = 0;
                activeStartupPlayInvocationMilliseconds = 0;
                double startupMilliseconds = 0;
                double startupPreWaitMilliseconds = 0;
                double startupMediaOpenWaitMilliseconds = 0;
                int startupAttemptCount = 0;
                for (int attempt = 0; attempt < 2; attempt++)
                {
                    startupAttemptCount = attempt + 1;
                    startupFailureDiagnostic = default;
                    activeStartupAttemptCount = startupAttemptCount;
                    activeStartupSourceCreationMilliseconds = 0;
                    activeStartupSourceAssignmentMilliseconds = 0;
                    activeStartupPlayInvocationMilliseconds = 0;
                    bool retryRequested = false;
                    bool sourceDetached = false;
                    _mediaFailure = NativePlaybackFailure.None;
                    _opened = new TaskCompletionSource<long>(TaskCreationOptions.RunContinuationsAsynchronously);
                    _advanced = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    _failureSignal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    BeginStartupStage(NativePlaybackStartupStage.SourceCreation);
                    MediaSource source = MediaSource.CreateFromUri(fixture);
                    activeStartupSourceCreationMilliseconds =
                        Stopwatch.GetElapsedTime(activeStartupStageStarted).TotalMilliseconds;
                    try
                    {
                        BeginStartupStage(NativePlaybackStartupStage.SourceAssignment);
                        _mediaPlayer.Source = source;
                        activeStartupSourceAssignmentMilliseconds =
                            Stopwatch.GetElapsedTime(activeStartupStageStarted).TotalMilliseconds;
                        BeginStartupStage(NativePlaybackStartupStage.PlayInvocation);
                        _mediaPlayer.Play();
                        activeStartupPlayInvocationMilliseconds =
                            Stopwatch.GetElapsedTime(activeStartupStageStarted).TotalMilliseconds;
                        long mediaOpenWaitStarted = Stopwatch.GetTimestamp();
                        activeStartupStage = NativePlaybackStartupStage.MediaOpenWait;
                        activeStartupStageStarted = mediaOpenWaitStarted;
                        long openedTimestamp = await _opened.Task.WaitAsync(
                            TimeSpan.FromSeconds(5),
                            probeCancellationToken);
                        long effectiveWaitStarted = Math.Min(mediaOpenWaitStarted, openedTimestamp);
                        startupMilliseconds = Stopwatch.GetElapsedTime(startupStarted, openedTimestamp).TotalMilliseconds;
                        startupPreWaitMilliseconds =
                            Stopwatch.GetElapsedTime(startupStarted, effectiveWaitStarted).TotalMilliseconds;
                        startupMediaOpenWaitMilliseconds =
                            Stopwatch.GetElapsedTime(effectiveWaitStarted, openedTimestamp).TotalMilliseconds;
                        timeoutFailure = NativePlaybackFailure.PlaybackAdvanceTimeout;
                        BeginStartupStage(NativePlaybackStartupStage.PlaybackAdvanceWait);
                        await _advanced.Task.WaitAsync(TimeSpan.FromSeconds(3), probeCancellationToken);
                        activeStartupStage = NativePlaybackStartupStage.None;
                        sourceDetachSamples.Add(await DetachSourceAsync(
                            pauseIfSupported: true,
                            probeCancellationToken));
                        sourceDetached = true;
                        detachedSourceCount++;
                        break;
                    }
                    catch (TimeoutException)
                    {
                        startupFailureDiagnostic = CaptureStartupFailureDiagnostic();
                        throw;
                    }
                    catch (InvalidOperationException) when (
                        _mediaFailure == NativePlaybackFailure.MediaFailed && attempt == 0)
                    {
                        playbackRetryCount++;
                        sourceDetachSamples.Add(await DetachSourceAsync(
                            pauseIfSupported: false,
                            probeCancellationToken));
                        sourceDetached = true;
                        detachedSourceCount++;
                        retryRequested = true;
                    }
                    catch (InvalidOperationException) when (
                        _mediaFailure == NativePlaybackFailure.MediaFailed)
                    {
                        startupFailureDiagnostic = CaptureStartupFailureDiagnostic();
                        throw;
                    }
                    finally
                    {
                        BestEffortResetAfterProbe();
                        if (sourceDetached)
                        {
                            DisposeMediaSource(source);
                        }
                        else
                        {
                            BestEffortDisposeMediaSource(source);
                        }
                    }

                    if (retryRequested)
                    {
                        activeStartupStage = NativePlaybackStartupStage.None;
                        await Task.Delay(TimeSpan.FromMilliseconds(250), probeCancellationToken);
                    }
                }

                startupSamples.Add(startupMilliseconds);
                (index % request.Fixtures.Count == 0 ? hlsStartupSamples : directStartupSamples)
                    .Add(startupMilliseconds);
                if (startupMilliseconds > startupMaximumMilliseconds)
                {
                    startupMaximumMilliseconds = startupMilliseconds;
                    startupMaximumSwitchOrdinal = index + 1;
                    startupMaximumFixture = fixtureKind;
                    startupMaximumAttemptCount = startupAttemptCount;
                    startupMaximumSurfaceTransitionCount = switchSurfaceTransitionCount;
                    startupMaximumPreWaitMilliseconds = startupPreWaitMilliseconds;
                    startupMaximumMediaOpenWaitMilliseconds = startupMediaOpenWaitMilliseconds;
                }
                completedSwitchCount++;
                activeStartupStage = NativePlaybackStartupStage.None;
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
                    sourceDetachSamples,
                    probeCancellationToken);
                detachedSourceCount++;
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
                startupMaximumSwitchOrdinal,
                startupMaximumFixture,
                startupMaximumAttemptCount,
                startupMaximumSurfaceTransitionCount,
                startupMaximumPreWaitMilliseconds,
                startupMaximumMediaOpenWaitMilliseconds,
                request.SoakDuration,
                soakMetrics,
                surfaceTransitionCount,
                detachedSourceCount,
                playbackRetryCount,
                sourceDetachSamples,
                initialPrivateBytes,
                process.PrivateMemorySize64,
                initialHandles,
                process.HandleCount);
        }
        catch (TimeoutException)
        {
            return NativePlaybackProbeResult.Failed(
                _mediaFailure == NativePlaybackFailure.None ? timeoutFailure : _mediaFailure,
                completedSwitchCount,
                playbackRetryCount: playbackRetryCount,
                startupFailureDiagnostic: startupFailureDiagnostic.Stage == NativePlaybackStartupStage.None
                    ? CaptureStartupFailureDiagnostic()
                    : startupFailureDiagnostic);
        }
        catch (OperationCanceledException)
        {
            return NativePlaybackProbeResult.Failed(
                NativePlaybackFailure.Cancelled,
                completedSwitchCount,
                playbackRetryCount: playbackRetryCount,
                startupFailureDiagnostic: CaptureStartupFailureDiagnostic());
        }
        catch (InvalidOperationException) when (_mediaFailure == NativePlaybackFailure.MediaFailed)
        {
            return NativePlaybackProbeResult.Failed(
                NativePlaybackFailure.MediaFailed,
                completedSwitchCount,
                playbackRetryCount: playbackRetryCount,
                startupFailureDiagnostic: CaptureStartupFailureDiagnostic());
        }
        catch (NativePlaybackSurfaceException)
        {
            return NativePlaybackProbeResult.Failed(
                NativePlaybackFailure.SurfaceLifecycleFailed,
                completedSwitchCount,
                surfaceTransitionCount: surfaceTransitionCount,
                playbackRetryCount: playbackRetryCount);
        }
        catch (NativePlaybackSourceDetachmentException exception)
        {
            return NativePlaybackProbeResult.Failed(
                NativePlaybackFailure.SourceDetachmentTimeout,
                completedSwitchCount,
                surfaceTransitionCount: surfaceTransitionCount,
                detachedSourceCount: detachedSourceCount,
                playbackStateBeforeDetach: exception.PlaybackStateBeforeDetach,
                sourceDetached: exception.SourceDetached,
                canPauseBeforeDetach: exception.CanPauseBeforeDetach,
                canSeekBeforeDetach: exception.CanSeekBeforeDetach,
                playbackRetryCount: playbackRetryCount);
        }
        catch (NativePlaybackTeardownException exception)
        {
            return NativePlaybackProbeResult.Failed(
                NativePlaybackFailure.SourceDetachmentFailed,
                completedSwitchCount,
                surfaceTransitionCount: surfaceTransitionCount,
                detachedSourceCount: detachedSourceCount,
                teardownStage: exception.Stage,
                exceptionCategory: exception.Category,
                exceptionHResult: exception.ExceptionHResult,
                playbackRetryCount: playbackRetryCount);
        }
        finally
        {
            BestEffortResetAfterProbe();
            _opened = null;
            _advanced = null;
            _failureSignal = null;
        }

    }

    private void BestEffortResetAfterProbe()
    {
        try
        {
            if (_mediaPlayer.Source is not null && _mediaPlayer.PlaybackSession.CanPause)
            {
                _mediaPlayer.Pause();
            }
        }
        catch (Exception exception) when (
            exception is not OutOfMemoryException and
            not StackOverflowException and
            not AccessViolationException)
        {
            // The controller owns exact process/package cleanup. Do not mask the probe's typed result.
        }

        try
        {
            _mediaPlayer.IsLoopingEnabled = false;
            _mediaPlayer.Source = null;
        }
        catch (Exception exception) when (
            exception is not OutOfMemoryException and
            not StackOverflowException and
            not AccessViolationException)
        {
            // The controller owns exact process/package cleanup. Do not mask the probe's typed result.
        }
    }

    private static void DisposeMediaSource(MediaSource source)
    {
        try
        {
            source.Dispose();
        }
        catch (Exception exception) when (
            exception is not OutOfMemoryException and
            not StackOverflowException and
            not AccessViolationException)
        {
            throw new NativePlaybackTeardownException(
                NativePlaybackTeardownStage.MediaSourceDispose,
                exception);
        }
    }

    private static void BestEffortDisposeMediaSource(MediaSource source)
    {
        try
        {
            source.Dispose();
        }
        catch (Exception exception) when (
            exception is not OutOfMemoryException and
            not StackOverflowException and
            not AccessViolationException)
        {
            // Preserve the primary typed probe failure; the controller terminates the disposable process.
        }
    }

    private async Task<double> DetachSourceAsync(
        bool pauseIfSupported,
        CancellationToken cancellationToken)
    {
        NativePlaybackTeardownStage stage = NativePlaybackTeardownStage.SourceInspection;
        try
        {
            if (_mediaPlayer.Source is null)
            {
                return 0;
            }

            MediaPlaybackState playbackStateBeforeDetach = MediaPlaybackState.None;
            bool canPauseBeforeDetach = false;
            bool canSeekBeforeDetach = false;
            if (pauseIfSupported)
            {
                stage = NativePlaybackTeardownStage.PlaybackSessionInspection;
                playbackStateBeforeDetach = _mediaPlayer.PlaybackSession.PlaybackState;
                canPauseBeforeDetach = _mediaPlayer.PlaybackSession.CanPause;
                canSeekBeforeDetach = _mediaPlayer.PlaybackSession.CanSeek;
                stage = NativePlaybackTeardownStage.Pause;
                if (canPauseBeforeDetach)
                {
                    _mediaPlayer.Pause();
                }
            }

            stage = NativePlaybackTeardownStage.SourceClear;
            var timeout = Stopwatch.StartNew();
            _mediaPlayer.Source = null;
            stage = NativePlaybackTeardownStage.SourceInspection;

            while (_mediaPlayer.Source is not null)
            {
                if (timeout.Elapsed >= TimeSpan.FromSeconds(5))
                {
                    throw new NativePlaybackSourceDetachmentException(
                        playbackStateBeforeDetach,
                        false,
                        canPauseBeforeDetach,
                        canSeekBeforeDetach);
                }

                await Task.Delay(TimeSpan.FromMilliseconds(20), cancellationToken);
            }

            return timeout.Elapsed.TotalMilliseconds;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (NativePlaybackSourceDetachmentException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is not OutOfMemoryException and
            not StackOverflowException and
            not AccessViolationException)
        {
            throw new NativePlaybackTeardownException(stage, exception);
        }
    }

    private async Task<int> ApplySurfaceTransitionAsync(
        int switchIndex,
        int switchCount,
        CancellationToken cancellationToken)
    {
        if (switchCount < 25) return 0;

        try
        {
            if (switchIndex == switchCount / 5)
            {
                AppWindow.Resize(new SizeInt32(960, 540));
                await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken);
                return 1;
            }

            if (switchIndex == switchCount * 2 / 5)
            {
                if (AppWindow.Presenter is not OverlappedPresenter presenter)
                {
                    throw new NativePlaybackSurfaceException();
                }

                presenter.Minimize();
                await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken);
                presenter.Restore();
                await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken);
                return 2;
            }

            if (switchIndex == switchCount * 3 / 5)
            {
                AppWindow.SetPresenter(AppWindowPresenterKind.FullScreen);
                await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken);
                return 1;
            }

            if (switchIndex == switchCount * 4 / 5)
            {
                AppWindow.SetPresenter(AppWindowPresenterKind.Overlapped);
                await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken);
                AppWindow.Resize(new SizeInt32(1280, 720));
                await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken);
                return 2;
            }

            return 0;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (NativePlaybackSurfaceException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is not OutOfMemoryException and
            not StackOverflowException and
            not AccessViolationException)
        {
            throw new NativePlaybackSurfaceException();
        }
    }

    private async Task<NativePlaybackSoakMetrics> RunSoakAsync(
        Uri fixture,
        TimeSpan soakDuration,
        Process process,
        Stopwatch soakStopwatch,
        List<NativePlaybackResourceSample> resourceSamples,
        List<double> sourceDetachSamples,
        CancellationToken cancellationToken)
    {
        _mediaFailure = NativePlaybackFailure.None;
        _opened = new TaskCompletionSource<long>(TaskCreationOptions.RunContinuationsAsynchronously);
        _advanced = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _failureSignal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _mediaPlayer.IsLoopingEnabled = true;
        MediaSource source = MediaSource.CreateFromUri(fixture);
        bool sourceDetached = false;
        try
        {
            _mediaPlayer.Source = source;
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
            NativePlaybackSoakMetrics metrics = NativePlaybackSoakMetrics.From(resourceSamples);
            sourceDetachSamples.Add(await DetachSourceAsync(
                pauseIfSupported: true,
                cancellationToken));
            sourceDetached = true;
            return metrics;
        }
        finally
        {
            BestEffortResetAfterProbe();
            if (sourceDetached)
            {
                DisposeMediaSource(source);
            }
            else
            {
                BestEffortDisposeMediaSource(source);
            }
        }
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

    internal void ShowResult(NativePlaybackProbeResult result)
    {
        if (_disposed) return;
        DispatcherQueue.TryEnqueue(() =>
        {
            if (!_disposed) StateText.Text = result.Success ? "Passed" : $"Failed: {result.Failure}";
        });
    }

    private void PlaybackSurface_Loaded(object sender, RoutedEventArgs args) => _surfaceLoaded = true;

    private void CompositionTarget_Rendered(object? sender, RenderedEventArgs args)
    {
        if (!_surfaceLoaded) return;
        CompositionTarget.Rendered -= CompositionTarget_Rendered;
        _surfaceReady.TrySetResult();
    }

    private void MediaPlayer_MediaOpened(MediaPlayer sender, object args)
    {
        _opened?.TrySetResult(Stopwatch.GetTimestamp());
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
        _lifetimeCancellation.Cancel();
        Closed -= MainWindow_Closed;
        _mediaPlayer.MediaOpened -= MediaPlayer_MediaOpened;
        _mediaPlayer.MediaFailed -= MediaPlayer_MediaFailed;
        _mediaPlayer.PlaybackSession.PositionChanged -= PlaybackSession_PositionChanged;
        PlaybackSurface.Loaded -= PlaybackSurface_Loaded;
        CompositionTarget.Rendered -= CompositionTarget_Rendered;
        PlaybackSurface.SetMediaPlayer(null);
        _mediaPlayer.Source = null;
        _mediaPlayer.Dispose();
        _lifetimeCancellation.Dispose();
        GC.SuppressFinalize(this);
    }
}

internal sealed record NativePlaybackProbeRequest(
    Guid RunId,
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
        if (parts is not ["probe", string runIdText, string direct, string hls, string switchText, string soakText] ||
            !Guid.TryParseExact(runIdText, "N", out Guid runId) ||
            runId.ToString("N") != runIdText ||
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

        return new NativePlaybackProbeRequest(runId, [hlsUri, directUri], switchCount, TimeSpan.FromMinutes(soakMinutes));
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
    RuntimeDependencyResolutionFailed,
    SurfaceReadinessTimeout,
    MediaFailed,
    MediaOpenTimeout,
    PlaybackAdvanceTimeout,
    ResourceBudgetExceeded,
    SurfaceLifecycleFailed,
    SourceDetachmentTimeout,
    SourceDetachmentFailed,
    Cancelled,
    UnexpectedFailure,
}

internal enum NativePlaybackFixture
{
    None,
    HlsH264AacMpegTs,
    DirectH264AacMpegTs,
}

internal enum NativePlaybackStartupStage
{
    None,
    SurfaceReadiness,
    SourceCreation,
    SourceAssignment,
    PlayInvocation,
    MediaOpenWait,
    PlaybackAdvanceWait,
}

internal readonly record struct NativePlaybackStartupFailureDiagnostic(
    NativePlaybackStartupStage Stage,
    int SwitchOrdinal,
    NativePlaybackFixture Fixture,
    int AttemptCount,
    int SurfaceTransitionCount,
    double TotalMilliseconds,
    double SourceCreationMilliseconds,
    double SourceAssignmentMilliseconds,
    double PlayInvocationMilliseconds,
    double ActiveStageElapsedMilliseconds);

internal sealed record NativePlaybackProbeResult(
    bool Success,
    NativePlaybackFailure Failure,
    int SwitchCount,
    double StartupP95Milliseconds,
    double StartupMaximumMilliseconds,
    double HlsStartupP95Milliseconds,
    double DirectStartupP95Milliseconds,
    int StartupMaximumSwitchOrdinal,
    NativePlaybackFixture StartupMaximumFixture,
    int StartupMaximumAttemptCount,
    int StartupMaximumSurfaceTransitionCount,
    double StartupMaximumPreWaitMilliseconds,
    double StartupMaximumMediaOpenWaitMilliseconds,
    double HlsStartupMaximumMilliseconds,
    double DirectStartupMaximumMilliseconds,
    NativePlaybackStartupStage StartupFailureStage,
    int StartupFailureSwitchOrdinal,
    NativePlaybackFixture StartupFailureFixture,
    int StartupFailureAttemptCount,
    int StartupFailureSurfaceTransitionCount,
    double StartupFailureTotalMilliseconds,
    double StartupFailureSourceCreationMilliseconds,
    double StartupFailureSourceAssignmentMilliseconds,
    double StartupFailurePlayInvocationMilliseconds,
    double StartupFailureActiveStageElapsedMilliseconds,
    int SoakMinutes,
    int ResourceSampleCount,
    long WarmupPrivateBytes,
    long MemoryNetGrowthBytes,
    double MemoryNetGrowthPercent,
    bool MemoryMonotonicIncrease,
    int WarmupHandleCount,
    int HandleNetGrowth,
    int SurfaceTransitionCount,
    int DetachedSourceCount,
    int PlaybackRetryCount,
    double SourceDetachP95Milliseconds,
    double SourceDetachMaximumMilliseconds,
    MediaPlaybackState PlaybackStateBeforeDetach,
    bool SourceDetached,
    bool CanPauseBeforeDetach,
    bool CanSeekBeforeDetach,
    NativePlaybackTeardownStage TeardownStage,
    NativePlaybackExceptionCategory ExceptionCategory,
    int ExceptionHResult,
    long InitialPrivateBytes,
    long FinalPrivateBytes,
    int InitialHandleCount,
    int FinalHandleCount)
{
    internal static NativePlaybackProbeResult Passed(
        int switchCount,
        IReadOnlyList<double> startupSamples,
        IReadOnlyList<double> hlsStartupSamples,
        IReadOnlyList<double> directStartupSamples,
        int startupMaximumSwitchOrdinal,
        NativePlaybackFixture startupMaximumFixture,
        int startupMaximumAttemptCount,
        int startupMaximumSurfaceTransitionCount,
        double startupMaximumPreWaitMilliseconds,
        double startupMaximumMediaOpenWaitMilliseconds,
        TimeSpan soakDuration,
        NativePlaybackSoakMetrics soakMetrics,
        int surfaceTransitionCount,
        int detachedSourceCount,
        int playbackRetryCount,
        IReadOnlyList<double> sourceDetachSamples,
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
            startupMaximumSwitchOrdinal,
            startupMaximumFixture,
            startupMaximumAttemptCount,
            startupMaximumSurfaceTransitionCount,
            startupMaximumPreWaitMilliseconds,
            startupMaximumMediaOpenWaitMilliseconds,
            hlsStartupSamples.Max(),
            directStartupSamples.Max(),
            NativePlaybackStartupStage.None,
            0,
            NativePlaybackFixture.None,
            0,
            0,
            0,
            0,
            0,
            0,
            0,
            (int)soakDuration.TotalMinutes,
            soakMetrics.ResourceSampleCount,
            soakMetrics.WarmupPrivateBytes,
            soakMetrics.MemoryNetGrowthBytes,
            soakMetrics.MemoryNetGrowthPercent,
            soakMetrics.MemoryMonotonicIncrease,
            soakMetrics.WarmupHandleCount,
            soakMetrics.HandleNetGrowth,
            surfaceTransitionCount,
            detachedSourceCount,
            playbackRetryCount,
            Percentile95(sourceDetachSamples),
            sourceDetachSamples.Max(),
            MediaPlaybackState.None,
            true,
            false,
            false,
            NativePlaybackTeardownStage.None,
            NativePlaybackExceptionCategory.None,
            0,
            initialPrivateBytes,
            finalPrivateBytes,
            initialHandleCount,
            finalHandleCount);
    }

    internal static NativePlaybackProbeResult Failed(
        NativePlaybackFailure failure,
        int completedSwitchCount = 0,
        TimeSpan soakDuration = default,
        NativePlaybackSoakMetrics soakMetrics = default,
        int surfaceTransitionCount = 0,
        int detachedSourceCount = 0,
        MediaPlaybackState playbackStateBeforeDetach = MediaPlaybackState.None,
        bool sourceDetached = false,
        bool canPauseBeforeDetach = false,
        bool canSeekBeforeDetach = false,
        NativePlaybackTeardownStage teardownStage = NativePlaybackTeardownStage.None,
        NativePlaybackExceptionCategory exceptionCategory = NativePlaybackExceptionCategory.None,
        int exceptionHResult = 0,
        int playbackRetryCount = 0,
        NativePlaybackStartupFailureDiagnostic startupFailureDiagnostic = default) =>
        new(false, failure, completedSwitchCount, 0, 0, 0, 0,
            0, NativePlaybackFixture.None, 0, 0, 0, 0, 0, 0,
            startupFailureDiagnostic.Stage,
            startupFailureDiagnostic.SwitchOrdinal,
            startupFailureDiagnostic.Fixture,
            startupFailureDiagnostic.AttemptCount,
            startupFailureDiagnostic.SurfaceTransitionCount,
            startupFailureDiagnostic.TotalMilliseconds,
            startupFailureDiagnostic.SourceCreationMilliseconds,
            startupFailureDiagnostic.SourceAssignmentMilliseconds,
            startupFailureDiagnostic.PlayInvocationMilliseconds,
            startupFailureDiagnostic.ActiveStageElapsedMilliseconds,
            (int)soakDuration.TotalMinutes,
            soakMetrics.ResourceSampleCount,
            soakMetrics.WarmupPrivateBytes,
            soakMetrics.MemoryNetGrowthBytes,
            soakMetrics.MemoryNetGrowthPercent,
            soakMetrics.MemoryMonotonicIncrease,
            soakMetrics.WarmupHandleCount,
            soakMetrics.HandleNetGrowth,
            surfaceTransitionCount,
            detachedSourceCount,
            playbackRetryCount,
            0,
            0,
            playbackStateBeforeDetach,
            sourceDetached,
            canPauseBeforeDetach,
            canSeekBeforeDetach,
            teardownStage,
            exceptionCategory,
            exceptionHResult,
            0, 0, 0, 0);

    private static double Percentile95(IReadOnlyList<double> samples)
    {
        double[] ordered = samples.Order().ToArray();
        int percentileIndex = Math.Max(0, (int)Math.Ceiling(ordered.Length * 0.95) - 1);
        return ordered[percentileIndex];
    }

}

internal sealed class NativePlaybackSurfaceException : Exception;

internal sealed class NativePlaybackSourceDetachmentException(
    MediaPlaybackState playbackStateBeforeDetach,
    bool sourceDetached,
    bool canPauseBeforeDetach,
    bool canSeekBeforeDetach) : Exception
{
    internal MediaPlaybackState PlaybackStateBeforeDetach { get; } = playbackStateBeforeDetach;

    internal bool SourceDetached { get; } = sourceDetached;

    internal bool CanPauseBeforeDetach { get; } = canPauseBeforeDetach;

    internal bool CanSeekBeforeDetach { get; } = canSeekBeforeDetach;
}

internal enum NativePlaybackTeardownStage
{
    None,
    PlaybackSessionInspection,
    Pause,
    SourceClear,
    SourceInspection,
    MediaSourceDispose,
}

internal enum NativePlaybackExceptionCategory
{
    None,
    Com,
    InvalidOperation,
    ObjectDisposed,
    Other,
}

internal sealed class NativePlaybackTeardownException(
    NativePlaybackTeardownStage stage,
    Exception exception) : Exception
{
    internal NativePlaybackTeardownStage Stage { get; } = stage;

    internal NativePlaybackExceptionCategory Category { get; } = exception switch
    {
        COMException => NativePlaybackExceptionCategory.Com,
        ObjectDisposedException => NativePlaybackExceptionCategory.ObjectDisposed,
        InvalidOperationException => NativePlaybackExceptionCategory.InvalidOperation,
        _ => NativePlaybackExceptionCategory.Other,
    };

    internal int ExceptionHResult { get; } = exception.HResult;
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
