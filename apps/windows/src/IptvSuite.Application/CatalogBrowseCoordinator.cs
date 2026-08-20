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
    private CancellationTokenSource? _activeRequest;
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
        ObjectDisposedException.ThrowIf(_disposed, this);
        long generation = Interlocked.Increment(ref _generation);
        var request = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        CancellationTokenSource? previous;
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            previous = _activeRequest;
            _activeRequest = request;
        }

        previous?.Cancel();
        previous?.Dispose();
        try
        {
            if (debounce)
            {
                await Task.Delay(DefaultDebounce, _timeProvider, request.Token).ConfigureAwait(false);
            }

            IReadOnlyList<CatalogCategoryItem> categories = await _browser
                .ReadCategoriesAsync(sourceId, request.Token).ConfigureAwait(false);
            CatalogChannelPage channels = await _browser
                .ReadChannelsAsync(sourceId, categoryId, searchText, offset, limit, request.Token)
                .ConfigureAwait(false);
            return generation == Volatile.Read(ref _generation)
                ? new CatalogBrowseResult(categories, channels, categoryId, searchText)
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

            request.Dispose();
        }
    }

    public ValueTask<IReadOnlyList<CatalogSourceItem>> ReadSourcesAsync(
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _browser.ReadSourcesAsync(cancellationToken);
    }

    public void Dispose()
    {
        CancellationTokenSource? active;
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

        active?.Cancel();
        active?.Dispose();
    }
}
