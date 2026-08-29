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
            importer,
            TimeProvider.System);
        var xtreamImport = new XtreamCatalogImportService(
            databasePath,
            secretStore,
            transport);
        var xtreamOnboarding = new XtreamSourceOnboardingService(
            secretStore,
            xtreamImport,
            TimeProvider.System);
        var remoteRefresh = new RemotePlaylistCatalogRefreshService(
            databasePath,
            secretStore,
            transport,
            TimeProvider.System);
        var seriesDetailRefresh = new XtreamSeriesDetailService(
            databasePath,
            secretStore,
            transport);
        var configurationRetirement =
            new SqliteSourceConfigurationRetirementReconciler(
                databasePath,
                secretStore);
        return new WindowsCatalogServices(
            new SqliteCatalogQuery(databasePath),
            new SqliteContentCatalog(databasePath),
            new SqliteSourceManagementCatalog(databasePath),
            logoCache,
            onboarding,
            xtreamOnboarding,
            xtreamImport,
            remoteRefresh,
            seriesDetailRefresh,
            configurationRetirement,
            transport,
            databasePath);
    }
}

internal sealed class WindowsCatalogServices(
    ICatalogBrowser browser,
    IContentCatalogBrowser contentBrowser,
    ISourceManagementCatalog sourceManagement,
    ChannelLogoCache logoCache,
    RemotePlaylistSourceOnboardingService onboarding,
    XtreamSourceOnboardingService xtreamOnboarding,
    ISourceRefreshCoordinator sourceRefresh,
    ISourceRefreshCoordinator remoteSourceRefresh,
    ISeriesDetailRefreshCoordinator seriesDetailRefresh,
    ISourceConfigurationRetirementReconciler configurationRetirement,
    BoundedHttpTransport transport,
    string databasePath) : IDisposable
{
    internal ICatalogBrowser Browser { get; } = browser;
    internal IContentCatalogBrowser ContentBrowser { get; } = contentBrowser;
    internal ISourceManagementCatalog SourceManagement { get; } = sourceManagement;
    internal ChannelLogoCache LogoCache { get; } = logoCache;
    internal RemotePlaylistSourceOnboardingService Onboarding { get; } = onboarding;
    internal XtreamSourceOnboardingService XtreamOnboarding { get; } = xtreamOnboarding;
    internal ISourceRefreshCoordinator SourceRefresh { get; } = sourceRefresh;
    internal ISourceRefreshCoordinator RemoteSourceRefresh { get; } = remoteSourceRefresh;
    internal ISeriesDetailRefreshCoordinator SeriesDetailRefresh { get; } = seriesDetailRefresh;
    internal ISourceConfigurationRetirementReconciler ConfigurationRetirement { get; } =
        configurationRetirement;
    internal string DatabasePath { get; } = databasePath;

    public void Dispose()
    {
        LogoCache.Dispose();
        transport.Dispose();
        GC.SuppressFinalize(this);
    }
}
