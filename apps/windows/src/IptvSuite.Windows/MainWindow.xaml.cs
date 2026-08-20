using IptvSuite.Application;
using Microsoft.UI.Xaml;

namespace IptvSuite.Windows;

public sealed partial class MainWindow : Window
{
    public MainWindow(ICatalogBrowser catalogBrowser, ChannelLogoCache logoCache)
    {
        ArgumentNullException.ThrowIfNull(catalogBrowser);
        ArgumentNullException.ThrowIfNull(logoCache);
        InitializeComponent();
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico"));
        RootFrame.Navigate(typeof(MainPage));
        ((MainPage)RootFrame.Content).Initialize(catalogBrowser, logoCache);
    }
}
