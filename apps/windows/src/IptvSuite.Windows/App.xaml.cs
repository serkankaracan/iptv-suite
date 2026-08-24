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

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        SecretStoreInitializationResult secretStoreInitialization =
            WindowsSecretStoreFactory.Create();
        ISecretStore secretStore = secretStoreInitialization.Store ??
            throw new InvalidOperationException("Protected storage is unavailable.");
        _secretStore = secretStore;
        WindowsCatalogServices catalogServices = WindowsCatalogBrowserFactory.Create();
        MainWindow? window = null;
        try
        {
            window = new MainWindow(catalogServices, secretStore);
            _window = window;
            await window.InitializeAsync();
        }
        catch
        {
            if (window is not null)
            {
                try
                {
                    await window.DisposeAsync();
                }
                catch (Exception exception) when (IsRecoverable(exception))
                {
                }
            }
            else
            {
                try
                {
                    catalogServices.Dispose();
                }
                catch (Exception exception) when (IsRecoverable(exception))
                {
                }
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
