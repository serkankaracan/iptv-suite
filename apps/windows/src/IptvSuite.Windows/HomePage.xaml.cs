using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace IptvSuite.Windows;

public sealed partial class HomePage : Page
{
    private HomeLayoutState? _layoutState;

    public HomePage()
    {
        InitializeComponent();
    }

    internal event EventHandler<AppSectionRequestedEventArgs>? SectionRequested;

    internal void SetCounts(long liveTv, long movies, long series)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(liveTv);
        ArgumentOutOfRangeException.ThrowIfNegative(movies);
        ArgumentOutOfRangeException.ThrowIfNegative(series);
        long total = checked(liveTv + movies + series);
        LiveTvCountText.Text = $"{liveTv:N0} channels";
        MovieCountText.Text = $"{movies:N0} movies";
        SeriesCountText.Text = $"{series:N0} series";
        TotalCountText.Text = total.ToString("N0", System.Globalization.CultureInfo.CurrentCulture);
    }

    private void LiveTvCard_Click(object sender, RoutedEventArgs e) => Request(AppSection.LiveTv);

    private void MoviesCard_Click(object sender, RoutedEventArgs e) => Request(AppSection.Movies);

    private void SeriesCard_Click(object sender, RoutedEventArgs e) => Request(AppSection.Series);

    private void SourcesCard_Click(object sender, RoutedEventArgs e) => Request(AppSection.Sources);

    private void Request(AppSection section) =>
        SectionRequested?.Invoke(this, new AppSectionRequestedEventArgs(section));

    private void HomePage_Loaded(object sender, RoutedEventArgs e) =>
        UpdateResponsiveLayout(ActualWidth);

    private void HomePage_SizeChanged(object sender, SizeChangedEventArgs e) =>
        UpdateResponsiveLayout(e.NewSize.Width);

    private void UpdateResponsiveLayout(double width)
    {
        HomeLayoutState nextState = width switch
        {
            >= 1180 => HomeLayoutState.Wide,
            >= 820 => HomeLayoutState.Standard,
            >= 620 => HomeLayoutState.Narrow,
            _ => HomeLayoutState.Compact,
        };
        if (_layoutState == nextState)
        {
            return;
        }

        _layoutState = nextState;
        bool showHeroVisual = nextState == HomeLayoutState.Wide;
        HeroVisual.Visibility = showHeroVisual ? Visibility.Visible : Visibility.Collapsed;
        HeroVisualColumn.Width = showHeroVisual
            ? new GridLength(280)
            : new GridLength(0);
        LocalWorkspaceBadge.Visibility = nextState == HomeLayoutState.Compact
            ? Visibility.Collapsed
            : Visibility.Visible;
        PrimaryInstructionText.Visibility = nextState is HomeLayoutState.Wide or HomeLayoutState.Standard
            ? Visibility.Visible
            : Visibility.Collapsed;
        HomeLayoutGrid.Padding = nextState == HomeLayoutState.Compact
            ? new Thickness(16, 20, 16, 28)
            : new Thickness(36, 28, 36, 40);
        HeroCard.Padding = nextState == HomeLayoutState.Compact
            ? new Thickness(20)
            : new Thickness(32);
        HeroActions.Orientation = nextState == HomeLayoutState.Compact
            ? Orientation.Vertical
            : Orientation.Horizontal;
        HorizontalAlignment actionAlignment = nextState == HomeLayoutState.Compact
            ? HorizontalAlignment.Stretch
            : HorizontalAlignment.Left;
        HeroLiveButton.HorizontalAlignment = actionAlignment;
        HeroSourcesButton.HorizontalAlignment = actionAlignment;

        ConfigurePrimaryCards(nextState);
        ConfigureWorkspace(nextState is HomeLayoutState.Wide or HomeLayoutState.Standard);
        ConfigurePlannedCards(nextState switch
        {
            HomeLayoutState.Wide => 4,
            HomeLayoutState.Standard or HomeLayoutState.Narrow => 2,
            _ => 1,
        });
    }

    private void ConfigurePrimaryCards(HomeLayoutState state)
    {
        if (state == HomeLayoutState.Wide)
        {
            SetColumnWidths(PrimaryColumn0, PrimaryColumn1, PrimaryColumn2, 3);
            SetRowHeights(PrimaryRow0, PrimaryRow1, PrimaryRow2, 1);
            Place(LiveTvCard, row: 0, column: 0);
            Place(MoviesCard, row: 0, column: 1);
            Place(SeriesCard, row: 0, column: 2);
            PrimaryCardGrid.ColumnSpacing = 18;
            PrimaryCardGrid.RowSpacing = 0;
            return;
        }

        if (state == HomeLayoutState.Standard)
        {
            SetColumnWidths(PrimaryColumn0, PrimaryColumn1, PrimaryColumn2, 2);
            SetRowHeights(PrimaryRow0, PrimaryRow1, PrimaryRow2, 2);
            Place(LiveTvCard, row: 0, column: 0);
            Place(MoviesCard, row: 0, column: 1);
            Place(SeriesCard, row: 1, column: 0, columnSpan: 2);
            PrimaryCardGrid.ColumnSpacing = 18;
            PrimaryCardGrid.RowSpacing = 14;
            return;
        }

        SetColumnWidths(PrimaryColumn0, PrimaryColumn1, PrimaryColumn2, 1);
        SetRowHeights(PrimaryRow0, PrimaryRow1, PrimaryRow2, 3);
        Place(LiveTvCard, row: 0, column: 0);
        Place(MoviesCard, row: 1, column: 0);
        Place(SeriesCard, row: 2, column: 0);
        PrimaryCardGrid.ColumnSpacing = 0;
        PrimaryCardGrid.RowSpacing = 14;
    }

    private void ConfigureWorkspace(bool useTwoColumns)
    {
        WorkspaceColumn0.Width = new GridLength(useTwoColumns ? 2 : 1, GridUnitType.Star);
        WorkspaceColumn1.Width = useTwoColumns
            ? new GridLength(1, GridUnitType.Star)
            : new GridLength(0);
        WorkspaceRow0.Height = GridLength.Auto;
        WorkspaceRow1.Height = useTwoColumns ? new GridLength(0) : GridLength.Auto;
        Place(ManageSourcesCard, row: 0, column: 0);
        Place(
            CatalogTotalCard,
            row: useTwoColumns ? 0 : 1,
            column: useTwoColumns ? 1 : 0);
        WorkspaceGrid.ColumnSpacing = useTwoColumns ? 18 : 0;
        WorkspaceGrid.RowSpacing = useTwoColumns ? 0 : 14;
    }

    private void ConfigurePlannedCards(int columnCount)
    {
        ColumnDefinition[] columns =
        [
            PlannedColumn0,
            PlannedColumn1,
            PlannedColumn2,
            PlannedColumn3,
        ];
        RowDefinition[] rows =
        [
            PlannedRow0,
            PlannedRow1,
            PlannedRow2,
            PlannedRow3,
        ];
        FrameworkElement[] cards =
        [
            PlannedFavoritesCard,
            PlannedGuideCard,
            PlannedContinueCard,
            PlannedDownloadsCard,
        ];
        int rowCount = (cards.Length + columnCount - 1) / columnCount;
        for (int index = 0; index < columns.Length; index++)
        {
            columns[index].Width = index < columnCount
                ? new GridLength(1, GridUnitType.Star)
                : new GridLength(0);
            rows[index].Height = index < rowCount
                ? GridLength.Auto
                : new GridLength(0);
        }

        for (int index = 0; index < cards.Length; index++)
        {
            Place(cards[index], index / columnCount, index % columnCount);
        }
    }

    private static void SetColumnWidths(
        ColumnDefinition first,
        ColumnDefinition second,
        ColumnDefinition third,
        int visibleCount)
    {
        first.Width = new GridLength(1, GridUnitType.Star);
        second.Width = visibleCount >= 2
            ? new GridLength(1, GridUnitType.Star)
            : new GridLength(0);
        third.Width = visibleCount >= 3
            ? new GridLength(1, GridUnitType.Star)
            : new GridLength(0);
    }

    private static void SetRowHeights(
        RowDefinition first,
        RowDefinition second,
        RowDefinition third,
        int visibleCount)
    {
        first.Height = GridLength.Auto;
        second.Height = visibleCount >= 2 ? GridLength.Auto : new GridLength(0);
        third.Height = visibleCount >= 3 ? GridLength.Auto : new GridLength(0);
    }

    private static void Place(
        FrameworkElement element,
        int row,
        int column,
        int columnSpan = 1)
    {
        Grid.SetRow(element, row);
        Grid.SetColumn(element, column);
        Grid.SetColumnSpan(element, columnSpan);
    }

    private enum HomeLayoutState
    {
        Wide,
        Standard,
        Narrow,
        Compact,
    }
}
