using System.Collections.ObjectModel;
using IptvSuite.Application;
using IptvSuite.Domain;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace IptvSuite.Windows;

internal enum ContentLibraryKind
{
    Movies,
    Series,
}

public sealed partial class ContentLibraryPage : Page, IDisposable
{
    private const int PageSize = 200;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly object _operationSync = new();
    private CancellationTokenSource? _loadCancellation;
    private CancellationTokenSource? _searchDebounceCancellation;
    private ISourceManagementCatalog? _sourceCatalog;
    private IContentCatalogBrowser? _contentCatalog;
    private Func<SourceId, SeriesId, CancellationToken, ValueTask<bool>>? _loadSeriesDetails;
    private ContentLibraryKind _kind;
    private ContentLibrarySourceOption? _selectedSource;
    private ContentLibraryRow? _selectedSeries;
    private ContentLibraryRow? _selectedSeason;
    private ContentLibraryLevel _level = ContentLibraryLevel.TopLevel;
    private int _offset;
    private long _loadGeneration;
    private int _activeOperations;
    private TaskCompletionSource? _operationsDrained;
    private bool _updatingSelectors;
    private bool _configured;
    private bool _navigationActive;
    private bool _disposed;
    private bool _lifetimeDisposed;

    public ContentLibraryPage()
    {
        InitializeComponent();
        LibraryItems.ItemsSource = Items;
    }

    internal ObservableCollection<ContentLibraryRow> Items { get; } = [];

    internal event EventHandler<ContentPlaybackRequestedEventArgs>? PlaybackRequested;

    internal void Configure(
        ContentLibraryKind kind,
        ISourceManagementCatalog sourceCatalog,
        IContentCatalogBrowser contentCatalog,
        Func<SourceId, SeriesId, CancellationToken, ValueTask<bool>>? loadSeriesDetails = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_configured || !Enum.IsDefined(kind))
        {
            throw new InvalidOperationException("The content library is already configured.");
        }

