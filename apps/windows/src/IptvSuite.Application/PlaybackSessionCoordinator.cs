using IptvSuite.Domain;

namespace IptvSuite.Application;

public sealed class PlaybackSessionCoordinator : IAsyncDisposable
{
    private readonly IPlaybackEngine _engine;
    private readonly PlaybackReconnectPolicy? _reconnectPolicy;
    private readonly PlaybackReconnectOrchestrator? _reconnectOrchestrator;
    private readonly SemaphoreSlim _engineGate = new(1, 1);
    private readonly object _sync = new();
    private readonly Dictionary<SourceId, SourceRetirementState> _sourceRetirements = [];
    private SessionLifetime? _currentLifetime;
    private PlaybackSessionId _engineSession;
    private PlaybackSessionId _engineLogicalSession;
    private SourceId _engineSource;
    private PlaybackSelection? _currentSelection;
    private PlaybackSessionSnapshot _current = PlaybackSessionSnapshot.Closed();
    private PlaybackVolume _volume = PlaybackVolume.FromPercent(100);
    private bool _isMuted;
    private PlaybackAspectMode _aspectMode = PlaybackAspectMode.Fit;
    private PlaybackControlSnapshot _currentControls = PlaybackControlSnapshot.Idle(
        PlaybackVolume.FromPercent(100),
        isMuted: false,
        PlaybackAspectMode.Fit);
    private PlaybackTrackSnapshot? _currentTracks;
    private ReconnectContext? _reconnectContext;
    private Task? _disposeTask;
    private long _generation;
    private long _reconnectSequence;
    private long _retirementSequence;
    private long _sessionSequence;
    private bool _disposed;

    public PlaybackSessionCoordinator(IPlaybackEngine engine)
    {
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        _engine.StateChanged += OnEngineStateChanged;
    }

