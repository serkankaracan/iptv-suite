using System.Security.Cryptography;
using IptvSuite.Application;
using IptvSuite.Domain;
using IptvSuite.Infrastructure;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.Windows.System.Power;

namespace IptvSuite.Windows;

public sealed partial class MainWindow : Window, IAsyncDisposable
{
    private readonly object _lifetimeSync = new();
    private readonly WindowsCatalogServices _catalogServices;
    private readonly DispatcherQueue _dispatcherQueue;
    private readonly MainPage _mainPage;
    private readonly PlaybackSessionCoordinator _playback;
    private readonly DomainErrorPresenter _domainErrorPresenter;
    private readonly INetworkAvailabilityHintSource _networkAvailabilityHintSource;
    private readonly SourceDeletionCoordinator _sourceDeletion;
    private readonly PlaybackPowerLifecycleCoordinator _powerLifecycle;
    private Task? _disposeTask;
    private Task<SourceDeletionReconciliationResult>? _sourceDeletionStartupTask;
    private SourceDeletionReconciliationResult? _initialSourceDeletionReconciliation;
    private bool _catalogInitialized;
    private volatile bool _closeStarted;
    private int _powerLifecycleSubscribed;

    internal MainWindow(
        WindowsCatalogServices catalogServices,
        ISecretStore secretStore)
    {
        _catalogServices = catalogServices ??
            throw new ArgumentNullException(nameof(catalogServices));
        ArgumentNullException.ThrowIfNull(secretStore);
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
        SourceDeletionCoordinator? rollbackSourceDeletion = null;
        try
        {
            var resolver = new SqlitePlaybackSourceResolver(
                _catalogServices.DatabasePath,
                secretStore);
            var engine = new WindowsNativePlaybackEngine(
                resolver,
                _mainPage.PlaybackSurfaceElement);
            rollbackOwner = engine;
            var playback = new PlaybackSessionCoordinator(
                engine,
                new PlaybackReconnectPolicy(),
                TimeProvider.System,
                CreatePlaybackReconnectJitter);
            rollbackOwner = playback;
            _domainErrorPresenter = new DomainErrorPresenter();
            _networkAvailabilityHintSource =
                new WindowsNetworkAvailabilityHintSource();
            var sourceDeletion = new SourceDeletionCoordinator(
                new SqliteSourceDeletionLifecycle(
                    _catalogServices.DatabasePath,
                    secretStore),
                playback);
            rollbackSourceDeletion = sourceDeletion;
            _playback = playback;
            _sourceDeletion = sourceDeletion;
            _mainPage.ConfigureSourceDeletion(
                RetryPendingSourceCleanupAsync,
                DeleteSourceAsync);
            _mainPage.ConfigureSourceOnboarding(
                _catalogServices.Onboarding.AddAsync);
            _mainPage.FullscreenToggleRequested += MainPage_FullscreenToggleRequested;
            AppWindow.Changed += AppWindow_Changed;
            AppWindow.Closing += AppWindow_Closing;
            _mainPage.SetFullscreenState(
                AppWindow.Presenter.Kind == AppWindowPresenterKind.FullScreen);
            _powerLifecycle = new PlaybackPowerLifecycleCoordinator(playback.StopAsync);
            PowerManager.SystemSuspendStatusChanged += PowerManager_SystemSuspendStatusChanged;
            _powerLifecycleSubscribed = 1;
            rollbackSourceDeletion = null;
            rollbackOwner = null;
        }
        catch
        {
            AppWindow.Changed -= AppWindow_Changed;
            AppWindow.Closing -= AppWindow_Closing;
            _mainPage.FullscreenToggleRequested -= MainPage_FullscreenToggleRequested;
            try
            {
                _mainPage.Dispose();
            }
            catch (Exception exception) when (IsRecoverable(exception))
            {
            }

            if (rollbackSourceDeletion is not null)
            {
                BeginRollback(rollbackSourceDeletion, rollbackOwner);
            }
            else if (rollbackOwner is not null)
            {
                BeginRollback(rollbackOwner);
            }

            throw;
        }
    }

    internal SourceDeletionReconciliationResult? InitialSourceDeletionReconciliation
    {
        get
        {
            lock (_lifetimeSync)
            {
                return _initialSourceDeletionReconciliation;
            }
        }
    }

    internal async Task InitializeAsync()
    {
        await ReconcileThenLoadAsync(retryCompleted: false);
    }

    internal Task<SourceDeletionReconciliationResult> RetryPendingSourceCleanupAsync() =>
        ReconcileThenLoadAsync(retryCompleted: true);

    private ValueTask<SourceDeletionResult> DeleteSourceAsync(
        SourceId sourceId,
        CancellationToken cancellationToken) =>
        _sourceDeletion.DeleteAsync(sourceId, cancellationToken);

    private Task<SourceDeletionReconciliationResult> ReconcileThenLoadAsync(
        bool retryCompleted)
    {
        lock (_lifetimeSync)
        {
            ObjectDisposedException.ThrowIf(_disposeTask is not null, this);
            if (!retryCompleted &&
                _catalogInitialized &&
                _initialSourceDeletionReconciliation is not null)
            {
                return Task.FromResult(_initialSourceDeletionReconciliation);
            }

            if (_sourceDeletionStartupTask is null ||
                (retryCompleted && _sourceDeletionStartupTask.IsCompleted))
            {
                _sourceDeletionStartupTask = ReconcileThenLoadCoreAsync();
            }

            return _sourceDeletionStartupTask;
        }
    }

