using System.Security.Cryptography;
using IptvSuite.Application;
using IptvSuite.Domain;
using IptvSuite.Infrastructure;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Windows.System.Power;

namespace IptvSuite.Windows;

public sealed partial class MainWindow : Window, IAsyncDisposable
{
    private readonly object _lifetimeSync = new();
    private readonly WindowsCatalogServices _catalogServices;
    private readonly DispatcherQueue _dispatcherQueue;
    private readonly HomePage _homePage;
    private readonly MainPage _mainPage;
    private readonly ContentLibraryPage _moviesPage;
    private readonly ContentLibraryPage _seriesPage;
    private readonly SourceManagerPage _sourceManagerPage;
    private readonly Grid _onDemandWorkspace;
    private readonly SemaphoreSlim _navigationGate = new(1, 1);
    private readonly PlaybackSessionCoordinator _playback;
    private readonly DomainErrorPresenter _domainErrorPresenter;
    private readonly WindowsNetworkAvailabilityHintSource _networkAvailabilityHintSource;
    private readonly SourceDeletionCoordinator _sourceDeletion;
    private readonly SourceReplacementCoordinator _sourceReplacement;
    private readonly PlaybackPowerLifecycleCoordinator _powerLifecycle;
    private CancellationTokenSource? _dashboardCountCancellation;
    private Task? _disposeTask;
    private Task<SourceDeletionReconciliationResult>? _sourceDeletionStartupTask;
    private SourceDeletionReconciliationResult? _initialSourceDeletionReconciliation;
    private bool _catalogInitialized;
    private bool _sourceManagerConfigured;
    private ContentLibraryPage? _visibleContentLibrary;
    private AppSection _activeSection = AppSection.Home;
    private long _dashboardCountGeneration;
    private bool _suppressNavigationSelectionChanged;
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
        _homePage = new HomePage();
        _mainPage = new MainPage();
        _moviesPage = new ContentLibraryPage();
        _moviesPage.Configure(
            ContentLibraryKind.Movies,
            _catalogServices.SourceManagement,
            _catalogServices.ContentBrowser);
        _seriesPage = new ContentLibraryPage();
        _seriesPage.Configure(
            ContentLibraryKind.Series,
            _catalogServices.SourceManagement,
            _catalogServices.ContentBrowser,
            LoadSeriesDetailsAsync);
        _sourceManagerPage = new SourceManagerPage();
        _onDemandWorkspace = new Grid
        {
            ColumnSpacing = 16,
        };
        _onDemandWorkspace.ColumnDefinitions.Add(new ColumnDefinition
        {
            Width = new GridLength(2, GridUnitType.Star),
        });
        _onDemandWorkspace.ColumnDefinitions.Add(new ColumnDefinition
        {
            Width = new GridLength(3, GridUnitType.Star),
        });
        _dispatcherQueue = _mainPage.DispatcherQueue ??
            throw new InvalidOperationException("The application dispatcher is unavailable.");
        _homePage.SectionRequested += HomePage_SectionRequested;
        _sourceManagerPage.SourcesChanged += SourceManagerPage_SourcesChanged;
        _moviesPage.PlaybackRequested += ContentLibrary_PlaybackRequested;
        _seriesPage.PlaybackRequested += ContentLibrary_PlaybackRequested;
        PageHost.Content = _homePage;
        SetNavigationSelection(AppSection.Home);
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
            _sourceReplacement = new SourceReplacementCoordinator(playback);
            _mainPage.ConfigureSourceDeletion(
                RetryPendingSourceCleanupAsync,
                DeleteSourceAsync);
            _mainPage.FullscreenToggleRequested += MainPage_FullscreenToggleRequested;
            AppWindow.Changed += AppWindow_Changed;
            AppWindow.Closing += AppWindow_Closing;
            _mainPage.SetFullscreenState(
                AppWindow.Presenter.Kind == AppWindowPresenterKind.FullScreen);
            bool startsFullscreen =
                AppWindow.Presenter.Kind == AppWindowPresenterKind.FullScreen;
            AppNavigation.IsPaneVisible = !startsFullscreen;
            AppNavigation.IsPaneToggleButtonVisible = !startsFullscreen;
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
            _homePage.SectionRequested -= HomePage_SectionRequested;
            _sourceManagerPage.SourcesChanged -= SourceManagerPage_SourcesChanged;
            _moviesPage.PlaybackRequested -= ContentLibrary_PlaybackRequested;
            _seriesPage.PlaybackRequested -= ContentLibrary_PlaybackRequested;
            try
            {
                _sourceManagerPage.Dispose();
                _moviesPage.Dispose();
                _seriesPage.Dispose();
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
        _ = await _catalogServices.ConfigurationRetirement.ReconcileAsync();
        await ReconcileThenLoadAsync(retryCompleted: false);
        await _sourceManagerPage.ConfigureAsync(CreateSourceManagerOperations());
        _sourceManagerConfigured = true;
        await UpdateDashboardCountsAsync();
    }

