using System.Text;
using IptvSuite.Application;
using IptvSuite.Domain;
using IptvSuite.Infrastructure;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.Media.Core;
using Windows.Media.Playback;

namespace IptvSuite.Windows;

internal sealed class WindowsNativePlaybackEngine : IPlaybackEngine
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private readonly object _sync = new();
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private readonly SqlitePlaybackSourceResolver _resolver;
    private readonly MediaPlayerElement _surface;
    private readonly DispatcherQueue _dispatcherQueue;
    private readonly MediaPlayer _mediaPlayer;
    private readonly PlaybackFaultWatchdog _faultWatchdog;
    private PlaybackEngineSnapshot _current = PlaybackEngineSnapshot.Closed();
    private PlaybackControlSnapshot _controls = PlaybackControlSnapshot.Idle(
        PlaybackVolume.FromPercent(100),
        isMuted: false,
        PlaybackAspectMode.Fit);
    private SessionContext? _active;
    private Task? _disposeTask;
    private long _generation;
    private bool _disposeStarted;

    internal WindowsNativePlaybackEngine(
        SqlitePlaybackSourceResolver resolver,
        MediaPlayerElement surface)
    {
        _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
        _surface = surface ?? throw new ArgumentNullException(nameof(surface));
        _dispatcherQueue = surface.DispatcherQueue ??
            throw new InvalidOperationException("The playback dispatcher is unavailable.");
        if (!_dispatcherQueue.HasThreadAccess)
        {
            throw new InvalidOperationException("The playback engine must be created on its UI thread.");
        }

        _mediaPlayer = new MediaPlayer
        {
            AudioCategory = MediaPlayerAudioCategory.Media,
            AutoPlay = false,
            RealTimePlayback = true,
        };
        _surface.SetMediaPlayer(_mediaPlayer);
        _faultWatchdog = new PlaybackFaultWatchdog(
            new PlaybackFaultWatchdogOptions(
                TimeSpan.FromSeconds(10),
                TimeSpan.FromSeconds(5)),
            TimeProvider.System);
        _faultWatchdog.Expired += FaultWatchdog_Expired;
    }

    public event EventHandler<PlaybackEngineStateChangedEventArgs>? StateChanged;

    public PlaybackEngineSnapshot Current
    {
        get
        {
            lock (_sync)
            {
                return _current;
            }
        }
    }

    public PlaybackControlSnapshot CurrentControls
    {
        get
        {
            lock (_sync)
            {
                return _controls;
            }
        }
    }

    public async ValueTask<PlaybackEngineOperationResult> OpenAsync(
        PlaybackSessionId sessionId,
        PlaybackSelection selection,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(selection);
        cancellationToken.ThrowIfCancellationRequested();
        if (sessionId.IsEmpty || IsDisposeStarted())
        {
            return PlaybackEngineOperationResult.Failed(DomainErrorCode.PlaybackStartFailed);
        }

        using CancellationTokenSource operationCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                _lifetimeCancellation.Token);
        CancellationToken operationToken = operationCancellation.Token;
        bool gateEntered = false;
        try
        {
            await _operationGate.WaitAsync(operationToken).ConfigureAwait(false);
            gateEntered = true;
            if (IsDisposeStarted())
            {
                return PlaybackEngineOperationResult.Failed(DomainErrorCode.PlaybackStartFailed);
            }

            PlaybackSourceResolutionResult resolved;
            try
            {
                resolved = await _resolver.ResolveAsync(selection, operationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception) when (IsRecoverable(exception))
            {
                return PlaybackEngineOperationResult.Failed(DomainErrorCode.StorageUnavailable);
            }

            if (!resolved.IsSuccess)
            {
                return PlaybackEngineOperationResult.Failed(
                    MapResolutionFailure(resolved.Failure));
            }

            using SecretLease lease = resolved.Lease!;
            try
            {
                return await RunOnDispatcherAsync(
                    () => OpenOnUiThread(sessionId, lease.Value.Span),
                    operationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception) when (IsRecoverable(exception))
            {
                return PlaybackEngineOperationResult.Failed(
                    DomainErrorCode.PlaybackStartFailed);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellationToken);
        }
        catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested)
        {
            return PlaybackEngineOperationResult.Failed(DomainErrorCode.OperationCancelled);
        }
        finally
        {
            if (gateEntered)
            {
                _operationGate.Release();
            }
        }
    }

    public ValueTask<PlaybackEngineOperationResult> PlayAsync(
        PlaybackSessionId sessionId,
        CancellationToken cancellationToken = default) =>
        ExecuteOnUiThreadAsync(
            sessionId,
            () => ExecutePlaybackCommandOnUiThread(
                sessionId,
                PlaybackIntent.Play,
                _mediaPlayer.Play),
            DomainErrorCode.PlaybackStartFailed,
            cancellationToken);

    public ValueTask<PlaybackEngineOperationResult> PauseAsync(
        PlaybackSessionId sessionId,
        CancellationToken cancellationToken = default) =>
        ExecuteOnUiThreadAsync(
            sessionId,
            () => ExecutePlaybackCommandOnUiThread(
                sessionId,
                PlaybackIntent.Pause,
                _mediaPlayer.Pause),
            DomainErrorCode.PlaybackControlFailed,
            cancellationToken);

    public async ValueTask<PlaybackEngineOperationResult> StopAsync(
        PlaybackSessionId sessionId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (IsDisposeStarted())
        {
            return PlaybackEngineOperationResult.Succeeded();
        }

        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await RunOnDispatcherAsync(
                () => StopOnUiThread(sessionId),
                CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception) when (IsRecoverable(exception))
        {
            return PlaybackEngineOperationResult.Failed(DomainErrorCode.StreamInterrupted);
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public ValueTask<PlaybackEngineOperationResult> SetVolumeAsync(
        PlaybackSessionId sessionId,
        PlaybackVolume volume,
        CancellationToken cancellationToken = default) =>
        ExecuteControlOnUiThreadAsync(
            sessionId,
            () =>
            {
                _mediaPlayer.Volume = volume.Percent / 100d;
                UpdateControls(sessionId, volume: volume);
            },
            cancellationToken);

    public ValueTask<PlaybackEngineOperationResult> SetMutedAsync(
        PlaybackSessionId sessionId,
        bool isMuted,
        CancellationToken cancellationToken = default) =>
        ExecuteControlOnUiThreadAsync(
            sessionId,
            () =>
            {
                _mediaPlayer.IsMuted = isMuted;
                UpdateControls(sessionId, isMuted: isMuted);
            },
            cancellationToken);

    public ValueTask<PlaybackEngineOperationResult> SetAspectModeAsync(
        PlaybackSessionId sessionId,
        PlaybackAspectMode aspectMode,
        CancellationToken cancellationToken = default)
    {
        if (!Enum.IsDefined(aspectMode))
        {
            return ValueTask.FromResult(
                PlaybackEngineOperationResult.Failed(DomainErrorCode.PlaybackControlFailed));
        }

        return ExecuteControlOnUiThreadAsync(
            sessionId,
            () =>
            {
                _surface.Stretch = aspectMode == PlaybackAspectMode.Fit
                    ? Stretch.Uniform
                    : Stretch.UniformToFill;
                UpdateControls(sessionId, aspectMode: aspectMode);
            },
            cancellationToken);
    }

    public ValueTask<DomainResult<PlaybackTrackSnapshot>> GetTracksAsync(
        PlaybackSessionId sessionId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            if (_disposeStarted || _active?.SessionId != sessionId ||
                _current.State is PlaybackState.Closed or PlaybackState.Failed)
            {
                return ValueTask.FromResult(
                    DomainResult.Failure<PlaybackTrackSnapshot>(
                        DomainErrorCode.PlaybackControlFailed));
            }
        }

        PlaybackTrackSnapshot tracks = PlaybackTrackSnapshot.Create(
            sessionId,
            PlaybackTrackCapabilities.None,
            [
                new PlaybackTrackInfo(
                    PlaybackTrackId.Create(sessionId, PlaybackTrackKind.Audio, ordinal: 1),
                    isSelected: true,
                    isSelectable: false),
            ]);
        return ValueTask.FromResult(DomainResult.Success(tracks));
    }

    public ValueTask<PlaybackEngineOperationResult> SelectTrackAsync(
        PlaybackSessionId sessionId,
        PlaybackTrackId trackId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(
            PlaybackEngineOperationResult.Failed(DomainErrorCode.PlaybackControlFailed));
    }

    public ValueTask DisposeAsync()
    {
        TaskCompletionSource? completion = null;
        Task disposeTask;
        lock (_sync)
        {
            if (_disposeTask is not null)
            {
                return new ValueTask(_disposeTask);
            }

            _disposeStarted = true;
            completion = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            _disposeTask = completion.Task;
            disposeTask = _disposeTask;
        }

        _lifetimeCancellation.Cancel();
        _ = CompleteDisposeAsync(completion);
        return new ValueTask(disposeTask);
    }

    private PlaybackEngineOperationResult OpenOnUiThread(
        PlaybackSessionId sessionId,
        ReadOnlySpan<byte> locatorBytes)
    {
        if (IsDisposeStarted())
        {
            return PlaybackEngineOperationResult.Failed(DomainErrorCode.PlaybackStartFailed);
        }

        string locator = StrictUtf8.GetString(locatorBytes);
        MediaSource source = MediaSource.CreateFromUri(new Uri(locator, UriKind.Absolute));
        PlaybackEngineSnapshot buffering =
            PlaybackEngineSnapshot.Active(sessionId, PlaybackState.Buffering);
        SessionContext? context = null;
        lock (_sync)
        {
            if (!_disposeStarted && _active is null)
            {
                long generation = checked(++_generation);
                context = new SessionContext(sessionId, generation, source);
                _active = context;
                _current = buffering;
                _controls = PlaybackControlSnapshot.Active(
                    sessionId,
                    PlaybackVolume.FromPercent(100),
                    isMuted: false,
                    PlaybackAspectMode.Fit);
            }
        }

        if (context is null)
        {
            source.Dispose();
            return PlaybackEngineOperationResult.Failed(
                IsDisposeStarted()
                    ? DomainErrorCode.OperationCancelled
                    : DomainErrorCode.DomainInvariantViolation);
        }

        try
        {
            AttachSessionHandlers(context);
            _mediaPlayer.AutoPlay = false;
            _mediaPlayer.Volume = 1d;
            _mediaPlayer.IsMuted = false;
            _surface.Stretch = Stretch.Uniform;
            _mediaPlayer.Source = source;
        }
        catch
        {
            ReleaseContextOnUiThread(context, preserveTerminalState: false);
            throw;
        }

        RaiseIfCurrent(buffering);
        return PlaybackEngineOperationResult.Succeeded();
    }

    private PlaybackEngineOperationResult StopOnUiThread(PlaybackSessionId sessionId)
    {
        SessionContext? context;
        lock (_sync)
        {
            context = _active;
            if (context is null)
            {
                if (_current.State != PlaybackState.Closed &&
                    _current.SessionId != sessionId)
                {
                    return PlaybackEngineOperationResult.Failed(
                        DomainErrorCode.DomainInvariantViolation);
                }

                _current = PlaybackEngineSnapshot.Closed();
                _controls = PlaybackControlSnapshot.Idle(
                    _controls.Volume,
                    _controls.IsMuted,
                    _controls.AspectMode);
                return PlaybackEngineOperationResult.Succeeded();
            }
        }

        if (context.SessionId != sessionId)
        {
            return PlaybackEngineOperationResult.Failed(
                DomainErrorCode.DomainInvariantViolation);
        }

        ReleaseContextOnUiThread(context, preserveTerminalState: false);
        return PlaybackEngineOperationResult.Succeeded();
    }

    private async ValueTask<PlaybackEngineOperationResult> ExecuteOnUiThreadAsync(
        PlaybackSessionId sessionId,
        Action operation,
        DomainErrorCode failure,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (IsDisposeStarted())
        {
            return PlaybackEngineOperationResult.Failed(DomainErrorCode.OperationCancelled);
        }

        using CancellationTokenSource operationCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                _lifetimeCancellation.Token);
        CancellationToken operationToken = operationCancellation.Token;
        bool gateEntered = false;
        try
        {
            await _operationGate.WaitAsync(operationToken).ConfigureAwait(false);
            gateEntered = true;
            return await RunOnDispatcherAsync(
                () =>
                {
                    if (!IsActive(sessionId))
                    {
                        return PlaybackEngineOperationResult.Failed(
                            DomainErrorCode.DomainInvariantViolation);
                    }

                    operation();
                    return PlaybackEngineOperationResult.Succeeded();
                },
                operationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested)
        {
            return PlaybackEngineOperationResult.Failed(DomainErrorCode.OperationCancelled);
        }
        catch (Exception exception) when (IsRecoverable(exception))
        {
            return PlaybackEngineOperationResult.Failed(failure);
        }
        finally
        {
            if (gateEntered)
            {
                _operationGate.Release();
            }
        }
    }

    private ValueTask<PlaybackEngineOperationResult> ExecuteControlOnUiThreadAsync(
        PlaybackSessionId sessionId,
        Action operation,
        CancellationToken cancellationToken) =>
        ExecuteOnUiThreadAsync(
            sessionId,
            operation,
            DomainErrorCode.PlaybackControlFailed,
            cancellationToken);

    private void ExecutePlaybackCommandOnUiThread(
        PlaybackSessionId sessionId,
        PlaybackIntent intent,
        Action operation)
    {
        SessionContext context;
        PlaybackIntent previousIntent;
        lock (_sync)
        {
            context = _active ??
                throw new InvalidOperationException("An active playback session is required.");
            if (context.Retired || context.SessionId != sessionId)
            {
                throw new InvalidOperationException("The playback session is no longer active.");
            }

            previousIntent = context.Intent;
            context.Intent = intent;
        }

        try
        {
            _mediaPlayer.AutoPlay = intent == PlaybackIntent.Play;
            operation();
        }
        catch (Exception exception) when (IsRecoverable(exception))
        {
            if (intent == PlaybackIntent.Play)
            {
                try
                {
                    _mediaPlayer.AutoPlay = false;
                }
                catch (Exception rollbackException) when (IsRecoverable(rollbackException))
                {
                    // The original command failure remains authoritative.
                }
            }

            lock (_sync)
            {
                if (ReferenceEquals(_active, context) && !context.Retired &&
                    context.Intent == intent)
                {
                    context.Intent = previousIntent;
                }
            }

            throw;
        }

        PlaybackState? reconciled = MapPlaybackState(
            _mediaPlayer.PlaybackSession.PlaybackState,
            context.Intent);
        if (intent == PlaybackIntent.Play)
        {
            reconciled ??= PlaybackState.Buffering;
        }

        PublishNativeState(context, reconciled);
    }

    private void AttachSessionHandlers(SessionContext context)
    {
        context.SourceOpenHandler = (sender, args) => PostNativeCallback(
            context,
            ReferenceEquals(sender, context.Source) && args.Error is null
                ? NativeCallback.SourceOpened
                : NativeCallback.SourceFailed,
            sender);
        context.MediaOpenedHandler = (_, _) => PostNativeCallback(
            context,
            NativeCallback.MediaOpened,
            source: null);
        context.MediaFailedHandler = (_, args) => PostNativeCallback(
            context,
            NativeCallback.MediaFailed,
            source: null,
            mediaPlayerError: args.Error);
        context.MediaEndedHandler = (sender, _) =>
        {
            if (ReferenceEquals(sender, _mediaPlayer))
            {
                PostNativeCallback(
                    context,
                    NativeCallback.MediaEnded,
                    source: context.Source);
            }
        };
        context.PlaybackStateChangedHandler = (_, _) => PostNativeCallback(
            context,
            NativeCallback.PlaybackStateChanged,
            source: null);
        context.Source.OpenOperationCompleted += context.SourceOpenHandler;
        _mediaPlayer.MediaOpened += context.MediaOpenedHandler;
        _mediaPlayer.MediaFailed += context.MediaFailedHandler;
        _mediaPlayer.MediaEnded += context.MediaEndedHandler;
        _mediaPlayer.PlaybackSession.PlaybackStateChanged +=
            context.PlaybackStateChangedHandler;
    }

    private bool DetachSessionHandlers(SessionContext context)
    {
        bool succeeded = true;
        if (context.SourceOpenHandler is not null)
        {
            try
            {
                context.Source.OpenOperationCompleted -= context.SourceOpenHandler;
                context.SourceOpenHandler = null;
            }
            catch (Exception exception) when (IsRecoverable(exception))
            {
                succeeded = false;
            }
        }

        if (context.MediaOpenedHandler is not null)
        {
            try
            {
                _mediaPlayer.MediaOpened -= context.MediaOpenedHandler;
                context.MediaOpenedHandler = null;
            }
            catch (Exception exception) when (IsRecoverable(exception))
            {
                succeeded = false;
            }
        }

        if (context.MediaFailedHandler is not null)
        {
            try
            {
                _mediaPlayer.MediaFailed -= context.MediaFailedHandler;
                context.MediaFailedHandler = null;
            }
            catch (Exception exception) when (IsRecoverable(exception))
            {
                succeeded = false;
            }
        }

        if (context.MediaEndedHandler is not null)
        {
            try
            {
                _mediaPlayer.MediaEnded -= context.MediaEndedHandler;
                context.MediaEndedHandler = null;
            }
            catch (Exception exception) when (IsRecoverable(exception))
            {
                succeeded = false;
            }
        }

        if (context.PlaybackStateChangedHandler is not null)
        {
            try
            {
                _mediaPlayer.PlaybackSession.PlaybackStateChanged -=
                    context.PlaybackStateChangedHandler;
                context.PlaybackStateChangedHandler = null;
            }
            catch (Exception exception) when (IsRecoverable(exception))
            {
                succeeded = false;
            }
        }

        return succeeded;
    }

    private void PostNativeCallback(
        SessionContext context,
        NativeCallback callback,
        MediaSource? source,
        MediaPlayerError? mediaPlayerError = null)
    {
        _dispatcherQueue.TryEnqueue(() =>
            ProcessNativeCallback(
                context,
                callback,
                source,
                mediaPlayerError));
    }

    private void ProcessNativeCallback(
        SessionContext context,
        NativeCallback callback,
        MediaSource? source,
        MediaPlayerError? mediaPlayerError)
    {
        if (!IsCurrentContext(context, source))
        {
            return;
        }

        if (callback is NativeCallback.SourceFailed or
            NativeCallback.MediaFailed or NativeCallback.MediaEnded)
        {
            PlaybackEngineSnapshot? failed = SetNativeFailure(
                context,
                callback,
                mediaPlayerError);
            try
            {
                ReleaseContextOnUiThread(context, preserveTerminalState: true);
            }
            catch (Exception exception) when (IsRecoverable(exception))
            {
                // Final disposal retries exact teardown.
            }
            finally
            {
                if (failed is not null)
                {
                    NotifyStateChanged(failed);
                }
            }

            return;
        }

        PlaybackState? state;
        try
        {
            state = callback switch
            {
                NativeCallback.MediaOpened or NativeCallback.PlaybackStateChanged =>
                    MapPlaybackState(
                        _mediaPlayer.PlaybackSession.PlaybackState,
                        context.Intent),
                _ => null,
            };
        }
        catch (Exception exception) when (IsRecoverable(exception))
        {
            // Native crash attribution remains unverified. A guarded read
            // failure stays bounded by the existing playback watchdog.
            return;
        }

        PublishNativeState(context, state);
    }

    private void FaultWatchdog_Expired(
        object? sender,
        PlaybackFaultWatchdogExpiredEventArgs eventArgs)
    {
        ArgumentNullException.ThrowIfNull(eventArgs);
        if (!ReferenceEquals(sender, _faultWatchdog))
        {
            return;
        }

        SessionContext context;
        lock (_sync)
        {
            if (_disposeStarted ||
                _active is null ||
                _active.Retired ||
                _active.Generation != _generation ||
                _active.SessionId != eventArgs.SessionId ||
                _current.SessionId != eventArgs.SessionId ||
                _current.State is PlaybackState.Closed or PlaybackState.Failed)
            {
                return;
            }

            context = _active;
        }

        try
        {
            if (!_dispatcherQueue.TryEnqueue(() =>
                    ProcessFaultWatchdogExpiration(context, eventArgs)))
            {
                // Dispatcher shutdown owns final native teardown; no terminal state is published.
                return;
            }
        }
        catch (Exception exception) when (IsRecoverable(exception))
        {
            // Dispatcher shutdown owns final native teardown; no terminal state is published.
        }
    }

    private void ProcessFaultWatchdogExpiration(
        SessionContext context,
        PlaybackFaultWatchdogExpiredEventArgs eventArgs)
    {
        if (context.SessionId != eventArgs.SessionId ||
            !IsCurrentContext(context, source: null))
        {
            return;
        }

        PlaybackEngineSnapshot? failed = SetWatchdogFailure(
            context,
            eventArgs.FailureKind);
        if (failed is null)
        {
            return;
        }

        try
        {
            ReleaseContextOnUiThread(context, preserveTerminalState: true);
        }
        catch (Exception exception) when (IsRecoverable(exception))
        {
            // Final disposal retries exact teardown.
        }
        finally
        {
            NotifyStateChanged(failed);
        }
    }

    private bool IsCurrentContext(SessionContext context, MediaSource? source)
    {
        lock (_sync)
        {
            return !_disposeStarted &&
                ReferenceEquals(_active, context) &&
                !context.Retired &&
                context.Generation == _generation &&
                context.SessionId == _current.SessionId &&
                (source is null || ReferenceEquals(source, context.Source)) &&
                ReferenceEquals(_mediaPlayer.Source, context.Source);
        }
    }

    private void ReleaseContextOnUiThread(
        SessionContext context,
        bool preserveTerminalState)
    {
        bool cleanupFailed = false;
        lock (_sync)
        {
            if (!ReferenceEquals(_active, context))
            {
                return;
            }
        }

        _faultWatchdog.Cancel(context.SessionId);

        lock (_sync)
        {
            if (!ReferenceEquals(_active, context))
            {
                return;
            }

            if (!context.Retired)
            {
                context.Retired = true;
                _generation = checked(_generation + 1);
            }
        }

        bool handlersDetached = DetachSessionHandlers(context);
        cleanupFailed = !handlersDetached;
        try
        {
            _mediaPlayer.AutoPlay = false;
        }
        catch (Exception exception) when (IsRecoverable(exception))
        {
            cleanupFailed = true;
        }

        try
        {
            if (_mediaPlayer.PlaybackSession.CanPause)
            {
                _mediaPlayer.Pause();
            }
        }
        catch (Exception exception) when (IsRecoverable(exception))
        {
            // Source detachment remains authoritative even if Pause is unavailable.
        }

        try
        {
            if (ReferenceEquals(_mediaPlayer.Source, context.Source))
            {
                _mediaPlayer.Source = null;
            }
        }
        catch (Exception exception) when (IsRecoverable(exception))
        {
            cleanupFailed = true;
        }

        bool sourceDetached;
        try
        {
            sourceDetached = _mediaPlayer.Source is null;
        }
        catch (Exception exception) when (IsRecoverable(exception))
        {
            sourceDetached = false;
            cleanupFailed = true;
        }

        if (sourceDetached && !context.SourceDisposed)
        {
            try
            {
                context.Source.Dispose();
                context.SourceDisposed = true;
            }
            catch (Exception exception) when (IsRecoverable(exception))
            {
                cleanupFailed = true;
            }
        }

        if (context.SourceDisposed && handlersDetached)
        {
            lock (_sync)
            {
                if (ReferenceEquals(_active, context))
                {
                    _active = null;
                    if (!preserveTerminalState)
                    {
                        _current = PlaybackEngineSnapshot.Closed();
                        _controls = PlaybackControlSnapshot.Idle(
                            _controls.Volume,
                            _controls.IsMuted,
                            _controls.AspectMode);
                    }
                }
            }
        }

        if (!sourceDetached || !context.SourceDisposed || cleanupFailed)
        {
            throw new InvalidOperationException("The native playback source could not be released safely.");
        }
    }

    private void PublishNativeState(SessionContext context, PlaybackState? state)
    {
        if (!state.HasValue)
        {
            return;
        }

        PlaybackEngineSnapshot? changed = null;
        lock (_sync)
        {
            if (!ReferenceEquals(_active, context) || context.Retired ||
                _current.State is PlaybackState.Closed or PlaybackState.Failed)
            {
                return;
            }

            if (state.Value == PlaybackState.Playing)
            {
                context.HasReachedPlayableState = true;
            }

            if (!CanTransition(_current.State, state.Value))
            {
                return;
            }

            changed = PlaybackEngineSnapshot.Active(context.SessionId, state.Value);
            _current = changed;
        }

        NotifyStateChanged(changed);
    }

    private PlaybackEngineSnapshot? SetNativeFailure(
        SessionContext context,
        NativeCallback callback,
        MediaPlayerError? mediaPlayerError)
    {
        PlaybackEngineSnapshot? failed = null;
        lock (_sync)
        {
            if (_disposeStarted ||
                !ReferenceEquals(_active, context) ||
                context.Retired ||
                context.Generation != _generation ||
                context.SessionId != _current.SessionId ||
                _current.State is PlaybackState.Closed or PlaybackState.Failed)
            {
                return null;
            }

            bool isActiveMediaFailure =
                (callback is NativeCallback.MediaFailed or NativeCallback.MediaEnded) &&
                context.HasReachedPlayableState &&
                _current.State is PlaybackState.Buffering or
                    PlaybackState.Playing or PlaybackState.Paused;
            DomainErrorCode phaseFallback = isActiveMediaFailure
                ? DomainErrorCode.StreamInterrupted
                : DomainErrorCode.PlaybackStartFailed;
            DomainErrorCode error = callback == NativeCallback.MediaFailed
                ? mediaPlayerError switch
                {
                    MediaPlayerError.NetworkError => DomainErrorCode.PlaybackNetworkFailed,
                    MediaPlayerError.SourceNotSupported =>
                        DomainErrorCode.PlaybackSourceUnsupported,
                    MediaPlayerError.DecodingError =>
                        DomainErrorCode.PlaybackDecodingFailed,
                    _ => phaseFallback,
                }
                : phaseFallback;
            failed = PlaybackEngineSnapshot.Failed(
                context.SessionId,
                DomainError.Create(error));
            _current = failed;
        }

        return failed;
    }

    private PlaybackEngineSnapshot? SetWatchdogFailure(
        SessionContext context,
        PlaybackFaultWatchdogFailureKind failureKind)
    {
        DomainErrorCode error = failureKind switch
        {
            PlaybackFaultWatchdogFailureKind.StartupTimeout =>
                DomainErrorCode.PlaybackStartFailed,
            PlaybackFaultWatchdogFailureKind.RebufferTimeout =>
                DomainErrorCode.StreamInterrupted,
            PlaybackFaultWatchdogFailureKind.SchedulerFailure =>
                DomainErrorCode.DomainInvariantViolation,
            _ => DomainErrorCode.DomainInvariantViolation,
        };

        PlaybackEngineSnapshot? failed = null;
        lock (_sync)
        {
            if (_disposeStarted ||
                !ReferenceEquals(_active, context) ||
                context.Retired ||
                context.Generation != _generation ||
                context.SessionId != _current.SessionId ||
                _current.State is PlaybackState.Closed or PlaybackState.Failed)
            {
                return null;
            }

            failed = PlaybackEngineSnapshot.Failed(
                context.SessionId,
                DomainError.Create(error));
            _current = failed;
        }

        return failed;
    }

    private void RaiseIfCurrent(PlaybackEngineSnapshot snapshot)
    {
        lock (_sync)
        {
            if (_current != snapshot)
            {
                return;
            }
        }

        NotifyStateChanged(snapshot);
    }

    private void NotifyStateChanged(PlaybackEngineSnapshot snapshot)
    {
        _faultWatchdog.Observe(snapshot);
        EventHandler<PlaybackEngineStateChangedEventArgs>? handlers = StateChanged;
        if (handlers is null)
        {
            return;
        }

        var eventArgs = new PlaybackEngineStateChangedEventArgs(snapshot);
        foreach (EventHandler<PlaybackEngineStateChangedEventArgs> handler in
            handlers.GetInvocationList().Cast<EventHandler<PlaybackEngineStateChangedEventArgs>>())
        {
            try
            {
                handler(this, eventArgs);
            }
            catch (Exception exception) when (IsRecoverable(exception))
            {
                // Observer failures cannot interrupt native resource ownership.
            }
        }
    }

    private void UpdateControls(
        PlaybackSessionId sessionId,
        PlaybackVolume? volume = null,
        bool? isMuted = null,
        PlaybackAspectMode? aspectMode = null)
    {
        lock (_sync)
        {
            _controls = PlaybackControlSnapshot.Active(
                sessionId,
                volume ?? _controls.Volume,
                isMuted ?? _controls.IsMuted,
                aspectMode ?? _controls.AspectMode);
        }
    }

    private bool IsActive(PlaybackSessionId sessionId)
    {
        lock (_sync)
        {
            return !_disposeStarted && _active?.SessionId == sessionId &&
                !_active.Retired &&
                _current.State is not PlaybackState.Closed and not PlaybackState.Failed;
        }
    }

    private bool IsDisposeStarted()
    {
        lock (_sync)
        {
            return _disposeStarted;
        }
    }

    private async Task CompleteDisposeAsync(TaskCompletionSource completion)
    {
        bool gateEntered = false;
        try
        {
            await _operationGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            gateEntered = true;
            await RunOnDispatcherAsync(
                DisposeOnUiThread,
                CancellationToken.None).ConfigureAwait(false);
            completion.TrySetResult();
        }
        catch (Exception exception) when (IsRecoverable(exception))
        {
            completion.TrySetException(
                new InvalidOperationException(
                    "The native playback engine could not be disposed safely."));
        }
        catch (Exception exception)
        {
            completion.TrySetException(exception);
        }
        finally
        {
            if (gateEntered)
            {
                _operationGate.Release();
            }
        }
    }

    private bool DisposeOnUiThread()
    {
        bool cleanupFailed = false;
        SessionContext? context;
        lock (_sync)
        {
            context = _active;
        }

        if (context is not null)
        {
            try
            {
                ReleaseContextOnUiThread(context, preserveTerminalState: true);
            }
            catch (Exception exception) when (IsRecoverable(exception))
            {
                cleanupFailed = true;
            }
        }

        try
        {
            _mediaPlayer.AutoPlay = false;
        }
        catch (Exception exception) when (IsRecoverable(exception))
        {
            cleanupFailed = true;
        }

        try
        {
            _surface.SetMediaPlayer(null);
        }
        catch (Exception exception) when (IsRecoverable(exception))
        {
            cleanupFailed = true;
        }

        try
        {
            if (_mediaPlayer.Source is not null)
            {
                _mediaPlayer.Source = null;
            }
        }
        catch (Exception exception) when (IsRecoverable(exception))
        {
            cleanupFailed = true;
        }

        bool sourceDetached;
        try
        {
            sourceDetached = _mediaPlayer.Source is null;
        }
        catch (Exception exception) when (IsRecoverable(exception))
        {
            sourceDetached = false;
            cleanupFailed = true;
        }

        if (context is not null && !context.SourceDisposed && sourceDetached)
        {
            try
            {
                context.Source.Dispose();
                context.SourceDisposed = true;
            }
            catch (Exception exception) when (IsRecoverable(exception))
            {
                cleanupFailed = true;
            }
        }

        bool playerDisposed = false;
        try
        {
            _mediaPlayer.Dispose();
            playerDisposed = true;
        }
        catch (Exception exception) when (IsRecoverable(exception))
        {
            cleanupFailed = true;
        }

        if (context is not null && !context.SourceDisposed &&
            (sourceDetached || playerDisposed))
        {
            try
            {
                context.Source.Dispose();
                context.SourceDisposed = true;
            }
            catch (Exception exception) when (IsRecoverable(exception))
            {
                cleanupFailed = true;
            }
        }

        if (context is not null && !context.SourceDisposed)
        {
            cleanupFailed = true;
        }

        lock (_sync)
        {
            _active = null;
            _current = PlaybackEngineSnapshot.Closed();
            _controls = PlaybackControlSnapshot.Idle(
                _controls.Volume,
                _controls.IsMuted,
                _controls.AspectMode);
        }

        _faultWatchdog.Expired -= FaultWatchdog_Expired;
        _faultWatchdog.Dispose();

        if (cleanupFailed)
        {
            throw new InvalidOperationException("The native playback engine could not be disposed safely.");
        }

        return true;
    }

    private async Task<T> RunOnDispatcherAsync<T>(
        Func<T> operation,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_dispatcherQueue.HasThreadAccess)
        {
            return operation();
        }

        var workItem = new DispatcherWorkItem<T>(operation, cancellationToken);
        using CancellationTokenRegistration registration =
            cancellationToken.Register(workItem.CancelBeforeStart);
        if (!_dispatcherQueue.TryEnqueue(workItem.Execute))
        {
            throw new InvalidOperationException("The playback dispatcher is unavailable.");
        }

        return await workItem.Task.ConfigureAwait(false);
    }

    private static PlaybackState? MapPlaybackState(
        MediaPlaybackState state,
        PlaybackIntent intent) => state switch
    {
        MediaPlaybackState.Opening => PlaybackState.Buffering,
        MediaPlaybackState.Buffering => PlaybackState.Buffering,
        MediaPlaybackState.Playing => PlaybackState.Playing,
        // A source can report Paused before the first Play advances; only an
        // explicit pause command is a user-visible paused state.
        MediaPlaybackState.Paused when intent == PlaybackIntent.Pause => PlaybackState.Paused,
        MediaPlaybackState.Paused => PlaybackState.Buffering,
        _ => null,
    };

    private static bool CanTransition(PlaybackState current, PlaybackState next)
    {
        if (current == next)
        {
            return false;
        }

        return current switch
        {
            PlaybackState.Opening => next is PlaybackState.Buffering or PlaybackState.Playing or PlaybackState.Paused,
            PlaybackState.Buffering => next is PlaybackState.Playing or PlaybackState.Paused,
            PlaybackState.Playing => next is PlaybackState.Buffering or PlaybackState.Paused,
            PlaybackState.Paused => next is PlaybackState.Buffering or PlaybackState.Playing,
            _ => false,
        };
    }

    private static DomainErrorCode MapResolutionFailure(
        PlaybackSourceResolutionFailure failure) => failure switch
        {
            PlaybackSourceResolutionFailure.StorageUnavailable => DomainErrorCode.StorageUnavailable,
            _ => DomainErrorCode.PlaybackStartFailed,
        };

    private static bool IsRecoverable(Exception exception) =>
        exception is not OutOfMemoryException and
        not StackOverflowException and
        not AccessViolationException;

    private enum NativeCallback
    {
        SourceOpened,
        SourceFailed,
        MediaOpened,
        MediaFailed,
        MediaEnded,
        PlaybackStateChanged,
    }

    private enum PlaybackIntent
    {
        Play,
        Pause,
    }

    private sealed class SessionContext(
        PlaybackSessionId sessionId,
        long generation,
        MediaSource source)
    {
        internal PlaybackSessionId SessionId { get; } = sessionId;
        internal long Generation { get; } = generation;
        internal MediaSource Source { get; } = source;
        internal TypedEventHandler<MediaSource, MediaSourceOpenOperationCompletedEventArgs>?
            SourceOpenHandler { get; set; }
        internal TypedEventHandler<MediaPlayer, object>? MediaOpenedHandler { get; set; }
        internal TypedEventHandler<MediaPlayer, MediaPlayerFailedEventArgs>?
            MediaFailedHandler { get; set; }
        internal TypedEventHandler<MediaPlayer, object>? MediaEndedHandler { get; set; }
        internal TypedEventHandler<MediaPlaybackSession, object>?
            PlaybackStateChangedHandler { get; set; }
        internal PlaybackIntent Intent { get; set; } = PlaybackIntent.Play;
        internal bool HasReachedPlayableState { get; set; }
        internal bool Retired { get; set; }
        internal bool SourceDisposed { get; set; }
    }

    private sealed class DispatcherWorkItem<T>(
        Func<T> operation,
        CancellationToken cancellationToken)
    {
        private readonly object _sync = new();
        private readonly TaskCompletionSource<T> _completion = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private bool _started;

        internal Task<T> Task => _completion.Task;

        internal void CancelBeforeStart()
        {
            lock (_sync)
            {
                if (!_started)
                {
                    _completion.TrySetCanceled(cancellationToken);
                }
            }
        }

        internal void Execute()
        {
            lock (_sync)
            {
                if (_completion.Task.IsCompleted)
                {
                    return;
                }

                if (cancellationToken.IsCancellationRequested)
                {
                    _completion.TrySetCanceled(cancellationToken);
                    return;
                }

                _started = true;
            }

            try
            {
                _completion.TrySetResult(operation());
            }
            catch (Exception exception)
            {
                _completion.TrySetException(exception);
            }
        }
    }
}
