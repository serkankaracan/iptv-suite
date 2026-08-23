using IptvSuite.Application;
using IptvSuite.Infrastructure;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;

namespace IptvSuite.Windows;

public sealed partial class MainWindow : Window, IAsyncDisposable
{
    private readonly object _lifetimeSync = new();
    private readonly WindowsCatalogServices _catalogServices;
    private readonly DispatcherQueue _dispatcherQueue;
    private readonly MainPage _mainPage;
    private readonly PlaybackSessionCoordinator _playback;
    private Task? _disposeTask;
    private bool _closeStarted;

    internal MainWindow(WindowsCatalogServices catalogServices)
    {
        _catalogServices = catalogServices ??
            throw new ArgumentNullException(nameof(catalogServices));
        InitializeComponent();
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico"));
        if (!RootFrame.Navigate(typeof(MainPage)) || RootFrame.Content is not MainPage mainPage)
        {
            throw new InvalidOperationException("The application page is unavailable.");
        }

        _mainPage = mainPage;
        _dispatcherQueue = mainPage.DispatcherQueue ??
            throw new InvalidOperationException("The application dispatcher is unavailable.");
        IAsyncDisposable? rollbackOwner = null;
        try
        {
            var resolver = new SqlitePlaybackSourceResolver(_catalogServices.DatabasePath);
            var engine = new WindowsNativePlaybackEngine(
                resolver,
                _mainPage.PlaybackSurfaceElement);
            rollbackOwner = engine;
            var playback = new PlaybackSessionCoordinator(engine);
            rollbackOwner = playback;
            _mainPage.Initialize(
                _catalogServices.Browser,
                _catalogServices.LogoCache,
                playback);
            _playback = playback;
            AppWindow.Closing += AppWindow_Closing;
            rollbackOwner = null;
        }
        catch
        {
            try
            {
                _mainPage.Dispose();
            }
            catch (Exception exception) when (IsRecoverable(exception))
            {
            }

            if (rollbackOwner is not null)
            {
                BeginRollback(rollbackOwner);
            }

            throw;
        }
    }

    private async void AppWindow_Closing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        args.Cancel = true;
        if (_closeStarted)
        {
            return;
        }

        _closeStarted = true;
        try
        {
            await DisposeAsync();
        }
        catch (Exception exception) when (IsRecoverable(exception))
        {
            // The engine already performed its fail-closed cleanup attempt.
        }
        finally
        {
            await RunOnDispatcherAsync(() =>
            {
                AppWindow.Closing -= AppWindow_Closing;
                Close();
            });
        }
    }

    public ValueTask DisposeAsync()
    {
        TaskCompletionSource? completion = null;
        Task disposeTask;
        lock (_lifetimeSync)
        {
            if (_disposeTask is not null)
            {
                return new ValueTask(_disposeTask);
            }

            completion = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            _disposeTask = completion.Task;
            disposeTask = _disposeTask;
        }

        _ = CompleteDisposeAsync(completion);
        return new ValueTask(disposeTask);
    }

    private async Task CompleteDisposeAsync(TaskCompletionSource completion)
    {
        bool cleanupFailed = false;
        try
        {
            try
            {
                await RunOnDispatcherAsync(_mainPage.Dispose);
            }
            catch (Exception exception) when (IsRecoverable(exception))
            {
                cleanupFailed = true;
            }

            try
            {
                await _playback.DisposeAsync();
            }
            catch (Exception exception) when (IsRecoverable(exception))
            {
                cleanupFailed = true;
            }

            try
            {
                await _mainPage.WaitForPendingOperationsAsync();
            }
            catch (Exception exception) when (IsRecoverable(exception))
            {
                cleanupFailed = true;
            }

            try
            {
                _catalogServices.Dispose();
            }
            catch (Exception exception) when (IsRecoverable(exception))
            {
                cleanupFailed = true;
            }

            if (cleanupFailed)
            {
                completion.TrySetException(
                    new InvalidOperationException(
                        "The application window could not be disposed safely."));
            }
            else
            {
                completion.TrySetResult();
            }
        }
        catch (Exception exception)
        {
            completion.TrySetException(exception);
        }
    }

    private async Task RunOnDispatcherAsync(Action operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        if (_dispatcherQueue.HasThreadAccess)
        {
            operation();
            return;
        }

        var completion = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_dispatcherQueue.TryEnqueue(() =>
            {
                try
                {
                    operation();
                    completion.TrySetResult();
                }
                catch (Exception exception)
                {
                    completion.TrySetException(exception);
                }
            }))
        {
            throw new InvalidOperationException("The application dispatcher is unavailable.");
        }

        await completion.Task.ConfigureAwait(false);
    }

    private static void BeginRollback(IAsyncDisposable owner)
    {
        try
        {
            ValueTask rollback = owner.DisposeAsync();
            if (!rollback.IsCompletedSuccessfully)
            {
                _ = ObserveRollbackAsync(rollback);
            }
        }
        catch (Exception exception) when (IsRecoverable(exception))
        {
        }
    }

    private static async Task ObserveRollbackAsync(ValueTask rollback)
    {
        try
        {
            await rollback.ConfigureAwait(false);
        }
        catch (Exception exception) when (IsRecoverable(exception))
        {
        }
    }

    private static bool IsRecoverable(Exception exception) =>
        exception is not OutOfMemoryException and
        not StackOverflowException and
        not AccessViolationException;
}
