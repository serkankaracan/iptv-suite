using System.Collections.Concurrent;
using System.Reflection;
using Microsoft.Extensions.Time.Testing;

namespace IptvSuite.UnitTests;

[TestClass]
public sealed class PlaybackSessionReconnectCoordinatorTests
{
    private static readonly DateTimeOffset Start = DateTimeOffset.Parse(
        "2026-08-25T00:00:00Z",
        System.Globalization.CultureInfo.InvariantCulture);

    [TestMethod]
    public async Task LegacyConstructorRemainsReconnectDisabled()
    {
        var engine = new ReconnectPlaybackEngine();
        await using var coordinator = new PlaybackSessionCoordinator(engine);
        PlaybackSessionSnapshot first = await StartPlayingAsync(coordinator);

        engine.EmitFailure(first.SessionId, DomainErrorCode.StreamInterrupted);

        Assert.AreEqual(PlaybackState.Failed, coordinator.Current.State);
        Assert.AreEqual(DomainErrorCode.StreamInterrupted, coordinator.Current.Error?.Code);
        Assert.IsNull(coordinator.Current.Reconnect);
        Assert.AreEqual(1, engine.OpenCount);
        Assert.IsFalse(coordinator.CanRetryReconnect);
        PlaybackEngineOperationResult retry = await coordinator.RetryReconnectAsync();
        Assert.IsFalse(retry.IsSuccess);
        Assert.AreEqual(DomainErrorCode.OperationCancelled, retry.Error?.Code);
    }

    [TestMethod]
    public void InvalidEnabledConstructorLeavesEngineUnsubscribed()
    {
        var engine = new ReconnectPlaybackEngine();
        var invalidPolicy = new PlaybackReconnectPolicy(
            new PlaybackReconnectPolicyOptions(
                maximumAttempts: 1,
                TimeSpan.FromSeconds(30),
                [TimeSpan.FromSeconds(1)],
                TimeSpan.FromMilliseconds(250)));

        Assert.ThrowsExactly<ArgumentException>(() =>
            new PlaybackSessionCoordinator(
                engine,
                invalidPolicy,
                TestTime.Create(Start),
                _ => TimeSpan.Zero));

        Assert.AreEqual(0, engine.StateChangedSubscriberCount);
    }

    [TestMethod]
    public async Task TransientFailureStopsBeforeReopenAndRestoresControls()
    {
        FakeTimeProvider time = TestTime.Create(Start);
        var engine = new ReconnectPlaybackEngine();
        await using var coordinator = Create(engine, time);
        PlaybackSessionSnapshot first = await StartPlayingAsync(coordinator);
        await coordinator.SetVolumeAsync(first.SessionId, PlaybackVolume.FromPercent(37));
        await coordinator.SetMutedAsync(first.SessionId, isMuted: true);
        await coordinator.SetAspectModeAsync(first.SessionId, PlaybackAspectMode.Fill);

        engine.EmitFailure(first.SessionId, DomainErrorCode.StreamInterrupted);
        await WaitForWaitingAttemptAsync(coordinator, attemptNumber: 1);
        await AdvanceAsync(time, TimeSpan.FromSeconds(1));
        await WaitUntilAsync(() => coordinator.Current.State == PlaybackState.Playing);

        Assert.AreEqual(2, engine.OpenCount);
        Assert.AreEqual(first.SessionId, coordinator.Current.SessionId);
        PlaybackSessionId[] physicalSessions = engine.OpenSessions.ToArray();
        AssertFreshPhysicalSessions(physicalSessions, expectedCount: 2);
        Assert.AreEqual(first.SessionId, physicalSessions[0]);
        Assert.AreEqual(37, coordinator.CurrentControls.Volume.Percent);
        Assert.IsTrue(coordinator.CurrentControls.IsMuted);
        Assert.AreEqual(PlaybackAspectMode.Fill, coordinator.CurrentControls.AspectMode);
        string[] journal = engine.Journal.ToArray();
        int stop = Array.FindIndex(journal, entry => entry == $"Stop:{first.SessionId.Value}");
        int reopen = Array.FindIndex(
            journal,
            stop + 1,
            entry => entry == $"Open:{physicalSessions[1].Value}");
        Assert.IsGreaterThanOrEqualTo(0, stop);
        Assert.IsGreaterThan(stop, reopen);
        CollectionAssert.IsSubsetOf(
            new[]
            {
                $"Volume:{physicalSessions[1].Value}:37",
                $"Muted:{physicalSessions[1].Value}:True",
                $"Aspect:{physicalSessions[1].Value}:Fill",
            },
            journal);
    }

    [TestMethod]
    public async Task ReconnectAttemptWaitsForExactPhysicalPlayableInsteadOfBuffering()
    {
        FakeTimeProvider time = TestTime.Create(Start);
        var engine = new ReconnectPlaybackEngine
        {
            HoldReconnectAtBuffering = true,
        };
        await using var coordinator = Create(engine, time);
        PlaybackSessionSnapshot logical = await StartPlayingAsync(coordinator);

        engine.EmitFailure(logical.SessionId, DomainErrorCode.StreamInterrupted);
        await WaitForWaitingAttemptAsync(coordinator, attemptNumber: 1);
        await AdvanceAsync(time, TimeSpan.FromSeconds(1));
        await WaitUntilAsync(() => engine.OpenCount == 2);
        PlaybackSessionId physical = engine.OpenSessions.ToArray()[^1];

        Assert.AreEqual(PlaybackState.Buffering, engine.Current.State);
        Assert.AreEqual(PlaybackState.Reconnecting, coordinator.Current.State);
        Assert.AreEqual(PlaybackReconnectPhase.Attempting, coordinator.Current.Reconnect?.Phase);

        engine.EmitState(logical.SessionId, PlaybackState.Playing);
        await Task.Yield();
        await Task.Yield();
        Assert.AreEqual(PlaybackState.Reconnecting, coordinator.Current.State);

        engine.EmitState(physical, PlaybackState.Playing);
        await WaitUntilAsync(() => coordinator.Current.State == PlaybackState.Playing);

        Assert.AreEqual(logical.SessionId, coordinator.Current.SessionId);
        Assert.AreEqual(2, engine.OpenCount);
        Assert.AreEqual(1, engine.StopCount);
    }

    [TestMethod]
    public async Task StopWhileReconnectWaitsForPlayablePreventsLateSuccess()
    {
        FakeTimeProvider time = TestTime.Create(Start);
        var engine = new ReconnectPlaybackEngine
        {
            HoldReconnectAtBuffering = true,
        };
        await using var coordinator = Create(engine, time);
        PlaybackSessionSnapshot logical = await StartPlayingAsync(coordinator);

        engine.EmitFailure(logical.SessionId, DomainErrorCode.StreamInterrupted);
        await WaitForWaitingAttemptAsync(coordinator, attemptNumber: 1);
        await AdvanceAsync(time, TimeSpan.FromSeconds(1));
        await WaitUntilAsync(() => engine.OpenCount == 2);
        PlaybackSessionId physical = engine.OpenSessions.ToArray()[^1];

        PlaybackEngineOperationResult stopped = await coordinator.StopAsync()
            .AsTask()
            .WaitAsync(TimeSpan.FromSeconds(2));
        engine.EmitState(physical, PlaybackState.Playing);
        await AdvanceAsync(time, TimeSpan.FromSeconds(30));

        Assert.IsTrue(stopped.IsSuccess);
        Assert.AreEqual(PlaybackState.Closed, coordinator.Current.State);
        Assert.AreEqual(2, engine.OpenCount);
        CollectionAssert.Contains(engine.StopSessions.ToArray(), physical);
    }

    [TestMethod]
    public async Task ReconnectPlayableWaitExpiresAtExactDeadlineAndDrainsPhysical()
    {
        FakeTimeProvider time = TestTime.Create(Start);
        var engine = new ReconnectPlaybackEngine
        {
            HoldReconnectAtBuffering = true,
        };
        await using var coordinator = Create(engine, time);
        PlaybackSessionSnapshot logical = await StartPlayingAsync(coordinator);

        engine.EmitFailure(logical.SessionId, DomainErrorCode.StreamInterrupted);
        await WaitForWaitingAttemptAsync(coordinator, attemptNumber: 1);
        await AdvanceAsync(time, TimeSpan.FromSeconds(1));
        await WaitUntilAsync(() => engine.OpenCount == 2);
        PlaybackSessionId physical = engine.OpenSessions.ToArray()[^1];

        await AdvanceAsync(time, TimeSpan.FromSeconds(29));
        await WaitUntilAsync(() => coordinator.Current is
        {
            State: PlaybackState.Failed,
            Error.Code: DomainErrorCode.ReconnectExhausted,
        });
        engine.EmitState(physical, PlaybackState.Playing);
        await Task.Yield();

        Assert.AreEqual(DomainErrorCode.ReconnectExhausted, coordinator.Current.Error?.Code);
        Assert.AreEqual(2, engine.OpenCount);
        CollectionAssert.Contains(engine.StopSessions.ToArray(), physical);
    }