    private SourceManagerOperations CreateSourceManagerOperations() => new()
    {
        ReadSourcesAsync = cancellationToken =>
            _catalogServices.SourceManagement.ReadAsync(cancellationToken),
        AddRemotePlaylistAsync = AddRemotePlaylistFromManagerAsync,
        AddXtreamAsync = AddXtreamFromManagerAsync,
        RenameAsync = RenameSourceFromManagerAsync,
        RefreshAsync = RefreshSourceFromManagerAsync,
        ReplaceRemotePlaylistAsync = ReplaceRemotePlaylistFromManagerAsync,
        ReplaceXtreamAsync = ReplaceXtreamFromManagerAsync,
        DeleteAsync = DeleteSourceFromManagerAsync,
    };

    private async ValueTask<SourceManagerOperationResult> AddRemotePlaylistFromManagerAsync(
        RemotePlaylistSourceInput input,
        CancellationToken cancellationToken)
    {
        DomainResult<RemotePlaylistSourceOnboardingResult> result = input.AllowInsecureHttp
            ? await _catalogServices.Onboarding.AddAllowingInsecureHttpAsync(
                input.DisplayName,
                input.PlaylistLocator,
                cancellationToken)
            : await _catalogServices.Onboarding.AddAsync(
                input.DisplayName,
                input.PlaylistLocator,
                cancellationToken);
        if (!result.IsSuccess)
        {
            return SourceManagerOperationResult.Failure(
                PresentSourceOperationFailure(result.Error));
        }

        await _mainPage.RefreshSourcesAfterSourceCleanupAsync();
        RemotePlaylistSourceOnboardingResult added = result.Value!;
        return SourceManagerOperationResult.Success(
            added.EntryLimitReached
                ? $"Source added with {added.ImportedChannelCount:N0} usable channels. The safe catalog limit was reached."
                : $"Source added with {added.ImportedChannelCount:N0} usable channels.");
    }

    private async ValueTask<SourceManagerOperationResult> AddXtreamFromManagerAsync(
        XtreamSourceInput input,
        CancellationToken cancellationToken)
    {
        DomainResult<XtreamSourceOnboardingResult> result;
        try
        {
            if (input.UsesM3uBootstrap)
            {
                ValueTask<DomainResult<XtreamSourceOnboardingResult>> pending =
                    input.AllowInsecureHttp
                    ? _catalogServices.XtreamOnboarding.AddFromM3uUrlAllowingInsecureHttpAsync(
                        input.DisplayName,
                        input.ServerLocator,
                        cancellationToken)
                    : _catalogServices.XtreamOnboarding.AddFromM3uUrlAsync(
                        input.DisplayName,
                        input.ServerLocator,
                        cancellationToken);
                input.ClearSensitiveFields();
                result = await pending;
            }
            else
            {
                result = input.AllowInsecureHttp
                    ? await _catalogServices.XtreamOnboarding.AddAllowingInsecureHttpAsync(
                        input.DisplayName,
                        input.ServerLocator,
                        input.Username,
                        input.Password,
                        cancellationToken)
                    : await _catalogServices.XtreamOnboarding.AddAsync(
                        input.DisplayName,
                        input.ServerLocator,
                        input.Username,
                        input.Password,
                        cancellationToken);
            }
        }
        finally
        {
            input.ClearSensitiveFields();
        }
        if (!result.IsSuccess)
        {
            return SourceManagerOperationResult.Failure(
                PresentSourceOperationFailure(result.Error));
        }

        await _mainPage.RefreshSourcesAfterSourceCleanupAsync();
        ContentCatalogCounts counts = result.Value!.Counts;
        return SourceManagerOperationResult.Success(
            $"Source added: {counts.LiveTvCount:N0} live, {counts.MovieCount:N0} movies and {counts.SeriesCount:N0} series.");
    }

