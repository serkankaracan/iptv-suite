using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace IptvSuite.Windows;

public sealed partial class HomePage : Page
{
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
}
