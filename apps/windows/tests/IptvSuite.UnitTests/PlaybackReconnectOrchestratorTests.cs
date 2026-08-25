using System.Reflection;
using System.Text.Json;
using IptvSuite.Application;
using IptvSuite.Domain;
using IptvSuite.Testing;
using Microsoft.Extensions.Time.Testing;

namespace IptvSuite.UnitTests;

[TestClass]
public sealed class PlaybackReconnectOrchestratorTests
{
    private static readonly DateTimeOffset Start = DateTimeOffset.Parse(
        "2026-08-25T00:00:00Z",
        System.Globalization.CultureInfo.InvariantCulture);
    private static readonly int[] ExpectedThreeAttempts = [1, 2, 3];

    [TestMethod]
    public async Task NeverAndManualFailuresDoNotDispatchOrReadJitter()
    {
        FakeTimeProvider time = TestTime.Create(Start);
        int jitterCalls = 0;
        int attemptCalls = 0;
        await using var orchestrator = Create(
            time,
            _ =>
            {
                Interlocked.Increment(ref jitterCalls);
                return TimeSpan.Zero;
            },
            (_, _, _) =>
            {
                Interlocked.Increment(ref attemptCalls);
                return ValueTask.FromResult(PlaybackEngineOperationResult.Succeeded());
            });

        PlaybackReconnectSnapshot never = await orchestrator.BeginAsync(
            Correlation(1),
            DomainError.Create(DomainErrorCode.AuthenticationRejected));
        PlaybackReconnectSnapshot manual = await orchestrator.BeginAsync(
            Correlation(2),
            DomainError.Create(DomainErrorCode.PlaybackStartFailed));

        Assert.AreEqual(PlaybackReconnectPhase.DoNotRetry, never.Phase);
        Assert.AreEqual(DomainErrorCode.AuthenticationRejected, never.TerminalErrorCode);
        Assert.AreEqual(PlaybackReconnectPhase.DoNotRetry, manual.Phase);
        Assert.AreEqual(DomainErrorCode.PlaybackStartFailed, manual.TerminalErrorCode);
        Assert.AreEqual(0, jitterCalls);
        Assert.AreEqual(0, attemptCalls);
    }

    [TestMethod]
    public async Task AutomaticChainUsesExactDelayAndJitterForEveryAttempt()
    {
        FakeTimeProvider time = TestTime.Create(Start);
        var attempts = new List<int>();
        var jitterAttempts = new List<int>();
        await using var orchestrator = Create(
            time,
            attempt =>
            {
                jitterAttempts.Add(attempt);
                return TimeSpan.FromMilliseconds(125);
            },
            (_, attempt, _) =>
            {
                attempts.Add(attempt);
                return ValueTask.FromResult(attempt == 3
                    ? PlaybackEngineOperationResult.Succeeded()
                    : PlaybackEngineOperationResult.Failed(DomainErrorCode.StreamInterrupted));
            });

        Task<PlaybackReconnectSnapshot> completion = orchestrator.BeginAsync(
            Correlation(1),
            DomainError.Create(DomainErrorCode.StreamInterrupted));

        Assert.AreEqual(0, attempts.Count);
        await AdvanceAsync(time, TimeSpan.FromMilliseconds(1124));
        Assert.AreEqual(0, attempts.Count);
        await AdvanceAsync(time, TimeSpan.FromMilliseconds(1));
        await WaitUntilAsync(() => attempts.Count == 1);
        await WaitUntilAsync(() => orchestrator.Current is
            { Phase: PlaybackReconnectPhase.Waiting, AttemptNumber: 2 });

        await AdvanceAsync(time, TimeSpan.FromMilliseconds(2125));
        await WaitUntilAsync(() => attempts.Count == 2);
        await WaitUntilAsync(() => orchestrator.Current is
            { Phase: PlaybackReconnectPhase.Waiting, AttemptNumber: 3 });

        await AdvanceAsync(time, TimeSpan.FromMilliseconds(4125));
        PlaybackReconnectSnapshot result = await completion.WaitAsync(TimeSpan.FromSeconds(2));

        CollectionAssert.AreEqual(ExpectedThreeAttempts, attempts);
        CollectionAssert.AreEqual(ExpectedThreeAttempts, jitterAttempts);
        Assert.AreEqual(PlaybackReconnectPhase.Succeeded, result.Phase);
        Assert.AreEqual(3, result.AttemptNumber);
    }

    [TestMethod]
    public async Task CountdownIsRecomputedFromMonotonicElapsedTimeAtBoundedTicks()
    {
        FakeTimeProvider time = TestTime.Create(Start);
        var snapshots = new List<PlaybackReconnectSnapshot>();
        await using var orchestrator = Create(
            time,
            _ => TimeSpan.FromMilliseconds(250),
            (_, _, _) => ValueTask.FromResult(PlaybackEngineOperationResult.Succeeded()));
        orchestrator.SnapshotChanged += (_, args) => snapshots.Add(args.Snapshot);

        Task<PlaybackReconnectSnapshot> completion = orchestrator.BeginAsync(
            Correlation(1),
            DomainError.Create(DomainErrorCode.StreamInterrupted));
        await WaitUntilAsync(() => snapshots.Any(snapshot => snapshot.Phase == PlaybackReconnectPhase.Waiting));
        Assert.AreEqual(TimeSpan.FromMilliseconds(1250), orchestrator.Current.RemainingDelay);

        await AdvanceAsync(time, TimeSpan.FromSeconds(1));
        await WaitUntilAsync(() => snapshots.Any(snapshot =>
            snapshot.Phase == PlaybackReconnectPhase.Waiting &&
            snapshot.RemainingDelay == TimeSpan.FromMilliseconds(250)));

        await AdvanceAsync(time, TimeSpan.FromMilliseconds(250));
        Assert.AreEqual(
            PlaybackReconnectPhase.Succeeded,
            (await completion.WaitAsync(TimeSpan.FromSeconds(2))).Phase);
        Assert.IsTrue(snapshots
            .Where(snapshot => snapshot.Phase == PlaybackReconnectPhase.Waiting)
            .Zip(snapshots.Where(snapshot => snapshot.Phase == PlaybackReconnectPhase.Waiting).Skip(1))
            .All(pair => pair.First.RemainingDelay >= pair.Second.RemainingDelay));
    }

