using IptvSuite.Application;
using IptvSuite.Domain;
using System.Text.Json;

namespace IptvSuite.UnitTests;

[TestClass]
public sealed class PlaybackControlCoordinatorTests
{
    [TestMethod]
    public void ControlContractsRejectInvalidValuesAndTrackInventories()
    {
        Assert.AreEqual(0, PlaybackVolume.FromPercent(0).Percent);
        Assert.AreEqual(100, PlaybackVolume.FromPercent(100).Percent);
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => PlaybackVolume.FromPercent(-1));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => PlaybackVolume.FromPercent(101));
        CollectionAssert.AreEqual(
            new[] { PlaybackAspectMode.Fit, PlaybackAspectMode.Fill },
            Enum.GetValues<PlaybackAspectMode>());

        PlaybackSessionId session = Session(1);
        PlaybackSessionId otherSession = Session(2);
        PlaybackTrackId audio = PlaybackTrackId.Create(session, PlaybackTrackKind.Audio, 1);
        Assert.ThrowsExactly<ArgumentException>(() =>
            PlaybackTrackId.Create(default, PlaybackTrackKind.Audio, 1));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            PlaybackTrackId.Create(session, (PlaybackTrackKind)int.MaxValue, 1));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            PlaybackTrackId.Create(session, PlaybackTrackKind.Audio, 0));
        Assert.ThrowsExactly<ArgumentException>(() =>
            new PlaybackTrackInfo(default, isSelected: false, isSelectable: false));
        Assert.ThrowsExactly<ArgumentException>(() =>
            PlaybackTrackSnapshot.Create(
                session,
                PlaybackTrackCapabilities.AudioSelection,
                [
                    new PlaybackTrackInfo(audio, isSelected: true, isSelectable: true),
                    new PlaybackTrackInfo(audio, isSelected: false, isSelectable: true),
                ]));
        Assert.ThrowsExactly<ArgumentException>(() =>
            PlaybackTrackSnapshot.Create(
                session,
                PlaybackTrackCapabilities.AudioSelection,
                [new PlaybackTrackInfo(
                    PlaybackTrackId.Create(otherSession, PlaybackTrackKind.Audio, 1),
                    isSelected: true,
                    isSelectable: true)]));
        Assert.ThrowsExactly<ArgumentException>(() =>
            PlaybackTrackSnapshot.Create(
                session,
                PlaybackTrackCapabilities.AudioSelection,
                [
                    new PlaybackTrackInfo(audio, isSelected: true, isSelectable: true),
                    new PlaybackTrackInfo(
                        PlaybackTrackId.Create(session, PlaybackTrackKind.Audio, 2),
                        isSelected: true,
                        isSelectable: true),
                ]));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            PlaybackTrackSnapshot.Create(
                session,
                (PlaybackTrackCapabilities)int.MaxValue,
                []));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            PlaybackTrackSnapshot.Create(
                session,
                PlaybackTrackCapabilities.None,
                Enumerable.Range(1, PlaybackTrackSnapshot.MaximumTrackCount + 1)
                    .Select(ordinal => new PlaybackTrackInfo(
                        PlaybackTrackId.Create(session, PlaybackTrackKind.Audio, ordinal),
                        isSelected: false,
                        isSelectable: false))));
    }

    [TestMethod]
    public void TrackSnapshotDefensivelyCopiesBoundedSessionInventory()
    {
        PlaybackSessionId session = Session(1);
        var mutable = new List<PlaybackTrackInfo>
        {
            new(
                PlaybackTrackId.Create(session, PlaybackTrackKind.Audio, 1),
                isSelected: true,
                isSelectable: false),
        };

        PlaybackTrackSnapshot snapshot = PlaybackTrackSnapshot.Create(
            session,
            PlaybackTrackCapabilities.None,
            mutable);
        mutable.Clear();

        Assert.HasCount(1, snapshot.Tracks);
        Assert.IsFalse(snapshot.Tracks[0].IsSelectable);
        Assert.IsFalse(snapshot.CanSelect(snapshot.Tracks[0].Id));
    }

    [TestMethod]
    public async Task ControlsTargetOnlyCurrentSessionAndMutePreservesVolume()
    {
        var engine = new ControlPlaybackEngine();
        await using var coordinator = new PlaybackSessionCoordinator(engine);
        PlaybackSessionSnapshot playing = (await coordinator.StartAsync(
            SourceId.Generate(),
            ChannelId.Generate()))!;

        Assert.IsTrue((await coordinator.SetVolumeAsync(
            playing.SessionId,
            PlaybackVolume.FromPercent(37))).IsSuccess);
        Assert.IsTrue((await coordinator.SetMutedAsync(playing.SessionId, isMuted: true)).IsSuccess);
        Assert.IsTrue((await coordinator.SetAspectModeAsync(
            playing.SessionId,
            PlaybackAspectMode.Fill)).IsSuccess);

        Assert.AreEqual(37, coordinator.CurrentControls.Volume.Percent);
        Assert.IsTrue(coordinator.CurrentControls.IsMuted);
        Assert.AreEqual(PlaybackAspectMode.Fill, coordinator.CurrentControls.AspectMode);
        Assert.IsTrue(engine.ControlSessions.All(session => session == playing.SessionId));

        PlaybackEngineOperationResult stale = await coordinator.SetMutedAsync(Session(999), isMuted: false);
        Assert.IsFalse(stale.IsSuccess);
        Assert.AreEqual(DomainErrorCode.OperationCancelled, stale.Error?.Code);
        Assert.IsTrue(coordinator.CurrentControls.IsMuted);
    }

    [TestMethod]
    public async Task CoordinatorRestoresControlsWhenReplacementOpenResetsEngineState()
    {
        var engine = new ControlPlaybackEngine { ResetControlsOnOpen = true };
        await using var coordinator = new PlaybackSessionCoordinator(engine);
        PlaybackSessionSnapshot first = (await coordinator.StartAsync(
            SourceId.Generate(),
            ChannelId.Generate()))!;
        await coordinator.SetVolumeAsync(first.SessionId, PlaybackVolume.FromPercent(37));
        await coordinator.SetMutedAsync(first.SessionId, isMuted: true);
        await coordinator.SetAspectModeAsync(first.SessionId, PlaybackAspectMode.Fill);

        PlaybackSessionSnapshot second = (await coordinator.StartAsync(
            SourceId.Generate(),
            ChannelId.Generate()))!;

        Assert.AreEqual(second.SessionId, engine.CurrentControls.SessionId);
        Assert.AreEqual(coordinator.CurrentControls, engine.CurrentControls);
        Assert.AreEqual(37, engine.CurrentControls.Volume.Percent);
        Assert.IsTrue(engine.CurrentControls.IsMuted);
        Assert.AreEqual(PlaybackAspectMode.Fill, engine.CurrentControls.AspectMode);
    }

    [TestMethod]
    public async Task ControlFailureDoesNotStopHealthyPlayback()
    {
        var engine = new ControlPlaybackEngine();
        await using var coordinator = new PlaybackSessionCoordinator(engine);
        PlaybackSessionSnapshot playing = (await coordinator.StartAsync(
            SourceId.Generate(),
            ChannelId.Generate()))!;
        engine.FailControl = true;

        PlaybackEngineOperationResult result = await coordinator.SetVolumeAsync(
            playing.SessionId,
            PlaybackVolume.FromPercent(12));

        Assert.IsFalse(result.IsSuccess);
        Assert.AreEqual(DomainErrorCode.PlaybackControlFailed, result.Error?.Code);
        Assert.AreEqual(PlaybackState.Playing, coordinator.Current.State);
        Assert.AreEqual(100, coordinator.CurrentControls.Volume.Percent);
        Assert.IsEmpty(engine.StopSessions);
    }

    [TestMethod]
    public async Task UnrequestedControlCancellationBecomesNonterminalTypedFailure()
    {
        var engine = new ControlPlaybackEngine();
        await using var coordinator = new PlaybackSessionCoordinator(engine);
        PlaybackSessionSnapshot playing = (await coordinator.StartAsync(
            SourceId.Generate(),
            ChannelId.Generate()))!;
        engine.ThrowUnrequestedControlCancellation = true;

        PlaybackEngineOperationResult result = await coordinator.SetVolumeAsync(
            playing.SessionId,
            PlaybackVolume.FromPercent(12));

        Assert.AreEqual(DomainErrorCode.PlaybackControlFailed, result.Error?.Code);
        Assert.AreEqual(PlaybackState.Playing, coordinator.Current.State);
        Assert.IsEmpty(engine.StopSessions);
    }

    [TestMethod]
    public async Task EngineFailureCallbackDuringControlRemainsTerminal()
    {
        var engine = new ControlPlaybackEngine();
        await using var coordinator = new PlaybackSessionCoordinator(engine);
        PlaybackSessionSnapshot playing = (await coordinator.StartAsync(
            SourceId.Generate(),
            ChannelId.Generate()))!;
        engine.EmitFailureDuringControl = true;

        PlaybackEngineOperationResult result = await coordinator.SetVolumeAsync(
            playing.SessionId,
            PlaybackVolume.FromPercent(12));

        Assert.AreEqual(DomainErrorCode.StreamInterrupted, result.Error?.Code);
        Assert.AreEqual(PlaybackState.Failed, coordinator.Current.State);
        CollectionAssert.AreEqual(new[] { playing.SessionId }, engine.StopSessions.ToArray());
    }

    [TestMethod]
    public async Task ReplacementCancelsInFlightControlWithoutMutatingNewSession()
    {
        var engine = new ControlPlaybackEngine();
        await using var coordinator = new PlaybackSessionCoordinator(engine);
        PlaybackSessionSnapshot first = (await coordinator.StartAsync(
            SourceId.Generate(),
            ChannelId.Generate()))!;
        engine.BlockVolume = true;
        Task<PlaybackEngineOperationResult> volume = coordinator.SetVolumeAsync(
            first.SessionId,
            PlaybackVolume.FromPercent(12)).AsTask();
        await engine.VolumeEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Task<PlaybackSessionSnapshot?> replacement = coordinator.StartAsync(
            SourceId.Generate(),
            ChannelId.Generate()).AsTask();

        await engine.VolumeCancelled.Task.WaitAsync(TimeSpan.FromSeconds(2));
        PlaybackEngineOperationResult cancelled = await volume.WaitAsync(TimeSpan.FromSeconds(2));
        PlaybackSessionSnapshot second = (await replacement.WaitAsync(TimeSpan.FromSeconds(2)))!;
        Assert.AreEqual(DomainErrorCode.OperationCancelled, cancelled.Error?.Code);
        Assert.AreEqual(second.SessionId, coordinator.CurrentControls.SessionId);
        Assert.AreEqual(100, coordinator.CurrentControls.Volume.Percent);
    }

    [TestMethod]
    public async Task CallerCancellationDuringControlRemainsCancellation()
    {
        var engine = new ControlPlaybackEngine();
        await using var coordinator = new PlaybackSessionCoordinator(engine);
        PlaybackSessionSnapshot playing = (await coordinator.StartAsync(
            SourceId.Generate(),
            ChannelId.Generate()))!;
        engine.BlockVolume = true;
        using var cancellation = new CancellationTokenSource();
        Task<PlaybackEngineOperationResult> volume = coordinator.SetVolumeAsync(
            playing.SessionId,
            PlaybackVolume.FromPercent(12),
            cancellation.Token).AsTask();
        await engine.VolumeEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));

        cancellation.Cancel();

        await Assert.ThrowsExactlyAsync<OperationCanceledException>(async () =>
            await volume.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.AreEqual(PlaybackState.Playing, coordinator.Current.State);
    }

    [TestMethod]
    public async Task FailureWhileQueuedControlWaitsPreventsDispatchAndStopsExactSession()
    {
        var engine = new ControlPlaybackEngine();
        await using var coordinator = new PlaybackSessionCoordinator(engine);
        PlaybackSessionSnapshot playing = (await coordinator.StartAsync(
            SourceId.Generate(),
            ChannelId.Generate()))!;
        engine.BlockVolume = true;
        Task<PlaybackEngineOperationResult> holding = coordinator.SetVolumeAsync(
            playing.SessionId,
            PlaybackVolume.FromPercent(12)).AsTask();
        await engine.VolumeEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        int dispatchesBeforeQueuedCommand = engine.ControlDispatchCount;
        Task<PlaybackEngineOperationResult> queued = coordinator.SetMutedAsync(
            playing.SessionId,
            isMuted: true).AsTask();

        engine.EmitFailure(playing.SessionId);
        engine.ReleaseVolume.TrySetResult();

        PlaybackEngineOperationResult holdingResult = await holding.WaitAsync(TimeSpan.FromSeconds(2));
        PlaybackEngineOperationResult queuedResult = await queued.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.AreEqual(DomainErrorCode.StreamInterrupted, holdingResult.Error?.Code);
        Assert.AreEqual(DomainErrorCode.StreamInterrupted, queuedResult.Error?.Code);
        Assert.AreEqual(dispatchesBeforeQueuedCommand, engine.ControlDispatchCount);
        CollectionAssert.AreEqual(new[] { playing.SessionId }, engine.StopSessions.ToArray());
        Assert.AreEqual(PlaybackState.Failed, coordinator.Current.State);
    }

    [TestMethod]
    public async Task TrackQueryAndSelectionStayBoundToReportedSession()
    {
        var engine = new ControlPlaybackEngine();
        await using var coordinator = new PlaybackSessionCoordinator(engine);
        PlaybackSessionSnapshot first = (await coordinator.StartAsync(
            SourceId.Generate(),
            ChannelId.Generate()))!;

        DomainResult<PlaybackTrackSnapshot> query = await coordinator.GetTracksAsync(first.SessionId);
        Assert.IsTrue(query.IsSuccess);
        PlaybackTrackId secondAudio = query.Value!.Tracks.Single(track =>
            track.Id.Kind == PlaybackTrackKind.Audio && track.Id.Ordinal == 2).Id;
        Assert.IsTrue((await coordinator.SelectTrackAsync(secondAudio)).IsSuccess);
        Assert.AreEqual(secondAudio, engine.SelectedTracks.Single());
        Assert.AreEqual(
            secondAudio,
            coordinator.CurrentTracks!.Tracks.Single(track =>
                track.Id.Kind == PlaybackTrackKind.Audio && track.IsSelected).Id);

        PlaybackSessionSnapshot second = (await coordinator.StartAsync(
            SourceId.Generate(),
            ChannelId.Generate()))!;
        PlaybackEngineOperationResult stale = await coordinator.SelectTrackAsync(secondAudio);
        Assert.AreEqual(DomainErrorCode.OperationCancelled, stale.Error?.Code);
        Assert.AreEqual(second.SessionId, coordinator.Current.SessionId);
        Assert.HasCount(1, engine.SelectedTracks);
    }

    [TestMethod]
    public async Task FixedTrackInventoryDoesNotClaimSelectionSupport()
    {
        var engine = new ControlPlaybackEngine { FixedTracksOnly = true };
        await using var coordinator = new PlaybackSessionCoordinator(engine);
        PlaybackSessionSnapshot playing = (await coordinator.StartAsync(
            SourceId.Generate(),
            ChannelId.Generate()))!;

        DomainResult<PlaybackTrackSnapshot> query = await coordinator.GetTracksAsync(playing.SessionId);
        Assert.IsTrue(query.IsSuccess);
        Assert.AreEqual(PlaybackTrackCapabilities.None, query.Value!.Capabilities);
        Assert.IsTrue(query.Value.Tracks[0].IsSelected);
        Assert.IsFalse(query.Value.Tracks[0].IsSelectable);

        PlaybackEngineOperationResult selection = await coordinator.SelectTrackAsync(
            query.Value.Tracks[0].Id);
        Assert.AreEqual(DomainErrorCode.DomainInvariantViolation, selection.Error?.Code);
        Assert.IsEmpty(engine.SelectedTracks);
        Assert.AreEqual(PlaybackState.Playing, coordinator.Current.State);
    }

    [TestMethod]
    public async Task TrackSelectionExceptionIsSanitizedAndNonterminal()
    {
        string sensitive = SecurityTestAssertions.CreateSensitiveValue("TRACK-NATIVE");
        var engine = new ControlPlaybackEngine
        {
            SelectionException = new InvalidOperationException(sensitive),
        };
        await using var coordinator = new PlaybackSessionCoordinator(engine);
        PlaybackSessionSnapshot playing = (await coordinator.StartAsync(
            SourceId.Generate(),
            ChannelId.Generate()))!;
        PlaybackTrackSnapshot tracks = (await coordinator.GetTracksAsync(playing.SessionId)).Value!;
        PlaybackTrackId candidate = tracks.Tracks.Single(track =>
            track.Id.Kind == PlaybackTrackKind.Audio && track.Id.Ordinal == 2).Id;

        PlaybackEngineOperationResult result = await coordinator.SelectTrackAsync(candidate);

        Assert.AreEqual(DomainErrorCode.PlaybackControlFailed, result.Error?.Code);
        Assert.AreEqual(PlaybackState.Playing, coordinator.Current.State);
        Assert.AreEqual(1, coordinator.CurrentTracks!.Tracks.Single(track =>
            track.Id.Kind == PlaybackTrackKind.Audio && track.IsSelected).Id.Ordinal);
        Assert.IsEmpty(engine.StopSessions);
        string observable = string.Join('|', result, JsonSerializer.Serialize(result));
        SecurityTestAssertions.DoesNotContainSensitive(observable, sensitive);
    }

    [TestMethod]
    public async Task WrongSessionTrackQueryIsRejectedAndNotPublished()
    {
        var engine = new ControlPlaybackEngine { ReturnMismatchedTrackSession = true };
        await using var coordinator = new PlaybackSessionCoordinator(engine);
        PlaybackSessionSnapshot playing = (await coordinator.StartAsync(
            SourceId.Generate(),
            ChannelId.Generate()))!;

        DomainResult<PlaybackTrackSnapshot> result = await coordinator.GetTracksAsync(playing.SessionId);

        Assert.AreEqual(DomainErrorCode.DomainInvariantViolation, result.Error?.Code);
        Assert.IsNull(coordinator.CurrentTracks);
        Assert.AreEqual(PlaybackState.Playing, coordinator.Current.State);
    }

    [TestMethod]
    public async Task StaleTrackQueryCannotPublishForReplacementSession()
    {
        var engine = new ControlPlaybackEngine { BlockTrackQuery = true };
        await using var coordinator = new PlaybackSessionCoordinator(engine);
        PlaybackSessionSnapshot first = (await coordinator.StartAsync(
            SourceId.Generate(),
            ChannelId.Generate()))!;
        Task<DomainResult<PlaybackTrackSnapshot>> query = coordinator
            .GetTracksAsync(first.SessionId)
            .AsTask();
        await engine.TrackQueryEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Task<PlaybackSessionSnapshot?> replacement = coordinator.StartAsync(
            SourceId.Generate(),
            ChannelId.Generate()).AsTask();

        await engine.TrackQueryCancelled.Task.WaitAsync(TimeSpan.FromSeconds(2));
        DomainResult<PlaybackTrackSnapshot> cancelled = await query.WaitAsync(TimeSpan.FromSeconds(2));
        PlaybackSessionSnapshot second = (await replacement.WaitAsync(TimeSpan.FromSeconds(2)))!;
        Assert.AreEqual(DomainErrorCode.OperationCancelled, cancelled.Error?.Code);
        Assert.AreEqual(second.SessionId, coordinator.Current.SessionId);
        Assert.IsNull(coordinator.CurrentTracks);
    }

    private static PlaybackSessionId Session(long value)
    {
        System.Reflection.MethodInfo factory = typeof(PlaybackSessionId).GetMethod(
            "FromSequence",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
        return (PlaybackSessionId)factory.Invoke(null, [value])!;
    }

    private sealed class ControlPlaybackEngine : IPlaybackEngine
    {
        private readonly object _sync = new();
        private PlaybackEngineSnapshot _current = PlaybackEngineSnapshot.Closed();
        private PlaybackControlSnapshot _controls = PlaybackControlSnapshot.Idle(
            PlaybackVolume.FromPercent(100),
            isMuted: false,
            PlaybackAspectMode.Fit);

        internal bool BlockVolume { get; set; }

        internal bool BlockTrackQuery { get; init; }

        internal bool FailControl { get; set; }

        internal bool ThrowUnrequestedControlCancellation { get; set; }

        internal bool EmitFailureDuringControl { get; set; }

        internal bool ResetControlsOnOpen { get; init; }

        internal bool FixedTracksOnly { get; init; }

        internal bool ReturnMismatchedTrackSession { get; init; }

        internal Exception? SelectionException { get; init; }

        internal TaskCompletionSource VolumeEntered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource VolumeCancelled { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource ReleaseVolume { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource TrackQueryEntered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource TrackQueryCancelled { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal List<PlaybackSessionId> ControlSessions { get; } = [];

        internal List<PlaybackSessionId> StopSessions { get; } = [];

        internal List<PlaybackTrackId> SelectedTracks { get; } = [];

        internal int ControlDispatchCount
        {
            get
            {
                lock (_sync)
                {
                    return ControlSessions.Count;
                }
            }
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

        public ValueTask<PlaybackEngineOperationResult> OpenAsync(
            PlaybackSessionId sessionId,
            PlaybackSelection selection,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_sync)
            {
                _current = PlaybackEngineSnapshot.Active(sessionId, PlaybackState.Buffering);
                _controls = PlaybackControlSnapshot.Active(
                    sessionId,
                    ResetControlsOnOpen ? PlaybackVolume.FromPercent(100) : _controls.Volume,
                    ResetControlsOnOpen ? false : _controls.IsMuted,
                    ResetControlsOnOpen ? PlaybackAspectMode.Fit : _controls.AspectMode);
            }

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
            lock (_sync)
            {
                StopSessions.Add(sessionId);
                _current = PlaybackEngineSnapshot.Closed();
                _controls = PlaybackControlSnapshot.Idle(
                    _controls.Volume,
                    _controls.IsMuted,
                    _controls.AspectMode);
            }

            return ValueTask.FromResult(PlaybackEngineOperationResult.Succeeded());
        }

        public async ValueTask<PlaybackEngineOperationResult> SetVolumeAsync(
            PlaybackSessionId sessionId,
            PlaybackVolume volume,
            CancellationToken cancellationToken = default)
        {
            bool shouldBlock;
            lock (_sync)
            {
                ControlSessions.Add(sessionId);
                shouldBlock = BlockVolume;
                BlockVolume = false;
            }

            if (shouldBlock)
            {
                VolumeEntered.TrySetResult();
                try
                {
                    await ReleaseVolume.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    VolumeCancelled.TrySetResult();
                    throw;
                }
            }

            if (FailControl)
            {
                return PlaybackEngineOperationResult.Failed(DomainErrorCode.PlaybackControlFailed);
            }

            if (ThrowUnrequestedControlCancellation)
            {
                throw new OperationCanceledException();
            }

            if (EmitFailureDuringControl)
            {
                Emit(PlaybackEngineSnapshot.Failed(
                    sessionId,
                    DomainError.Create(DomainErrorCode.StreamInterrupted)));
                return PlaybackEngineOperationResult.Succeeded();
            }

            lock (_sync)
            {
                _controls = PlaybackControlSnapshot.Active(
                    sessionId,
                    volume,
                    _controls.IsMuted,
                    _controls.AspectMode);
            }

            return PlaybackEngineOperationResult.Succeeded();
        }

        public ValueTask<PlaybackEngineOperationResult> SetMutedAsync(
            PlaybackSessionId sessionId,
            bool isMuted,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_sync)
            {
                ControlSessions.Add(sessionId);
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
            lock (_sync)
            {
                ControlSessions.Add(sessionId);
                _controls = PlaybackControlSnapshot.Active(
                    sessionId,
                    _controls.Volume,
                    _controls.IsMuted,
                    aspectMode);
            }

            return ValueTask.FromResult(PlaybackEngineOperationResult.Succeeded());
        }

        public async ValueTask<DomainResult<PlaybackTrackSnapshot>> GetTracksAsync(
            PlaybackSessionId sessionId,
            CancellationToken cancellationToken = default)
        {
            if (BlockTrackQuery)
            {
                TrackQueryEntered.TrySetResult();
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    TrackQueryCancelled.TrySetResult();
                    throw;
                }
            }

            PlaybackSessionId resultSession = ReturnMismatchedTrackSession
                ? Session(sessionId.Value + 1)
                : sessionId;
            return DomainResult.Success(CreateTracks(resultSession, FixedTracksOnly));
        }

        public ValueTask<PlaybackEngineOperationResult> SelectTrackAsync(
            PlaybackSessionId sessionId,
            PlaybackTrackId trackId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (SelectionException is not null)
            {
                throw SelectionException;
            }

            lock (_sync)
            {
                ControlSessions.Add(sessionId);
                SelectedTracks.Add(trackId);
            }

            return ValueTask.FromResult(PlaybackEngineOperationResult.Succeeded());
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        internal void EmitFailure(PlaybackSessionId sessionId) =>
            Emit(PlaybackEngineSnapshot.Failed(
                sessionId,
                DomainError.Create(DomainErrorCode.StreamInterrupted)));

        private void Emit(PlaybackEngineSnapshot snapshot)
        {
            lock (_sync)
            {
                _current = snapshot;
            }

            StateChanged?.Invoke(this, new PlaybackEngineStateChangedEventArgs(snapshot));
        }

        private static PlaybackTrackSnapshot CreateTracks(
            PlaybackSessionId sessionId,
            bool fixedTracksOnly) => PlaybackTrackSnapshot.Create(
                sessionId,
                fixedTracksOnly
                    ? PlaybackTrackCapabilities.None
                    : PlaybackTrackCapabilities.AudioSelection |
                        PlaybackTrackCapabilities.SubtitleSelection,
                fixedTracksOnly
                    ?
                    [
                        new PlaybackTrackInfo(
                            PlaybackTrackId.Create(sessionId, PlaybackTrackKind.Audio, 1),
                            isSelected: true,
                            isSelectable: false),
                    ]
                    :
                    [
                    new PlaybackTrackInfo(
                        PlaybackTrackId.Create(sessionId, PlaybackTrackKind.Audio, 1),
                        isSelected: true,
                        isSelectable: true),
                    new PlaybackTrackInfo(
                        PlaybackTrackId.Create(sessionId, PlaybackTrackKind.Audio, 2),
                        isSelected: false,
                        isSelectable: true),
                    new PlaybackTrackInfo(
                        PlaybackTrackId.Create(sessionId, PlaybackTrackKind.Subtitle, 1),
                        isSelected: false,
                        isSelectable: true),
                ]);
    }
}
