using IptvSuite.Application;
using IptvSuite.Domain;
using IptvSuite.Testing;
using Microsoft.Extensions.Time.Testing;

namespace IptvSuite.UnitTests;

[TestClass]
public sealed class CatalogBrowseCoordinatorTests
{
    [TestMethod]
    public async Task CategoryInheritedFromAnotherSourceIsClearedBeforeChannelQuery()
    {
        CategoryId currentSourceCategoryId = CategoryId.Generate();
        CategoryId previousSourceCategoryId = CategoryId.Generate();
        var browser = new CategoryAdmissionBrowser(currentSourceCategoryId);
        using var coordinator = new CatalogBrowseCoordinator(browser);

        CatalogBrowseResult? result = await coordinator.BrowseAsync(
            SourceId.Generate(),
            previousSourceCategoryId,
            searchText: null,
            offset: 0,
            limit: 50,
            debounce: false);

        Assert.IsNotNull(result);
        Assert.IsNull(result.SelectedCategoryId);
        Assert.IsNull(browser.RequestedCategoryId);
    }

    [TestMethod]
    public async Task CategoryAvailableInTheCurrentSourceRemainsSelected()
    {
        CategoryId currentSourceCategoryId = CategoryId.Generate();
        var browser = new CategoryAdmissionBrowser(currentSourceCategoryId);
        using var coordinator = new CatalogBrowseCoordinator(browser);

        CatalogBrowseResult? result = await coordinator.BrowseAsync(
            SourceId.Generate(),
            currentSourceCategoryId,
            searchText: null,
            offset: 0,
            limit: 50,
            debounce: false);

        Assert.IsNotNull(result);
        Assert.AreEqual(currentSourceCategoryId, result.SelectedCategoryId);
        Assert.AreEqual(currentSourceCategoryId, browser.RequestedCategoryId);
    }

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
    public async Task CancelPendingCancelsAndSuppressesTheActiveBrowse()
    {
        var browser = new ControlledBrowser();
        using var coordinator = new CatalogBrowseCoordinator(browser);
        Task<CatalogBrowseResult?> pending = coordinator
            .BrowseAsync(SourceId.Generate(), null, "pending", 0, 50, debounce: false)
            .AsTask();
        await browser.FirstStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        coordinator.CancelPending();

        Assert.IsNull(await pending.WaitAsync(TimeSpan.FromSeconds(2)));
    }