    private async ValueTask<SourceManagerOperationResult> RenameSourceFromManagerAsync(
        SourceId sourceId,
        string displayName,
        CancellationToken cancellationToken)
    {
        DomainResult<SourceManagementSummary> result =
            await _catalogServices.SourceManagement.RenameAsync(
                sourceId,
                displayName,
                TimeProvider.System.GetUtcNow(),
                cancellationToken);
        return result.IsSuccess
            ? SourceManagerOperationResult.Success("Source renamed.")
            : SourceManagerOperationResult.Failure(
                PresentSourceOperationFailure(result.Error));
    }

    private async ValueTask<SourceManagerOperationResult> RefreshSourceFromManagerAsync(
        SourceId sourceId,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<SourceManagementSummary> sources =
            await _catalogServices.SourceManagement.ReadAsync(cancellationToken);
        SourceManagementSummary? selected = null;
        foreach (SourceManagementSummary source in sources)
        {
            if (source.SourceId.Equals(sourceId))
            {
                selected = source;
                break;
            }
        }

        if (selected is null)
        {
            return SourceManagerOperationResult.Failure("The source is unavailable.");
        }

        ISourceRefreshCoordinator refresh = selected.Kind == SourceKind.XtreamCompatible
            ? _catalogServices.SourceRefresh
            : _catalogServices.RemoteSourceRefresh;
        DomainResult<ContentCatalogCounts> result =
            await refresh.RefreshFromStoredConfigurationAsync(
                sourceId,
                cancellationToken);
        if (!result.IsSuccess)
        {
            return SourceManagerOperationResult.Failure(
                PresentSourceOperationFailure(result.Error));
        }

        await _mainPage.RefreshSourcesAfterSourceCleanupAsync();
        ContentCatalogCounts counts = result.Value!;
        return SourceManagerOperationResult.Success(
            $"Source refreshed: {counts.LiveTvCount:N0} live, {counts.MovieCount:N0} movies and {counts.SeriesCount:N0} series.");
    }

    private async ValueTask<SourceManagerOperationResult> ReplaceRemotePlaylistFromManagerAsync(
        SourceId sourceId,
        RemotePlaylistSourceInput input,
        CancellationToken cancellationToken)
    {
        DomainResult<RemotePlaylistSourceOnboardingResult>? replacement = null;
        DomainResult<bool> coordinated;
        try
        {
            coordinated = await _sourceReplacement.ReplaceAsync(
                sourceId,
                async token =>
                {
                    await PrepareSourceMutationAsync(sourceId, "Replacing the selected source.");
                    DomainResult<ContentSource> current =
                        await _catalogServices.SourceManagement.ReadConfigurationAsync(
                            sourceId,
                            token);
                    if (!current.IsSuccess)
                    {
                        return DomainResult.Failure<bool>(current.Error!);
                    }

                    replacement = input.AllowInsecureHttp
                        ? await _catalogServices.Onboarding.ReplaceAllowingInsecureHttpAsync(
                            current.Value!,
                            input.DisplayName,
                            input.PlaylistLocator,
                            token)
                        : await _catalogServices.Onboarding.ReplaceAsync(
                            current.Value!,
                            input.DisplayName,
                            input.PlaylistLocator,
                            token);
                    return replacement.IsSuccess
                        ? DomainResult.Success(true)
                        : DomainResult.Failure<bool>(replacement.Error!);
                },
                cancellationToken);
        }
        finally
        {
            await TryRefreshCatalogAfterSourceMutationAsync();
        }

        if (!coordinated.IsSuccess || replacement is null || !replacement.IsSuccess)
        {
            return SourceManagerOperationResult.Failure(
                PresentSourceOperationFailure(coordinated.Error ?? replacement?.Error));
        }

        RemotePlaylistSourceOnboardingResult replaced = replacement.Value!;
        SourceConfigurationRetirementReconciliationResult cleanup =
            await _catalogServices.ConfigurationRetirement.ReconcileAsync(
                CancellationToken.None);
        return SourceManagerOperationResult.Success(
            cleanup.HasRemaining
                ? "Source replaced under the same identity, but the previous protected configuration still needs cleanup."
                : replaced.EntryLimitReached
                    ? $"Source replaced with {replaced.ImportedChannelCount:N0} usable channels. The safe catalog limit was reached."
                    : $"Source replaced with {replaced.ImportedChannelCount:N0} usable channels.");
    }

