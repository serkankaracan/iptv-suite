using IptvSuite.Domain;

namespace IptvSuite.Application;

public sealed class PlaybackReconnectOrchestrator : IAsyncDisposable
{
    public static readonly TimeSpan MaximumCountdownTick = TimeSpan.FromSeconds(1);

    private readonly PlaybackReconnectPolicy _policy;
    private readonly TimeProvider _timeProvider;
    private readonly PlaybackReconnectJitterSource _jitterSource;
    private readonly PlaybackReconnectAttemptExecutor _attemptExecutor;
    private readonly SemaphoreSlim _attemptGate = new(1, 1);
    private readonly object _sync = new();
    private readonly HashSet<ReconnectChain> _runningChains = [];
    private readonly Queue<QueuedSnapshot> _queuedSnapshots = [];
    private readonly TaskCompletionSource<bool> _disposeCompletion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private PlaybackReconnectSnapshot _current = PlaybackReconnectSnapshot.Idle();
    private ReconnectChain? _activeChain;
    private long _generation;
    private bool _dispatchingSnapshots;
    private bool _disposeFinalized;
    private bool _disposed;

    public PlaybackReconnectOrchestrator(
        PlaybackReconnectPolicy policy,
        TimeProvider timeProvider,
        PlaybackReconnectJitterSource jitterSource,
        PlaybackReconnectAttemptExecutor attemptExecutor)
    {
        _policy = policy ?? throw new ArgumentNullException(nameof(policy));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _jitterSource = jitterSource ?? throw new ArgumentNullException(nameof(jitterSource));
        _attemptExecutor = attemptExecutor ?? throw new ArgumentNullException(nameof(attemptExecutor));

        ValidateExactPolicy(policy.Options);
    }

    public event EventHandler<PlaybackReconnectSnapshotChangedEventArgs>? SnapshotChanged;

    public PlaybackReconnectSnapshot Current
    {
        get
        {
            lock (_sync)
            {
                return _current;
            }
        }
    }

    public Task<PlaybackReconnectSnapshot> BeginAsync(
        PlaybackReconnectCorrelationId correlationId,
        DomainError failure)
    {
        ValidateCorrelationId(correlationId);
        ArgumentNullException.ThrowIfNull(failure);

        ReconnectChain? replaced;
        ReconnectChain chain;
        PlaybackReconnectSnapshot initial;
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (CanCoalesceLocked(correlationId))
            {
                return _activeChain!.Completion.Task;
            }

            replaced = _activeChain;
            CompleteInvalidatedLocked(replaced);
            long generation = checked(_generation + 1);
            initial = PlaybackReconnectSnapshot.Active(
                PlaybackReconnectPhase.Evaluating,
                correlationId,
                attemptNumber: 0,
                TimeSpan.Zero,
                _policy.Options.TotalBudget);
            chain = new ReconnectChain(
                generation,
                correlationId,
                failure,
                isManual: false);
            _generation = generation;
            _activeChain = chain;
            _current = initial;
            _runningChains.Add(chain);
            QueueSnapshotLocked(chain, initial);
        }

