namespace IptvSuite.ArchitectureTests;

[TestClass]
public sealed class PlaybackPowerLifecycleRulesTests
{
    [TestMethod]
    public void M12SuspendStopsPlaybackWithoutResumeAutoplayAndDrainsBeforeDispose()
    {
        string repositoryRoot = FindRepositoryRoot();
        string window = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "apps",
            "windows",
            "src",
            "IptvSuite.Windows",
            "MainWindow.xaml.cs"));
        string coordinator = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "apps",
            "windows",
            "src",
            "IptvSuite.Application",
            "PlaybackPowerLifecycleCoordinator.cs"));

        StringAssert.Contains(window, "using Microsoft.Windows.System.Power;");
        StringAssert.Contains(
            window,
            "PowerManager.SystemSuspendStatusChanged += PowerManager_SystemSuspendStatusChanged;");
        StringAssert.Contains(
            window,
            "PowerManager.SystemSuspendStatus != SystemSuspendStatus.Entering");
        StringAssert.Contains(window, "await _powerLifecycle.StopForSuspendAsync();");
        StringAssert.Contains(window, "DetachPowerLifecycleEvent();");
        StringAssert.Contains(
            window,
            "PowerManager.SystemSuspendStatusChanged -= PowerManager_SystemSuspendStatusChanged;");

        string handler = window[
            window.IndexOf(
                "private async void PowerManager_SystemSuspendStatusChanged(",
                StringComparison.Ordinal)..
            window.IndexOf(
                "private void MainPage_FullscreenToggleRequested(",
                StringComparison.Ordinal)];
        Assert.IsFalse(handler.Contains("StartAsync", StringComparison.Ordinal));
        Assert.IsFalse(handler.Contains("AutoResume", StringComparison.Ordinal));
        Assert.IsFalse(handler.Contains("ManualResume", StringComparison.Ordinal));

        int disposeEntry = window.IndexOf(
            "public ValueTask DisposeAsync()",
            StringComparison.Ordinal);
        int disposeStart = window.IndexOf(
            "private async Task CompleteDisposeAsync(TaskCompletionSource completion)",
            StringComparison.Ordinal);
        int powerDetach = window.IndexOf(
            "DetachPowerLifecycleEvent();",
            disposeEntry,
            StringComparison.Ordinal);
        int lifetimeLock = window.IndexOf(
            "lock (_lifetimeSync)",
            disposeEntry,
            StringComparison.Ordinal);
        int powerDrain = window.IndexOf(
            "await _powerLifecycle.DisposeAsync();",
            disposeStart,
            StringComparison.Ordinal);
        int playbackDispose = window.IndexOf(
            "await _playback.DisposeAsync();",
            disposeStart,
            StringComparison.Ordinal);
        Assert.IsTrue(disposeEntry >= 0 && powerDetach > disposeEntry);
        Assert.IsTrue(powerDetach < lifetimeLock);
        Assert.IsTrue(disposeStart >= 0 && powerDrain > disposeStart);
        Assert.IsTrue(powerDrain < playbackDispose);

        StringAssert.Contains(coordinator, "_stopPlayback(CancellationToken.None)");
        Assert.IsFalse(coordinator.Contains("StartAsync", StringComparison.Ordinal));
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "IPTVSuite.slnx")) ||
                Directory.Exists(Path.Combine(directory.FullName, ".git")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("Repository root could not be located.");
    }
}
