using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;
using System.Text.Json;
using System.Text.Json.Serialization;
using Windows.ApplicationModel;
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
        NativePlaybackProbeRequest request;
        NativePlaybackProbeResult result;
        try
        {
            AppActivationArguments? activation =
                Microsoft.Windows.AppLifecycle.AppInstance.GetCurrent().GetActivatedEventArgs();
            string? arguments =
                activation is not null &&
                activation.Kind is ExtendedActivationKind.Launch &&
                activation.Data is Windows.ApplicationModel.Activation.ILaunchActivatedEventArgs launchArguments
                    ? launchArguments.Arguments
                    : null;
            request = NativePlaybackProbeRequest.Parse(arguments);
        }
        catch (ArgumentException)
        {
            result = NativePlaybackProbeResult.Failed(NativePlaybackFailure.InvalidArguments);
            window.ShowResult(result);
            return;
        }

        NativePlaybackRuntimeDependency? runtimeDependency;
        try
        {
            runtimeDependency = NativePlaybackRuntimeDependency.Capture();
        }
        catch (Exception exception) when (
            exception is not OutOfMemoryException and
            not StackOverflowException and
            not AccessViolationException)
        {
            runtimeDependency = null;
            result = NativePlaybackProbeResult.Failed(NativePlaybackFailure.RuntimeDependencyResolutionFailed);
            await PublishEvidenceAsync(request, runtimeDependency, result);
            window.ShowResult(result);
            return;
        }

        try
        {
            result = await window.RunProbeAsync(request, CancellationToken.None);
        }
        catch (Exception exception) when (
            exception is not OutOfMemoryException and
            not StackOverflowException and
            not AccessViolationException)
        {
            result = NativePlaybackProbeResult.Failed(NativePlaybackFailure.UnexpectedFailure);
        }

        await PublishEvidenceAsync(request, runtimeDependency, result);
        window.ShowResult(result);
    }

    private static async Task PublishEvidenceAsync(
        NativePlaybackProbeRequest request,
        NativePlaybackRuntimeDependency? runtimeDependency,
        NativePlaybackProbeResult result)
    {
        var evidence = new NativePlaybackProbeEnvelope(
            1,
            request.RunId.ToString("N"),
            runtimeDependency,
            result);
        string evidenceRoot = Path.Combine(ApplicationData.Current.LocalCacheFolder.Path, "M10NativePlayback");
        Directory.CreateDirectory(evidenceRoot);
        string evidencePath = Path.Combine(evidenceRoot, $"result-{request.RunId:N}.json");
        string pendingEvidencePath = Path.Combine(evidenceRoot, $"result-{request.RunId:N}.pending");
        try
        {
            byte[] bytes = new System.Text.UTF8Encoding(false).GetBytes(evidence.ToJson());
            await using (var stream = new FileStream(
                pendingEvidencePath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                4096,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(bytes);
                await stream.FlushAsync();
                stream.Flush(flushToDisk: true);
            }
            File.Move(pendingEvidencePath, evidencePath, overwrite: false);
        }
        finally
        {
            if (File.Exists(pendingEvidencePath)) File.Delete(pendingEvidencePath);
        }
    }
}

internal sealed record NativePlaybackProbeEnvelope(
    int SchemaVersion,
    string RunId,
    NativePlaybackRuntimeDependency? RuntimeDependency,
    NativePlaybackProbeResult Probe)
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    internal string ToJson() => JsonSerializer.Serialize(this, SerializerOptions);
}

internal sealed record NativePlaybackRuntimeDependency(
    string Name,
    string Version,
    string Architecture,
    string PublisherId,
    bool IsFramework)
{
    internal static NativePlaybackRuntimeDependency Capture()
    {
        Package[] matches = Package.Current.Dependencies
            .Where(package => package.Id.Name == "Microsoft.WindowsAppRuntime.2")
            .ToArray();
        if (matches is not [Package dependency] ||
            dependency.Id.Architecture.ToString() != "X64" ||
            dependency.Id.PublisherId != "8wekyb3d8bbwe" ||
            !string.IsNullOrEmpty(dependency.Id.ResourceId) ||
            !dependency.IsFramework)
        {
            throw new InvalidOperationException("The resolved Windows App Runtime dependency is outside policy.");
        }

        PackageVersion version = dependency.Id.Version;
        return new NativePlaybackRuntimeDependency(
            dependency.Id.Name,
            FormattableString.Invariant($"{version.Major}.{version.Minor}.{version.Build}.{version.Revision}"),
            dependency.Id.Architecture.ToString(),
            dependency.Id.PublisherId,
            dependency.IsFramework);
    }
}
