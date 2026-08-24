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

    /// <summary>
    /// Resumes actionable durable deletion records in stable, bounded keyset pages.
    /// </summary>
    public async ValueTask<SourceDeletionReconciliationResult> ReconcilePendingAsync(
        CancellationToken cancellationToken = default)
    {
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

            int attemptedCount = 0;
            int completedCount = 0;
            int failedCount = 0;
            SourceDeletionFailureStage firstFailureStage = SourceDeletionFailureStage.None;
            DomainError? firstError = null;
            SourceDeletionPendingCursorReadResult cursor = await ReadPendingCursorAsync(
                cancellationToken).ConfigureAwait(false);
            if (!cursor.IsSuccess)
            {
                return SourceDeletionReconciliationResult.Create(
                    attemptedCount,
                    completedCount,
                    failedCount,
                    hasRemaining: true,
                    SourceDeletionFailureStage.PendingDiscovery,
                    cursor.Error!);
            }

            SourceId? cycleBoundary = cursor.AfterExclusive;
            SourceId? afterExclusive = cycleBoundary;
            bool wrapped = !cycleBoundary.HasValue;

            while (attemptedCount < SourceDeletionReconciliationResult.MaximumAttemptCount)
            {
                cancellationToken.ThrowIfCancellationRequested();
                SourceDeletionPendingBatchReadResult batch = await ReadPendingBatchAsync(
                    afterExclusive,
                    cancellationToken).ConfigureAwait(false);
                if (!batch.IsSuccess)
                {
                    if (firstError is null)
                    {
                        firstFailureStage = SourceDeletionFailureStage.PendingDiscovery;
                        firstError = batch.Error!;
                    }

                    return SourceDeletionReconciliationResult.Create(
                        attemptedCount,
                        completedCount,
                        failedCount,
                        hasRemaining: true,
                        firstFailureStage,
                        firstError);
                }

                if (batch.SourceIds.Count == 0)
                {
                    if (!wrapped)
                    {
                        afterExclusive = null;
                        wrapped = true;
                        continue;
                    }

                    return SourceDeletionReconciliationResult.Create(
                        attemptedCount,
                        completedCount,
                        failedCount,
                        hasRemaining: failedCount > 0,
                        firstFailureStage,
                        firstError);
                }

                for (int index = 0; index < batch.SourceIds.Count; index++)
                {
                    SourceId sourceId = batch.SourceIds[index];
                    if (wrapped && cycleBoundary.HasValue &&
                        CompareSourceIds(sourceId, cycleBoundary.Value) > 0)
                    {
                        return SourceDeletionReconciliationResult.Create(
                            attemptedCount,
                            completedCount,
                            failedCount,
                            hasRemaining: failedCount > 0,
                            firstFailureStage,
                            firstError);
                    }

                    cancellationToken.ThrowIfCancellationRequested();
                    SourceDeletionLifecycleOperationResult advanced =
                        await AdvancePendingCursorAsync(sourceId).ConfigureAwait(false);
                    if (!advanced.IsSuccess)
                    {
                        if (firstError is null)
                        {
                            firstFailureStage = SourceDeletionFailureStage.PendingDiscovery;
                            firstError = advanced.Error!;
                        }

                        return SourceDeletionReconciliationResult.Create(
                            attemptedCount,
                            completedCount,
                            failedCount,
                            hasRemaining: true,
                            firstFailureStage,
                            firstError);
                    }

                    SourceDeletionResult entry = await ReconcileEntryAsync(
                        sourceId).ConfigureAwait(false);
                    attemptedCount++;
                    if (entry.IsSuccess)
                    {
                        completedCount++;
                    }
                    else
                    {
                        failedCount++;
                        if (firstError is null)
                        {
                            firstFailureStage = entry.FailureStage;
                            firstError = entry.Error!;
                        }
                    }

                    if (attemptedCount == SourceDeletionReconciliationResult.MaximumAttemptCount)
                    {
                        bool hasUnattemptedEntries = index + 1 < batch.SourceIds.Count ||
                            batch.NextAfterExclusive.HasValue ||
                            (!wrapped && cycleBoundary.HasValue);
                        return SourceDeletionReconciliationResult.Create(
                            attemptedCount,
                            completedCount,
                            failedCount,
                            hasRemaining: failedCount > 0 || hasUnattemptedEntries,
                            firstFailureStage,
                            firstError);
                    }
                }

                if (!batch.NextAfterExclusive.HasValue)
                {
                    if (!wrapped)
                    {
                        afterExclusive = null;
                        wrapped = true;
                        continue;
                    }

                    return SourceDeletionReconciliationResult.Create(
                        attemptedCount,
                        completedCount,
                        failedCount,
                        hasRemaining: failedCount > 0,
                        firstFailureStage,
                        firstError);
                }

                afterExclusive = batch.NextAfterExclusive;
            }

            throw new InvalidOperationException("The bounded reconciliation loop did not terminate.");
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

    private async ValueTask<SourceDeletionPendingBatchReadResult> ReadPendingBatchAsync(
        SourceId? afterExclusive,
        CancellationToken cancellationToken)
    {
        try
        {
            return await _lifecycle
                .ReadPendingBatchAsync(afterExclusive, cancellationToken)
                .ConfigureAwait(false) ??
                SourceDeletionPendingBatchReadResult.Failed(
                    DomainErrorCode.StorageUnavailable);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellationToken);
        }
        catch (Exception)
        {
            return SourceDeletionPendingBatchReadResult.Failed(
                DomainErrorCode.StorageUnavailable);
        }
    }

    private async ValueTask<SourceDeletionPendingCursorReadResult> ReadPendingCursorAsync(
        CancellationToken cancellationToken)
    {
        try
        {
            return await _lifecycle
                .ReadPendingCursorAsync(cancellationToken)
                .ConfigureAwait(false) ??
                SourceDeletionPendingCursorReadResult.Failed(
                    DomainErrorCode.StorageUnavailable);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellationToken);
        }
        catch (Exception)
        {
            return SourceDeletionPendingCursorReadResult.Failed(
                DomainErrorCode.StorageUnavailable);
        }
    }

    private async ValueTask<SourceDeletionLifecycleOperationResult> AdvancePendingCursorAsync(
        SourceId sourceId)
    {
        try
        {
            return await _lifecycle
                .AdvancePendingCursorAsync(sourceId, CancellationToken.None)
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

    private static int CompareSourceIds(SourceId left, SourceId right) =>
        string.CompareOrdinal(
            left.Value.ToString("N"),
            right.Value.ToString("N"));

    private async ValueTask<SourceDeletionResult> ReconcileEntryAsync(SourceId sourceId)
    {
        try
        {
            using PlaybackSessionCoordinator.SourceRetirementLease retirement =
                _playback.AcquireSourceRetirement(sourceId);
            retirement.Commit();

            SourceDeletionLifecycleOperationResult marked =
                await MarkPendingAsync(sourceId, CancellationToken.None).ConfigureAwait(false);
            if (!marked.IsSuccess)
            {
                return SourceDeletionResult.Failed(
                    SourceDeletionFailureStage.MarkPending,
                    marked.Error!);
            }

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
        catch (Exception)
        {
            return SourceDeletionResult.Failed(
                SourceDeletionFailureStage.PlaybackRelease,
                DomainError.Create(DomainErrorCode.PlaybackControlFailed));
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
