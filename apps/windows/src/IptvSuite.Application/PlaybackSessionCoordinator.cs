using IptvSuite.Domain;

namespace IptvSuite.Application;

public sealed class PlaybackSessionCoordinator : IAsyncDisposable
{
    private readonly IPlaybackEngine _engine;
    private readonly SemaphoreSlim _engineGate = new(1, 1);
    private readonly object _sync = new();
    private SessionLifetime? _currentLifetime;
    private PlaybackSessionId _engineSession;
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
    private Task? _disposeTask;
    private long _generation;
    private long _sessionSequence;
    private bool _disposed;

    public PlaybackSessionCoordinator(IPlaybackEngine engine)
    {
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        _engine.StateChanged += OnEngineStateChanged;
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

        try
        {
            lock (_sync)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                sessionId = NextSessionId();
                generation = checked(++_generation);
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

                _engineSession = sessionId;
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
            (currentSession, token) => _engine.SelectTrackAsync(currentSession, trackId, token),
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
        PlaybackSessionId sessionId;
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_currentSelection?.SourceId != sourceId ||
                _current.State is not (
                    PlaybackState.Opening or
                    PlaybackState.Buffering or
                    PlaybackState.Playing or
                    PlaybackState.Paused or
                    PlaybackState.Stopping or
                    PlaybackState.Failed))
            {
                return ValueTask.FromResult(PlaybackEngineOperationResult.Succeeded());
            }

            sessionId = _current.SessionId;
        }

