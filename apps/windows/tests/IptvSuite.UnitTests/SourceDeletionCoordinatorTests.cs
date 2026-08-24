using System.Collections.Concurrent;
using IptvSuite.Application;
using IptvSuite.Domain;

namespace IptvSuite.UnitTests;

[TestClass]
public sealed class SourceDeletionCoordinatorTests
{
    private static readonly string[] CompleteSequence =
        ["MarkPending", "ReleasePlayback", "CompletePending"];

    private static readonly string[] MarkOnlySequence = ["MarkPending"];

    private static readonly string[] PendingPlaybackSequence =
        ["MarkPending", "ReleasePlayback"];

    private static readonly string[] RepeatedSequence =
    [
        "MarkPending",
        "ReleasePlayback",
        "CompletePending",
        "MarkPending",
        "CompletePending",
    ];

    [TestMethod]
    public async Task SuccessfulDeleteMarksReleasesAndCompletesInExactOrder()
    {
        var journal = new ConcurrentQueue<string>();
        var lifecycle = new ControlledSourceDeletionLifecycle(journal);
        var engine = new SourceDeletionPlaybackEngine(journal);
        await using var playback = new PlaybackSessionCoordinator(engine);
        SourceId sourceId = SourceId.Generate();
        await StartPlaybackAsync(playback, sourceId, journal);
        using var cancellation = new CancellationTokenSource();
        await using var coordinator = new SourceDeletionCoordinator(lifecycle, playback);

        SourceDeletionResult result = await coordinator.DeleteAsync(
            sourceId,
            cancellation.Token);

        Assert.IsTrue(result.IsSuccess);
        Assert.AreEqual(SourceDeletionFailureStage.None, result.FailureStage);
        Assert.IsNull(result.Error);
        CollectionAssert.AreEqual(
            CompleteSequence,
            journal.ToArray());
        Assert.AreEqual(cancellation.Token, lifecycle.MarkToken);
        Assert.IsFalse(engine.StopToken.CanBeCanceled);
        Assert.IsFalse(lifecycle.CompleteToken.CanBeCanceled);
        Assert.IsFalse(lifecycle.IsPending(sourceId));
    }

    [TestMethod]
    public async Task MarkFailureDoesNotReleasePlaybackOrCompletePending()
    {
        var journal = new ConcurrentQueue<string>();
        var lifecycle = new ControlledSourceDeletionLifecycle(journal)
        {
            MarkResult = SourceDeletionLifecycleOperationResult.Failed(
                DomainErrorCode.StorageUnavailable),
        };
        var engine = new SourceDeletionPlaybackEngine(journal);
        await using var playback = new PlaybackSessionCoordinator(engine);
        SourceId sourceId = SourceId.Generate();
        await StartPlaybackAsync(playback, sourceId, journal);
        await using var coordinator = new SourceDeletionCoordinator(lifecycle, playback);

        SourceDeletionResult result = await coordinator.DeleteAsync(sourceId);

        Assert.IsFalse(result.IsSuccess);
        Assert.AreEqual(SourceDeletionFailureStage.MarkPending, result.FailureStage);
        Assert.AreEqual(DomainErrorCode.StorageUnavailable, result.Error?.Code);
        CollectionAssert.AreEqual(MarkOnlySequence, journal.ToArray());
        Assert.IsFalse(lifecycle.IsPending(sourceId));
        Assert.AreEqual(PlaybackState.Playing, playback.Current.State);
    }