        replaced?.CancelSafely();
        DrainSnapshotEvents();
        StartRunner(chain);
        return chain.Completion.Task;
    }

    public Task<PlaybackReconnectSnapshot> RetryNowAsync(
        PlaybackReconnectCorrelationId correlationId)
    {
        ValidateCorrelationId(correlationId);

        ReconnectChain replaced;
        ReconnectChain chain;
        PlaybackReconnectSnapshot initial;
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_activeChain is null || _activeChain.CorrelationId != correlationId)
            {
                throw new InvalidOperationException(
                    "Manual reconnect requires the exact terminal correlation.");
            }

            if (!_activeChain.Completion.Task.IsCompleted)
            {
                return _activeChain.Completion.Task;
            }

            ValidateManualTerminal(correlationId);

            replaced = _activeChain;
            CompleteInvalidatedLocked(replaced);
            long generation = checked(_generation + 1);
            initial = PlaybackReconnectSnapshot.Active(
                PlaybackReconnectPhase.Evaluating,
                correlationId,
                attemptNumber: 0,
                TimeSpan.Zero,
                _policy.Options.TotalBudget);
            chain = new ReconnectChain(
                generation,
                correlationId,
                initialFailure: null,
                isManual: true);
            _generation = generation;
            _activeChain = chain;
            _current = initial;
            _runningChains.Add(chain);
            QueueSnapshotLocked(chain, initial);
        }

        replaced.CancelSafely();
        DrainSnapshotEvents();
        StartRunner(chain);
        return chain.Completion.Task;
    }

    public bool Cancel(PlaybackReconnectCorrelationId correlationId)
    {
        ValidateCorrelationId(correlationId);
        ReconnectChain? chain;
        PlaybackReconnectSnapshot? cancelled = null;
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            chain = _activeChain;
            if (chain is null ||
                chain.CorrelationId != correlationId ||
                chain.Completion.Task.IsCompleted)
            {
                return false;
            }

            checked
            {
                _generation++;
            }

            cancelled = PlaybackReconnectSnapshot.Terminal(
                PlaybackReconnectPhase.Cancelled,
                correlationId,
                _current.AttemptNumber,
                TimeSpan.Zero,
                DomainErrorCode.OperationCancelled);
            _current = cancelled;
            chain.Completion.TrySetResult(cancelled);
            QueueSnapshotLocked(chain: null, cancelled);
        }

        chain.CancelSafely();
        DrainSnapshotEvents();
        return true;
    }

    public ValueTask DisposeAsync()
    {
        ReconnectChain[] chains;
        PlaybackReconnectSnapshot? cancelled = null;
        lock (_sync)
        {
            if (_disposed)
            {
                return new ValueTask(_disposeCompletion.Task);
            }

            _disposed = true;
            checked
            {
                _generation++;
            }

            chains = _runningChains.ToArray();
            _queuedSnapshots.Clear();
            if (_activeChain is not null && !_activeChain.Completion.Task.IsCompleted)
            {
                cancelled = PlaybackReconnectSnapshot.Terminal(
                    PlaybackReconnectPhase.Cancelled,
                    _activeChain.CorrelationId,
                    _current.AttemptNumber,
                    TimeSpan.Zero,
                    DomainErrorCode.OperationCancelled);
                _current = cancelled;
                _activeChain.Completion.TrySetResult(cancelled);
            }

            _activeChain = null;
        }

        foreach (ReconnectChain chain in chains)
        {
            chain.CancelSafely();
        }

        CompleteDisposeIfDrained();

        return new ValueTask(_disposeCompletion.Task);
    }

    public override string ToString() => "[PLAYBACK-RECONNECT-ORCHESTRATOR]";

    private static void ValidateExactPolicy(PlaybackReconnectPolicyOptions options)
    {
        bool exact = options.MaximumAttempts == PlaybackReconnectPolicyOptions.MaximumAllowedAttempts &&
            options.TotalBudget == PlaybackReconnectPolicyOptions.MaximumAllowedTotalBudget &&
            options.MaximumJitter == PlaybackReconnectPolicyOptions.MaximumAllowedJitter &&
            options.BaseDelays.SequenceEqual(
                [TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4)]);
        if (!exact)
        {
            throw new ArgumentException(
                "Playback reconnect orchestration requires the exact bounded policy.",
                nameof(options));
        }
    }

    private static void ValidateCorrelationId(PlaybackReconnectCorrelationId correlationId)
    {
        if (correlationId.IsEmpty)
        {
            throw new ArgumentException(
                "A playback reconnect correlation identifier is required.",
                nameof(correlationId));
        }
    }

    private void ValidateManualTerminal(PlaybackReconnectCorrelationId correlationId)
    {
        if (_current.CorrelationId != correlationId ||
            _current.Phase is PlaybackReconnectPhase.Idle or
                PlaybackReconnectPhase.Evaluating or
                PlaybackReconnectPhase.Waiting or
                PlaybackReconnectPhase.Attempting or
                PlaybackReconnectPhase.Succeeded)
        {
            throw new InvalidOperationException(
                "Manual reconnect requires a retryable terminal phase.");
        }
    }

    private void StartRunner(ReconnectChain chain)
    {
        chain.Runner = RunChainAsync(chain);
    }

    private async Task RunChainAsync(ReconnectChain chain)
    {
        CancellationTokenSource? deadline = null;
        CancellationTokenSource? deadlineSchedulerStop = null;
        CancellationTokenSource? linked = null;
        Task? deadlineScheduler = null;
        try
        {
            if (!IsCurrent(chain))
            {
                return;
            }

            chain.StartBudget(_timeProvider.GetTimestamp());
            if (!IsCurrent(chain))
            {
                return;
            }

            TimeSpan remainingBudget = GetRemainingBudget(chain);
            if (remainingBudget <= TimeSpan.Zero)
            {
                CompleteExhausted(chain, attemptNumber: 0);
                return;
            }

            deadline = new CancellationTokenSource();
            deadlineSchedulerStop = new CancellationTokenSource();
            linked = CancellationTokenSource.CreateLinkedTokenSource(
                chain.Cancellation.Token,
                deadline.Token);
            deadlineScheduler = RunOwnedDeadlineAsync(
                deadline,
                remainingBudget,
                deadlineSchedulerStop.Token);

            int completedAttempts = 0;
            DomainError? failure = chain.InitialFailure;
            if (chain.IsManual)
            {
                PlaybackEngineOperationResult manualResult = await ExecuteAttemptAsync(
                    chain,
                    attemptNumber: 1,
                    deadline,
                    linked.Token).ConfigureAwait(false);
                completedAttempts = 1;
                if (!IsCurrent(chain))
                {
                    return;
                }

                if (IsAtOrAfterDeadline(chain, deadline))
                {
                    CompleteExhausted(chain, completedAttempts);
                    return;
                }

                if (manualResult.IsSuccess)
                {
                    CompleteSucceeded(chain, completedAttempts, deadline);
                    return;
                }

                failure = manualResult.Error ?? DomainError.Create(
                    DomainErrorCode.DomainInvariantViolation);
            }

            while (failure is not null)
            {
                if (!IsCurrent(chain))
                {
                    return;
                }

                TimeSpan elapsed = GetElapsed(chain.StartTimestamp);
                PlaybackReconnectDecision decision = EvaluateDecision(
                    failure,
                    completedAttempts,
                    elapsed);
                if (decision.Kind == PlaybackReconnectDecisionKind.DoNotRetry)
                {
                    CompletePolicyTerminal(
                        chain,
                        completedAttempts,
                        decision.TerminalErrorCode ?? DomainErrorCode.DomainInvariantViolation);
                    return;
                }

                if (decision.Kind == PlaybackReconnectDecisionKind.Exhausted)
                {
                    CompleteExhausted(chain, completedAttempts);
                    return;
                }

                await WaitForDelayAsync(chain, decision, linked.Token).ConfigureAwait(false);
                if (!IsCurrent(chain))
                {
                    return;
                }

                if (IsAtOrAfterDeadline(chain, deadline))
                {
                    CompleteExhausted(chain, completedAttempts);
                    return;
                }

                PlaybackEngineOperationResult result = await ExecuteAttemptAsync(
                    chain,
                    decision.NextAttemptNumber,
                    deadline,
                    linked.Token).ConfigureAwait(false);
                completedAttempts = decision.NextAttemptNumber;
                if (!IsCurrent(chain))
                {
                    return;
                }

                if (IsAtOrAfterDeadline(chain, deadline))
                {
                    CompleteExhausted(chain, completedAttempts);
                    return;
                }

                if (result.IsSuccess)
                {
                    CompleteSucceeded(chain, completedAttempts, deadline);
                    return;
                }

                failure = result.Error ?? DomainError.Create(
                    DomainErrorCode.DomainInvariantViolation);
            }
        }
        catch (OperationCanceledException) when (
            chain.Cancellation.IsCancellationRequested || !IsCurrent(chain))
        {
            CompleteCancelledIfCurrent(chain);
        }
        catch (PlaybackReconnectDeadlineException)
        {
            CompleteExhausted(chain, GetCurrentAttempt(chain));
        }
        catch (OperationCanceledException) when (deadline?.IsCancellationRequested == true)
        {
            CompleteExhausted(chain, GetCurrentAttempt(chain));
        }
        catch (Exception)
        {
            if (deadline is not null && IsAtOrAfterDeadlineSafely(chain, deadline))
            {
                CompleteExhausted(chain, GetCurrentAttempt(chain));
            }
            else
            {
                CompleteDoNotRetry(
                    chain,
                    GetCurrentAttempt(chain),
                    DomainErrorCode.DomainInvariantViolation);
            }
        }
        finally
        {
            CancelSourceSafely(deadlineSchedulerStop);
            if (deadlineScheduler is not null)
            {
                try
                {
                    await deadlineScheduler.ConfigureAwait(false);
                }
                catch (Exception)
                {
                    // Owned deadline scheduling is fail-closed and cannot escape cleanup.
                }
            }

            try
            {
                linked?.Dispose();
                deadline?.Dispose();
                deadlineSchedulerStop?.Dispose();
                chain.Cancellation.Dispose();
            }
            catch (Exception)
            {
                // Owned cancellation cleanup cannot fault or strand chain completion.
            }
            finally
            {
                CompleteRunner(chain);
            }
        }
    }

    private PlaybackReconnectDecision EvaluateDecision(
        DomainError failure,
        int completedAttempts,
        TimeSpan elapsed)
    {
        PlaybackReconnectDecision eligibility = _policy.Evaluate(
            failure,
            completedAttempts,
            elapsed,
            TimeSpan.Zero);
        if (eligibility.Kind != PlaybackReconnectDecisionKind.RetryAfterDelay)
        {
            return eligibility;
        }

        TimeSpan jitter = _jitterSource(eligibility.NextAttemptNumber);
        return _policy.Evaluate(failure, completedAttempts, elapsed, jitter);
    }

    private async Task WaitForDelayAsync(
        ReconnectChain chain,
        PlaybackReconnectDecision decision,
        CancellationToken cancellationToken)
    {
        long delayStart = _timeProvider.GetTimestamp();
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsCurrent(chain))
            {
                throw new OperationCanceledException(cancellationToken);
            }

            TimeSpan delayElapsed = GetElapsed(delayStart);
            TimeSpan remaining = decision.Delay - delayElapsed;
            if (remaining <= TimeSpan.Zero)
            {
                return;
            }

            TimeSpan budget = GetRemainingBudget(chain);
            if (budget <= TimeSpan.Zero)
            {
                throw new PlaybackReconnectDeadlineException();
            }

            if (remaining >= budget)
            {
                throw new PlaybackReconnectDeadlineException();
            }

            cancellationToken.ThrowIfCancellationRequested();

            PlaybackReconnectSnapshot waiting = PlaybackReconnectSnapshot.Active(
                PlaybackReconnectPhase.Waiting,
                chain.CorrelationId,
                decision.NextAttemptNumber,
                remaining,
                budget);
            if (!TrySetCurrent(chain, waiting))
            {
                throw new OperationCanceledException(cancellationToken);
            }

            TimeSpan tick = remaining < MaximumCountdownTick
                ? remaining
                : MaximumCountdownTick;
            await Task.Delay(tick, _timeProvider, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<PlaybackEngineOperationResult> ExecuteAttemptAsync(
        ReconnectChain chain,
        int attemptNumber,
        CancellationTokenSource deadline,
        CancellationToken cancellationToken)
    {
        bool entered = false;
        try
        {
            await _attemptGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            entered = true;
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsCurrent(chain) || IsAtOrAfterDeadline(chain, deadline))
            {
                if (!IsCurrent(chain))
                {
                    throw new OperationCanceledException(cancellationToken);
                }

                throw new PlaybackReconnectDeadlineException();
            }

            TimeSpan remainingBudget = GetRemainingBudget(chain);
            if (remainingBudget <= TimeSpan.Zero || IsAtOrAfterDeadline(chain, deadline))
            {
                throw new PlaybackReconnectDeadlineException();
            }

            PlaybackReconnectSnapshot attempting = PlaybackReconnectSnapshot.Active(
                PlaybackReconnectPhase.Attempting,
                chain.CorrelationId,
                attemptNumber,
                TimeSpan.Zero,
                remainingBudget);
            if (!TrySetCurrent(chain, attempting))
            {
                throw new OperationCanceledException(cancellationToken);
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (!IsCurrent(chain) || IsAtOrAfterDeadline(chain, deadline))
            {
                if (!IsCurrent(chain))
                {
                    throw new OperationCanceledException(cancellationToken);
                }

                throw new PlaybackReconnectDeadlineException();
            }

            PlaybackEngineOperationResult? result = await _attemptExecutor(
                chain.CorrelationId,
                attemptNumber,
                cancellationToken).ConfigureAwait(false);
            return result ?? PlaybackEngineOperationResult.Failed(
                DomainErrorCode.DomainInvariantViolation);
        }
        finally
        {
            if (entered)
            {
                _attemptGate.Release();
            }
        }
    }

    private bool IsAtOrAfterDeadline(
        ReconnectChain chain,
        CancellationTokenSource deadline) =>
        deadline.IsCancellationRequested ||
        GetElapsed(chain.StartTimestamp) >= _policy.Options.TotalBudget;

    private TimeSpan GetElapsed(long startTimestamp)
    {
        TimeSpan elapsed = _timeProvider.GetElapsedTime(startTimestamp, _timeProvider.GetTimestamp());
        if (elapsed < TimeSpan.Zero)
        {
            throw new InvalidOperationException("The monotonic time provider moved backwards.");
        }

        return elapsed;
    }

    private TimeSpan GetRemainingBudget(ReconnectChain chain)
    {
        if (!chain.HasStartedBudget)
        {
            throw new InvalidOperationException("The reconnect budget has not started.");
        }

        TimeSpan remaining = _policy.Options.TotalBudget - GetElapsed(chain.StartTimestamp);
        return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
    }

    private TimeSpan GetSafeRemainingBudget(ReconnectChain chain)
    {
        try
        {
            return GetRemainingBudget(chain);
        }
        catch (Exception)
        {
            return TimeSpan.Zero;
        }
    }

    private bool IsAtOrAfterDeadlineSafely(
        ReconnectChain chain,
        CancellationTokenSource deadline)
    {
        try
        {
            return IsAtOrAfterDeadline(chain, deadline);
        }
        catch (Exception)
        {
            return deadline.IsCancellationRequested;
        }
    }

    private async Task RunOwnedDeadlineAsync(
        CancellationTokenSource deadline,
        TimeSpan delay,
        CancellationToken stopToken)
    {
        try
        {
            await Task.Delay(delay, _timeProvider, stopToken).ConfigureAwait(false);
            CancelSourceSafely(deadline);
        }
        catch (OperationCanceledException) when (stopToken.IsCancellationRequested)
        {
            // The runner completed before its owned deadline.
        }
        catch (Exception)
        {
            // Timer/provider failure closes the chain through the deadline token.
            CancelSourceSafely(deadline);
        }
    }

    private static void CancelSourceSafely(CancellationTokenSource? source)
    {
        if (source is null)
        {
            return;
        }

        try
        {
            source.Cancel(throwOnFirstException: false);
        }
        catch (ObjectDisposedException)
        {
            // The owning runner can win cleanup.
        }
        catch (AggregateException)
        {
            // Cancellation callbacks cannot escape owned scheduling.
        }
    }

    private int GetCurrentAttempt(ReconnectChain chain)
    {
        lock (_sync)
        {
            return ReferenceEquals(_activeChain, chain) && _generation == chain.Generation
                ? _current.AttemptNumber
                : 0;
        }
    }

    private bool IsCurrent(ReconnectChain chain)
    {
        lock (_sync)
        {
            return !_disposed &&
                ReferenceEquals(_activeChain, chain) &&
                _generation == chain.Generation;
        }
    }

    private bool TrySetCurrent(
        ReconnectChain chain,
        PlaybackReconnectSnapshot snapshot,
        bool complete = false)
    {
        lock (_sync)
        {
            if (_disposed ||
                !ReferenceEquals(_activeChain, chain) ||
                _generation != chain.Generation)
            {
                return false;
            }

            _current = snapshot;
            if (complete)
            {
                chain.Completion.TrySetResult(snapshot);
            }

            QueueSnapshotLocked(chain, snapshot);
        }

        DrainSnapshotEvents();
        return true;
    }

    private void CompleteSucceeded(
        ReconnectChain chain,
        int attemptNumber,
        CancellationTokenSource deadline)
    {
        TimeSpan remainingBudget;
        try
        {
            remainingBudget = GetRemainingBudget(chain);
        }
        catch (Exception)
        {
            if (deadline.IsCancellationRequested)
            {
                CompleteExhausted(chain, attemptNumber);
            }
            else
            {
                CompleteDoNotRetry(
                    chain,
                    attemptNumber,
                    DomainErrorCode.DomainInvariantViolation);
            }

            return;
        }

        PlaybackReconnectSnapshot succeeded = PlaybackReconnectSnapshot.Terminal(
            PlaybackReconnectPhase.Succeeded,
            chain.CorrelationId,
            attemptNumber,
            remainingBudget,
            terminalErrorCode: null);
        PlaybackReconnectSnapshot exhausted = PlaybackReconnectSnapshot.Terminal(
            PlaybackReconnectPhase.Exhausted,
            chain.CorrelationId,
            attemptNumber,
            TimeSpan.Zero,
            DomainErrorCode.ReconnectExhausted);

        lock (_sync)
        {
            if (_disposed ||
                !ReferenceEquals(_activeChain, chain) ||
                _generation != chain.Generation)
            {
                return;
            }

            PlaybackReconnectSnapshot terminal =
                remainingBudget <= TimeSpan.Zero || deadline.IsCancellationRequested
                    ? exhausted
                    : succeeded;
            _current = terminal;
            chain.Completion.TrySetResult(terminal);
            QueueSnapshotLocked(chain, terminal);
        }

        DrainSnapshotEvents();
    }

    private void CompletePolicyTerminal(
        ReconnectChain chain,
        int attemptNumber,
        DomainErrorCode terminalErrorCode)
    {
        if (terminalErrorCode == DomainErrorCode.OperationCancelled)
        {
            CompleteCancelledIfCurrent(chain);
            return;
        }

        if (terminalErrorCode == DomainErrorCode.ReconnectExhausted)
        {
            CompleteExhausted(chain, attemptNumber);
            return;
        }

        CompleteDoNotRetry(chain, attemptNumber, terminalErrorCode);
    }

    private void CompleteDoNotRetry(
        ReconnectChain chain,
        int attemptNumber,
        DomainErrorCode terminalErrorCode)
    {
        PlaybackReconnectSnapshot snapshot = PlaybackReconnectSnapshot.Terminal(
            PlaybackReconnectPhase.DoNotRetry,
            chain.CorrelationId,
            attemptNumber,
            GetSafeRemainingBudget(chain),
            terminalErrorCode);
        TrySetCurrent(chain, snapshot, complete: true);
    }

    private void CompleteExhausted(ReconnectChain chain, int attemptNumber)
    {
        PlaybackReconnectSnapshot snapshot = PlaybackReconnectSnapshot.Terminal(
            PlaybackReconnectPhase.Exhausted,
            chain.CorrelationId,
            attemptNumber,
            GetSafeRemainingBudget(chain),
            DomainErrorCode.ReconnectExhausted);
        TrySetCurrent(chain, snapshot, complete: true);
    }

    private void CompleteCancelledIfCurrent(ReconnectChain chain)
    {
        PlaybackReconnectSnapshot snapshot = PlaybackReconnectSnapshot.Terminal(
            PlaybackReconnectPhase.Cancelled,
            chain.CorrelationId,
            GetCurrentAttempt(chain),
            GetSafeRemainingBudget(chain),
            DomainErrorCode.OperationCancelled);
        TrySetCurrent(chain, snapshot, complete: true);
    }

    private void QueueSnapshotLocked(
        ReconnectChain? chain,
        PlaybackReconnectSnapshot snapshot) =>
        _queuedSnapshots.Enqueue(new QueuedSnapshot(_generation, chain, snapshot));

    private bool IsQueuedSnapshotCurrentLocked(QueuedSnapshot queued) =>
        !_disposed &&
        queued.Generation == _generation &&
        (queued.Chain is null ||
            (ReferenceEquals(queued.Chain, _activeChain) &&
                queued.Chain.Generation == queued.Generation));

    private void DrainSnapshotEvents()
    {
        bool retry;
        do
        {
            lock (_sync)
            {
                if (_disposed || _dispatchingSnapshots)
                {
                    return;
                }

                _dispatchingSnapshots = true;
            }

            try
            {
                while (true)
                {
                    QueuedSnapshot? queued = null;
                    EventHandler<PlaybackReconnectSnapshotChangedEventArgs>[] handlers = [];
                    lock (_sync)
                    {
                        while (_queuedSnapshots.TryDequeue(out QueuedSnapshot? candidate))
                        {
                            if (IsQueuedSnapshotCurrentLocked(candidate))
                            {
                                queued = candidate;
                                break;
                            }
                        }

                        if (queued is null)
                        {
                            break;
                        }

                        handlers = SnapshotChanged?.GetInvocationList()
                            .Cast<EventHandler<PlaybackReconnectSnapshotChangedEventArgs>>()
                            .ToArray() ?? [];
                    }

                    var eventArgs = new PlaybackReconnectSnapshotChangedEventArgs(queued.Snapshot);
                    foreach (EventHandler<PlaybackReconnectSnapshotChangedEventArgs> handler in handlers)
                    {
                        lock (_sync)
                        {
                            if (!IsQueuedSnapshotCurrentLocked(queued))
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
                            // Observer failures cannot mutate or stop the reconnect chain.
                        }
                    }
                }
            }
            finally
            {
                lock (_sync)
                {
                    _dispatchingSnapshots = false;
                    retry = !_disposed && _queuedSnapshots.Count > 0;
                }

                CompleteDisposeIfDrained();
            }
        }
        while (retry);
    }

    private bool CanCoalesceLocked(PlaybackReconnectCorrelationId correlationId) =>
        _activeChain is not null &&
        _activeChain.CorrelationId == correlationId &&
        !_activeChain.Completion.Task.IsCompleted &&
        !_current.IsTerminal;

    private void CompleteInvalidatedLocked(ReconnectChain? chain)
    {
        if (chain is null || chain.Completion.Task.IsCompleted)
        {
            return;
        }

        PlaybackReconnectSnapshot cancelled = PlaybackReconnectSnapshot.Terminal(
            PlaybackReconnectPhase.Cancelled,
            chain.CorrelationId,
            ReferenceEquals(chain, _activeChain) ? _current.AttemptNumber : 0,
            TimeSpan.Zero,
            DomainErrorCode.OperationCancelled);
        chain.Completion.TrySetResult(cancelled);
    }

    private void CompleteRunner(ReconnectChain chain)
    {
        lock (_sync)
        {
            _runningChains.Remove(chain);
        }

        CompleteDisposeIfDrained();
    }

    private void CompleteDisposeIfDrained()
    {
        bool finalize;
        lock (_sync)
        {
            finalize = _disposed &&
                !_disposeFinalized &&
                _runningChains.Count == 0 &&
                !_dispatchingSnapshots;
            if (finalize)
            {
                _disposeFinalized = true;
            }
        }

        if (finalize)
        {
            _attemptGate.Dispose();
            _disposeCompletion.TrySetResult(true);
        }
    }

    private sealed class ReconnectChain
    {
        internal ReconnectChain(
            long generation,
            PlaybackReconnectCorrelationId correlationId,
            DomainError? initialFailure,
            bool isManual)
        {
            Generation = generation;
            CorrelationId = correlationId;
            InitialFailure = initialFailure;
            IsManual = isManual;
        }

        internal long Generation { get; }

        internal PlaybackReconnectCorrelationId CorrelationId { get; }

        internal long StartTimestamp { get; private set; }

        internal bool HasStartedBudget => Volatile.Read(ref _budgetStarted) != 0;

        internal DomainError? InitialFailure { get; }

        internal bool IsManual { get; }

        internal CancellationTokenSource Cancellation { get; } = new();

        internal TaskCompletionSource<PlaybackReconnectSnapshot> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal Task? Runner { get; set; }

        private int _budgetStarted;

        internal void StartBudget(long timestamp)
        {
            if (Volatile.Read(ref _budgetStarted) != 0)
            {
                throw new InvalidOperationException("The reconnect budget was already started.");
            }

            StartTimestamp = timestamp;
            Volatile.Write(ref _budgetStarted, 1);
        }

        internal void CancelSafely()
        {
            try
            {
                Cancellation.Cancel(throwOnFirstException: false);
            }
            catch (ObjectDisposedException)
            {
                // A synchronously completed runner may already own disposal.
            }
            catch (AggregateException)
            {
                // Cancellation callback failures cannot escape chain invalidation.
            }
        }
    }

    private sealed record QueuedSnapshot(
        long Generation,
        ReconnectChain? Chain,
        PlaybackReconnectSnapshot Snapshot);

    private sealed class PlaybackReconnectDeadlineException : Exception;
}
