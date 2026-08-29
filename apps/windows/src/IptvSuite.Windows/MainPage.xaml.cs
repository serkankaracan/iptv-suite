using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Reflection;
using System.Runtime.InteropServices;
using IptvSuite.Application;
using IptvSuite.Domain;
using IptvSuite.Infrastructure;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Input;
using Windows.ApplicationModel;
using Windows.Foundation;
using Windows.Storage.Streams;
using Windows.System;
using Windows.UI.Core;
using DispatcherQueueTimer = Microsoft.UI.Dispatching.DispatcherQueueTimer;

namespace IptvSuite.Windows;

public sealed partial class MainPage : Page, IDisposable
{
    private const int PageSize = 200;
    private const int VolumeStep = 5;
    private static readonly TimeSpan FullscreenControlsAutoHideDelay =
        TimeSpan.FromSeconds(3);
    private const string InsecureHttpCatalogWarning =
        "Warning: cleartext HTTP traffic is unencrypted and can be observed or modified in transit (MITM).";
    private readonly CancellationTokenSource _lifetime = new();
    private readonly object _catalogOperationSync = new();
    private readonly object _operationSync = new();
    private readonly SemaphoreSlim _playbackControlGate = new(1, 1);
    private readonly DispatcherQueueTimer _fullscreenControlsAutoHideTimer;
    private readonly PointerEventHandler _fullscreenPointerMovedHandler;
    private readonly KeyEventHandler _fullscreenKeyDownHandler;
    private readonly PointerEventHandler _timelinePointerPressedHandler;
    private readonly PointerEventHandler _timelinePointerReleasedHandler;
    private CatalogBrowseCoordinator? _coordinator;
    private ChannelLogoCache? _logoCache;
    private PlaybackSessionCoordinator? _playback;
    private DomainErrorPresenter? _domainErrorPresenter;
    private INetworkAvailabilityHintSource? _networkAvailabilityHintSource;
    private Func<Task<SourceDeletionReconciliationResult>>? _retryPendingSourceCleanup;
    private Func<SourceId, CancellationToken, ValueTask<SourceDeletionResult>>? _deleteSource;
    private ChannelRow? _playbackChannel;
    private string? _playbackItemName;
    private ContentDialog? _sourceDeletionDialog;
    private CancellationTokenSource _logoPageCancellation = new();
    private int _offset;
    private bool _updatingSelectors;
    private bool _disposed;
    private bool _catalogAdmissionReady;
    private bool _movingTabFocus;
    private bool _isFullscreen;
    private bool _fullscreenTransitionPending;
    private bool _onDemandWorkspace;
    private bool _timelinePointerInteractionActive;
    private PlaybackSessionId _timelinePointerSessionId;
    private int _timelineSeekInProgress;
    private WeakReference<Control>? _focusBeforeFullscreen;
    private long _loadingGeneration;
    private int _activeCatalogOperations;
    private int _activeAsyncOperations;
    private TaskCompletionSource? _catalogOperationsDrained;
    private TaskCompletionSource? _operationsDrained;
    private bool _sourceDeletionOperationPending;
    private bool _playbackControlGateDisposed;
    private PlaybackSessionSnapshot? _presentedFailureSnapshot;
    private DomainErrorPresentation? _presentedFailure;

    public MainPage()
    {
        InitializeComponent();
        _fullscreenControlsAutoHideTimer = DispatcherQueue.CreateTimer();
        _fullscreenControlsAutoHideTimer.Interval = FullscreenControlsAutoHideDelay;
        _fullscreenControlsAutoHideTimer.IsRepeating = false;
        _fullscreenControlsAutoHideTimer.Tick +=
            FullscreenControlsAutoHideTimer_Tick;
        _fullscreenPointerMovedHandler = FullscreenSurface_PointerMoved;
        _fullscreenKeyDownHandler = FullscreenSurface_KeyDown;
        _timelinePointerPressedHandler = PlaybackTimelineSlider_PointerPressed;
        _timelinePointerReleasedHandler = PlaybackTimelineSlider_PointerReleased;
        PageRoot.AddHandler(
            UIElement.PointerMovedEvent,
            _fullscreenPointerMovedHandler,
            handledEventsToo: true);
        PageRoot.AddHandler(
            UIElement.KeyDownEvent,
            _fullscreenKeyDownHandler,
            handledEventsToo: true);
        PageRoot.GettingFocus += FullscreenSurface_GettingFocus;
        PlaybackTimelineSlider.AddHandler(
            UIElement.PointerPressedEvent,
            _timelinePointerPressedHandler,
            handledEventsToo: true);
        PlaybackTimelineSlider.AddHandler(
            UIElement.PointerReleasedEvent,
            _timelinePointerReleasedHandler,
            handledEventsToo: true);
        AddHandler(
            UIElement.LosingFocusEvent,
            new TypedEventHandler<UIElement, LosingFocusEventArgs>(CatalogFilter_LosingFocus),
            handledEventsToo: true);
        SourceSelector.AddHandler(
            UIElement.PreviewKeyDownEvent,
            new KeyEventHandler(CatalogFilter_PreviewKeyDown),
            handledEventsToo: true);
        SourceSelector.AddHandler(
            UIElement.KeyDownEvent,
            new KeyEventHandler(SourceSelector_KeyDown),
            handledEventsToo: true);
        CategorySelector.AddHandler(
            UIElement.PreviewKeyDownEvent,
            new KeyEventHandler(CatalogFilter_PreviewKeyDown),
            handledEventsToo: true);
        CategorySelector.AddHandler(
            UIElement.KeyDownEvent,
            new KeyEventHandler(CategorySelector_KeyDown),
            handledEventsToo: true);
        SearchBox.AddHandler(
            UIElement.PreviewKeyDownEvent,
            new KeyEventHandler(CatalogFilter_PreviewKeyDown),
            handledEventsToo: true);
        RegisterPlaybackAccelerator(
            VirtualKey.Down,
            VirtualKeyModifiers.Control | VirtualKeyModifiers.Shift,
            () => ChangeVolumeAsync(-VolumeStep));
        RegisterPlaybackAccelerator(
            VirtualKey.Up,
            VirtualKeyModifiers.Control | VirtualKeyModifiers.Shift,
            () => ChangeVolumeAsync(VolumeStep));
        RegisterPlaybackAccelerator(
            VirtualKey.M,
            VirtualKeyModifiers.Control | VirtualKeyModifiers.Shift,
            ToggleMutedAsync);
        RegisterPlaybackAccelerator(
            VirtualKey.A,
            VirtualKeyModifiers.Control | VirtualKeyModifiers.Shift,
            ToggleAspectModeAsync);
        RegisterPlaybackAccelerator(
            VirtualKey.F11,
            VirtualKeyModifiers.None,
            ToggleFullscreenAsync);
        string assemblyVersion = typeof(App).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown";
#if DEBUG
        const string configuration = "Debug";
#else
        const string configuration = "Release";
#endif
        PackageVersion packageVersion = Package.Current.Id.Version;
        BuildInfoText.Text = $"Assembly {assemblyVersion} · {configuration} · {RuntimeInformation.ProcessArchitecture}";
        PackageInfoText.Text = $"Development package {packageVersion.Major}.{packageVersion.Minor}.{packageVersion.Build}.{packageVersion.Revision}";
    }

    public ObservableCollection<ChannelRow> Channels { get; } = [];

    internal event EventHandler? FullscreenToggleRequested;

    internal MediaPlayerElement PlaybackSurfaceElement => PlaybackSurface;

    internal void ConfigureSourceDeletion(
        Func<Task<SourceDeletionReconciliationResult>> retryPendingSourceCleanup,
        Func<SourceId, CancellationToken, ValueTask<SourceDeletionResult>> deleteSource)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(retryPendingSourceCleanup);
        ArgumentNullException.ThrowIfNull(deleteSource);
        if (_retryPendingSourceCleanup is not null || _deleteSource is not null)
        {
            throw new InvalidOperationException(
                "The source-deletion route is already configured.");
        }

