using IptvSuite.Application;
using IptvSuite.Domain;
using IptvSuite.Testing;
using Microsoft.Extensions.Time.Testing;

namespace IptvSuite.UnitTests;

[TestClass]
public sealed class CatalogBrowseCoordinatorTests
{
    [TestMethod]
    public async Task NewBrowseCancelsAndSuppressesThePreviousResult()
    {
        var browser = new ControlledBrowser();
        using var coordinator = new CatalogBrowseCoordinator(browser);
        SourceId sourceId = SourceId.Generate();

        Task<CatalogBrowseResult?> first = coordinator
            .BrowseAsync(sourceId, null, "old", 0, 50, debounce: false).AsTask();
        await browser.FirstStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Task<CatalogBrowseResult?> second = coordinator
            .BrowseAsync(sourceId, null, "new", 0, 50, debounce: false).AsTask();

        Assert.IsNull(await first.WaitAsync(TimeSpan.FromSeconds(2)));
        CatalogBrowseResult? current = await second.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.IsNotNull(current);
        Assert.AreEqual("new", current.SearchText);
        Assert.AreEqual("new", current.Channels.Items[0].Name);
    }

    [TestMethod]
    public async Task CallerCancellationRemainsCancellation()
    {
        var browser = new ControlledBrowser();
        using var coordinator = new CatalogBrowseCoordinator(browser);
        using var cancellation = new CancellationTokenSource();
        Task<CatalogBrowseResult?> pending = coordinator
            .BrowseAsync(SourceId.Generate(), null, "old", 0, 50, false, cancellation.Token)
            .AsTask();
        await browser.FirstStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        cancellation.Cancel();

        await Assert.ThrowsExactlyAsync<OperationCanceledException>(async () => await pending);
    }

    [TestMethod]
    public async Task DebouncedBrowseDoesNotQueryBeforeTheBoundedDelay()
    {
        var browser = new ControlledBrowser();
        FakeTimeProvider time = TestTime.Create(DateTimeOffset.Parse(
            "2026-08-21T00:00:00Z",
            System.Globalization.CultureInfo.InvariantCulture));
        using var coordinator = new CatalogBrowseCoordinator(browser, time);
        Task<CatalogBrowseResult?> pending = coordinator
            .BrowseAsync(SourceId.Generate(), null, "news", 0, 50, debounce: true)
            .AsTask();

        time.Advance(CatalogBrowseCoordinator.DefaultDebounce - TimeSpan.FromMilliseconds(1));
        await Task.Yield();
        Assert.IsFalse(browser.FirstStarted.Task.IsCompleted);

        time.Advance(TimeSpan.FromMilliseconds(1));
        await browser.FirstStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        coordinator.Dispose();
        Assert.IsNull(await pending.WaitAsync(TimeSpan.FromSeconds(2)));
    }

    private sealed class ControlledBrowser : ICatalogBrowser
    {
        private int _channelCalls;

        internal TaskCompletionSource<bool> FirstStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ValueTask<IReadOnlyList<CatalogSourceItem>> ReadSourcesAsync(
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<IReadOnlyList<CatalogSourceItem>>([]);

        public ValueTask<IReadOnlyList<CatalogCategoryItem>> ReadCategoriesAsync(
            SourceId sourceId,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<IReadOnlyList<CatalogCategoryItem>>([]);

        public async ValueTask<CatalogChannelPage> ReadChannelsAsync(
            SourceId sourceId,
            CategoryId? categoryId,
            string? searchText,
            int offset,
            int limit,
            CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref _channelCalls) == 1)
            {
                FirstStarted.TrySetResult(true);
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }

            ChannelId channelId = ChannelId.Generate();
            CategoryId rowCategoryId = CategoryId.Generate();
            return new CatalogChannelPage(
                [new CatalogChannelItem(channelId, rowCategoryId, "stable", searchText!, null, false, false)],
                offset,
                1);
        }
    }
}
