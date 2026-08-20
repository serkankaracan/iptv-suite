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
    private readonly Queue<(SourceId SourceId, ChannelId ChannelId)> _insertionOrder = [];
    private bool _disposed;

    public ChannelLogoCache(IChannelLogoProvider provider) =>
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));

    public async ValueTask<ChannelLogoImage?> GetAsync(
        SourceId sourceId,
        ChannelId channelId,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        lock (_sync)
        {
            if (_entries.TryGetValue((sourceId, channelId), out ChannelLogoImage? cached)) return cached;
        }

        await _concurrency.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            lock (_sync)
            {
                if (_entries.TryGetValue((sourceId, channelId), out ChannelLogoImage? cached)) return cached;
            }

            ChannelLogoImage? loaded = await _provider.LoadAsync(sourceId, channelId, cancellationToken)
                .ConfigureAwait(false);
            if (loaded is null) return null;
            lock (_sync)
            {
                if (_entries.Count == MaximumEntries)
                {
                    _entries.Remove(_insertionOrder.Dequeue());
                }

                _entries[(sourceId, channelId)] = loaded;
                _insertionOrder.Enqueue((sourceId, channelId));
            }

            return loaded;
        }
        finally
        {
            _concurrency.Release();
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        lock (_sync)
        {
            _entries.Clear();
            _insertionOrder.Clear();
        }
        _concurrency.Dispose();
        GC.SuppressFinalize(this);
    }
}
