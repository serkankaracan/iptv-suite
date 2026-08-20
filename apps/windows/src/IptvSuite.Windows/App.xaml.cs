using IptvSuite.Application;
using Microsoft.UI.Xaml;

namespace IptvSuite.Windows;

public partial class App : Microsoft.UI.Xaml.Application
{
    private ISecretStore? _secretStore;
    private ICatalogBrowser? _catalogBrowser;
    private Window? _window;

    public App()
    {
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        SecretStoreInitializationResult secretStoreInitialization =
            WindowsSecretStoreFactory.Create();
        ISecretStore secretStore = secretStoreInitialization.Store ??
            throw new InvalidOperationException("Protected storage is unavailable.");
        _secretStore = secretStore;
        _catalogBrowser = WindowsCatalogBrowserFactory.Create();
        _window = new MainWindow(_catalogBrowser);
        _window.Activate();
    }
}
