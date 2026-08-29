using IptvSuite.Application;
using IptvSuite.Domain;
using System.Reflection;

namespace IptvSuite.UnitTests;

[TestClass]
public sealed class PlaybackTimelineCoordinatorTests
{
    [TestMethod]
    public async Task LiveSessionUsesLiveIntentAndRejectsSeek()
    {
        var engine = new TimelinePlaybackEngine();
        await using var coordinator = new PlaybackSessionCoordinator(engine);

        PlaybackSessionSnapshot started = (await coordinator.StartAsync(
            SourceId.Generate(),
            ChannelId.Generate()))!;

        Assert.AreEqual(PlaybackContentIntent.Live, engine.LastContentIntent);
        Assert.AreEqual(PlaybackContentIntent.Live, started.ContentIntent);
        Assert.AreEqual(PlaybackTargetKind.Live, started.Target!.Kind);
        Assert.IsFalse(coordinator.CurrentTimeline.CanSeek);

        engine.PublishTimeline(
            started.SessionId,
            TimeSpan.FromSeconds(10),
            TimeSpan.FromMinutes(30),
            canSeek: true);

        Assert.AreEqual(started.SessionId, coordinator.CurrentTimeline.SessionId);
        Assert.AreEqual(TimeSpan.Zero, coordinator.CurrentTimeline.Duration);
        Assert.IsFalse(coordinator.CurrentTimeline.CanSeek);

        PlaybackEngineOperationResult seek = await coordinator.SeekAsync(
            started.SessionId,
            TimeSpan.FromSeconds(1));

        Assert.IsFalse(seek.IsSuccess);
        Assert.AreEqual(DomainErrorCode.DomainInvariantViolation, seek.Error!.Code);
        Assert.AreEqual(0, engine.SeekCount);
    }

    [TestMethod]
    public async Task OnDemandSessionPublishesTimelineAndSeeksWithinRange()
    {
        var engine = new TimelinePlaybackEngine();
        await using var coordinator = new PlaybackSessionCoordinator(engine);
        var observed = new List<PlaybackTimelineSnapshot>();
        coordinator.TimelineChanged += (_, args) => observed.Add(args.Snapshot);

        PlaybackSessionSnapshot started = (await coordinator.StartAsync(
            SourceId.Generate(),
            MovieId.Generate()))!;
        engine.PublishTimeline(
            started.SessionId,
            TimeSpan.FromSeconds(15),
            TimeSpan.FromMinutes(2),
            canSeek: true);

        PlaybackEngineOperationResult seek = await coordinator.SeekAsync(
            started.SessionId,
            TimeSpan.FromSeconds(45));

        Assert.IsTrue(seek.IsSuccess);
        Assert.AreEqual(PlaybackContentIntent.OnDemand, engine.LastContentIntent);
        Assert.AreEqual(PlaybackContentIntent.OnDemand, started.ContentIntent);
        Assert.AreEqual(PlaybackTargetKind.Movie, started.Target!.Kind);
        Assert.AreEqual(1, engine.SeekCount);
        Assert.AreEqual(TimeSpan.FromSeconds(45), engine.CurrentTimeline.Position);
        Assert.AreEqual(TimeSpan.FromSeconds(45), coordinator.CurrentTimeline.Position);
        Assert.AreEqual(TimeSpan.FromMinutes(2), coordinator.CurrentTimeline.Duration);
        Assert.IsTrue(coordinator.CurrentTimeline.CanSeek);
        Assert.IsTrue(observed.Count >= 2);
    }