    private async ValueTask<SourceManagerOperationResult> ReplaceXtreamFromManagerAsync(
        SourceId sourceId,
        XtreamSourceInput input,
        CancellationToken cancellationToken)
    {
        DomainResult<XtreamSourceOnboardingResult>? replacement = null;
        DomainResult<bool> coordinated;
        try
        {
            coordinated = await _sourceReplacement.ReplaceAsync(
                sourceId,
                async token =>
                {
                    await PrepareSourceMutationAsync(sourceId, "Replacing the selected source.");
                    DomainResult<ContentSource> current =
                        await _catalogServices.SourceManagement.ReadConfigurationAsync(
                            sourceId,
                            token);
                    if (!current.IsSuccess)
                    {
                        return DomainResult.Failure<bool>(current.Error!);
                    }

                    if (input.UsesM3uBootstrap)
                    {
                        ValueTask<DomainResult<XtreamSourceOnboardingResult>> pending =
                            input.AllowInsecureHttp
                            ? _catalogServices.XtreamOnboarding.ReplaceFromM3uUrlAllowingInsecureHttpAsync(
                                    current.Value!,
                                    input.DisplayName,
                                    input.ServerLocator,
                                    token)
                            : _catalogServices.XtreamOnboarding.ReplaceFromM3uUrlAsync(
                                current.Value!,
                                input.DisplayName,
                                input.ServerLocator,
                                token);
                        input.ClearSensitiveFields();
                        replacement = await pending;
                    }
                    else
                    {
                        replacement = input.AllowInsecureHttp
                            ? await _catalogServices.XtreamOnboarding.ReplaceAllowingInsecureHttpAsync(
                                current.Value!,
                                input.DisplayName,
                                input.ServerLocator,
                                input.Username,
                                input.Password,
                                token)
                            : await _catalogServices.XtreamOnboarding.ReplaceAsync(
                                current.Value!,
                                input.DisplayName,
                                input.ServerLocator,
                                input.Username,
                                input.Password,
                                token);
                    }
                    return replacement.IsSuccess
                        ? DomainResult.Success(true)
                        : DomainResult.Failure<bool>(replacement.Error!);
                },
                cancellationToken);
        }
        finally
        {
            input.ClearSensitiveFields();
            await TryRefreshCatalogAfterSourceMutationAsync();
        }

        if (!coordinated.IsSuccess || replacement is null || !replacement.IsSuccess)
        {
            return SourceManagerOperationResult.Failure(
                PresentSourceOperationFailure(coordinated.Error ?? replacement?.Error));
        }

        XtreamSourceOnboardingResult replaced = replacement.Value!;
        ContentCatalogCounts counts = replaced.Counts;
        SourceConfigurationRetirementReconciliationResult cleanup =
            await _catalogServices.ConfigurationRetirement.ReconcileAsync(
                CancellationToken.None);
        return SourceManagerOperationResult.Success(
            cleanup.HasRemaining
                ? "Source replaced under the same identity, but the previous protected configuration still needs cleanup."
                : $"Source replaced: {counts.LiveTvCount:N0} live, {counts.MovieCount:N0} movies and {counts.SeriesCount:N0} series.");
    }

