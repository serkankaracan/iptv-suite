using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Reflection;
using System.Runtime.InteropServices;
using IptvSuite.Application;
using IptvSuite.Domain;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Input;
using Windows.ApplicationModel;
using Windows.Foundation;
using Windows.Storage.Streams;
using Windows.System;
using Windows.UI.Core;

namespace IptvSuite.Windows;

public sealed partial class MainPage : Page, IDisposable
{
    private const int PageSize = 200;
    private const int VolumeStep = 5;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly object _operationSync = new();
    private readonly SemaphoreSlim _playbackControlGate = new(1, 1);
    private CatalogBrowseCoordinator? _coordinator;
    private ChannelLogoCache? _logoCache;
    private PlaybackSessionCoordinator? _playback;
    private ChannelRow? _playbackChannel;
    private CancellationTokenSource _logoPageCancellation = new();
    private int _offset;
    private bool _updatingSelectors;
    private bool _disposed;
    private bool _catalogAdmissionReady;
    private bool _movingTabFocus;
    private bool _isFullscreen;
    private bool _fullscreenTransitionPending;
    private WeakReference<Control>? _focusBeforeFullscreen;
    private long _loadingGeneration;
    private int _activeAsyncOperations;
    private TaskCompletionSource? _operationsDrained;
    private bool _playbackControlGateDisposed;

    public MainPage()
    {
        InitializeComponent();
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
        Unloaded += MainPage_Unloaded;
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
            ? Visibility.Collapsed
            : Visibility.Visible;
        HeaderPanel.Visibility = catalogVisibility;
        FilterPanel.Visibility = catalogVisibility;
        ChannelList.Visibility = catalogVisibility;
        CatalogStatusPanel.Visibility = catalogVisibility;
        CatalogPagingPanel.Visibility = catalogVisibility;
        PageRoot.Padding = isFullscreen ? new Thickness(0) : new Thickness(32);
        Grid.SetColumn(PlaybackPanel, isFullscreen ? 0 : 1);
        Grid.SetColumnSpan(PlaybackPanel, isFullscreen ? 2 : 1);
        FullscreenButton.Content = isFullscreen ? "Exit fullscreen" : "Fullscreen";
        AutomationProperties.SetName(
            FullscreenButton,
            isFullscreen ? "Exit fullscreen" : "Enter fullscreen");
        PlaybackState state = _playback?.Current.State ?? PlaybackState.Closed;
        FullscreenButton.IsEnabled = _isFullscreen || CanChangePlaybackControls(state);
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
        PlaybackState state = _playback?.Current.State ?? PlaybackState.Closed;
        FullscreenButton.IsEnabled = _isFullscreen || CanChangePlaybackControls(state);
        PlaybackStatusText.Text = "Fullscreen is unavailable.";
        DispatcherQueue.TryEnqueue(RestoreFocusAfterFullscreen);
    }

    internal async Task InitializeAsync(
        ICatalogBrowser catalogBrowser,
        ChannelLogoCache logoCache,
        PlaybackSessionCoordinator playback)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(catalogBrowser);
        ArgumentNullException.ThrowIfNull(logoCache);
        ArgumentNullException.ThrowIfNull(playback);
        if (_coordinator is not null || _playback is not null)
        {
            throw new InvalidOperationException("The application page is already initialized.");
        }

