using IptvSuite.Domain;

namespace IptvSuite.Application;

public enum ChannelLogoFormat
{
    Png,
    Jpeg,
    WebP,
}

public sealed record ChannelLogoImage(ReadOnlyMemory<byte> Content, ChannelLogoFormat Format);

public interface IChannelLogoProvider
{
    ValueTask<ChannelLogoImage?> LoadAsync(
        SourceId sourceId,
        ChannelId channelId,
        CancellationToken cancellationToken = default);
}

public sealed class ChannelLogoCache : IDisposable
{
    public const int MaximumEntries = 128;
    private const int MaximumConcurrentLoads = 4;
    private readonly IChannelLogoProvider _provider;
    private readonly SemaphoreSlim _concurrency = new(MaximumConcurrentLoads);
    private readonly object _sync = new();
    private readonly Dictionary<(SourceId SourceId, ChannelId ChannelId), ChannelLogoImage> _entries = [];
    private readonly Dictionary<SourceId, long> _sourceGenerations = [];
    private readonly Queue<(SourceId SourceId, ChannelId ChannelId)> _insertionOrder = [];
    private int _activeOperations;
    private bool _disposed;
    private bool _semaphoreDisposed;

    public ChannelLogoCache(IChannelLogoProvider provider) =>
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));

    public async ValueTask<ChannelLogoImage?> GetAsync(
        SourceId sourceId,
        ChannelId channelId,
        CancellationToken cancellationToken = default)
    {
        long sourceGeneration;
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_entries.TryGetValue((sourceId, channelId), out ChannelLogoImage? cached)) return cached;
            sourceGeneration = GetSourceGeneration(sourceId);
            _activeOperations = checked(_activeOperations + 1);
        }

        bool acquired = false;
        try
        {
            await _concurrency.WaitAsync(cancellationToken).ConfigureAwait(false);
            acquired = true;
            lock (_sync)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (_entries.TryGetValue((sourceId, channelId), out ChannelLogoImage? cached)) return cached;
            }

            ChannelLogoImage? loaded = await _provider.LoadAsync(sourceId, channelId, cancellationToken)
                .ConfigureAwait(false);
            if (loaded is null) return null;
            lock (_sync)
            {
                if (_disposed || GetSourceGeneration(sourceId) != sourceGeneration)
                {
                    return loaded;
                }

                if (_entries.TryGetValue((sourceId, channelId), out ChannelLogoImage? cached))
                {
                    return cached;
                }

                while (_entries.Count >= MaximumEntries)
                {
                    if (_entries.Remove(_insertionOrder.Dequeue()))
                    {
                        break;
                    }
                }

                _entries.Add((sourceId, channelId), loaded);
                _insertionOrder.Enqueue((sourceId, channelId));
            }

            return loaded;
        }
        finally
        {
            try
            {
                if (acquired)
                {
                    _concurrency.Release();
                }
            }
            finally
            {
                EndOperation();
            }
        }
    }

    public void EvictSource(SourceId sourceId)
    {
        if (sourceId.IsEmpty)
        {
            throw new ArgumentException("A source identifier is required.", nameof(sourceId));
        }

        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _sourceGenerations[sourceId] = checked(GetSourceGeneration(sourceId) + 1);

            foreach ((SourceId SourceId, ChannelId ChannelId) key in _entries.Keys
                         .Where(key => key.SourceId == sourceId)
                         .ToArray())
            {
                _entries.Remove(key);
            }

            int queued = _insertionOrder.Count;
            for (int index = 0; index < queued; index++)
            {
                (SourceId SourceId, ChannelId ChannelId) key = _insertionOrder.Dequeue();
                if (key.SourceId != sourceId)
                {
                    _insertionOrder.Enqueue(key);
                }
            }
        }
    }

    public void Dispose()
    {
        bool disposeSemaphore = false;
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _entries.Clear();
            _sourceGenerations.Clear();
            _insertionOrder.Clear();
            if (_activeOperations == 0)
            {
                _semaphoreDisposed = true;
                disposeSemaphore = true;
            }
        }

        if (disposeSemaphore)
        {
            _concurrency.Dispose();
        }

        GC.SuppressFinalize(this);
    }

    private long GetSourceGeneration(SourceId sourceId) =>
        _sourceGenerations.GetValueOrDefault(sourceId);

    private void EndOperation()
    {
        bool disposeSemaphore = false;
        lock (_sync)
        {
            _activeOperations--;
            if (_disposed && _activeOperations == 0 && !_semaphoreDisposed)
            {
                _semaphoreDisposed = true;
                disposeSemaphore = true;
            }
        }

        if (disposeSemaphore)
        {
            _concurrency.Dispose();
        }
    }
}
