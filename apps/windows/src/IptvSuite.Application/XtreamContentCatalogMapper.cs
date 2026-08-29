using System.Security.Cryptography;
using System.Text;

namespace IptvSuite.Application;

public static class XtreamContentCatalogMapper
{
    public static DomainResult<ContentCatalogMutation> Map(
        SourceId sourceId,
        SnapshotId snapshotId,
        XtreamContentCatalog? catalog)
    {
        if (sourceId.IsEmpty || snapshotId.IsEmpty || catalog is null)
        {
            return DomainResult.Failure<ContentCatalogMutation>(
                DomainErrorCode.DomainInvariantViolation);
        }

        var categories = new List<ChannelCategory>();
        var categoryMap = new Dictionary<(ContentKind Kind, string ProviderId), CategoryId>();
        int sortOrder = 0;
        foreach (XtreamCategoryInput input in catalog.MovieCategories.Items
                     .Concat(catalog.SeriesCategories.Items))
        {
            if (input.ContentKind is not (ContentKind.Movie or ContentKind.Series))
            {
                return DomainResult.Failure<ContentCatalogMutation>(
                    DomainErrorCode.DomainInvariantViolation);
            }

            CategoryId categoryId = CategoryId.Generate();
            string stableKey = BuildCategoryStableKey(input.ContentKind, input.ProviderIdentifier);
            DomainResult<ChannelCategory> category = ChannelCategory.Create(
                categoryId,
                snapshotId,
                stableKey,
                input.Name,
                sortOrder++,
                isSynthetic: false);
            if (!category.IsSuccess ||
                !categoryMap.TryAdd((input.ContentKind, input.ProviderIdentifier), categoryId))
            {
                return DomainResult.Failure<ContentCatalogMutation>(
                    DomainErrorCode.DomainInvariantViolation);
            }

            categories.Add(category.Value!);
        }

        var movies = new List<Movie>(catalog.Movies.Items.Count);
        foreach (XtreamMovieInput input in catalog.Movies.Items)
        {
            CategoryId? categoryId = ResolveCategory(
                categoryMap,
                ContentKind.Movie,
                input.CategoryIdentifier);
            DomainResult<Movie> movie = Movie.Create(
                MovieId.Generate(),
                snapshotId,
                categoryId,
                input.ProviderPlaybackKey,
                input.Name,
                input.ContainerExtension,
                input.IsAdultHint);
            if (!movie.IsSuccess)
            {
                return DomainResult.Failure<ContentCatalogMutation>(movie.Error!);
            }

            movies.Add(movie.Value!);
        }

        var series = new List<Series>(catalog.Series.Items.Count);
        foreach (XtreamSeriesInput input in catalog.Series.Items)
        {
            CategoryId? categoryId = ResolveCategory(
                categoryMap,
                ContentKind.Series,
                input.CategoryIdentifier);
            DomainResult<Series> item = Series.Create(
                SeriesId.Generate(),
                snapshotId,
                categoryId,
                input.ProviderKey,
                input.Name,
                input.IsAdultHint);
            if (!item.IsSuccess)
            {
                return DomainResult.Failure<ContentCatalogMutation>(item.Error!);
            }

            series.Add(item.Value!);
        }

        return DomainResult.Success(new ContentCatalogMutation(
            sourceId,
            snapshotId,
            categories,
            movies,
            series,
            [],
            []));
    }

    private static CategoryId? ResolveCategory(
        Dictionary<(ContentKind Kind, string ProviderId), CategoryId> categories,
        ContentKind kind,
        string? providerIdentifier) =>
        providerIdentifier is not null &&
        categories.TryGetValue((kind, providerIdentifier), out CategoryId categoryId)
            ? categoryId
            : null;

    private static string BuildCategoryStableKey(ContentKind kind, string providerIdentifier)
    {
        byte[] material = Encoding.UTF8.GetBytes(providerIdentifier);
        try
        {
            return $"xtream:{kind.ToString().ToLowerInvariant()}:{Convert.ToHexString(SHA256.HashData(material))}";
        }
        finally
        {
            CryptographicOperations.ZeroMemory(material);
        }
    }
}
