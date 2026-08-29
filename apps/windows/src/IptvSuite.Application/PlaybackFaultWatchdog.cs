using System.Text.Json.Serialization;
using IptvSuite.Domain;

namespace IptvSuite.Application;

public sealed class PlaybackFaultWatchdogOptions
{
    public static readonly TimeSpan MinimumSupportedTimeout = TimeSpan.FromMilliseconds(100);

    public static readonly TimeSpan MaximumSupportedTimeout = TimeSpan.FromMinutes(2);

    public PlaybackFaultWatchdogOptions(
        TimeSpan startupTimeout,
        TimeSpan rebufferTimeout)
    {
        ValidateTimeout(startupTimeout, nameof(startupTimeout));
        ValidateTimeout(rebufferTimeout, nameof(rebufferTimeout));

        StartupTimeout = startupTimeout;
        RebufferTimeout = rebufferTimeout;
    }

    public TimeSpan StartupTimeout { get; }

    public TimeSpan RebufferTimeout { get; }

    public override string ToString() => "[PLAYBACK-FAULT-WATCHDOG-OPTIONS]";

    private static void ValidateTimeout(TimeSpan timeout, string parameterName)
    {
        if (timeout < MinimumSupportedTimeout || timeout > MaximumSupportedTimeout)
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                "A watchdog timeout must be within the supported bounded interval.");
        }
    }
}

[JsonConverter(typeof(JsonStringEnumConverter<PlaybackFaultWatchdogFailureKind>))]
public enum PlaybackFaultWatchdogFailureKind
{
    StartupTimeout,
    RebufferTimeout,
    SchedulerFailure,
}

public sealed class PlaybackFaultWatchdogExpiredEventArgs : EventArgs
{
    public PlaybackFaultWatchdogExpiredEventArgs(
        PlaybackSessionId sessionId,
        PlaybackFaultWatchdogFailureKind failureKind)
    {
        if (sessionId.IsEmpty)
        {
            throw new ArgumentException(
                "A playback session identifier is required.",
                nameof(sessionId));
        }

        if (!Enum.IsDefined(failureKind))
        {
            throw new ArgumentOutOfRangeException(nameof(failureKind));
        }

        SessionId = sessionId;
        FailureKind = failureKind;
        Error = DomainError.Create(failureKind switch
        {
            PlaybackFaultWatchdogFailureKind.StartupTimeout =>
                DomainErrorCode.PlaybackStartFailed,
            PlaybackFaultWatchdogFailureKind.RebufferTimeout =>
                DomainErrorCode.StreamInterrupted,
            PlaybackFaultWatchdogFailureKind.SchedulerFailure =>
                DomainErrorCode.DomainInvariantViolation,
            _ => DomainErrorCode.DomainInvariantViolation,
        });
    }

    public PlaybackSessionId SessionId { get; }

    public PlaybackFaultWatchdogFailureKind FailureKind { get; }

    public DomainError Error { get; }

    public override string ToString() =>
        $"[PLAYBACK-FAULT-WATCHDOG:{FailureKind}:{SessionId}:{Error.Code}]";
}

public sealed class PlaybackFaultWatchdog : IDisposable
{
    private readonly PlaybackFaultWatchdogOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly object _sync = new();
    private PlaybackSessionId _activeSessionId;
    private DeadlineRegistration? _activeDeadline;
    private long _generation;
    private long _deadlineOrdinal;
    private bool _playableObserved;
    private bool _terminalRaised;
    private bool _disposed;

    public PlaybackFaultWatchdog(
        PlaybackFaultWatchdogOptions options,
        TimeProvider timeProvider)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    public event EventHandler<PlaybackFaultWatchdogExpiredEventArgs>? Expired;

    public PlaybackFaultWatchdogOptions Options => _options;