    private async ValueTask<SourceManagerOperationResult> DeleteSourceFromManagerAsync(
        SourceId sourceId,
        CancellationToken cancellationToken)
    {
        SourceDeletionResult result;
        bool deletionInvoked = false;
        try
        {
            await PrepareSourceMutationAsync(sourceId, "Deleting the selected source.");
            deletionInvoked = true;
            result = await DeleteSourceAsync(sourceId, cancellationToken);

            if (!result.IsSuccess)
            {
                if (result.FailureStage == SourceDeletionFailureStage.MarkPending)
                {
                    await _mainPage.RestoreCatalogAfterUncommittedDeletionFailureAsync();
                }
                else
                {
                    _mainPage.ReportPendingSourceCleanup();
                }
            }
            else
            {
                await TryRefreshCatalogAfterSourceMutationAsync();
            }
        }
        catch
        {
            if (deletionInvoked)
            {
                _mainPage.ReportPendingSourceCleanup();
            }
            else
            {
                await _mainPage.RestoreCatalogAfterUncommittedDeletionFailureAsync();
            }

            throw;
        }

        if (!result.IsSuccess)
        {
            return SourceManagerOperationResult.Failure(
                PresentSourceOperationFailure(result.Error));
        }

        return SourceManagerOperationResult.Success("Source deleted.");
    }

    private async ValueTask PrepareSourceMutationAsync(
        SourceId sourceId,
        string operationStatus)
    {
        await _mainPage.PrepareSourceMutationAsync(sourceId, operationStatus);
        _catalogServices.LogoCache.EvictSource(sourceId);
    }

    private async Task TryRefreshCatalogAfterSourceMutationAsync()
    {
        try
        {
            await _mainPage.RefreshSourcesAfterSourceCleanupAsync();
        }
        catch (Exception exception) when (IsRecoverable(exception))
        {
            _mainPage.ReportPendingSourceCleanup();
        }
    }

    private string PresentSourceOperationFailure(DomainError? error)
    {
        DomainError safeError = error ??
            DomainError.Create(DomainErrorCode.DomainInvariantViolation);
        return _domainErrorPresenter.Present(
            safeError,
            _networkAvailabilityHintSource.ReadCurrent()).Message;
    }