        _retryPendingSourceCleanup = retryPendingSourceCleanup;
        _deleteSource = deleteSource;
    }

    internal void SetFullscreenState(bool isFullscreen)
    {
        if (_disposed)
        {
            return;
        }

        bool restoreFocus = !isFullscreen &&
            (_isFullscreen || _fullscreenTransitionPending);
        _isFullscreen = isFullscreen;
        _fullscreenTransitionPending = false;
        Visibility catalogVisibility = isFullscreen
            || _onDemandWorkspace
                ? Visibility.Collapsed
                : Visibility.Visible;
        HeaderPanel.Visibility = catalogVisibility;
        FilterPanel.Visibility = catalogVisibility;
        ChannelList.Visibility = catalogVisibility;
        CatalogStatusPanel.Visibility = catalogVisibility;
        CatalogPagingPanel.Visibility = catalogVisibility;
        PageRoot.Padding = isFullscreen ? new Thickness(0) : new Thickness(32);
        PageRoot.RowSpacing = isFullscreen ? 0 : 16;
        Grid.SetColumn(PlaybackPanel, isFullscreen ? 0 : 1);
        Grid.SetColumnSpan(PlaybackPanel, isFullscreen ? 2 : 1);
        if (_onDemandWorkspace)
        {
            Grid.SetColumn(PlaybackPanel, 0);
            Grid.SetColumnSpan(PlaybackPanel, 2);
        }
        FullscreenButton.Content = isFullscreen ? "Exit fullscreen" : "Fullscreen";
        AutomationProperties.SetName(
            FullscreenButton,
            isFullscreen ? "Exit fullscreen" : "Enter fullscreen");
        PlaybackState state = _playback?.Current.State ?? PlaybackState.Closed;
        FullscreenButton.IsEnabled = _isFullscreen || CanChangePlaybackControls(state);
        if (isFullscreen)
        {
            ShowFullscreenControlsAndRestartAutoHide();
        }
        else
        {
            StopFullscreenControlsAutoHide(showControls: true);
        }

        if (restoreFocus)
        {
            DispatcherQueue.TryEnqueue(RestoreFocusAfterFullscreen);
        }
    }

    internal void ReportFullscreenUnavailable()
    {
        if (_disposed)
        {
            return;
        }

        _fullscreenTransitionPending = false;
        if (_isFullscreen)
        {
            ShowFullscreenControlsAndRestartAutoHide();
        }
        else
        {
            StopFullscreenControlsAutoHide(showControls: true);
        }

        PlaybackState state = _playback?.Current.State ?? PlaybackState.Closed;
        FullscreenButton.IsEnabled = _isFullscreen || CanChangePlaybackControls(state);
        PlaybackStatusText.Text = "Fullscreen is unavailable.";
        DispatcherQueue.TryEnqueue(RestoreFocusAfterFullscreen);
    }

    internal async Task InitializeAsync(
        ICatalogBrowser catalogBrowser,
        ChannelLogoCache logoCache,
        PlaybackSessionCoordinator playback,
        DomainErrorPresenter domainErrorPresenter,
        INetworkAvailabilityHintSource networkAvailabilityHintSource)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(catalogBrowser);
        ArgumentNullException.ThrowIfNull(logoCache);
        ArgumentNullException.ThrowIfNull(playback);
        ArgumentNullException.ThrowIfNull(domainErrorPresenter);
        ArgumentNullException.ThrowIfNull(networkAvailabilityHintSource);
        if (_coordinator is not null || _playback is not null)
        {
            throw new InvalidOperationException("The application page is already initialized.");
        }

        _coordinator = new CatalogBrowseCoordinator(catalogBrowser);
        _logoCache = logoCache;
        _playback = playback;
        _domainErrorPresenter = domainErrorPresenter;
        _networkAvailabilityHintSource = networkAvailabilityHintSource;
        _catalogAdmissionReady = true;
        ChannelList.IsEnabled = true;
        RetryPendingDeletionButton.Visibility = Visibility.Collapsed;
        RetryPendingDeletionButton.IsEnabled = false;
        _playback.StateChanged += Playback_StateChanged;
        _playback.TimelineChanged += Playback_TimelineChanged;
        ApplyPlaybackState(_playback.Current);
        ApplyPlaybackTimeline(_playback.CurrentTimeline);
        await LoadSourcesAsync();
        UpdateSourceMutationControls();
    }

    internal void ReportPendingSourceCleanup()
    {
        if (_disposed)
        {
            return;
        }

        _catalogAdmissionReady = false;
        SourceSelector.IsEnabled = false;
        CategorySelector.IsEnabled = false;
        SearchBox.IsEnabled = false;
        ChannelList.IsEnabled = false;
        PreviousButton.IsEnabled = false;
        NextButton.IsEnabled = false;
        DeleteSourceButton.IsEnabled = false;
        RetryPendingDeletionButton.Visibility = Visibility.Visible;
        RetryPendingDeletionButton.IsEnabled = !_sourceDeletionOperationPending &&
            _retryPendingSourceCleanup is not null;
        LoadingIndicator.IsActive = false;
        StatusText.Text = "Pending source cleanup must finish before the catalog can be opened.";
    }

    internal async Task RefreshSourcesAfterSourceCleanupAsync()
    {
        if (_disposed)
        {
            return;
        }

        if (_coordinator is null || _logoCache is null || _playback is null)
        {
            throw new InvalidOperationException("The catalog page is not initialized.");
        }

        ResetLogoPageCancellation();
        ClearCatalogView();
        _catalogAdmissionReady = true;
        ChannelList.IsEnabled = true;
        RetryPendingDeletionButton.Visibility = Visibility.Collapsed;
        RetryPendingDeletionButton.IsEnabled = false;
        await LoadSourcesAsync();
        UpdateSourceMutationControls();
    }

    internal async Task PrepareSourceMutationAsync(
        SourceId sourceId,
        string operationStatus)
    {
        if (_disposed || sourceId.IsEmpty)
        {
            return;
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(operationStatus);
        BeginCatalogRetirement(operationStatus);
        await CancelAndWaitForCatalogOperationsAsync(sourceId);
    }

    internal void SetOnDemandWorkspace(bool enabled)
    {
        if (_disposed || _onDemandWorkspace == enabled)
        {
            return;
        }

        _onDemandWorkspace = enabled;
        SetFullscreenState(_isFullscreen);
        if (!enabled)
        {
            PlaybackTimelinePanel.Visibility = Visibility.Collapsed;
        }
        else if (_playback?.Current.ContentIntent == PlaybackContentIntent.OnDemand)
        {
            PlaybackTimelinePanel.Visibility = Visibility.Visible;
        }
    }

    internal Task PlayMovieAsync(
        SourceId sourceId,
        MovieId movieId,
        string displayName,
        CancellationToken cancellationToken = default) =>
        StartOnDemandAsync(
            sourceId,
            displayName,
            token => _playback!.StartAsync(sourceId, movieId, token),
            cancellationToken);

    internal Task PlayEpisodeAsync(
        SourceId sourceId,
        EpisodeId episodeId,
        string displayName,
        CancellationToken cancellationToken = default) =>
        StartOnDemandAsync(
            sourceId,
            displayName,
            token => _playback!.StartAsync(sourceId, episodeId, token),
            cancellationToken);

    private async Task StartOnDemandAsync(
        SourceId sourceId,
        string displayName,
        Func<CancellationToken, ValueTask<PlaybackSessionSnapshot?>> start,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);
        ArgumentNullException.ThrowIfNull(start);
        PlaybackSessionCoordinator? playback = _playback;
        if (_disposed || playback is null || sourceId.IsEmpty)
        {
            return;
        }

        using AsyncOperationLease operation = BeginAsyncOperation();
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _lifetime.Token);
        _playbackChannel = null;
        _playbackItemName = displayName;
        PlaybackChannelText.Text = displayName;
        SetOnDemandWorkspace(enabled: true);
        try
        {
            await start(linked.Token);
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (IsRecoverable(exception))
        {
            ApplyPlaybackState(playback.Current);
        }
    }

    private async Task LoadSourcesAsync(SourceId? preferredSourceId = null)
    {
        using AsyncOperationLease operation = BeginAsyncOperation();
        using CatalogOperationLease catalogOperation = BeginCatalogOperation();
        CatalogBrowseCoordinator coordinator = _coordinator ?? throw new InvalidOperationException("The catalog page is not initialized.");
        long loadingGeneration = BeginLoading();
        try
        {
            IReadOnlyList<CatalogSourceItem> sources = await coordinator.ReadSourcesAsync(_lifetime.Token);
            _updatingSelectors = true;
            SourceSelector.ItemsSource = sources;
            int preferredIndex = preferredSourceId.HasValue
                ? sources.ToList().FindIndex(source => source.SourceId.Equals(preferredSourceId.Value))
                : -1;
            SourceSelector.SelectedIndex = sources.Count == 0
                ? -1
                : Math.Max(0, preferredIndex);
            ResetCategoryFilterForSourceChange();
            _updatingSelectors = false;
            if (sources.Count == 0)
            {
                StatusText.Text = "No imported Live TV catalog is available.";
                UpdatePaging(0);
            }
            else await BrowseAsync(debounce: false);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception) { StatusText.Text = "The catalog could not be loaded."; }
        finally { EndLoading(loadingGeneration); }
    }

    private async Task BrowseAsync(bool debounce)
    {
        if (_disposed || !_catalogAdmissionReady || _coordinator is null ||
            SourceSelector.SelectedItem is not CatalogSourceItem source)
        {
            return;
        }

        using AsyncOperationLease operation = BeginAsyncOperation();
        using CatalogOperationLease catalogOperation = BeginCatalogOperation();
        long loadingGeneration = BeginLoading();
        try
        {
            CategoryId? categoryId = (CategorySelector.SelectedItem as CategoryOption)?.CategoryId;
            CatalogBrowseResult? result = await _coordinator.BrowseAsync(source.SourceId, categoryId, SearchBox.Text, _offset, PageSize, debounce, _lifetime.Token);
            if (result is null) return;
            _updatingSelectors = true;
            var options = new List<CategoryOption>(result.Categories.Count + 1) { new("All categories", null) };
            options.AddRange(result.Categories.Select(item => new CategoryOption(item.Name, item.CategoryId)));
            CategorySelector.ItemsSource = options;
            CategorySelector.SelectedItem = options.FirstOrDefault(item => item.CategoryId == result.SelectedCategoryId) ?? options[0];
            _updatingSelectors = false;
            ResetLogoPageCancellation();
            Channels.Clear();
            foreach (CatalogChannelItem channel in result.Channels.Items)
            {
                Channels.Add(new ChannelRow
                {
                    SourceId = source.SourceId,
                    ChannelId = channel.ChannelId,
                    Name = channel.Name,
                    Number = channel.Number,
                    HasLogo = channel.HasLogo,
                });
            }
            int first = result.Channels.TotalCount == 0 ? 0 : result.Channels.Offset + 1;
            int last = Math.Min(result.Channels.Offset + result.Channels.Items.Count, result.Channels.TotalCount);
            string browseStatus = result.Channels.TotalCount == 0
                ? "No channels match the current filters."
                : $"Showing {first}–{last} of {result.Channels.TotalCount} channels.";
            StatusText.Text = source.UsesInsecureHttp
                ? $"{browseStatus} {InsecureHttpCatalogWarning}"
                : browseStatus;
            UpdatePaging(result.Channels.TotalCount);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception) { StatusText.Text = "The catalog request failed."; }
        finally { EndLoading(loadingGeneration); }
    }

    private long BeginLoading()
    {
        long generation = Interlocked.Increment(ref _loadingGeneration);
        LoadingIndicator.IsActive = true;
        SourceSelector.IsEnabled = false;
        CategorySelector.IsEnabled = false;
        SearchBox.IsEnabled = false;
        DeleteSourceButton.IsEnabled = false;
        return generation;
    }

    private void EndLoading(long generation)
    {
        if (generation != Volatile.Read(ref _loadingGeneration) ||
            _disposed ||
            !_catalogAdmissionReady)
        {
            return;
        }

        LoadingIndicator.IsActive = false;
        SourceSelector.IsEnabled = true;
        CategorySelector.IsEnabled = true;
        SearchBox.IsEnabled = true;
        UpdateSourceMutationControls();
    }

    private void UpdatePaging(int totalCount)
    {
        PreviousButton.IsEnabled = _offset > 0;
        NextButton.IsEnabled = _offset + PageSize < totalCount;
    }

    private async void DeleteSourceButton_Click(object sender, RoutedEventArgs e)
    {
        if (_disposed || !_catalogAdmissionReady ||
            SourceSelector.SelectedItem is not CatalogSourceItem selectedSource ||
            _logoCache is not { } logoCache ||
            _deleteSource is not { } deleteSource)
        {
            return;
        }

        SourceId sourceId = selectedSource.SourceId;
        if (sourceId.IsEmpty || !TryBeginSourceDeletionOperation())
        {
            return;
        }

        bool deletionInvoked = false;
        using AsyncOperationLease operation = BeginAsyncOperation();
        try
        {
            var dialog = new ContentDialog
            {
                Title = "Delete source?",
                Content = "This removes the selected source and its imported channels from this device.",
                PrimaryButtonText = "Delete",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = XamlRoot,
            };
            _sourceDeletionDialog = dialog;
            ContentDialogResult confirmation = await dialog.ShowAsync();
            _sourceDeletionDialog = null;
            if (confirmation != ContentDialogResult.Primary)
            {
                return;
            }

            BeginCatalogRetirement("Deleting the selected source.");
            await CancelAndWaitForCatalogOperationsAsync(sourceId);
            if (_disposed)
            {
                return;
            }

            logoCache.EvictSource(sourceId);
            deletionInvoked = true;
            SourceDeletionResult deletion = await deleteSource(
                sourceId,
                _lifetime.Token);
            if (!deletion.IsSuccess)
            {
                if (deletion.FailureStage == SourceDeletionFailureStage.MarkPending)
                {
                    await RestoreCatalogAfterUncommittedDeletionFailureAsync();
                }
                else
                {
                    ReportPendingSourceCleanup();
                }

                return;
            }

            await RefreshSourcesAfterSourceCleanupAsync();
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception)
        {
            if (deletionInvoked)
            {
                ReportPendingSourceCleanup();
            }
            else
            {
                await RestoreCatalogAfterUncommittedDeletionFailureAsync();
            }
        }
        finally
        {
            _sourceDeletionDialog = null;
            EndSourceDeletionOperation();
        }
    }

    internal async Task RestoreCatalogAfterUncommittedDeletionFailureAsync()
    {
        try
        {
            await RefreshSourcesAfterSourceCleanupAsync();
            if (!_disposed)
            {
                StatusText.Text = "The selected source could not be deleted.";
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception)
        {
            ReportCatalogUnavailable();
        }
    }

    private void ReportCatalogUnavailable()
    {
        if (_disposed)
        {
            return;
        }

        _catalogAdmissionReady = false;
        SourceSelector.IsEnabled = false;
        CategorySelector.IsEnabled = false;
        SearchBox.IsEnabled = false;
        ChannelList.IsEnabled = false;
        PreviousButton.IsEnabled = false;
        NextButton.IsEnabled = false;
        DeleteSourceButton.IsEnabled = false;
        RetryPendingDeletionButton.IsEnabled = false;
        RetryPendingDeletionButton.Visibility = Visibility.Collapsed;
        LoadingIndicator.IsActive = false;
        StatusText.Text = "The catalog could not be reopened. Restart the application.";
    }

    private async void RetryPendingDeletionButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        if (_disposed ||
            _retryPendingSourceCleanup is not { } retryPendingSourceCleanup ||
            !TryBeginSourceDeletionOperation())
        {
            return;
        }

        using AsyncOperationLease operation = BeginAsyncOperation();
        try
        {
            RetryPendingDeletionButton.IsEnabled = false;
            StatusText.Text = "Retrying pending source cleanup.";
            LoadingIndicator.IsActive = true;
            SourceDeletionReconciliationResult reconciliation =
                await retryPendingSourceCleanup();
            if (!reconciliation.IsSuccess)
            {
                ReportPendingSourceCleanup();
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception)
        {
            ReportPendingSourceCleanup();
        }
        finally
        {
            EndSourceDeletionOperation();
        }
    }

    private void BeginCatalogRetirement(string operationStatus)
    {
        _catalogAdmissionReady = false;
        Interlocked.Increment(ref _loadingGeneration);
        LoadingIndicator.IsActive = true;
        SourceSelector.IsEnabled = false;
        CategorySelector.IsEnabled = false;
        SearchBox.IsEnabled = false;
        ChannelList.IsEnabled = false;
        PreviousButton.IsEnabled = false;
        NextButton.IsEnabled = false;
        DeleteSourceButton.IsEnabled = false;
        StatusText.Text = operationStatus;
    }

    private async Task CancelAndWaitForCatalogOperationsAsync(SourceId sourceId)
    {
        CatalogBrowseCoordinator coordinator = _coordinator ??
            throw new InvalidOperationException("The catalog page is not initialized.");
        coordinator.CancelPending();
        _logoPageCancellation.Cancel();
        foreach (ChannelRow row in Channels.Where(row => row.SourceId == sourceId))
        {
            row.CancelLogoLoad(releaseLogoSource: true);
        }

        await WaitForCatalogOperationsAsync();
        if (_disposed)
        {
            return;
        }

        ResetLogoPageCancellation();
        ClearCatalogView();
    }

    private bool TryBeginSourceDeletionOperation()
    {
        if (_sourceDeletionOperationPending)
        {
            return false;
        }

        _sourceDeletionOperationPending = true;
        UpdateSourceMutationControls();
        return true;
    }

    private void EndSourceDeletionOperation()
    {
        _sourceDeletionOperationPending = false;
        UpdateSourceMutationControls();
    }

    private void UpdateSourceMutationControls()
    {
        bool mutationIdle = !_disposed &&
            !_sourceDeletionOperationPending &&
            !LoadingIndicator.IsActive;
        bool commonAdmission = mutationIdle && _catalogAdmissionReady;
        DeleteSourceButton.IsEnabled = commonAdmission &&
            _deleteSource is not null &&
            SourceSelector.SelectedItem is CatalogSourceItem;
        RetryPendingDeletionButton.IsEnabled = mutationIdle &&
            RetryPendingDeletionButton.Visibility == Visibility.Visible &&
            _retryPendingSourceCleanup is not null;
    }

    private void ClearCatalogView()
    {
        _updatingSelectors = true;
        SourceSelector.SelectedIndex = -1;
        SourceSelector.ItemsSource = null;
        CategorySelector.SelectedIndex = -1;
        CategorySelector.ItemsSource = null;
        _updatingSelectors = false;
        SearchBox.Text = string.Empty;
        Channels.Clear();
        _offset = 0;
        UpdatePaging(0);
    }

    private void ResetLogoPageCancellation()
    {
        _logoPageCancellation.Cancel();
        _logoPageCancellation.Dispose();
        _logoPageCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            _lifetime.Token);
    }

    private async void SourceSelector_SelectionChanged(
        object sender,
        SelectionChangedEventArgs e)
    {
        UpdateSourceMutationControls();
        if (!_updatingSelectors)
        {
            _offset = 0;
            ResetCategoryFilterForSourceChange();
            await BrowseAsync(false);
        }
    }

    private void ResetCategoryFilterForSourceChange()
    {
        bool wasUpdatingSelectors = _updatingSelectors;
        _updatingSelectors = true;
        try
        {
            CategorySelector.ItemsSource = new[]
            {
                new CategoryOption("All categories", null),
            };
            CategorySelector.SelectedIndex = 0;
        }
        finally
        {
            _updatingSelectors = wasUpdatingSelectors;
        }
    }

    private async void CategorySelector_SelectionChanged(object sender, SelectionChangedEventArgs e) { if (!_updatingSelectors) { _offset = 0; await BrowseAsync(false); } }
    private async void SearchBox_QuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args) { _offset = 0; await BrowseAsync(false); }
    private async void SearchBox_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args) { if (args.Reason == AutoSuggestionBoxTextChangeReason.UserInput) { _offset = 0; await BrowseAsync(debounce: true); } }

    private void CatalogFilter_LosingFocus(UIElement sender, LosingFocusEventArgs args)
    {
        DependencyObject? oldFocus = args.OldFocusedElement as DependencyObject;
        DependencyObject? newFocus = args.NewFocusedElement as DependencyObject;
        bool skippedForward = IsWithin(oldFocus, SourceSelector) && IsWithin(newFocus, SearchBox);
        bool skippedBackward = IsWithin(oldFocus, SearchBox) && IsWithin(newFocus, SourceSelector);
        if ((skippedForward || skippedBackward) && !args.TrySetNewFocusedElement(CategorySelector))
        {
            args.Cancel = true;
        }
    }

    private void SourceSelector_KeyDown(object sender, KeyRoutedEventArgs args) =>
        MoveForwardOnTab(args, SourceSelector, CategorySelector);

    private void CategorySelector_KeyDown(object sender, KeyRoutedEventArgs args) =>
        MoveForwardOnTab(args, CategorySelector, SearchBox);

    private void CatalogFilter_PreviewKeyDown(object sender, KeyRoutedEventArgs args)
    {
        if (_movingTabFocus || args.Key != VirtualKey.Tab)
        {
            return;
        }

        bool shiftPressed = InputKeyboardSource
            .GetKeyStateForCurrentThread(VirtualKey.Shift)
            .HasFlag(CoreVirtualKeyStates.Down);
        Control? target = sender switch
        {
            _ when ReferenceEquals(sender, SourceSelector) && !shiftPressed => CategorySelector,
            _ when ReferenceEquals(sender, CategorySelector) && shiftPressed => SourceSelector,
            _ when ReferenceEquals(sender, CategorySelector) => SearchBox,
            _ when ReferenceEquals(sender, SearchBox) && shiftPressed => CategorySelector,
            _ => null,
        };
        if (target is null || !target.IsEnabled || !target.IsTabStop)
        {
            return;
        }

        _movingTabFocus = true;
        try { args.Handled = target.Focus(FocusState.Keyboard); }
        finally { _movingTabFocus = false; }
    }

    private void MoveForwardOnTab(KeyRoutedEventArgs args, Control owner, Control target)
    {
        if (args.Handled || _movingTabFocus ||
            args.OriginalSource is not DependencyObject origin || !IsWithin(origin, owner) ||
            args.Key != VirtualKey.Tab ||
            InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift).HasFlag(CoreVirtualKeyStates.Down))
        {
            return;
        }

        args.Handled = true;
        _movingTabFocus = true;
        try { target.Focus(FocusState.Keyboard); }
        finally { _movingTabFocus = false; }
    }

    private static bool IsWithin(DependencyObject? candidate, DependencyObject ancestor)
    {
        for (int depth = 0; candidate is not null && depth < 32; depth++)
        {
            if (ReferenceEquals(candidate, ancestor)) return true;
            candidate = VisualTreeHelper.GetParent(candidate);
        }

        return false;
    }

    private async void PreviousButton_Click(object sender, RoutedEventArgs e) { _offset = Math.Max(0, _offset - PageSize); await BrowseAsync(false); }
    private async void NextButton_Click(object sender, RoutedEventArgs e) { _offset += PageSize; await BrowseAsync(false); }

    private async void ChannelList_ItemClick(object sender, ItemClickEventArgs e)
    {
        PlaybackSessionCoordinator? playback = _playback;
        if (_disposed || !_catalogAdmissionReady || playback is null ||
            e.ClickedItem is not ChannelRow channel)
        {
            return;
        }

        _playbackChannel = channel;
        _playbackItemName = channel.Name;
        SetOnDemandWorkspace(enabled: false);
        using AsyncOperationLease operation = BeginAsyncOperation();
        try
        {
            await playback.StartAsync(
                channel.SourceId,
                channel.ChannelId,
                _lifetime.Token);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception)
        {
            ApplyPlaybackState(playback.Current);
        }
    }

    private async void PlayButton_Click(object sender, RoutedEventArgs e) =>
        await ExecutePlaybackCommandAsync(
            static (playback, token) => playback.PlayAsync(token));

    private async void PauseButton_Click(object sender, RoutedEventArgs e) =>
        await ExecutePlaybackCommandAsync(
            static (playback, token) => playback.PauseAsync(token));

    private async void StopButton_Click(object sender, RoutedEventArgs e) =>
        await ExecutePlaybackCommandAsync(
            static (playback, token) => playback.StopAsync(token));

    private void RetryPlaybackButton_Click(object sender, RoutedEventArgs e)
    {
        PlaybackSessionCoordinator? playback = _playback;
        if (_disposed || playback is null || !playback.CanRetryReconnect)
        {
            if (!_disposed && playback is not null)
            {
                ApplyPlaybackState(playback.Current);
            }

            return;
        }

        RetryPlaybackButton.IsEnabled = false;
        _ = ObserveRetryPlaybackAdmissionAsync(playback);
    }

    private async Task ObserveRetryPlaybackAdmissionAsync(
        PlaybackSessionCoordinator playback)
    {
        using AsyncOperationLease operation = BeginAsyncOperation();
        try
        {
            PlaybackEngineOperationResult result = await playback.RetryReconnectAsync();
            if (!result.IsSuccess &&
                !_disposed &&
                ReferenceEquals(_playback, playback))
            {
                ApplyPlaybackState(playback.Current);
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception)
        {
            if (!_disposed && ReferenceEquals(_playback, playback))
            {
                ApplyPlaybackState(playback.Current);
            }
        }
    }

    private async void VolumeDownButton_Click(object sender, RoutedEventArgs e) =>
        await ChangeVolumeAsync(-VolumeStep);

    private async void VolumeUpButton_Click(object sender, RoutedEventArgs e) =>
        await ChangeVolumeAsync(VolumeStep);

    private async void MuteButton_Click(object sender, RoutedEventArgs e) =>
        await ToggleMutedAsync();

    private async void AspectModeButton_Click(object sender, RoutedEventArgs e) =>
        await ToggleAspectModeAsync();

    private async void FullscreenButton_Click(object sender, RoutedEventArgs e) =>
        await ToggleFullscreenAsync();

    private void FullscreenSurface_PointerMoved(
        object sender,
        PointerRoutedEventArgs args) =>
        ShowFullscreenControlsAndRestartAutoHide();

    private void FullscreenSurface_KeyDown(object sender, KeyRoutedEventArgs args) =>
        ShowFullscreenControlsAndRestartAutoHide();

    private void FullscreenSurface_GettingFocus(
        UIElement sender,
        GettingFocusEventArgs args) =>
        ShowFullscreenControlsAndRestartAutoHide();

    private void FullscreenControlsAutoHideTimer_Tick(
        DispatcherQueueTimer sender,
        object args)
    {
        sender.Stop();
        if (_disposed || !_isFullscreen)
        {
            return;
        }

        if (IsKeyboardFocusWithinPlaybackControls())
        {
            RestartFullscreenControlsAutoHideTimer();
            return;
        }

        PlaybackControlsPanel.Visibility = Visibility.Collapsed;
        PlaybackPanel.RowSpacing = 0;
    }

    private void ShowFullscreenControlsAndRestartAutoHide()
    {
        if (_disposed || !_isFullscreen)
        {
            return;
        }

        PlaybackControlsPanel.Visibility = Visibility.Visible;
        PlaybackPanel.RowSpacing = 8;
        RestartFullscreenControlsAutoHideTimer();
    }

    private void RestartFullscreenControlsAutoHideTimer()
    {
        _fullscreenControlsAutoHideTimer.Stop();
        _fullscreenControlsAutoHideTimer.Start();
    }

    private void StopFullscreenControlsAutoHide(bool showControls)
    {
        _fullscreenControlsAutoHideTimer.Stop();
        if (!showControls)
        {
            return;
        }

        PlaybackControlsPanel.Visibility = Visibility.Visible;
        PlaybackPanel.RowSpacing = 8;
    }

    private bool IsKeyboardFocusWithinPlaybackControls()
    {
        if (XamlRoot is not { } xamlRoot ||
            FocusManager.GetFocusedElement(xamlRoot) is not Control
            {
                FocusState: FocusState.Keyboard,
            } focusedControl)
        {
            return false;
        }

        DependencyObject? current = focusedControl;
        while (current is not null)
        {
            if (ReferenceEquals(current, PlaybackControlsPanel))
            {
                return true;
            }

            current = VisualTreeHelper.GetParent(current);
        }

        return false;
    }

    private Task ChangeVolumeAsync(int delta)
        => ExecutePlaybackControlAsync(
            (coordinator, sessionId, token) =>
            {
                int target = Math.Clamp(
                    coordinator.CurrentControls.Volume.Percent + delta,
                    0,
                    100);
                return coordinator.SetVolumeAsync(
                    sessionId,
                    PlaybackVolume.FromPercent(target),
                    token);
            });

    private Task ToggleMutedAsync()
        => ExecutePlaybackControlAsync(
            (coordinator, sessionId, token) => coordinator.SetMutedAsync(
                sessionId,
                !coordinator.CurrentControls.IsMuted,
                token));

    private Task ToggleAspectModeAsync()
        => ExecutePlaybackControlAsync(
            (coordinator, sessionId, token) => coordinator.SetAspectModeAsync(
                sessionId,
                coordinator.CurrentControls.AspectMode == PlaybackAspectMode.Fit
                    ? PlaybackAspectMode.Fill
                    : PlaybackAspectMode.Fit,
                token));

    private Task ToggleFullscreenAsync()
    {
        if (_disposed || _fullscreenTransitionPending)
        {
            return Task.CompletedTask;
        }

        if (!_isFullscreen &&
            XamlRoot is { } xamlRoot &&
            FocusManager.GetFocusedElement(xamlRoot) is Control focusedControl)
        {
            _focusBeforeFullscreen = new WeakReference<Control>(focusedControl);
        }

        EventHandler? request = FullscreenToggleRequested;
        if (request is null)
        {
            ReportFullscreenUnavailable();
            return Task.CompletedTask;
        }

        _fullscreenTransitionPending = true;
        FullscreenButton.IsEnabled = false;
        request.Invoke(this, EventArgs.Empty);
        return Task.CompletedTask;
    }

    private void RestoreFocusAfterFullscreen()
    {
        if (_disposed)
        {
            return;
        }

        if (_focusBeforeFullscreen is not null &&
            _focusBeforeFullscreen.TryGetTarget(out Control? previousFocus) &&
            previousFocus.XamlRoot == XamlRoot &&
            previousFocus.Visibility == Visibility.Visible &&
            previousFocus.IsEnabled &&
            previousFocus.Focus(FocusState.Keyboard))
        {
            _focusBeforeFullscreen = null;
            return;
        }

        _focusBeforeFullscreen = null;
        FullscreenButton.Focus(FocusState.Keyboard);
    }

    private async Task ExecutePlaybackControlAsync(
        Func<PlaybackSessionCoordinator, PlaybackSessionId, CancellationToken,
            ValueTask<PlaybackEngineOperationResult>> command)
    {
        PlaybackSessionCoordinator? playback = _playback;
        if (_disposed || playback is null)
        {
            return;
        }

        using AsyncOperationLease operation = BeginAsyncOperation();
        bool gateEntered = false;
        try
        {
            await _playbackControlGate.WaitAsync(_lifetime.Token);
            gateEntered = true;
            if (_disposed || _playback != playback)
            {
                return;
            }

            PlaybackSessionSnapshot session = playback.Current;
            if (!CanChangePlaybackControls(session.State))
            {
                ApplyPlaybackState(session);
                return;
            }

            await command(
                playback,
                session.SessionId,
                _lifetime.Token);
            ApplyPlaybackState(playback.Current);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception)
        {
            ApplyPlaybackState(playback.Current);
        }
        finally
        {
            if (gateEntered)
            {
                _playbackControlGate.Release();
            }
        }
    }

    private void RegisterPlaybackAccelerator(
        VirtualKey key,
        VirtualKeyModifiers modifiers,
        Func<Task> operation)
    {
        var accelerator = new KeyboardAccelerator
        {
            Key = key,
            Modifiers = modifiers,
        };
        accelerator.Invoked += async (_, args) =>
        {
            args.Handled = true;
            await operation();
        };
        KeyboardAccelerators.Add(accelerator);
    }

    private async Task ExecutePlaybackCommandAsync(
        Func<PlaybackSessionCoordinator, CancellationToken,
            ValueTask<PlaybackEngineOperationResult>> command)
    {
        PlaybackSessionCoordinator? playback = _playback;
        if (_disposed || playback is null)
        {
            return;
        }

        using AsyncOperationLease operation = BeginAsyncOperation();
        try
        {
            PlaybackEngineOperationResult result = await command(
                playback,
                _lifetime.Token);
            if (!result.IsSuccess)
            {
                ApplyPlaybackState(playback.Current);
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception)
        {
            ApplyPlaybackState(playback.Current);
        }
    }

    private void Playback_StateChanged(
        object? sender,
        PlaybackSessionStateChangedEventArgs args)
    {
        PlaybackSessionSnapshot snapshot = args.Snapshot;
        if (DispatcherQueue.HasThreadAccess)
        {
            ApplyPlaybackState(snapshot);
            return;
        }

        DispatcherQueue.TryEnqueue(() => ApplyPlaybackState(snapshot));
    }

    private void Playback_TimelineChanged(
        object? sender,
        PlaybackTimelineChangedEventArgs args)
    {
        PlaybackTimelineSnapshot snapshot = args.Snapshot;
        if (DispatcherQueue.HasThreadAccess)
        {
            ApplyPlaybackTimeline(snapshot);
            return;
        }

        DispatcherQueue.TryEnqueue(() => ApplyPlaybackTimeline(snapshot));
    }

    private void ApplyPlaybackTimeline(PlaybackTimelineSnapshot snapshot)
    {
        PlaybackSessionCoordinator? playback = _playback;
        if (_disposed || playback is null ||
            !ReferenceEquals(playback.CurrentTimeline, snapshot))
        {
            return;
        }

        PlaybackSessionSnapshot current = playback.Current;
        bool onDemand = current.ContentIntent == PlaybackContentIntent.OnDemand &&
            current.State != PlaybackState.Closed;
        PlaybackTimelinePanel.Visibility = onDemand
            ? Visibility.Visible
            : Visibility.Collapsed;

        TimeSpan position = snapshot.Position;
        TimeSpan duration = snapshot.Duration;
        PlaybackStartTimeText.Text = "00:00";
        PlaybackEndTimeText.Text = duration > TimeSpan.Zero
            ? FormatPlaybackTime(duration)
            : "--:--";
        PlaybackTimelineSlider.Maximum = duration > TimeSpan.Zero
            ? duration.TotalSeconds
            : 1;
        bool timelineCanSeek = onDemand && snapshot.CanSeek &&
            current.State is PlaybackState.Playing or PlaybackState.Paused;
        bool currentPointerInteraction = timelineCanSeek &&
            _timelinePointerInteractionActive &&
            _timelinePointerSessionId == snapshot.SessionId;
        TimeSpan displayedPosition;
        if (!currentPointerInteraction)
        {
            _timelinePointerInteractionActive = false;
            _timelinePointerSessionId = default;
            PlaybackTimelineSlider.Value = duration > TimeSpan.Zero
                ? Math.Clamp(position.TotalSeconds, 0, duration.TotalSeconds)
                : 0;
            displayedPosition = position;
        }
        else
        {
            double previewSeconds = duration > TimeSpan.Zero
                ? Math.Clamp(
                    PlaybackTimelineSlider.Value,
                    0,
                    duration.TotalSeconds)
                : 0;
            displayedPosition = TimeSpan.FromSeconds(previewSeconds);
        }

        PlaybackCurrentTimeText.Text = FormatPlaybackTime(displayedPosition);

        PlaybackTimelineSlider.IsEnabled = timelineCanSeek;
        AutomationProperties.SetName(
            PlaybackTimelineSlider,
            snapshot.CanSeek
                ? $"Seek through on-demand content, {FormatPlaybackTime(displayedPosition)} of {FormatPlaybackTime(duration)}"
                : "On-demand timeline is not seekable");
    }

    private void PlaybackTimelineSlider_PointerPressed(
        object sender,
        PointerRoutedEventArgs args)
    {
        PlaybackTimelineSnapshot? timeline = _playback?.CurrentTimeline;
        if (_disposed || !PlaybackTimelineSlider.IsEnabled ||
            timeline is null || timeline.SessionId.IsEmpty || !timeline.CanSeek)
        {
            return;
        }

        _timelinePointerInteractionActive = true;
        _timelinePointerSessionId = timeline.SessionId;
    }

    private async void PlaybackTimelineSlider_PointerReleased(
        object sender,
        PointerRoutedEventArgs args) =>
        await CompleteTimelinePointerInteractionAsync();

    private async void PlaybackTimelineSlider_PointerCaptureLost(
        object sender,
        PointerRoutedEventArgs args) =>
        await CompleteTimelinePointerInteractionAsync();

    private void PlaybackTimelineSlider_ValueChanged(
        object sender,
        RangeBaseValueChangedEventArgs args)
    {
        if (!_timelinePointerInteractionActive)
        {
            return;
        }

        PlaybackTimelineSnapshot? timeline = _playback?.CurrentTimeline;
        if (timeline is null || timeline.SessionId != _timelinePointerSessionId ||
            timeline.Duration <= TimeSpan.Zero)
        {
            return;
        }

        double previewSeconds = Math.Clamp(
            args.NewValue,
            0,
            timeline.Duration.TotalSeconds);
        PlaybackCurrentTimeText.Text = FormatPlaybackTime(
            TimeSpan.FromSeconds(previewSeconds));
    }

    private async void PlaybackTimelineSlider_KeyUp(
        object sender,
        KeyRoutedEventArgs args)
    {
        if (args.Key is VirtualKey.Left or VirtualKey.Right or
            VirtualKey.Home or VirtualKey.End or VirtualKey.Enter or VirtualKey.Space)
        {
            PlaybackTimelineSnapshot? timeline = _playback?.CurrentTimeline;
            if (timeline is not null)
            {
                await SeekToTimelineSliderAsync(
                    timeline.SessionId,
                    PlaybackTimelineSlider.Value);
            }
        }
    }

    private async Task CompleteTimelinePointerInteractionAsync()
    {
        if (!_timelinePointerInteractionActive)
        {
            return;
        }

        PlaybackSessionId sessionId = _timelinePointerSessionId;
        double requestedSeconds = PlaybackTimelineSlider.Value;
        _timelinePointerInteractionActive = false;
        _timelinePointerSessionId = default;
        await SeekToTimelineSliderAsync(sessionId, requestedSeconds);
    }

    private async Task SeekToTimelineSliderAsync(
        PlaybackSessionId sessionId,
        double requestedSeconds)
    {
        PlaybackSessionCoordinator? playback = _playback;
        if (_disposed || playback is null || sessionId.IsEmpty ||
            !double.IsFinite(requestedSeconds) ||
            !PlaybackTimelineSlider.IsEnabled ||
            Interlocked.Exchange(ref _timelineSeekInProgress, 1) != 0)
        {
            return;
        }

        using AsyncOperationLease operation = BeginAsyncOperation();
        try
        {
            PlaybackTimelineSnapshot timeline = playback.CurrentTimeline;
            if (timeline.SessionId != sessionId || !timeline.CanSeek ||
                timeline.Duration <= TimeSpan.Zero)
            {
                ApplyPlaybackTimeline(timeline);
                return;
            }

            requestedSeconds = Math.Clamp(
                requestedSeconds,
                0,
                timeline.Duration.TotalSeconds);
            PlaybackEngineOperationResult result = await playback.SeekAsync(
                sessionId,
                TimeSpan.FromSeconds(requestedSeconds),
                _lifetime.Token);
            if (!result.IsSuccess)
            {
                ApplyPlaybackTimeline(playback.CurrentTimeline);
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (IsRecoverable(exception))
        {
            ApplyPlaybackTimeline(playback.CurrentTimeline);
        }
        finally
        {
            Volatile.Write(ref _timelineSeekInProgress, 0);
        }
    }

    private static string FormatPlaybackTime(TimeSpan value)
    {
        value = value < TimeSpan.Zero ? TimeSpan.Zero : value;
        return value.TotalHours >= 1
            ? $"{(long)value.TotalHours:00}:{value.Minutes:00}:{value.Seconds:00}"
            : $"{value.Minutes:00}:{value.Seconds:00}";
    }

    private void ApplyPlaybackState(PlaybackSessionSnapshot snapshot)
    {
        PlaybackSessionCoordinator? playback = _playback;
        if (_disposed || playback is null)
        {
            return;
        }

        PlaybackSessionSnapshot current = playback.Current;
        if (!ReferenceEquals(current, snapshot))
        {
            return;
        }

        if (snapshot.SourceId is SourceId sourceId &&
            snapshot.ChannelId is ChannelId channelId &&
            _playbackChannel is { } playbackChannel &&
            playbackChannel.SourceId.Equals(sourceId) &&
            playbackChannel.ChannelId.Equals(channelId))
        {
            PlaybackChannelText.Text = playbackChannel.Name;
        }
        else if (snapshot.ContentIntent == PlaybackContentIntent.OnDemand &&
            !string.IsNullOrWhiteSpace(_playbackItemName))
        {
            PlaybackChannelText.Text = _playbackItemName;
        }
        else if (snapshot.State == PlaybackState.Closed)
        {
            PlaybackChannelText.Text = _onDemandWorkspace
                ? "No movie or episode selected."
                : "No channel selected.";
        }

        bool canRetryReconnect = snapshot.State == PlaybackState.Failed &&
            playback.CanRetryReconnect;
        DomainErrorPresentation? failurePresentation =
            GetOrCreateFailurePresentation(snapshot);
        PlaybackStatusText.Text = failurePresentation?.Message ??
            GetPlaybackStatusText(snapshot, canRetryReconnect);
        PlaybackReconnectSnapshot? reconnect = snapshot.Reconnect;
        bool waitingToReconnect = reconnect?.Phase == PlaybackReconnectPhase.Waiting;
        PlaybackReconnectCountdownText.Text = waitingToReconnect
            ? $"Retrying in {GetRemainingDelaySeconds(reconnect!.RemainingDelay)} seconds."
            : string.Empty;
        PlaybackReconnectCountdownText.Visibility = waitingToReconnect
            ? Visibility.Visible
            : Visibility.Collapsed;
        PlayButton.IsEnabled = snapshot.State == PlaybackState.Paused;
        PauseButton.IsEnabled = snapshot.State == PlaybackState.Playing;
        StopButton.IsEnabled = snapshot.State is
            PlaybackState.Opening or
            PlaybackState.Buffering or
            PlaybackState.Playing or
            PlaybackState.Paused or
            PlaybackState.Reconnecting or
            PlaybackState.Failed;
        bool isReconnecting = snapshot.State == PlaybackState.Reconnecting;
        StopButton.Content = isReconnecting ? "Cancel reconnect" : "Stop";
        AutomationProperties.SetName(
            StopButton,
            isReconnecting ? "Cancel reconnect" : "Stop content");
        RetryPlaybackButton.Visibility = canRetryReconnect
            ? Visibility.Visible
            : Visibility.Collapsed;
        RetryPlaybackButton.IsEnabled = canRetryReconnect;
        bool failureVisible = failurePresentation is not null;
        PlaybackFailurePanel.Visibility = failureVisible
            ? Visibility.Visible
            : Visibility.Collapsed;
        PlaybackOperationIdLabelText.Text = failureVisible
            ? $"{failurePresentation!.OperationIdLabel}:"
            : string.Empty;
        PlaybackOperationIdText.Text = failureVisible
            ? failurePresentation!.OperationId.Value
            : string.Empty;
        AutomationProperties.SetName(
            PlaybackOperationIdText,
            failureVisible
                ? $"{failurePresentation!.OperationIdLabel} " +
                    failurePresentation.OperationId.Value
                : string.Empty);
        string connectivityHint = failurePresentation?.ConnectivityHint ?? string.Empty;
        PlaybackConnectivityHintText.Text = connectivityHint;
        PlaybackConnectivityHintText.Visibility = string.IsNullOrEmpty(connectivityHint)
            ? Visibility.Collapsed
            : Visibility.Visible;

        PlaybackControlSnapshot controls = playback.CurrentControls;
        bool controlsEnabled = CanChangePlaybackControls(snapshot.State);
        VolumeDownButton.IsEnabled = controlsEnabled && controls.Volume.Percent > 0;
        VolumeUpButton.IsEnabled = controlsEnabled && controls.Volume.Percent < 100;
        MuteButton.IsEnabled = controlsEnabled;
        AspectModeButton.IsEnabled = controlsEnabled;
        FullscreenButton.IsEnabled = !_fullscreenTransitionPending &&
            (_isFullscreen || controlsEnabled);
        PlaybackVolumeText.Text = $"Volume {controls.Volume.Percent}%";
        MuteButton.Content = controls.IsMuted ? "Unmute" : "Mute";
        AutomationProperties.SetName(
            MuteButton,
            controls.IsMuted ? "Unmute playback" : "Mute playback");
        bool isFit = controls.AspectMode == PlaybackAspectMode.Fit;
        AspectModeButton.Content = isFit ? "Fill" : "Fit";
        AutomationProperties.SetName(
            AspectModeButton,
            isFit ? "Use fill aspect mode" : "Use fit aspect mode");
        ApplyPlaybackTimeline(playback.CurrentTimeline);
    }

    private static bool CanChangePlaybackControls(PlaybackState state) =>
        state is PlaybackState.Buffering or PlaybackState.Playing or PlaybackState.Paused;

    private DomainErrorPresentation? GetOrCreateFailurePresentation(
        PlaybackSessionSnapshot snapshot)
    {
        if (snapshot.State != PlaybackState.Failed)
        {
            _presentedFailureSnapshot = null;
            _presentedFailure = null;
            return null;
        }

        if (ReferenceEquals(_presentedFailureSnapshot, snapshot))
        {
            return _presentedFailure;
        }

        DomainError error = snapshot.Error ??
            DomainError.Create(DomainErrorCode.DomainInvariantViolation);
        NetworkAvailabilityHint hint = ReadNetworkAvailabilityHint();
        _presentedFailure = _domainErrorPresenter?.Present(error, hint);
        _presentedFailureSnapshot = snapshot;
        return _presentedFailure;
    }

    private NetworkAvailabilityHint ReadNetworkAvailabilityHint()
    {
        try
        {
            return _networkAvailabilityHintSource?.ReadCurrent() ??
                NetworkAvailabilityHint.Unknown;
        }
        catch (Exception exception) when (IsRecoverable(exception))
        {
            return NetworkAvailabilityHint.Unknown;
        }
    }

    private static string GetPlaybackStatusText(
        PlaybackSessionSnapshot snapshot,
        bool canRetryReconnect)
    {
        bool onDemand = snapshot.ContentIntent == PlaybackContentIntent.OnDemand;
        return snapshot.State switch
        {
            PlaybackState.Opening => onDemand ? "Opening content." : "Opening channel.",
            PlaybackState.Buffering => onDemand ? "Buffering content." : "Buffering channel.",
            PlaybackState.Playing => onDemand ? "Content is playing." : "Channel is playing.",
            PlaybackState.Paused => "Playback paused.",
            PlaybackState.Completed => "Playback completed.",
            PlaybackState.Reconnecting => GetReconnectStatusText(snapshot.Reconnect),
            PlaybackState.Stopping => "Stopping playback.",
            PlaybackState.Failed when canRetryReconnect =>
                "Playback could not reconnect. Check your connection and retry.",
            PlaybackState.Failed => "Playback is unavailable.",
            _ => "Playback stopped.",
        };
    }

    private static string GetReconnectStatusText(PlaybackReconnectSnapshot? reconnect) =>
        reconnect?.Phase switch
        {
            PlaybackReconnectPhase.Evaluating => "Checking playback connection.",
            PlaybackReconnectPhase.Waiting =>
                $"Reconnect attempt {reconnect.AttemptNumber} of {reconnect.MaximumAttempts} is waiting.",
            PlaybackReconnectPhase.Attempting =>
                $"Reconnect attempt {reconnect.AttemptNumber} of {reconnect.MaximumAttempts} is starting.",
            _ => "Reconnecting playback.",
        };

    private static int GetRemainingDelaySeconds(TimeSpan remainingDelay) =>
        Math.Max(1, checked((int)Math.Ceiling(remainingDelay.TotalSeconds)));

    private static bool IsRecoverable(Exception exception) =>
        exception is not OutOfMemoryException and
        not StackOverflowException and
        not AccessViolationException;

    private async void ChannelList_ContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (args.InRecycleQueue)
        {
            if (args.Item is ChannelRow recycledRow)
            {
                recycledRow.CancelLogoLoad(releaseLogoSource: true);
            }

            return;
        }

        if (_disposed || !_catalogAdmissionReady || args.Item is not ChannelRow row ||
            !row.HasLogo || row.LogoSource is not null || _logoCache is null)
        {
            return;
        }
        using AsyncOperationLease operation = BeginAsyncOperation();
        using CatalogOperationLease catalogOperation = BeginCatalogOperation();
        ChannelRow.LogoLoadOperation logoLoad = row.BeginLogoLoad(
            _logoPageCancellation.Token);
        long generation = logoLoad.Generation;
        try
        {
            ChannelLogoImage? logo = await _logoCache.GetAsync(
                row.SourceId,
                row.ChannelId,
                logoLoad.Token);
            if (_disposed || logo is null || !row.IsCurrentLogoLoad(generation)) return;
            if (logo.PixelWidth is <= 0 or > SqliteChannelLogoProvider.MaximumLogoDimension ||
                logo.PixelHeight is <= 0 or > SqliteChannelLogoProvider.MaximumLogoDimension ||
                (long)logo.PixelWidth * logo.PixelHeight > SqliteChannelLogoProvider.MaximumLogoPixels)
            {
                return;
            }
            using var stream = new InMemoryRandomAccessStream();
            using (var writer = new DataWriter(stream))
            {
                writer.WriteBytes(logo.Content.ToArray());
                await writer.StoreAsync();
                logoLoad.Token.ThrowIfCancellationRequested();
                writer.DetachStream();
            }
            stream.Seek(0);
            var image = new BitmapImage
            {
                DecodePixelWidth = logo.PixelWidth,
                DecodePixelHeight = logo.PixelHeight,
            };
            await image.SetSourceAsync(stream);
            logoLoad.Token.ThrowIfCancellationRequested();
            if (!_disposed && row.IsCurrentLogoLoad(generation)) row.LogoSource = image;
        }
        catch (OperationCanceledException) when (logoLoad.Token.IsCancellationRequested) { }
        catch (Exception) { }
        finally
        {
            logoLoad.Dispose();
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        try
        {
            _sourceDeletionDialog?.Hide();
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or COMException)
        {
        }

        _sourceDeletionDialog = null;
        StopFullscreenControlsAutoHide(showControls: false);
        _fullscreenControlsAutoHideTimer.Tick -=
            FullscreenControlsAutoHideTimer_Tick;
        PageRoot.RemoveHandler(
            UIElement.PointerMovedEvent,
            _fullscreenPointerMovedHandler);
        PageRoot.RemoveHandler(
            UIElement.KeyDownEvent,
            _fullscreenKeyDownHandler);
        PlaybackTimelineSlider.RemoveHandler(
            UIElement.PointerPressedEvent,
            _timelinePointerPressedHandler);
        PlaybackTimelineSlider.RemoveHandler(
            UIElement.PointerReleasedEvent,
            _timelinePointerReleasedHandler);
        PageRoot.GettingFocus -= FullscreenSurface_GettingFocus;
        _disposed = true;
        _catalogAdmissionReady = false;
        if (_playback is not null)
        {
            _playback.StateChanged -= Playback_StateChanged;
            _playback.TimelineChanged -= Playback_TimelineChanged;
            _playback = null;
        }

        _lifetime.Cancel();
        _logoPageCancellation.Cancel();
        foreach (ChannelRow row in Channels)
        {
            row.CancelLogoLoad(releaseLogoSource: true);
        }
        _coordinator?.Dispose();
        _retryPendingSourceCleanup = null;
        _deleteSource = null;
        FullscreenToggleRequested = null;
        _lifetime.Dispose();
        _logoPageCancellation.Dispose();
        DisposePlaybackControlGateIfDrained();
        GC.SuppressFinalize(this);
    }

    private ValueTask WaitForCatalogOperationsAsync()
    {
        lock (_catalogOperationSync)
        {
            if (_activeCatalogOperations == 0)
            {
                return ValueTask.CompletedTask;
            }

            _catalogOperationsDrained ??= new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            return new ValueTask(_catalogOperationsDrained.Task);
        }
    }

    private CatalogOperationLease BeginCatalogOperation()
    {
        lock (_catalogOperationSync)
        {
            _activeCatalogOperations = checked(_activeCatalogOperations + 1);
        }

        return new CatalogOperationLease(this);
    }

    private void EndCatalogOperation()
    {
        TaskCompletionSource? completion = null;
        lock (_catalogOperationSync)
        {
            _activeCatalogOperations--;
            if (_activeCatalogOperations == 0)
            {
                completion = _catalogOperationsDrained;
                _catalogOperationsDrained = null;
            }
        }

        completion?.TrySetResult();
    }

    internal ValueTask WaitForPendingOperationsAsync()
    {
        lock (_operationSync)
        {
            if (_activeAsyncOperations == 0)
            {
                return ValueTask.CompletedTask;
            }

            _operationsDrained ??= new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            return new ValueTask(_operationsDrained.Task);
        }
    }

    private AsyncOperationLease BeginAsyncOperation()
    {
        lock (_operationSync)
        {
            _activeAsyncOperations = checked(_activeAsyncOperations + 1);
        }

        return new AsyncOperationLease(this);
    }

    private void EndAsyncOperation()
    {
        TaskCompletionSource? completion = null;
        bool disposePlaybackControlGate = false;
        lock (_operationSync)
        {
            _activeAsyncOperations--;
            if (_activeAsyncOperations == 0)
            {
                completion = _operationsDrained;
                _operationsDrained = null;
                if (_disposed && !_playbackControlGateDisposed)
                {
                    _playbackControlGateDisposed = true;
                    disposePlaybackControlGate = true;
                }
            }
        }

        completion?.TrySetResult();
        if (disposePlaybackControlGate)
        {
            _playbackControlGate.Dispose();
        }
    }

    private void DisposePlaybackControlGateIfDrained()
    {
        bool disposePlaybackControlGate = false;
        lock (_operationSync)
        {
            if (_activeAsyncOperations == 0 && !_playbackControlGateDisposed)
            {
                _playbackControlGateDisposed = true;
                disposePlaybackControlGate = true;
            }
        }

        if (disposePlaybackControlGate)
        {
            _playbackControlGate.Dispose();
        }
    }

    private sealed record CategoryOption(string Name, CategoryId? CategoryId);

    private sealed class CatalogOperationLease(MainPage owner) : IDisposable
    {
        private MainPage? _owner = owner;

        public void Dispose()
        {
            MainPage? current = Interlocked.Exchange(ref _owner, null);
            current?.EndCatalogOperation();
        }
    }

    private sealed class AsyncOperationLease(MainPage owner) : IDisposable
    {
        private MainPage? _owner = owner;

        public void Dispose()
        {
            MainPage? current = Interlocked.Exchange(ref _owner, null);
            current?.EndAsyncOperation();
        }
    }

    public sealed class ChannelRow : INotifyPropertyChanged
    {
        private readonly object _logoLoadSync = new();
        private ImageSource? _logoSource;
        private long _logoGeneration;
        private LogoLoadOperation? _logoLoad;
        public SourceId SourceId { get; set; }
        public ChannelId ChannelId { get; set; }
        public string Name { get; set; } = string.Empty;
        public int? Number { get; set; }
        public bool HasLogo { get; set; }
        public ImageSource? LogoSource { get => _logoSource; set { _logoSource = value; PropertyChanged?.Invoke(this, new(nameof(LogoSource))); } }
        public event PropertyChangedEventHandler? PropertyChanged;

        internal LogoLoadOperation BeginLogoLoad(CancellationToken pageCancellationToken)
        {
            LogoLoadOperation current;
            LogoLoadOperation? previous;
            lock (_logoLoadSync)
            {
                long generation = checked(_logoGeneration + 1);
                _logoGeneration = generation;
                current = new LogoLoadOperation(this, generation, pageCancellationToken);
                previous = _logoLoad;
                _logoLoad = current;
            }

            previous?.Cancel();
            return current;
        }

        internal void CancelLogoLoad(bool releaseLogoSource)
        {
            LogoLoadOperation? current;
            lock (_logoLoadSync)
            {
                _logoGeneration = checked(_logoGeneration + 1);
                current = _logoLoad;
                _logoLoad = null;
            }

            current?.Cancel();
            if (releaseLogoSource && LogoSource is not null)
            {
                LogoSource = null;
            }
        }

        internal bool IsCurrentLogoLoad(long generation)
        {
            lock (_logoLoadSync)
            {
                return _logoGeneration == generation &&
                    _logoLoad is { } current &&
                    current.Generation == generation &&
                    !current.Token.IsCancellationRequested;
            }
        }

        private void CompleteLogoLoad(LogoLoadOperation completed)
        {
            lock (_logoLoadSync)
            {
                if (ReferenceEquals(_logoLoad, completed))
                {
                    _logoLoad = null;
                }
            }
        }

        internal sealed class LogoLoadOperation : IDisposable
        {
            private readonly object _sync = new();
            private readonly ChannelRow _owner;
            private readonly CancellationTokenSource _cancellation;
            private bool _disposed;

            internal LogoLoadOperation(
                ChannelRow owner,
                long generation,
                CancellationToken pageCancellationToken)
            {
                _owner = owner;
                Generation = generation;
                _cancellation = CancellationTokenSource.CreateLinkedTokenSource(
                    pageCancellationToken);
                Token = _cancellation.Token;
            }

            internal long Generation { get; }

            internal CancellationToken Token { get; }

            internal void Cancel()
            {
                lock (_sync)
                {
                    if (!_disposed)
                    {
                        _cancellation.Cancel();
                    }
                }
            }

            public void Dispose()
            {
                _owner.CompleteLogoLoad(this);
                lock (_sync)
                {
                    if (_disposed)
                    {
                        return;
                    }

                    _disposed = true;
                    _cancellation.Dispose();
                }
            }
        }
    }
}
