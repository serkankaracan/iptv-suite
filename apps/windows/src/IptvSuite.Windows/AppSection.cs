namespace IptvSuite.Windows;

internal enum AppSection
{
    Home,
    LiveTv,
    Movies,
    Series,
    Sources,
}

internal sealed class AppSectionRequestedEventArgs(AppSection section) : EventArgs
{
    internal AppSection Section { get; } = section;
}