    private async Task UpdateDashboardCountsAsync()
    {
        if (_closeStarted)
        {
            return;
        }

        long generation = Interlocked.Increment(ref _dashboardCountGeneration);
        var cancellation = new CancellationTokenSource();
        CancellationTokenSource? previous = Interlocked.Exchange(
            ref _dashboardCountCancellation,
            cancellation);
        previous?.Cancel();
        try
        {
            IReadOnlyList<SourceManagementSummary> sources =
                await _catalogServices.SourceManagement.ReadAsync(cancellation.Token);
            long liveCount = 0;
            long movieCount = 0;
            long seriesCount = 0;
            foreach (SourceManagementSummary source in sources)
            {
                liveCount = checked(liveCount + source.Counts.LiveTvCount);
                movieCount = checked(movieCount + source.Counts.MovieCount);
                seriesCount = checked(seriesCount + source.Counts.SeriesCount);
            }

            if (!_closeStarted &&
                generation == Volatile.Read(ref _dashboardCountGeneration))
            {
                _homePage.SetCounts(liveCount, movieCount, seriesCount);
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (IsRecoverable(exception))
        {
            // Keep the last authoritative totals visible when a refresh fails.
        }
        finally
        {
            _ = Interlocked.CompareExchange(
                ref _dashboardCountCancellation,
                null,
                cancellation);
            cancellation.Dispose();
        }
    }

    private async void HomePage_SectionRequested(
        object? sender,
        AppSectionRequestedEventArgs args)
    {
        await NavigateSafelyAsync(args.Section, updateNavigationSelection: true);
    }

    private async void SourceManagerPage_SourcesChanged(object? sender, EventArgs args)
    {
        try
        {
            await _mainPage.RefreshSourcesAfterSourceCleanupAsync();
            await _moviesPage.RefreshAsync();
            await _seriesPage.RefreshAsync();
            await UpdateDashboardCountsAsync();
        }
        catch (Exception exception) when (IsRecoverable(exception))
        {
        }
    }

    private async void AppNavigation_SelectionChanged(
        NavigationView sender,
        NavigationViewSelectionChangedEventArgs args)
    {
        if (_suppressNavigationSelectionChanged ||
            args.SelectedItemContainer?.Tag is not string tag ||
            !Enum.TryParse(tag, ignoreCase: false, out AppSection section))
        {
            return;
        }

        await NavigateSafelyAsync(section, updateNavigationSelection: false);
    }

    private Task SelectSectionAsync(AppSection section) =>
        NavigateSafelyAsync(section, updateNavigationSelection: true);

    private async Task NavigateSafelyAsync(
        AppSection section,
        bool updateNavigationSelection)
    {
        if (_closeStarted || !Enum.IsDefined(section))
        {
            return;
        }

        if (section == _activeSection && IsSectionPresented(section))
        {
            if (updateNavigationSelection)
            {
                SetNavigationSelection(section);
            }

            return;
        }

        await _navigationGate.WaitAsync();
        try
        {
            if (section == _activeSection && IsSectionPresented(section))
            {
                if (updateNavigationSelection)
                {
                    SetNavigationSelection(section);
                }

                return;
            }

            await NavigateToSectionAsync(section, updateNavigationSelection);
        }
        catch (Exception exception) when (IsRecoverable(exception))
        {
            // The current page remains authoritative when navigation cannot complete.
        }
        finally
        {
            _navigationGate.Release();
        }
    }

    private async Task NavigateToSectionAsync(
        AppSection section,
        bool updateNavigationSelection)
    {
        if (_closeStarted || !Enum.IsDefined(section))
        {
            return;
        }

        bool leavingPlaybackSection = _activeSection is
            AppSection.LiveTv or AppSection.Movies or AppSection.Series;
        if (leavingPlaybackSection && section != _activeSection)
        {
            await _playback.StopAsync();
        }

        DetachOnDemandWorkspace();
        _activeSection = section;
        if (section is AppSection.Movies or AppSection.Series)
        {
            AttachOnDemandWorkspace(
                section == AppSection.Movies ? _moviesPage : _seriesPage);
        }
        else
        {
            _mainPage.SetOnDemandWorkspace(enabled: false);
            PageHost.Content = section switch
            {
                AppSection.Home => _homePage,
                AppSection.LiveTv => _mainPage,
                AppSection.Sources => _sourceManagerPage,
                _ => _homePage,
            };
        }

        if (updateNavigationSelection)
        {
            SetNavigationSelection(section);
        }

        if (section == AppSection.Home)
        {
            await UpdateDashboardCountsAsync();
        }
        else if (section == AppSection.Sources && _sourceManagerConfigured)
        {
            await _sourceManagerPage.RefreshAsync();
        }
        else if (section == AppSection.Movies)
        {
            await _moviesPage.RefreshAsync();
        }
        else if (section == AppSection.Series)
        {
            await _seriesPage.RefreshAsync();
        }
    }

    private void AttachOnDemandWorkspace(ContentLibraryPage library)
    {
        if (ReferenceEquals(PageHost.Content, _mainPage))
        {
            PageHost.Content = null;
        }

        library.Activate();
        _visibleContentLibrary = library;
        _onDemandWorkspace.Children.Clear();
        Grid.SetColumn(library, 0);
        Grid.SetColumnSpan(library, 1);
        _onDemandWorkspace.Children.Add(library);
        Grid.SetColumn(_mainPage, 1);
        Grid.SetColumnSpan(_mainPage, 1);
        _onDemandWorkspace.Children.Add(_mainPage);
        _mainPage.SetOnDemandWorkspace(enabled: true);
        PageHost.Content = _onDemandWorkspace;
        UpdateOnDemandFullscreenLayout(
            AppWindow.Presenter.Kind == AppWindowPresenterKind.FullScreen);
    }

    private void DetachOnDemandWorkspace()
    {
        _visibleContentLibrary?.Deactivate();
        if (ReferenceEquals(PageHost.Content, _onDemandWorkspace))
        {
            PageHost.Content = null;
        }

        _onDemandWorkspace.Children.Clear();
        _visibleContentLibrary = null;
    }

    private bool IsSectionPresented(AppSection section) => section switch
    {
        AppSection.Home => ReferenceEquals(PageHost.Content, _homePage),
        AppSection.LiveTv => ReferenceEquals(PageHost.Content, _mainPage),
        AppSection.Movies => ReferenceEquals(PageHost.Content, _onDemandWorkspace) &&
            ReferenceEquals(_visibleContentLibrary, _moviesPage),
        AppSection.Series => ReferenceEquals(PageHost.Content, _onDemandWorkspace) &&
            ReferenceEquals(_visibleContentLibrary, _seriesPage),
        AppSection.Sources => ReferenceEquals(PageHost.Content, _sourceManagerPage),
        _ => false,
    };

    private void SetNavigationSelection(AppSection section)
    {
        object selectedItem = section switch
        {
            AppSection.Home => HomeNavigationItem,
            AppSection.LiveTv => LiveTvNavigationItem,
            AppSection.Movies => MoviesNavigationItem,
            AppSection.Series => SeriesNavigationItem,
            AppSection.Sources => SourcesNavigationItem,
            _ => HomeNavigationItem,
        };
        if (ReferenceEquals(AppNavigation.SelectedItem, selectedItem))
        {
            return;
        }

        _suppressNavigationSelectionChanged = true;
        try
        {
            AppNavigation.SelectedItem = selectedItem;
        }
        finally
        {
            _suppressNavigationSelectionChanged = false;
        }
    }

    private void UpdateOnDemandFullscreenLayout(bool isFullscreen)
    {
        if (_visibleContentLibrary is null ||
            !_onDemandWorkspace.Children.Contains(_mainPage))
        {
            return;
        }

        _visibleContentLibrary.Visibility = isFullscreen
            ? Visibility.Collapsed
            : Visibility.Visible;
        Grid.SetColumn(_mainPage, isFullscreen ? 0 : 1);
        Grid.SetColumnSpan(_mainPage, isFullscreen ? 2 : 1);
    }

    private async void ContentLibrary_PlaybackRequested(
        object? sender,
        ContentPlaybackRequestedEventArgs args)
    {
        if (_closeStarted || !ReferenceEquals(sender, _visibleContentLibrary))
        {
            return;
        }

        try
        {
            if (args.MovieId is MovieId movieId)
            {
                await _mainPage.PlayMovieAsync(
                    args.SourceId,
                    movieId,
                    args.DisplayName);
            }
            else if (args.EpisodeId is EpisodeId episodeId)
            {
                await _mainPage.PlayEpisodeAsync(
                    args.SourceId,
                    episodeId,
                    args.DisplayName);
            }
        }
        catch (Exception exception) when (IsRecoverable(exception))
        {
        }
    }

    private async ValueTask<bool> LoadSeriesDetailsAsync(
        SourceId sourceId,
        SeriesId seriesId,
        CancellationToken cancellationToken)
    {
        DomainResult<SeriesDetailRefreshResult> result =
            await _catalogServices.SeriesDetailRefresh.RefreshAsync(
                sourceId,
                seriesId,
                cancellationToken);
        return result.IsSuccess;
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
        AppNavigation.IsPaneVisible = !isFullscreen;
        AppNavigation.IsPaneToggleButtonVisible = !isFullscreen;
        UpdateOnDemandFullscreenLayout(isFullscreen);
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
        CancelDashboardCountRequest();
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
        CancelDashboardCountRequest();
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
                await RunOnDispatcherAsync(DetachWindowEvents);
                await RunOnDispatcherAsync(_sourceManagerPage.Dispose);
                await RunOnDispatcherAsync(_moviesPage.Dispose);
                await RunOnDispatcherAsync(_seriesPage.Dispose);
                await RunOnDispatcherAsync(_mainPage.Dispose);
            }
            catch (Exception exception) when (IsRecoverable(exception))
            {
                cleanupFailed = true;
            }

            try
            {
                await _mainPage.WaitForPendingOperationsAsync();
                await _sourceManagerPage.WaitForPendingOperationsAsync();
                await _moviesPage.WaitForPendingOperationsAsync();
                await _seriesPage.WaitForPendingOperationsAsync();
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
                await _sourceReplacement.DisposeAsync();
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

    private void DetachWindowEvents()
    {
        CancelDashboardCountRequest();
        AppWindow.Changed -= AppWindow_Changed;
        _mainPage.FullscreenToggleRequested -= MainPage_FullscreenToggleRequested;
        _homePage.SectionRequested -= HomePage_SectionRequested;
        _sourceManagerPage.SourcesChanged -= SourceManagerPage_SourcesChanged;
        _moviesPage.PlaybackRequested -= ContentLibrary_PlaybackRequested;
        _seriesPage.PlaybackRequested -= ContentLibrary_PlaybackRequested;
    }

    private void CancelDashboardCountRequest()
    {
        Interlocked.Increment(ref _dashboardCountGeneration);
        Volatile.Read(ref _dashboardCountCancellation)?.Cancel();
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