    [TestMethod]
    public async Task PermanentFailureDoesNotStartReconnect()
    {
        FakeTimeProvider time = TestTime.Create(Start);
        var engine = new ReconnectPlaybackEngine();
        await using var coordinator = Create(engine, time);
        PlaybackSessionSnapshot first = await StartPlayingAsync(coordinator);

        engine.EmitFailure(first.SessionId, DomainErrorCode.AuthenticationRejected);
        await AdvanceAsync(time, TimeSpan.FromSeconds(30));

        Assert.AreEqual(PlaybackState.Failed, coordinator.Current.State);
        Assert.AreEqual(DomainErrorCode.AuthenticationRejected, coordinator.Current.Error?.Code);
        Assert.IsNull(coordinator.Current.Reconnect);
        Assert.AreEqual(1, engine.OpenCount);
        Assert.IsFalse(coordinator.CanRetryReconnect);
    }

    [TestMethod]
    public async Task DuplicateTransientCallbacksCoalesceIntoOneChain()
    {
        FakeTimeProvider time = TestTime.Create(Start);
        var engine = new ReconnectPlaybackEngine();
        await using var coordinator = Create(engine, time);
        PlaybackSessionSnapshot first = await StartPlayingAsync(coordinator);

        engine.EmitFailure(first.SessionId, DomainErrorCode.StreamInterrupted);
        await WaitForWaitingAttemptAsync(coordinator, attemptNumber: 1);
        PlaybackReconnectCorrelationId correlation = coordinator.Current.Reconnect!.CorrelationId;
        engine.EmitFailure(first.SessionId, DomainErrorCode.StreamInterrupted);
        await AdvanceAsync(time, TimeSpan.FromSeconds(1));
        await WaitUntilAsync(() => coordinator.Current.State == PlaybackState.Playing);

        Assert.AreEqual(2, engine.OpenCount);
        Assert.AreEqual(1, engine.StopCount);
        Assert.IsFalse(correlation.IsEmpty);
    }

    [TestMethod]
    public async Task ConcurrentDuplicateInFailureBeginGapUsesOneCorrelationAndOneAttempt()
    {
        FakeTimeProvider innerTime = TestTime.Create(Start);
        using var time = new FirstTimestampBlockingTimeProvider(innerTime);
        var engine = new ReconnectPlaybackEngine();
        await using var coordinator = Create(engine, time);
        PlaybackSessionSnapshot first = await StartPlayingAsync(coordinator);
        var correlations = new ConcurrentQueue<PlaybackReconnectCorrelationId>();
        coordinator.StateChanged += (_, args) =>
        {
            if (args.Snapshot.Reconnect is { } reconnect)
            {
                correlations.Enqueue(reconnect.CorrelationId);
            }
        };

        Task firstCallback = Task.Run(() =>
            engine.EmitFailure(first.SessionId, DomainErrorCode.StreamInterrupted));
        await time.FirstTimestampEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Task duplicateCallback = Task.Run(() =>
            engine.EmitFailure(first.SessionId, DomainErrorCode.StreamInterrupted));

        try
        {
            await duplicateCallback.WaitAsync(TimeSpan.FromSeconds(2));
        }
        finally
        {
            time.ReleaseFirstTimestamp.Set();
        }

        await firstCallback.WaitAsync(TimeSpan.FromSeconds(2));
        await WaitForWaitingAttemptAsync(coordinator, attemptNumber: 1);
        await AdvanceAsync(innerTime, TimeSpan.FromSeconds(1));
        await WaitUntilAsync(() => coordinator.Current.State == PlaybackState.Playing);

        Assert.AreEqual(2, engine.OpenCount);
        Assert.AreEqual(1, correlations.Distinct().Count());
        AssertFreshPhysicalSessions(engine.OpenSessions.ToArray(), expectedCount: 2);
    }

    [TestMethod]
    public async Task ThreeTransientAttemptFailuresEndWithReconnectExhausted()
    {
        FakeTimeProvider time = TestTime.Create(Start);
        var engine = new ReconnectPlaybackEngine();
        engine.EnqueueReconnectOpenFailure(DomainErrorCode.StreamInterrupted);
        engine.EnqueueReconnectOpenFailure(DomainErrorCode.StreamInterrupted);
        engine.EnqueueReconnectOpenFailure(DomainErrorCode.StreamInterrupted);
        await using var coordinator = Create(engine, time);
        PlaybackSessionSnapshot first = await StartPlayingAsync(coordinator);

        engine.EmitFailure(first.SessionId, DomainErrorCode.StreamInterrupted);
        await WaitForWaitingAttemptAsync(coordinator, attemptNumber: 1);
        await AdvanceAsync(time, TimeSpan.FromSeconds(1));
        await WaitForWaitingAttemptAsync(coordinator, attemptNumber: 2);
        await AdvanceAsync(time, TimeSpan.FromSeconds(2));
        await WaitForWaitingAttemptAsync(coordinator, attemptNumber: 3);
        await AdvanceAsync(time, TimeSpan.FromSeconds(4));
        await WaitUntilAsync(() => coordinator.Current.State == PlaybackState.Failed);

        Assert.AreEqual(4, engine.OpenCount);
        AssertFreshPhysicalSessions(engine.OpenSessions.ToArray(), expectedCount: 4);
        Assert.AreEqual(DomainErrorCode.ReconnectExhausted, coordinator.Current.Error?.Code);
        Assert.IsNull(coordinator.Current.Reconnect);
        Assert.IsTrue(coordinator.CanRetryReconnect);
    }