    private async Task<SourceDeletionReconciliationResult> ReconcileThenLoadCoreAsync()
    {
        SourceDeletionReconciliationResult reconciliation =
            await _sourceDeletion.ReconcilePendingAsync();
        lock (_lifetimeSync)
        {
            _initialSourceDeletionReconciliation = reconciliation;
        }

        if (reconciliation.IsSuccess)
        {
            bool catalogLoaded = false;
            await RunOnDispatcherAsync(async () =>
            {
                if (_closeStarted)
                {
                    return;
                }

                bool catalogInitialized;
                lock (_lifetimeSync)
                {
                    catalogInitialized = _catalogInitialized;
                }

                if (catalogInitialized)
                {
                    await _mainPage.RefreshSourcesAfterSourceCleanupAsync();
                }
                else
                {
                    await _mainPage.InitializeAsync(
                        _catalogServices.Browser,
                        _catalogServices.LogoCache,
                        _playback,
                        _domainErrorPresenter,
                        _networkAvailabilityHintSource);
                }

                catalogLoaded = true;
            });
            if (catalogLoaded)
            {
                lock (_lifetimeSync)
                {
                    _catalogInitialized = true;
                }
            }
        }
        else
        {
            await RunOnDispatcherAsync(_mainPage.ReportPendingSourceCleanup);
        }

        return reconciliation;
    }

    private async void PowerManager_SystemSuspendStatusChanged(object? sender, object args)
    {
        try
        {
            if (PowerManager.SystemSuspendStatus != SystemSuspendStatus.Entering)
            {
                return;
            }

            await _powerLifecycle.StopForSuspendAsync();
        }
        catch (Exception exception) when (IsRecoverable(exception))
        {
            // Closing or native teardown still owns the fail-closed cleanup path.
        }
    }

    private void MainPage_FullscreenToggleRequested(object? sender, EventArgs args)
    {
        if (_closeStarted)
        {
            return;
        }

        try
        {
            AppWindow.SetPresenter(
                AppWindow.Presenter.Kind == AppWindowPresenterKind.FullScreen
                    ? AppWindowPresenterKind.Default
                    : AppWindowPresenterKind.FullScreen);
        }
        catch (Exception exception) when (IsRecoverable(exception))
        {
            _mainPage.ReportFullscreenUnavailable();
        }
    }

    private void AppWindow_Changed(AppWindow sender, AppWindowChangedEventArgs args)
    {
        if (_closeStarted || !args.DidPresenterChange)
        {
            return;
        }

        bool isFullscreen =
            sender.Presenter.Kind == AppWindowPresenterKind.FullScreen;
        AppTitleBar.Visibility = isFullscreen
            ? Visibility.Collapsed
            : Visibility.Visible;
        _mainPage.SetFullscreenState(isFullscreen);
    }

    private async void AppWindow_Closing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        args.Cancel = true;
        if (_closeStarted)
        {
            return;
        }

        _closeStarted = true;
        DetachPowerLifecycleEvent();
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
        _closeStarted = true;
        DetachPowerLifecycleEvent();
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
                await RunOnDispatcherAsync(DetachFullscreenEvents);
                await RunOnDispatcherAsync(_mainPage.Dispose);
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
                await _sourceDeletion.DisposeAsync();
            }
            catch (Exception exception) when (IsRecoverable(exception))
            {
                cleanupFailed = true;
            }

            try
            {
                try
                {
                    await _powerLifecycle.DisposeAsync();
                }
                finally
                {
                    await _playback.DisposeAsync();
                }
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

    private void DetachFullscreenEvents()
    {
        AppWindow.Changed -= AppWindow_Changed;
        _mainPage.FullscreenToggleRequested -= MainPage_FullscreenToggleRequested;
    }

    private void DetachPowerLifecycleEvent()
    {
        if (Interlocked.Exchange(ref _powerLifecycleSubscribed, 0) == 1)
        {
            PowerManager.SystemSuspendStatusChanged -= PowerManager_SystemSuspendStatusChanged;
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

    private async Task RunOnDispatcherAsync(Func<Task> operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        if (_dispatcherQueue.HasThreadAccess)
        {
            await operation();
            return;
        }

        var completion = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_dispatcherQueue.TryEnqueue(async () =>
            {
                try
                {
                    await operation();
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

    private static void BeginRollback(
        IAsyncDisposable first,
        IAsyncDisposable? second)
    {
        _ = ObserveRollbackAsync(first, second);
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

    private static async Task ObserveRollbackAsync(
        IAsyncDisposable first,
        IAsyncDisposable? second)
    {
        try
        {
            await first.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception exception) when (IsRecoverable(exception))
        {
        }

        if (second is null)
        {
            return;
        }

        try
        {
            await second.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception exception) when (IsRecoverable(exception))
        {
        }
    }

    private static bool IsRecoverable(Exception exception) =>
        exception is not OutOfMemoryException and
        not StackOverflowException and
        not AccessViolationException;

    private static TimeSpan CreatePlaybackReconnectJitter(int nextAttemptNumber)
    {
        if (nextAttemptNumber is < 1 or > PlaybackReconnectPolicyOptions.MaximumAllowedAttempts)
        {
            throw new ArgumentOutOfRangeException(nameof(nextAttemptNumber));
        }

        int maximumMilliseconds = checked(
            (int)PlaybackReconnectPolicyOptions.MaximumAllowedJitter.TotalMilliseconds);
        return TimeSpan.FromMilliseconds(
            RandomNumberGenerator.GetInt32(maximumMilliseconds + 1));
    }
}