    [TestMethod]
    public async Task CancelPendingSuppressesAResultFromANonCooperativeBrowser()
    {
        var browser = new NonCooperativeBrowser();
        using var coordinator = new CatalogBrowseCoordinator(browser);
        Task<CatalogBrowseResult?> pending = coordinator
            .BrowseAsync(SourceId.Generate(), null, "pending", 0, 50, debounce: false)
            .AsTask();
        await browser.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));

        coordinator.CancelPending();
        browser.Release.TrySetResult(true);

        Assert.IsNull(await pending.WaitAsync(TimeSpan.FromSeconds(2)));
    }

    [TestMethod]
    public async Task CompletionDuringDetachedCancellationDefersOwnedTokenDisposal()
    {
        var browser = new SlowCancellationBrowser();
        using var coordinator = new CatalogBrowseCoordinator(browser);
        Task<CatalogBrowseResult?> first = coordinator
            .BrowseAsync(SourceId.Generate(), null, "first", 0, 50, debounce: false)
            .AsTask();
        await browser.FirstStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Task<CatalogBrowseResult?> second = Task.Run(async () => await coordinator.BrowseAsync(
            SourceId.Generate(),
            null,
            "second",
            0,
            50,
            debounce: false));
        await browser.CallbackStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        browser.ReleaseFirst.TrySetResult(true);

        Assert.IsNull(await first.WaitAsync(TimeSpan.FromSeconds(2)));
        browser.ReleaseCallback.TrySetResult(true);
        await browser.CallbackCompleted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        CatalogBrowseResult? current = await second.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.IsNotNull(current);
        Assert.AreEqual("second", current.SearchText);
    }

    [TestMethod]
    public async Task ReplacementIsolatesAThrowingLinkedCancellationCallback()
    {
        var browser = new ThrowingCancellationBrowser();
        using var coordinator = new CatalogBrowseCoordinator(browser);
        SourceId sourceId = SourceId.Generate();
        Task<CatalogBrowseResult?> first = coordinator
            .BrowseAsync(sourceId, null, "first", 0, 50, debounce: false)
            .AsTask();
        await browser.FirstStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Task<CatalogBrowseResult?> second = coordinator
            .BrowseAsync(sourceId, null, "second", 0, 50, debounce: false)
            .AsTask();

        Assert.IsNull(await first.WaitAsync(TimeSpan.FromSeconds(2)));
        CatalogBrowseResult? current = await second.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.IsNotNull(current);
        Assert.AreEqual("second", current.SearchText);
    }

    [TestMethod]
    public async Task CancelPendingIsolatesAThrowingLinkedCancellationCallback()
    {
        var browser = new ThrowingCancellationBrowser();
        using var coordinator = new CatalogBrowseCoordinator(browser);
        Task<CatalogBrowseResult?> pending = coordinator
            .BrowseAsync(SourceId.Generate(), null, "pending", 0, 50, debounce: false)
            .AsTask();
        await browser.FirstStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        coordinator.CancelPending();

        Assert.IsNull(await pending.WaitAsync(TimeSpan.FromSeconds(2)));
    }

    [TestMethod]
    public async Task DisposeIsolatesAThrowingLinkedCancellationCallback()
    {
        var browser = new ThrowingCancellationBrowser();
        var coordinator = new CatalogBrowseCoordinator(browser);
        Task<CatalogBrowseResult?> pending = coordinator
            .BrowseAsync(SourceId.Generate(), null, "pending", 0, 50, debounce: false)
            .AsTask();
        await browser.FirstStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        coordinator.Dispose();

        Assert.IsNull(await pending.WaitAsync(TimeSpan.FromSeconds(2)));
        coordinator.Dispose();
    }

    [TestMethod]
    public async Task ConcurrentBrowseReplacementAndCancellationRemainExceptionSafe()
    {
        for (int iteration = 0; iteration < 50; iteration++)
        {
            using var coordinator = new CatalogBrowseCoordinator(new YieldingBrowser());
            var operations = new List<Task>();
            for (int request = 0; request < 16; request++)
            {
                operations.Add(Task.Run(async () =>
                {
                    _ = await coordinator.BrowseAsync(
                        SourceId.Generate(),
                        null,
                        "stress",
                        0,
                        50,
                        debounce: false);
                }));
                operations.Add(Task.Run(coordinator.CancelPending));
            }

            await Task.WhenAll(operations).WaitAsync(TimeSpan.FromSeconds(2));
        }
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

    private sealed class CategoryAdmissionBrowser(CategoryId availableCategoryId) : ICatalogBrowser
    {
        internal CategoryId? RequestedCategoryId { get; private set; }

        public ValueTask<IReadOnlyList<CatalogSourceItem>> ReadSourcesAsync(
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<IReadOnlyList<CatalogSourceItem>>([]);

        public ValueTask<IReadOnlyList<CatalogCategoryItem>> ReadCategoriesAsync(
            SourceId sourceId,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<IReadOnlyList<CatalogCategoryItem>>(
                [new CatalogCategoryItem(availableCategoryId, "Current source", 0)]);

        public ValueTask<CatalogChannelPage> ReadChannelsAsync(
            SourceId sourceId,
            CategoryId? categoryId,
            string? searchText,
            int offset,
            int limit,
            CancellationToken cancellationToken = default)
        {
            RequestedCategoryId = categoryId;
            return ValueTask.FromResult(new CatalogChannelPage([], offset, 0));
        }
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

    private sealed class NonCooperativeBrowser : ICatalogBrowser
    {
        internal TaskCompletionSource<bool> Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource<bool> Release { get; } =
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
            Started.TrySetResult(true);
            await Release.Task.ConfigureAwait(false);
            return new CatalogChannelPage([], offset, 0);
        }
    }

    private sealed class YieldingBrowser : ICatalogBrowser
    {
        public ValueTask<IReadOnlyList<CatalogSourceItem>> ReadSourcesAsync(
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<IReadOnlyList<CatalogSourceItem>>([]);

        public async ValueTask<IReadOnlyList<CatalogCategoryItem>> ReadCategoriesAsync(
            SourceId sourceId,
            CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            return [];
        }

        public async ValueTask<CatalogChannelPage> ReadChannelsAsync(
            SourceId sourceId,
            CategoryId? categoryId,
            string? searchText,
            int offset,
            int limit,
            CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            return new CatalogChannelPage([], offset, 0);
        }
    }

    private sealed class ThrowingCancellationBrowser : ICatalogBrowser
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
                using CancellationTokenRegistration registration = cancellationToken.Register(
                    static () => throw new InvalidOperationException("Synthetic cancellation callback failure."));
                FirstStarted.TrySetResult(true);
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
            }

            return new CatalogChannelPage(
                [new CatalogChannelItem(
                    ChannelId.Generate(),
                    CategoryId.Generate(),
                    "stable",
                    searchText!,
                    null,
                    false,
                    false)],
                offset,
                1);
        }
    }

    private sealed class SlowCancellationBrowser : ICatalogBrowser
    {
        private int _channelCalls;

        internal TaskCompletionSource<bool> FirstStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource<bool> CallbackStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource<bool> CallbackCompleted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource<bool> ReleaseCallback { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource<bool> ReleaseFirst { get; } =
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
                _ = cancellationToken.Register(() =>
                {
                    CallbackStarted.TrySetResult(true);
                    ReleaseCallback.Task.GetAwaiter().GetResult();
                    try
                    {
                        _ = cancellationToken.Register(static () => { });
                        CallbackCompleted.TrySetResult(true);
                    }
                    catch (Exception exception)
                    {
                        CallbackCompleted.TrySetException(exception);
                        throw;
                    }
                });
                FirstStarted.TrySetResult(true);
                await ReleaseFirst.Task.ConfigureAwait(false);
            }

            return new CatalogChannelPage(
                [new CatalogChannelItem(
                    ChannelId.Generate(),
                    CategoryId.Generate(),
                    "stable",
                    searchText!,
                    null,
                    false,
                    false)],
                offset,
                1);
        }
    }
}
