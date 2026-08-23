using System.Text.Json;
using IptvSuite.Application;
using IptvSuite.Domain;

namespace IptvSuite.UnitTests;

[TestClass]
public sealed class PlaybackSessionCoordinatorTests
{
    [TestMethod]
    public async Task StartAssignsStrictlyIncreasingSessionIdentifiers()
    {
        var engine = new ControlledPlaybackEngine();
        await using var coordinator = new PlaybackSessionCoordinator(engine);

        PlaybackSessionSnapshot? first = await coordinator.StartAsync(
            SourceId.Generate(),
            ChannelId.Generate());
        PlaybackSessionSnapshot? second = await coordinator.StartAsync(
            SourceId.Generate(),
            ChannelId.Generate());

        Assert.IsNotNull(first);
        Assert.IsNotNull(second);
        Assert.AreEqual(PlaybackState.Playing, first.State);
        Assert.AreEqual(PlaybackState.Playing, second.State);
        Assert.IsTrue(first.SessionId.Value > 0);
        Assert.IsTrue(second.SessionId.Value > first.SessionId.Value);
        CollectionAssert.AreEqual(
            new[]
            {
                $"Open:{first.SessionId.Value}",
                $"Play:{first.SessionId.Value}",
                $"Stop:{first.SessionId.Value}",
                $"Open:{second.SessionId.Value}",
                $"Play:{second.SessionId.Value}",
            },
            engine.Journal.ToArray());
    }

    [TestMethod]
    public async Task RapidSecondStartCancelsAndStopsTheFirstBeforeOpeningTheSecond()
    {
        var engine = new ControlledPlaybackEngine { BlockFirstOpen = true };
        await using var coordinator = new PlaybackSessionCoordinator(engine);
        Task<PlaybackSessionSnapshot?> first = coordinator.StartAsync(
            SourceId.Generate(),
            ChannelId.Generate()).AsTask();
        await engine.FirstOpenEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        PlaybackSessionId firstSession = engine.OpenSessions.Single();

        Task<PlaybackSessionSnapshot?> second = coordinator.StartAsync(
            SourceId.Generate(),
            ChannelId.Generate()).AsTask();

        await engine.FirstOpenCancelled.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.IsNull(await first.WaitAsync(TimeSpan.FromSeconds(2)));
        PlaybackSessionSnapshot? current = await second.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.IsNotNull(current);
        Assert.IsTrue(current.SessionId.Value > firstSession.Value);
        Assert.AreEqual(PlaybackState.Playing, current.State);
        Assert.IsTrue(
            engine.Journal.IndexOf($"Stop:{firstSession.Value}") <
            engine.Journal.IndexOf($"Open:{current.SessionId.Value}"));

        engine.Emit(PlaybackEngineSnapshot.Active(firstSession, PlaybackState.Playing));

        Assert.AreEqual(current, coordinator.Current);
    }

