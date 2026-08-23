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
        WindowsCatalogServices catalogServices = WindowsCatalogBrowserFactory.Create();
        try
        {
            _window = new MainWindow(catalogServices);
        }
        catch
        {
            try
            {
                catalogServices.Dispose();
            }
            catch (Exception exception) when (IsRecoverable(exception))
            {
            }

            throw;
        }

        _window.Activate();
    }

    private static bool IsRecoverable(Exception exception) =>
        exception is not OutOfMemoryException and
        not StackOverflowException and
        not AccessViolationException;
}
