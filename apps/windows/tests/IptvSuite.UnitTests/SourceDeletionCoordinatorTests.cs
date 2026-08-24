using System.Collections.Concurrent;
using System.Globalization;
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

    private static readonly string[] ReconciliationSequence =
        ["ReadPending", "MarkPending", "ReleasePlayback", "CompletePending"];

    private static readonly string[] ReadOnlySequence = ["ReadPending"];

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
    public async Task ReconciliationMarksReleasesAndCompletesOneDurableEntryInExactOrder()
    {
        var journal = new ConcurrentQueue<string>();
        SourceId sourceId = SourceIdAt(1);
        var lifecycle = new ReconciliationSourceDeletionLifecycle(journal);
        lifecycle.SeedPending(sourceId);
        var engine = new SourceDeletionPlaybackEngine(journal);
        await using var playback = new PlaybackSessionCoordinator(engine);
        await StartPlaybackAsync(playback, sourceId, journal);
        using var cancellation = new CancellationTokenSource();
        await using var coordinator = new SourceDeletionCoordinator(lifecycle, playback);

        SourceDeletionReconciliationResult result =
            await coordinator.ReconcilePendingAsync(cancellation.Token);

        Assert.IsTrue(result.IsSuccess);
        Assert.AreEqual(1, result.AttemptedCount);
        Assert.AreEqual(1, result.CompletedCount);
        Assert.AreEqual(0, result.FailedCount);
        Assert.IsFalse(result.HasRemaining);
        CollectionAssert.AreEqual(
            ReconciliationSequence,
            journal.ToArray());
        Assert.AreEqual(cancellation.Token, lifecycle.ReadToken);
        Assert.IsFalse(lifecycle.MarkTokens.Single().CanBeCanceled);
        Assert.IsFalse(lifecycle.CompleteTokens.Single().CanBeCanceled);
        Assert.IsFalse(lifecycle.IsPending(sourceId));
    }

    [TestMethod]
    public async Task EmptyReconciliationDoesNotMutateLifecycleOrPlayback()
    {
        var journal = new ConcurrentQueue<string>();
        var lifecycle = new ReconciliationSourceDeletionLifecycle(journal);
        var engine = new SourceDeletionPlaybackEngine(journal);
        await using var playback = new PlaybackSessionCoordinator(engine);
        await using var coordinator = new SourceDeletionCoordinator(lifecycle, playback);

        SourceDeletionReconciliationResult result =
            await coordinator.ReconcilePendingAsync();

        Assert.IsTrue(result.IsSuccess);
        Assert.AreEqual(0, result.AttemptedCount);
        Assert.AreEqual(1, lifecycle.ReadCount);
        Assert.AreEqual(0, engine.StopCount);
        CollectionAssert.AreEqual(ReadOnlySequence, journal.ToArray());
    }

    [TestMethod]
    public async Task PreCancelledReconciliationDoesNotDiscoverOrRetireAnyEntry()
    {
        var journal = new ConcurrentQueue<string>();
        SourceId sourceId = SourceIdAt(1);
        var lifecycle = new ReconciliationSourceDeletionLifecycle(journal);
        lifecycle.SeedPending(sourceId);
        await using var playback = new PlaybackSessionCoordinator(
            new SourceDeletionPlaybackEngine(journal));
        await using var coordinator = new SourceDeletionCoordinator(lifecycle, playback);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        OperationCanceledException exception =
            await Assert.ThrowsExactlyAsync<OperationCanceledException>(async () =>
                await coordinator.ReconcilePendingAsync(cancellation.Token));
        PlaybackSessionSnapshot? admitted = await playback.StartAsync(
            sourceId,
            ChannelId.Generate());

        Assert.AreEqual(cancellation.Token, exception.CancellationToken);
        Assert.AreEqual(0, lifecycle.ReadCount);
        Assert.IsNotNull(admitted);
        Assert.AreEqual(sourceId, admitted.SourceId);
    }

    [TestMethod]
    public async Task ReconciliationUsesStableBoundedKeysetPagesInSourceOrder()
    {
        var journal = new ConcurrentQueue<string>();
        SourceId[] expected = Enumerable.Range(1, 70).Select(SourceIdAt).ToArray();
        var lifecycle = new ReconciliationSourceDeletionLifecycle(journal);
        lifecycle.SeedPending(expected.Reverse().ToArray());
        await using var playback = new PlaybackSessionCoordinator(
            new SourceDeletionPlaybackEngine(journal));
        await using var coordinator = new SourceDeletionCoordinator(lifecycle, playback);

        SourceDeletionReconciliationResult result =
            await coordinator.ReconcilePendingAsync();

        Assert.IsTrue(result.IsSuccess);
        Assert.AreEqual(70, result.AttemptedCount);
        Assert.AreEqual(70, result.CompletedCount);
        Assert.AreEqual(3, lifecycle.ReadCount);
        CollectionAssert.AreEqual(expected, lifecycle.MarkedSourceIds.ToArray());
        SourceId?[] cursors = lifecycle.ReadCursors.ToArray();
        Assert.HasCount(3, cursors);
        Assert.IsNull(cursors[0]);
        Assert.AreEqual(expected[31], cursors[1]);
        Assert.AreEqual(expected[63], cursors[2]);
    }

    [TestMethod]
    public async Task ReconciliationStopsAtHardCapAndReportsRemainingWork()
    {
        var journal = new ConcurrentQueue<string>();
        SourceId[] pending = Enumerable.Range(1, 101).Select(SourceIdAt).ToArray();
        var lifecycle = new ReconciliationSourceDeletionLifecycle(journal);
        lifecycle.SeedPending(pending);
        await using var playback = new PlaybackSessionCoordinator(
            new SourceDeletionPlaybackEngine(journal));
        await using var coordinator = new SourceDeletionCoordinator(lifecycle, playback);

        SourceDeletionReconciliationResult result =
            await coordinator.ReconcilePendingAsync();

        Assert.IsFalse(result.IsSuccess);
        Assert.AreEqual(SourceDeletionReconciliationResult.MaximumAttemptCount, result.AttemptedCount);
        Assert.AreEqual(SourceDeletionReconciliationResult.MaximumAttemptCount, result.CompletedCount);
        Assert.AreEqual(0, result.FailedCount);
        Assert.IsTrue(result.HasRemaining);
        Assert.AreEqual(SourceDeletionFailureStage.None, result.FirstFailureStage);
        Assert.IsNull(result.FirstError);
        Assert.IsTrue(lifecycle.IsPending(pending[^1]));
    }

    [TestMethod]
    public async Task DurableCursorPreventsPersistentLowIdentifiersFromStarvingLaterEntry()
    {
        var journal = new ConcurrentQueue<string>();
        SourceId[] persistentFailures = Enumerable.Range(1, 100)
            .Select(SourceIdAt)
            .ToArray();
        SourceId healthy = SourceIdAt(101);
        var firstLifecycle = new ReconciliationSourceDeletionLifecycle(journal);
        firstLifecycle.SeedPending([.. persistentFailures, healthy]);
        foreach (SourceId sourceId in persistentFailures)
        {
            firstLifecycle.MarkFailureSources.Add(sourceId);
        }

        await using (var firstPlayback = new PlaybackSessionCoordinator(
            new SourceDeletionPlaybackEngine(journal)))
        await using (var firstCoordinator = new SourceDeletionCoordinator(
            firstLifecycle,
            firstPlayback))
        {
            SourceDeletionReconciliationResult first =
                await firstCoordinator.ReconcilePendingAsync();

            Assert.AreEqual(100, first.AttemptedCount);
            Assert.AreEqual(0, first.CompletedCount);
            Assert.AreEqual(100, first.FailedCount);
            Assert.IsTrue(first.HasRemaining);
            Assert.IsTrue(firstLifecycle.IsPending(healthy));
        }

        var restartedLifecycle = new ReconciliationSourceDeletionLifecycle(
            journal,
            firstLifecycle.DurableState);
        foreach (SourceId sourceId in persistentFailures)
        {
            restartedLifecycle.MarkFailureSources.Add(sourceId);
        }

        await using (var restartedPlayback = new PlaybackSessionCoordinator(
            new SourceDeletionPlaybackEngine(journal)))
        await using (var restartedCoordinator = new SourceDeletionCoordinator(
            restartedLifecycle,
            restartedPlayback))
        {
            SourceDeletionReconciliationResult restarted =
                await restartedCoordinator.ReconcilePendingAsync();

            Assert.AreEqual(100, restarted.AttemptedCount);
            Assert.AreEqual(1, restarted.CompletedCount);
            Assert.AreEqual(99, restarted.FailedCount);
            Assert.IsTrue(restarted.HasRemaining);
            Assert.IsFalse(restartedLifecycle.IsPending(healthy));
        }

        Assert.AreEqual(healthy, restartedLifecycle.MarkedSourceIds.First());
    }

    [TestMethod]
    public async Task ReconciliationPermanentlyRetiresEntryBeforeMarkAndRetainsItAfterMarkFailure()
    {
        var journal = new ConcurrentQueue<string>();
        SourceId sourceId = SourceIdAt(1);
        var lifecycle = new ReconciliationSourceDeletionLifecycle(journal)
        {
            BlockFirstMark = true,
        };
        lifecycle.MarkFailureSources.Add(sourceId);
        lifecycle.SeedPending(sourceId);
        var engine = new SourceDeletionPlaybackEngine(journal);
        await using var playback = new PlaybackSessionCoordinator(engine);
        await using var coordinator = new SourceDeletionCoordinator(lifecycle, playback);

        Task<SourceDeletionReconciliationResult> reconciliation = coordinator
            .ReconcilePendingAsync()
            .AsTask();
        await lifecycle.FirstMarkEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));

        SourceDeletionPendingCursorReadResult durableCursor =
            await lifecycle.ReadPendingCursorAsync();

        Assert.AreEqual(sourceId, durableCursor.AfterExclusive);
        Assert.IsNull(await playback.StartAsync(sourceId, ChannelId.Generate()));
        SourceId sibling = SourceIdAt(2);
        PlaybackSessionSnapshot? replacement = await playback.StartAsync(
            sibling,
            ChannelId.Generate());
        lifecycle.ReleaseFirstMark.TrySetResult();
        SourceDeletionReconciliationResult result =
            await reconciliation.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.IsFalse(result.IsSuccess);
        Assert.AreEqual(SourceDeletionFailureStage.MarkPending, result.FirstFailureStage);
        Assert.AreEqual(DomainErrorCode.StorageUnavailable, result.FirstError?.Code);
        Assert.IsTrue(result.HasRemaining);
        Assert.IsNull(await playback.StartAsync(sourceId, ChannelId.Generate()));
        Assert.IsNotNull(replacement);
        Assert.AreEqual(replacement, playback.Current);
        Assert.AreEqual(0, engine.StopCount);
    }

    [TestMethod]
    public async Task ReconciliationWrapsOnceAndDoesNotRetryCycleBoundaryInSamePass()
    {
        var journal = new ConcurrentQueue<string>();
        SourceId first = SourceIdAt(1);
        SourceId boundary = SourceIdAt(2);
        var lifecycle = new ReconciliationSourceDeletionLifecycle(journal);
        lifecycle.SeedPending(first, boundary);
        lifecycle.MarkFailureSources.Add(first);
        lifecycle.MarkFailureSources.Add(boundary);
        Assert.IsTrue((await lifecycle.AdvancePendingCursorAsync(boundary)).IsSuccess);
        await using var playback = new PlaybackSessionCoordinator(
            new SourceDeletionPlaybackEngine(journal));
        await using var coordinator = new SourceDeletionCoordinator(lifecycle, playback);

        SourceDeletionReconciliationResult result =
            await coordinator.ReconcilePendingAsync();

        Assert.AreEqual(2, result.AttemptedCount);
        Assert.AreEqual(0, result.CompletedCount);
        Assert.AreEqual(2, result.FailedCount);
        CollectionAssert.AreEqual(
            new[] { first, boundary },
            lifecycle.MarkedSourceIds.ToArray());
        CollectionAssert.AreEqual(
            new SourceId?[] { boundary, null },
            lifecycle.ReadCursors.ToArray());
    }

    [TestMethod]
    public async Task CursorAdvanceFailureDoesNotSelectOrRetireEntry()
    {
        var journal = new ConcurrentQueue<string>();
        SourceId sourceId = SourceIdAt(1);
        var lifecycle = new ReconciliationSourceDeletionLifecycle(journal);
        lifecycle.SeedPending(sourceId);
        lifecycle.CursorAdvanceFailureSources.Add(sourceId);
        await using var playback = new PlaybackSessionCoordinator(
            new SourceDeletionPlaybackEngine(journal));
        await using var coordinator = new SourceDeletionCoordinator(lifecycle, playback);

        SourceDeletionReconciliationResult result =
            await coordinator.ReconcilePendingAsync();
        PlaybackSessionSnapshot? admitted = await playback.StartAsync(
            sourceId,
            ChannelId.Generate());

        Assert.AreEqual(0, result.AttemptedCount);
        Assert.AreEqual(SourceDeletionFailureStage.PendingDiscovery, result.FirstFailureStage);
        Assert.AreEqual(DomainErrorCode.StorageUnavailable, result.FirstError?.Code);
        Assert.IsTrue(result.HasRemaining);
        Assert.IsEmpty(lifecycle.MarkedSourceIds);
        Assert.IsNotNull(admitted);
    }

    [TestMethod]
    public async Task ReconciliationOfInactiveEntryDoesNotStopPlaybackEngine()
    {
        var journal = new ConcurrentQueue<string>();
        SourceId sourceId = SourceIdAt(1);
        var lifecycle = new ReconciliationSourceDeletionLifecycle(journal);
        lifecycle.SeedPending(sourceId);
        var engine = new SourceDeletionPlaybackEngine(journal);
        await using var playback = new PlaybackSessionCoordinator(engine);
        await using var coordinator = new SourceDeletionCoordinator(lifecycle, playback);

        SourceDeletionReconciliationResult result =
            await coordinator.ReconcilePendingAsync();

        Assert.IsTrue(result.IsSuccess);
        Assert.AreEqual(0, engine.StopCount);
        Assert.AreEqual(PlaybackState.Closed, playback.Current.State);
        Assert.IsNull(await playback.StartAsync(sourceId, ChannelId.Generate()));
    }

    [TestMethod]
    public async Task FailedEntryDoesNotStopLaterSiblingFromConverging()
    {
        var journal = new ConcurrentQueue<string>();
        SourceId failedSource = SourceIdAt(1);
        SourceId completedSource = SourceIdAt(2);
        var lifecycle = new ReconciliationSourceDeletionLifecycle(journal);
        lifecycle.CompleteFailureSources.Add(failedSource);
        lifecycle.SeedPending(completedSource, failedSource);
        await using var playback = new PlaybackSessionCoordinator(
            new SourceDeletionPlaybackEngine(journal));
        await using var coordinator = new SourceDeletionCoordinator(lifecycle, playback);

        SourceDeletionReconciliationResult result =
            await coordinator.ReconcilePendingAsync();

        Assert.IsFalse(result.IsSuccess);
        Assert.AreEqual(2, result.AttemptedCount);
        Assert.AreEqual(1, result.CompletedCount);
        Assert.AreEqual(1, result.FailedCount);
        Assert.IsTrue(result.HasRemaining);
        Assert.AreEqual(SourceDeletionFailureStage.CompletePending, result.FirstFailureStage);
        Assert.AreEqual(DomainErrorCode.StorageUnavailable, result.FirstError?.Code);
        CollectionAssert.AreEqual(
            new[] { failedSource, completedSource },
            lifecycle.MarkedSourceIds.ToArray());
        Assert.IsTrue(lifecycle.IsPending(failedSource));
        Assert.IsFalse(lifecycle.IsPending(completedSource));
        Assert.IsNull(await playback.StartAsync(failedSource, ChannelId.Generate()));
        Assert.IsNull(await playback.StartAsync(completedSource, ChannelId.Generate()));
    }

    [TestMethod]
    public async Task CancellationBetweenEntriesOccursOnlyAfterSelectedEntryConverges()
    {
        var journal = new ConcurrentQueue<string>();
        SourceId first = SourceIdAt(1);
        SourceId second = SourceIdAt(2);
        using var cancellation = new CancellationTokenSource();
        var lifecycle = new ReconciliationSourceDeletionLifecycle(journal)
        {
            AfterComplete = sourceId =>
            {
                if (sourceId == first)
                {
                    cancellation.Cancel();
                }
            },
        };
        lifecycle.SeedPending(first, second);
        await using var playback = new PlaybackSessionCoordinator(
            new SourceDeletionPlaybackEngine(journal));
        await using var coordinator = new SourceDeletionCoordinator(lifecycle, playback);

        OperationCanceledException exception =
            await Assert.ThrowsExactlyAsync<OperationCanceledException>(async () =>
                await coordinator.ReconcilePendingAsync(cancellation.Token));

        Assert.AreEqual(cancellation.Token, exception.CancellationToken);
        Assert.IsFalse(lifecycle.IsPending(first));
        Assert.IsTrue(lifecycle.IsPending(second));
        CollectionAssert.AreEqual(new[] { first }, lifecycle.MarkedSourceIds.ToArray());
        Assert.IsFalse(lifecycle.MarkTokens.Single().CanBeCanceled);
        Assert.IsFalse(lifecycle.CompleteTokens.Single().CanBeCanceled);
    }

    [TestMethod]
    public async Task DeleteAndReconciliationShareOneSerializationGate()
    {
        var journal = new ConcurrentQueue<string>();
        SourceId sourceId = SourceIdAt(1);
        var lifecycle = new ReconciliationSourceDeletionLifecycle(journal)
        {
            BlockFirstMark = true,
        };
        lifecycle.SeedPending(sourceId);
        await using var playback = new PlaybackSessionCoordinator(
            new SourceDeletionPlaybackEngine(journal));
        await using var coordinator = new SourceDeletionCoordinator(lifecycle, playback);

        Task<SourceDeletionResult> deletion = coordinator.DeleteAsync(sourceId).AsTask();
        await lifecycle.FirstMarkEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Task<SourceDeletionReconciliationResult> reconciliation = coordinator
            .ReconcilePendingAsync()
            .AsTask();

        Assert.IsFalse(reconciliation.IsCompleted);
        Assert.AreEqual(0, lifecycle.ReadCount);
        lifecycle.ReleaseFirstMark.TrySetResult();

        Assert.IsTrue((await deletion.WaitAsync(TimeSpan.FromSeconds(2))).IsSuccess);
        Assert.IsTrue((await reconciliation.WaitAsync(TimeSpan.FromSeconds(2))).IsSuccess);
        Assert.AreEqual(1, lifecycle.ReadCount);
        Assert.AreEqual(1, lifecycle.MaximumConcurrentCalls);
    }

    [TestMethod]
    public async Task DisposeWaitsForActiveReconciliationAndThenRejectsNewWork()
    {
        var journal = new ConcurrentQueue<string>();
        SourceId sourceId = SourceIdAt(1);
        var lifecycle = new ReconciliationSourceDeletionLifecycle(journal)
        {
            BlockFirstMark = true,
        };
        lifecycle.SeedPending(sourceId);
        await using var playback = new PlaybackSessionCoordinator(
            new SourceDeletionPlaybackEngine(journal));
        var coordinator = new SourceDeletionCoordinator(lifecycle, playback);

        Task<SourceDeletionReconciliationResult> reconciliation = coordinator
            .ReconcilePendingAsync()
            .AsTask();
        await lifecycle.FirstMarkEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Task disposal = coordinator.DisposeAsync().AsTask();

        Assert.IsFalse(disposal.IsCompleted);
        lifecycle.ReleaseFirstMark.TrySetResult();
        Assert.IsTrue((await reconciliation.WaitAsync(TimeSpan.FromSeconds(2))).IsSuccess);
        await disposal.WaitAsync(TimeSpan.FromSeconds(2));
        await Assert.ThrowsExactlyAsync<ObjectDisposedException>(async () =>
            await coordinator.ReconcilePendingAsync());
        await Assert.ThrowsExactlyAsync<ObjectDisposedException>(async () =>
            await coordinator.DeleteAsync(sourceId));
    }

    [TestMethod]
    public async Task DiscoveryFailureReturnsBoundedRedactedAggregate()
    {
        string sensitive = SecurityTestAssertions.CreateSensitiveValue(
            "SOURCE-DELETION-RECONCILIATION");
        SourceId sourceId = SourceIdAt(1);
        var journal = new ConcurrentQueue<string>();
        var lifecycle = new ReconciliationSourceDeletionLifecycle(journal)
        {
            ReadException = new InvalidOperationException(
                $"{sensitive} https://user:secret@fixtures.invalid/{sourceId}"),
        };
        await using var playback = new PlaybackSessionCoordinator(
            new SourceDeletionPlaybackEngine(journal));
        await using var coordinator = new SourceDeletionCoordinator(lifecycle, playback);

        SourceDeletionReconciliationResult result =
            await coordinator.ReconcilePendingAsync();
        SourceDeletionPendingBatchReadResult batch =
            SourceDeletionPendingBatchReadResult.Succeeded([sourceId], sourceId);

        Assert.IsFalse(result.IsSuccess);
        Assert.AreEqual(0, result.AttemptedCount);
        Assert.IsTrue(result.HasRemaining);
        Assert.AreEqual(SourceDeletionFailureStage.PendingDiscovery, result.FirstFailureStage);
        Assert.AreEqual(DomainErrorCode.StorageUnavailable, result.FirstError?.Code);
        SecurityTestAssertions.DoesNotContainSensitive(result.ToString(), sensitive);
        Assert.IsFalse(result.ToString().Contains(sourceId.ToString(), StringComparison.Ordinal));
        Assert.IsFalse(batch.ToString().Contains(sourceId.ToString(), StringComparison.Ordinal));
        Assert.IsFalse(result.ToString().Contains("fixtures.invalid", StringComparison.Ordinal));
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

    private static SourceId SourceIdAt(int ordinal)
    {
        Guid value = Guid.ParseExact(
            ordinal.ToString("x32", CultureInfo.InvariantCulture),
            "N");
        DomainResult<SourceId> sourceId = SourceId.Create(value);
        Assert.IsTrue(sourceId.IsSuccess);
        return sourceId.Value;
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

        public ValueTask<SourceDeletionPendingCursorReadResult> ReadPendingCursorAsync(
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(
                SourceDeletionPendingCursorReadResult.Succeeded());
        }

        public ValueTask<SourceDeletionLifecycleOperationResult> AdvancePendingCursorAsync(
            SourceId sourceId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(
                SourceDeletionLifecycleOperationResult.Succeeded());
        }

        public ValueTask<SourceDeletionPendingBatchReadResult> ReadPendingBatchAsync(
            SourceId? afterExclusive = null,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(
                SourceDeletionPendingBatchReadResult.Succeeded([]));
        }

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

    private sealed class ReconciliationSourceDeletionLifecycle : ISourceDeletionLifecycle
    {
        private readonly ConcurrentQueue<string> _journal;
        private readonly ReconciliationDurableState _durableState;
        private int _activeCalls;
        private int _markCount;
        private int _maximumConcurrentCalls;
        private int _readCount;

        internal ReconciliationSourceDeletionLifecycle(
            ConcurrentQueue<string> journal,
            ReconciliationDurableState? durableState = null)
        {
            _journal = journal;
            _durableState = durableState ?? new ReconciliationDurableState();
        }

        internal ReconciliationDurableState DurableState => _durableState;

        internal Action<SourceId>? AfterComplete { get; init; }

        internal bool BlockFirstMark { get; init; }

        internal Exception? ReadException { get; init; }

        internal HashSet<SourceId> MarkFailureSources { get; } = [];

        internal HashSet<SourceId> CompleteFailureSources { get; } = [];

        internal HashSet<SourceId> CursorAdvanceFailureSources { get; } = [];

        internal ConcurrentQueue<SourceId> MarkedSourceIds { get; } = new();

        internal ConcurrentQueue<SourceId?> ReadCursors { get; } = new();

        internal ConcurrentQueue<CancellationToken> MarkTokens { get; } = new();

        internal ConcurrentQueue<CancellationToken> CompleteTokens { get; } = new();

        internal TaskCompletionSource FirstMarkEntered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource ReleaseFirstMark { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal CancellationToken ReadToken { get; private set; }

        internal int MaximumConcurrentCalls => Volatile.Read(ref _maximumConcurrentCalls);

        internal int ReadCount => Volatile.Read(ref _readCount);

        public ValueTask<SourceDeletionPendingCursorReadResult> ReadPendingCursorAsync(
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_durableState.Sync)
            {
                return ValueTask.FromResult(
                    SourceDeletionPendingCursorReadResult.Succeeded(
                        _durableState.Cursor));
            }
        }

        public ValueTask<SourceDeletionLifecycleOperationResult> AdvancePendingCursorAsync(
            SourceId sourceId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_durableState.Sync)
            {
                if (CursorAdvanceFailureSources.Contains(sourceId))
                {
                    return ValueTask.FromResult(
                        SourceDeletionLifecycleOperationResult.Failed(
                            DomainErrorCode.StorageUnavailable));
                }

                if (!_durableState.Pending.Contains(sourceId))
                {
                    return ValueTask.FromResult(
                        SourceDeletionLifecycleOperationResult.Failed(
                            DomainErrorCode.DomainInvariantViolation));
                }

                _durableState.Cursor = sourceId;
            }

            return ValueTask.FromResult(
                SourceDeletionLifecycleOperationResult.Succeeded());
        }

        public ValueTask<SourceDeletionPendingBatchReadResult> ReadPendingBatchAsync(
            SourceId? afterExclusive = null,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            EnterCall();
            try
            {
                ReadToken = cancellationToken;
                Interlocked.Increment(ref _readCount);
                ReadCursors.Enqueue(afterExclusive);
                _journal.Enqueue("ReadPending");
                if (ReadException is not null)
                {
                    throw ReadException;
                }

                string? after = afterExclusive?.Value.ToString("N");
                SourceId[] page;
                lock (_durableState.Sync)
                {
                    page = _durableState.Pending
                        .Where(sourceId => after is null || string.CompareOrdinal(
                            sourceId.Value.ToString("N"),
                            after) > 0)
                        .OrderBy(static sourceId => sourceId.Value.ToString("N"), StringComparer.Ordinal)
                        .Take(SourceDeletionPendingBatchReadResult.MaximumSourceCount + 1)
                        .ToArray();
                }

                bool hasMore =
                    page.Length > SourceDeletionPendingBatchReadResult.MaximumSourceCount;
                SourceId[] returned = hasMore
                    ? page[..SourceDeletionPendingBatchReadResult.MaximumSourceCount]
                    : page;
                SourceId? nextAfter = hasMore ? returned[^1] : null;
                return ValueTask.FromResult(
                    SourceDeletionPendingBatchReadResult.Succeeded(returned, nextAfter));
            }
            finally
            {
                ExitCall();
            }
        }

        public async ValueTask<SourceDeletionLifecycleOperationResult> MarkPendingAsync(
            SourceId sourceId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            EnterCall();
            try
            {
                MarkTokens.Enqueue(cancellationToken);
                MarkedSourceIds.Enqueue(sourceId);
                _journal.Enqueue("MarkPending");
                int call = Interlocked.Increment(ref _markCount);
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

                if (MarkFailureSources.Contains(sourceId))
                {
                    return SourceDeletionLifecycleOperationResult.Failed(
                        DomainErrorCode.StorageUnavailable);
                }

                lock (_durableState.Sync)
                {
                    _durableState.Pending.Add(sourceId);
                }

                return SourceDeletionLifecycleOperationResult.Succeeded();
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
            cancellationToken.ThrowIfCancellationRequested();
            EnterCall();
            try
            {
                CompleteTokens.Enqueue(cancellationToken);
                _journal.Enqueue("CompletePending");
                if (CompleteFailureSources.Contains(sourceId))
                {
                    return ValueTask.FromResult(
                        SourceDeletionLifecycleOperationResult.Failed(
                            DomainErrorCode.StorageUnavailable));
                }

                lock (_durableState.Sync)
                {
                    _durableState.Pending.Remove(sourceId);
                }

                AfterComplete?.Invoke(sourceId);
                return ValueTask.FromResult(
                    SourceDeletionLifecycleOperationResult.Succeeded());
            }
            finally
            {
                ExitCall();
            }
        }

        internal bool IsPending(SourceId sourceId)
        {
            lock (_durableState.Sync)
            {
                return _durableState.Pending.Contains(sourceId);
            }
        }

        internal void SeedPending(params SourceId[] sourceIds)
        {
            lock (_durableState.Sync)
            {
                foreach (SourceId sourceId in sourceIds)
                {
                    _durableState.Pending.Add(sourceId);
                }
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

    private sealed class ReconciliationDurableState
    {
        internal object Sync { get; } = new();

        internal HashSet<SourceId> Pending { get; } = [];

        internal SourceId? Cursor { get; set; }
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