    public void Observe(PlaybackEngineSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        long observationTimestamp = _timeProvider.GetTimestamp();
        DeadlineRegistration? deadlineToCancel = null;
        DeadlineRegistration? deadlineToStart = null;
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (snapshot.State is PlaybackState.Closed or
                PlaybackState.Stopping or PlaybackState.Failed)
            {
                if (!snapshot.SessionId.IsEmpty && snapshot.SessionId == _activeSessionId)
                {
                    deadlineToCancel = InvalidateGenerationLocked();
                }
            }
            else
            {
                if (snapshot.SessionId != _activeSessionId)
                {
                    deadlineToCancel = BeginGenerationLocked(snapshot.SessionId);
                }

                if (!_terminalRaised)
                {
                    switch (snapshot.State)
                    {
                        case PlaybackState.Opening:
                            if (!_playableObserved && _activeDeadline is null)
                            {
                                deadlineToStart = ArmDeadlineLocked(
                                    PlaybackFaultWatchdogFailureKind.StartupTimeout,
                                    _options.StartupTimeout,
                                    observationTimestamp);
                            }

                            break;

                        case PlaybackState.Buffering:
                            if (_activeDeadline is null)
                            {
                                deadlineToStart = ArmDeadlineLocked(
                                    _playableObserved
                                        ? PlaybackFaultWatchdogFailureKind.RebufferTimeout
                                        : PlaybackFaultWatchdogFailureKind.StartupTimeout,
                                    _playableObserved
                                        ? _options.RebufferTimeout
                                        : _options.StartupTimeout,
                                    observationTimestamp);
                            }

                            break;

                        case PlaybackState.Playing:
                            _playableObserved = true;
                            deadlineToCancel ??= DetachDeadlineLocked();
                            break;

                        case PlaybackState.Paused:
                            // Paused is only proof of a healthy session after Playing was observed.
                            if (_playableObserved)
                            {
                                deadlineToCancel ??= DetachDeadlineLocked();
                            }
                            else if (_activeDeadline is null)
                            {
                                deadlineToStart = ArmDeadlineLocked(
                                    PlaybackFaultWatchdogFailureKind.StartupTimeout,
                                    _options.StartupTimeout,
                                    observationTimestamp);
                            }

                            break;
                    }
                }
            }
        }

