using IptvSuite.Domain;

namespace IptvSuite.Application;

public sealed record CatalogBrowseResult(
    IReadOnlyList<CatalogCategoryItem> Categories,
    CatalogChannelPage Channels,
    CategoryId? SelectedCategoryId,
    string? SearchText);

public sealed class CatalogBrowseCoordinator : IDisposable
{
    public static readonly TimeSpan DefaultDebounce = TimeSpan.FromMilliseconds(250);

    private readonly ICatalogBrowser _browser;
    private readonly TimeProvider _timeProvider;
    private readonly object _sync = new();
    private BrowseRequest? _activeRequest;
    private long _generation;
    private bool _disposed;

    public CatalogBrowseCoordinator(ICatalogBrowser browser, TimeProvider? timeProvider = null)
    {
        _browser = browser ?? throw new ArgumentNullException(nameof(browser));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async ValueTask<CatalogBrowseResult?> BrowseAsync(
        SourceId sourceId,
        CategoryId? categoryId,
        string? searchText,
        int offset,
        int limit,
        bool debounce,
        CancellationToken cancellationToken = default)
    {
        BrowseRequest request;
        BrowseRequest? previous;
        long generation;
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            generation = checked(_generation + 1);
            request = new BrowseRequest(cancellationToken);
            _generation = generation;
            previous = _activeRequest;
            _activeRequest = request;
        }

        try
        {
            previous?.CancelSafely();
            if (debounce)
            {
                await Task.Delay(DefaultDebounce, _timeProvider, request.Token).ConfigureAwait(false);
            }

            IReadOnlyList<CatalogCategoryItem> categories = await _browser
                .ReadCategoriesAsync(sourceId, request.Token).ConfigureAwait(false);
            CategoryId? admittedCategoryId = categoryId.HasValue && categories.Any(
                category => category.CategoryId.Equals(categoryId.Value))
                ? categoryId
                : null;
            CatalogChannelPage channels = await _browser
                .ReadChannelsAsync(
                    sourceId,
                    admittedCategoryId,
                    searchText,
                    offset,
                    limit,
                    request.Token)
                .ConfigureAwait(false);
            return generation == Volatile.Read(ref _generation)
                ? new CatalogBrowseResult(
                    categories,
                    channels,
                    admittedCategoryId,
                    searchText)
                : null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellationToken);
        }
        catch (OperationCanceledException) when (
            !cancellationToken.IsCancellationRequested &&
            generation != Volatile.Read(ref _generation))
        {
            return null;
        }
        finally
        {
            lock (_sync)
            {
                if (ReferenceEquals(_activeRequest, request))
                {
                    _activeRequest = null;
                }
            }

            request.Complete();
        }
    }

    public ValueTask<IReadOnlyList<CatalogSourceItem>> ReadSourcesAsync(
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _browser.ReadSourcesAsync(cancellationToken);
    }

    public void CancelPending()
    {
        BrowseRequest? active;
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            checked
            {
                _generation++;
            }

            active = _activeRequest;
            _activeRequest = null;
        }

        active?.CancelSafely();
    }

    public void Dispose()
    {
        BrowseRequest? active;
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            Interlocked.Increment(ref _generation);
            active = _activeRequest;
            _activeRequest = null;
        }

        active?.CancelSafely();
    }

    private sealed class BrowseRequest
    {
        private readonly object _sync = new();
        private readonly CancellationTokenSource _source;
        private bool _canceling;
        private bool _completed;
        private bool _disposed;

        internal BrowseRequest(CancellationToken cancellationToken)
        {
            _source = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            Token = _source.Token;
        }

        internal CancellationToken Token { get; }

        internal void CancelSafely()
        {
            lock (_sync)
            {
                if (_disposed || _canceling)
                {
                    return;
                }

                _canceling = true;
            }

            try
            {
                _source.Cancel(throwOnFirstException: false);
            }
            catch (ObjectDisposedException)
            {
                // Completion can win before a detached request is canceled.
            }
            catch (AggregateException)
            {
                // Cancellation callbacks cannot escape the coordinator control flow.
            }
            finally
            {
                bool dispose = false;
                lock (_sync)
                {
                    _canceling = false;
                    if (_completed && !_disposed)
                    {
                        _disposed = true;
                        dispose = true;
                    }
                }

                if (dispose)
                {
                    _source.Dispose();
                }
            }
        }

        internal void Complete()
        {
            bool dispose = false;
            lock (_sync)
            {
                if (_completed)
                {
                    return;
                }

                _completed = true;
                if (!_canceling && !_disposed)
                {
                    _disposed = true;
                    dispose = true;
                }
            }

            if (dispose)
            {
                _source.Dispose();
            }
        }
    }
}