    [TestMethod]
    public async Task SeekRejectsNegativeOutOfRangeStaleAndDisposedRequests()
    {
        var engine = new TimelinePlaybackEngine();
        var coordinator = new PlaybackSessionCoordinator(engine);
        PlaybackSessionSnapshot first = (await coordinator.StartAsync(
            SourceId.Generate(),
            EpisodeId.Generate()))!;
        engine.PublishTimeline(
            first.SessionId,
            TimeSpan.FromSeconds(10),
            TimeSpan.FromMinutes(1),
            canSeek: true);

        await Assert.ThrowsExactlyAsync<ArgumentOutOfRangeException>(async () =>
            await coordinator.SeekAsync(first.SessionId, TimeSpan.FromTicks(-1)));

        PlaybackEngineOperationResult outOfRange = await coordinator.SeekAsync(
            first.SessionId,
            TimeSpan.FromMinutes(2));
        Assert.IsFalse(outOfRange.IsSuccess);
        Assert.AreEqual(DomainErrorCode.DomainInvariantViolation, outOfRange.Error!.Code);

        PlaybackSessionSnapshot second = (await coordinator.StartAsync(
            SourceId.Generate(),
            MovieId.Generate()))!;
        PlaybackEngineOperationResult stale = await coordinator.SeekAsync(
            first.SessionId,
            TimeSpan.Zero);
        Assert.IsFalse(stale.IsSuccess);
        Assert.AreEqual(DomainErrorCode.OperationCancelled, stale.Error!.Code);

        await coordinator.DisposeAsync();
        await Assert.ThrowsExactlyAsync<ObjectDisposedException>(async () =>
            await coordinator.SeekAsync(second.SessionId, TimeSpan.Zero));
    }

    [TestMethod]
    public async Task StaleTimelineEventsCannotReplaceCurrentSessionTimeline()
    {
        var engine = new TimelinePlaybackEngine();
        await using var coordinator = new PlaybackSessionCoordinator(engine);
        PlaybackSessionSnapshot first = (await coordinator.StartAsync(
            SourceId.Generate(),
            MovieId.Generate()))!;
        PlaybackSessionSnapshot second = (await coordinator.StartAsync(
            SourceId.Generate(),
            EpisodeId.Generate()))!;
        engine.PublishTimeline(
            second.SessionId,
            TimeSpan.FromSeconds(20),
            TimeSpan.FromMinutes(3),
            canSeek: true);

        engine.PublishTimeline(
            first.SessionId,
            TimeSpan.FromSeconds(50),
            TimeSpan.FromMinutes(4),
            canSeek: true,
            updateCurrent: false);

        Assert.AreEqual(second.SessionId, coordinator.CurrentTimeline.SessionId);
        Assert.AreEqual(TimeSpan.FromSeconds(20), coordinator.CurrentTimeline.Position);
        Assert.AreEqual(TimeSpan.FromMinutes(3), coordinator.CurrentTimeline.Duration);
    }

    [TestMethod]
    public async Task OnDemandCompletionIsTerminalWhileLiveInterruptionRemainsFailure()
    {
        var engine = new TimelinePlaybackEngine();
        await using var coordinator = new PlaybackSessionCoordinator(engine);
        PlaybackSessionSnapshot onDemand = (await coordinator.StartAsync(
            SourceId.Generate(),
            MovieId.Generate()))!;
        engine.Emit(PlaybackEngineSnapshot.Active(
            onDemand.SessionId,
            PlaybackState.Completed));

        Assert.AreEqual(PlaybackState.Completed, coordinator.Current.State);
        Assert.AreEqual(PlaybackContentIntent.OnDemand, coordinator.Current.ContentIntent);
        PlaybackEngineOperationResult replay = await coordinator.PlayAsync();
        Assert.IsFalse(replay.IsSuccess);

        PlaybackSessionSnapshot live = (await coordinator.StartAsync(
            SourceId.Generate(),
            ChannelId.Generate()))!;
        engine.Emit(PlaybackEngineSnapshot.Failed(
            live.SessionId,
            DomainError.Create(DomainErrorCode.StreamInterrupted)));

        Assert.AreEqual(PlaybackState.Failed, coordinator.Current.State);
        Assert.AreEqual(DomainErrorCode.StreamInterrupted, coordinator.Current.Error!.Code);
    }

