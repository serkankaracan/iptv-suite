using IptvSuite.Application;
using IptvSuite.Infrastructure;
using Microsoft.Windows.Storage;

namespace IptvSuite.Windows;

internal static class WindowsCatalogBrowserFactory
{
    public static WindowsCatalogServices Create(ISecretStore secretStore)
    {
        ArgumentNullException.ThrowIfNull(secretStore);
        string catalogRoot = Path.Combine(
            ApplicationData.GetDefault().LocalCachePath,
            "Catalog",
            "v2");
        Directory.CreateDirectory(catalogRoot);
        string databasePath = Path.Combine(catalogRoot, "catalog.db");
        var transport = new BoundedHttpTransport();
        var logoCache = new ChannelLogoCache(new SqliteChannelLogoProvider(databasePath, transport));
        var importer = new SqliteRemotePlaylistCatalogImporter(
            databasePath,
            secretStore,
            transport);
        var onboarding = new RemotePlaylistSourceOnboardingService(
            secretStore,
            transport,
            importer,
            TimeProvider.System);
        return new WindowsCatalogServices(
            new SqliteCatalogQuery(databasePath),
            logoCache,
            onboarding,
            transport,
            databasePath);
    }
}

internal sealed class WindowsCatalogServices(
    ICatalogBrowser browser,
    ChannelLogoCache logoCache,
    RemotePlaylistSourceOnboardingService onboarding,
    BoundedHttpTransport transport,
    string databasePath) : IDisposable
{
    internal ICatalogBrowser Browser { get; } = browser;
    internal ChannelLogoCache LogoCache { get; } = logoCache;
    internal RemotePlaylistSourceOnboardingService Onboarding { get; } = onboarding;
    internal string DatabasePath { get; } = databasePath;

    public void Dispose()
    {
        LogoCache.Dispose();
        transport.Dispose();
        GC.SuppressFinalize(this);
    }
}
