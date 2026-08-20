using IptvSuite.Application;
using IptvSuite.Infrastructure;
using Microsoft.Windows.Storage;

namespace IptvSuite.Windows;

internal static class WindowsCatalogBrowserFactory
{
    public static ICatalogBrowser Create()
    {
        string catalogRoot = Path.Combine(
            ApplicationData.GetDefault().LocalCachePath,
            "Catalog",
            "v2");
        Directory.CreateDirectory(catalogRoot);
        return new SqliteCatalogQuery(Path.Combine(catalogRoot, "catalog.db"));
    }
}