    [TestMethod]
    public async Task PausedOnDemandSessionCanSeekAndKeepsFinalTimelineWhenCompleted()
    {
        var engine = new TimelinePlaybackEngine();
        await using var coordinator = new PlaybackSessionCoordinator(engine);
        PlaybackSessionSnapshot onDemand = (await coordinator.StartAsync(
            SourceId.Generate(),
            EpisodeId.Generate()))!;
        engine.PublishTimeline(
            onDemand.SessionId,
            TimeSpan.FromMinutes(5),
            TimeSpan.FromMinutes(50),
            canSeek: true);
        engine.Emit(PlaybackEngineSnapshot.Active(
            onDemand.SessionId,
            PlaybackState.Paused));

        PlaybackEngineOperationResult seek = await coordinator.SeekAsync(
            onDemand.SessionId,
            TimeSpan.FromMinutes(25));

        Assert.IsTrue(seek.IsSuccess);
        Assert.AreEqual(PlaybackState.Paused, coordinator.Current.State);
        Assert.AreEqual(TimeSpan.FromMinutes(25), coordinator.CurrentTimeline.Position);

        engine.PublishTimeline(
            onDemand.SessionId,
            TimeSpan.FromMinutes(50),
            TimeSpan.FromMinutes(50),
            canSeek: true);
        engine.Emit(PlaybackEngineSnapshot.Active(
            onDemand.SessionId,
            PlaybackState.Completed));

        Assert.AreEqual(PlaybackState.Completed, coordinator.Current.State);
        Assert.AreEqual(TimeSpan.FromMinutes(50), coordinator.CurrentTimeline.Position);
        Assert.AreEqual(TimeSpan.FromMinutes(50), coordinator.CurrentTimeline.Duration);
    }

