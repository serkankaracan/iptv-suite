using IptvSuite.Application;
using IptvSuite.Domain;

namespace IptvSuite.UnitTests;

[TestClass]
public sealed class ChannelLogoCacheTests
{
    [TestMethod]
    public async Task RepeatedChannelUsesCachedImageWithoutASecondProviderCall()
    {
        var provider = new CountingProvider();
        using var cache = new ChannelLogoCache(provider);
        SourceId sourceId = SourceId.Generate();
        ChannelId channelId = ChannelId.Generate();

        ChannelLogoImage? first = await cache.GetAsync(sourceId, channelId);
        ChannelLogoImage? second = await cache.GetAsync(sourceId, channelId);

        Assert.IsNotNull(first);
        Assert.AreSame(first, second);
        Assert.AreEqual(1, provider.Calls);
    }

    [TestMethod]
    public async Task CacheEvictsTheOldestEntryAtItsFixedCapacity()
    {
        var provider = new CountingProvider();
        using var cache = new ChannelLogoCache(provider);
        SourceId sourceId = SourceId.Generate();
        ChannelId oldest = ChannelId.Generate();
        await cache.GetAsync(sourceId, oldest);
        for (int index = 1; index <= ChannelLogoCache.MaximumEntries; index++)
        {
            await cache.GetAsync(sourceId, ChannelId.Generate());
        }

        await cache.GetAsync(sourceId, oldest);
        Assert.AreEqual(ChannelLogoCache.MaximumEntries + 2, provider.Calls);
    }

    private sealed class CountingProvider : IChannelLogoProvider
    {
        internal int Calls { get; private set; }

        public ValueTask<ChannelLogoImage?> LoadAsync(SourceId sourceId, ChannelId channelId, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;
            return ValueTask.FromResult<ChannelLogoImage?>(new ChannelLogoImage(new byte[] { 1, 2, 3 }, ChannelLogoFormat.Png));
        }
    }
}