    [TestMethod]
    public async Task StopDuringOpenCancelsAndStopsTheExactSession()
    {
        var engine = new ControlledPlaybackEngine { BlockFirstOpen = true };
        await using var coordinator = new PlaybackSessionCoordinator(engine);
        Task<PlaybackSessionSnapshot?> start = coordinator.StartAsync(
            SourceId.Generate(),
            ChannelId.Generate()).AsTask();
        await engine.FirstOpenEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        PlaybackSessionId openingSession = engine.OpenSessions.Single();

        PlaybackEngineOperationResult stopped = await coordinator.StopAsync();

        Assert.IsTrue(stopped.IsSuccess);
        Assert.IsNull(await start.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.AreEqual(PlaybackState.Closed, coordinator.Current.State);
        CollectionAssert.AreEqual(new[] { openingSession }, engine.StopSessions.ToArray());

        engine.Emit(PlaybackEngineSnapshot.Active(openingSession, PlaybackState.Playing));

        Assert.AreEqual(PlaybackState.Closed, coordinator.Current.State);
    }

    [TestMethod]
    public async Task StopOfQueuedReplacementAlsoStopsThePhysicalPreviousSession()
    {
        var engine = new ControlledPlaybackEngine
        {
            BlockFirstOpen = true,
            HoldFirstOpenCancellation = true,
        };
        await using var coordinator = new PlaybackSessionCoordinator(engine);
        Task<PlaybackSessionSnapshot?> first = coordinator.StartAsync(
            SourceId.Generate(),
            ChannelId.Generate()).AsTask();
        await engine.FirstOpenEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        PlaybackSessionId physicalSession = engine.OpenSessions.Single();
        Task<PlaybackSessionSnapshot?> replacement = coordinator.StartAsync(
            SourceId.Generate(),
            ChannelId.Generate()).AsTask();
        await engine.FirstOpenCancelled.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Task<PlaybackEngineOperationResult> stop = coordinator.StopAsync().AsTask();
        engine.ReleaseFirstOpenCancellation.TrySetResult();

        Assert.IsTrue((await stop.WaitAsync(TimeSpan.FromSeconds(2))).IsSuccess);
        Assert.IsNull(await first.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.IsNull(await replacement.WaitAsync(TimeSpan.FromSeconds(2)));
        CollectionAssert.AreEqual(new[] { physicalSession }, engine.StopSessions.ToArray());
        Assert.AreEqual(PlaybackState.Closed, coordinator.Current.State);
    }

    [TestMethod]
    public async Task NewStartCancelsInFlightCommandBeforeReplacingSession()
    {
        var engine = new ControlledPlaybackEngine { BlockFirstPause = true };
        await using var coordinator = new PlaybackSessionCoordinator(engine);
        PlaybackSessionSnapshot first = (await coordinator.StartAsync(
            SourceId.Generate(),
            ChannelId.Generate()))!;
        Task<PlaybackEngineOperationResult> pause = coordinator.PauseAsync().AsTask();
        await engine.FirstPauseEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Task<PlaybackSessionSnapshot?> replacement = coordinator.StartAsync(
            SourceId.Generate(),
            ChannelId.Generate()).AsTask();

        await engine.FirstPauseCancelled.Task.WaitAsync(TimeSpan.FromSeconds(2));
        PlaybackEngineOperationResult cancelled = await pause.WaitAsync(TimeSpan.FromSeconds(2));
        PlaybackSessionSnapshot? second = await replacement.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.IsFalse(cancelled.IsSuccess);
        Assert.AreEqual(DomainErrorCode.OperationCancelled, cancelled.Error?.Code);
        Assert.IsNotNull(second);
        Assert.AreEqual(PlaybackState.Playing, second.State);
        Assert.IsTrue(
            engine.Journal.IndexOf($"Stop:{first.SessionId.Value}") <
            engine.Journal.IndexOf($"Open:{second.SessionId.Value}"));
    }

    [TestMethod]
    public async Task CallerCancellationDuringCommandRemainsCancellation()
    {
        var engine = new ControlledPlaybackEngine { BlockFirstPause = true };
        await using var coordinator = new PlaybackSessionCoordinator(engine);
        PlaybackSessionSnapshot playing = (await coordinator.StartAsync(
            SourceId.Generate(),
            ChannelId.Generate()))!;
        using var cancellation = new CancellationTokenSource();
        Task<PlaybackEngineOperationResult> pause = coordinator.PauseAsync(cancellation.Token).AsTask();
        await engine.FirstPauseEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));

        cancellation.Cancel();

        await Assert.ThrowsExactlyAsync<OperationCanceledException>(async () =>
            await pause.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.AreEqual(playing.SessionId, coordinator.Current.SessionId);
        Assert.AreEqual(PlaybackState.Playing, coordinator.Current.State);
    }

    [TestMethod]
    public async Task CallerCancellationRemainsCancellationAndClosesTheSession()
    {
        var engine = new ControlledPlaybackEngine { BlockFirstOpen = true };
        await using var coordinator = new PlaybackSessionCoordinator(engine);
        using var cancellation = new CancellationTokenSource();
        Task<PlaybackSessionSnapshot?> start = coordinator.StartAsync(
            SourceId.Generate(),
            ChannelId.Generate(),
            cancellation.Token).AsTask();
        await engine.FirstOpenEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        PlaybackSessionId openingSession = engine.OpenSessions.Single();

        cancellation.Cancel();

        await Assert.ThrowsExactlyAsync<OperationCanceledException>(async () =>
            await start.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.AreEqual(PlaybackState.Closed, coordinator.Current.State);
        CollectionAssert.AreEqual(new[] { openingSession }, engine.StopSessions.ToArray());
    }

    [TestMethod]
    public async Task WrongOrderAndStaleEventsCannotRegressCurrentState()
    {
        var engine = new ControlledPlaybackEngine();
        await using var coordinator = new PlaybackSessionCoordinator(engine);
        PlaybackSessionSnapshot first = (await coordinator.StartAsync(
            SourceId.Generate(),
            ChannelId.Generate()))!;
        PlaybackSessionSnapshot second = (await coordinator.StartAsync(
            SourceId.Generate(),
            ChannelId.Generate()))!;

        engine.Emit(PlaybackEngineSnapshot.Active(second.SessionId, PlaybackState.Opening));
        engine.Emit(PlaybackEngineSnapshot.Active(first.SessionId, PlaybackState.Paused));

        Assert.AreEqual(second.SessionId, coordinator.Current.SessionId);
        Assert.AreEqual(PlaybackState.Playing, coordinator.Current.State);
    }

    [TestMethod]
    public async Task EngineExceptionBecomesTypedFailureWithoutDiagnosticText()
    {
        string sensitive = SecurityTestAssertions.CreateSensitiveValue("PLAYBACK-NATIVE");
        var engine = new ControlledPlaybackEngine
        {
            OpenException = new InvalidOperationException(sensitive),
        };
        await using var coordinator = new PlaybackSessionCoordinator(engine);

        PlaybackSessionSnapshot? result = await coordinator.StartAsync(
            SourceId.Generate(),
            ChannelId.Generate());

        Assert.IsNotNull(result);
        Assert.AreEqual(PlaybackState.Failed, result.State);
        Assert.AreEqual(DomainErrorCode.PlaybackStartFailed, result.Error?.Code);
        CollectionAssert.AreEqual(new[] { result.SessionId }, engine.StopSessions.ToArray());
        string observable = string.Join('|', result, JsonSerializer.Serialize(result));
        SecurityTestAssertions.DoesNotContainSensitive(observable, sensitive);
        Assert.IsFalse(observable.Contains(nameof(Exception), StringComparison.Ordinal));
    }


    [TestMethod]
    public async Task UnrequestedEngineCancellationBecomesTypedFailureAndRollsBack()
    {
        var engine = new ControlledPlaybackEngine { ThrowUnrequestedOpenCancellation = true };
        await using var coordinator = new PlaybackSessionCoordinator(engine);

        PlaybackSessionSnapshot? result = await coordinator.StartAsync(
            SourceId.Generate(),
            ChannelId.Generate());

        Assert.IsNotNull(result);
        Assert.AreEqual(PlaybackState.Failed, result.State);
        Assert.AreEqual(DomainErrorCode.PlaybackStartFailed, result.Error?.Code);
        CollectionAssert.AreEqual(new[] { result.SessionId }, engine.StopSessions.ToArray());
    }

    [TestMethod]
    public async Task FailureCallbackDuringOpenPreventsPlayAndRollsBack()
    {
        var engine = new ControlledPlaybackEngine { FailDuringOpen = true };
        await using var coordinator = new PlaybackSessionCoordinator(engine);

        PlaybackSessionSnapshot? result = await coordinator.StartAsync(
            SourceId.Generate(),
            ChannelId.Generate());

        Assert.IsNotNull(result);
        Assert.AreEqual(PlaybackState.Failed, result.State);
        Assert.AreEqual(DomainErrorCode.PlaybackStartFailed, result.Error?.Code);
        Assert.IsFalse(engine.Journal.Any(entry => entry.StartsWith("Play:", StringComparison.Ordinal)));
        CollectionAssert.AreEqual(new[] { result.SessionId }, engine.StopSessions.ToArray());
    }

    [TestMethod]
    public async Task ConcurrentStopsInvokeEngineStopExactlyOnce()
    {
        var engine = new ControlledPlaybackEngine { BlockFirstStop = true };
        await using var coordinator = new PlaybackSessionCoordinator(engine);
        PlaybackSessionSnapshot playing = (await coordinator.StartAsync(
            SourceId.Generate(),
            ChannelId.Generate()))!;
        Task<PlaybackEngineOperationResult> first = coordinator.StopAsync().AsTask();
        await engine.FirstStopEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Task<PlaybackEngineOperationResult> second = coordinator.StopAsync().AsTask();
        engine.ReleaseFirstStop.TrySetResult();

        Assert.IsTrue((await first.WaitAsync(TimeSpan.FromSeconds(2))).IsSuccess);
        Assert.IsTrue((await second.WaitAsync(TimeSpan.FromSeconds(2))).IsSuccess);
        CollectionAssert.AreEqual(new[] { playing.SessionId }, engine.StopSessions.ToArray());
        Assert.AreEqual(PlaybackState.Closed, coordinator.Current.State);
    }

    [TestMethod]
    public async Task SessionBoundClosedCallbackCanCompleteStoppingState()
    {
        var engine = new ControlledPlaybackEngine { BlockFirstStop = true };
        await using var coordinator = new PlaybackSessionCoordinator(engine);
        PlaybackSessionSnapshot playing = (await coordinator.StartAsync(
            SourceId.Generate(),
            ChannelId.Generate()))!;
        Task<PlaybackEngineOperationResult> stop = coordinator.StopAsync().AsTask();
        await engine.FirstStopEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));

        engine.Emit(PlaybackEngineSnapshot.Closed(playing.SessionId));

        Assert.AreEqual(PlaybackState.Closed, coordinator.Current.State);
        engine.ReleaseFirstStop.TrySetResult();
        Assert.IsTrue((await stop.WaitAsync(TimeSpan.FromSeconds(2))).IsSuccess);
    }

