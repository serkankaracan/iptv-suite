using IptvSuite.Application;
using Microsoft.UI.Xaml;

namespace IptvSuite.Windows;

public partial class App : Microsoft.UI.Xaml.Application
{
    private ISecretStore? _secretStore;
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
        _window = new MainWindow();
        _window.Activate();
    }
}
