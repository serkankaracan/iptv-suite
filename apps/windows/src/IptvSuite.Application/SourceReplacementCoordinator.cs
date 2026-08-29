using IptvSuite.Domain;

namespace IptvSuite.Application;

/// <summary>
/// Temporarily blocks playback admission for one source while its configuration and snapshot are
/// replaced under the same source identity.
/// </summary>
/// <remarks>
/// The temporary retirement prevents a session from resolving a half-replaced configuration. The
/// replacement operation remains responsible for its database old-or-new commit boundary. Unlike
/// deletion, the retirement is rolled back after the operation so the same source identifier can
/// be played again.
/// </remarks>
public sealed class SourceReplacementCoordinator : IAsyncDisposable
{
    private readonly PlaybackSessionCoordinator _playback;
    private readonly SemaphoreSlim _replacementGate = new(1, 1);
    private readonly object _sync = new();
    private Task? _disposeTask;
    private bool _disposed;

    public SourceReplacementCoordinator(PlaybackSessionCoordinator playback)
    {
        _playback = playback ?? throw new ArgumentNullException(nameof(playback));
    }

    public async ValueTask<DomainResult<bool>> ReplaceAsync(
        SourceId sourceId,
        Func<CancellationToken, ValueTask<DomainResult<bool>>> replacement,
        CancellationToken cancellationToken = default)
    {
        if (sourceId.IsEmpty)
        {
            throw new ArgumentException(
                "A source identifier is required for replacement.",
                nameof(sourceId));
        }

        ArgumentNullException.ThrowIfNull(replacement);
        cancellationToken.ThrowIfCancellationRequested();
        Task gateWait;
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            gateWait = _replacementGate.WaitAsync(cancellationToken);
        }

        bool gateEntered = false;
        try
        {
            await gateWait.ConfigureAwait(false);
            gateEntered = true;
            cancellationToken.ThrowIfCancellationRequested();
            using PlaybackSessionCoordinator.SourceRetirementLease retirement =
                _playback.AcquireSourceRetirement(sourceId);
            PlaybackEngineOperationResult drained =
                await _playback.DrainSourceRetirementAsync(sourceId).ConfigureAwait(false);
            if (!drained.IsSuccess)
            {
                return DomainResult.Failure<bool>(
                    drained.Error ?? DomainError.Create(DomainErrorCode.PlaybackControlFailed));
            }

            try
            {
                return await replacement(cancellationToken).ConfigureAwait(false) ??
                    DomainResult.Failure<bool>(DomainErrorCode.StorageUnavailable);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception) when (IsRecoverable(exception))
            {
                return DomainResult.Failure<bool>(DomainErrorCode.StorageUnavailable);
            }
        }
        finally
        {
            if (gateEntered)
            {
                _replacementGate.Release();
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

    private async Task DisposeGateAsync()
    {
        await _replacementGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        _replacementGate.Dispose();
    }

    private static bool IsRecoverable(Exception exception) =>
        exception is not OutOfMemoryException and
        not StackOverflowException and
        not AccessViolationException;
}