    [TestMethod]
    public async Task DisposeDuringOpenStopsAndDisposesExactlyOnce()
    {
        var engine = new ControlledPlaybackEngine { BlockFirstOpen = true };
        var coordinator = new PlaybackSessionCoordinator(engine);
        Task<PlaybackSessionSnapshot?> start = coordinator.StartAsync(
            SourceId.Generate(),
            ChannelId.Generate()).AsTask();
        await engine.FirstOpenEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        PlaybackSessionId openingSession = engine.OpenSessions.Single();

        await coordinator.DisposeAsync();
        await coordinator.DisposeAsync();

        Assert.IsNull(await start.WaitAsync(TimeSpan.FromSeconds(2)));
        CollectionAssert.AreEqual(new[] { openingSession }, engine.StopSessions.ToArray());
        Assert.AreEqual(1, engine.DisposeCount);
        await Assert.ThrowsExactlyAsync<ObjectDisposedException>(async () =>
            await coordinator.PlayAsync());
    }

    private sealed class ControlledPlaybackEngine : IPlaybackEngine
    {
        private readonly object _sync = new();
        private PlaybackEngineSnapshot _current = PlaybackEngineSnapshot.Closed();
        private int _openCount;
        private int _disposeCount;

        internal bool BlockFirstOpen { get; init; }