        deadlineToCancel?.Cancel();
        if (deadlineToStart is not null)
        {
            _ = RunDeadlineAsync(deadlineToStart);
        }
    }

    public bool Cancel(PlaybackSessionId sessionId)
    {
        if (sessionId.IsEmpty)
        {
            throw new ArgumentException(
                "A playback session identifier is required.",
                nameof(sessionId));
        }

        DeadlineRegistration? deadlineToCancel;
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_activeSessionId != sessionId || _terminalRaised)
            {
                return false;
            }

            deadlineToCancel = InvalidateGenerationLocked();
        }

        deadlineToCancel?.Cancel();
        return true;
    }

    public void Dispose()
    {
        DeadlineRegistration? deadlineToCancel;
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            deadlineToCancel = InvalidateGenerationLocked();
        }

        deadlineToCancel?.Cancel();
    }

    private DeadlineRegistration? BeginGenerationLocked(PlaybackSessionId sessionId)
    {
        DeadlineRegistration? deadline = DetachDeadlineLocked();
        checked
        {
            _generation++;
        }

        _activeSessionId = sessionId;
        _playableObserved = false;
        _terminalRaised = false;
        return deadline;
    }

    private DeadlineRegistration? InvalidateGenerationLocked()
    {
        DeadlineRegistration? deadline = DetachDeadlineLocked();
        checked
        {
            _generation++;
        }

        _activeSessionId = default;
        _playableObserved = false;
        _terminalRaised = false;
        return deadline;
    }

    private DeadlineRegistration ArmDeadlineLocked(
        PlaybackFaultWatchdogFailureKind failureKind,
        TimeSpan timeout,
        long startTimestamp)
    {
        checked
        {
            _deadlineOrdinal++;
        }

        var registration = new DeadlineRegistration(
            _generation,
            _deadlineOrdinal,
            _activeSessionId,
            failureKind,
            timeout,
            startTimestamp);
        _activeDeadline = registration;
        return registration;
    }

    private DeadlineRegistration? DetachDeadlineLocked()
    {
        checked
        {
            _deadlineOrdinal++;
        }

        DeadlineRegistration? deadline = _activeDeadline;
        _activeDeadline = null;
        return deadline;
    }

    private async Task RunDeadlineAsync(DeadlineRegistration registration)
    {
        try
        {
            while (true)
            {
                if (!IsCurrent(registration))
                {
                    return;
                }

                TimeSpan elapsed = _timeProvider.GetElapsedTime(
                    registration.StartTimestamp,
                    _timeProvider.GetTimestamp());
                if (elapsed < TimeSpan.Zero)
                {
                    PublishTerminal(
                        registration,
                        PlaybackFaultWatchdogFailureKind.SchedulerFailure);
                    return;
                }

                TimeSpan remaining = registration.Timeout - elapsed;
                if (remaining <= TimeSpan.Zero)
                {
                    PublishTerminal(registration, registration.FailureKind);
                    return;
                }

                await Task.Delay(
                    remaining,
                    _timeProvider,
                    registration.CancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (registration.IsCancellationRequested)
        {
        }
        catch (Exception)
        {
            PublishTerminal(
                registration,
                PlaybackFaultWatchdogFailureKind.SchedulerFailure);
        }
        finally
        {
            registration.Dispose();
        }
    }

    private bool IsCurrent(DeadlineRegistration registration)
    {
        lock (_sync)
        {
            return !_disposed &&
                registration.Generation == _generation &&
                registration.DeadlineOrdinal == _deadlineOrdinal &&
                ReferenceEquals(registration, _activeDeadline) &&
                registration.SessionId == _activeSessionId &&
                !_terminalRaised;
        }
    }

    private void PublishTerminal(
        DeadlineRegistration registration,
        PlaybackFaultWatchdogFailureKind failureKind)
    {
        PlaybackFaultWatchdogExpiredEventArgs? eventArgs = null;
        EventHandler<PlaybackFaultWatchdogExpiredEventArgs>[] handlers = [];
        lock (_sync)
        {
            if (_disposed ||
                registration.Generation != _generation ||
                registration.DeadlineOrdinal != _deadlineOrdinal ||
                !ReferenceEquals(registration, _activeDeadline) ||
                registration.SessionId != _activeSessionId ||
                _terminalRaised)
            {
                return;
            }

            _terminalRaised = true;
            _activeDeadline = null;
            eventArgs = new PlaybackFaultWatchdogExpiredEventArgs(
                registration.SessionId,
                failureKind);
            handlers = Expired?.GetInvocationList()
                .Cast<EventHandler<PlaybackFaultWatchdogExpiredEventArgs>>()
                .ToArray() ?? [];
        }

        foreach (EventHandler<PlaybackFaultWatchdogExpiredEventArgs> handler in handlers)
        {
            try
            {
                handler.Invoke(this, eventArgs);
            }
            catch (Exception)
            {
                // Observer failures cannot mutate or duplicate the terminal watchdog event.
            }
        }
    }

    private sealed class DeadlineRegistration(
        long generation,
        long deadlineOrdinal,
        PlaybackSessionId sessionId,
        PlaybackFaultWatchdogFailureKind failureKind,
        TimeSpan timeout,
        long startTimestamp) : IDisposable
    {
        private readonly CancellationTokenSource _cancellation = new();

        internal long Generation { get; } = generation;

        internal long DeadlineOrdinal { get; } = deadlineOrdinal;

        internal PlaybackSessionId SessionId { get; } = sessionId;

        internal PlaybackFaultWatchdogFailureKind FailureKind { get; } = failureKind;

        internal TimeSpan Timeout { get; } = timeout;

        internal long StartTimestamp { get; } = startTimestamp;

        internal CancellationToken CancellationToken => _cancellation.Token;

        internal bool IsCancellationRequested => _cancellation.IsCancellationRequested;

        internal void Cancel()
        {
            try
            {
                _cancellation.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
        }

        public void Dispose() => _cancellation.Dispose();
    }
}