        _kind = kind;
        _sourceCatalog = sourceCatalog ?? throw new ArgumentNullException(nameof(sourceCatalog));
        _contentCatalog = contentCatalog ?? throw new ArgumentNullException(nameof(contentCatalog));
        _loadSeriesDetails = loadSeriesDetails;
        string title = kind == ContentLibraryKind.Movies ? "Movies" : "Series";
        SectionTitleText.Text = title;
        SectionDescriptionText.Text = kind == ContentLibraryKind.Movies
            ? "Browse and play movies from an authorized Xtream-compatible source."
            : "Choose a series, season and episode from an authorized Xtream-compatible source.";
        LibrarySearchBox.PlaceholderText = $"Search {title.ToLowerInvariant()}";
        LibraryCategorySelector.ItemsSource = new[]
        {
            new ContentLibraryCategoryOption("All categories", null),
        };
        LibraryCategorySelector.SelectedIndex = 0;
        _configured = true;
    }

    internal void Activate()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_configured)
        {
            throw new InvalidOperationException("The content library is not configured.");
        }

        _navigationActive = true;
    }

    internal void Deactivate()
    {
        if (_disposed || !_navigationActive)
        {
            return;
        }

        _navigationActive = false;
        CancelPendingRequests();
        SetBusy(false);
    }

    internal Task RefreshAsync() => _navigationActive
        ? ReloadSourcesAsync(_selectedSource?.SourceId)
        : Task.CompletedTask;

    private async Task ReloadSourcesAsync(SourceId? preferredSourceId = null)
    {
        if (_disposed || !_navigationActive || _sourceCatalog is null)
        {
            return;
        }

        CancelSearchDebounce();
        (long generation, CancellationTokenSource cancellation) = BeginLoad();
        CancellationToken token = cancellation.Token;
        try
        {
            IReadOnlyList<SourceManagementSummary> sources =
                await _sourceCatalog.ReadAsync(token);
            ContentLibrarySourceOption[] options = sources
                .Where(source => source.Status == ContentSourceStatus.Ready)
                .Select(source => new ContentLibrarySourceOption(
                    source.SourceId,
                    source.Name,
                    source.UsesInsecureHttp,
                    _kind == ContentLibraryKind.Movies
                        ? source.Counts.MovieCount
                        : source.Counts.SeriesCount))
                .ToArray();
            if (!IsCurrent(generation))
            {
                return;
            }

            _updatingSelectors = true;
            LibrarySourceSelector.ItemsSource = options;
            _selectedSource = preferredSourceId.HasValue
                ? options.FirstOrDefault(item => item.SourceId.Equals(preferredSourceId.Value))
                : null;
            _selectedSource ??= options.Length == 0 ? null : options[0];
            LibrarySourceSelector.SelectedItem = _selectedSource;
            _updatingSelectors = false;
            ResetHierarchy();
            if (_selectedSource is null)
            {
                Items.Clear();
                SetTopLevelStatus(0, 0);
                return;
            }

            await LoadCategoriesAsync(generation, token);
            await BrowseTopLevelAsync(generation, token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (IsRecoverable(exception))
        {
            if (IsCurrent(generation))
            {
                LibraryStatusText.Text = "The content library could not be loaded safely.";
            }
        }
        finally
        {
            EndLoad(generation, cancellation);
        }
    }

    private async Task ReloadTopLevelAsync(
        bool reloadCategories = false,
        bool cancelSearchDebounce = true)
    {
        if (_disposed || !_navigationActive ||
            _selectedSource is null || _contentCatalog is null)
        {
            return;
        }

        if (cancelSearchDebounce)
        {
            CancelSearchDebounce();
        }

        (long generation, CancellationTokenSource cancellation) = BeginLoad();
        CancellationToken token = cancellation.Token;
        try
        {
            if (reloadCategories)
            {
                await LoadCategoriesAsync(generation, token);
            }

            await BrowseTopLevelAsync(generation, token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (IsRecoverable(exception))
        {
            if (IsCurrent(generation))
            {
                LibraryStatusText.Text = "The content request failed safely.";
            }
        }
        finally
        {
            EndLoad(generation, cancellation);
        }
    }

    private async Task LoadCategoriesAsync(long generation, CancellationToken token)
    {
        IContentCatalogBrowser contentCatalog = _contentCatalog ??
            throw new InvalidOperationException("The content library is not configured.");
        ContentLibrarySourceOption source = _selectedSource ??
            throw new InvalidOperationException("A content source is required.");
        ContentKind contentKind = _kind == ContentLibraryKind.Movies
            ? ContentKind.Movie
            : ContentKind.Series;
        IReadOnlyList<CatalogCategoryItem> categories =
            await contentCatalog.ReadCategoriesAsync(source.SourceId, contentKind, token);
        if (!IsCurrent(generation))
        {
            return;
        }

        var options = new List<ContentLibraryCategoryOption>(categories.Count + 1)
        {
            new("All categories", null),
        };
        options.AddRange(categories.Select(category =>
            new ContentLibraryCategoryOption(category.Name, category.CategoryId)));
        _updatingSelectors = true;
        LibraryCategorySelector.ItemsSource = options;
        LibraryCategorySelector.SelectedIndex = 0;
        LibraryCategorySelector.IsEnabled = options.Count > 1;
        _updatingSelectors = false;
    }

    private async Task BrowseTopLevelAsync(long generation, CancellationToken token)
    {
        IContentCatalogBrowser contentCatalog = _contentCatalog ??
            throw new InvalidOperationException("The content library is not configured.");
        ContentLibrarySourceOption source = _selectedSource ??
            throw new InvalidOperationException("A content source is required.");
        CategoryId? categoryId =
            (LibraryCategorySelector.SelectedItem as ContentLibraryCategoryOption)?.CategoryId;

        ContentLibraryRow[] rows;
        int totalCount;
        if (_kind == ContentLibraryKind.Movies)
        {
            ContentPage<ContentMovieItem> page = await contentCatalog.ReadMoviesAsync(
                source.SourceId,
                categoryId,
                LibrarySearchBox.Text,
                _offset,
                PageSize,
                token);
            totalCount = page.TotalCount;
            rows = page.Items.Select(item => ContentLibraryRow.ForMovie(
                source.SourceId,
                item.MovieId,
                item.Name,
                item.IsAdult ? "Movie · Adult hint" : "Movie")).ToArray();
        }
        else
        {
            ContentPage<ContentSeriesItem> page = await contentCatalog.ReadSeriesAsync(
                source.SourceId,
                categoryId,
                LibrarySearchBox.Text,
                _offset,
                PageSize,
                token);
            totalCount = page.TotalCount;
            rows = page.Items.Select(item => ContentLibraryRow.ForSeries(
                source.SourceId,
                item.SeriesId,
                item.Name,
                item.IsAdult ? "Series · Adult hint" : "Series")).ToArray();
        }

        if (!IsCurrent(generation))
        {
            return;
        }

        _level = ContentLibraryLevel.TopLevel;
        _selectedSeries = null;
        _selectedSeason = null;
        LibraryContextText.Text = string.Empty;
        LibraryBackButton.Visibility = Visibility.Collapsed;
        LibraryBackButton.IsEnabled = false;
        LibrarySearchBox.IsEnabled = true;
        LibraryCategorySelector.IsEnabled = LibraryCategorySelector.Items.Count > 1;
        ReplaceItems(rows);
        SetTopLevelStatus(totalCount, rows.Length);
        LibraryPreviousButton.IsEnabled = _offset > 0;
        LibraryNextButton.IsEnabled = _offset + PageSize < totalCount;
    }

    private Task OpenSeriesAsync(ContentLibraryRow series) =>
        LoadSeasonsAsync(series, refreshDetails: true);

    private Task ShowCachedSeasonsAsync(ContentLibraryRow series) =>
        LoadSeasonsAsync(series, refreshDetails: false);

    private async Task LoadSeasonsAsync(
        ContentLibraryRow series,
        bool refreshDetails)
    {
        if (_disposed || !_navigationActive ||
            _contentCatalog is null || !series.SeriesId.HasValue)
        {
            return;
        }

        CancelSearchDebounce();
        (long generation, CancellationTokenSource cancellation) = BeginLoad();
        CancellationToken token = cancellation.Token;
        try
        {
            bool detailsRefreshed = !refreshDetails || _loadSeriesDetails is null ||
                await _loadSeriesDetails(series.SourceId, series.SeriesId.Value, token);

            IReadOnlyList<ContentSeasonItem> seasons = await _contentCatalog.ReadSeasonsAsync(
                series.SourceId,
                series.SeriesId.Value,
                token);
            if (!IsCurrent(generation))
            {
                return;
            }

            if (!detailsRefreshed && seasons.Count == 0)
            {
                LibraryStatusText.Text =
                    "This series' episode catalog could not be loaded safely.";
                return;
            }

            _selectedSeries = series;
            _selectedSeason = null;
            _level = ContentLibraryLevel.Seasons;
            ReplaceItems(seasons.Select(item => ContentLibraryRow.ForSeason(
                series.SourceId,
                item.SeasonId,
                item.SeriesId,
                item.Name,
                $"Season {item.Number}")));
            SetNestedState(series.Title, seasons.Count, "seasons");
            if (!detailsRefreshed)
            {
                LibraryStatusText.Text +=
                    " Showing the last cached episode catalog because refresh is unavailable.";
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (IsRecoverable(exception))
        {
            if (IsCurrent(generation))
            {
                LibraryStatusText.Text = "The series catalog could not be loaded safely.";
            }
        }
        finally
        {
            EndLoad(generation, cancellation);
        }
    }

    private async Task OpenSeasonAsync(ContentLibraryRow season)
    {
        if (_disposed || !_navigationActive ||
            _contentCatalog is null || !season.SeasonId.HasValue)
        {
            return;
        }

        CancelSearchDebounce();
        (long generation, CancellationTokenSource cancellation) = BeginLoad();
        CancellationToken token = cancellation.Token;
        try
        {
            IReadOnlyList<ContentEpisodeItem> episodes = await _contentCatalog.ReadEpisodesAsync(
                season.SourceId,
                season.SeasonId.Value,
                token);
            if (!IsCurrent(generation))
            {
                return;
            }

            _selectedSeason = season;
            _level = ContentLibraryLevel.Episodes;
            ReplaceItems(episodes.Select(item => ContentLibraryRow.ForEpisode(
                season.SourceId,
                item.EpisodeId,
                item.Name,
                item.Duration.HasValue
                    ? $"Episode {item.Number} · {FormatDuration(item.Duration.Value)}"
                    : $"Episode {item.Number}")));
            SetNestedState(
                $"{_selectedSeries?.Title} · {season.Title}",
                episodes.Count,
                "episodes");
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (IsRecoverable(exception))
        {
            if (IsCurrent(generation))
            {
                LibraryStatusText.Text = "The episode catalog could not be loaded safely.";
            }
        }
        finally
        {
            EndLoad(generation, cancellation);
        }
    }

    private void SetNestedState(string context, int count, string itemKind)
    {
        LibraryContextText.Text = context;
        LibraryBackButton.Visibility = Visibility.Visible;
        LibraryBackButton.IsEnabled = true;
        LibrarySearchBox.IsEnabled = false;
        LibraryCategorySelector.IsEnabled = false;
        LibraryPreviousButton.IsEnabled = false;
        LibraryNextButton.IsEnabled = false;
        LibraryStatusText.Text = count == 0
            ? $"No {itemKind} are available."
            : $"{count:N0} {itemKind}.";
    }

    private void SetTopLevelStatus(int totalCount, int visibleCount)
    {
        string kind = _kind == ContentLibraryKind.Movies ? "movies" : "series";
        LibraryStatusText.Text = totalCount == 0
            ? $"No {kind} are available from the selected source."
            : $"Showing {_offset + 1:N0}–{_offset + visibleCount:N0} of {totalCount:N0} {kind}.";
        if (_selectedSource?.UsesInsecureHttp == true)
        {
            LibraryStatusText.Text +=
                " Warning: cleartext HTTP traffic is unencrypted and can be observed or modified in transit (MITM).";
        }
    }

    private void ReplaceItems(IEnumerable<ContentLibraryRow> items)
    {
        Items.Clear();
        foreach (ContentLibraryRow item in items)
        {
            Items.Add(item);
        }
    }

    private void ResetHierarchy()
    {
        _offset = 0;
        _level = ContentLibraryLevel.TopLevel;
        _selectedSeries = null;
        _selectedSeason = null;
        LibraryContextText.Text = string.Empty;
        LibraryBackButton.Visibility = Visibility.Collapsed;
        LibraryBackButton.IsEnabled = false;
        LibrarySearchBox.IsEnabled = _selectedSource is not null;
        LibraryPreviousButton.IsEnabled = false;
        LibraryNextButton.IsEnabled = false;
    }

    private async void LibraryItems_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (_disposed || !_navigationActive ||
            e.ClickedItem is not ContentLibraryRow row)
        {
            return;
        }

        switch (row.Kind)
        {
            case ContentLibraryRowKind.Movie when row.MovieId.HasValue:
                PlaybackRequested?.Invoke(
                    this,
                    ContentPlaybackRequestedEventArgs.ForMovie(
                        row.SourceId,
                        row.MovieId.Value,
                        row.Title));
                break;
            case ContentLibraryRowKind.Series:
                await OpenSeriesAsync(row);
                break;
            case ContentLibraryRowKind.Season:
                await OpenSeasonAsync(row);
                break;
            case ContentLibraryRowKind.Episode when row.EpisodeId.HasValue:
                PlaybackRequested?.Invoke(
                    this,
                    ContentPlaybackRequestedEventArgs.ForEpisode(
                        row.SourceId,
                        row.EpisodeId.Value,
                        row.Title));
                break;
        }
    }

    private async void LibrarySourceSelector_SelectionChanged(
        object sender,
        SelectionChangedEventArgs e)
    {
        if (_disposed || !_navigationActive || _updatingSelectors ||
            LibrarySourceSelector.SelectedItem is not ContentLibrarySourceOption source)
        {
            return;
        }

        _selectedSource = source;
        _offset = 0;
        ResetHierarchy();
        await ReloadTopLevelAsync(reloadCategories: true);
    }

    private async void LibraryCategorySelector_SelectionChanged(
        object sender,
        SelectionChangedEventArgs e)
    {
        if (_disposed || !_navigationActive || _updatingSelectors ||
            _level != ContentLibraryLevel.TopLevel ||
            _selectedSource is null)
        {
            return;
        }

        _offset = 0;
        await ReloadTopLevelAsync();
    }

    private async void LibrarySearchBox_QuerySubmitted(
        AutoSuggestBox sender,
        AutoSuggestBoxQuerySubmittedEventArgs args)
    {
        if (_disposed || !_navigationActive ||
            _level != ContentLibraryLevel.TopLevel || _selectedSource is null)
        {
            return;
        }

        _offset = 0;
        await ReloadTopLevelAsync();
    }

    private async void LibrarySearchBox_TextChanged(
        AutoSuggestBox sender,
        AutoSuggestBoxTextChangedEventArgs args)
    {
        if (_disposed || !_navigationActive ||
            args.Reason != AutoSuggestionBoxTextChangeReason.UserInput ||
            _level != ContentLibraryLevel.TopLevel || _selectedSource is null)
        {
            return;
        }

        using OperationLease operation = BeginOperation();
        CancellationTokenSource debounce = CancellationTokenSource.CreateLinkedTokenSource(
            _lifetime.Token);
        CancellationTokenSource? previous = Interlocked.Exchange(
            ref _searchDebounceCancellation,
            debounce);
        previous?.Cancel();
        try
        {
            await Task.Delay(TimeSpan.FromMilliseconds(300), debounce.Token);
            if (!_navigationActive)
            {
                return;
            }

            _offset = 0;
            await ReloadTopLevelAsync(cancelSearchDebounce: false);
        }
        catch (OperationCanceledException) when (debounce.IsCancellationRequested)
        {
        }
        finally
        {
            _ = Interlocked.CompareExchange(
                ref _searchDebounceCancellation,
                null,
                debounce);
            debounce.Dispose();
        }
    }

    private async void LibraryBackButton_Click(object sender, RoutedEventArgs e)
    {
        if (_disposed || !_navigationActive)
        {
            return;
        }

        if (_level == ContentLibraryLevel.Episodes && _selectedSeries is not null)
        {
            await ShowCachedSeasonsAsync(_selectedSeries);
            return;
        }

        _offset = 0;
        await ReloadTopLevelAsync();
    }

    private async void LibraryPreviousButton_Click(object sender, RoutedEventArgs e)
    {
        if (_disposed || !_navigationActive)
        {
            return;
        }

        _offset = Math.Max(0, _offset - PageSize);
        await ReloadTopLevelAsync();
    }

    private async void LibraryNextButton_Click(object sender, RoutedEventArgs e)
    {
        if (_disposed || !_navigationActive)
        {
            return;
        }

        _offset = checked(_offset + PageSize);
        await ReloadTopLevelAsync();
    }

    private (long Generation, CancellationTokenSource Cancellation) BeginLoad()
    {
        BeginDetachedOperation();
        long generation = Interlocked.Increment(ref _loadGeneration);
        CancellationTokenSource replacement = CancellationTokenSource.CreateLinkedTokenSource(
            _lifetime.Token);
        CancellationTokenSource? previous = Interlocked.Exchange(
            ref _loadCancellation,
            replacement);
        previous?.Cancel();
        SetBusy(true);
        return (generation, replacement);
    }

    private void EndLoad(long generation, CancellationTokenSource cancellation)
    {
        if (IsCurrent(generation))
        {
            SetBusy(false);
        }

        _ = Interlocked.CompareExchange(ref _loadCancellation, null, cancellation);
        cancellation.Dispose();
        EndOperation();
    }

    private bool IsCurrent(long generation) =>
        !_disposed && _navigationActive &&
        generation == Volatile.Read(ref _loadGeneration);

    private void CancelPendingRequests()
    {
        Interlocked.Increment(ref _loadGeneration);
        Volatile.Read(ref _searchDebounceCancellation)?.Cancel();
        Volatile.Read(ref _loadCancellation)?.Cancel();
    }

    private void CancelSearchDebounce() =>
        Volatile.Read(ref _searchDebounceCancellation)?.Cancel();

    private void SetBusy(bool busy)
    {
        LibraryProgressRing.IsActive = busy;
        LibraryProgressRing.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        LibraryItems.IsEnabled = !busy;
        LibrarySourceSelector.IsEnabled = !busy;
        LibrarySearchBox.IsEnabled = !busy &&
            _selectedSource is not null &&
            _level == ContentLibraryLevel.TopLevel;
        LibraryBackButton.IsEnabled = !busy && _level != ContentLibraryLevel.TopLevel;
    }

    private static string FormatDuration(TimeSpan value) => value.TotalHours >= 1
        ? $"{(long)value.TotalHours:00}:{value.Minutes:00}:{value.Seconds:00}"
        : $"{value.Minutes:00}:{value.Seconds:00}";

    private static bool IsRecoverable(Exception exception) =>
        exception is not OutOfMemoryException and
        not StackOverflowException and
        not AccessViolationException;

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _navigationActive = false;
        _lifetime.Cancel();
        CancelPendingRequests();
        PlaybackRequested = null;
        DisposeLifetimeIfDrained();
        GC.SuppressFinalize(this);
    }

    internal ValueTask WaitForPendingOperationsAsync()
    {
        lock (_operationSync)
        {
            if (_activeOperations == 0)
            {
                return ValueTask.CompletedTask;
            }

            _operationsDrained ??= new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            return new ValueTask(_operationsDrained.Task);
        }
    }

    private OperationLease BeginOperation()
    {
        BeginDetachedOperation();
        return new OperationLease(this);
    }

    private void BeginDetachedOperation()
    {
        lock (_operationSync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _activeOperations = checked(_activeOperations + 1);
        }
    }

    private void EndOperation()
    {
        TaskCompletionSource? completion = null;
        lock (_operationSync)
        {
            _activeOperations--;
            if (_activeOperations == 0)
            {
                completion = _operationsDrained;
                _operationsDrained = null;
            }
        }

        completion?.TrySetResult();
        DisposeLifetimeIfDrained();
    }

    private void DisposeLifetimeIfDrained()
    {
        CancellationTokenSource? load = null;
        CancellationTokenSource? debounce = null;
        lock (_operationSync)
        {
            if (!_disposed || _activeOperations != 0 || _lifetimeDisposed)
            {
                return;
            }

            _lifetimeDisposed = true;
            load = Interlocked.Exchange(ref _loadCancellation, null);
            debounce = Interlocked.Exchange(ref _searchDebounceCancellation, null);
        }

        load?.Dispose();
        debounce?.Dispose();
        _lifetime.Dispose();
    }

    private readonly struct OperationLease(ContentLibraryPage owner) : IDisposable
    {
        private readonly ContentLibraryPage? _owner = owner;

        public void Dispose() => _owner?.EndOperation();
    }
}

internal sealed record ContentLibrarySourceOption(
    SourceId SourceId,
    string Name,
    bool UsesInsecureHttp,
    int ItemCount);

internal sealed record ContentLibraryCategoryOption(string Name, CategoryId? CategoryId);

internal enum ContentLibraryLevel
{
    TopLevel,
    Seasons,
    Episodes,
}

internal enum ContentLibraryRowKind
{
    Movie,
    Series,
    Season,
    Episode,
}

internal sealed record ContentLibraryRow(
    ContentLibraryRowKind Kind,
    SourceId SourceId,
    MovieId? MovieId,
    SeriesId? SeriesId,
    SeasonId? SeasonId,
    EpisodeId? EpisodeId,
    string Title,
    string Subtitle)
{
    internal static ContentLibraryRow ForMovie(
        SourceId sourceId,
        MovieId movieId,
        string title,
        string subtitle) =>
        new(ContentLibraryRowKind.Movie, sourceId, movieId, null, null, null, title, subtitle);

    internal static ContentLibraryRow ForSeries(
        SourceId sourceId,
        SeriesId seriesId,
        string title,
        string subtitle) =>
        new(ContentLibraryRowKind.Series, sourceId, null, seriesId, null, null, title, subtitle);

    internal static ContentLibraryRow ForSeason(
        SourceId sourceId,
        SeasonId seasonId,
        SeriesId seriesId,
        string title,
        string subtitle) =>
        new(ContentLibraryRowKind.Season, sourceId, null, seriesId, seasonId, null, title, subtitle);

    internal static ContentLibraryRow ForEpisode(
        SourceId sourceId,
        EpisodeId episodeId,
        string title,
        string subtitle) =>
        new(ContentLibraryRowKind.Episode, sourceId, null, null, null, episodeId, title, subtitle);

    public override string ToString() => $"[CONTENT-LIBRARY-ROW:{Kind}]";
}

internal sealed class ContentPlaybackRequestedEventArgs : EventArgs
{
    private ContentPlaybackRequestedEventArgs(
        SourceId sourceId,
        MovieId? movieId,
        EpisodeId? episodeId,
        string displayName)
    {
        SourceId = sourceId;
        MovieId = movieId;
        EpisodeId = episodeId;
        DisplayName = displayName;
    }

    internal SourceId SourceId { get; }

    internal MovieId? MovieId { get; }

    internal EpisodeId? EpisodeId { get; }

    internal string DisplayName { get; }

    internal static ContentPlaybackRequestedEventArgs ForMovie(
        SourceId sourceId,
        MovieId movieId,
        string displayName) => new(sourceId, movieId, null, displayName);

    internal static ContentPlaybackRequestedEventArgs ForEpisode(
        SourceId sourceId,
        EpisodeId episodeId,
        string displayName) => new(sourceId, null, episodeId, displayName);
}
