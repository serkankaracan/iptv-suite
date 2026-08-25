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

    [TestMethod]
    public async Task CacheHitMakesTheEntryMostRecentlyUsedBeforeCountEviction()
    {
        var provider = new CountingProvider();
        using var cache = new ChannelLogoCache(provider);
        SourceId sourceId = SourceId.Generate();
        ChannelId touchedChannel = ChannelId.Generate();
        ChannelId leastRecentlyUsedChannel = ChannelId.Generate();
        ChannelLogoImage? touchedImage = await cache.GetAsync(sourceId, touchedChannel);
        ChannelLogoImage? leastRecentlyUsedImage = await cache.GetAsync(sourceId, leastRecentlyUsedChannel);
        for (int index = 2; index < ChannelLogoCache.MaximumEntries; index++)
        {
            await cache.GetAsync(sourceId, ChannelId.Generate());
        }

        Assert.AreSame(touchedImage, await cache.GetAsync(sourceId, touchedChannel));
        await cache.GetAsync(sourceId, ChannelId.Generate());
        int callsAfterOverflow = provider.Calls;

        Assert.AreSame(touchedImage, await cache.GetAsync(sourceId, touchedChannel));
        Assert.AreNotSame(leastRecentlyUsedImage, await cache.GetAsync(sourceId, leastRecentlyUsedChannel));
        Assert.AreEqual(callsAfterOverflow + 1, provider.Calls);
        Assert.AreEqual(ChannelLogoCache.MaximumEntries, ReadEntryCount(cache));
    }

    [TestMethod]
    public async Task CacheEvictsLeastRecentlyUsedEntriesToStayWithinTheByteBudget()
    {
        SourceId sourceId = SourceId.Generate();
        ChannelId twentyMebibyteChannel = ChannelId.Generate();
        ChannelId twelveMebibyteChannel = ChannelId.Generate();
        ChannelId oneByteChannel = ChannelId.Generate();
        var provider = new SizedProvider(new Dictionary<ChannelId, int>
        {
            [twentyMebibyteChannel] = 20 * 1024 * 1024,
            [twelveMebibyteChannel] = 12 * 1024 * 1024,
            [oneByteChannel] = 1,
        });
        using var cache = new ChannelLogoCache(provider);

        await cache.GetAsync(sourceId, twentyMebibyteChannel);
        ChannelLogoImage? retained = await cache.GetAsync(sourceId, twelveMebibyteChannel);
        Assert.AreEqual(ChannelLogoCache.MaximumCachedPayloadBytes, ReadCachedPayloadBytes(cache));

        await cache.GetAsync(sourceId, oneByteChannel);
        Assert.AreSame(retained, await cache.GetAsync(sourceId, twelveMebibyteChannel));
        await cache.GetAsync(sourceId, twentyMebibyteChannel);

        Assert.AreEqual(2, provider.GetCalls(twentyMebibyteChannel));
        Assert.AreEqual(1, provider.GetCalls(twelveMebibyteChannel));
        Assert.AreEqual(1, provider.GetCalls(oneByteChannel));
        Assert.AreEqual(ChannelLogoCache.MaximumCachedPayloadBytes, ReadCachedPayloadBytes(cache));
        Assert.AreEqual(2, ReadEntryCount(cache));
    }

    [TestMethod]
    public async Task SourceEvictionRemovesOnlyTheExactSourceEntries()
    {
        var provider = new CountingProvider();
        using var cache = new ChannelLogoCache(provider);
        SourceId removedSource = SourceId.Generate();
        SourceId retainedSource = SourceId.Generate();
        ChannelId removedChannel = ChannelId.Generate();
        ChannelId retainedChannel = ChannelId.Generate();
        ChannelLogoImage? removed = await cache.GetAsync(removedSource, removedChannel);
        ChannelLogoImage? retained = await cache.GetAsync(retainedSource, retainedChannel);

        cache.EvictSource(removedSource);

        ChannelLogoImage? retainedAgain = await cache.GetAsync(retainedSource, retainedChannel);
        ChannelLogoImage? removedAgain = await cache.GetAsync(removedSource, removedChannel);
        Assert.AreSame(retained, retainedAgain);
        Assert.AreNotSame(removed, removedAgain);
        Assert.AreEqual(3, provider.Calls);
    }

    [TestMethod]
    public async Task SourceEvictionPreventsAnInFlightLoadFromReinsertingTheEntry()
    {
        var provider = new BlockingProvider();
        using var cache = new ChannelLogoCache(provider);
        SourceId sourceId = SourceId.Generate();
        ChannelId channelId = ChannelId.Generate();
        Task<ChannelLogoImage?> staleLoad = cache.GetAsync(sourceId, channelId).AsTask();
        await provider.FirstStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        cache.EvictSource(sourceId);
        provider.ReleaseFirst.TrySetResult(true);

        ChannelLogoImage? stale = await staleLoad.WaitAsync(TimeSpan.FromSeconds(2));
        ChannelLogoImage? current = await cache.GetAsync(sourceId, channelId);
        ChannelLogoImage? cached = await cache.GetAsync(sourceId, channelId);
        Assert.IsNotNull(stale);
        Assert.IsNotNull(current);
        Assert.AreNotSame(stale, current);
        Assert.AreSame(current, cached);
        Assert.AreEqual(2, provider.Calls);
    }

    [TestMethod]
    public async Task ConcurrentSameKeyLoadsPreserveTheBoundedUniqueLru()
    {
        SourceId sourceId = SourceId.Generate();
        ChannelId repeatedChannel = ChannelId.Generate();
        var provider = new SaturatingProvider(repeatedChannel, 4);
        using var cache = new ChannelLogoCache(provider);
        Task<ChannelLogoImage?>[] concurrent = Enumerable.Range(0, 4)
            .Select(_ => cache.GetAsync(sourceId, repeatedChannel).AsTask())
            .ToArray();
        await provider.AllBlocked.Task.WaitAsync(TimeSpan.FromSeconds(2));
        provider.ReleaseBlocked.TrySetResult(true);

        ChannelLogoImage?[] repeatedResults = await Task.WhenAll(concurrent);
        Assert.IsNotNull(repeatedResults[0]);
        Assert.IsTrue(repeatedResults.All(image => ReferenceEquals(image, repeatedResults[0])));

        var distinctChannels = new List<(ChannelId Id, ChannelLogoImage Image)>();
        for (int index = 0; index < ChannelLogoCache.MaximumEntries - 1; index++)
        {
            ChannelId channelId = ChannelId.Generate();
            ChannelLogoImage? image = await cache.GetAsync(sourceId, channelId);
            Assert.IsNotNull(image);
            distinctChannels.Add((channelId, image));
        }

        _ = await cache.GetAsync(sourceId, ChannelId.Generate());
        _ = await cache.GetAsync(sourceId, ChannelId.Generate());
        int callsAfterOverflow = provider.Calls;
        ChannelLogoImage? retained = await cache.GetAsync(sourceId, distinctChannels[1].Id);
        Assert.AreSame(distinctChannels[1].Image, retained);
        Assert.AreEqual(callsAfterOverflow, provider.Calls);

        _ = await cache.GetAsync(sourceId, repeatedChannel);
        _ = await cache.GetAsync(sourceId, distinctChannels[0].Id);
        Assert.AreEqual(callsAfterOverflow + 2, provider.Calls);
        Assert.AreEqual(ChannelLogoCache.MaximumEntries, ReadEntryCount(cache));
    }

    [TestMethod]
    public async Task ProviderLoadsNeverExceedTheConcurrencyLimit()
    {
        var provider = new ConcurrencyTrackingProvider();
        using var cache = new ChannelLogoCache(provider);
        SourceId sourceId = SourceId.Generate();
        Task<ChannelLogoImage?>[] loads = Enumerable.Range(0, 12)
            .Select(_ => cache.GetAsync(sourceId, ChannelId.Generate()).AsTask())
            .ToArray();

        try
        {
            await provider.CapacityReached.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.AreEqual(4, provider.Calls);
            Assert.AreEqual(4, provider.PeakConcurrency);
        }
        finally
        {
            provider.Release.TrySetResult(true);
        }

        ChannelLogoImage?[] results = await Task.WhenAll(loads).WaitAsync(TimeSpan.FromSeconds(2));
        Assert.IsTrue(results.All(image => image is not null));
        Assert.AreEqual(12, provider.Calls);
        Assert.AreEqual(4, provider.PeakConcurrency);
    }

    [TestMethod]
    public async Task CancellationRemovesAQueuedFifthLoadWithoutCallingTheProvider()
    {
        var provider = new ConcurrencyTrackingProvider();
        using var cache = new ChannelLogoCache(provider);
        SourceId sourceId = SourceId.Generate();
        Task<ChannelLogoImage?>[] occupyingLoads = Enumerable.Range(0, 4)
            .Select(_ => cache.GetAsync(sourceId, ChannelId.Generate()).AsTask())
            .ToArray();
        await provider.CapacityReached.Task.WaitAsync(TimeSpan.FromSeconds(2));
        using var cancellation = new CancellationTokenSource();
        Task<ChannelLogoImage?> queuedLoad = cache.GetAsync(
            sourceId,
            ChannelId.Generate(),
            cancellation.Token).AsTask();

        try
        {
            cancellation.Cancel();
            await Assert.ThrowsExactlyAsync<OperationCanceledException>(async () =>
                await queuedLoad);
            Assert.AreEqual(4, provider.Calls);
            Assert.AreEqual(4, provider.PeakConcurrency);
        }
        finally
        {
            provider.Release.TrySetResult(true);
        }

        await Task.WhenAll(occupyingLoads).WaitAsync(TimeSpan.FromSeconds(2));
        Assert.AreEqual(4, provider.Calls);
    }

    [TestMethod]
    public async Task DisposeDefersSemaphoreCleanupAndRejectsReinsertion()
    {
        var provider = new BlockingProvider();
        var cache = new ChannelLogoCache(provider);
        SourceId sourceId = SourceId.Generate();
        ChannelId channelId = ChannelId.Generate();
        Task<ChannelLogoImage?> inFlight = cache.GetAsync(sourceId, channelId).AsTask();
        await provider.FirstStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        cache.Dispose();
        provider.ReleaseFirst.TrySetResult(true);

        Assert.IsNotNull(await inFlight.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.AreEqual(0, ReadEntryCount(cache));
        await Assert.ThrowsExactlyAsync<ObjectDisposedException>(async () =>
            await cache.GetAsync(sourceId, channelId));
        cache.Dispose();
    }

    private static int ReadEntryCount(ChannelLogoCache cache)
    {
        object entries = typeof(ChannelLogoCache).GetField(
            "_entries",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(cache)!;
        return (int)entries.GetType().GetProperty("Count")!.GetValue(entries)!;
    }

    private static long ReadCachedPayloadBytes(ChannelLogoCache cache) =>
        (long)typeof(ChannelLogoCache).GetField(
            "_cachedPayloadBytes",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(cache)!;

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

    private sealed class BlockingProvider : IChannelLogoProvider
    {
        private int _calls;

        internal int Calls => Volatile.Read(ref _calls);

        internal TaskCompletionSource<bool> FirstStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource<bool> ReleaseFirst { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async ValueTask<ChannelLogoImage?> LoadAsync(
            SourceId sourceId,
            ChannelId channelId,
            CancellationToken cancellationToken = default)
        {
            int call = Interlocked.Increment(ref _calls);
            if (call == 1)
            {
                FirstStarted.TrySetResult(true);
                await ReleaseFirst.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            }

            return new ChannelLogoImage(new byte[] { (byte)call }, ChannelLogoFormat.Png);
        }
    }

    private sealed class SizedProvider(IReadOnlyDictionary<ChannelId, int> payloadBytes)
        : IChannelLogoProvider
    {
        private readonly Dictionary<ChannelId, int> _calls = [];
        private readonly IReadOnlyDictionary<ChannelId, int> _payloadBytes = payloadBytes;

        internal int GetCalls(ChannelId channelId) => _calls.GetValueOrDefault(channelId);

        public ValueTask<ChannelLogoImage?> LoadAsync(
            SourceId sourceId,
            ChannelId channelId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _calls[channelId] = checked(GetCalls(channelId) + 1);
            return ValueTask.FromResult<ChannelLogoImage?>(new ChannelLogoImage(
                new byte[_payloadBytes[channelId]],
                ChannelLogoFormat.Png));
        }
    }

    private sealed class ConcurrencyTrackingProvider : IChannelLogoProvider
    {
        private int _active;
        private int _calls;
        private int _peakConcurrency;

        internal int Calls => Volatile.Read(ref _calls);

        internal int PeakConcurrency => Volatile.Read(ref _peakConcurrency);

        internal TaskCompletionSource<bool> CapacityReached { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource<bool> Release { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async ValueTask<ChannelLogoImage?> LoadAsync(
            SourceId sourceId,
            ChannelId channelId,
            CancellationToken cancellationToken = default)
        {
            int calls = Interlocked.Increment(ref _calls);
            int active = Interlocked.Increment(ref _active);
            UpdatePeak(active);
            if (calls == 4)
            {
                CapacityReached.TrySetResult(true);
            }

            try
            {
                await Release.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
                return new ChannelLogoImage(new byte[] { (byte)calls }, ChannelLogoFormat.Png);
            }
            finally
            {
                Interlocked.Decrement(ref _active);
            }
        }

        private void UpdatePeak(int active)
        {
            int observed = Volatile.Read(ref _peakConcurrency);
            while (active > observed)
            {
                int previous = Interlocked.CompareExchange(ref _peakConcurrency, active, observed);
                if (previous == observed)
                {
                    return;
                }

                observed = previous;
            }
        }
    }

    private sealed class SaturatingProvider(ChannelId blockedChannel, int expectedBlockedCalls)
        : IChannelLogoProvider
    {
        private readonly ChannelId _blockedChannel = blockedChannel;
        private readonly int _expectedBlockedCalls = expectedBlockedCalls;
        private int _blockedCalls;
        private int _calls;

        internal int Calls => Volatile.Read(ref _calls);

        internal TaskCompletionSource<bool> AllBlocked { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource<bool> ReleaseBlocked { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async ValueTask<ChannelLogoImage?> LoadAsync(
            SourceId sourceId,
            ChannelId channelId,
            CancellationToken cancellationToken = default)
        {
            int call = Interlocked.Increment(ref _calls);
            if (channelId == _blockedChannel)
            {
                int blockedCall = Interlocked.Increment(ref _blockedCalls);
                if (blockedCall <= _expectedBlockedCalls)
                {
                    if (blockedCall == _expectedBlockedCalls)
                    {
                        AllBlocked.TrySetResult(true);
                    }

                    await ReleaseBlocked.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
                }
            }

            return new ChannelLogoImage(
                new byte[] { (byte)(call % byte.MaxValue) },
                ChannelLogoFormat.Png);
        }
    }
}