        return new ValueTask<PlaybackEngineOperationResult>(
            StopSessionAsync(sessionId, requireCurrentSession: true));
    }

    public ValueTask DisposeAsync()
    {
        TaskCompletionSource? completion = null;
        SessionLifetime? lifetime = null;
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
        }

        lifetime?.Retire();
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
            if (_current.State is PlaybackState.Closed or PlaybackState.Stopping or PlaybackState.Failed ||
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
            if (!IsCurrent(generation, sessionId) || _engineSession != sessionId)
            {
                return PlaybackEngineOperationResult.Failed(DomainErrorCode.OperationCancelled);
            }

            PlaybackEngineOperationResult result = await InvokeEngineOperationAsync(
                token => command(sessionId, token),
                DomainErrorCode.StreamInterrupted,
                request.Token).ConfigureAwait(false);
            request.Token.ThrowIfCancellationRequested();
            if (!IsCurrent(generation, sessionId))
            {
                return PlaybackEngineOperationResult.Failed(DomainErrorCode.OperationCancelled);
            }

            if (!result.IsSuccess)
            {
                await StopEngineSessionUnderGateAsync(sessionId).ConfigureAwait(false);
                SetFailureIfCurrent(generation, sessionId, result.Error!);
                return result;
            }

            DomainError? terminalError = GetTerminalErrorIfCurrent(generation, sessionId);
            if (terminalError is not null)
            {
                await StopEngineSessionUnderGateAsync(sessionId).ConfigureAwait(false);
                return PlaybackEngineOperationResult.Failed(terminalError);
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

            if (_current.State is PlaybackState.Closed or PlaybackState.Stopping or PlaybackState.Failed ||
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
                    (_current.State is PlaybackState.Closed or PlaybackState.Stopping ||
                        _currentLifetime is null ||
                        canExecute is not null && !canExecute());
            }

            if (terminalBeforeDispatch is not null)
            {
                await StopEngineSessionUnderGateAsync(expectedSession).ConfigureAwait(false);
                return PlaybackEngineOperationResult.Failed(terminalBeforeDispatch);
            }

            if (invalidBeforeDispatch)
            {
                return PlaybackEngineOperationResult.Failed(DomainErrorCode.DomainInvariantViolation);
            }

            if (_engineSession != expectedSession)
            {
                return PlaybackEngineOperationResult.Failed(DomainErrorCode.OperationCancelled);
            }

            PlaybackEngineOperationResult result = await InvokeEngineOperationAsync(
                token => command(expectedSession, token),
                DomainErrorCode.PlaybackControlFailed,
                request.Token).ConfigureAwait(false);
            request.Token.ThrowIfCancellationRequested();

            DomainError? terminalError;
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
                if (terminalError is null && result.IsSuccess)
                {
                    applySuccessfulControlUnderLock();
                    applied = true;
                }
            }

            if (terminalError is not null)
            {
                await StopEngineSessionUnderGateAsync(expectedSession).ConfigureAwait(false);
                return PlaybackEngineOperationResult.Failed(terminalError);
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

            if (_current.State is PlaybackState.Closed or PlaybackState.Stopping or PlaybackState.Failed ||
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
                    (_current.State is PlaybackState.Closed or PlaybackState.Stopping ||
                        _currentLifetime is null);
            }

            if (terminalBeforeDispatch is not null)
            {
                await StopEngineSessionUnderGateAsync(expectedSession).ConfigureAwait(false);
                return DomainResult.Failure<PlaybackTrackSnapshot>(terminalBeforeDispatch);
            }

            if (invalidBeforeDispatch)
            {
                return DomainResult.Failure<PlaybackTrackSnapshot>(
                    DomainErrorCode.DomainInvariantViolation);
            }

            if (_engineSession != expectedSession)
            {
                return DomainResult.Failure<PlaybackTrackSnapshot>(DomainErrorCode.OperationCancelled);
            }

            DomainResult<PlaybackTrackSnapshot> result = await InvokeTrackQueryAsync(
                token => _engine.GetTracksAsync(expectedSession, token),
                request.Token).ConfigureAwait(false);
            request.Token.ThrowIfCancellationRequested();

            DomainError? terminalError;
            lock (_sync)
            {
                if (_disposed || generation != _generation || _current.SessionId != expectedSession)
                {
                    return DomainResult.Failure<PlaybackTrackSnapshot>(DomainErrorCode.OperationCancelled);
                }

                terminalError = _current.State == PlaybackState.Failed
                    ? _current.Error
                    : null;
                if (terminalError is null && result.IsSuccess)
                {
                    if (result.Value!.SessionId != expectedSession)
                    {
                        _currentTracks = null;
                        return DomainResult.Failure<PlaybackTrackSnapshot>(
                            DomainErrorCode.DomainInvariantViolation);
                    }

                    _currentTracks = result.Value;
                }
                else if (terminalError is null)
                {
                    _currentTracks = null;
                }
            }

            if (terminalError is not null)
            {
                await StopEngineSessionUnderGateAsync(expectedSession).ConfigureAwait(false);
                return DomainResult.Failure<PlaybackTrackSnapshot>(terminalError);
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
        try
        {
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
                _currentSelection = null;
                _current = PlaybackSessionSnapshot.Closed();
                _currentControls = PlaybackControlSnapshot.Idle(
                    _volume,
                    _isMuted,
                    _aspectMode);
                _currentTracks = null;
            }

            completion.TrySetResult();
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
        if (_engineSession != sessionId)
        {
            return PlaybackEngineOperationResult.Succeeded();
        }

        PlaybackEngineOperationResult result = await InvokeEngineOperationAsync(
            token => _engine.StopAsync(sessionId, token),
            DomainErrorCode.StreamInterrupted,
            CancellationToken.None).ConfigureAwait(false);
        if (result.IsSuccess)
        {
            _engineSession = default;
        }

        return result;
    }

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

    private void OnEngineStateChanged(
        object? sender,
        PlaybackEngineStateChangedEventArgs eventArgs)
    {
        ArgumentNullException.ThrowIfNull(eventArgs);
        PlaybackEngineSnapshot engineSnapshot = eventArgs.Snapshot;
        PlaybackSessionSnapshot? changed = null;

        lock (_sync)
        {
            if (_disposed ||
                _currentSelection is null ||
                engineSnapshot.SessionId != _current.SessionId ||
                !CanTransition(_current.State, engineSnapshot.State))
            {
                return;
            }

            if (engineSnapshot.State == PlaybackState.Closed)
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
                    engineSnapshot.SessionId,
                    _currentSelection,
                    engineSnapshot.Error!);
                _currentTracks = null;
            }
            else
            {
                _current = PlaybackSessionSnapshot.Active(
                    engineSnapshot.SessionId,
                    _currentSelection,
                    engineSnapshot.State);
            }

            changed = _current;
        }

        RaiseStateChanged(changed);
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
            return current is not PlaybackState.Closed and not PlaybackState.Failed;
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

    private void RaiseStateChanged(PlaybackSessionSnapshot snapshot) =>
        StateChanged?.Invoke(this, new PlaybackSessionStateChangedEventArgs(snapshot));

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
