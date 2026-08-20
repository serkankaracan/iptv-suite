using IptvSuite.Domain;

namespace IptvSuite.Application;

public sealed record CatalogCategoryItem(CategoryId CategoryId, string Name, int SortOrder);

public sealed record CatalogChannelItem(
    ChannelId ChannelId,
    CategoryId CategoryId,
    string StableKey,
    string Name,
    int? Number,
    bool IsAdult);

public sealed record CatalogChannelPage(
    IReadOnlyList<CatalogChannelItem> Items,
    int Offset,
    int TotalCount);

public interface ICatalogBrowser
{
    ValueTask<IReadOnlyList<CatalogCategoryItem>> ReadCategoriesAsync(
        SourceId sourceId,
        CancellationToken cancellationToken = default);

    ValueTask<CatalogChannelPage> ReadChannelsAsync(
        SourceId sourceId,
        CategoryId? categoryId,
        string? searchText,
        int offset,
        int limit,
        CancellationToken cancellationToken = default);
}