    [TestMethod]
    public async Task ManualRetryStartsImmediatelyWithFreshBudgetAndCoalescesDuplicates()
    {
        FakeTimeProvider time = TestTime.Create(Start);
        var engine = new ReconnectPlaybackEngine
        {
            BlockFirstReconnectOpen = true,
            BlockingReconnectOpenOrdinal = 5,
        };
        await using var coordinator = Create(engine, time);
        await ExhaustReconnectAsync(coordinator, engine, time);

        ValueTask<PlaybackEngineOperationResult> firstRetry = coordinator.RetryReconnectAsync();
        Assert.IsTrue(firstRetry.IsCompletedSuccessfully);
        Assert.IsTrue((await firstRetry).IsSuccess);
        await engine.FirstReconnectOpenEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));

        PlaybackReconnectSnapshot progress = coordinator.Current.Reconnect!;
        Assert.AreEqual(PlaybackReconnectPhase.Attempting, progress.Phase);
        Assert.AreEqual(1, progress.AttemptNumber);
        Assert.AreEqual(TimeSpan.FromSeconds(30), progress.RemainingBudget);
        Assert.IsFalse(coordinator.CanRetryReconnect);

        ValueTask<PlaybackEngineOperationResult> duplicate = coordinator.RetryReconnectAsync();
        Assert.IsTrue(duplicate.IsCompletedSuccessfully);
        Assert.IsTrue((await duplicate).IsSuccess);
        Assert.AreEqual(5, engine.OpenCount);

        Assert.IsTrue((await coordinator.StopAsync()).IsSuccess);
        await AdvanceAsync(time, TimeSpan.FromSeconds(30));
        Assert.AreEqual(PlaybackState.Closed, coordinator.Current.State);
        Assert.AreEqual(5, engine.OpenCount);
    }

    [TestMethod]
    public async Task ManualRetryKeepsLogicalSessionAndRestoresDesiredControls()
    {
        FakeTimeProvider time = TestTime.Create(Start);
        var engine = new ReconnectPlaybackEngine();
        await using var coordinator = Create(engine, time);
        PlaybackSessionSnapshot logical = await StartPlayingAsync(coordinator);
        await coordinator.SetVolumeAsync(logical.SessionId, PlaybackVolume.FromPercent(37));
        await coordinator.SetMutedAsync(logical.SessionId, isMuted: true);
        await coordinator.SetAspectModeAsync(logical.SessionId, PlaybackAspectMode.Fill);
        await ExhaustCurrentReconnectAsync(coordinator, engine, time, logical.SessionId);

        PlaybackEngineOperationResult retry = await coordinator.RetryReconnectAsync();
        await WaitUntilAsync(() => coordinator.Current.State == PlaybackState.Playing);

        Assert.IsTrue(retry.IsSuccess);
        Assert.IsFalse(coordinator.CanRetryReconnect);
        Assert.AreEqual(logical.SessionId, coordinator.Current.SessionId);
        Assert.AreEqual(5, engine.OpenCount);
        PlaybackSessionId physical = engine.OpenSessions.ToArray()[^1];
        Assert.AreNotEqual(logical.SessionId, physical);
        Assert.AreEqual(37, coordinator.CurrentControls.Volume.Percent);
        Assert.IsTrue(coordinator.CurrentControls.IsMuted);
        Assert.AreEqual(PlaybackAspectMode.Fill, coordinator.CurrentControls.AspectMode);
        CollectionAssert.IsSubsetOf(
            new[]
            {
                $"Volume:{physical.Value}:37",
                $"Muted:{physical.Value}:True",
                $"Aspect:{physical.Value}:Fill",
            },
            engine.Journal.ToArray());
    }

    [TestMethod]
    public async Task CanonicalManualAttemptFailureAllowsManualRetry()
    {
        FakeTimeProvider time = TestTime.Create(Start);
        var engine = new ReconnectPlaybackEngine();
        await using var coordinator = Create(engine, time);
        PlaybackSessionSnapshot logical = await StartPlayingAsync(coordinator);
        engine.EnqueueReconnectOpenFailure(DomainErrorCode.PlaybackStartFailed);

        engine.EmitFailure(logical.SessionId, DomainErrorCode.StreamInterrupted);
        await WaitForWaitingAttemptAsync(coordinator, attemptNumber: 1);
        await AdvanceAsync(time, TimeSpan.FromSeconds(1));
        await WaitUntilAsync(() => coordinator.Current is
        {
            State: PlaybackState.Failed,
            Error.Code: DomainErrorCode.PlaybackStartFailed,
        });

        Assert.IsTrue(coordinator.CanRetryReconnect);
        Assert.IsTrue((await coordinator.RetryReconnectAsync()).IsSuccess);
        await WaitUntilAsync(() => coordinator.Current.State == PlaybackState.Playing);
        Assert.AreEqual(logical.SessionId, coordinator.Current.SessionId);
        Assert.AreEqual(3, engine.OpenCount);
    }

    [TestMethod]
    public async Task NeverAttemptFailureRejectsManualRetry()
    {
        FakeTimeProvider time = TestTime.Create(Start);
        var engine = new ReconnectPlaybackEngine();
        await using var coordinator = Create(engine, time);
        PlaybackSessionSnapshot logical = await StartPlayingAsync(coordinator);
        engine.EnqueueReconnectOpenFailure(DomainErrorCode.AuthenticationRejected);

        engine.EmitFailure(logical.SessionId, DomainErrorCode.StreamInterrupted);
        await WaitForWaitingAttemptAsync(coordinator, attemptNumber: 1);
        await AdvanceAsync(time, TimeSpan.FromSeconds(1));
        await WaitUntilAsync(() => coordinator.Current is
        {
            State: PlaybackState.Failed,
            Error.Code: DomainErrorCode.AuthenticationRejected,
        });

        Assert.IsFalse(coordinator.CanRetryReconnect);
        PlaybackEngineOperationResult retry = await coordinator.RetryReconnectAsync();
        Assert.IsFalse(retry.IsSuccess);
        Assert.AreEqual(DomainErrorCode.OperationCancelled, retry.Error?.Code);
        Assert.AreEqual(2, engine.OpenCount);
    }

    [TestMethod]
    public async Task SourceRetirementInvalidatesTerminalManualRetryAndPreventsLaterOpen()
    {
        FakeTimeProvider time = TestTime.Create(Start);
        var engine = new ReconnectPlaybackEngine();
        await using var coordinator = Create(engine, time);
        PlaybackSessionSnapshot logical = await ExhaustReconnectAsync(coordinator, engine, time);

        Assert.IsTrue((await coordinator.ReleaseSourceAsync(logical.SourceId!.Value)).IsSuccess);
        PlaybackEngineOperationResult retry = await coordinator.RetryReconnectAsync();
        await AdvanceAsync(time, TimeSpan.FromSeconds(30));

        Assert.IsFalse(retry.IsSuccess);
        Assert.AreEqual(DomainErrorCode.OperationCancelled, retry.Error?.Code);
        Assert.IsFalse(coordinator.CanRetryReconnect);
        Assert.AreEqual(PlaybackState.Closed, coordinator.Current.State);
        Assert.AreEqual(4, engine.OpenCount);
    }

    [TestMethod]
    public async Task ReplacementAndDisposeInvalidateTerminalManualRetry()
    {
        FakeTimeProvider time = TestTime.Create(Start);
        var engine = new ReconnectPlaybackEngine();
        var coordinator = Create(engine, time);
        await ExhaustReconnectAsync(coordinator, engine, time);

        PlaybackSessionSnapshot? replacement = await coordinator.StartAsync(
            SourceId.Generate(),
            ChannelId.Generate());
        PlaybackEngineOperationResult replacedRetry = await coordinator.RetryReconnectAsync();
        Assert.IsNotNull(replacement);
        Assert.IsFalse(replacedRetry.IsSuccess);
        Assert.AreEqual(DomainErrorCode.OperationCancelled, replacedRetry.Error?.Code);
        Assert.AreEqual(5, engine.OpenCount);

        await coordinator.DisposeAsync();
        PlaybackEngineOperationResult disposedRetry = await coordinator.RetryReconnectAsync();
        Assert.IsFalse(disposedRetry.IsSuccess);
        Assert.AreEqual(DomainErrorCode.OperationCancelled, disposedRetry.Error?.Code);
        Assert.IsFalse(coordinator.CanRetryReconnect);
        await AdvanceAsync(time, TimeSpan.FromSeconds(30));
        Assert.AreEqual(5, engine.OpenCount);
    }

    [TestMethod]
    public async Task StopDuringWaitCancelsChainAndPreventsLaterOpen()
    {
        FakeTimeProvider time = TestTime.Create(Start);
        var engine = new ReconnectPlaybackEngine();
        await using var coordinator = Create(engine, time);
        PlaybackSessionSnapshot first = await StartPlayingAsync(coordinator);
        engine.EmitFailure(first.SessionId, DomainErrorCode.StreamInterrupted);
        await WaitForWaitingAttemptAsync(coordinator, attemptNumber: 1);

        PlaybackEngineOperationResult stopped = await coordinator.StopAsync();
        await AdvanceAsync(time, TimeSpan.FromSeconds(30));

        Assert.IsTrue(stopped.IsSuccess);
        Assert.AreEqual(PlaybackState.Closed, coordinator.Current.State);
        Assert.AreEqual(1, engine.OpenCount);
        Assert.AreEqual(1, engine.StopCount);
    }

    [TestMethod]
    public async Task TransientCallbackWhileStoppingCannotStartReconnect()
    {
        FakeTimeProvider time = TestTime.Create(Start);
        var engine = new ReconnectPlaybackEngine { BlockFirstStop = true };
        await using var coordinator = Create(engine, time);
        PlaybackSessionSnapshot first = await StartPlayingAsync(coordinator);
        var correlations = new ConcurrentQueue<PlaybackReconnectCorrelationId>();
        coordinator.StateChanged += (_, args) =>
        {
            if (args.Snapshot.Reconnect is { } reconnect)
            {
                correlations.Enqueue(reconnect.CorrelationId);
            }
        };

        Task<PlaybackEngineOperationResult> stop = coordinator.StopAsync().AsTask();
        await engine.FirstStopEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.AreEqual(PlaybackState.Stopping, coordinator.Current.State);

        try
        {
            engine.EmitFailure(first.SessionId, DomainErrorCode.StreamInterrupted);
            Assert.AreEqual(PlaybackState.Stopping, coordinator.Current.State);
            Assert.IsTrue(correlations.IsEmpty);
        }
        finally
        {
            engine.ReleaseFirstStop.TrySetResult(true);
        }

        Assert.IsTrue((await stop.WaitAsync(TimeSpan.FromSeconds(2))).IsSuccess);
        await AdvanceAsync(time, TimeSpan.FromSeconds(30));
        Assert.AreEqual(PlaybackState.Closed, coordinator.Current.State);
        Assert.AreEqual(1, engine.OpenCount);
        Assert.IsTrue(correlations.IsEmpty);
    }

    [TestMethod]
    public async Task ReplacementDuringWaitCancelsOldChainAndOpensOnlyReplacement()
    {
        FakeTimeProvider time = TestTime.Create(Start);
        var engine = new ReconnectPlaybackEngine();
        await using var coordinator = Create(engine, time);
        PlaybackSessionSnapshot first = await StartPlayingAsync(coordinator);
        engine.EmitFailure(first.SessionId, DomainErrorCode.StreamInterrupted);
        await WaitForWaitingAttemptAsync(coordinator, attemptNumber: 1);
        SourceId replacementSource = SourceId.Generate();

        PlaybackSessionSnapshot? replacement = await coordinator.StartAsync(
            replacementSource,
            ChannelId.Generate());
        await AdvanceAsync(time, TimeSpan.FromSeconds(30));

        Assert.IsNotNull(replacement);
        Assert.AreEqual(PlaybackState.Playing, replacement.State);
        Assert.AreEqual(replacementSource, coordinator.Current.SourceId);
        Assert.AreEqual(2, engine.OpenCount);
        string[] journal = engine.Journal.ToArray();
        int oldStop = Array.FindIndex(journal, entry => entry == $"Stop:{first.SessionId.Value}");
        int replacementOpen = Array.FindIndex(
            journal,
            entry => entry == $"Open:{replacement.SessionId.Value}");
        Assert.IsGreaterThanOrEqualTo(0, oldStop);
        Assert.IsGreaterThan(oldStop, replacementOpen);
    }

    [TestMethod]
    public async Task OldPhysicalCallbacksDuringReplacementIntentCannotAffectReplacement()
    {
        FakeTimeProvider time = TestTime.Create(Start);
        var engine = new ReconnectPlaybackEngine();
        await using var coordinator = Create(engine, time);
        PlaybackSessionSnapshot first = await StartPlayingAsync(coordinator);
        SourceId replacementSource = SourceId.Generate();
        var correlations = new ConcurrentQueue<PlaybackReconnectCorrelationId>();
        coordinator.StateChanged += (_, args) =>
        {
            if (args.Snapshot.Reconnect is { } reconnect)
            {
                correlations.Enqueue(reconnect.CorrelationId);
            }
        };
        SemaphoreSlim engineGate = GetEngineGate(coordinator);
        await engineGate.WaitAsync();
        Task<PlaybackSessionSnapshot?> replacement;

        try
        {
            replacement = coordinator.StartAsync(
                replacementSource,
                ChannelId.Generate()).AsTask();
            await WaitUntilAsync(() => coordinator.Current is
            {
                State: PlaybackState.Opening,
                SourceId: var source,
            } && source == replacementSource);
            Assert.IsFalse(replacement.IsCompleted);

            engine.EmitFailure(first.SessionId, DomainErrorCode.StreamInterrupted);
            engine.EmitState(first.SessionId, PlaybackState.Playing);

            Assert.AreEqual(PlaybackState.Opening, coordinator.Current.State);
            Assert.AreEqual(replacementSource, coordinator.Current.SourceId);
            Assert.IsNull(coordinator.Current.Reconnect);
            Assert.IsTrue(correlations.IsEmpty);
        }
        finally
        {
            engineGate.Release();
        }

        PlaybackSessionSnapshot? result =
            await replacement.WaitAsync(TimeSpan.FromSeconds(2));
        await AdvanceAsync(time, TimeSpan.FromSeconds(30));

        Assert.IsNotNull(result);
        Assert.AreEqual(PlaybackState.Playing, result.State);
        Assert.AreEqual(replacementSource, coordinator.Current.SourceId);
        Assert.AreEqual(2, engine.OpenCount);
        Assert.IsTrue(correlations.IsEmpty);
    }

    [TestMethod]
    public async Task NonMatchingSourceReleaseLeavesReconnectActive()
    {
        FakeTimeProvider time = TestTime.Create(Start);
        var engine = new ReconnectPlaybackEngine();
        await using var coordinator = Create(engine, time);
        PlaybackSessionSnapshot first = await StartPlayingAsync(coordinator);
        engine.EmitFailure(first.SessionId, DomainErrorCode.StreamInterrupted);
        await WaitForWaitingAttemptAsync(coordinator, attemptNumber: 1);

        PlaybackEngineOperationResult released = await coordinator.ReleaseSourceAsync(SourceId.Generate());
        await AdvanceAsync(time, TimeSpan.FromSeconds(1));
        await WaitUntilAsync(() => coordinator.Current.State == PlaybackState.Playing);

        Assert.IsTrue(released.IsSuccess);
        Assert.AreEqual(first.SourceId, coordinator.Current.SourceId);
        Assert.AreEqual(2, engine.OpenCount);
    }

    [TestMethod]
    public async Task MatchingSourceReleaseCancelsReconnectAndPreventsLaterOpen()
    {
        FakeTimeProvider time = TestTime.Create(Start);
        var engine = new ReconnectPlaybackEngine();
        await using var coordinator = Create(engine, time);
        PlaybackSessionSnapshot first = await StartPlayingAsync(coordinator);
        engine.EmitFailure(first.SessionId, DomainErrorCode.StreamInterrupted);
        await WaitForWaitingAttemptAsync(coordinator, attemptNumber: 1);

        PlaybackEngineOperationResult released = await coordinator.ReleaseSourceAsync(first.SourceId!.Value);
        await AdvanceAsync(time, TimeSpan.FromSeconds(30));

        Assert.IsTrue(released.IsSuccess);
        Assert.AreEqual(PlaybackState.Closed, coordinator.Current.State);
        Assert.AreEqual(1, engine.OpenCount);
        Assert.AreEqual(1, engine.StopCount);
        Assert.IsNull(await coordinator.StartAsync(first.SourceId.Value, ChannelId.Generate()));
    }

    [TestMethod]
    public async Task TransientCallbackDuringAttemptContinuesTheSameChain()
    {
        FakeTimeProvider time = TestTime.Create(Start);
        var engine = new ReconnectPlaybackEngine();
        engine.EnqueueReconnectOpenFailure(
            DomainErrorCode.StreamInterrupted,
            emitCallback: true);
        await using var coordinator = Create(engine, time);
        PlaybackSessionSnapshot first = await StartPlayingAsync(coordinator);
        var correlations = new ConcurrentQueue<PlaybackReconnectCorrelationId>();
        coordinator.StateChanged += (_, args) =>
        {
            if (args.Snapshot.Reconnect is { } reconnect)
            {
                correlations.Enqueue(reconnect.CorrelationId);
            }
        };

        engine.EmitFailure(first.SessionId, DomainErrorCode.StreamInterrupted);
        await WaitForWaitingAttemptAsync(coordinator, attemptNumber: 1);
        PlaybackReconnectCorrelationId expected = coordinator.Current.Reconnect!.CorrelationId;
        await AdvanceAsync(time, TimeSpan.FromSeconds(1));
        await WaitForWaitingAttemptAsync(coordinator, attemptNumber: 2);
        await AdvanceAsync(time, TimeSpan.FromSeconds(2));
        await WaitUntilAsync(() => coordinator.Current.State == PlaybackState.Playing);

        Assert.AreEqual(3, engine.OpenCount);
        AssertFreshPhysicalSessions(engine.OpenSessions.ToArray(), expectedCount: 3);
        Assert.IsFalse(correlations.IsEmpty);
        Assert.IsTrue(correlations.All(correlation => correlation == expected));
    }

    [TestMethod]
    public async Task StaleOldPhysicalFailureCannotRegressRecoveredLogicalSession()
    {
        FakeTimeProvider time = TestTime.Create(Start);
        var engine = new ReconnectPlaybackEngine();
        await using var coordinator = Create(engine, time);
        PlaybackSessionSnapshot first = await StartPlayingAsync(coordinator);
        engine.EmitFailure(first.SessionId, DomainErrorCode.StreamInterrupted);
        await WaitForWaitingAttemptAsync(coordinator, attemptNumber: 1);
        await AdvanceAsync(time, TimeSpan.FromSeconds(1));
        await WaitUntilAsync(() => coordinator.Current.State == PlaybackState.Playing);

        engine.EmitFailure(first.SessionId, DomainErrorCode.StreamInterrupted);
        await AdvanceAsync(time, TimeSpan.FromSeconds(30));

        Assert.AreEqual(first.SessionId, coordinator.Current.SessionId);
        Assert.AreEqual(PlaybackState.Playing, coordinator.Current.State);
        Assert.AreEqual(2, engine.OpenCount);
        AssertFreshPhysicalSessions(engine.OpenSessions.ToArray(), expectedCount: 2);
    }

    [TestMethod]
    [DataRow(false, DomainErrorCode.AuthenticationRejected)]
    [DataRow(true, DomainErrorCode.AuthenticationRejected)]
    [DataRow(false, DomainErrorCode.StreamInterrupted)]
    [DataRow(true, DomainErrorCode.StreamInterrupted)]
    public async Task QueuedPlayPauseDoesNotDispatchAcrossTerminalOrReconnectRace(
        bool pause,
        DomainErrorCode callbackError)
    {
        FakeTimeProvider time = TestTime.Create(Start);
        var engine = new ReconnectPlaybackEngine();
        await using var coordinator = Create(engine, time);
        PlaybackSessionSnapshot logical = await StartPlayingAsync(coordinator);
        PlaybackSessionId physical = await ReconnectSuccessfullyAsync(
            coordinator,
            engine,
            time,
            logical.SessionId);
        var correlations = new ConcurrentQueue<PlaybackReconnectCorrelationId>();
        coordinator.StateChanged += (_, args) =>
        {
            if (args.Snapshot.Reconnect is { } reconnect)
            {
                correlations.Enqueue(reconnect.CorrelationId);
            }
        };
        int dispatchesBefore = CountCommandDispatches(engine, physical, pause);
        int stopsBefore = engine.StopSessions.Count;
        SemaphoreSlim engineGate = GetEngineGate(coordinator);
        await engineGate.WaitAsync();
        Task<PlaybackEngineOperationResult> pending;

        try
        {
            pending = InvokeCommandAsync(coordinator, pause);
            Assert.IsFalse(pending.IsCompleted);
            engine.EmitFailure(physical, callbackError);
        }
        finally
        {
            engineGate.Release();
        }

        PlaybackEngineOperationResult result =
            await pending.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.IsFalse(result.IsSuccess);
        Assert.AreEqual(
            callbackError == DomainErrorCode.StreamInterrupted
                ? DomainErrorCode.OperationCancelled
                : callbackError,
            result.Error?.Code);
        Assert.AreEqual(dispatchesBefore, CountCommandDispatches(engine, physical, pause));

        if (callbackError == DomainErrorCode.StreamInterrupted)
        {
            await WaitForWaitingAttemptAsync(coordinator, attemptNumber: 1);
            Assert.AreEqual(1, correlations.Distinct().Count());
            await AdvanceAsync(time, TimeSpan.FromSeconds(1));
            await WaitUntilAsync(() => coordinator.Current.State == PlaybackState.Playing);
            Assert.AreEqual(3, engine.OpenCount);
        }
        else
        {
            PlaybackSessionId[] raceStops = engine.StopSessions
                .Skip(stopsBefore)
                .ToArray();
            CollectionAssert.AreEqual(new[] { physical }, raceStops);
            Assert.IsFalse(raceStops.Contains(logical.SessionId));
            await AdvanceAsync(time, TimeSpan.FromSeconds(30));
            Assert.AreEqual(2, engine.OpenCount);
            Assert.IsTrue(correlations.IsEmpty);
        }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task TransientCallbackDuringPlayPauseDispatchKeepsSingleReconnectChain(
        bool pause)
    {
        FakeTimeProvider time = TestTime.Create(Start);
        var engine = new ReconnectPlaybackEngine();
        await using var coordinator = Create(engine, time);
        PlaybackSessionSnapshot logical = await StartPlayingAsync(coordinator);
        PlaybackSessionId physical = await ReconnectSuccessfullyAsync(
            coordinator,
            engine,
            time,
            logical.SessionId);
        var correlations = new ConcurrentQueue<PlaybackReconnectCorrelationId>();
        coordinator.StateChanged += (_, args) =>
        {
            if (args.Snapshot.Reconnect is { } reconnect)
            {
                correlations.Enqueue(reconnect.CorrelationId);
            }
        };
        int dispatchesBefore = CountCommandDispatches(engine, physical, pause);
        engine.EmitTransientFailureOnNextCommand(pause);

        PlaybackEngineOperationResult result =
            await InvokeCommandAsync(coordinator, pause).WaitAsync(TimeSpan.FromSeconds(2));
        await WaitForWaitingAttemptAsync(coordinator, attemptNumber: 1);

        Assert.IsFalse(result.IsSuccess);
        Assert.AreEqual(DomainErrorCode.OperationCancelled, result.Error?.Code);
        Assert.AreEqual(PlaybackState.Reconnecting, coordinator.Current.State);
        Assert.AreEqual(dispatchesBefore + 1, CountCommandDispatches(engine, physical, pause));
        Assert.AreEqual(1, correlations.Distinct().Count());

        await AdvanceAsync(time, TimeSpan.FromSeconds(1));
        await WaitUntilAsync(() => coordinator.Current.State == PlaybackState.Playing);
        Assert.AreEqual(logical.SessionId, coordinator.Current.SessionId);
        Assert.AreEqual(3, engine.OpenCount);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task TransientCallbackDuringControlTrackDoesNotApplyStaleSnapshot(
        bool useTrackQuery)
    {
        FakeTimeProvider time = TestTime.Create(Start);
        var engine = new ReconnectPlaybackEngine();
        await using var coordinator = Create(engine, time);
        PlaybackSessionSnapshot logical = await StartPlayingAsync(coordinator);
        PlaybackSessionId physical = await ReconnectSuccessfullyAsync(
            coordinator,
            engine,
            time,
            logical.SessionId);
        PlaybackControlSnapshot controlsBefore = coordinator.CurrentControls;
        Assert.IsNull(coordinator.CurrentTracks);
        var correlations = new ConcurrentQueue<PlaybackReconnectCorrelationId>();
        coordinator.StateChanged += (_, args) =>
        {
            if (args.Snapshot.Reconnect is { } reconnect)
            {
                correlations.Enqueue(reconnect.CorrelationId);
            }
        };

        if (useTrackQuery)
        {
            engine.EmitTransientFailureOnNextTrackQuery();
            DomainResult<PlaybackTrackSnapshot> result =
                await coordinator.GetTracksAsync(logical.SessionId)
                    .AsTask()
                    .WaitAsync(TimeSpan.FromSeconds(2));
            Assert.IsFalse(result.IsSuccess);
            Assert.AreEqual(DomainErrorCode.OperationCancelled, result.Error?.Code);
            Assert.IsNull(coordinator.CurrentTracks);
            Assert.IsTrue(engine.Journal.Contains($"Tracks:{physical.Value}"));
        }
        else
        {
            engine.EmitTransientFailureOnNextVolume();
            PlaybackEngineOperationResult result =
                await coordinator.SetVolumeAsync(
                    logical.SessionId,
                    PlaybackVolume.FromPercent(42))
                    .AsTask()
                    .WaitAsync(TimeSpan.FromSeconds(2));
            Assert.IsFalse(result.IsSuccess);
            Assert.AreEqual(DomainErrorCode.OperationCancelled, result.Error?.Code);
            Assert.AreEqual(controlsBefore, coordinator.CurrentControls);
            Assert.IsTrue(engine.Journal.Contains($"Volume:{physical.Value}:42"));
        }

        await WaitForWaitingAttemptAsync(coordinator, attemptNumber: 1);
        Assert.AreEqual(PlaybackState.Reconnecting, coordinator.Current.State);
        Assert.AreEqual(1, correlations.Distinct().Count());
        await AdvanceAsync(time, TimeSpan.FromSeconds(1));
        await WaitUntilAsync(() => coordinator.Current.State == PlaybackState.Playing);
        Assert.AreEqual(logical.SessionId, coordinator.Current.SessionId);
        Assert.IsNull(coordinator.CurrentTracks);
        Assert.AreEqual(controlsBefore, coordinator.CurrentControls);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task TerminalPreDispatchRaceStopsFreshPhysicalSessionNotLogical(
        bool useTrackQuery)
    {
        FakeTimeProvider time = TestTime.Create(Start);
        var engine = new ReconnectPlaybackEngine();
        await using var coordinator = Create(engine, time);
        PlaybackSessionSnapshot first = await StartPlayingAsync(coordinator);
        engine.EmitFailure(first.SessionId, DomainErrorCode.StreamInterrupted);
        await WaitForWaitingAttemptAsync(coordinator, attemptNumber: 1);
        await AdvanceAsync(time, TimeSpan.FromSeconds(1));
        await WaitUntilAsync(() => coordinator.Current.State == PlaybackState.Playing);
        PlaybackSessionId physicalSession = engine.OpenSessions.ToArray()[1];
        Assert.AreNotEqual(first.SessionId, physicalSession);
        int stopsBeforeRace = engine.StopSessions.Count;
        SemaphoreSlim engineGate = GetEngineGate(coordinator);
        await engineGate.WaitAsync();

        Task<PlaybackEngineOperationResult>? control = null;
        Task<DomainResult<PlaybackTrackSnapshot>>? tracks = null;
        try
        {
            if (useTrackQuery)
            {
                tracks = coordinator.GetTracksAsync(first.SessionId).AsTask();
                Assert.IsFalse(tracks.IsCompleted);
            }
            else
            {
                control = coordinator.SetVolumeAsync(
                    first.SessionId,
                    PlaybackVolume.FromPercent(41)).AsTask();
                Assert.IsFalse(control.IsCompleted);
            }

            engine.EmitFailure(physicalSession, DomainErrorCode.AuthenticationRejected);
        }
        finally
        {
            engineGate.Release();
        }

        if (useTrackQuery)
        {
            DomainResult<PlaybackTrackSnapshot> result =
                await tracks!.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.IsFalse(result.IsSuccess);
            Assert.AreEqual(DomainErrorCode.AuthenticationRejected, result.Error?.Code);
        }
        else
        {
            PlaybackEngineOperationResult result =
                await control!.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.IsFalse(result.IsSuccess);
            Assert.AreEqual(DomainErrorCode.AuthenticationRejected, result.Error?.Code);
        }

        PlaybackSessionId[] raceStops = engine.StopSessions
            .Skip(stopsBeforeRace)
            .ToArray();
        CollectionAssert.AreEqual(new[] { physicalSession }, raceStops);
        Assert.IsFalse(raceStops.Contains(first.SessionId));
    }

    [TestMethod]
    public async Task DisposeCancelsAndDrainsBlockedReconnectAttempt()
    {
        FakeTimeProvider time = TestTime.Create(Start);
        var engine = new ReconnectPlaybackEngine
        {
            BlockFirstReconnectOpen = true,
            HoldFirstReconnectOpenAfterCancellation = true,
        };
        var coordinator = Create(engine, time);
        PlaybackSessionSnapshot first = await StartPlayingAsync(coordinator);
        engine.EmitFailure(first.SessionId, DomainErrorCode.StreamInterrupted);
        await WaitForWaitingAttemptAsync(coordinator, attemptNumber: 1);
        await AdvanceAsync(time, TimeSpan.FromSeconds(1));
        await engine.FirstReconnectOpenEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Task dispose = coordinator.DisposeAsync().AsTask();
        await engine.FirstReconnectOpenCancelled.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.IsFalse(dispose.IsCompleted);
        engine.ReleaseFirstReconnectOpen.TrySetResult(true);
        await dispose.WaitAsync(TimeSpan.FromSeconds(2));
        await AdvanceAsync(time, TimeSpan.FromSeconds(30));

        Assert.AreEqual(2, engine.OpenCount);
        Assert.AreEqual(1, engine.DisposeCount);
    }

    [TestMethod]
    public async Task ExactDeadlineAfterSuccessfulAttemptDrainsFreshPhysicalEvenWhenObserverThrows()
    {
        FakeTimeProvider innerTime = TestTime.Create(Start);
        using var time = new ArmableTimestampBlockingTimeProvider(innerTime);
        var engine = new ReconnectPlaybackEngine();
        await using var coordinator = Create(engine, time);
        PlaybackSessionSnapshot logical = await StartPlayingAsync(coordinator);
        bool terminalObserved = false;
        coordinator.StateChanged += (_, args) =>
        {
            if (args.Snapshot is
                {
                    State: PlaybackState.Failed,
                    Error.Code: DomainErrorCode.ReconnectExhausted,
                })
            {
                terminalObserved = true;
                throw new InvalidOperationException("Synthetic terminal observer failure.");
            }
        };
        engine.BlockNextPlayAfterSuccess();

        engine.EmitFailure(logical.SessionId, DomainErrorCode.StreamInterrupted);
        await WaitForWaitingAttemptAsync(coordinator, attemptNumber: 1);
        await AdvanceAsync(innerTime, TimeSpan.FromSeconds(1));
        await engine.BlockedPlaySuccessEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        PlaybackSessionId freshPhysical = engine.OpenSessions.ToArray()[^1];
        Assert.AreNotEqual(logical.SessionId, freshPhysical);
        time.ArmNextTimestamp();

        try
        {
            engine.ReleaseBlockedPlaySuccess.TrySetResult(true);
            await time.ArmedTimestampEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            innerTime.Advance(TimeSpan.FromSeconds(29));
        }
        finally
        {
            engine.ReleaseBlockedPlaySuccess.TrySetResult(true);
            time.ReleaseArmedTimestamp.Set();
        }

        await WaitUntilAsync(() => coordinator.Current is
        {
            State: PlaybackState.Failed,
            Error.Code: DomainErrorCode.ReconnectExhausted,
        });
        PlaybackSessionId drainedPhysical = await engine.ReconnectPhysicalStopCompleted.Task
            .WaitAsync(TimeSpan.FromSeconds(2));

        Assert.IsTrue(terminalObserved);
        Assert.AreEqual(DomainErrorCode.ReconnectExhausted, coordinator.Current.Error?.Code);
        Assert.AreEqual(freshPhysical, drainedPhysical);
        Assert.AreEqual(freshPhysical, engine.StopSessions.ToArray()[^1]);
        Assert.AreEqual(0, engine.ActiveSessionCount);
    }

    [TestMethod]
    public async Task ThrowingStateObserverDoesNotBlockLaterObservers()
    {
        var engine = new ReconnectPlaybackEngine();
        await using var coordinator = Create(engine, TestTime.Create(Start));
        var observed = new ConcurrentQueue<PlaybackState>();
        coordinator.StateChanged += (_, _) =>
            throw new InvalidOperationException("Synthetic observer failure.");
        coordinator.StateChanged += (_, args) => observed.Enqueue(args.Snapshot.State);

        PlaybackSessionSnapshot playing = await StartPlayingAsync(coordinator);

        Assert.AreEqual(PlaybackState.Playing, playing.State);
        Assert.IsTrue(observed.Contains(PlaybackState.Opening));
        Assert.IsTrue(observed.Contains(PlaybackState.Playing));
    }

    [TestMethod]
    public async Task ReentrantStopPreventsStaleReconnectDeliveryToLaterObservers()
    {
        FakeTimeProvider time = TestTime.Create(Start);
        var engine = new ReconnectPlaybackEngine();
        await using var coordinator = Create(engine, time);
        PlaybackSessionSnapshot logical = await StartPlayingAsync(coordinator);
        var laterObserver = new ConcurrentQueue<PlaybackState>();
        coordinator.StateChanged += (_, args) =>
        {
            if (args.Snapshot.State == PlaybackState.Reconnecting)
            {
                _ = coordinator.StopAsync().AsTask();
            }
        };
        coordinator.StateChanged += (_, args) => laterObserver.Enqueue(args.Snapshot.State);

        engine.EmitFailure(logical.SessionId, DomainErrorCode.StreamInterrupted);
        await WaitUntilAsync(() => coordinator.Current.State == PlaybackState.Closed);
        await AdvanceAsync(time, TimeSpan.FromSeconds(30));

        Assert.IsFalse(laterObserver.Contains(PlaybackState.Reconnecting));
        Assert.IsTrue(laterObserver.Contains(PlaybackState.Stopping));
        Assert.IsTrue(laterObserver.Contains(PlaybackState.Closed));
        Assert.AreEqual(1, engine.OpenCount);
    }

    [TestMethod]
    public void ReconnectingContractsRejectEngineOwnershipAndInactiveProgress()
    {
        PlaybackSessionId sessionId = CreateSessionId(1);
        var selection = new PlaybackSelection(SourceId.Generate(), ChannelId.Generate());
        PlaybackReconnectCorrelationId correlation = PlaybackReconnectCorrelationId.FromSequence(1);
        PlaybackReconnectSnapshot terminal = CreateReconnectSnapshot(
            PlaybackReconnectPhase.Succeeded,
            correlation,
            attemptNumber: 1,
            remainingDelay: TimeSpan.Zero,
            TimeSpan.FromSeconds(20),
            terminalErrorCode: null);

        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            PlaybackEngineSnapshot.Active(sessionId, PlaybackState.Reconnecting));
        AssertInvocationInnerException<ArgumentException>(() =>
            InvokeReconnectingFactory(sessionId, selection, PlaybackReconnectSnapshot.Idle()));
        AssertInvocationInnerException<ArgumentException>(() =>
            InvokeReconnectingFactory(sessionId, selection, terminal));
    }

    [TestMethod]
    public void ReconnectingContractRejectsAnErrorAlongsideActiveProgress()
    {
        PlaybackSessionId sessionId = CreateSessionId(1);
        SourceId sourceId = SourceId.Generate();
        ChannelId channelId = ChannelId.Generate();
        PlaybackReconnectSnapshot reconnect = CreateReconnectSnapshot(
            PlaybackReconnectPhase.Evaluating,
            PlaybackReconnectCorrelationId.FromSequence(1),
            attemptNumber: 0,
            TimeSpan.Zero,
            TimeSpan.FromSeconds(30),
            terminalErrorCode: null);
        ConstructorInfo constructor = typeof(PlaybackSessionSnapshot)
            .GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic)
            .Single(candidate => candidate.GetParameters().Length == 6);

        TargetInvocationException exception = Assert.ThrowsExactly<TargetInvocationException>(() =>
            constructor.Invoke(
            [
                sessionId,
                (SourceId?)sourceId,
                (ChannelId?)channelId,
                PlaybackState.Reconnecting,
                DomainError.Create(DomainErrorCode.StreamInterrupted),
                reconnect,
            ]));

        Assert.IsInstanceOfType<ArgumentException>(exception.InnerException);
    }

    private static PlaybackSessionId CreateSessionId(long value)
    {
        MethodInfo factory = typeof(PlaybackSessionId).GetMethod(
            "FromSequence",
            BindingFlags.Static | BindingFlags.NonPublic)!;
        return (PlaybackSessionId)factory.Invoke(null, [value])!;
    }

    private static SemaphoreSlim GetEngineGate(PlaybackSessionCoordinator coordinator)
    {
        FieldInfo field = typeof(PlaybackSessionCoordinator).GetField(
            "_engineGate",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        return (SemaphoreSlim)field.GetValue(coordinator)!;
    }

    private static Task<PlaybackEngineOperationResult> InvokeCommandAsync(
        PlaybackSessionCoordinator coordinator,
        bool pause) => pause
            ? coordinator.PauseAsync().AsTask()
            : coordinator.PlayAsync().AsTask();

    private static int CountCommandDispatches(
        ReconnectPlaybackEngine engine,
        PlaybackSessionId physicalSession,
        bool pause)
    {
        string expected = $"{(pause ? "Pause" : "Play")}:{physicalSession.Value}";
        return engine.Journal.Count(entry => entry == expected);
    }

    private static async Task<PlaybackSessionId> ReconnectSuccessfullyAsync(
        PlaybackSessionCoordinator coordinator,
        ReconnectPlaybackEngine engine,
        FakeTimeProvider time,
        PlaybackSessionId logicalSession)
    {
        engine.EmitFailure(logicalSession, DomainErrorCode.StreamInterrupted);
        await WaitForWaitingAttemptAsync(coordinator, attemptNumber: 1);
        await AdvanceAsync(time, TimeSpan.FromSeconds(1));
        await WaitUntilAsync(() => coordinator.Current.State == PlaybackState.Playing);
        PlaybackSessionId physical = engine.OpenSessions.ToArray()[^1];
        Assert.AreEqual(logicalSession, coordinator.Current.SessionId);
        Assert.AreNotEqual(logicalSession, physical);
        return physical;
    }

    private static void AssertFreshPhysicalSessions(
        PlaybackSessionId[] physicalSessions,
        int expectedCount)
    {
        Assert.HasCount(expectedCount, physicalSessions);
        Assert.AreEqual(expectedCount, physicalSessions.Distinct().Count());
        for (int ordinal = 1; ordinal < physicalSessions.Length; ordinal++)
        {
            Assert.IsGreaterThan(
                physicalSessions[ordinal - 1].Value,
                physicalSessions[ordinal].Value);
        }
    }

    private static PlaybackReconnectSnapshot CreateReconnectSnapshot(
        PlaybackReconnectPhase phase,
        PlaybackReconnectCorrelationId correlationId,
        int attemptNumber,
        TimeSpan remainingDelay,
        TimeSpan remainingBudget,
        DomainErrorCode? terminalErrorCode)
    {
        ConstructorInfo constructor = typeof(PlaybackReconnectSnapshot)
            .GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic)
            .Single(candidate => candidate.GetParameters().Length == 7);
        return (PlaybackReconnectSnapshot)constructor.Invoke(
        [
            phase,
            correlationId,
            attemptNumber,
            PlaybackReconnectPolicyOptions.MaximumAllowedAttempts,
            remainingDelay,
            remainingBudget,
            terminalErrorCode,
        ]);
    }

    private static void InvokeReconnectingFactory(
        PlaybackSessionId sessionId,
        PlaybackSelection selection,
        PlaybackReconnectSnapshot reconnect)
    {
        MethodInfo factory = typeof(PlaybackSessionSnapshot).GetMethod(
            "Reconnecting",
            BindingFlags.Static | BindingFlags.NonPublic)!;
        factory.Invoke(null, [sessionId, selection, reconnect]);
    }

    private static void AssertInvocationInnerException<TException>(Action action)
        where TException : Exception
    {
        TargetInvocationException exception =
            Assert.ThrowsExactly<TargetInvocationException>(action);
        Assert.IsInstanceOfType<TException>(exception.InnerException);
    }

    private static PlaybackSessionCoordinator Create(
        IPlaybackEngine engine,
        TimeProvider timeProvider) => new(
            engine,
            new PlaybackReconnectPolicy(),
            timeProvider,
            _ => TimeSpan.Zero);

    private static async Task<PlaybackSessionSnapshot> StartPlayingAsync(
        PlaybackSessionCoordinator coordinator)
    {
        PlaybackSessionSnapshot? snapshot = await coordinator.StartAsync(
            SourceId.Generate(),
            ChannelId.Generate());
        Assert.IsNotNull(snapshot);
        Assert.AreEqual(PlaybackState.Playing, snapshot.State);
        return snapshot;
    }

    private static async Task<PlaybackSessionSnapshot> ExhaustReconnectAsync(
        PlaybackSessionCoordinator coordinator,
        ReconnectPlaybackEngine engine,
        FakeTimeProvider time)
    {
        PlaybackSessionSnapshot logical = await StartPlayingAsync(coordinator);
        await ExhaustCurrentReconnectAsync(coordinator, engine, time, logical.SessionId);
        return logical;
    }

    private static async Task ExhaustCurrentReconnectAsync(
        PlaybackSessionCoordinator coordinator,
        ReconnectPlaybackEngine engine,
        FakeTimeProvider time,
        PlaybackSessionId logicalSession)
    {
        engine.EnqueueReconnectOpenFailure(DomainErrorCode.StreamInterrupted);
        engine.EnqueueReconnectOpenFailure(DomainErrorCode.StreamInterrupted);
        engine.EnqueueReconnectOpenFailure(DomainErrorCode.StreamInterrupted);
        engine.EmitFailure(logicalSession, DomainErrorCode.StreamInterrupted);
        await WaitForWaitingAttemptAsync(coordinator, attemptNumber: 1);
        await AdvanceAsync(time, TimeSpan.FromSeconds(1));
        await WaitForWaitingAttemptAsync(coordinator, attemptNumber: 2);
        await AdvanceAsync(time, TimeSpan.FromSeconds(2));
        await WaitForWaitingAttemptAsync(coordinator, attemptNumber: 3);
        await AdvanceAsync(time, TimeSpan.FromSeconds(4));
        await WaitUntilAsync(() => coordinator.Current is
        {
            State: PlaybackState.Failed,
            Error.Code: DomainErrorCode.ReconnectExhausted,
        });
    }

    private static Task WaitForWaitingAttemptAsync(
        PlaybackSessionCoordinator coordinator,
        int attemptNumber) => WaitUntilAsync(() =>
            coordinator.Current is
            {
                State: PlaybackState.Reconnecting,
                Reconnect.Phase: PlaybackReconnectPhase.Waiting,
                Reconnect.AttemptNumber: var attempt,
            } && attempt == attemptNumber);

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
        for (int iteration = 0; iteration < 2_000 && !predicate(); iteration++)
        {
            await Task.Yield();
        }

        Assert.IsTrue(predicate(), "The deterministic coordinator condition was not reached.");
    }

    private sealed class FirstTimestampBlockingTimeProvider(
        FakeTimeProvider inner) : TimeProvider, IDisposable
    {
        private int _timestampReads;

        internal TaskCompletionSource<bool> FirstTimestampEntered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal ManualResetEventSlim ReleaseFirstTimestamp { get; } = new(false);

        public override long TimestampFrequency => inner.TimestampFrequency;

        public override DateTimeOffset GetUtcNow() => inner.GetUtcNow();

        public override TimeZoneInfo LocalTimeZone => inner.LocalTimeZone;

        public override long GetTimestamp()
        {
            if (Interlocked.Increment(ref _timestampReads) == 1)
            {
                FirstTimestampEntered.TrySetResult(true);
                if (!ReleaseFirstTimestamp.Wait(TimeSpan.FromSeconds(5)))
                {
                    throw new TimeoutException("The reconnect begin barrier was not released.");
                }
            }

            return inner.GetTimestamp();
        }

        public override ITimer CreateTimer(
            TimerCallback callback,
            object? state,
            TimeSpan dueTime,
            TimeSpan period) => inner.CreateTimer(callback, state, dueTime, period);

        public void Dispose()
        {
            ReleaseFirstTimestamp.Set();
            ReleaseFirstTimestamp.Dispose();
        }
    }

    private sealed class ArmableTimestampBlockingTimeProvider(
        FakeTimeProvider inner) : TimeProvider, IDisposable
    {
        private int _armed;

        internal TaskCompletionSource<bool> ArmedTimestampEntered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal ManualResetEventSlim ReleaseArmedTimestamp { get; } = new(false);

        public override long TimestampFrequency => inner.TimestampFrequency;

        public override DateTimeOffset GetUtcNow() => inner.GetUtcNow();

        public override TimeZoneInfo LocalTimeZone => inner.LocalTimeZone;

        internal void ArmNextTimestamp()
        {
            if (Interlocked.Exchange(ref _armed, 1) != 0)
            {
                throw new InvalidOperationException("The timestamp barrier is already armed.");
            }
        }

        public override long GetTimestamp()
        {
            if (Interlocked.Exchange(ref _armed, 0) == 1)
            {
                ArmedTimestampEntered.TrySetResult(true);
                if (!ReleaseArmedTimestamp.Wait(TimeSpan.FromSeconds(5)))
                {
                    throw new TimeoutException("The armed timestamp barrier was not released.");
                }
            }

            return inner.GetTimestamp();
        }

        public override ITimer CreateTimer(
            TimerCallback callback,
            object? state,
            TimeSpan dueTime,
            TimeSpan period) => inner.CreateTimer(callback, state, dueTime, period);

        public void Dispose()
        {
            ReleaseArmedTimestamp.Set();
            ReleaseArmedTimestamp.Dispose();
        }
    }

    private sealed class ReconnectPlaybackEngine : IPlaybackEngine
    {
        private readonly object _sync = new();
        private readonly Queue<OpenOutcome> _reconnectOpenOutcomes = [];
        private PlaybackEngineSnapshot _current = PlaybackEngineSnapshot.Closed();
        private PlaybackControlSnapshot _controls = PlaybackControlSnapshot.Idle(
            PlaybackVolume.FromPercent(100),
            isMuted: false,
            PlaybackAspectMode.Fit);
        private int _disposeCount;
        private int _blockNextPlayAfterSuccess;
        private int _failNextPause;
        private int _failNextPlay;
        private int _failNextTrackQuery;
        private int _failNextVolume;
        private int _openCount;
        private int _stopCount;

        internal bool BlockFirstReconnectOpen { get; init; }

        internal int BlockingReconnectOpenOrdinal { get; init; } = 2;

        internal bool BlockFirstStop { get; init; }

        internal bool HoldFirstReconnectOpenAfterCancellation { get; init; }

        internal bool HoldReconnectAtBuffering { get; init; }

        internal ConcurrentQueue<string> Journal { get; } = new();

        internal ConcurrentQueue<PlaybackSessionId> OpenSessions { get; } = new();

        internal ConcurrentQueue<PlaybackSessionId> StopSessions { get; } = new();

        internal TaskCompletionSource<bool> FirstReconnectOpenEntered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource<bool> FirstReconnectOpenCancelled { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource<bool> ReleaseFirstReconnectOpen { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource<bool> FirstStopEntered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource<bool> ReleaseFirstStop { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource<bool> BlockedPlaySuccessEntered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource<bool> ReleaseBlockedPlaySuccess { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource<PlaybackSessionId> ReconnectPhysicalStopCompleted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal int DisposeCount => Volatile.Read(ref _disposeCount);

        internal int OpenCount => Volatile.Read(ref _openCount);

        internal int StopCount => Volatile.Read(ref _stopCount);

        internal int ActiveSessionCount
        {
            get
            {
                lock (_sync)
                {
                    return _current.State == PlaybackState.Closed ? 0 : 1;
                }
            }
        }

        private EventHandler<PlaybackEngineStateChangedEventArgs>? _stateChanged;

        public event EventHandler<PlaybackEngineStateChangedEventArgs>? StateChanged
        {
            add
            {
                lock (_sync)
                {
                    _stateChanged += value;
                }
            }
            remove
            {
                lock (_sync)
                {
                    _stateChanged -= value;
                }
            }
        }

        internal int StateChangedSubscriberCount
        {
            get
            {
                lock (_sync)
                {
                    return _stateChanged?.GetInvocationList().Length ?? 0;
                }
            }
        }

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

        internal void EnqueueReconnectOpenFailure(
            DomainErrorCode errorCode,
            bool emitCallback = false)
        {
            lock (_sync)
            {
                _reconnectOpenOutcomes.Enqueue(new OpenOutcome(errorCode, emitCallback));
            }
        }

        internal void EmitFailure(PlaybackSessionId sessionId, DomainErrorCode errorCode) =>
            Emit(PlaybackEngineSnapshot.Failed(sessionId, DomainError.Create(errorCode)));

        internal void EmitState(PlaybackSessionId sessionId, PlaybackState state) =>
            Emit(PlaybackEngineSnapshot.Active(sessionId, state));

        internal void EmitTransientFailureOnNextCommand(bool pause)
        {
            if (pause)
            {
                Interlocked.Exchange(ref _failNextPause, 1);
            }
            else
            {
                Interlocked.Exchange(ref _failNextPlay, 1);
            }
        }

        internal void EmitTransientFailureOnNextTrackQuery() =>
            Interlocked.Exchange(ref _failNextTrackQuery, 1);

        internal void EmitTransientFailureOnNextVolume() =>
            Interlocked.Exchange(ref _failNextVolume, 1);

        internal void BlockNextPlayAfterSuccess() =>
            Interlocked.Exchange(ref _blockNextPlayAfterSuccess, 1);

        public async ValueTask<PlaybackEngineOperationResult> OpenAsync(
            PlaybackSessionId sessionId,
            PlaybackSelection selection,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int ordinal = Interlocked.Increment(ref _openCount);
            Journal.Enqueue($"Open:{sessionId.Value}");
            OpenSessions.Enqueue(sessionId);
            if (ordinal == BlockingReconnectOpenOrdinal && BlockFirstReconnectOpen)
            {
                FirstReconnectOpenEntered.TrySetResult(true);
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    FirstReconnectOpenCancelled.TrySetResult(true);
                    if (HoldFirstReconnectOpenAfterCancellation)
                    {
                        await ReleaseFirstReconnectOpen.Task.ConfigureAwait(false);
                    }

                    throw;
                }
            }

            OpenOutcome outcome = default;
            lock (_sync)
            {
                if (ordinal > 1 && _reconnectOpenOutcomes.Count > 0)
                {
                    outcome = _reconnectOpenOutcomes.Dequeue();
                }
            }

            if (outcome.ErrorCode is { } errorCode)
            {
                if (outcome.EmitCallback)
                {
                    EmitFailure(sessionId, errorCode);
                    return PlaybackEngineOperationResult.Succeeded();
                }

                return PlaybackEngineOperationResult.Failed(errorCode);
            }

            Emit(PlaybackEngineSnapshot.Active(sessionId, PlaybackState.Buffering));
            return PlaybackEngineOperationResult.Succeeded();
        }

        public async ValueTask<PlaybackEngineOperationResult> PlayAsync(
            PlaybackSessionId sessionId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Journal.Enqueue($"Play:{sessionId.Value}");
            if (Interlocked.Exchange(ref _failNextPlay, 0) == 1)
            {
                EmitFailure(sessionId, DomainErrorCode.StreamInterrupted);
                return PlaybackEngineOperationResult.Succeeded();
            }

            if (!(HoldReconnectAtBuffering && Volatile.Read(ref _openCount) > 1))
            {
                Emit(PlaybackEngineSnapshot.Active(sessionId, PlaybackState.Playing));
            }

            if (Interlocked.Exchange(ref _blockNextPlayAfterSuccess, 0) == 1)
            {
                BlockedPlaySuccessEntered.TrySetResult(true);
                await ReleaseBlockedPlaySuccess.Task.ConfigureAwait(false);
            }

            return PlaybackEngineOperationResult.Succeeded();
        }

        public ValueTask<PlaybackEngineOperationResult> PauseAsync(
            PlaybackSessionId sessionId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Journal.Enqueue($"Pause:{sessionId.Value}");
            if (Interlocked.Exchange(ref _failNextPause, 0) == 1)
            {
                EmitFailure(sessionId, DomainErrorCode.StreamInterrupted);
                return ValueTask.FromResult(PlaybackEngineOperationResult.Succeeded());
            }

            Emit(PlaybackEngineSnapshot.Active(sessionId, PlaybackState.Paused));
            return ValueTask.FromResult(PlaybackEngineOperationResult.Succeeded());
        }

        public async ValueTask<PlaybackEngineOperationResult> StopAsync(
            PlaybackSessionId sessionId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int ordinal = Interlocked.Increment(ref _stopCount);
            Journal.Enqueue($"Stop:{sessionId.Value}");
            StopSessions.Enqueue(sessionId);
            if (ordinal == 1 && BlockFirstStop)
            {
                FirstStopEntered.TrySetResult(true);
                await ReleaseFirstStop.Task
                    .WaitAsync(cancellationToken)
                    .ConfigureAwait(false);
            }

            lock (_sync)
            {
                _current = PlaybackEngineSnapshot.Closed();
                _controls = PlaybackControlSnapshot.Idle(
                    _controls.Volume,
                    _controls.IsMuted,
                    _controls.AspectMode);
            }

            if (ordinal > 1)
            {
                ReconnectPhysicalStopCompleted.TrySetResult(sessionId);
            }

            return PlaybackEngineOperationResult.Succeeded();
        }

        public ValueTask<PlaybackEngineOperationResult> SetVolumeAsync(
            PlaybackSessionId sessionId,
            PlaybackVolume volume,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Journal.Enqueue($"Volume:{sessionId.Value}:{volume.Percent}");
            lock (_sync)
            {
                _controls = PlaybackControlSnapshot.Active(
                    sessionId,
                    volume,
                    _controls.IsMuted,
                    _controls.AspectMode);
            }

            if (Interlocked.Exchange(ref _failNextVolume, 0) == 1)
            {
                EmitFailure(sessionId, DomainErrorCode.StreamInterrupted);
            }

            return ValueTask.FromResult(PlaybackEngineOperationResult.Succeeded());
        }

        public ValueTask<PlaybackEngineOperationResult> SetMutedAsync(
            PlaybackSessionId sessionId,
            bool isMuted,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Journal.Enqueue($"Muted:{sessionId.Value}:{isMuted}");
            lock (_sync)
            {
                _controls = PlaybackControlSnapshot.Active(
                    sessionId,
                    _controls.Volume,
                    isMuted,
                    _controls.AspectMode);
            }

            return ValueTask.FromResult(PlaybackEngineOperationResult.Succeeded());
        }

        public ValueTask<PlaybackEngineOperationResult> SetAspectModeAsync(
            PlaybackSessionId sessionId,
            PlaybackAspectMode aspectMode,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Journal.Enqueue($"Aspect:{sessionId.Value}:{aspectMode}");
            lock (_sync)
            {
                _controls = PlaybackControlSnapshot.Active(
                    sessionId,
                    _controls.Volume,
                    _controls.IsMuted,
                    aspectMode);
            }

            return ValueTask.FromResult(PlaybackEngineOperationResult.Succeeded());
        }

        public ValueTask<DomainResult<PlaybackTrackSnapshot>> GetTracksAsync(
            PlaybackSessionId sessionId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Journal.Enqueue($"Tracks:{sessionId.Value}");
            bool emitFailure = Interlocked.Exchange(ref _failNextTrackQuery, 0) == 1;
            if (emitFailure)
            {
                EmitFailure(sessionId, DomainErrorCode.StreamInterrupted);
            }

            return ValueTask.FromResult(DomainResult.Success(
                PlaybackTrackSnapshot.Create(
                    sessionId,
                    PlaybackTrackCapabilities.AudioSelection,
                    [
                        new PlaybackTrackInfo(
                            PlaybackTrackId.Create(
                                sessionId,
                                PlaybackTrackKind.Audio,
                                ordinal: 1),
                            isSelected: true,
                            isSelectable: true),
                    ])));
        }

        public ValueTask<PlaybackEngineOperationResult> SelectTrackAsync(
            PlaybackSessionId sessionId,
            PlaybackTrackId trackId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(PlaybackEngineOperationResult.Succeeded());
        }

        public ValueTask DisposeAsync()
        {
            Interlocked.Increment(ref _disposeCount);
            return ValueTask.CompletedTask;
        }

        private void Emit(PlaybackEngineSnapshot snapshot)
        {
            EventHandler<PlaybackEngineStateChangedEventArgs>? handlers;
            lock (_sync)
            {
                _current = snapshot;
                handlers = _stateChanged;
            }

            handlers?.Invoke(this, new PlaybackEngineStateChangedEventArgs(snapshot));
        }

        private readonly record struct OpenOutcome(
            DomainErrorCode? ErrorCode,
            bool EmitCallback);
    }
}
