using IptvSuite.Application;
using IptvSuite.Domain;

namespace IptvSuite.UnitTests;

[TestClass]
public sealed class PlaybackPowerLifecycleCoordinatorTests
{
    [TestMethod]
    public async Task SuspendEnteringStopsPlaybackWithoutCallerCancellation()
    {
        CancellationToken observedToken = new(canceled: true);
        int stopCount = 0;
        await using var coordinator = new PlaybackPowerLifecycleCoordinator(token =>
        {
            stopCount++;
            observedToken = token;
            return ValueTask.FromResult(PlaybackEngineOperationResult.Succeeded());
        });

        PlaybackEngineOperationResult result = await coordinator.StopForSuspendAsync();

        Assert.IsTrue(result.IsSuccess);
        Assert.AreEqual(1, stopCount);
        Assert.IsFalse(observedToken.CanBeCanceled);
    }

    [TestMethod]
    public async Task ConcurrentSuspendNotificationsShareOnePendingStop()
    {
        var stopEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseStop = new TaskCompletionSource<PlaybackEngineOperationResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        int stopCount = 0;
        await using var coordinator = new PlaybackPowerLifecycleCoordinator(async _ =>
        {
            stopCount++;
            stopEntered.TrySetResult();
            return await releaseStop.Task.ConfigureAwait(false);
        });

        Task<PlaybackEngineOperationResult> first = coordinator.StopForSuspendAsync().AsTask();
        await stopEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Task<PlaybackEngineOperationResult> second = coordinator.StopForSuspendAsync().AsTask();

        Assert.AreEqual(1, stopCount);
        Assert.AreSame(first, second);
        releaseStop.TrySetResult(PlaybackEngineOperationResult.Succeeded());
        Assert.IsTrue((await first.WaitAsync(TimeSpan.FromSeconds(2))).IsSuccess);
        Assert.IsTrue((await second.WaitAsync(TimeSpan.FromSeconds(2))).IsSuccess);
    }

    [TestMethod]
    public async Task LaterSuspendAfterCompletedStopStartsAnewStop()
    {
        int stopCount = 0;
        await using var coordinator = new PlaybackPowerLifecycleCoordinator(_ =>
        {
            stopCount++;
            return ValueTask.FromResult(PlaybackEngineOperationResult.Succeeded());
        });

        await coordinator.StopForSuspendAsync();
        await coordinator.StopForSuspendAsync();

        Assert.AreEqual(2, stopCount);
    }

    [TestMethod]
    public async Task DisposeDrainsPendingStopAndRejectsLaterNotifications()
    {
        var stopEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseStop = new TaskCompletionSource<PlaybackEngineOperationResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var coordinator = new PlaybackPowerLifecycleCoordinator(async _ =>
        {
            stopEntered.TrySetResult();
            return await releaseStop.Task.ConfigureAwait(false);
        });
        Task<PlaybackEngineOperationResult> stop = coordinator.StopForSuspendAsync().AsTask();
        await stopEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Task dispose = coordinator.DisposeAsync().AsTask();

        Assert.IsFalse(dispose.IsCompleted);
        releaseStop.TrySetResult(PlaybackEngineOperationResult.Succeeded());
        await dispose.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.IsTrue((await stop.WaitAsync(TimeSpan.FromSeconds(2))).IsSuccess);
        await Assert.ThrowsExactlyAsync<ObjectDisposedException>(async () =>
            await coordinator.StopForSuspendAsync());
    }

    [TestMethod]
    public async Task FailedStopResultRemainsTypedAndSanitized()
    {
        await using var coordinator = new PlaybackPowerLifecycleCoordinator(_ =>
            ValueTask.FromResult(PlaybackEngineOperationResult.Failed(
                DomainErrorCode.StreamInterrupted)));

        PlaybackEngineOperationResult result = await coordinator.StopForSuspendAsync();

        Assert.IsFalse(result.IsSuccess);
        Assert.AreEqual(DomainErrorCode.StreamInterrupted, result.Error?.Code);
    }
}