    [TestMethod]
    public async Task ExactDeadlineCancelsBlockedAttemptAndRejectsRacingSuccess()
    {
        FakeTimeProvider time = TestTime.Create(Start);
        var started = NewSignal();
        var cancelled = NewSignal();
        await using var orchestrator = Create(
            time,
            _ => TimeSpan.Zero,
            async (_, _, token) =>
            {
                started.TrySetResult(true);
                using CancellationTokenRegistration registration = token.Register(
                    () => cancelled.TrySetResult(true));
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                return PlaybackEngineOperationResult.Succeeded();
            });

        Task<PlaybackReconnectSnapshot> completion = orchestrator.BeginAsync(
            Correlation(1),
            DomainError.Create(DomainErrorCode.StreamInterrupted));
        await AdvanceAsync(time, TimeSpan.FromSeconds(1));
        await started.Task.WaitAsync(TimeSpan.FromSeconds(2));

        await AdvanceAsync(time, TimeSpan.FromSeconds(29));

        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(2));
        PlaybackReconnectSnapshot result = await completion.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.AreEqual(PlaybackReconnectPhase.Exhausted, result.Phase);
        Assert.AreEqual(DomainErrorCode.ReconnectExhausted, result.TerminalErrorCode);
        Assert.AreEqual(TimeSpan.Zero, result.RemainingBudget);
    }

    [TestMethod]
    public async Task LateNonCooperativeSuccessAtExactDeadlineIsRejectedAsExhausted()
    {
        FakeTimeProvider time = TestTime.Create(Start);
        var started = NewSignal();
        var release = NewSignal();
        await using var orchestrator = Create(
            time,
            _ => TimeSpan.Zero,
            async (_, _, token) =>
            {
                started.TrySetResult(true);
                using CancellationTokenRegistration registration = token.Register(
                    static () => throw new InvalidOperationException(
                        "Synthetic deadline callback failure."));
                await release.Task;
                return PlaybackEngineOperationResult.Succeeded();
            });

        Task<PlaybackReconnectSnapshot> completion = orchestrator.BeginAsync(
            Correlation(1),
            DomainError.Create(DomainErrorCode.StreamInterrupted));
        await AdvanceAsync(time, TimeSpan.FromSeconds(1));
        await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await AdvanceAsync(time, TimeSpan.FromSeconds(29));

        Assert.IsFalse(completion.IsCompleted);
        release.TrySetResult(true);
        PlaybackReconnectSnapshot result = await completion.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.AreEqual(PlaybackReconnectPhase.Exhausted, result.Phase);
        Assert.AreEqual(DomainErrorCode.ReconnectExhausted, result.TerminalErrorCode);
        Assert.AreEqual(TimeSpan.Zero, result.RemainingBudget);
    }

    [TestMethod]
    public async Task LateNonCancellationFaultAtExactDeadlineIsClassifiedAsExhausted()
    {
        FakeTimeProvider time = TestTime.Create(Start);
        var started = NewSignal();
        var release = NewSignal();
        await using var orchestrator = Create(
            time,
            _ => TimeSpan.Zero,
            async (_, _, _) =>
            {
                started.TrySetResult(true);
                await release.Task;
                throw new InvalidOperationException("Synthetic late failure.");
            });

        Task<PlaybackReconnectSnapshot> completion = orchestrator.BeginAsync(
            Correlation(1),
            DomainError.Create(DomainErrorCode.StreamInterrupted));
        await AdvanceAsync(time, TimeSpan.FromSeconds(1));
        await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await AdvanceAsync(time, TimeSpan.FromSeconds(29));

        release.TrySetResult(true);
        PlaybackReconnectSnapshot result = await completion.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.AreEqual(PlaybackReconnectPhase.Exhausted, result.Phase);
        Assert.AreEqual(DomainErrorCode.ReconnectExhausted, result.TerminalErrorCode);
    }

    [TestMethod]
    public async Task CancelDuringDelayIsSynchronousAndNoFutureAttemptIsDispatched()
    {
        FakeTimeProvider time = TestTime.Create(Start);
        int attempts = 0;
        await using var orchestrator = Create(
            time,
            _ => TimeSpan.Zero,
            (_, _, _) =>
            {
                Interlocked.Increment(ref attempts);
                return ValueTask.FromResult(PlaybackEngineOperationResult.Succeeded());
            });
        PlaybackReconnectCorrelationId correlation = Correlation(1);
        Task<PlaybackReconnectSnapshot> completion = orchestrator.BeginAsync(
            correlation,
            DomainError.Create(DomainErrorCode.StreamInterrupted));

        Assert.IsTrue(orchestrator.Cancel(correlation));
        Assert.AreEqual(PlaybackReconnectPhase.Cancelled, orchestrator.Current.Phase);
        Assert.AreEqual(
            PlaybackReconnectPhase.Cancelled,
            (await completion.WaitAsync(TimeSpan.FromSeconds(2))).Phase);

        await AdvanceAsync(time, TimeSpan.FromHours(1));
        Assert.AreEqual(0, attempts);
    }

    [TestMethod]
    public async Task CancelDuringAttemptPropagatesTokenAndDispatchesNothingLater()
    {
        FakeTimeProvider time = TestTime.Create(Start);
        var started = NewSignal();
        var cancelled = NewSignal();
        int attempts = 0;
        await using var orchestrator = Create(
            time,
            _ => TimeSpan.Zero,
            async (_, _, token) =>
            {
                Interlocked.Increment(ref attempts);
                started.TrySetResult(true);
                _ = token.Register(
                    static () => throw new InvalidOperationException(
                        "Synthetic cancellation callback failure."));
                using CancellationTokenRegistration registration = token.Register(
                    () => cancelled.TrySetResult(true));
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                return PlaybackEngineOperationResult.Succeeded();
            });
        PlaybackReconnectCorrelationId correlation = Correlation(1);
        Task<PlaybackReconnectSnapshot> completion = orchestrator.BeginAsync(
            correlation,
            DomainError.Create(DomainErrorCode.StreamInterrupted));
        await AdvanceAsync(time, TimeSpan.FromSeconds(1));
        await started.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.IsTrue(orchestrator.Cancel(correlation));

        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.AreEqual(
            PlaybackReconnectPhase.Cancelled,
            (await completion.WaitAsync(TimeSpan.FromSeconds(2))).Phase);
        await AdvanceAsync(time, TimeSpan.FromHours(1));
        Assert.AreEqual(1, attempts);
    }

    [TestMethod]
    public async Task DuplicateBeginSharesOneChainBudgetTaskAndJitterRead()
    {
        FakeTimeProvider time = TestTime.Create(Start);
        int jitterCalls = 0;
        await using var orchestrator = Create(
            time,
            _ =>
            {
                Interlocked.Increment(ref jitterCalls);
                return TimeSpan.Zero;
            },
            (_, _, _) => ValueTask.FromResult(PlaybackEngineOperationResult.Succeeded()));
        PlaybackReconnectCorrelationId correlation = Correlation(1);
        Task<PlaybackReconnectSnapshot> first = orchestrator.BeginAsync(
            correlation,
            DomainError.Create(DomainErrorCode.StreamInterrupted));

        Task<PlaybackReconnectSnapshot>[] duplicates = Enumerable.Range(0, 32)
            .Select(_ => orchestrator.BeginAsync(
                correlation,
                DomainError.Create(DomainErrorCode.StreamInterrupted)))
            .ToArray();

        Assert.IsTrue(duplicates.All(task => ReferenceEquals(first, task)));
        Assert.IsTrue(ReferenceEquals(first, orchestrator.RetryNowAsync(correlation)));
        Assert.AreEqual(1, jitterCalls);
        Assert.AreEqual(TimeSpan.FromSeconds(30), orchestrator.Current.RemainingBudget);
        orchestrator.Cancel(correlation);
        await first.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [TestMethod]
    public async Task ReplacementCancelsStaleChainAndGlobalGatePreventsOverlap()
    {
        FakeTimeProvider time = TestTime.Create(Start);
        PlaybackReconnectCorrelationId firstCorrelation = Correlation(1);
        PlaybackReconnectCorrelationId secondCorrelation = Correlation(2);
        var firstStarted = NewSignal();
        var releaseFirst = NewSignal();
        var secondStarted = NewSignal();
        int concurrent = 0;
        int maximumConcurrent = 0;
        await using var orchestrator = Create(
            time,
            _ => TimeSpan.Zero,
            async (correlation, _, _) =>
            {
                int current = Interlocked.Increment(ref concurrent);
                maximumConcurrent = Math.Max(maximumConcurrent, current);
                try
                {
                    if (correlation == firstCorrelation)
                    {
                        firstStarted.TrySetResult(true);
                        await releaseFirst.Task;
                    }
                    else
                    {
                        secondStarted.TrySetResult(true);
                    }

                    return PlaybackEngineOperationResult.Succeeded();
                }
                finally
                {
                    Interlocked.Decrement(ref concurrent);
                }
            });

        Task<PlaybackReconnectSnapshot> first = orchestrator.BeginAsync(
            firstCorrelation,
            DomainError.Create(DomainErrorCode.StreamInterrupted));
        await AdvanceAsync(time, TimeSpan.FromSeconds(1));
        await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Task<PlaybackReconnectSnapshot> second = orchestrator.BeginAsync(
            secondCorrelation,
            DomainError.Create(DomainErrorCode.StreamInterrupted));
        await AdvanceAsync(time, TimeSpan.FromSeconds(1));
        Assert.IsFalse(secondStarted.Task.IsCompleted);
        Assert.AreEqual(
            PlaybackReconnectPhase.Cancelled,
            (await first.WaitAsync(TimeSpan.FromSeconds(2))).Phase);

        releaseFirst.TrySetResult(true);
        await secondStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.AreEqual(
            PlaybackReconnectPhase.Succeeded,
            (await second.WaitAsync(TimeSpan.FromSeconds(2))).Phase);
        Assert.AreEqual(1, maximumConcurrent);
        Assert.AreEqual(secondCorrelation, orchestrator.Current.CorrelationId);
    }

    [TestMethod]
    public async Task ThreeTransientFailuresExhaustWithoutFourthAttempt()
    {
        FakeTimeProvider time = TestTime.Create(Start);
        int attempts = 0;
        await using var orchestrator = Create(
            time,
            _ => TimeSpan.Zero,
            (_, _, _) =>
            {
                Interlocked.Increment(ref attempts);
                return ValueTask.FromResult(
                    PlaybackEngineOperationResult.Failed(DomainErrorCode.StreamInterrupted));
            });

        Task<PlaybackReconnectSnapshot> completion = orchestrator.BeginAsync(
            Correlation(1),
            DomainError.Create(DomainErrorCode.StreamInterrupted));
        await AdvanceAsync(time, TimeSpan.FromSeconds(1));
        await WaitUntilAsync(() => attempts == 1);
        await AdvanceAsync(time, TimeSpan.FromSeconds(2));
        await WaitUntilAsync(() => attempts == 2);
        await AdvanceAsync(time, TimeSpan.FromSeconds(4));

        PlaybackReconnectSnapshot result = await completion.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.AreEqual(3, attempts);
        Assert.AreEqual(PlaybackReconnectPhase.Exhausted, result.Phase);
        Assert.AreEqual(3, result.AttemptNumber);
        await AdvanceAsync(time, TimeSpan.FromHours(1));
        Assert.AreEqual(3, attempts);
    }

    [TestMethod]
    public async Task ManualRetryRequiresTerminalStateStartsImmediatelyAndCoalesces()
    {
        FakeTimeProvider time = TestTime.Create(Start);
        var started = NewSignal();
        var release = NewSignal();
        int attempts = 0;
        await using var orchestrator = Create(
            time,
            _ => TimeSpan.Zero,
            async (_, attempt, token) =>
            {
                Assert.AreEqual(1, attempt);
                Assert.IsFalse(token.IsCancellationRequested);
                Interlocked.Increment(ref attempts);
                started.TrySetResult(true);
                await release.Task;
                return PlaybackEngineOperationResult.Succeeded();
            });
        PlaybackReconnectCorrelationId correlation = Correlation(1);
        PlaybackReconnectSnapshot terminal = await orchestrator.BeginAsync(
            correlation,
            DomainError.Create(DomainErrorCode.PlaybackStartFailed));
        Assert.AreEqual(PlaybackReconnectPhase.DoNotRetry, terminal.Phase);

        Task<PlaybackReconnectSnapshot> first = orchestrator.RetryNowAsync(correlation);
        Task<PlaybackReconnectSnapshot> duplicate = orchestrator.RetryNowAsync(correlation);
        Assert.IsTrue(ReferenceEquals(first, duplicate));
        await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.AreEqual(1, attempts);
        Assert.AreEqual(PlaybackReconnectPhase.Attempting, orchestrator.Current.Phase);
        Assert.IsGreaterThan(TimeSpan.FromSeconds(29), orchestrator.Current.RemainingBudget);

        release.TrySetResult(true);
        Assert.AreEqual(
            PlaybackReconnectPhase.Succeeded,
            (await first.WaitAsync(TimeSpan.FromSeconds(2))).Phase);
        Assert.ThrowsExactly<InvalidOperationException>(() =>
            orchestrator.RetryNowAsync(correlation));
    }

    [TestMethod]
    public async Task SameCorrelationCanStartANewIndependentChainAfterTerminalCompletion()
    {
        FakeTimeProvider time = TestTime.Create(Start);
        int attempts = 0;
        await using var orchestrator = Create(
            time,
            _ => TimeSpan.Zero,
            (_, _, _) =>
            {
                Interlocked.Increment(ref attempts);
                return ValueTask.FromResult(PlaybackEngineOperationResult.Succeeded());
            });
        PlaybackReconnectCorrelationId correlation = Correlation(1);
        Task<PlaybackReconnectSnapshot> first = orchestrator.BeginAsync(
            correlation,
            DomainError.Create(DomainErrorCode.PlaybackStartFailed));
        Assert.AreEqual(PlaybackReconnectPhase.DoNotRetry, (await first).Phase);

        Task<PlaybackReconnectSnapshot> second = orchestrator.BeginAsync(
            correlation,
            DomainError.Create(DomainErrorCode.StreamInterrupted));
        Assert.IsFalse(ReferenceEquals(first, second));
        await AdvanceAsync(time, TimeSpan.FromSeconds(1));

        Assert.AreEqual(
            PlaybackReconnectPhase.Succeeded,
            (await second.WaitAsync(TimeSpan.FromSeconds(2))).Phase);
        Assert.AreEqual(1, attempts);
    }

    [TestMethod]
    public async Task TerminalCommitCompletesOriginalTaskBeforeReentrantSameCorrelationBegin()
    {
        FakeTimeProvider time = TestTime.Create(Start);
        PlaybackReconnectCorrelationId correlation = Correlation(1);
        Task<PlaybackReconnectSnapshot>? replacement = null;
        int terminalReentry = 0;
        await using var orchestrator = Create(
            time,
            _ => TimeSpan.Zero,
            (_, _, _) => ValueTask.FromResult(PlaybackEngineOperationResult.Succeeded()));
        orchestrator.SnapshotChanged += (_, args) =>
        {
            if (args.Snapshot.Phase == PlaybackReconnectPhase.DoNotRetry &&
                Interlocked.CompareExchange(ref terminalReentry, 1, 0) == 0)
            {
                replacement = orchestrator.BeginAsync(
                    correlation,
                    DomainError.Create(DomainErrorCode.StreamInterrupted));
            }
        };

        Task<PlaybackReconnectSnapshot> original = orchestrator.BeginAsync(
            correlation,
            DomainError.Create(DomainErrorCode.PlaybackStartFailed));

        Assert.IsNotNull(replacement);
        Task<PlaybackReconnectSnapshot> replacementTask = replacement;
        Assert.IsFalse(ReferenceEquals(original, replacementTask));
        Assert.AreEqual(
            PlaybackReconnectPhase.DoNotRetry,
            (await original.WaitAsync(TimeSpan.FromSeconds(2))).Phase);
        await AdvanceAsync(time, TimeSpan.FromSeconds(1));
        Assert.AreEqual(
            PlaybackReconnectPhase.Succeeded,
            (await replacementTask.WaitAsync(TimeSpan.FromSeconds(2))).Phase);
    }

    [TestMethod]
    public async Task ReentrantCancelDropsStaleAttemptEventAndPreventsExecutorDispatch()
    {
        FakeTimeProvider time = TestTime.Create(Start);
        PlaybackReconnectCorrelationId correlation = Correlation(1);
        var secondObserverPhases = new List<PlaybackReconnectPhase>();
        int attempts = 0;
        await using var orchestrator = Create(
            time,
            _ => TimeSpan.Zero,
            (_, _, _) =>
            {
                Interlocked.Increment(ref attempts);
                return ValueTask.FromResult(PlaybackEngineOperationResult.Succeeded());
            });
        orchestrator.SnapshotChanged += (_, args) =>
        {
            if (args.Snapshot.Phase == PlaybackReconnectPhase.Attempting)
            {
                Assert.IsTrue(orchestrator.Cancel(correlation));
            }
        };
        orchestrator.SnapshotChanged += (_, args) => secondObserverPhases.Add(args.Snapshot.Phase);

        Task<PlaybackReconnectSnapshot> completion = orchestrator.BeginAsync(
            correlation,
            DomainError.Create(DomainErrorCode.StreamInterrupted));
        await AdvanceAsync(time, TimeSpan.FromSeconds(1));
        PlaybackReconnectSnapshot result = await completion.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.AreEqual(PlaybackReconnectPhase.Cancelled, result.Phase);
        Assert.AreEqual(0, attempts);
        Assert.IsFalse(secondObserverPhases.Contains(PlaybackReconnectPhase.Attempting));
        CollectionAssert.AreEqual(
            new[]
            {
                PlaybackReconnectPhase.Evaluating,
                PlaybackReconnectPhase.Waiting,
                PlaybackReconnectPhase.Cancelled,
            },
            secondObserverPhases);
    }

    [TestMethod]
    public async Task ReplacementWhileRunnerTimestampIsBlockedInvalidatesOldChainBeforeDispatch()
    {
        FakeTimeProvider inner = TestTime.Create(Start);
        using var releaseTimestamp = new ManualResetEventSlim(initialState: false);
        var timestampEntered = NewSignal();
        var time = new FirstTimestampBlockingTimeProvider(
            inner,
            timestampEntered,
            releaseTimestamp);
        PlaybackReconnectCorrelationId firstCorrelation = Correlation(1);
        PlaybackReconnectCorrelationId secondCorrelation = Correlation(2);
        var attempted = new List<PlaybackReconnectCorrelationId>();
        await using var orchestrator = Create(
            time,
            _ => TimeSpan.Zero,
            (correlation, _, _) =>
            {
                attempted.Add(correlation);
                return ValueTask.FromResult(PlaybackEngineOperationResult.Succeeded());
            });

        Task<object> blockedAdmission = Task.Run(() =>
            (object)orchestrator.BeginAsync(
                firstCorrelation,
                DomainError.Create(DomainErrorCode.StreamInterrupted)));
        await timestampEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Task<PlaybackReconnectSnapshot> second = orchestrator.BeginAsync(
            secondCorrelation,
            DomainError.Create(DomainErrorCode.StreamInterrupted));
        releaseTimestamp.Set();
        var first = (Task<PlaybackReconnectSnapshot>)await blockedAdmission.WaitAsync(
            TimeSpan.FromSeconds(2));
        Assert.AreEqual(
            PlaybackReconnectPhase.Cancelled,
            (await first.WaitAsync(TimeSpan.FromSeconds(2))).Phase);

        await AdvanceAsync(inner, TimeSpan.FromSeconds(1));
        Assert.AreEqual(
            PlaybackReconnectPhase.Succeeded,
            (await second.WaitAsync(TimeSpan.FromSeconds(2))).Phase);
        CollectionAssert.AreEqual(new[] { secondCorrelation }, attempted);
    }

    [TestMethod]
    public async Task OperationCancelledResultUsesCancelledTerminalSemantics()
    {
        FakeTimeProvider time = TestTime.Create(Start);
        await using var orchestrator = Create(
            time,
            _ => TimeSpan.Zero,
            (_, _, _) => ValueTask.FromResult(
                PlaybackEngineOperationResult.Failed(DomainErrorCode.OperationCancelled)));
        Task<PlaybackReconnectSnapshot> completion = orchestrator.BeginAsync(
            Correlation(1),
            DomainError.Create(DomainErrorCode.StreamInterrupted));

        await AdvanceAsync(time, TimeSpan.FromSeconds(1));
        PlaybackReconnectSnapshot result = await completion.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.AreEqual(PlaybackReconnectPhase.Cancelled, result.Phase);
        Assert.AreEqual(DomainErrorCode.OperationCancelled, result.TerminalErrorCode);
    }

    [TestMethod]
    public async Task MalformedFailureJitterAndExecutorFaultFailClosedSafely()
    {
        string sensitive = SecurityTestAssertions.CreateSensitiveValue("RECONNECT-KERNEL");
        DomainError malformed = CreateDomainError(
            DomainErrorCode.StreamInterrupted,
            DomainRetryability.BoundedTransient,
            sensitive);
        FakeTimeProvider time = TestTime.Create(Start);
        await using var malformedOrchestrator = Create(
            time,
            _ => throw new InvalidOperationException(sensitive),
            (_, _, _) => throw new InvalidOperationException(sensitive));

        PlaybackReconnectSnapshot malformedResult = await malformedOrchestrator.BeginAsync(
            Correlation(1),
            malformed);
        Assert.AreEqual(DomainErrorCode.DomainInvariantViolation, malformedResult.TerminalErrorCode);

        await using var jitterOrchestrator = Create(
            TestTime.Create(Start),
            _ => TimeSpan.FromMilliseconds(251),
            (_, _, _) => throw new InvalidOperationException(sensitive));
        PlaybackReconnectSnapshot jitterResult = await jitterOrchestrator.BeginAsync(
            Correlation(2),
            DomainError.Create(DomainErrorCode.StreamInterrupted));
        Assert.AreEqual(DomainErrorCode.DomainInvariantViolation, jitterResult.TerminalErrorCode);

        string observable = string.Join(
            '|',
            malformedResult,
            jitterResult,
            JsonSerializer.Serialize(malformedResult),
            JsonSerializer.Serialize(jitterResult));
        SecurityTestAssertions.DoesNotContainSensitive(observable, sensitive);
    }

    [TestMethod]
    public async Task PreDeadlineExecutorFaultDispatchesOnceAndFailsClosedWithoutSensitiveData()
    {
        string sensitive = SecurityTestAssertions.CreateSensitiveValue("RECONNECT-EXECUTOR");
        FakeTimeProvider time = TestTime.Create(Start);
        int dispatches = 0;
        await using var orchestrator = Create(
            time,
            _ => TimeSpan.FromMilliseconds(125),
            (_, _, _) =>
            {
                Interlocked.Increment(ref dispatches);
                throw new InvalidOperationException(sensitive);
            });

        Task<PlaybackReconnectSnapshot> completion = orchestrator.BeginAsync(
            Correlation(1),
            DomainError.Create(DomainErrorCode.StreamInterrupted));
        await AdvanceAsync(time, TimeSpan.FromMilliseconds(1125));
        PlaybackReconnectSnapshot result = await completion.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.AreEqual(1, dispatches);
        Assert.AreEqual(PlaybackReconnectPhase.DoNotRetry, result.Phase);
        Assert.AreEqual(DomainErrorCode.DomainInvariantViolation, result.TerminalErrorCode);
        SecurityTestAssertions.DoesNotContainSensitive(
            string.Join('|', result, orchestrator.Current, JsonSerializer.Serialize(result)),
            sensitive);
    }

    [TestMethod]
    public async Task ThrowingObserverIsContainedAndDisposeIsIdempotentAndDrains()
    {
        FakeTimeProvider time = TestTime.Create(Start);
        int attempts = 0;
        object? observedSender = null;
        var observed = new List<PlaybackReconnectPhase>();
        var orchestrator = Create(
            time,
            _ => TimeSpan.Zero,
            (_, _, _) =>
            {
                Interlocked.Increment(ref attempts);
                return ValueTask.FromResult(PlaybackEngineOperationResult.Succeeded());
            });
        orchestrator.SnapshotChanged += (_, _) => throw new InvalidOperationException("observer");
        orchestrator.SnapshotChanged += (sender, args) =>
        {
            observedSender = sender;
            observed.Add(args.Snapshot.Phase);
        };
        PlaybackReconnectCorrelationId correlation = Correlation(1);
        Task<PlaybackReconnectSnapshot> completion = orchestrator.BeginAsync(
            correlation,
            DomainError.Create(DomainErrorCode.StreamInterrupted));
        int observedBeforeDispose = observed.Count;
        Assert.AreSame(orchestrator, observedSender);

        ValueTask firstDispose = orchestrator.DisposeAsync();
        ValueTask secondDispose = orchestrator.DisposeAsync();
        await firstDispose.AsTask().WaitAsync(TimeSpan.FromSeconds(2));
        await secondDispose.AsTask().WaitAsync(TimeSpan.FromSeconds(2));
        Assert.AreEqual(
            PlaybackReconnectPhase.Cancelled,
            (await completion.WaitAsync(TimeSpan.FromSeconds(2))).Phase);

        await AdvanceAsync(time, TimeSpan.FromHours(1));
        Assert.AreEqual(0, attempts);
        Assert.AreEqual(observedBeforeDispose, observed.Count);
        Assert.ThrowsExactly<ObjectDisposedException>(() => orchestrator.BeginAsync(
            Correlation(2),
            DomainError.Create(DomainErrorCode.StreamInterrupted)));
    }

    [TestMethod]
    public async Task DisposeWaitsForNonCooperativeInFlightAttemptAndPublishesNothingLater()
    {
        FakeTimeProvider time = TestTime.Create(Start);
        var started = NewSignal();
        var release = NewSignal();
        var phases = new List<PlaybackReconnectPhase>();
        int concurrent = 0;
        int maximumConcurrent = 0;
        var orchestrator = Create(
            time,
            _ => TimeSpan.Zero,
            async (_, _, _) =>
            {
                int current = Interlocked.Increment(ref concurrent);
                maximumConcurrent = Math.Max(maximumConcurrent, current);
                try
                {
                    started.TrySetResult(true);
                    await release.Task;
                    return PlaybackEngineOperationResult.Succeeded();
                }
                finally
                {
                    Interlocked.Decrement(ref concurrent);
                }
            });
        orchestrator.SnapshotChanged += (_, args) => phases.Add(args.Snapshot.Phase);
        Task<PlaybackReconnectSnapshot> completion = orchestrator.BeginAsync(
            Correlation(1),
            DomainError.Create(DomainErrorCode.StreamInterrupted));
        await AdvanceAsync(time, TimeSpan.FromSeconds(1));
        await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        int phaseCountAtDispose = phases.Count;

        Task dispose = orchestrator.DisposeAsync().AsTask();
        Assert.IsFalse(dispose.IsCompleted);
        Assert.AreEqual(
            PlaybackReconnectPhase.Cancelled,
            (await completion.WaitAsync(TimeSpan.FromSeconds(2))).Phase);
        await AdvanceAsync(time, TimeSpan.FromHours(1));
        Assert.AreEqual(phaseCountAtDispose, phases.Count);

        release.TrySetResult(true);
        await dispose.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.AreEqual(1, maximumConcurrent);
        Assert.AreEqual(0, concurrent);
        Assert.AreEqual(phaseCountAtDispose, phases.Count);
    }

    [TestMethod]
    public async Task DisposeWaitsForActiveSnapshotObserverPumpToDrain()
    {
        FakeTimeProvider time = TestTime.Create(Start);
        var observerEntered = NewSignal();
        using var releaseObserver = new ManualResetEventSlim(initialState: false);
        var orchestrator = Create(
            time,
            _ => TimeSpan.Zero,
            (_, _, _) => ValueTask.FromResult(PlaybackEngineOperationResult.Succeeded()));
        orchestrator.SnapshotChanged += (_, args) =>
        {
            if (args.Snapshot.Phase == PlaybackReconnectPhase.DoNotRetry)
            {
                observerEntered.TrySetResult(true);
                releaseObserver.Wait(TimeSpan.FromSeconds(5));
            }
        };

        Task<object> beginCall = Task.Run(() =>
            (object)orchestrator.BeginAsync(
                Correlation(1),
                DomainError.Create(DomainErrorCode.PlaybackStartFailed)));
        await observerEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Task dispose = orchestrator.DisposeAsync().AsTask();
        try
        {
            Assert.IsFalse(dispose.IsCompleted);
        }
        finally
        {
            releaseObserver.Set();
        }

        var completion = (Task<PlaybackReconnectSnapshot>)await beginCall.WaitAsync(
            TimeSpan.FromSeconds(2));
        Assert.AreEqual(
            PlaybackReconnectPhase.DoNotRetry,
            (await completion.WaitAsync(TimeSpan.FromSeconds(2))).Phase);
        await dispose.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [TestMethod]
    public async Task BackwardsMonotonicProviderFailsClosedWithoutDispatch()
    {
        var time = new BackwardsTimeProvider();
        int attempts = 0;
        await using var orchestrator = Create(
            time,
            _ => TimeSpan.Zero,
            (_, _, _) =>
            {
                Interlocked.Increment(ref attempts);
                return ValueTask.FromResult(PlaybackEngineOperationResult.Succeeded());
            });

        PlaybackReconnectSnapshot result = await orchestrator.BeginAsync(
            Correlation(1),
            DomainError.Create(DomainErrorCode.StreamInterrupted));

        Assert.AreEqual(PlaybackReconnectPhase.DoNotRetry, result.Phase);
        Assert.AreEqual(DomainErrorCode.DomainInvariantViolation, result.TerminalErrorCode);
        Assert.AreEqual(0, attempts);
    }

    [TestMethod]
    public async Task DisposedAdmissionRejectsBeforeReadingExternalTimeProvider()
    {
        var time = new GuardedTimeProvider(TestTime.Create(Start));
        var orchestrator = Create(
            time,
            _ => TimeSpan.Zero,
            (_, _, _) => ValueTask.FromResult(PlaybackEngineOperationResult.Succeeded()));
        PlaybackReconnectCorrelationId correlation = Correlation(1);
        PlaybackReconnectSnapshot terminal = await orchestrator.BeginAsync(
            correlation,
            DomainError.Create(DomainErrorCode.PlaybackStartFailed));
        Assert.AreEqual(PlaybackReconnectPhase.DoNotRetry, terminal.Phase);
        await orchestrator.DisposeAsync();

        int readsBeforeRejectedAdmission = time.TimestampReads;
        time.ThrowOnTimestampRead = true;
        Assert.ThrowsExactly<ObjectDisposedException>(() => orchestrator.BeginAsync(
            Correlation(2),
            DomainError.Create(DomainErrorCode.StreamInterrupted)));
        Assert.ThrowsExactly<ObjectDisposedException>(() => orchestrator.RetryNowAsync(correlation));
        Assert.AreEqual(readsBeforeRejectedAdmission, time.TimestampReads);
    }

    [TestMethod]
    public void PublicFactoriesAndSnapshotConstructionRejectInvalidInvariants()
    {
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => Correlation(0));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => Correlation(-1));
        Assert.ThrowsExactly<ArgumentException>(() => new PlaybackReconnectOrchestrator(
            new PlaybackReconnectPolicy(new PlaybackReconnectPolicyOptions(
                maximumAttempts: 1,
                totalBudget: TimeSpan.FromSeconds(30),
                baseDelays: [TimeSpan.FromSeconds(1)],
                maximumJitter: TimeSpan.FromMilliseconds(250))),
            TimeProvider.System,
            _ => TimeSpan.Zero,
            (_, _, _) => ValueTask.FromResult(PlaybackEngineOperationResult.Succeeded())));

        ConstructorInfo constructor = typeof(PlaybackReconnectSnapshot)
            .GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic)
            .Single(candidate => candidate.GetParameters().Length == 7);
        object?[][] invalidArguments =
        [
            [(PlaybackReconnectPhase)int.MaxValue, default(PlaybackReconnectCorrelationId), 0, 0, TimeSpan.Zero, TimeSpan.Zero, null],
            [PlaybackReconnectPhase.Waiting, Correlation(1), 1, 3, TimeSpan.FromMilliseconds(1250) + TimeSpan.FromTicks(1), TimeSpan.FromSeconds(29), null],
            [PlaybackReconnectPhase.Waiting, Correlation(1), 2, 3, TimeSpan.FromMilliseconds(2250) + TimeSpan.FromTicks(1), TimeSpan.FromSeconds(29), null],
            [PlaybackReconnectPhase.Waiting, Correlation(1), 3, 3, TimeSpan.FromMilliseconds(4250) + TimeSpan.FromTicks(1), TimeSpan.FromSeconds(29), null],
            [PlaybackReconnectPhase.Waiting, Correlation(1), 1, 3, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), null],
            [PlaybackReconnectPhase.Waiting, Correlation(1), 1, 3, TimeSpan.FromSeconds(1) + TimeSpan.FromTicks(1), TimeSpan.FromSeconds(1), null],
            [PlaybackReconnectPhase.Evaluating, Correlation(1), 0, 3, TimeSpan.Zero, TimeSpan.Zero, null],
            [PlaybackReconnectPhase.Waiting, Correlation(1), 1, 3, TimeSpan.FromSeconds(1), TimeSpan.Zero, null],
            [PlaybackReconnectPhase.Attempting, Correlation(1), 1, 3, TimeSpan.Zero, TimeSpan.Zero, null],
            [PlaybackReconnectPhase.Succeeded, Correlation(1), 0, 3, TimeSpan.Zero, TimeSpan.FromSeconds(1), null],
            [PlaybackReconnectPhase.Succeeded, Correlation(1), 1, 3, TimeSpan.Zero, TimeSpan.Zero, null],
            [PlaybackReconnectPhase.Cancelled, Correlation(1), 0, 3, TimeSpan.Zero, TimeSpan.Zero, DomainErrorCode.ReconnectExhausted],
        ];
        foreach (object?[] arguments in invalidArguments)
        {
            TargetInvocationException exception = Assert.ThrowsExactly<TargetInvocationException>(
                () => constructor.Invoke(arguments));
            Assert.IsInstanceOfType<ArgumentException>(exception.InnerException);
        }
    }

    private static PlaybackReconnectOrchestrator Create(
        TimeProvider timeProvider,
        PlaybackReconnectJitterSource jitterSource,
        PlaybackReconnectAttemptExecutor executor) => new(
            new PlaybackReconnectPolicy(),
            timeProvider,
            jitterSource,
            executor);

    private static PlaybackReconnectCorrelationId Correlation(long value) =>
        PlaybackReconnectCorrelationId.FromSequence(value);

    private static TaskCompletionSource<bool> NewSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static async Task AdvanceAsync(FakeTimeProvider time, TimeSpan duration)
    {
        TimeSpan remaining = duration;
        while (remaining > TimeSpan.Zero)
        {
            TimeSpan step = remaining > TimeSpan.FromSeconds(1)
                ? TimeSpan.FromSeconds(1)
                : remaining;
            time.Advance(step);
            remaining -= step;
            await Task.Yield();
            await Task.Yield();
        }
    }

    private static async Task WaitUntilAsync(Func<bool> predicate)
    {
        for (int iteration = 0; iteration < 1_000 && !predicate(); iteration++)
        {
            await Task.Yield();
        }

        Assert.IsTrue(predicate(), "The deterministic reconnect condition was not reached.");
    }

    private static DomainError CreateDomainError(
        DomainErrorCode code,
        DomainRetryability retryability,
        string resourceKey)
    {
        ConstructorInfo constructor = typeof(DomainError).GetConstructors(
                BindingFlags.Instance | BindingFlags.NonPublic)
            .Single(candidate => candidate.GetParameters().Length == 3);
        return (DomainError)constructor.Invoke([code, retryability, resourceKey]);
    }

    private sealed class BackwardsTimeProvider : TimeProvider
    {
        private long _timestamp = 2;

        public override long TimestampFrequency => 1;

        public override long GetTimestamp() => Interlocked.Decrement(ref _timestamp);
    }

    private sealed class GuardedTimeProvider(FakeTimeProvider inner) : TimeProvider
    {
        public bool ThrowOnTimestampRead { get; set; }

        public int TimestampReads { get; private set; }

        public override long TimestampFrequency => inner.TimestampFrequency;

        public override long GetTimestamp()
        {
            TimestampReads++;
            if (ThrowOnTimestampRead)
            {
                throw new InvalidOperationException("Timestamp access was forbidden.");
            }

            return inner.GetTimestamp();
        }

        public override DateTimeOffset GetUtcNow() => inner.GetUtcNow();

        public override TimeZoneInfo LocalTimeZone => inner.LocalTimeZone;

        public override ITimer CreateTimer(
            TimerCallback callback,
            object? state,
            TimeSpan dueTime,
            TimeSpan period) => inner.CreateTimer(callback, state, dueTime, period);
    }

    private sealed class FirstTimestampBlockingTimeProvider(
        FakeTimeProvider inner,
        TaskCompletionSource<bool> entered,
        ManualResetEventSlim release) : TimeProvider
    {
        private int _timestampReads;

        public override long TimestampFrequency => inner.TimestampFrequency;

        public override long GetTimestamp()
        {
            if (Interlocked.Increment(ref _timestampReads) == 1)
            {
                entered.TrySetResult(true);
                release.Wait(TimeSpan.FromSeconds(5));
            }

            return inner.GetTimestamp();
        }

        public override DateTimeOffset GetUtcNow() => inner.GetUtcNow();

        public override TimeZoneInfo LocalTimeZone => inner.LocalTimeZone;

        public override ITimer CreateTimer(
            TimerCallback callback,
            object? state,
            TimeSpan dueTime,
            TimeSpan period) => inner.CreateTimer(callback, state, dueTime, period);
    }
}
