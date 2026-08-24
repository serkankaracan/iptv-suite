namespace IptvSuite.Application;

public sealed class PlaybackPowerLifecycleCoordinator : IAsyncDisposable
{
    private readonly object _sync = new();
    private readonly Func<CancellationToken, ValueTask<PlaybackEngineOperationResult>>
        _stopPlayback;
    private Task<PlaybackEngineOperationResult>? _pendingStop;
    private Task? _disposeTask;
    private bool _disposed;

    public PlaybackPowerLifecycleCoordinator(
        Func<CancellationToken, ValueTask<PlaybackEngineOperationResult>> stopPlayback)
    {
        _stopPlayback = stopPlayback ?? throw new ArgumentNullException(nameof(stopPlayback));
    }

    public ValueTask<PlaybackEngineOperationResult> StopForSuspendAsync()
    {
        Task<PlaybackEngineOperationResult> stopTask;
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_pendingStop is null || _pendingStop.IsCompleted)
            {
                _pendingStop = CompleteStopAsync();
            }

            stopTask = _pendingStop;
        }

        return new ValueTask<PlaybackEngineOperationResult>(stopTask);
    }

    public ValueTask DisposeAsync()
    {
        Task disposeTask;
        lock (_sync)
        {
            if (_disposeTask is not null)
            {
                return new ValueTask(_disposeTask);
            }

            _disposed = true;
            _disposeTask = _pendingStop ?? Task.CompletedTask;
            disposeTask = _disposeTask;
        }

        return new ValueTask(disposeTask);
    }

    private async Task<PlaybackEngineOperationResult> CompleteStopAsync() =>
        await _stopPlayback(CancellationToken.None).ConfigureAwait(false);
}
