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
    public const int MaximumCachedPayloadBytes = 32 * 1024 * 1024;
    private const int MaximumConcurrentLoads = 4;
    private readonly IChannelLogoProvider _provider;
    private readonly SemaphoreSlim _concurrency = new(MaximumConcurrentLoads);
    private readonly object _sync = new();
    private readonly Dictionary<(SourceId SourceId, ChannelId ChannelId), CacheEntry> _entries = [];
    private readonly Dictionary<SourceId, long> _sourceGenerations = [];
    private readonly LinkedList<(SourceId SourceId, ChannelId ChannelId)> _recency = [];
    private long _cachedPayloadBytes;
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
        (SourceId SourceId, ChannelId ChannelId) key = (sourceId, channelId);
        long sourceGeneration;
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            ChannelLogoImage? cached = GetCached(key);
            if (cached is not null) return cached;
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
                ChannelLogoImage? cached = GetCached(key);
                if (cached is not null) return cached;
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

                ChannelLogoImage? cached = GetCached(key);
                if (cached is not null)
                {
                    return cached;
                }

                int payloadBytes = loaded.Content.Length;
                if (payloadBytes > MaximumCachedPayloadBytes)
                {
                    return loaded;
                }

                while (_entries.Count >= MaximumEntries ||
                       _cachedPayloadBytes + payloadBytes > MaximumCachedPayloadBytes)
                {
                    RemoveEntry(_recency.First!.Value);
                }

                LinkedListNode<(SourceId SourceId, ChannelId ChannelId)> recencyNode =
                    _recency.AddLast(key);
                _entries.Add(key, new CacheEntry(loaded, recencyNode, payloadBytes));
                _cachedPayloadBytes += payloadBytes;
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
                RemoveEntry(key);
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
            _recency.Clear();
            _cachedPayloadBytes = 0;
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

    private ChannelLogoImage? GetCached((SourceId SourceId, ChannelId ChannelId) key)
    {
        if (!_entries.TryGetValue(key, out CacheEntry? entry))
        {
            return null;
        }

        _recency.Remove(entry.RecencyNode);
        _recency.AddLast(entry.RecencyNode);
        return entry.Image;
    }

    private void RemoveEntry((SourceId SourceId, ChannelId ChannelId) key)
    {
        if (!_entries.Remove(key, out CacheEntry? entry))
        {
            return;
        }

        _recency.Remove(entry.RecencyNode);
        _cachedPayloadBytes -= entry.PayloadBytes;
    }

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

    private sealed record CacheEntry(
        ChannelLogoImage Image,
        LinkedListNode<(SourceId SourceId, ChannelId ChannelId)> RecencyNode,
        int PayloadBytes);
}
