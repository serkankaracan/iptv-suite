using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Reflection;
using System.Runtime.InteropServices;
using IptvSuite.Application;
using IptvSuite.Domain;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.ApplicationModel;
using Windows.Storage.Streams;
using Windows.System;
using Windows.UI.Core;

namespace IptvSuite.Windows;

public sealed partial class MainPage : Page, IDisposable
{
    private const int PageSize = 200;
    private readonly CancellationTokenSource _lifetime = new();
    private CatalogBrowseCoordinator? _coordinator;
    private ChannelLogoCache? _logoCache;
    private CancellationTokenSource _logoPageCancellation = new();
    private int _offset;
    private bool _updatingSelectors;
    private bool _disposed;
    private long _loadingGeneration;

    public MainPage()
    {
        InitializeComponent();
        var filterKeyHandler = new KeyEventHandler(CatalogFilter_KeyDown);
        SourceSelector.AddHandler(UIElement.KeyDownEvent, filterKeyHandler, handledEventsToo: true);
        CategorySelector.AddHandler(UIElement.KeyDownEvent, filterKeyHandler, handledEventsToo: true);
        SearchBox.AddHandler(UIElement.KeyDownEvent, filterKeyHandler, handledEventsToo: true);
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

    internal void Initialize(ICatalogBrowser catalogBrowser, ChannelLogoCache logoCache)
    {
        ArgumentNullException.ThrowIfNull(catalogBrowser);
        ArgumentNullException.ThrowIfNull(logoCache);
        if (_coordinator is not null) throw new InvalidOperationException("The catalog page is already initialized.");
        _coordinator = new CatalogBrowseCoordinator(catalogBrowser);
        _logoCache = logoCache;
        _ = LoadSourcesAsync();
    }

    private async Task LoadSourcesAsync()
    {
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
        if (_coordinator is null || SourceSelector.SelectedItem is not CatalogSourceItem source) return;
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
        return generation;
    }

    private void EndLoading(long generation)
    {
        if (generation != Volatile.Read(ref _loadingGeneration)) return;
        LoadingIndicator.IsActive = false;
        SourceSelector.IsEnabled = true;
        CategorySelector.IsEnabled = true;
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

    private void CatalogFilter_KeyDown(object sender, KeyRoutedEventArgs args)
    {
        if (args.Key != VirtualKey.Tab) return;

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

        if (target is not null && target.Focus(FocusState.Keyboard))
        {
            args.Handled = true;
        }
    }

    private async void PreviousButton_Click(object sender, RoutedEventArgs e) { _offset = Math.Max(0, _offset - PageSize); await BrowseAsync(false); }
    private async void NextButton_Click(object sender, RoutedEventArgs e) { _offset += PageSize; await BrowseAsync(false); }

    private async void ChannelList_ContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (args.InRecycleQueue || args.Item is not ChannelRow row || !row.HasLogo || row.LogoSource is not null || _logoCache is null) return;
        long generation = row.BeginLogoLoad();
        try
        {
            ChannelLogoImage? logo = await _logoCache.GetAsync(row.SourceId, row.ChannelId, _logoPageCancellation.Token);
            if (logo is null || !row.IsCurrentLogoLoad(generation)) return;
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
            if (row.IsCurrentLogoLoad(generation)) row.LogoSource = image;
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
        _lifetime.Cancel();
        _logoPageCancellation.Cancel();
        _coordinator?.Dispose();
        _lifetime.Dispose();
        _logoPageCancellation.Dispose();
        GC.SuppressFinalize(this);
    }

    private sealed record CategoryOption(string Name, CategoryId? CategoryId);

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