        _coordinator = new CatalogBrowseCoordinator(catalogBrowser);
        _logoCache = logoCache;
        _playback = playback;
        _catalogAdmissionReady = true;
        _playback.StateChanged += Playback_StateChanged;
        ApplyPlaybackState(_playback.Current);
        await LoadSourcesAsync();
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
        PreviousButton.IsEnabled = false;
        NextButton.IsEnabled = false;
        StatusText.Text = "Pending source cleanup must finish before the catalog can be opened.";
    }

    private async Task LoadSourcesAsync()
    {
        using AsyncOperationLease operation = BeginAsyncOperation();
        CatalogBrowseCoordinator coordinator = _coordinator ?? throw new InvalidOperationException("The catalog page is not initialized.");
        long loadingGeneration = BeginLoading();
        try
        {
            IReadOnlyList<CatalogSourceItem> sources = await coordinator.ReadSourcesAsync(_lifetime.Token);
            _updatingSelectors = true;
            SourceSelector.ItemsSource = sources;
            SourceSelector.SelectedIndex = sources.Count == 0 ? -1 : 0;
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
        if (_disposed || _coordinator is null ||
            SourceSelector.SelectedItem is not CatalogSourceItem source)
        {
            return;
        }

        using AsyncOperationLease operation = BeginAsyncOperation();
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
            _logoPageCancellation.Cancel();
            _logoPageCancellation.Dispose();
            _logoPageCancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
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
            StatusText.Text = result.Channels.TotalCount == 0 ? "No channels match the current filters." : $"Showing {first}–{last} of {result.Channels.TotalCount} channels.";
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
    }

    private void UpdatePaging(int totalCount)
    {
        PreviousButton.IsEnabled = _offset > 0;
        NextButton.IsEnabled = _offset + PageSize < totalCount;
    }

    private async void SourceSelector_SelectionChanged(object sender, SelectionChangedEventArgs e) { if (!_updatingSelectors) { _offset = 0; await BrowseAsync(false); } }
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
        if (_disposed || playback is null || e.ClickedItem is not ChannelRow channel)
        {
            return;
        }

        _playbackChannel = channel;
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

    private void ApplyPlaybackState(PlaybackSessionSnapshot snapshot)
    {
        PlaybackSessionCoordinator? playback = _playback;
        if (_disposed || playback is null)
        {
            return;
        }

        PlaybackSessionSnapshot current = playback.Current;
        if (current.SessionId != snapshot.SessionId || current.State != snapshot.State)
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
        else if (snapshot.State == PlaybackState.Closed)
        {
            PlaybackChannelText.Text = "No channel selected.";
        }

        PlaybackStatusText.Text = snapshot.State switch
        {
            PlaybackState.Opening => "Opening channel.",
            PlaybackState.Buffering => "Buffering channel.",
            PlaybackState.Playing => "Channel is playing.",
            PlaybackState.Paused => "Playback paused.",
            PlaybackState.Stopping => "Stopping playback.",
            PlaybackState.Failed => "Playback is unavailable.",
            _ => "Playback stopped.",
        };
        PlayButton.IsEnabled = snapshot.State == PlaybackState.Paused;
        PauseButton.IsEnabled = snapshot.State == PlaybackState.Playing;
        StopButton.IsEnabled = snapshot.State is
            PlaybackState.Opening or
            PlaybackState.Buffering or
            PlaybackState.Playing or
            PlaybackState.Paused or
            PlaybackState.Failed;

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
    }

    private static bool CanChangePlaybackControls(PlaybackState state) =>
        state is PlaybackState.Buffering or PlaybackState.Playing or PlaybackState.Paused;

    private async void ChannelList_ContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (_disposed || args.InRecycleQueue || args.Item is not ChannelRow row ||
            !row.HasLogo || row.LogoSource is not null || _logoCache is null)
        {
            return;
        }
        using AsyncOperationLease operation = BeginAsyncOperation();
        long generation = row.BeginLogoLoad();
        try
        {
            ChannelLogoImage? logo = await _logoCache.GetAsync(row.SourceId, row.ChannelId, _logoPageCancellation.Token);
            if (_disposed || logo is null || !row.IsCurrentLogoLoad(generation)) return;
            using var stream = new InMemoryRandomAccessStream();
            using (var writer = new DataWriter(stream))
            {
                writer.WriteBytes(logo.Content.ToArray());
                await writer.StoreAsync();
                writer.DetachStream();
            }
            stream.Seek(0);
            var image = new BitmapImage();
            await image.SetSourceAsync(stream);
            if (!_disposed && row.IsCurrentLogoLoad(generation)) row.LogoSource = image;
        }
        catch (OperationCanceledException) when (_logoPageCancellation.IsCancellationRequested) { }
        catch (Exception) { }
    }

    private void MainPage_Unloaded(object sender, RoutedEventArgs e)
    {
        Dispose();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _catalogAdmissionReady = false;
        if (_playback is not null)
        {
            _playback.StateChanged -= Playback_StateChanged;
            _playback = null;
        }

        _lifetime.Cancel();
        _logoPageCancellation.Cancel();
        _coordinator?.Dispose();
        FullscreenToggleRequested = null;
        _lifetime.Dispose();
        _logoPageCancellation.Dispose();
        DisposePlaybackControlGateIfDrained();
        GC.SuppressFinalize(this);
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
        private ImageSource? _logoSource;
        private long _logoGeneration;
        public SourceId SourceId { get; set; }
        public ChannelId ChannelId { get; set; }
        public string Name { get; set; } = string.Empty;
        public int? Number { get; set; }
        public bool HasLogo { get; set; }
        public ImageSource? LogoSource { get => _logoSource; set { _logoSource = value; PropertyChanged?.Invoke(this, new(nameof(LogoSource))); } }
        public event PropertyChangedEventHandler? PropertyChanged;
        internal long BeginLogoLoad() => Interlocked.Increment(ref _logoGeneration);
        internal bool IsCurrentLogoLoad(long generation) => generation == Volatile.Read(ref _logoGeneration);
    }
}