        internal bool BlockFirstPause { get; init; }

        internal bool BlockFirstStop { get; init; }

        internal bool HoldFirstOpenCancellation { get; init; }

        internal bool ThrowUnrequestedOpenCancellation { get; init; }

        internal bool FailDuringOpen { get; init; }

        internal Exception? OpenException { get; init; }

        internal TaskCompletionSource FirstOpenEntered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource FirstOpenCancelled { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource ReleaseFirstOpenCancellation { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource FirstPauseEntered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource FirstPauseCancelled { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource FirstStopEntered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource ReleaseFirstStop { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal List<string> Journal { get; } = [];

        internal List<PlaybackSessionId> OpenSessions { get; } = [];

        internal List<PlaybackSessionId> StopSessions { get; } = [];

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

        internal int DisposeCount => Volatile.Read(ref _disposeCount);

        public async ValueTask<PlaybackEngineOperationResult> OpenAsync(
            PlaybackSessionId sessionId,
            PlaybackSelection selection,
            CancellationToken cancellationToken = default)
        {
            int ordinal;
            lock (_sync)
            {
                Journal.Add($"Open:{sessionId.Value}");
                OpenSessions.Add(sessionId);
                ordinal = ++_openCount;
                _current = PlaybackEngineSnapshot.Active(sessionId, PlaybackState.Opening);
            }

            if (ordinal == 1 && BlockFirstOpen)
            {
                FirstOpenEntered.TrySetResult();
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    FirstOpenCancelled.TrySetResult();
                    if (HoldFirstOpenCancellation)
                    {
                        await ReleaseFirstOpenCancellation.Task.ConfigureAwait(false);
                    }

                    throw;
                }
            }

            if (ThrowUnrequestedOpenCancellation)
            {
                throw new OperationCanceledException();
            }

            if (OpenException is not null)
            {
                throw OpenException;
            }

            if (FailDuringOpen)
            {
                Emit(PlaybackEngineSnapshot.Failed(
                    sessionId,
                    DomainError.Create(DomainErrorCode.PlaybackStartFailed)));
                return PlaybackEngineOperationResult.Succeeded();
            }

            Emit(PlaybackEngineSnapshot.Active(sessionId, PlaybackState.Buffering));
            return PlaybackEngineOperationResult.Succeeded();
        }

        public ValueTask<PlaybackEngineOperationResult> PlayAsync(
            PlaybackSessionId sessionId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_sync)
            {
                Journal.Add($"Play:{sessionId.Value}");
            }

            Emit(PlaybackEngineSnapshot.Active(sessionId, PlaybackState.Playing));
            return ValueTask.FromResult(PlaybackEngineOperationResult.Succeeded());
        }

        public async ValueTask<PlaybackEngineOperationResult> PauseAsync(
            PlaybackSessionId sessionId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_sync)
            {
                Journal.Add($"Pause:{sessionId.Value}");
            }

            if (BlockFirstPause)
            {
                FirstPauseEntered.TrySetResult();
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    FirstPauseCancelled.TrySetResult();
                    throw;
                }
            }

            Emit(PlaybackEngineSnapshot.Active(sessionId, PlaybackState.Paused));
            return PlaybackEngineOperationResult.Succeeded();
        }

        public async ValueTask<PlaybackEngineOperationResult> StopAsync(
            PlaybackSessionId sessionId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_sync)
            {
                Journal.Add($"Stop:{sessionId.Value}");
                StopSessions.Add(sessionId);
                _current = PlaybackEngineSnapshot.Closed();
            }

            if (BlockFirstStop && StopSessions.Count == 1)
            {
                FirstStopEntered.TrySetResult();
                await ReleaseFirstStop.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            }

            return PlaybackEngineOperationResult.Succeeded();
        }

        public ValueTask DisposeAsync()
        {
            Interlocked.Increment(ref _disposeCount);
            return ValueTask.CompletedTask;
        }

        internal void Emit(PlaybackEngineSnapshot snapshot)
        {
            lock (_sync)
            {
                _current = snapshot;
            }

            StateChanged?.Invoke(this, new PlaybackEngineStateChangedEventArgs(snapshot));
        }
    }
}
