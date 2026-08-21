using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;
using Windows.Storage;

namespace IptvSuite.NativePlaybackCompatibilitySpike;

public partial class App : Application
{
    private Window? _window;

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        var mainWindow = new MainWindow();
        _window = mainWindow;
        _window.Activate();
        _ = RunProbeAsync(mainWindow);
    }

    private static async Task RunProbeAsync(MainWindow window)
    {
        NativePlaybackProbeResult result;
        try
        {
            AppActivationArguments? activation = AppInstance.GetCurrent().GetActivatedEventArgs();
            string? arguments =
                activation is not null &&
                activation.Kind is ExtendedActivationKind.Launch &&
                activation.Data is Windows.ApplicationModel.Activation.ILaunchActivatedEventArgs launchArguments
                    ? launchArguments.Arguments
                    : null;
            NativePlaybackProbeRequest request = NativePlaybackProbeRequest.Parse(arguments);
            result = await window.RunProbeAsync(request, CancellationToken.None);
        }
        catch (ArgumentException)
        {
            result = NativePlaybackProbeResult.Failed(NativePlaybackFailure.InvalidArguments);
        }
        catch (Exception exception) when (
            exception is not OutOfMemoryException and
            not StackOverflowException and
            not AccessViolationException)
        {
            result = NativePlaybackProbeResult.Failed(NativePlaybackFailure.UnexpectedFailure);
        }

        string evidenceRoot = Path.Combine(ApplicationData.Current.LocalCacheFolder.Path, "M10NativePlayback");
        Directory.CreateDirectory(evidenceRoot);
        string evidencePath = Path.Combine(evidenceRoot, "last-result.json");
        await File.WriteAllTextAsync(evidencePath, result.ToJson(), new System.Text.UTF8Encoding(false));
        window.ShowResult(result);
    }
}