    [TestMethod]
    public void TimelineAndTargetFactoriesRejectContradictoryValues()
    {
        Assert.ThrowsExactly<ArgumentException>(() => PlaybackTarget.Live(default));
        Assert.ThrowsExactly<ArgumentException>(() => PlaybackTarget.Movie(default));
        Assert.ThrowsExactly<ArgumentException>(() => PlaybackTarget.Episode(default));
        Assert.ThrowsExactly<ArgumentException>(() =>
            PlaybackTimelineSnapshot.Unavailable(default));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            PlaybackTimelineSnapshot.Create(
                CreateSessionId(),
                TimeSpan.FromSeconds(-1),
                TimeSpan.FromSeconds(1),
                canSeek: false));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            PlaybackTimelineSnapshot.Create(
                CreateSessionId(),
                TimeSpan.FromSeconds(2),
                TimeSpan.FromSeconds(1),
                canSeek: false));
        Assert.ThrowsExactly<ArgumentException>(() =>
            PlaybackTimelineSnapshot.Create(
                CreateSessionId(),
                TimeSpan.Zero,
                TimeSpan.Zero,
                canSeek: true));
    }

    private static PlaybackSessionId CreateSessionId()
    {
        MethodInfo factory = typeof(PlaybackSessionId).GetMethod(
            "FromSequence",
            BindingFlags.NonPublic | BindingFlags.Static)!;
        return (PlaybackSessionId)factory.Invoke(null, [1L])!;
    }

    private sealed class TimelinePlaybackEngine : IPlaybackEngine, IPlaybackTimelineEngine
    {
        private PlaybackEngineSnapshot _current = PlaybackEngineSnapshot.Closed();
        private PlaybackControlSnapshot _controls = PlaybackControlSnapshot.Idle(
            PlaybackVolume.FromPercent(100),
            isMuted: false,
            PlaybackAspectMode.Fit);
        private PlaybackTimelineSnapshot _timeline = PlaybackTimelineSnapshot.Unavailable();
        private PlaybackContentIntent _contentIntent = PlaybackContentIntent.Live;

        public event EventHandler<PlaybackEngineStateChangedEventArgs>? StateChanged;

        public event EventHandler<PlaybackTimelineChangedEventArgs>? TimelineChanged;

        public PlaybackEngineSnapshot Current => _current;

        public PlaybackControlSnapshot CurrentControls => _controls;

        public PlaybackTimelineSnapshot CurrentTimeline => _timeline;

        internal PlaybackContentIntent LastContentIntent => _contentIntent;

        internal int SeekCount { get; private set; }

        public ValueTask<PlaybackEngineOperationResult> OpenAsync(
            PlaybackSessionId sessionId,
            PlaybackSelection selection,
            CancellationToken cancellationToken = default) =>
            OpenAsync(
                sessionId,
                selection,
                PlaybackContentIntent.Live,
                cancellationToken);

        public ValueTask<PlaybackEngineOperationResult> OpenAsync(
            PlaybackSessionId sessionId,
            PlaybackSelection selection,
            PlaybackContentIntent contentIntent,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _contentIntent = contentIntent;
            _controls = PlaybackControlSnapshot.Active(
                sessionId,
                _controls.Volume,
                _controls.IsMuted,
                _controls.AspectMode);
            _timeline = PlaybackTimelineSnapshot.Unavailable(sessionId);
            Emit(PlaybackEngineSnapshot.Active(sessionId, PlaybackState.Buffering));
            TimelineChanged?.Invoke(this, new PlaybackTimelineChangedEventArgs(_timeline));
            return ValueTask.FromResult(PlaybackEngineOperationResult.Succeeded());
        }

        public ValueTask<PlaybackEngineOperationResult> PlayAsync(
            PlaybackSessionId sessionId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Emit(PlaybackEngineSnapshot.Active(sessionId, PlaybackState.Playing));
            return ValueTask.FromResult(PlaybackEngineOperationResult.Succeeded());
        }

        public ValueTask<PlaybackEngineOperationResult> PauseAsync(
            PlaybackSessionId sessionId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Emit(PlaybackEngineSnapshot.Active(sessionId, PlaybackState.Paused));
            return ValueTask.FromResult(PlaybackEngineOperationResult.Succeeded());
        }

        public ValueTask<PlaybackEngineOperationResult> StopAsync(
            PlaybackSessionId sessionId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _current = PlaybackEngineSnapshot.Closed(sessionId);
            _timeline = PlaybackTimelineSnapshot.Unavailable();
            _controls = PlaybackControlSnapshot.Idle(
                _controls.Volume,
                _controls.IsMuted,
                _controls.AspectMode);
            StateChanged?.Invoke(this, new PlaybackEngineStateChangedEventArgs(_current));
            return ValueTask.FromResult(PlaybackEngineOperationResult.Succeeded());
        }

        public ValueTask<PlaybackEngineOperationResult> SetVolumeAsync(
            PlaybackSessionId sessionId,
            PlaybackVolume volume,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
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
            cancellationToken.ThrowIfCancellationRequested();
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
            cancellationToken.ThrowIfCancellationRequested();
            _controls = PlaybackControlSnapshot.Active(
                sessionId,
                _controls.Volume,
                _controls.IsMuted,
                aspectMode);
            return ValueTask.FromResult(PlaybackEngineOperationResult.Succeeded());
        }

        public ValueTask<DomainResult<PlaybackTrackSnapshot>> GetTracksAsync(
            PlaybackSessionId sessionId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(DomainResult.Success(
                PlaybackTrackSnapshot.Create(
                    sessionId,
                    PlaybackTrackCapabilities.None,
                    [])));
        }

        public ValueTask<PlaybackEngineOperationResult> SelectTrackAsync(
            PlaybackSessionId sessionId,
            PlaybackTrackId trackId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(PlaybackEngineOperationResult.Failed(
                DomainErrorCode.PlaybackControlFailed));
        }

        public ValueTask<PlaybackEngineOperationResult> SeekAsync(
            PlaybackSessionId sessionId,
            TimeSpan position,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_contentIntent != PlaybackContentIntent.OnDemand ||
                _timeline.SessionId != sessionId ||
                !_timeline.CanSeek ||
                position < TimeSpan.Zero ||
                position > _timeline.Duration)
            {
                return ValueTask.FromResult(PlaybackEngineOperationResult.Failed(
                    DomainErrorCode.PlaybackControlFailed));
            }

            SeekCount++;
            PublishTimeline(
                sessionId,
                position,
                _timeline.Duration,
                canSeek: true);
            return ValueTask.FromResult(PlaybackEngineOperationResult.Succeeded());
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        internal void Emit(PlaybackEngineSnapshot snapshot)
        {
            _current = snapshot;
            StateChanged?.Invoke(this, new PlaybackEngineStateChangedEventArgs(snapshot));
        }

        internal void PublishTimeline(
            PlaybackSessionId sessionId,
            TimeSpan position,
            TimeSpan duration,
            bool canSeek,
            bool updateCurrent = true)
        {
            PlaybackTimelineSnapshot snapshot = PlaybackTimelineSnapshot.Create(
                sessionId,
                position,
                duration,
                canSeek);
            if (updateCurrent)
            {
                _timeline = snapshot;
            }

            TimelineChanged?.Invoke(this, new PlaybackTimelineChangedEventArgs(snapshot));
        }
    }
}
