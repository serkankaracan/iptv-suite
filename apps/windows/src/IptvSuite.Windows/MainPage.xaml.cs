using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel;

namespace IptvSuite.Windows;

public sealed partial class MainPage : Page
{
    public MainPage()
    {
        InitializeComponent();

        string assemblyVersion = typeof(App).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion ?? "unknown";

#if DEBUG
        const string configuration = "Debug";
#else
        const string configuration = "Release";
#endif

        PackageVersion packageVersion = Package.Current.Id.Version;
        BuildInfoText.Text = $"Assembly {assemblyVersion} · {configuration} · {RuntimeInformation.ProcessArchitecture}";
        PackageInfoText.Text = $"Development package {packageVersion.Major}.{packageVersion.Minor}.{packageVersion.Build}.{packageVersion.Revision}";
    }
}