    public PlaybackSessionCoordinator(
        IPlaybackEngine engine,
        PlaybackReconnectPolicy reconnectPolicy,
        TimeProvider timeProvider,
        PlaybackReconnectJitterSource jitterSource)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(reconnectPolicy);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(jitterSource);
        _engine = engine;
        _reconnectPolicy = reconnectPolicy;
        _reconnectOrchestrator = new PlaybackReconnectOrchestrator(
            reconnectPolicy,
            timeProvider,
            jitterSource,
            ExecuteReconnectAttemptAsync);
        _engine.StateChanged += OnEngineStateChanged;
        _reconnectOrchestrator.SnapshotChanged += OnReconnectSnapshotChanged;
    }

    public event EventHandler<PlaybackSessionStateChangedEventArgs>? StateChanged;

    public PlaybackSessionSnapshot Current
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
                return _currentControls;
            }
        }
    }

    public PlaybackTrackSnapshot? CurrentTracks
    {
        get
        {
            lock (_sync)
            {
                return _currentTracks;
            }
        }
    }

    public bool CanRetryReconnect
    {
        get
        {
            lock (_sync)
            {
                return _reconnectContext is { } context &&
                    CanStartManualReconnectLocked(context);
            }
        }
    }

    public async ValueTask<PlaybackSessionSnapshot?> StartAsync(
        SourceId sourceId,
        ChannelId channelId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var selection = new PlaybackSelection(sourceId, channelId);
        var lifetime = new SessionLifetime();
        SessionOperationCancellation request = lifetime.CreateOperation(cancellationToken);
        SessionLifetime? previousLifetime;
        PlaybackSessionSnapshot opening;
        PlaybackControlSnapshot desiredControls;
        PlaybackSessionId sessionId;
        long generation;
        PlaybackReconnectCorrelationId? reconnectToCancel = null;

        try
        {
            lock (_sync)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (_sourceRetirements.ContainsKey(sourceId))
                {
                    request.Dispose();
                    lifetime.Retire();
                    return null;
                }

                sessionId = NextSessionId();
                generation = checked(++_generation);
                reconnectToCancel = InvalidateReconnectUnderLock();
                previousLifetime = _currentLifetime;
                _currentLifetime = lifetime;
                _currentSelection = selection;
                opening = PlaybackSessionSnapshot.Active(
                    sessionId,
                    selection,
                    PlaybackState.Opening);
                _current = opening;
                _currentControls = PlaybackControlSnapshot.Active(
                    sessionId,
                    _volume,
                    _isMuted,
                    _aspectMode);
                desiredControls = _currentControls;
                _currentTracks = null;
            }
        }
        catch
        {
            request.Dispose();
            lifetime.Retire();
            throw;
        }

        CancelReconnectSafely(reconnectToCancel);
        previousLifetime?.Retire();
        RaiseStateChanged(opening);

        try
        {
            await _engineGate.WaitAsync(request.Token).ConfigureAwait(false);
            try
            {
                if (!IsCurrent(generation, sessionId))
                {
                    return null;
                }

                if (!_engineSession.IsEmpty)
                {
                    PlaybackEngineOperationResult stopped = await StopEngineSessionUnderGateAsync(
                        _engineSession).ConfigureAwait(false);
                    if (!stopped.IsSuccess)
                    {
                        return SetFailureIfCurrent(
                            generation,
                            sessionId,
                            selection,
                            stopped.Error!);
                    }
                }

                request.Token.ThrowIfCancellationRequested();
                if (!IsCurrent(generation, sessionId))
                {
                    return null;
                }

                lock (_sync)
                {
                    if (_disposed || generation != _generation || _current.SessionId != sessionId)
                    {
                        return null;
                    }

                    _engineSession = sessionId;
                    _engineLogicalSession = sessionId;
                    _engineSource = sourceId;
                }
                PlaybackEngineOperationResult opened = await InvokeEngineOperationAsync(
                    token => _engine.OpenAsync(sessionId, selection, token),
                    DomainErrorCode.PlaybackStartFailed,
                    request.Token).ConfigureAwait(false);
                request.Token.ThrowIfCancellationRequested();
                if (!opened.IsSuccess)
                {
                    await StopEngineSessionUnderGateAsync(sessionId).ConfigureAwait(false);
                    return SetFailureIfCurrent(
                        generation,
                        sessionId,
                        selection,
                        opened.Error!);
                }

                if (!CanContinueStart(generation, sessionId))
                {
                    PlaybackSessionSnapshot? terminal = GetCurrentIfCurrent(generation, sessionId);
                    await StopEngineSessionUnderGateAsync(sessionId).ConfigureAwait(false);
                    return terminal;
                }

                PlaybackEngineOperationResult controlsApplied =
                    await ApplyDesiredControlsUnderGateAsync(
                        generation,
                        sessionId,
                        desiredControls,
                        request.Token).ConfigureAwait(false);
                request.Token.ThrowIfCancellationRequested();
                if (!controlsApplied.IsSuccess)
                {
                    await StopEngineSessionUnderGateAsync(sessionId).ConfigureAwait(false);
                    PlaybackSessionSnapshot? terminal = GetCurrentIfCurrent(generation, sessionId);
                    return terminal?.State == PlaybackState.Failed
                        ? terminal
                        : SetFailureIfCurrent(
                            generation,
                            sessionId,
                            selection,
                            controlsApplied.Error!);
                }

                if (!CanContinueStart(generation, sessionId))
                {
                    PlaybackSessionSnapshot? terminal = GetCurrentIfCurrent(generation, sessionId);
                    await StopEngineSessionUnderGateAsync(sessionId).ConfigureAwait(false);
                    return terminal;
                }

                PlaybackEngineOperationResult played = await InvokeEngineOperationAsync(
                    token => _engine.PlayAsync(sessionId, token),
                    DomainErrorCode.PlaybackStartFailed,
                    request.Token).ConfigureAwait(false);
                request.Token.ThrowIfCancellationRequested();
                if (!played.IsSuccess)
                {
                    await StopEngineSessionUnderGateAsync(sessionId).ConfigureAwait(false);
                    return SetFailureIfCurrent(
                        generation,
                        sessionId,
                        selection,
                        played.Error!);
                }

                if (!CanContinueStart(generation, sessionId))
                {
                    PlaybackSessionSnapshot? terminal = GetCurrentIfCurrent(generation, sessionId);
                    await StopEngineSessionUnderGateAsync(sessionId).ConfigureAwait(false);
                    return terminal;
                }

                return GetCurrentIfCurrent(generation, sessionId);
            }
            finally
            {
                _engineGate.Release();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await StopSessionAsync(sessionId, requireCurrentSession: true).ConfigureAwait(false);
            throw new OperationCanceledException(cancellationToken);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
        finally
        {
            request.Dispose();
        }
    }

    public ValueTask<PlaybackEngineOperationResult> PlayAsync(
        CancellationToken cancellationToken = default) =>
        ExecuteCurrentCommandAsync(
            (sessionId, token) => _engine.PlayAsync(sessionId, token),
            cancellationToken);

    public ValueTask<PlaybackEngineOperationResult> PauseAsync(
        CancellationToken cancellationToken = default) =>
        ExecuteCurrentCommandAsync(
            (sessionId, token) => _engine.PauseAsync(sessionId, token),
            cancellationToken);

    public ValueTask<PlaybackEngineOperationResult> SetVolumeAsync(
        PlaybackSessionId sessionId,
        PlaybackVolume volume,
        CancellationToken cancellationToken = default) =>
        ExecuteCurrentControlCommandAsync(
            sessionId,
            (currentSession, token) => _engine.SetVolumeAsync(currentSession, volume, token),
            () =>
            {
                _volume = volume;
                _currentControls = PlaybackControlSnapshot.Active(
                    sessionId,
                    _volume,
                    _isMuted,
                    _aspectMode);
            },
            canExecute: null,
            cancellationToken);

    public ValueTask<PlaybackEngineOperationResult> SetMutedAsync(
        PlaybackSessionId sessionId,
        bool isMuted,
        CancellationToken cancellationToken = default) =>
        ExecuteCurrentControlCommandAsync(
            sessionId,
            (currentSession, token) => _engine.SetMutedAsync(currentSession, isMuted, token),
            () =>
            {
                _isMuted = isMuted;
                _currentControls = PlaybackControlSnapshot.Active(
                    sessionId,
                    _volume,
                    _isMuted,
                    _aspectMode);
            },
            canExecute: null,
            cancellationToken);

    public ValueTask<PlaybackEngineOperationResult> SetAspectModeAsync(
        PlaybackSessionId sessionId,
        PlaybackAspectMode aspectMode,
        CancellationToken cancellationToken = default)
    {
        if (!Enum.IsDefined(aspectMode))
        {
            throw new ArgumentOutOfRangeException(
                nameof(aspectMode),
                aspectMode,
                "Unknown playback aspect mode.");
        }

        return ExecuteCurrentControlCommandAsync(
            sessionId,
            (currentSession, token) => _engine.SetAspectModeAsync(currentSession, aspectMode, token),
            () =>
            {
                _aspectMode = aspectMode;
                _currentControls = PlaybackControlSnapshot.Active(
                    sessionId,
                    _volume,
                    _isMuted,
                    _aspectMode);
            },
            canExecute: null,
            cancellationToken);
    }

    public ValueTask<DomainResult<PlaybackTrackSnapshot>> GetTracksAsync(
        PlaybackSessionId sessionId,
        CancellationToken cancellationToken = default) =>
        ExecuteCurrentTrackQueryAsync(sessionId, cancellationToken);

    public ValueTask<PlaybackEngineOperationResult> SelectTrackAsync(
        PlaybackTrackId trackId,
        CancellationToken cancellationToken = default)
    {
        if (trackId.IsEmpty)
        {
            throw new ArgumentException(
                "A session-bound playback track identifier is required.",
                nameof(trackId));
        }

        return ExecuteCurrentControlCommandAsync(
            trackId.SessionId,
            (physicalSession, token) => _engine.SelectTrackAsync(
                physicalSession,
                PlaybackTrackId.Create(
                    physicalSession,
                    trackId.Kind,
                    trackId.Ordinal),
                token),
            () => UpdateSelectedTrackUnderLock(trackId),
            () => _currentTracks?.CanSelect(trackId) == true,
            cancellationToken);
    }

    public ValueTask<PlaybackEngineOperationResult> StopAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
        }

        return new ValueTask<PlaybackEngineOperationResult>(
            StopSessionAsync(expectedSession: null, requireCurrentSession: false));
    }

    public ValueTask<PlaybackEngineOperationResult> RetryReconnectAsync()
    {
        ReconnectContext? context;
        PlaybackReconnectOrchestrator? orchestrator;
        SessionLifetime? lifetime;
        long generation;

        lock (_sync)
        {
            context = _reconnectContext;
            if (context is not null && IsManualReconnectInFlightLocked(context))
            {
                return ValueTask.FromResult(PlaybackEngineOperationResult.Succeeded());
            }

            if (context is null || !CanStartManualReconnectLocked(context))
            {
                return ValueTask.FromResult(PlaybackEngineOperationResult.Failed(
                    DomainErrorCode.OperationCancelled));
            }

            orchestrator = _reconnectOrchestrator;
            lifetime = _currentLifetime;
            generation = _generation;
            context.ManualRetryStarting = true;
        }

        try
        {
            _ = orchestrator!.RetryNowAsync(context.CorrelationId);
        }
        catch (ObjectDisposedException)
        {
            ResetManualRetryStarting(context);
            return ValueTask.FromResult(PlaybackEngineOperationResult.Failed(
                DomainErrorCode.OperationCancelled));
        }
        catch (InvalidOperationException)
        {
            ResetManualRetryStarting(context);
            return ValueTask.FromResult(PlaybackEngineOperationResult.Failed(
                DomainErrorCode.OperationCancelled));
        }
        catch (Exception)
        {
            ResetManualRetryStarting(context);
            return ValueTask.FromResult(PlaybackEngineOperationResult.Failed(
                DomainErrorCode.DomainInvariantViolation));
        }

        bool accepted;
        lock (_sync)
        {
            accepted = !_disposed &&
                generation == _generation &&
                ReferenceEquals(_currentLifetime, lifetime) &&
                _current.SessionId == context.SessionId &&
                ReferenceEquals(_currentSelection, context.Selection) &&
                !_sourceRetirements.ContainsKey(context.Selection.SourceId);

            if (ReferenceEquals(_reconnectContext, context))
            {
                context.ManualRetryStarting = false;
                context.ManualRetryActive = accepted &&
                    _current.State == PlaybackState.Reconnecting &&
                    _current.Reconnect?.CorrelationId == context.CorrelationId;
            }
        }

        if (!accepted)
        {
            CancelReconnectSafely(context.CorrelationId);
        }

        return ValueTask.FromResult(accepted
            ? PlaybackEngineOperationResult.Succeeded()
            : PlaybackEngineOperationResult.Failed(DomainErrorCode.OperationCancelled));
    }

    internal SourceRetirementLease AcquireSourceRetirement(SourceId sourceId)
    {
        if (sourceId.IsEmpty)
        {
            throw new ArgumentException(
                "A playback source identifier is required.",
                nameof(sourceId));
        }

        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_sourceRetirements.TryGetValue(sourceId, out SourceRetirementState? existing) &&
                existing.IsPermanent)
            {
                return new SourceRetirementLease(this, sourceId, reservationId: 0);
            }

            SourceRetirementState retirement = existing ?? new SourceRetirementState();
            long reservationId = checked(++_retirementSequence);
            retirement.Reservations.Add(reservationId);
            _sourceRetirements[sourceId] = retirement;
            return new SourceRetirementLease(this, sourceId, reservationId);
        }
    }

    /// <summary>
    /// Atomically retires a source from playback admission and drains its exact current or
    /// in-flight physical session.
    /// </summary>
    /// <remarks>
    /// Retirement is idempotent and permanent for this coordinator lifetime. Once this method
    /// observes the source, later starts for that source are rejected even while its previous
    /// session is still draining. A different source may replace the retiring session.
    /// </remarks>
    public ValueTask<PlaybackEngineOperationResult> ReleaseSourceAsync(
        SourceId sourceId,
        CancellationToken cancellationToken = default)
    {
        if (sourceId.IsEmpty)
        {
            throw new ArgumentException(
                "A playback source identifier is required.",
                nameof(sourceId));
        }

        cancellationToken.ThrowIfCancellationRequested();
        PlaybackSessionId sessionId = default;
        bool releaseCurrent;
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            CommitSourceRetirementUnderLock(sourceId, reservationId: 0);
            releaseCurrent = _currentSelection?.SourceId == sourceId &&
                _current.State is (
                    PlaybackState.Opening or
                    PlaybackState.Buffering or
                    PlaybackState.Playing or
                    PlaybackState.Paused or
                    PlaybackState.Reconnecting or
                    PlaybackState.Stopping or
                    PlaybackState.Failed);
            if (releaseCurrent)
            {
                sessionId = _current.SessionId;
            }
        }

        return new ValueTask<PlaybackEngineOperationResult>(
            ReleaseRetiredSourceAsync(sourceId, releaseCurrent, sessionId));
    }

    public ValueTask DisposeAsync()
    {
        TaskCompletionSource? completion = null;
        SessionLifetime? lifetime = null;
        PlaybackReconnectCorrelationId? reconnectToCancel = null;
        Task disposeTask;

        lock (_sync)
        {
            if (_disposeTask is not null)
            {
                return new ValueTask(_disposeTask);
            }

            completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _disposeTask = completion.Task;
            disposeTask = _disposeTask;
            _disposed = true;
            checked
            {
                _generation++;
            }

            lifetime = _currentLifetime;
            _currentLifetime = null;
            reconnectToCancel = InvalidateReconnectUnderLock();
        }

        lifetime?.Retire();
        CancelReconnectSafely(reconnectToCancel);
        _ = CompleteDisposeAsync(completion);
        return new ValueTask(disposeTask);
    }

    private async ValueTask<PlaybackEngineOperationResult> ExecuteCurrentCommandAsync(
        Func<PlaybackSessionId, CancellationToken, ValueTask<PlaybackEngineOperationResult>> command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        cancellationToken.ThrowIfCancellationRequested();
        PlaybackSessionId sessionId;
        SessionOperationCancellation request;
        long generation;

        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_current.State is PlaybackState.Closed or PlaybackState.Reconnecting or
                    PlaybackState.Stopping or PlaybackState.Failed ||
                _currentLifetime is null)
            {
                return PlaybackEngineOperationResult.Failed(
                    DomainErrorCode.DomainInvariantViolation);
            }

            sessionId = _current.SessionId;
            generation = _generation;
            request = _currentLifetime.CreateOperation(cancellationToken);
        }

        bool gateEntered = false;
        try
        {
            await _engineGate.WaitAsync(request.Token).ConfigureAwait(false);
            gateEntered = true;
            request.Token.ThrowIfCancellationRequested();
            PlaybackSessionId physicalSession;
            DomainError? terminalBeforeDispatch;
            bool invalidBeforeDispatch;
            bool exactBindingBeforeDispatch;
            lock (_sync)
            {
                if (_disposed || generation != _generation || _current.SessionId != sessionId)
                {
                    return PlaybackEngineOperationResult.Failed(
                        DomainErrorCode.OperationCancelled);
                }

                physicalSession = _engineSession;
                exactBindingBeforeDispatch = !physicalSession.IsEmpty &&
                    _engineLogicalSession == sessionId;
                terminalBeforeDispatch = _current.State == PlaybackState.Failed
                    ? _current.Error
                    : null;
                invalidBeforeDispatch = terminalBeforeDispatch is null &&
                    (_current.State is PlaybackState.Closed or PlaybackState.Reconnecting or
                        PlaybackState.Stopping ||
                        _currentLifetime is null);
            }

            if (terminalBeforeDispatch is not null)
            {
                if (exactBindingBeforeDispatch)
                {
                    await StopEngineSessionUnderGateAsync(physicalSession).ConfigureAwait(false);
                }

                return PlaybackEngineOperationResult.Failed(terminalBeforeDispatch);
            }

            if (invalidBeforeDispatch)
            {
                return PlaybackEngineOperationResult.Failed(
                    DomainErrorCode.OperationCancelled);
            }

            if (!exactBindingBeforeDispatch)
            {
                return PlaybackEngineOperationResult.Failed(DomainErrorCode.OperationCancelled);
            }

            PlaybackEngineOperationResult result = await InvokeEngineOperationAsync(
                token => command(physicalSession, token),
                DomainErrorCode.StreamInterrupted,
                request.Token).ConfigureAwait(false);
            request.Token.ThrowIfCancellationRequested();
            DomainError? terminalAfterDispatch;
            bool invalidAfterDispatch;
            bool exactBindingAfterDispatch;
            lock (_sync)
            {
                if (_disposed || generation != _generation || _current.SessionId != sessionId)
                {
                    return PlaybackEngineOperationResult.Failed(
                        DomainErrorCode.OperationCancelled);
                }

                exactBindingAfterDispatch = _engineSession == physicalSession &&
                    _engineLogicalSession == sessionId;
                terminalAfterDispatch = _current.State == PlaybackState.Failed
                    ? _current.Error
                    : null;
                invalidAfterDispatch = terminalAfterDispatch is null &&
                    (_current.State is PlaybackState.Closed or PlaybackState.Reconnecting or
                        PlaybackState.Stopping ||
                        _currentLifetime is null);
            }

            if (terminalAfterDispatch is not null)
            {
                if (exactBindingAfterDispatch)
                {
                    await StopEngineSessionUnderGateAsync(physicalSession).ConfigureAwait(false);
                }

                return PlaybackEngineOperationResult.Failed(terminalAfterDispatch);
            }

            if (invalidAfterDispatch || !exactBindingAfterDispatch)
            {
                return PlaybackEngineOperationResult.Failed(DomainErrorCode.OperationCancelled);
            }

            if (!result.IsSuccess)
            {
                await StopEngineSessionUnderGateAsync(physicalSession).ConfigureAwait(false);
                SetFailureIfCurrent(generation, sessionId, result.Error!);
                return result;
            }

            return PlaybackEngineOperationResult.Succeeded();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellationToken);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return PlaybackEngineOperationResult.Failed(DomainErrorCode.OperationCancelled);
        }
        finally
        {
            if (gateEntered)
            {
                _engineGate.Release();
            }

            request.Dispose();
        }
    }

    private async ValueTask<PlaybackEngineOperationResult> ExecuteCurrentControlCommandAsync(
        PlaybackSessionId expectedSession,
        Func<PlaybackSessionId, CancellationToken, ValueTask<PlaybackEngineOperationResult>> command,
        Action applySuccessfulControlUnderLock,
        Func<bool>? canExecute,
        CancellationToken cancellationToken)
    {
        if (expectedSession.IsEmpty)
        {
            throw new ArgumentException("A playback session identifier is required.", nameof(expectedSession));
        }

        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(applySuccessfulControlUnderLock);
        cancellationToken.ThrowIfCancellationRequested();
        SessionOperationCancellation request;
        long generation;

        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_current.SessionId != expectedSession)
            {
                return PlaybackEngineOperationResult.Failed(DomainErrorCode.OperationCancelled);
            }

            if (_current.State is PlaybackState.Closed or PlaybackState.Reconnecting or
                    PlaybackState.Stopping or PlaybackState.Failed ||
                _currentLifetime is null ||
                canExecute is not null && !canExecute())
            {
                return PlaybackEngineOperationResult.Failed(DomainErrorCode.DomainInvariantViolation);
            }

            generation = _generation;
            request = _currentLifetime.CreateOperation(cancellationToken);
        }

        bool gateEntered = false;
        try
        {
            await _engineGate.WaitAsync(request.Token).ConfigureAwait(false);
            gateEntered = true;
            request.Token.ThrowIfCancellationRequested();
            DomainError? terminalBeforeDispatch;
            bool invalidBeforeDispatch;
            PlaybackSessionId physicalSession;
            bool exactBindingBeforeDispatch;
            lock (_sync)
            {
                if (_disposed || generation != _generation || _current.SessionId != expectedSession)
                {
                    return PlaybackEngineOperationResult.Failed(DomainErrorCode.OperationCancelled);
                }

                terminalBeforeDispatch = _current.State == PlaybackState.Failed
                    ? _current.Error
                    : null;
                invalidBeforeDispatch = terminalBeforeDispatch is null &&
                    (_current.State is PlaybackState.Closed or PlaybackState.Reconnecting or
                        PlaybackState.Stopping ||
                        _currentLifetime is null ||
                        canExecute is not null && !canExecute());
                physicalSession = _engineSession;
                exactBindingBeforeDispatch = !physicalSession.IsEmpty &&
                    _engineLogicalSession == expectedSession;
            }

            if (terminalBeforeDispatch is not null)
            {
                if (exactBindingBeforeDispatch)
                {
                    await StopEngineSessionUnderGateAsync(physicalSession).ConfigureAwait(false);
                }

                return PlaybackEngineOperationResult.Failed(terminalBeforeDispatch);
            }

            if (invalidBeforeDispatch)
            {
                return PlaybackEngineOperationResult.Failed(DomainErrorCode.OperationCancelled);
            }

            if (!exactBindingBeforeDispatch)
            {
                return PlaybackEngineOperationResult.Failed(DomainErrorCode.OperationCancelled);
            }

            PlaybackEngineOperationResult result = await InvokeEngineOperationAsync(
                token => command(physicalSession, token),
                DomainErrorCode.PlaybackControlFailed,
                request.Token).ConfigureAwait(false);
            request.Token.ThrowIfCancellationRequested();

            DomainError? terminalError;
            bool invalidAfterDispatch;
            bool exactBindingAfterDispatch;
            bool applied = false;
            lock (_sync)
            {
                if (_disposed || generation != _generation || _current.SessionId != expectedSession)
                {
                    return PlaybackEngineOperationResult.Failed(DomainErrorCode.OperationCancelled);
                }

                terminalError = _current.State == PlaybackState.Failed
                    ? _current.Error
                    : null;
                invalidAfterDispatch = terminalError is null &&
                    (_current.State is PlaybackState.Closed or PlaybackState.Reconnecting or
                        PlaybackState.Stopping ||
                        _currentLifetime is null);
                exactBindingAfterDispatch = _engineSession == physicalSession &&
                    _engineLogicalSession == expectedSession;
                if (terminalError is null &&
                    !invalidAfterDispatch &&
                    exactBindingAfterDispatch &&
                    result.IsSuccess)
                {
                    applySuccessfulControlUnderLock();
                    applied = true;
                }
            }

            if (terminalError is not null)
            {
                if (exactBindingAfterDispatch)
                {
                    await StopEngineSessionUnderGateAsync(physicalSession).ConfigureAwait(false);
                }

                return PlaybackEngineOperationResult.Failed(terminalError);
            }

            if (invalidAfterDispatch || !exactBindingAfterDispatch)
            {
                return PlaybackEngineOperationResult.Failed(DomainErrorCode.OperationCancelled);
            }

            return applied
                ? PlaybackEngineOperationResult.Succeeded()
                : result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellationToken);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return PlaybackEngineOperationResult.Failed(DomainErrorCode.OperationCancelled);
        }
        finally
        {
            if (gateEntered)
            {
                _engineGate.Release();
            }

            request.Dispose();
        }
    }

    private async ValueTask<DomainResult<PlaybackTrackSnapshot>> ExecuteCurrentTrackQueryAsync(
        PlaybackSessionId expectedSession,
        CancellationToken cancellationToken)
    {
        if (expectedSession.IsEmpty)
        {
            throw new ArgumentException("A playback session identifier is required.", nameof(expectedSession));
        }

        cancellationToken.ThrowIfCancellationRequested();
        SessionOperationCancellation request;
        long generation;

        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_current.SessionId != expectedSession)
            {
                return DomainResult.Failure<PlaybackTrackSnapshot>(DomainErrorCode.OperationCancelled);
            }

            if (_current.State is PlaybackState.Closed or PlaybackState.Reconnecting or
                    PlaybackState.Stopping or PlaybackState.Failed ||
                _currentLifetime is null)
            {
                return DomainResult.Failure<PlaybackTrackSnapshot>(DomainErrorCode.DomainInvariantViolation);
            }

            generation = _generation;
            request = _currentLifetime.CreateOperation(cancellationToken);
        }

        bool gateEntered = false;
        try
        {
            await _engineGate.WaitAsync(request.Token).ConfigureAwait(false);
            gateEntered = true;
            request.Token.ThrowIfCancellationRequested();
            DomainError? terminalBeforeDispatch;
            bool invalidBeforeDispatch;
            PlaybackSessionId physicalSession;
            bool exactBindingBeforeDispatch;
            lock (_sync)
            {
                if (_disposed || generation != _generation || _current.SessionId != expectedSession)
                {
                    return DomainResult.Failure<PlaybackTrackSnapshot>(
                        DomainErrorCode.OperationCancelled);
                }

                terminalBeforeDispatch = _current.State == PlaybackState.Failed
                    ? _current.Error
                    : null;
                invalidBeforeDispatch = terminalBeforeDispatch is null &&
                    (_current.State is PlaybackState.Closed or PlaybackState.Reconnecting or
                        PlaybackState.Stopping ||
                        _currentLifetime is null);
                physicalSession = _engineSession;
                exactBindingBeforeDispatch = !physicalSession.IsEmpty &&
                    _engineLogicalSession == expectedSession;
            }

            if (terminalBeforeDispatch is not null)
            {
                if (exactBindingBeforeDispatch)
                {
                    await StopEngineSessionUnderGateAsync(physicalSession).ConfigureAwait(false);
                }

                return DomainResult.Failure<PlaybackTrackSnapshot>(terminalBeforeDispatch);
            }

            if (invalidBeforeDispatch)
            {
                return DomainResult.Failure<PlaybackTrackSnapshot>(
                    DomainErrorCode.OperationCancelled);
            }

            if (!exactBindingBeforeDispatch)
            {
                return DomainResult.Failure<PlaybackTrackSnapshot>(DomainErrorCode.OperationCancelled);
            }

            DomainResult<PlaybackTrackSnapshot> physicalResult = await InvokeTrackQueryAsync(
                token => _engine.GetTracksAsync(physicalSession, token),
                request.Token).ConfigureAwait(false);
            DomainResult<PlaybackTrackSnapshot> result = RebindTrackResult(
                physicalResult,
                physicalSession,
                expectedSession);
            request.Token.ThrowIfCancellationRequested();

            DomainError? terminalError;
            bool invalidAfterDispatch;
            bool exactBindingAfterDispatch;
            lock (_sync)
            {
                if (_disposed || generation != _generation || _current.SessionId != expectedSession)
                {
                    return DomainResult.Failure<PlaybackTrackSnapshot>(DomainErrorCode.OperationCancelled);
                }

                terminalError = _current.State == PlaybackState.Failed
                    ? _current.Error
                    : null;
                invalidAfterDispatch = terminalError is null &&
                    (_current.State is PlaybackState.Closed or PlaybackState.Reconnecting or
                        PlaybackState.Stopping ||
                        _currentLifetime is null);
                exactBindingAfterDispatch = _engineSession == physicalSession &&
                    _engineLogicalSession == expectedSession;
                if (terminalError is null &&
                    !invalidAfterDispatch &&
                    exactBindingAfterDispatch &&
                    result.IsSuccess)
                {
                    if (result.Value!.SessionId != expectedSession)
                    {
                        _currentTracks = null;
                        return DomainResult.Failure<PlaybackTrackSnapshot>(
                            DomainErrorCode.DomainInvariantViolation);
                    }

                    _currentTracks = result.Value;
                }
                else if (terminalError is null &&
                    !invalidAfterDispatch &&
                    exactBindingAfterDispatch)
                {
                    _currentTracks = null;
                }
            }

            if (terminalError is not null)
            {
                if (exactBindingAfterDispatch)
                {
                    await StopEngineSessionUnderGateAsync(physicalSession).ConfigureAwait(false);
                }

                return DomainResult.Failure<PlaybackTrackSnapshot>(terminalError);
            }

            if (invalidAfterDispatch || !exactBindingAfterDispatch)
            {
                return DomainResult.Failure<PlaybackTrackSnapshot>(
                    DomainErrorCode.OperationCancelled);
            }

            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellationToken);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return DomainResult.Failure<PlaybackTrackSnapshot>(DomainErrorCode.OperationCancelled);
        }
        finally
        {
            if (gateEntered)
            {
                _engineGate.Release();
            }

            request.Dispose();
        }
    }

    private async ValueTask<PlaybackEngineOperationResult> ApplyDesiredControlsUnderGateAsync(
        long generation,
        PlaybackSessionId sessionId,
        PlaybackControlSnapshot desiredControls,
        CancellationToken cancellationToken)
    {
        PlaybackEngineOperationResult volume = await InvokeEngineOperationAsync(
            token => _engine.SetVolumeAsync(sessionId, desiredControls.Volume, token),
            DomainErrorCode.PlaybackControlFailed,
            cancellationToken).ConfigureAwait(false);
        PlaybackEngineOperationResult checkedVolume = CheckControlRestoreProgress(
            generation,
            sessionId,
            volume,
            cancellationToken);
        if (!checkedVolume.IsSuccess)
        {
            return checkedVolume;
        }

        PlaybackEngineOperationResult mute = await InvokeEngineOperationAsync(
            token => _engine.SetMutedAsync(sessionId, desiredControls.IsMuted, token),
            DomainErrorCode.PlaybackControlFailed,
            cancellationToken).ConfigureAwait(false);
        PlaybackEngineOperationResult checkedMute = CheckControlRestoreProgress(
            generation,
            sessionId,
            mute,
            cancellationToken);
        if (!checkedMute.IsSuccess)
        {
            return checkedMute;
        }

        PlaybackEngineOperationResult aspect = await InvokeEngineOperationAsync(
            token => _engine.SetAspectModeAsync(sessionId, desiredControls.AspectMode, token),
            DomainErrorCode.PlaybackControlFailed,
            cancellationToken).ConfigureAwait(false);
        return CheckControlRestoreProgress(
            generation,
            sessionId,
            aspect,
            cancellationToken);
    }

    private PlaybackEngineOperationResult CheckControlRestoreProgress(
        long generation,
        PlaybackSessionId sessionId,
        PlaybackEngineOperationResult result,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!result.IsSuccess)
        {
            return result;
        }

        DomainError? terminalError = GetTerminalErrorIfCurrent(generation, sessionId);
        if (terminalError is not null)
        {
            return PlaybackEngineOperationResult.Failed(terminalError);
        }

        return IsCurrent(generation, sessionId)
            ? PlaybackEngineOperationResult.Succeeded()
            : PlaybackEngineOperationResult.Failed(DomainErrorCode.OperationCancelled);
    }

    private async Task<PlaybackEngineOperationResult> StopSessionAsync(
        PlaybackSessionId? expectedSession,
        bool requireCurrentSession)
    {
        SessionLifetime? lifetime = null;
        PlaybackSessionSnapshot? stopping = null;
        PlaybackSessionId sessionId;
        long generation;
        PlaybackReconnectCorrelationId? reconnectToCancel = null;

        lock (_sync)
        {
            if (_current.State == PlaybackState.Closed ||
                _currentSelection is null ||
                (expectedSession.HasValue && _current.SessionId != expectedSession.Value))
            {
                return PlaybackEngineOperationResult.Succeeded();
            }

            if (requireCurrentSession && expectedSession.HasValue &&
                _current.SessionId != expectedSession.Value)
            {
                return PlaybackEngineOperationResult.Succeeded();
            }

            sessionId = _current.SessionId;
            generation = checked(++_generation);
            reconnectToCancel = InvalidateReconnectUnderLock();
            stopping = PlaybackSessionSnapshot.Active(
                sessionId,
                _currentSelection,
                PlaybackState.Stopping);
            _current = stopping;
            if (_currentLifetime is not null)
            {
                lifetime = _currentLifetime;
                _currentLifetime = null;
            }
        }

        lifetime?.Retire();
        CancelReconnectSafely(reconnectToCancel);
        RaiseStateChanged(stopping);

        await _engineGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        PlaybackEngineOperationResult result;
        try
        {
            bool stopIntentIsCurrent;
            lock (_sync)
            {
                stopIntentIsCurrent = !_disposed &&
                    generation == _generation &&
                    _current.SessionId == sessionId;
            }

            result = stopIntentIsCurrent && !_engineSession.IsEmpty
                ? await StopEngineSessionUnderGateAsync(_engineSession).ConfigureAwait(false)
                : PlaybackEngineOperationResult.Succeeded();
        }
        finally
        {
            _engineGate.Release();
        }

        PlaybackSessionSnapshot? completed = null;
        lock (_sync)
        {
            if (!_disposed &&
                generation == _generation &&
                _current.SessionId == sessionId)
            {
                if (result.IsSuccess)
                {
                    _current = PlaybackSessionSnapshot.Closed();
                    _currentSelection = null;
                    _currentControls = PlaybackControlSnapshot.Idle(
                        _volume,
                        _isMuted,
                        _aspectMode);
                    _currentTracks = null;
                }
                else
                {
                    _current = PlaybackSessionSnapshot.Failed(
                        sessionId,
                        _currentSelection!,
                        result.Error!);
                }

                completed = _current;
            }
        }

        if (completed is not null)
        {
            RaiseStateChanged(completed);
        }

        return result;
    }

    private async Task CompleteDisposeAsync(TaskCompletionSource completion)
    {
        bool gateEntered = false;
        bool cleanupFailed = false;
        try
        {
            if (_reconnectOrchestrator is not null)
            {
                _reconnectOrchestrator.SnapshotChanged -= OnReconnectSnapshotChanged;
                try
                {
                    await _reconnectOrchestrator.DisposeAsync().ConfigureAwait(false);
                }
                catch (Exception)
                {
                    cleanupFailed = true;
                }
            }

            await _engineGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            gateEntered = true;
            _engine.StateChanged -= OnEngineStateChanged;
            if (!_engineSession.IsEmpty)
            {
                await StopEngineSessionUnderGateAsync(_engineSession).ConfigureAwait(false);
            }

            await _engine.DisposeAsync().ConfigureAwait(false);
            lock (_sync)
            {
                _engineSession = default;
                _engineLogicalSession = default;
                _engineSource = default;
                _currentSelection = null;
                _current = PlaybackSessionSnapshot.Closed();
                _currentControls = PlaybackControlSnapshot.Idle(
                    _volume,
                    _isMuted,
                    _aspectMode);
                _currentTracks = null;
            }

            if (cleanupFailed)
            {
                completion.TrySetException(
                    new InvalidOperationException(
                        "The playback reconnect coordinator could not be disposed safely."));
            }
            else
            {
                completion.TrySetResult();
            }
        }
        catch (Exception)
        {
            completion.TrySetException(
                new InvalidOperationException("The playback engine could not be disposed safely."));
        }
        finally
        {
            if (gateEntered)
            {
                _engineGate.Release();
            }
        }
    }

    private async ValueTask<PlaybackEngineOperationResult> StopEngineSessionUnderGateAsync(
        PlaybackSessionId sessionId)
    {
        lock (_sync)
        {
            if (_engineSession != sessionId)
            {
                return PlaybackEngineOperationResult.Succeeded();
            }
        }

        PlaybackEngineOperationResult result = await InvokeEngineOperationAsync(
            token => _engine.StopAsync(sessionId, token),
            DomainErrorCode.StreamInterrupted,
            CancellationToken.None).ConfigureAwait(false);
        if (result.IsSuccess)
        {
            lock (_sync)
            {
                if (_engineSession == sessionId)
                {
                    _engineSession = default;
                    _engineLogicalSession = default;
                    _engineSource = default;
                }
            }
        }

        return result;
    }

    private async Task<PlaybackEngineOperationResult> DrainRetiredSourceAsync(SourceId sourceId)
    {
        await _engineGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            return _engineSource == sourceId && !_engineSession.IsEmpty
                ? await StopEngineSessionUnderGateAsync(_engineSession).ConfigureAwait(false)
                : PlaybackEngineOperationResult.Succeeded();
        }
        finally
        {
            _engineGate.Release();
        }
    }

    private async Task<PlaybackEngineOperationResult> ReleaseRetiredSourceAsync(
        SourceId sourceId,
        bool releaseCurrent,
        PlaybackSessionId sessionId)
    {
        if (releaseCurrent)
        {
            PlaybackEngineOperationResult released = await StopSessionAsync(
                sessionId,
                requireCurrentSession: true).ConfigureAwait(false);
            if (!released.IsSuccess)
            {
                return released;
            }
        }

        return await DrainRetiredSourceAsync(sourceId).ConfigureAwait(false);
    }

    private void CommitSourceRetirement(SourceId sourceId, long reservationId)
    {
        lock (_sync)
        {
            CommitSourceRetirementUnderLock(sourceId, reservationId);
        }
    }

    private void CommitSourceRetirementUnderLock(SourceId sourceId, long reservationId)
    {
        if (!_sourceRetirements.TryGetValue(sourceId, out SourceRetirementState? retirement))
        {
            retirement = new SourceRetirementState();
            _sourceRetirements.Add(sourceId, retirement);
        }

        if (reservationId > 0)
        {
            retirement.Reservations.Remove(reservationId);
        }

        retirement.Reservations.Clear();
        retirement.IsPermanent = true;
    }

    private void RollbackSourceRetirement(SourceId sourceId, long reservationId)
    {
        if (reservationId <= 0)
        {
            return;
        }

        lock (_sync)
        {
            if (!_sourceRetirements.TryGetValue(sourceId, out SourceRetirementState? retirement) ||
                retirement.IsPermanent)
            {
                return;
            }

            retirement.Reservations.Remove(reservationId);
            if (retirement.Reservations.Count == 0)
            {
                _sourceRetirements.Remove(sourceId);
            }
        }
    }

    private async ValueTask<PlaybackEngineOperationResult> ExecuteReconnectAttemptAsync(
        PlaybackReconnectCorrelationId correlationId,
        int attemptNumber,
        CancellationToken cancellationToken)
    {
        ReconnectContext? context = null;
        SessionOperationCancellation? request = null;
        PlaybackSessionId physicalSession = default;
        TaskCompletionSource<PlaybackEngineOperationResult>? playableCompletion = null;
        bool gateEntered = false;
        bool successCandidate = false;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_sync)
            {
                context = _reconnectContext;
                if (context is null ||
                    context.CorrelationId != correlationId ||
                    context.IsTerminal ||
                    _current.State != PlaybackState.Reconnecting ||
                    _currentLifetime is null ||
                    !IsExactReconnectContextLocked(context) ||
                    _sourceRetirements.ContainsKey(context.Selection.SourceId))
                {
                    return PlaybackEngineOperationResult.Failed(
                        DomainErrorCode.OperationCancelled);
                }

                request = _currentLifetime.CreateOperation(cancellationToken);
            }

            await _engineGate.WaitAsync(request.Token).ConfigureAwait(false);
            gateEntered = true;
            request.Token.ThrowIfCancellationRequested();

            PlaybackControlSnapshot desiredControls;
            PlaybackSessionId boundPhysicalSession;
            PlaybackSessionId boundLogicalSession;
            lock (_sync)
            {
                if (!CanRunReconnectAttemptLocked(context, correlationId))
                {
                    return PlaybackEngineOperationResult.Failed(
                        DomainErrorCode.OperationCancelled);
                }

                desiredControls = _currentControls;
                boundPhysicalSession = _engineSession;
                boundLogicalSession = _engineLogicalSession;
            }

            if (!boundPhysicalSession.IsEmpty)
            {
                if (boundPhysicalSession != context.PhysicalSessionId ||
                    boundLogicalSession != context.SessionId)
                {
                    return PlaybackEngineOperationResult.Failed(
                        DomainErrorCode.DomainInvariantViolation);
                }

                physicalSession = boundPhysicalSession;
                PlaybackEngineOperationResult drained =
                    await StopEngineSessionUnderGateAsync(physicalSession).ConfigureAwait(false);
                if (!drained.IsSuccess)
                {
                    return drained;
                }
            }
            else if (!boundLogicalSession.IsEmpty)
            {
                return PlaybackEngineOperationResult.Failed(
                    DomainErrorCode.DomainInvariantViolation);
            }

            request.Token.ThrowIfCancellationRequested();
            lock (_sync)
            {
                if (!CanRunReconnectAttemptLocked(context, correlationId))
                {
                    return PlaybackEngineOperationResult.Failed(
                        DomainErrorCode.OperationCancelled);
                }

                physicalSession = NextSessionId();
                playableCompletion = new TaskCompletionSource<PlaybackEngineOperationResult>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                context.PhysicalSessionId = physicalSession;
                context.AttemptInProgress = true;
                context.AttemptNumber = attemptNumber;
                context.AttemptFailure = null;
                context.RecoveredState = null;
                context.PlayableCompletion = playableCompletion;
                _engineSession = physicalSession;
                _engineLogicalSession = context.SessionId;
                _engineSource = context.Selection.SourceId;
            }

            PlaybackEngineOperationResult opened = await InvokeEngineOperationAsync(
                token => _engine.OpenAsync(physicalSession, context.Selection, token),
                DomainErrorCode.PlaybackStartFailed,
                request.Token).ConfigureAwait(false);
            PlaybackEngineOperationResult checkedOpen = CheckReconnectAttemptProgress(
                context,
                opened,
                request.Token);
            if (!checkedOpen.IsSuccess)
            {
                return await RollbackReconnectAttemptUnderGateAsync(
                    context,
                    physicalSession,
                    checkedOpen).ConfigureAwait(false);
            }

            PlaybackEngineOperationResult volume = await InvokeEngineOperationAsync(
                token => _engine.SetVolumeAsync(
                    physicalSession,
                    desiredControls.Volume,
                    token),
                DomainErrorCode.PlaybackControlFailed,
                request.Token).ConfigureAwait(false);
            PlaybackEngineOperationResult checkedVolume = CheckReconnectAttemptProgress(
                context,
                volume,
                request.Token);
            if (!checkedVolume.IsSuccess)
            {
                return await RollbackReconnectAttemptUnderGateAsync(
                    context,
                    physicalSession,
                    checkedVolume).ConfigureAwait(false);
            }

            PlaybackEngineOperationResult mute = await InvokeEngineOperationAsync(
                token => _engine.SetMutedAsync(
                    physicalSession,
                    desiredControls.IsMuted,
                    token),
                DomainErrorCode.PlaybackControlFailed,
                request.Token).ConfigureAwait(false);
            PlaybackEngineOperationResult checkedMute = CheckReconnectAttemptProgress(
                context,
                mute,
                request.Token);
            if (!checkedMute.IsSuccess)
            {
                return await RollbackReconnectAttemptUnderGateAsync(
                    context,
                    physicalSession,
                    checkedMute).ConfigureAwait(false);
            }

            PlaybackEngineOperationResult aspect = await InvokeEngineOperationAsync(
                token => _engine.SetAspectModeAsync(
                    physicalSession,
                    desiredControls.AspectMode,
                    token),
                DomainErrorCode.PlaybackControlFailed,
                request.Token).ConfigureAwait(false);
            PlaybackEngineOperationResult checkedAspect = CheckReconnectAttemptProgress(
                context,
                aspect,
                request.Token);
            if (!checkedAspect.IsSuccess)
            {
                return await RollbackReconnectAttemptUnderGateAsync(
                    context,
                    physicalSession,
                    checkedAspect).ConfigureAwait(false);
            }

            PlaybackEngineOperationResult played = await InvokeEngineOperationAsync(
                token => _engine.PlayAsync(physicalSession, token),
                DomainErrorCode.PlaybackStartFailed,
                request.Token).ConfigureAwait(false);
            PlaybackEngineOperationResult checkedPlay = CheckReconnectAttemptProgress(
                context,
                played,
                request.Token);
            if (!checkedPlay.IsSuccess)
            {
                return await RollbackReconnectAttemptUnderGateAsync(
                    context,
                    physicalSession,
                    checkedPlay).ConfigureAwait(false);
            }

            PlaybackEngineOperationResult playable =
                await WaitForReconnectPlayableAsync(
                    context,
                    physicalSession,
                    playableCompletion,
                    request.Token).ConfigureAwait(false);
            if (!playable.IsSuccess)
            {
                return await RollbackReconnectAttemptUnderGateAsync(
                    context,
                    physicalSession,
                    playable)
                    .ConfigureAwait(false);
            }

            successCandidate = true;
            return PlaybackEngineOperationResult.Succeeded();
        }
        catch (OperationCanceledException)
        {
            if (gateEntered && context is not null)
            {
                await RollbackReconnectAttemptUnderGateAsync(
                    context,
                    physicalSession,
                    PlaybackEngineOperationResult.Failed(
                        DomainErrorCode.OperationCancelled)).ConfigureAwait(false);
            }

            return PlaybackEngineOperationResult.Failed(DomainErrorCode.OperationCancelled);
        }
        catch (Exception)
        {
            if (gateEntered && context is not null)
            {
                await RollbackReconnectAttemptUnderGateAsync(
                    context,
                    physicalSession,
                    PlaybackEngineOperationResult.Failed(
                        DomainErrorCode.DomainInvariantViolation)).ConfigureAwait(false);
            }

            return PlaybackEngineOperationResult.Failed(
                DomainErrorCode.DomainInvariantViolation);
        }
        finally
        {
            if (context is not null && !successCandidate)
            {
                lock (_sync)
                {
                    if (ReferenceEquals(_reconnectContext, context))
                    {
                        context.AttemptInProgress = false;
                        context.RecoveredState = null;
                        context.PlayableCompletion?.TrySetResult(
                            PlaybackEngineOperationResult.Failed(
                                DomainErrorCode.OperationCancelled));
                        context.PlayableCompletion = null;
                    }
                }
            }

            if (gateEntered)
            {
                _engineGate.Release();
            }

            request?.Dispose();
        }
    }

    private async ValueTask<PlaybackEngineOperationResult> WaitForReconnectPlayableAsync(
        ReconnectContext context,
        PlaybackSessionId physicalSession,
        TaskCompletionSource<PlaybackEngineOperationResult> playableCompletion,
        CancellationToken cancellationToken)
    {
        PlaybackEngineOperationResult observed = await playableCompletion.Task
            .WaitAsync(cancellationToken)
            .ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (!observed.IsSuccess)
        {
            return observed;
        }

        PlaybackEngineSnapshot engineCurrent = _engine.Current;
        lock (_sync)
        {
            if (!CanRunReconnectAttemptLocked(context, context.CorrelationId) ||
                !context.AttemptInProgress ||
                !ReferenceEquals(context.PlayableCompletion, playableCompletion) ||
                context.PhysicalSessionId != physicalSession ||
                context.AttemptFailure is not null)
            {
                return PlaybackEngineOperationResult.Failed(
                    context.AttemptFailure ??
                        DomainError.Create(DomainErrorCode.OperationCancelled));
            }

            if (context.RecoveredState is not (
                    PlaybackState.Playing or PlaybackState.Paused) ||
                engineCurrent.SessionId != physicalSession ||
                engineCurrent.State is not (
                    PlaybackState.Playing or PlaybackState.Paused))
            {
                return PlaybackEngineOperationResult.Failed(
                    engineCurrent.State == PlaybackState.Failed &&
                        engineCurrent.Error is not null
                        ? engineCurrent.Error
                        : DomainError.Create(DomainErrorCode.PlaybackStartFailed));
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        return PlaybackEngineOperationResult.Succeeded();
    }

    private PlaybackEngineOperationResult CheckReconnectAttemptProgress(
        ReconnectContext context,
        PlaybackEngineOperationResult result,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            if (!CanRunReconnectAttemptLocked(context, context.CorrelationId))
            {
                return PlaybackEngineOperationResult.Failed(
                    DomainErrorCode.OperationCancelled);
            }

            return context.AttemptFailure is null
                ? result
                : PlaybackEngineOperationResult.Failed(context.AttemptFailure);
        }
    }

    private async ValueTask<PlaybackEngineOperationResult>
        RollbackReconnectAttemptUnderGateAsync(
            ReconnectContext context,
            PlaybackSessionId sessionId,
            PlaybackEngineOperationResult failure)
    {
        lock (_sync)
        {
            if (context.PhysicalSessionId == sessionId)
            {
                context.AttemptInProgress = false;
                context.AttemptFailure = failure.Error ??
                    DomainError.Create(DomainErrorCode.DomainInvariantViolation);
                context.RecoveredState = null;
                context.PlayableCompletion?.TrySetResult(failure);
                context.PlayableCompletion = null;
            }
        }

        if (_engineSession != sessionId)
        {
            return failure;
        }

        PlaybackEngineOperationResult rollback =
            await StopEngineSessionUnderGateAsync(sessionId).ConfigureAwait(false);
        return rollback.IsSuccess ? failure : rollback;
    }

    private bool CanRunReconnectAttemptLocked(
        ReconnectContext context,
        PlaybackReconnectCorrelationId correlationId) =>
        !_disposed &&
        !context.IsTerminal &&
        context.CorrelationId == correlationId &&
        _current.State == PlaybackState.Reconnecting &&
        _currentLifetime is not null &&
        !_sourceRetirements.ContainsKey(context.Selection.SourceId) &&
        IsExactReconnectContextLocked(context);

    private static async ValueTask<PlaybackEngineOperationResult> InvokeEngineOperationAsync(
        Func<CancellationToken, ValueTask<PlaybackEngineOperationResult>> operation,
        DomainErrorCode fallbackError,
        CancellationToken cancellationToken)
    {
        try
        {
            PlaybackEngineOperationResult result = await operation(cancellationToken).ConfigureAwait(false);
            return result ?? PlaybackEngineOperationResult.Failed(fallbackError);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return PlaybackEngineOperationResult.Failed(fallbackError);
        }
        catch (Exception)
        {
            return PlaybackEngineOperationResult.Failed(fallbackError);
        }
    }

    private static async ValueTask<DomainResult<PlaybackTrackSnapshot>> InvokeTrackQueryAsync(
        Func<CancellationToken, ValueTask<DomainResult<PlaybackTrackSnapshot>>> operation,
        CancellationToken cancellationToken)
    {
        try
        {
            DomainResult<PlaybackTrackSnapshot> result =
                await operation(cancellationToken).ConfigureAwait(false);
            return result ?? DomainResult.Failure<PlaybackTrackSnapshot>(
                DomainErrorCode.PlaybackControlFailed);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return DomainResult.Failure<PlaybackTrackSnapshot>(
                DomainErrorCode.PlaybackControlFailed);
        }
        catch (Exception)
        {
            return DomainResult.Failure<PlaybackTrackSnapshot>(
                DomainErrorCode.PlaybackControlFailed);
        }
    }

    private static DomainResult<PlaybackTrackSnapshot> RebindTrackResult(
        DomainResult<PlaybackTrackSnapshot> result,
        PlaybackSessionId physicalSession,
        PlaybackSessionId logicalSession)
    {
        if (!result.IsSuccess)
        {
            return DomainResult.Failure<PlaybackTrackSnapshot>(
                result.Error ?? DomainError.Create(DomainErrorCode.PlaybackControlFailed));
        }

        PlaybackTrackSnapshot physical = result.Value!;
        if (physical.SessionId != physicalSession)
        {
            return DomainResult.Failure<PlaybackTrackSnapshot>(
                DomainErrorCode.DomainInvariantViolation);
        }

        try
        {
            return DomainResult.Success(PlaybackTrackSnapshot.Create(
                logicalSession,
                physical.Capabilities,
                physical.Tracks.Select(track => new PlaybackTrackInfo(
                    PlaybackTrackId.Create(
                        logicalSession,
                        track.Id.Kind,
                        track.Id.Ordinal),
                    track.IsSelected,
                    track.IsSelectable))));
        }
        catch (Exception)
        {
            return DomainResult.Failure<PlaybackTrackSnapshot>(
                DomainErrorCode.DomainInvariantViolation);
        }
    }

    private void OnEngineStateChanged(
        object? sender,
        PlaybackEngineStateChangedEventArgs eventArgs)
    {
        ArgumentNullException.ThrowIfNull(eventArgs);
        PlaybackEngineSnapshot engineSnapshot = eventArgs.Snapshot;
        PlaybackSessionSnapshot? changed = null;
        ReconnectContext? reconnectToBegin = null;
        DomainError? reconnectFailure = null;

        lock (_sync)
        {
            if (_disposed ||
                _currentSelection is null ||
                _engineSession.IsEmpty ||
                engineSnapshot.SessionId != _engineSession ||
                _engineLogicalSession != _current.SessionId)
            {
                return;
            }

            if (_reconnectContext is { } activeReconnect &&
                IsExactReconnectContextLocked(activeReconnect))
            {
                if (_current.State == PlaybackState.Reconnecting &&
                    activeReconnect.AttemptInProgress)
                {
                    if (engineSnapshot.State == PlaybackState.Failed)
                    {
                        activeReconnect.AttemptFailure = engineSnapshot.Error ??
                            DomainError.Create(DomainErrorCode.DomainInvariantViolation);
                        activeReconnect.RecoveredState = null;
                        activeReconnect.PlayableCompletion?.TrySetResult(
                            PlaybackEngineOperationResult.Failed(
                                activeReconnect.AttemptFailure));
                    }
                    else if (engineSnapshot.State == PlaybackState.Buffering)
                    {
                        activeReconnect.RecoveredState = null;
                    }
                    else if (engineSnapshot.State is PlaybackState.Playing or PlaybackState.Paused)
                    {
                        activeReconnect.RecoveredState = engineSnapshot.State;
                        activeReconnect.PlayableCompletion?.TrySetResult(
                            PlaybackEngineOperationResult.Succeeded());
                    }
                }

                return;
            }

            if (!CanTransition(_current.State, engineSnapshot.State))
            {
                return;
            }

            if (engineSnapshot.State == PlaybackState.Failed &&
                _currentLifetime is not null &&
                _current.State is (
                    PlaybackState.Opening or
                    PlaybackState.Buffering or
                    PlaybackState.Playing or
                    PlaybackState.Paused) &&
                !_sourceRetirements.ContainsKey(_currentSelection.SourceId) &&
                IsAutomaticReconnectEligible(engineSnapshot.Error))
            {
                reconnectFailure = engineSnapshot.Error!;
                reconnectToBegin = new ReconnectContext(
                    _generation,
                    NextReconnectCorrelationId(),
                    _current.SessionId,
                    _engineSession,
                    _currentSelection);
                _reconnectContext = reconnectToBegin;
                _currentTracks = null;
            }
            else if (engineSnapshot.State == PlaybackState.Closed)
            {
                _current = PlaybackSessionSnapshot.Closed();
                _currentSelection = null;
                _currentControls = PlaybackControlSnapshot.Idle(
                    _volume,
                    _isMuted,
                    _aspectMode);
                _currentTracks = null;
            }
            else if (engineSnapshot.State == PlaybackState.Failed)
            {
                _current = PlaybackSessionSnapshot.Failed(
                    _current.SessionId,
                    _currentSelection,
                    engineSnapshot.Error!);
                _currentTracks = null;
            }
            else
            {
                _current = PlaybackSessionSnapshot.Active(
                    _current.SessionId,
                    _currentSelection,
                    engineSnapshot.State);
            }

            changed = _current;
        }

        if (reconnectToBegin is not null)
        {
            BeginReconnect(reconnectToBegin, reconnectFailure!);
        }
        else if (changed is not null)
        {
            RaiseStateChanged(changed);
        }
    }

    private void BeginReconnect(ReconnectContext context, DomainError failure)
    {
        PlaybackReconnectOrchestrator orchestrator = _reconnectOrchestrator ??
            throw new InvalidOperationException("Reconnect orchestration is not enabled.");
        try
        {
            _ = orchestrator.BeginAsync(context.CorrelationId, failure);
        }
        catch (Exception)
        {
            PlaybackSessionSnapshot? failed = null;
            lock (_sync)
            {
                if (IsExactReconnectContextLocked(context))
                {
                    _reconnectContext = null;
                    failed = PlaybackSessionSnapshot.Failed(
                        context.SessionId,
                        context.Selection,
                        DomainError.Create(DomainErrorCode.DomainInvariantViolation));
                    _current = failed;
                    _currentTracks = null;
                }
            }

            if (failed is not null)
            {
                RaiseStateChanged(failed);
            }

            return;
        }

        bool stillCurrent;
        lock (_sync)
        {
            stillCurrent = IsExactReconnectContextLocked(context);
        }

        if (!stillCurrent)
        {
            CancelReconnectSafely(context.CorrelationId);
        }
    }

    private void OnReconnectSnapshotChanged(
        object? sender,
        PlaybackReconnectSnapshotChangedEventArgs eventArgs)
    {
        ArgumentNullException.ThrowIfNull(eventArgs);
        PlaybackReconnectSnapshot reconnect = eventArgs.Snapshot;
        PlaybackSessionSnapshot? changed = null;
        ReconnectContext? terminalContextToDrain = null;
        PlaybackSessionId terminalPhysicalSession = default;

        lock (_sync)
        {
            ReconnectContext? context = _reconnectContext;
            if (_disposed ||
                context is null ||
                context.CorrelationId != reconnect.CorrelationId ||
                !IsExactReconnectContextLocked(context))
            {
                return;
            }

            if (!reconnect.IsTerminal)
            {
                context.IsTerminal = false;
                context.ManualRetryActive = context.ManualRetryActive ||
                    context.ManualRetryStarting;
                _current = PlaybackSessionSnapshot.Reconnecting(
                    context.SessionId,
                    context.Selection,
                    reconnect);
            }
            else if (reconnect.Phase == PlaybackReconnectPhase.Succeeded &&
                context.AttemptFailure is null &&
                context.RecoveredState is PlaybackState.Playing or PlaybackState.Paused)
            {
                _current = PlaybackSessionSnapshot.Active(
                    context.SessionId,
                    context.Selection,
                    context.RecoveredState.Value);
                context.AttemptInProgress = false;
                context.PlayableCompletion = null;
                _reconnectContext = null;
            }
            else
            {
                DomainErrorCode terminalCode = reconnect.Phase switch
                {
                    PlaybackReconnectPhase.Exhausted => DomainErrorCode.ReconnectExhausted,
                    PlaybackReconnectPhase.DoNotRetry => reconnect.TerminalErrorCode ??
                        DomainErrorCode.DomainInvariantViolation,
                    PlaybackReconnectPhase.Cancelled => DomainErrorCode.OperationCancelled,
                    _ => context.AttemptFailure?.Code ??
                        DomainErrorCode.DomainInvariantViolation,
                };
                _current = PlaybackSessionSnapshot.Failed(
                    context.SessionId,
                    context.Selection,
                    DomainError.Create(terminalCode));
                _currentTracks = null;
                context.AttemptInProgress = false;
                context.PlayableCompletion?.TrySetResult(
                    PlaybackEngineOperationResult.Failed(terminalCode));
                context.PlayableCompletion = null;
                context.IsTerminal = true;
                context.ManualRetryStarting = false;
                context.ManualRetryActive = false;
                if (!_engineSession.IsEmpty &&
                    _engineSession == context.PhysicalSessionId &&
                    _engineLogicalSession == context.SessionId)
                {
                    terminalContextToDrain = context;
                    terminalPhysicalSession = context.PhysicalSessionId;
                }
            }

            changed = _current;
        }

        if (terminalContextToDrain is not null)
        {
            _ = DrainTerminalReconnectPhysicalAsync(
                terminalContextToDrain,
                terminalPhysicalSession);
        }

        RaiseStateChanged(changed);
    }

    private async Task DrainTerminalReconnectPhysicalAsync(
        ReconnectContext context,
        PlaybackSessionId physicalSession)
    {
        bool gateEntered = false;
        try
        {
            await _engineGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            gateEntered = true;
            bool exactTerminalBinding;
            lock (_sync)
            {
                exactTerminalBinding = !_disposed &&
                    context.IsTerminal &&
                    IsExactReconnectContextLocked(context) &&
                    _current.State == PlaybackState.Failed &&
                    _engineSession == physicalSession &&
                    _engineLogicalSession == context.SessionId;
            }

            if (exactTerminalBinding)
            {
                await StopEngineSessionUnderGateAsync(physicalSession).ConfigureAwait(false);
            }
        }
        catch (Exception)
        {
            // The safe terminal snapshot remains authoritative if cleanup cannot be completed.
        }
        finally
        {
            if (gateEntered)
            {
                _engineGate.Release();
            }
        }
    }

    private bool IsAutomaticReconnectEligible(DomainError? failure)
    {
        if (failure is null || _reconnectPolicy is null || _reconnectOrchestrator is null)
        {
            return false;
        }

        try
        {
            return _reconnectPolicy.Evaluate(
                failure,
                completedAttemptCount: 0,
                elapsed: TimeSpan.Zero,
                injectedJitter: TimeSpan.Zero).Kind ==
                    PlaybackReconnectDecisionKind.RetryAfterDelay;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private bool CanStartManualReconnectLocked(ReconnectContext context) =>
        !_disposed &&
        _reconnectOrchestrator is not null &&
        _currentLifetime is not null &&
        context.IsTerminal &&
        !context.ManualRetryStarting &&
        !context.ManualRetryActive &&
        _current.State == PlaybackState.Failed &&
        IsCanonicalManualError(_current.Error) &&
        !_sourceRetirements.ContainsKey(context.Selection.SourceId) &&
        IsExactReconnectContextLocked(context);

    private bool IsManualReconnectInFlightLocked(ReconnectContext context) =>
        !_disposed &&
        _currentLifetime is not null &&
        (context.ManualRetryStarting || context.ManualRetryActive) &&
        !_sourceRetirements.ContainsKey(context.Selection.SourceId) &&
        IsExactReconnectContextLocked(context) &&
        (_current.State == PlaybackState.Reconnecting ||
            (_current.State == PlaybackState.Failed &&
                IsCanonicalManualError(_current.Error)));

    private static bool IsCanonicalManualError(DomainError? error)
    {
        if (error is null || !Enum.IsDefined(error.Code))
        {
            return false;
        }

        DomainError canonical = DomainError.Create(error.Code);
        return canonical.Retryability == DomainRetryability.Manual &&
            error.Retryability == canonical.Retryability &&
            string.Equals(
                error.ResourceKey,
                canonical.ResourceKey,
                StringComparison.Ordinal);
    }

    private void ResetManualRetryStarting(ReconnectContext context)
    {
        lock (_sync)
        {
            if (ReferenceEquals(_reconnectContext, context))
            {
                context.ManualRetryStarting = false;
            }
        }
    }

    private PlaybackSessionSnapshot? SetFailureIfCurrent(
        long generation,
        PlaybackSessionId sessionId,
        PlaybackSelection selection,
        DomainError error)
    {
        PlaybackSessionSnapshot? failed = null;
        lock (_sync)
        {
            if (!_disposed && generation == _generation && _current.SessionId == sessionId)
            {
                failed = PlaybackSessionSnapshot.Failed(sessionId, selection, error);
                _current = failed;
                _currentTracks = null;
            }
        }

        if (failed is not null)
        {
            RaiseStateChanged(failed);
        }

        return failed;
    }

    private void SetFailureIfCurrent(
        long generation,
        PlaybackSessionId sessionId,
        DomainError error)
    {
        PlaybackSelection? selection;
        lock (_sync)
        {
            selection = _currentSelection;
        }

        if (selection is not null)
        {
            SetFailureIfCurrent(generation, sessionId, selection, error);
        }
    }

    private PlaybackSessionSnapshot? GetCurrentIfCurrent(
        long generation,
        PlaybackSessionId sessionId)
    {
        lock (_sync)
        {
            return !_disposed && generation == _generation && _current.SessionId == sessionId
                ? _current
                : null;
        }
    }

    private bool CanContinueStart(long generation, PlaybackSessionId sessionId)
    {
        lock (_sync)
        {
            return !_disposed &&
                generation == _generation &&
                _current.SessionId == sessionId &&
                _current.State is PlaybackState.Opening or
                    PlaybackState.Buffering or
                    PlaybackState.Playing or
                    PlaybackState.Paused;
        }
    }

    private DomainError? GetTerminalErrorIfCurrent(
        long generation,
        PlaybackSessionId sessionId)
    {
        lock (_sync)
        {
            return !_disposed &&
                generation == _generation &&
                _current.SessionId == sessionId &&
                _current.State == PlaybackState.Failed
                    ? _current.Error
                    : null;
        }
    }

    private bool IsCurrent(long generation, PlaybackSessionId sessionId)
    {
        lock (_sync)
        {
            return !_disposed && generation == _generation && _current.SessionId == sessionId;
        }
    }

    private PlaybackSessionId NextSessionId()
    {
        long next = checked(++_sessionSequence);
        return PlaybackSessionId.FromSequence(next);
    }

    private PlaybackReconnectCorrelationId NextReconnectCorrelationId()
    {
        long next = checked(++_reconnectSequence);
        return PlaybackReconnectCorrelationId.FromSequence(next);
    }

    private PlaybackReconnectCorrelationId? InvalidateReconnectUnderLock()
    {
        ReconnectContext? context = _reconnectContext;
        if (context is not null)
        {
            context.AttemptInProgress = false;
            context.RecoveredState = null;
            context.PlayableCompletion?.TrySetResult(
                PlaybackEngineOperationResult.Failed(
                    DomainErrorCode.OperationCancelled));
            context.PlayableCompletion = null;
        }

        _reconnectContext = null;
        return context?.CorrelationId;
    }

    private bool IsExactReconnectContextLocked(ReconnectContext context) =>
        ReferenceEquals(_reconnectContext, context) &&
        context.Generation == _generation &&
        _current.SessionId == context.SessionId &&
        ReferenceEquals(_currentSelection, context.Selection);

    private void CancelReconnectSafely(
        PlaybackReconnectCorrelationId? correlationId)
    {
        if (!correlationId.HasValue || _reconnectOrchestrator is null)
        {
            return;
        }

        try
        {
            _reconnectOrchestrator.Cancel(correlationId.Value);
        }
        catch (ObjectDisposedException)
        {
            // Concurrent coordinator disposal already owns reconnect cancellation.
        }
    }

    private void UpdateSelectedTrackUnderLock(PlaybackTrackId selectedTrack)
    {
        PlaybackTrackSnapshot currentTracks = _currentTracks ??
            throw new InvalidOperationException("A track snapshot is required before selection.");
        PlaybackTrackInfo[] updated = currentTracks.Tracks
            .Select(track => track.Id.Kind == selectedTrack.Kind
                ? new PlaybackTrackInfo(
                    track.Id,
                    isSelected: track.Id == selectedTrack,
                    isSelectable: track.IsSelectable)
                : track)
            .ToArray();
        _currentTracks = PlaybackTrackSnapshot.Create(
            currentTracks.SessionId,
            currentTracks.Capabilities,
            updated);
    }

    private static bool CanTransition(PlaybackState current, PlaybackState next)
    {
        if (current == next)
        {
            return false;
        }

        if (next == PlaybackState.Failed)
        {
            return current is not PlaybackState.Closed and
                not PlaybackState.Reconnecting and
                not PlaybackState.Stopping and
                not PlaybackState.Failed;
        }

        return current switch
        {
            PlaybackState.Opening => next is PlaybackState.Buffering or PlaybackState.Playing or PlaybackState.Paused,
            PlaybackState.Buffering => next is PlaybackState.Playing or PlaybackState.Paused,
            PlaybackState.Playing => next is PlaybackState.Buffering or PlaybackState.Paused,
            PlaybackState.Paused => next is PlaybackState.Buffering or PlaybackState.Playing,
            PlaybackState.Stopping => next == PlaybackState.Closed,
            _ => false,
        };
    }

    private void RaiseStateChanged(PlaybackSessionSnapshot snapshot)
    {
        EventHandler<PlaybackSessionStateChangedEventArgs>[] handlers;
        lock (_sync)
        {
            if (_disposed || !ReferenceEquals(_current, snapshot))
            {
                return;
            }

            handlers = StateChanged?.GetInvocationList()
                .Cast<EventHandler<PlaybackSessionStateChangedEventArgs>>()
                .ToArray() ?? [];
        }

        var eventArgs = new PlaybackSessionStateChangedEventArgs(snapshot);
        foreach (EventHandler<PlaybackSessionStateChangedEventArgs> handler in handlers)
        {
            lock (_sync)
            {
                if (_disposed || !ReferenceEquals(_current, snapshot))
                {
                    break;
                }
            }

            try
            {
                handler.Invoke(this, eventArgs);
            }
            catch (Exception)
            {
                // Observer failures cannot mutate or stop playback lifecycle coordination.
            }
        }
    }

    private sealed class ReconnectContext
    {
        internal ReconnectContext(
            long generation,
            PlaybackReconnectCorrelationId correlationId,
            PlaybackSessionId sessionId,
            PlaybackSessionId physicalSessionId,
            PlaybackSelection selection)
        {
            Generation = generation;
            CorrelationId = correlationId;
            SessionId = sessionId;
            PhysicalSessionId = physicalSessionId;
            Selection = selection;
        }

        internal long Generation { get; }

        internal PlaybackReconnectCorrelationId CorrelationId { get; }

        internal PlaybackSessionId SessionId { get; }

        internal PlaybackSessionId PhysicalSessionId { get; set; }

        internal PlaybackSelection Selection { get; }

        internal int AttemptNumber { get; set; }

        internal bool AttemptInProgress { get; set; }

        internal bool IsTerminal { get; set; }

        internal bool ManualRetryStarting { get; set; }

        internal bool ManualRetryActive { get; set; }

        internal DomainError? AttemptFailure { get; set; }

        internal PlaybackState? RecoveredState { get; set; }

        internal TaskCompletionSource<PlaybackEngineOperationResult>? PlayableCompletion { get; set; }
    }

    private sealed class SessionLifetime : IDisposable
    {
        private readonly CancellationTokenSource _source = new();
        private readonly object _sync = new();
        private int _references = 1;
        private bool _retired;

        internal SessionOperationCancellation CreateOperation(CancellationToken callerToken)
        {
            lock (_sync)
            {
                if (_retired)
                {
                    throw new OperationCanceledException();
                }

                checked
                {
                    _references++;
                }

                try
                {
                    return new SessionOperationCancellation(
                        this,
                        CancellationTokenSource.CreateLinkedTokenSource(callerToken, _source.Token));
                }
                catch
                {
                    ReleaseReference();
                    throw;
                }
            }
        }

        internal void Retire()
        {
            lock (_sync)
            {
                if (_retired)
                {
                    return;
                }

                _retired = true;
            }

            try
            {
                _source.Cancel(throwOnFirstException: false);
            }
            catch (AggregateException)
            {
            }
            finally
            {
                ReleaseReference();
            }
        }

        public void Dispose() => Retire();

        internal void ReleaseOperation(CancellationTokenSource operationSource)
        {
            operationSource.Dispose();
            ReleaseReference();
        }

        private void ReleaseReference()
        {
            bool dispose;
            lock (_sync)
            {
                _references--;
                dispose = _references == 0;
            }

            if (dispose)
            {
                _source.Dispose();
            }
        }
    }

    internal sealed class SourceRetirementLease : IDisposable
    {
        private PlaybackSessionCoordinator? _owner;
        private readonly SourceId _sourceId;
        private readonly long _reservationId;

        internal SourceRetirementLease(
            PlaybackSessionCoordinator owner,
            SourceId sourceId,
            long reservationId)
        {
            _owner = owner;
            _sourceId = sourceId;
            _reservationId = reservationId;
        }

        internal void Commit()
        {
            PlaybackSessionCoordinator? owner = Interlocked.Exchange(ref _owner, null);
            owner?.CommitSourceRetirement(_sourceId, _reservationId);
        }

        public void Dispose()
        {
            PlaybackSessionCoordinator? owner = Interlocked.Exchange(ref _owner, null);
            owner?.RollbackSourceRetirement(_sourceId, _reservationId);
        }
    }

    private sealed class SourceRetirementState
    {
        internal HashSet<long> Reservations { get; } = [];

        internal bool IsPermanent { get; set; }
    }

    private sealed class SessionOperationCancellation : IDisposable
    {
        private SessionLifetime? _owner;
        private CancellationTokenSource? _source;

        internal SessionOperationCancellation(
            SessionLifetime owner,
            CancellationTokenSource source)
        {
            _owner = owner;
            _source = source;
        }

        internal CancellationToken Token =>
            Volatile.Read(ref _source)?.Token ?? throw new ObjectDisposedException(nameof(SessionOperationCancellation));

        public void Dispose()
        {
            SessionLifetime? owner = Interlocked.Exchange(ref _owner, null);
            CancellationTokenSource? source = Interlocked.Exchange(ref _source, null);
            if (owner is not null && source is not null)
            {
                owner.ReleaseOperation(source);
            }
        }
    }
}