    [TestMethod]
    public async Task PendingMarkReservationRejectsSameSourceStartBeforeDurableOutcome()
    {
        var journal = new ConcurrentQueue<string>();
        var lifecycle = new ControlledSourceDeletionLifecycle(journal)
        {
            BlockFirstMark = true,
        };
        var engine = new SourceDeletionPlaybackEngine(journal);
        await using var playback = new PlaybackSessionCoordinator(engine);
        SourceId sourceId = SourceId.Generate();
        await using var coordinator = new SourceDeletionCoordinator(lifecycle, playback);

        Task<SourceDeletionResult> deletion = coordinator.DeleteAsync(sourceId).AsTask();
        await lifecycle.FirstMarkEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));

        PlaybackSessionSnapshot? blocked = await playback.StartAsync(
            sourceId,
            ChannelId.Generate());
        SourceId replacementSource = SourceId.Generate();
        PlaybackSessionSnapshot? replacement = await playback.StartAsync(
            replacementSource,
            ChannelId.Generate());

        Assert.IsNull(blocked);
        Assert.IsNotNull(replacement);
        Assert.AreEqual(PlaybackState.Playing, replacement.State);
        Assert.AreEqual(replacementSource, replacement.SourceId);
        lifecycle.ReleaseFirstMark.TrySetResult();
        Assert.IsTrue((await deletion.WaitAsync(TimeSpan.FromSeconds(2))).IsSuccess);
        Assert.AreEqual(replacement, playback.Current);
    }

    [TestMethod]
    public async Task FailedMarkRollsBackOwnedReservationAndReopensSourceAdmission()
    {
        var journal = new ConcurrentQueue<string>();
        var lifecycle = new ControlledSourceDeletionLifecycle(journal)
        {
            BlockFirstMark = true,
            MarkResult = SourceDeletionLifecycleOperationResult.Failed(
                DomainErrorCode.StorageUnavailable),
        };
        var engine = new SourceDeletionPlaybackEngine(journal);
        await using var playback = new PlaybackSessionCoordinator(engine);
        SourceId sourceId = SourceId.Generate();
        await using var coordinator = new SourceDeletionCoordinator(lifecycle, playback);

        Task<SourceDeletionResult> deletion = coordinator.DeleteAsync(sourceId).AsTask();
        await lifecycle.FirstMarkEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.IsNull(await playback.StartAsync(sourceId, ChannelId.Generate()));
        lifecycle.ReleaseFirstMark.TrySetResult();

        SourceDeletionResult failed = await deletion.WaitAsync(TimeSpan.FromSeconds(2));
        PlaybackSessionSnapshot? reopened = await playback.StartAsync(
            sourceId,
            ChannelId.Generate());

        Assert.IsFalse(failed.IsSuccess);
        Assert.AreEqual(SourceDeletionFailureStage.MarkPending, failed.FailureStage);
        Assert.IsNotNull(reopened);
        Assert.AreEqual(PlaybackState.Playing, reopened.State);
        Assert.AreEqual(sourceId, reopened.SourceId);
    }

    [TestMethod]
    public async Task CancellationBeforeMarkCommitRollsBackReservationAndReopensAdmission()
    {
        var journal = new ConcurrentQueue<string>();
        var lifecycle = new ControlledSourceDeletionLifecycle(journal)
        {
            BlockFirstMark = true,
        };
        var engine = new SourceDeletionPlaybackEngine(journal);
        await using var playback = new PlaybackSessionCoordinator(engine);
        SourceId sourceId = SourceId.Generate();
        await using var coordinator = new SourceDeletionCoordinator(lifecycle, playback);
        using var cancellation = new CancellationTokenSource();

        Task<SourceDeletionResult> deletion = coordinator
            .DeleteAsync(sourceId, cancellation.Token)
            .AsTask();
        await lifecycle.FirstMarkEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.IsNull(await playback.StartAsync(sourceId, ChannelId.Generate()));
        cancellation.Cancel();

        OperationCanceledException exception =
            await Assert.ThrowsExactlyAsync<OperationCanceledException>(async () =>
                await deletion.WaitAsync(TimeSpan.FromSeconds(2)));
        PlaybackSessionSnapshot? reopened = await playback.StartAsync(
            sourceId,
            ChannelId.Generate());

        Assert.AreEqual(cancellation.Token, exception.CancellationToken);
        Assert.IsNotNull(reopened);
        Assert.AreEqual(PlaybackState.Playing, reopened.State);
        Assert.AreEqual(sourceId, reopened.SourceId);
        Assert.IsFalse(lifecycle.IsPending(sourceId));
    }

    [TestMethod]
    public async Task ReleaseFailureReturnsSafeResultAndLeavesDurablePendingState()
    {
        var journal = new ConcurrentQueue<string>();
        var lifecycle = new ControlledSourceDeletionLifecycle(journal);
        var engine = new SourceDeletionPlaybackEngine(journal)
        {
            StopResult = PlaybackEngineOperationResult.Failed(
                DomainErrorCode.StreamInterrupted),
        };
        await using var playback = new PlaybackSessionCoordinator(engine);
        SourceId sourceId = SourceId.Generate();
        await StartPlaybackAsync(playback, sourceId, journal);
        await using var coordinator = new SourceDeletionCoordinator(lifecycle, playback);

        SourceDeletionResult result = await coordinator.DeleteAsync(sourceId);

        Assert.IsFalse(result.IsSuccess);
        Assert.AreEqual(SourceDeletionFailureStage.PlaybackRelease, result.FailureStage);
        Assert.AreEqual(DomainErrorCode.StreamInterrupted, result.Error?.Code);
        CollectionAssert.AreEqual(
            PendingPlaybackSequence,
            journal.ToArray());
        Assert.IsTrue(lifecycle.IsPending(sourceId));
        Assert.AreEqual(
            "[SOURCE-DELETION:PlaybackRelease:StreamInterrupted]",
            result.ToString());
        Assert.IsNull(await playback.StartAsync(sourceId, ChannelId.Generate()));
        engine.StopResult = PlaybackEngineOperationResult.Succeeded();
        SourceId replacementSource = SourceId.Generate();
        PlaybackSessionSnapshot? replacement = await playback.StartAsync(
            replacementSource,
            ChannelId.Generate());
        Assert.IsNotNull(replacement);
        Assert.AreEqual(PlaybackState.Playing, replacement.State);
        Assert.AreEqual(replacementSource, replacement.SourceId);
    }

    [TestMethod]
    public async Task CompletionFailureReturnsSafeResultAndLeavesDurablePendingState()
    {
        var journal = new ConcurrentQueue<string>();
        var lifecycle = new ControlledSourceDeletionLifecycle(journal)
        {
            CompleteResult = SourceDeletionLifecycleOperationResult.Failed(
                DomainErrorCode.StorageUnavailable),
        };
        var engine = new SourceDeletionPlaybackEngine(journal);
        await using var playback = new PlaybackSessionCoordinator(engine);
        SourceId sourceId = SourceId.Generate();
        await StartPlaybackAsync(playback, sourceId, journal);
        await using var coordinator = new SourceDeletionCoordinator(lifecycle, playback);

        SourceDeletionResult result = await coordinator.DeleteAsync(sourceId);

        Assert.IsFalse(result.IsSuccess);
        Assert.AreEqual(SourceDeletionFailureStage.CompletePending, result.FailureStage);
        Assert.AreEqual(DomainErrorCode.StorageUnavailable, result.Error?.Code);
        CollectionAssert.AreEqual(
            CompleteSequence,
            journal.ToArray());
        Assert.IsTrue(lifecycle.IsPending(sourceId));
        Assert.AreEqual(PlaybackState.Closed, playback.Current.State);
        Assert.IsNull(await playback.StartAsync(sourceId, ChannelId.Generate()));
        SourceId replacementSource = SourceId.Generate();
        PlaybackSessionSnapshot? replacement = await playback.StartAsync(
            replacementSource,
            ChannelId.Generate());
        Assert.IsNotNull(replacement);
        Assert.AreEqual(PlaybackState.Playing, replacement.State);
        Assert.AreEqual(replacementSource, replacement.SourceId);
    }

    [TestMethod]
    public async Task InfrastructureExceptionContextIsMappedToASafeCompletionResult()
    {
        string sensitive = SecurityTestAssertions.CreateSensitiveValue(
            "SOURCE-DELETION-COMPLETION");
        var journal = new ConcurrentQueue<string>();
        var lifecycle = new ControlledSourceDeletionLifecycle(journal)
        {
            CompleteException = new InvalidOperationException(sensitive),
        };
        var engine = new SourceDeletionPlaybackEngine(journal);
        await using var playback = new PlaybackSessionCoordinator(engine);
        SourceId sourceId = SourceId.Generate();
        await StartPlaybackAsync(playback, sourceId, journal);
        await using var coordinator = new SourceDeletionCoordinator(lifecycle, playback);

        SourceDeletionResult result = await coordinator.DeleteAsync(sourceId);

        Assert.IsFalse(result.IsSuccess);
        Assert.AreEqual(SourceDeletionFailureStage.CompletePending, result.FailureStage);
        Assert.AreEqual(DomainErrorCode.StorageUnavailable, result.Error?.Code);
        SecurityTestAssertions.DoesNotContainSensitive(result.ToString(), sensitive);
        SecurityTestAssertions.DoesNotContainSensitive(result.Error!.ResourceKey, sensitive);
        Assert.IsTrue(lifecycle.IsPending(sourceId));
    }

    [TestMethod]
    public async Task PreMarkCancellationDoesNotMutateLifecycleOrPlayback()
    {
        var journal = new ConcurrentQueue<string>();
        var lifecycle = new ControlledSourceDeletionLifecycle(journal);
        var engine = new SourceDeletionPlaybackEngine(journal);
        await using var playback = new PlaybackSessionCoordinator(engine);
        SourceId sourceId = SourceId.Generate();
        await StartPlaybackAsync(playback, sourceId, journal);
        await using var coordinator = new SourceDeletionCoordinator(lifecycle, playback);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        OperationCanceledException exception =
            await Assert.ThrowsExactlyAsync<OperationCanceledException>(async () =>
                await coordinator.DeleteAsync(sourceId, cancellation.Token));

        Assert.AreEqual(cancellation.Token, exception.CancellationToken);
        Assert.IsEmpty(journal);
        Assert.IsFalse(lifecycle.IsPending(sourceId));
        Assert.AreEqual(PlaybackState.Playing, playback.Current.State);
    }

    [TestMethod]
    public async Task CancellationAfterMarkCommitCannotInterruptConvergence()
    {
        var journal = new ConcurrentQueue<string>();
        using var cancellation = new CancellationTokenSource();
        var lifecycle = new ControlledSourceDeletionLifecycle(journal)
        {
            AfterSuccessfulMark = cancellation.Cancel,
        };
        var engine = new SourceDeletionPlaybackEngine(journal);
        await using var playback = new PlaybackSessionCoordinator(engine);
        SourceId sourceId = SourceId.Generate();
        await StartPlaybackAsync(playback, sourceId, journal);
        await using var coordinator = new SourceDeletionCoordinator(lifecycle, playback);

        SourceDeletionResult result = await coordinator.DeleteAsync(
            sourceId,
            cancellation.Token);

        Assert.IsTrue(cancellation.IsCancellationRequested);
        Assert.IsTrue(result.IsSuccess);
        CollectionAssert.AreEqual(
            CompleteSequence,
            journal.ToArray());
        Assert.IsFalse(engine.StopToken.CanBeCanceled);
        Assert.IsFalse(lifecycle.CompleteToken.CanBeCanceled);
        Assert.IsFalse(lifecycle.IsPending(sourceId));
    }

    [TestMethod]
    public async Task SameSourceStartAtDurableMarkBoundaryIsRejectedBeforeRelease()
    {
        SourceId sourceId = SourceId.Generate();
        var journal = new ConcurrentQueue<string>();
        PlaybackSessionCoordinator? playbackForRace = null;
        Task<PlaybackSessionSnapshot?>? racedStart = null;
        var lifecycle = new ControlledSourceDeletionLifecycle(journal)
        {
            AfterSuccessfulMark = () => racedStart = playbackForRace!
                .StartAsync(sourceId, ChannelId.Generate())
                .AsTask(),
        };
        var engine = new SourceDeletionPlaybackEngine(journal);
        await using var playback = new PlaybackSessionCoordinator(engine);
        playbackForRace = playback;
        await StartPlaybackAsync(playback, sourceId, journal);
        await using var coordinator = new SourceDeletionCoordinator(lifecycle, playback);

        SourceDeletionResult result = await coordinator.DeleteAsync(sourceId);

        Assert.IsTrue(result.IsSuccess);
        Assert.IsNotNull(racedStart);
        PlaybackSessionSnapshot? raced = await racedStart.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.IsNull(raced);
        Assert.AreEqual(PlaybackState.Closed, playback.Current.State);
        Assert.IsNull(await playback.StartAsync(sourceId, ChannelId.Generate()));
        Assert.AreEqual(1, engine.StopCount);
        CollectionAssert.AreEqual(CompleteSequence, journal.ToArray());
        Assert.IsFalse(lifecycle.IsPending(sourceId));
    }

    [TestMethod]
    public async Task DurableMarkRetiresSourceDuringDrainAndAllowsDifferentSourceReplacement()
    {
        var journal = new ConcurrentQueue<string>();
        var lifecycle = new ControlledSourceDeletionLifecycle(journal);
        var engine = new SourceDeletionPlaybackEngine(journal)
        {
            BlockFirstStop = true,
        };
        await using var playback = new PlaybackSessionCoordinator(engine);
        SourceId retiringSource = SourceId.Generate();
        await StartPlaybackAsync(playback, retiringSource, journal);
        await using var coordinator = new SourceDeletionCoordinator(lifecycle, playback);

        Task<SourceDeletionResult> deletion = coordinator
            .DeleteAsync(retiringSource)
            .AsTask();
        await engine.FirstStopEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));

        PlaybackSessionSnapshot? rejected = await playback.StartAsync(
            retiringSource,
            ChannelId.Generate());
        SourceId replacementSource = SourceId.Generate();
        Task<PlaybackSessionSnapshot?> replacement = playback
            .StartAsync(replacementSource, ChannelId.Generate())
            .AsTask();

        Assert.IsNull(rejected);
        Assert.IsFalse(replacement.IsCompleted);
        Assert.AreEqual(replacementSource, playback.Current.SourceId);
        engine.ReleaseFirstStop.TrySetResult();

        SourceDeletionResult deleted = await deletion.WaitAsync(TimeSpan.FromSeconds(2));
        PlaybackSessionSnapshot? playing = await replacement.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.IsTrue(deleted.IsSuccess);
        Assert.IsNotNull(playing);
        Assert.AreEqual(replacementSource, playing.SourceId);
        Assert.AreEqual(PlaybackState.Playing, playing.State);
        Assert.AreEqual(playing, playback.Current);
        CollectionAssert.AreEqual(CompleteSequence, journal.ToArray());
        Assert.IsFalse(lifecycle.IsPending(retiringSource));
    }

    [TestMethod]
    public async Task RepeatedDeleteConvergesIdempotentlyWithoutASecondPlaybackStop()
    {
        var journal = new ConcurrentQueue<string>();
        var lifecycle = new ControlledSourceDeletionLifecycle(journal);
        var engine = new SourceDeletionPlaybackEngine(journal);
        await using var playback = new PlaybackSessionCoordinator(engine);
        SourceId sourceId = SourceId.Generate();
        await StartPlaybackAsync(playback, sourceId, journal);
        await using var coordinator = new SourceDeletionCoordinator(lifecycle, playback);

        SourceDeletionResult first = await coordinator.DeleteAsync(sourceId);
        SourceDeletionResult repeated = await coordinator.DeleteAsync(sourceId);

        Assert.IsTrue(first.IsSuccess);
        Assert.IsTrue(repeated.IsSuccess);
        CollectionAssert.AreEqual(
            RepeatedSequence,
            journal.ToArray());
        Assert.AreEqual(2, lifecycle.MarkCount);
        Assert.AreEqual(2, lifecycle.CompleteCount);
        Assert.AreEqual(1, engine.StopCount);
        Assert.IsFalse(lifecycle.IsPending(sourceId));
    }

    [TestMethod]
    public async Task ConcurrentDeletesAreSerializedAndConvergeIdempotently()
    {
        var journal = new ConcurrentQueue<string>();
        var lifecycle = new ControlledSourceDeletionLifecycle(journal)
        {
            BlockFirstMark = true,
        };
        var engine = new SourceDeletionPlaybackEngine(journal);
        await using var playback = new PlaybackSessionCoordinator(engine);
        SourceId sourceId = SourceId.Generate();
        await StartPlaybackAsync(playback, sourceId, journal);
        await using var coordinator = new SourceDeletionCoordinator(lifecycle, playback);

        Task<SourceDeletionResult> first = coordinator.DeleteAsync(sourceId).AsTask();
        await lifecycle.FirstMarkEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Task<SourceDeletionResult> second = coordinator.DeleteAsync(sourceId).AsTask();

        Assert.IsFalse(second.IsCompleted);
        Assert.AreEqual(1, lifecycle.MarkCount);
        lifecycle.ReleaseFirstMark.TrySetResult();

        Assert.IsTrue((await first.WaitAsync(TimeSpan.FromSeconds(2))).IsSuccess);
        Assert.IsTrue((await second.WaitAsync(TimeSpan.FromSeconds(2))).IsSuccess);
        Assert.AreEqual(1, lifecycle.MaximumConcurrentCalls);
        CollectionAssert.AreEqual(
            RepeatedSequence,
            journal.ToArray());
        Assert.IsFalse(lifecycle.IsPending(sourceId));
    }

    [TestMethod]
    public async Task EmptyIdentifierIsRejectedBeforeAnyMutation()
    {
        var journal = new ConcurrentQueue<string>();
        var lifecycle = new ControlledSourceDeletionLifecycle(journal);
        await using var playback = new PlaybackSessionCoordinator(
            new SourceDeletionPlaybackEngine(journal));
        await using var coordinator = new SourceDeletionCoordinator(lifecycle, playback);

        await Assert.ThrowsExactlyAsync<ArgumentException>(async () =>
            await coordinator.DeleteAsync(default));

        Assert.IsEmpty(journal);
    }

    private static async Task StartPlaybackAsync(
        PlaybackSessionCoordinator playback,
        SourceId sourceId,
        ConcurrentQueue<string> journal)
    {
        PlaybackSessionSnapshot? started = await playback.StartAsync(
            sourceId,
            ChannelId.Generate());
        Assert.IsNotNull(started);
        Assert.AreEqual(PlaybackState.Playing, started.State);
        journal.Clear();
    }

    private sealed class ControlledSourceDeletionLifecycle : ISourceDeletionLifecycle
    {
        private readonly ConcurrentQueue<string> _journal;
        private readonly object _sync = new();
        private readonly HashSet<SourceId> _pending = [];
        private int _activeCalls;
        private int _completeCount;
        private int _markCount;
        private int _maximumConcurrentCalls;

        internal ControlledSourceDeletionLifecycle(ConcurrentQueue<string> journal)
        {
            _journal = journal;
        }

        internal SourceDeletionLifecycleOperationResult MarkResult { get; init; } =
            SourceDeletionLifecycleOperationResult.Succeeded();

        internal SourceDeletionLifecycleOperationResult CompleteResult { get; init; } =
            SourceDeletionLifecycleOperationResult.Succeeded();

        internal Action? AfterSuccessfulMark { get; init; }

        internal bool BlockFirstMark { get; init; }

        internal Exception? CompleteException { get; init; }

        internal TaskCompletionSource FirstMarkEntered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource ReleaseFirstMark { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal CancellationToken MarkToken { get; private set; }

        internal CancellationToken CompleteToken { get; private set; }

        internal int MarkCount => Volatile.Read(ref _markCount);

        internal int CompleteCount => Volatile.Read(ref _completeCount);

        internal int MaximumConcurrentCalls => Volatile.Read(ref _maximumConcurrentCalls);

        public async ValueTask<SourceDeletionLifecycleOperationResult> MarkPendingAsync(
            SourceId sourceId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            EnterCall();
            try
            {
                MarkToken = cancellationToken;
                int call = Interlocked.Increment(ref _markCount);
                _journal.Enqueue("MarkPending");
                if (call == 1)
                {
                    FirstMarkEntered.TrySetResult();
                    if (BlockFirstMark)
                    {
                        await ReleaseFirstMark.Task
                            .WaitAsync(cancellationToken)
                            .ConfigureAwait(false);
                    }
                }

                if (MarkResult.IsSuccess)
                {
                    lock (_sync)
                    {
                        _pending.Add(sourceId);
                    }

                    AfterSuccessfulMark?.Invoke();
                }

                return MarkResult;
            }
            finally
            {
                ExitCall();
            }
        }

        public ValueTask<SourceDeletionLifecycleOperationResult> CompletePendingAsync(
            SourceId sourceId,
            CancellationToken cancellationToken = default)
        {
            EnterCall();
            try
            {
                CompleteToken = cancellationToken;
                Interlocked.Increment(ref _completeCount);
                _journal.Enqueue("CompletePending");
                if (CompleteException is not null)
                {
                    throw CompleteException;
                }

                if (CompleteResult.IsSuccess)
                {
                    lock (_sync)
                    {
                        _pending.Remove(sourceId);
                    }
                }

                return ValueTask.FromResult(CompleteResult);
            }
            finally
            {
                ExitCall();
            }
        }

        internal bool IsPending(SourceId sourceId)
        {
            lock (_sync)
            {
                return _pending.Contains(sourceId);
            }
        }

        private void EnterCall()
        {
            int active = Interlocked.Increment(ref _activeCalls);
            int observed;
            do
            {
                observed = Volatile.Read(ref _maximumConcurrentCalls);
                if (active <= observed)
                {
                    break;
                }
            }
            while (Interlocked.CompareExchange(
                ref _maximumConcurrentCalls,
                active,
                observed) != observed);
        }

        private void ExitCall() => Interlocked.Decrement(ref _activeCalls);
    }

    private sealed class SourceDeletionPlaybackEngine : IPlaybackEngine
    {
        private readonly ConcurrentQueue<string> _journal;
        private PlaybackEngineSnapshot _current = PlaybackEngineSnapshot.Closed();
        private PlaybackControlSnapshot _controls = PlaybackControlSnapshot.Idle(
            PlaybackVolume.FromPercent(100),
            isMuted: false,
            PlaybackAspectMode.Fit);
        private int _stopCount;

        internal SourceDeletionPlaybackEngine(ConcurrentQueue<string> journal)
        {
            _journal = journal;
        }

        public event EventHandler<PlaybackEngineStateChangedEventArgs>? StateChanged;

        public PlaybackEngineSnapshot Current => _current;

        public PlaybackControlSnapshot CurrentControls => _controls;

        internal PlaybackEngineOperationResult StopResult { get; set; } =
            PlaybackEngineOperationResult.Succeeded();

        internal bool BlockFirstStop { get; init; }

        internal TaskCompletionSource FirstStopEntered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource ReleaseFirstStop { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal CancellationToken StopToken { get; private set; }

        internal int StopCount => Volatile.Read(ref _stopCount);

        public ValueTask<PlaybackEngineOperationResult> OpenAsync(
            PlaybackSessionId sessionId,
            PlaybackSelection selection,
            CancellationToken cancellationToken = default)
        {
            _current = PlaybackEngineSnapshot.Active(sessionId, PlaybackState.Opening);
            StateChanged?.Invoke(this, new PlaybackEngineStateChangedEventArgs(_current));
            return ValueTask.FromResult(PlaybackEngineOperationResult.Succeeded());
        }

        public ValueTask<PlaybackEngineOperationResult> PlayAsync(
            PlaybackSessionId sessionId,
            CancellationToken cancellationToken = default)
        {
            _current = PlaybackEngineSnapshot.Active(sessionId, PlaybackState.Playing);
            StateChanged?.Invoke(this, new PlaybackEngineStateChangedEventArgs(_current));
            return ValueTask.FromResult(PlaybackEngineOperationResult.Succeeded());
        }

        public ValueTask<PlaybackEngineOperationResult> PauseAsync(
            PlaybackSessionId sessionId,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(PlaybackEngineOperationResult.Succeeded());

        public async ValueTask<PlaybackEngineOperationResult> StopAsync(
            PlaybackSessionId sessionId,
            CancellationToken cancellationToken = default)
        {
            StopToken = cancellationToken;
            int call = Interlocked.Increment(ref _stopCount);
            _journal.Enqueue("ReleasePlayback");
            if (call == 1)
            {
                FirstStopEntered.TrySetResult();
                if (BlockFirstStop)
                {
                    await ReleaseFirstStop.Task.ConfigureAwait(false);
                }
            }

            if (StopResult.IsSuccess)
            {
                _current = PlaybackEngineSnapshot.Closed(sessionId);
                StateChanged?.Invoke(this, new PlaybackEngineStateChangedEventArgs(_current));
            }

            return StopResult;
        }

        public ValueTask<PlaybackEngineOperationResult> SetVolumeAsync(
            PlaybackSessionId sessionId,
            PlaybackVolume volume,
            CancellationToken cancellationToken = default)
        {
            _controls = PlaybackControlSnapshot.Active(
                sessionId,
                volume,
                _controls.IsMuted,
                _controls.AspectMode);
            return ValueTask.FromResult(PlaybackEngineOperationResult.Succeeded());
        }

        public ValueTask<PlaybackEngineOperationResult> SetMutedAsync(
            PlaybackSessionId sessionId,
            bool isMuted,
            CancellationToken cancellationToken = default)
        {
            _controls = PlaybackControlSnapshot.Active(
                sessionId,
                _controls.Volume,
                isMuted,
                _controls.AspectMode);
            return ValueTask.FromResult(PlaybackEngineOperationResult.Succeeded());
        }

        public ValueTask<PlaybackEngineOperationResult> SetAspectModeAsync(
            PlaybackSessionId sessionId,
            PlaybackAspectMode aspectMode,
            CancellationToken cancellationToken = default)
        {
            _controls = PlaybackControlSnapshot.Active(
                sessionId,
                _controls.Volume,
                _controls.IsMuted,
                aspectMode);
            return ValueTask.FromResult(PlaybackEngineOperationResult.Succeeded());
        }

        public ValueTask<DomainResult<PlaybackTrackSnapshot>> GetTracksAsync(
            PlaybackSessionId sessionId,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(DomainResult.Success(
                PlaybackTrackSnapshot.Create(
                    sessionId,
                    PlaybackTrackCapabilities.None,
                    [])));

        public ValueTask<PlaybackEngineOperationResult> SelectTrackAsync(
            PlaybackSessionId sessionId,
            PlaybackTrackId trackId,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(PlaybackEngineOperationResult.Succeeded());

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
