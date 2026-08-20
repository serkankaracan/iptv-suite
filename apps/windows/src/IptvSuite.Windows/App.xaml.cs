using IptvSuite.Application;
using Microsoft.UI.Xaml;

namespace IptvSuite.Windows;

public partial class App : Microsoft.UI.Xaml.Application
{
    private ISecretStore? _secretStore;
    private WindowsCatalogServices? _catalogServices;
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
        _catalogServices = WindowsCatalogBrowserFactory.Create();
        _window = new MainWindow(_catalogServices.Browser, _catalogServices.LogoCache);
        _window.Activate();
    }
}
