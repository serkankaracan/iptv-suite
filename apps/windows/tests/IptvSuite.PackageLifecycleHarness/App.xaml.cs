using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;

namespace IptvSuite.PackageLifecycleHarness;

public partial class App : Microsoft.UI.Xaml.Application
{
    public App()
    {
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        AppActivationArguments? activation = AppInstance.GetCurrent().GetActivatedEventArgs();
        string? arguments =
            activation is not null &&
            activation.Kind is ExtendedActivationKind.Launch &&
            activation.Data is Windows.ApplicationModel.Activation.ILaunchActivatedEventArgs launchArguments
                ? launchArguments.Arguments
                : null;

        _ = RunAndExitAsync(arguments);
    }

    private static async Task RunAndExitAsync(string? arguments)
    {
        int exitCode;

        try
        {
            exitCode = await LifecycleHarnessRunner.RunAsync(arguments).ConfigureAwait(false);
        }
        catch (Exception exception) when (
            exception is not OutOfMemoryException and
            not StackOverflowException and
            not AccessViolationException)
        {
            exitCode = HarnessExitCode.UnexpectedFailure;
        }

        Environment.Exit(exitCode);
    }
}
