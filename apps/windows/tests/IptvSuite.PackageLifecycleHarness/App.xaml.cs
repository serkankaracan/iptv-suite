using Microsoft.UI.Xaml;

namespace IptvSuite.PackageLifecycleHarness;

public partial class App : Microsoft.UI.Xaml.Application
{
    public App()
    {
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _ = RunAndExitAsync(args.Arguments);
    }

    private static async Task RunAndExitAsync(string arguments)
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
