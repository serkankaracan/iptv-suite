using System.Collections.Concurrent;
using System.Reflection;
using IptvSuite.Application;
using IptvSuite.Domain;
using IptvSuite.Testing;
using Microsoft.Extensions.Time.Testing;

namespace IptvSuite.UnitTests;

[TestClass]
public sealed class PlaybackFaultWatchdogTests
{
    private static readonly DateTimeOffset Start = DateTimeOffset.Parse(
        "2026-08-25T00:00:00Z",
        System.Globalization.CultureInfo.InvariantCulture);
    private static readonly TimeSpan StartupTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan RebufferTimeout = TimeSpan.FromSeconds(5);

    [TestMethod]
    public void OptionsRequireExplicitPositiveDeadlines()
    {
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            new PlaybackFaultWatchdogOptions(TimeSpan.Zero, RebufferTimeout));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            new PlaybackFaultWatchdogOptions(StartupTimeout, Timeout.InfiniteTimeSpan));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            new PlaybackFaultWatchdogOptions(
                PlaybackFaultWatchdogOptions.MinimumSupportedTimeout - TimeSpan.FromTicks(1),
                RebufferTimeout));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            new PlaybackFaultWatchdogOptions(
                StartupTimeout,
                PlaybackFaultWatchdogOptions.MaximumSupportedTimeout + TimeSpan.FromTicks(1)));

        var options = new PlaybackFaultWatchdogOptions(
            StartupTimeout,
            RebufferTimeout);

        Assert.AreEqual(StartupTimeout, options.StartupTimeout);
        Assert.AreEqual(RebufferTimeout, options.RebufferTimeout);
    }

    [TestMethod]
    public void EarlyTimerCallbackRearmsAgainstMonotonicExactDeadline()
    {
        using var time = new ManualTimeProvider();
        using var watchdog = Create(time);
        int expired = 0;
        watchdog.Expired += (_, _) => Interlocked.Increment(ref expired);
        watchdog.Observe(Active(Session(1), PlaybackState.Opening));

        time.FireEvenIfDisposed(timerOrdinal: 0);
        Assert.IsTrue(
            SpinWait.SpinUntil(() => time.TimerCount >= 2, TimeSpan.FromSeconds(2)),
            "The early callback did not rearm the remaining monotonic deadline.");
        Assert.AreEqual(0, Volatile.Read(ref expired));

        time.Advance(StartupTimeout - TimeSpan.FromMilliseconds(1));
        time.FireEvenIfDisposed(timerOrdinal: 1);
        Assert.IsTrue(
            SpinWait.SpinUntil(() => time.TimerCount >= 3, TimeSpan.FromSeconds(2)),
            "The callback before the exact boundary did not rearm.");
        Assert.AreEqual(0, Volatile.Read(ref expired));

        time.Advance(TimeSpan.FromMilliseconds(1));
        time.FireEvenIfDisposed(timerOrdinal: 2);
        Assert.IsTrue(
            SpinWait.SpinUntil(() => Volatile.Read(ref expired) == 1, TimeSpan.FromSeconds(2)),
            "The exact monotonic deadline did not publish its terminal event.");
    }

    [TestMethod]
    public void RepeatedOpeningAndStartupBufferingDoNotResetExactDeadline()
    {
        FakeTimeProvider time = TestTime.Create(Start);
        using var watchdog = Create(time);
        PlaybackSessionId session = Session(1);
        var expired = new ConcurrentQueue<PlaybackFaultWatchdogExpiredEventArgs>();
        watchdog.Expired += (_, args) => expired.Enqueue(args);

        watchdog.Observe(Active(session, PlaybackState.Opening));
        time.Advance(TimeSpan.FromSeconds(6));
        watchdog.Observe(Active(session, PlaybackState.Opening));
        watchdog.Observe(Active(session, PlaybackState.Buffering));
        time.Advance(TimeSpan.FromMilliseconds(3999));

        Assert.AreEqual(0, expired.Count);

        time.Advance(TimeSpan.FromMilliseconds(1));
        time.Advance(StartupTimeout);

        Assert.AreEqual(1, expired.Count);
        Assert.IsTrue(expired.TryDequeue(out PlaybackFaultWatchdogExpiredEventArgs? observed));
        Assert.AreEqual(session, observed!.SessionId);
        Assert.AreEqual(
            PlaybackFaultWatchdogFailureKind.StartupTimeout,
            observed.FailureKind);
        Assert.AreEqual(DomainErrorCode.PlaybackStartFailed, observed.Error.Code);
    }

    [TestMethod]
    public void PlayableClosesStartupAndSustainedRebufferUsesIndependentDeadline()
    {
        FakeTimeProvider time = TestTime.Create(Start);
        using var watchdog = Create(time);
        PlaybackSessionId session = Session(1);
        var expired = new ConcurrentQueue<PlaybackFaultWatchdogExpiredEventArgs>();
        watchdog.Expired += (_, args) => expired.Enqueue(args);

        watchdog.Observe(Active(session, PlaybackState.Opening));
        time.Advance(TimeSpan.FromSeconds(9));
        watchdog.Observe(Active(session, PlaybackState.Playing));
        time.Advance(StartupTimeout);
        Assert.AreEqual(0, expired.Count);

        watchdog.Observe(Active(session, PlaybackState.Buffering));
        time.Advance(TimeSpan.FromSeconds(3));
        watchdog.Observe(Active(session, PlaybackState.Buffering));
        time.Advance(TimeSpan.FromMilliseconds(1999));
        Assert.AreEqual(0, expired.Count);

        time.Advance(TimeSpan.FromMilliseconds(1));

        Assert.AreEqual(1, expired.Count);
        Assert.IsTrue(expired.TryDequeue(out PlaybackFaultWatchdogExpiredEventArgs? observed));
        Assert.AreEqual(
            PlaybackFaultWatchdogFailureKind.RebufferTimeout,
            observed!.FailureKind);
        Assert.AreEqual(DomainErrorCode.StreamInterrupted, observed.Error.Code);
    }

    [TestMethod]
    public void PlayingOrPausedCancelsRebufferUntilALaterBufferingTransition()
    {
        FakeTimeProvider time = TestTime.Create(Start);
        using var watchdog = Create(time);
        PlaybackSessionId session = Session(1);
        int expired = 0;
        watchdog.Expired += (_, _) => expired++;

        watchdog.Observe(Active(session, PlaybackState.Playing));
        watchdog.Observe(Active(session, PlaybackState.Buffering));
        time.Advance(TimeSpan.FromSeconds(4));
        watchdog.Observe(Active(session, PlaybackState.Paused));
        time.Advance(RebufferTimeout);
        Assert.AreEqual(0, expired);

        watchdog.Observe(Active(session, PlaybackState.Buffering));
        time.Advance(RebufferTimeout);

        Assert.AreEqual(1, expired);
    }

    [TestMethod]
    public void ReplacementGenerationRejectsNonCooperativeStaleCallback()
    {
        using var time = new ManualTimeProvider();
        using var watchdog = Create(time);
        PlaybackSessionId first = Session(1);
        PlaybackSessionId second = Session(2);
        var expired = new ConcurrentQueue<PlaybackFaultWatchdogExpiredEventArgs>();
        watchdog.Expired += (_, args) => expired.Enqueue(args);

        watchdog.Observe(Active(first, PlaybackState.Opening));
        watchdog.Observe(Active(second, PlaybackState.Opening));
        time.Advance(StartupTimeout);
        time.FireEvenIfDisposed(timerOrdinal: 0);

        Assert.AreEqual(0, expired.Count);

        time.FireEvenIfDisposed(timerOrdinal: 1);
        Assert.IsTrue(
            SpinWait.SpinUntil(() => expired.Count == 1, TimeSpan.FromSeconds(2)),
            "The replacement generation deadline was not published.");

        Assert.AreEqual(1, expired.Count);
        Assert.IsTrue(expired.TryDequeue(out PlaybackFaultWatchdogExpiredEventArgs? observed));
        Assert.AreEqual(second, observed!.SessionId);
    }

    [TestMethod]
    public void CancelAndDisposeMakeNonCooperativeCallbacksNoOps()
    {
        using var cancelTime = new ManualTimeProvider();
        using var cancelled = Create(cancelTime);
        PlaybackSessionId session = Session(1);
        int cancelledEvents = 0;
        cancelled.Expired += (_, _) => cancelledEvents++;
        cancelled.Observe(Active(session, PlaybackState.Opening));

        Assert.IsTrue(cancelled.Cancel(session));
        Assert.IsFalse(cancelled.Cancel(Session(2)));
        cancelTime.Advance(StartupTimeout);
        cancelTime.FireEvenIfDisposed(timerOrdinal: 0);
        Assert.AreEqual(0, cancelledEvents);

        using var disposeTime = new ManualTimeProvider();
        var disposed = Create(disposeTime);
        int disposedEvents = 0;
        disposed.Expired += (_, _) => disposedEvents++;
        disposed.Observe(Active(session, PlaybackState.Opening));
        disposed.Dispose();
        disposeTime.Advance(StartupTimeout);
        disposeTime.FireEvenIfDisposed(timerOrdinal: 0);

        Assert.AreEqual(0, disposedEvents);
        Assert.ThrowsExactly<ObjectDisposedException>(() =>
            disposed.Observe(Active(session, PlaybackState.Opening)));
    }

    [TestMethod]
    public void ExactDeadlinePublishesOneTerminalEventDespiteObserverFailure()
    {
        using var time = new ManualTimeProvider();
        using var watchdog = Create(time);
        int throwingObserverCalls = 0;
        int laterObserverCalls = 0;
        watchdog.Expired += (_, _) =>
        {
            throwingObserverCalls++;
            throw new InvalidOperationException("Synthetic observer failure.");
        };
        watchdog.Expired += (_, _) => laterObserverCalls++;
        watchdog.Observe(Active(Session(1), PlaybackState.Opening));

        time.Advance(StartupTimeout);
        time.FireEvenIfDisposed(timerOrdinal: 0);
        time.FireEvenIfDisposed(timerOrdinal: 0);
        Assert.IsTrue(
            SpinWait.SpinUntil(
                () => Volatile.Read(ref laterObserverCalls) == 1,
                TimeSpan.FromSeconds(2)),
            "The immutable terminal observer set was not published.");

        Assert.AreEqual(1, throwingObserverCalls);
        Assert.AreEqual(1, laterObserverCalls);
    }

    [TestMethod]
    public void EmptyClosedSnapshotCannotInvalidateAnActiveGeneration()
    {
        using var time = new ManualTimeProvider();
        using var watchdog = Create(time);
        PlaybackSessionId session = Session(1);
        var expired = new ConcurrentQueue<PlaybackFaultWatchdogExpiredEventArgs>();
        watchdog.Expired += (_, args) => expired.Enqueue(args);

        watchdog.Observe(Active(session, PlaybackState.Opening));
        watchdog.Observe(PlaybackEngineSnapshot.Closed());
        time.Advance(StartupTimeout);
        time.FireEvenIfDisposed(timerOrdinal: 0);

        Assert.IsTrue(
            SpinWait.SpinUntil(() => !expired.IsEmpty, TimeSpan.FromSeconds(2)),
            "The uncorrelated closed snapshot invalidated the active deadline.");
        Assert.IsTrue(expired.TryDequeue(out PlaybackFaultWatchdogExpiredEventArgs? observed));
        Assert.AreEqual(session, observed!.SessionId);
    }

    [TestMethod]
    public void ReentrantReplacementDoesNotChangeTheImmutableTerminalObserverSet()
    {
        using var time = new ManualTimeProvider();
        using var watchdog = Create(time);
        PlaybackSessionId first = Session(1);
        PlaybackSessionId replacement = Session(2);
        var firstObserverSessions = new ConcurrentQueue<PlaybackSessionId>();
        var secondObserverSessions = new ConcurrentQueue<PlaybackSessionId>();
        watchdog.Expired += (_, args) =>
        {
            firstObserverSessions.Enqueue(args.SessionId);
            watchdog.Observe(Active(replacement, PlaybackState.Opening));
        };
        watchdog.Expired += (_, args) => secondObserverSessions.Enqueue(args.SessionId);

        watchdog.Observe(Active(first, PlaybackState.Opening));
        time.Advance(StartupTimeout);
        time.FireEvenIfDisposed(timerOrdinal: 0);

        Assert.IsTrue(
            SpinWait.SpinUntil(() => time.TimerCount >= 2, TimeSpan.FromSeconds(2)),
            "The reentrant replacement did not establish its own deadline.");
        CollectionAssert.AreEqual(
            new[] { first },
            firstObserverSessions.ToArray());
        CollectionAssert.AreEqual(
            new[] { first },
            secondObserverSessions.ToArray());

        time.Advance(StartupTimeout);
        time.FireEvenIfDisposed(timerOrdinal: 1);
        Assert.IsTrue(
            SpinWait.SpinUntil(
                () => secondObserverSessions.Count == 2,
                TimeSpan.FromSeconds(2)),
            "The replacement generation did not publish its own deadline.");
        CollectionAssert.AreEqual(
            new[] { first, replacement },
            secondObserverSessions.ToArray());
    }

    [TestMethod]
    public void TimerCancellationRunsOutsideOwnerLockAndPreservesReentrantReplacement()
    {
        using var time = new ManualTimeProvider();
        using var watchdog = Create(time);
        PlaybackSessionId first = Session(1);
        PlaybackSessionId replacement = Session(2);
        int disposeReentry = 0;

        watchdog.Observe(Active(first, PlaybackState.Opening));
        time.RunOnceWhenTimerDisposes(() =>
        {
            Interlocked.Increment(ref disposeReentry);
            watchdog.Observe(Active(replacement, PlaybackState.Opening));
        });

        Assert.IsTrue(watchdog.Cancel(first));
        Assert.AreEqual(1, Volatile.Read(ref disposeReentry));
        Assert.IsTrue(
            SpinWait.SpinUntil(() => time.TimerCount >= 2, TimeSpan.FromSeconds(2)),
            "The timer-dispose reentry lost the replacement generation.");

        var expired = new ConcurrentQueue<PlaybackFaultWatchdogExpiredEventArgs>();
        watchdog.Expired += (_, args) => expired.Enqueue(args);
        time.Advance(StartupTimeout);
        time.FireEvenIfDisposed(timerOrdinal: 1);
        Assert.IsTrue(
            SpinWait.SpinUntil(() => !expired.IsEmpty, TimeSpan.FromSeconds(2)),
            "The reentrant replacement deadline was not retained.");
        Assert.IsTrue(expired.TryDequeue(out PlaybackFaultWatchdogExpiredEventArgs? observed));
        Assert.AreEqual(replacement, observed!.SessionId);
    }

    private static PlaybackFaultWatchdog Create(TimeProvider timeProvider) =>
        new(
            new PlaybackFaultWatchdogOptions(StartupTimeout, RebufferTimeout),
            timeProvider);

    private static PlaybackSessionId Session(long value)
    {
        MethodInfo factory = typeof(PlaybackSessionId).GetMethod(
            "FromSequence",
            BindingFlags.Static | BindingFlags.NonPublic)!;
        return (PlaybackSessionId)factory.Invoke(null, [value])!;
    }

    private static PlaybackEngineSnapshot Active(
        PlaybackSessionId sessionId,
        PlaybackState state) => PlaybackEngineSnapshot.Active(sessionId, state);

    private sealed class ManualTimeProvider : TimeProvider, IDisposable
    {
        private readonly object _sync = new();
        private readonly List<ManualTimer> _timers = [];
        private long _timestamp;
        private Action? _nextTimerDispose;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => Interlocked.Read(ref _timestamp);

        internal int TimerCount
        {
            get
            {
                lock (_sync)
                {
                    return _timers.Count;
                }
            }
        }

        public override ITimer CreateTimer(
            TimerCallback callback,
            object? state,
            TimeSpan dueTime,
            TimeSpan period)
        {
            ArgumentNullException.ThrowIfNull(callback);
            var timer = new ManualTimer(this, callback, state);
            lock (_sync)
            {
                _timers.Add(timer);
            }

            return timer;
        }

        internal void Advance(TimeSpan duration) =>
            Interlocked.Add(ref _timestamp, duration.Ticks);

        internal void FireEvenIfDisposed(int timerOrdinal)
        {
            ManualTimer timer;
            lock (_sync)
            {
                timer = _timers[timerOrdinal];
            }

            timer.FireEvenIfDisposed();
        }

        internal void RunOnceWhenTimerDisposes(Action callback)
        {
            ArgumentNullException.ThrowIfNull(callback);
            lock (_sync)
            {
                if (_nextTimerDispose is not null)
                {
                    throw new InvalidOperationException("A timer-dispose callback is already armed.");
                }

                _nextTimerDispose = callback;
            }
        }

        internal void OnTimerDisposed()
        {
            Action? callback;
            lock (_sync)
            {
                callback = _nextTimerDispose;
                _nextTimerDispose = null;
            }

            callback?.Invoke();
        }

        public void Dispose()
        {
            ManualTimer[] timers;
            lock (_sync)
            {
                timers = [.. _timers];
            }

            foreach (ManualTimer timer in timers)
            {
                timer.Dispose();
            }
        }
    }

    private sealed class ManualTimer(
        ManualTimeProvider owner,
        TimerCallback callback,
        object? state) : ITimer
    {
        private int _disposed;

        public bool Change(TimeSpan dueTime, TimeSpan period) =>
            Volatile.Read(ref _disposed) == 0;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                owner.OnTimerDisposed();
            }
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }

        internal void FireEvenIfDisposed() => callback.Invoke(state);
    }
}
