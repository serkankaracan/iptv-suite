using IptvSuite.Domain;

namespace IptvSuite.Application;

/// <summary>
/// Establishes the durable deletion boundary, drains exact-source playback, and completes
/// the pending lifecycle record in one serialized, retry-safe sequence.
/// </summary>
public sealed class SourceDeletionCoordinator : IAsyncDisposable
{
    private readonly ISourceDeletionLifecycle _lifecycle;
    private readonly PlaybackSessionCoordinator _playback;
    private readonly SemaphoreSlim _deletionGate = new(1, 1);
    private readonly object _sync = new();
    private Task? _disposeTask;
    private bool _disposed;

    public SourceDeletionCoordinator(
        ISourceDeletionLifecycle lifecycle,
        PlaybackSessionCoordinator playback)
    {
        _lifecycle = lifecycle ?? throw new ArgumentNullException(nameof(lifecycle));
        _playback = playback ?? throw new ArgumentNullException(nameof(playback));
    }

    public async ValueTask<SourceDeletionResult> DeleteAsync(
        SourceId sourceId,
        CancellationToken cancellationToken = default)
    {
        if (sourceId.IsEmpty)
        {
            throw new ArgumentException(
                "A source identifier is required for deletion.",
                nameof(sourceId));
        }

        cancellationToken.ThrowIfCancellationRequested();
        Task gateWait;
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            gateWait = _deletionGate.WaitAsync(cancellationToken);
        }

        bool gateEntered = false;
        try
        {
            await gateWait.ConfigureAwait(false);
            gateEntered = true;
            cancellationToken.ThrowIfCancellationRequested();
            using PlaybackSessionCoordinator.SourceRetirementLease retirement =
                _playback.AcquireSourceRetirement(sourceId);

            SourceDeletionLifecycleOperationResult marked =
                await MarkPendingAsync(sourceId, cancellationToken).ConfigureAwait(false);
            if (!marked.IsSuccess)
            {
                return SourceDeletionResult.Failed(
                    SourceDeletionFailureStage.MarkPending,
                    marked.Error!);
            }

            retirement.Commit();

            PlaybackEngineOperationResult released =
                await ReleasePlaybackAsync(sourceId).ConfigureAwait(false);
            if (!released.IsSuccess)
            {
                return SourceDeletionResult.Failed(
                    SourceDeletionFailureStage.PlaybackRelease,
                    released.Error!);
            }

            SourceDeletionLifecycleOperationResult completed =
                await CompletePendingAsync(sourceId).ConfigureAwait(false);
            return completed.IsSuccess
                ? SourceDeletionResult.Succeeded()
                : SourceDeletionResult.Failed(
                    SourceDeletionFailureStage.CompletePending,
                    completed.Error!);
        }
        finally
        {
            if (gateEntered)
            {
                _deletionGate.Release();
            }
        }
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
            _disposeTask = DisposeGateAsync();
            disposeTask = _disposeTask;
        }

        return new ValueTask(disposeTask);
    }

    private async ValueTask<SourceDeletionLifecycleOperationResult> MarkPendingAsync(
        SourceId sourceId,
        CancellationToken cancellationToken)
    {
        try
        {
            return await _lifecycle
                .MarkPendingAsync(sourceId, cancellationToken)
                .ConfigureAwait(false) ??
                SourceDeletionLifecycleOperationResult.Failed(
                    DomainErrorCode.StorageUnavailable);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellationToken);
        }
        catch (Exception)
        {
            return SourceDeletionLifecycleOperationResult.Failed(
                DomainErrorCode.StorageUnavailable);
        }
    }

    private async ValueTask<PlaybackEngineOperationResult> ReleasePlaybackAsync(SourceId sourceId)
    {
        try
        {
            return await _playback
                .ReleaseSourceAsync(sourceId, CancellationToken.None)
                .ConfigureAwait(false) ??
                PlaybackEngineOperationResult.Failed(
                    DomainErrorCode.PlaybackControlFailed);
        }
        catch (Exception)
        {
            return PlaybackEngineOperationResult.Failed(
                DomainErrorCode.PlaybackControlFailed);
        }
    }

    private async ValueTask<SourceDeletionLifecycleOperationResult> CompletePendingAsync(
        SourceId sourceId)
    {
        try
        {
            return await _lifecycle
                .CompletePendingAsync(sourceId, CancellationToken.None)
                .ConfigureAwait(false) ??
                SourceDeletionLifecycleOperationResult.Failed(
                    DomainErrorCode.StorageUnavailable);
        }
        catch (Exception)
        {
            return SourceDeletionLifecycleOperationResult.Failed(
                DomainErrorCode.StorageUnavailable);
        }
    }

    private async Task DisposeGateAsync()
    {
        await _deletionGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        _deletionGate.Dispose();
    }
}
