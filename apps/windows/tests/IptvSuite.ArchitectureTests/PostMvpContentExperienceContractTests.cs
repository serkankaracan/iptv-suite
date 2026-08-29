using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace IptvSuite.ArchitectureTests;

[TestClass]
public sealed class PostMvpContentExperienceContractTests
{
    private static readonly string RepositoryRoot = FindRepositoryRoot();

    [TestMethod]
    public void M17HomeAndNavigationKeepContentSectionsDistinctAndCounted()
    {
        string windowsRoot = WindowsSourceRoot();
        XDocument windowMarkup = LoadMarkup(windowsRoot, "MainWindow.xaml");
        XDocument homeMarkup = LoadMarkup(windowsRoot, "HomePage.xaml");
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        string homeCode = File.ReadAllText(Path.Combine(windowsRoot, "HomePage.xaml.cs"));
        string windowCode = File.ReadAllText(Path.Combine(windowsRoot, "MainWindow.xaml.cs"));
        string catalogContracts = File.ReadAllText(Path.Combine(
            RepositoryRoot,
            "apps",
            "windows",
            "src",
            "IptvSuite.Application",
            "ContentCatalogContracts.cs"));

        (string Name, string Tag)[] navigationItems =
        [
            ("HomeNavigationItem", "Home"),
            ("LiveTvNavigationItem", "LiveTv"),
            ("MoviesNavigationItem", "Movies"),
            ("SeriesNavigationItem", "Series"),
            ("SourcesNavigationItem", "Sources"),
        ];
        foreach ((string name, string tag) in navigationItems)
        {
            XElement item = RequiredNamedElement(windowMarkup, x, name);
            Assert.AreEqual(tag, item.Attribute("Tag")?.Value);
        }

        _ = RequiredNamedElement(windowMarkup, x, "PageHost");
        (string Name, string AutomationId)[] homeCards =
        [
            ("LiveTvCard", "HomeLiveTvCard"),
            ("MoviesCard", "HomeMoviesCard"),
            ("SeriesCard", "HomeSeriesCard"),
        ];
        foreach ((string name, string automationId) in homeCards)
        {
            XElement card = RequiredNamedElement(homeMarkup, x, name);
            Assert.AreEqual(
                automationId,
                card.Attribute("AutomationProperties.AutomationId")?.Value);
        }

        _ = RequiredNamedElement(homeMarkup, x, "LiveTvCountText");
        _ = RequiredNamedElement(homeMarkup, x, "MovieCountText");
        _ = RequiredNamedElement(homeMarkup, x, "SeriesCountText");
        _ = RequiredNamedElement(homeMarkup, x, "TotalCountText");
        Assert.AreEqual(
            "HomeManageSourcesCard",
            RequiredAutomationElement(homeMarkup, "HomeManageSourcesCard")
                .Attribute("AutomationProperties.AutomationId")?.Value);

        StringAssert.Contains(homeCode, "long total = checked(liveTv + movies + series);");
        StringAssert.Contains(catalogContracts, "public sealed record ContentCatalogCounts");
        StringAssert.Contains(catalogContracts, "Catalog counts cannot be negative.");
        StringAssert.Contains(
            catalogContracts,
            "TotalTopLevelCount = checked(liveTvCount + movieCount + seriesCount);");
        StringAssert.Contains(catalogContracts, "ValueTask<ContentCatalogCounts> ReadCountsAsync(");
        StringAssert.Contains(
            windowCode,
            "section is AppSection.Movies or AppSection.Series");
        StringAssert.Contains(windowCode, "AttachOnDemandWorkspace(");
        StringAssert.Contains(
            windowCode,
            "section == AppSection.Movies ? _moviesPage : _seriesPage");
        StringAssert.Contains(windowCode, "AppSection.Sources => _sourceManagerPage");
        StringAssert.Contains(windowCode, ".Counts.LiveTvCount");
        StringAssert.Contains(windowCode, ".Counts.MovieCount");
        StringAssert.Contains(windowCode, ".Counts.SeriesCount");
        Assert.IsFalse(
            Regex.IsMatch(
                windowCode,
                @"_homePage\.SetCounts\([^;]*movies:\s*0[^;]*series:\s*0",
                RegexOptions.CultureInvariant | RegexOptions.Singleline),
            "The Home totals must come from authoritative content counts, not hard-coded VOD zeros.");
    }

    [TestMethod]
    public void M20SignalSlateShellFoundationIsAdaptiveAndKeepsPlannedItemsDisabled()
    {
        string windowsRoot = WindowsSourceRoot();
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        XDocument appMarkup = LoadMarkup(windowsRoot, "App.xaml");
        XDocument windowMarkup = LoadMarkup(windowsRoot, "MainWindow.xaml");
        XDocument homeMarkup = LoadMarkup(windowsRoot, "HomePage.xaml");
        string windowCode = File.ReadAllText(Path.Combine(windowsRoot, "MainWindow.xaml.cs"));
        string homeCode = File.ReadAllText(Path.Combine(windowsRoot, "HomePage.xaml.cs"));

        HashSet<string> resourceKeys = appMarkup
            .Descendants()
            .Select(element => element.Attribute(x + "Key")?.Value)
            .Where(key => !string.IsNullOrWhiteSpace(key))
            .Select(key => key!)
            .ToHashSet(StringComparer.Ordinal);
        string[] requiredResources =
        [
            "ShellWindowBrush",
            "ShellNavigationBrush",
            "ShellContentBrush",
            "ShellCardBrush",
            "ShellAccentBrush",
            "LiveDashboardBrush",
            "MovieDashboardBrush",
            "SeriesDashboardBrush",
        ];
        foreach (string resourceKey in requiredResources)
        {
            Assert.IsTrue(
                resourceKeys.Contains(resourceKey),
                $"The shell resource {resourceKey} must remain defined.");
        }

        XElement navigation = RequiredNamedElement(windowMarkup, x, "AppNavigation");
        Assert.AreEqual("Auto", navigation.Attribute("PaneDisplayMode")?.Value);
        Assert.AreEqual("True", navigation.Attribute("IsPaneOpen")?.Value);
        Assert.AreEqual("False", navigation.Attribute("AlwaysShowHeader")?.Value);
        Assert.AreEqual("64", navigation.Attribute("CompactPaneLength")?.Value);
        Assert.AreEqual("244", navigation.Attribute("OpenPaneLength")?.Value);
        _ = RequiredNamedElement(windowMarkup, x, "ShellContentFrame");
        XElement pageHost = RequiredNamedElement(windowMarkup, x, "PageHost");
        Assert.AreEqual("Stretch", pageHost.Attribute("HorizontalContentAlignment")?.Value);
        Assert.AreEqual("Stretch", pageHost.Attribute("VerticalContentAlignment")?.Value);
        XElement homeScrollViewer = homeMarkup
            .Descendants()
            .Single(element => element.Name.LocalName == "ScrollViewer");
        Assert.AreEqual(
            "Stretch",
            homeScrollViewer.Attribute("HorizontalContentAlignment")?.Value);

        XDocument liveMarkup = LoadMarkup(windowsRoot, "MainPage.xaml");
        foreach ((string name, string header) in new[]
                 {
                     ("SourceSelector", "Playlist source"),
                     ("CategorySelector", "Category"),
                     ("SearchBox", "Search"),
                 })
        {
            XElement filter = RequiredNamedElement(liveMarkup, x, name);
            Assert.AreEqual(header, filter.Attribute("Header")?.Value);
            Assert.AreEqual("Bottom", filter.Attribute("VerticalAlignment")?.Value);
        }

        XDocument libraryMarkup = LoadMarkup(windowsRoot, "ContentLibraryPage.xaml");
        Assert.AreEqual(
            "Search",
            RequiredNamedElement(libraryMarkup, x, "LibrarySearchBox")
                .Attribute("Header")?.Value);

        XElement[] plannedItems = windowMarkup
            .Descendants()
            .Where(element =>
                element.Attribute("Content")?.Value.Contains(
                    "coming soon",
                    StringComparison.OrdinalIgnoreCase) == true)
            .ToArray();
        Assert.IsTrue(plannedItems.Length >= 5);
        foreach (XElement plannedItem in plannedItems)
        {
            Assert.AreEqual("False", plannedItem.Attribute("IsEnabled")?.Value);
        }

        foreach (string countName in new[]
                 {
                     "LiveTvCountText",
                     "MovieCountText",
                     "SeriesCountText",
                     "TotalCountText",
                 })
        {
            Assert.AreEqual(
                "Polite",
                RequiredNamedElement(homeMarkup, x, countName)
                    .Attribute("AutomationProperties.LiveSetting")?.Value);
        }

        StringAssert.Contains(homeCode, ">= 1180 => HomeLayoutState.Wide");
        StringAssert.Contains(homeCode, ">= 820 => HomeLayoutState.Standard");
        StringAssert.Contains(homeCode, ">= 620 => HomeLayoutState.Narrow");
        StringAssert.Contains(homeCode, "HeroActions.Orientation");
        StringAssert.Contains(windowCode, "UpdateShellFullscreenLayout(isFullscreen);");
        StringAssert.Contains(windowCode, "ShellContentFrame.Margin = isFullscreen");
        StringAssert.Contains(windowCode, "ShellContentFrame.CornerRadius = isFullscreen");
        StringAssert.Contains(windowCode, "ShellContentFrame.BorderThickness = isFullscreen");

        string shellMarkup = string.Concat(appMarkup, windowMarkup, homeMarkup);
        Assert.IsFalse(
            shellMarkup.Contains("IPTVnator", StringComparison.OrdinalIgnoreCase),
            "The original shell must not carry the reference product's trademark.");
    }

    [TestMethod]
    public void M17SourceManagerIsSeparateCrudSurfaceAndDoesNotRedisplaySecrets()
    {
        string windowsRoot = WindowsSourceRoot();
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        XDocument windowMarkup = LoadMarkup(windowsRoot, "MainWindow.xaml");
        XDocument managerMarkup = LoadMarkup(windowsRoot, "SourceManagerPage.xaml");
        XDocument liveMarkup = LoadMarkup(windowsRoot, "MainPage.xaml");
        string managerCode = File.ReadAllText(Path.Combine(windowsRoot, "SourceManagerPage.xaml.cs"));
        string contracts = File.ReadAllText(Path.Combine(windowsRoot, "SourceManagerContracts.cs"));
        string windowCode = File.ReadAllText(Path.Combine(windowsRoot, "MainWindow.xaml.cs"));

        Assert.AreEqual(
            "AppNavigationSourcesItem",
            RequiredNamedElement(windowMarkup, x, "SourcesNavigationItem")
                .Attribute("AutomationProperties.AutomationId")?.Value);
        Assert.AreEqual(
            "AppNavigationLiveTvItem",
            RequiredNamedElement(windowMarkup, x, "LiveTvNavigationItem")
                .Attribute("AutomationProperties.AutomationId")?.Value);
        StringAssert.Contains(windowCode, "_sourceManagerPage = new SourceManagerPage();");
        StringAssert.Contains(windowCode, "AppSection.Sources => _sourceManagerPage");

        (string Name, string AutomationId)[] automationContracts =
        [
            ("AddSourceButton", "SourceManagerAddButton"),
            ("SourceList", "SourceManagerList"),
            ("RenameSourceTextBox", "SourceManagerRenameTextBox"),
            ("SourceNameTextBox", "SourceManagerNameTextBox"),
            ("RemotePlaylistLocatorTextBox", "SourceManagerM3uUrlTextBox"),
            ("XtreamServerTextBox", "SourceManagerXtreamServerTextBox"),
            ("XtreamUsernameTextBox", "SourceManagerXtreamUsernameTextBox"),
            ("XtreamPasswordBox", "SourceManagerXtreamPasswordBox"),
            ("SourceAuthorizationCheckBox", "SourceManagerAuthorizationCheckBox"),
            ("SaveSourceButton", "SourceManagerSaveButton"),
            ("CancelEditorButton", "SourceManagerCancelButton"),
            ("RenameSourceButton", "SourceManagerRenameButton"),
            ("RefreshSourceButton", "SourceManagerRefreshButton"),
            ("ReplaceSourceButton", "SourceManagerReplaceButton"),
            ("DeleteSourceButton", "SourceManagerDeleteButton"),
            ("SourceStatusText", "SourceManagerStatusText"),
        ];
        foreach ((string name, string automationId) in automationContracts)
        {
            XElement element = RequiredNamedElement(managerMarkup, x, name);
            Assert.AreEqual(
                automationId,
                element.Attribute("AutomationProperties.AutomationId")?.Value);
        }

        XElement passwordInput = RequiredNamedElement(managerMarkup, x, "XtreamPasswordBox");
        Assert.AreEqual("PasswordBox", passwordInput.Name.LocalName);
        Assert.AreEqual("1024", passwordInput.Attribute("MaxLength")?.Value);
        XElement details = RequiredNamedElement(managerMarkup, x, "SourceDetailsPanel");
        string[] sensitiveEditorNames =
        [
            "RemotePlaylistLocatorTextBox",
            "XtreamServerTextBox",
            "XtreamUsernameTextBox",
            "XtreamPasswordBox",
        ];
        foreach (string sensitiveName in sensitiveEditorNames)
        {
            Assert.IsFalse(
                details.DescendantsAndSelf().Any(element => string.Equals(
                    element.Attribute(x + "Name")?.Value,
                    sensitiveName,
                    StringComparison.Ordinal)),
                $"Saved secret field {sensitiveName} must not be part of the source-details view.");
        }

        XElement operationProgress = RequiredNamedElement(
            managerMarkup,
            x,
            "SourceOperationProgressRing");
        Assert.AreEqual("False", operationProgress.Attribute("IsActive")?.Value);
        Assert.AreEqual("Collapsed", operationProgress.Attribute("Visibility")?.Value);
        XElement authorizationCheckBox = RequiredNamedElement(
            managerMarkup,
            x,
            "SourceAuthorizationCheckBox");
        string authorizationText = string.Join(
            " ",
            authorizationCheckBox
                .DescendantsAndSelf()
                .SelectMany(element => element.Attributes())
                .Where(attribute =>
                    attribute.Name.LocalName is "Content" or "Text" or "AutomationProperties.Name")
                .Select(attribute => attribute.Value));
        StringAssert.Contains(authorizationText, "MITM");

        string[] crudHandlers =
        [
            "AddSourceButton_Click",
            "RenameSourceButton_Click",
            "RefreshSourceButton_Click",
            "ReplaceSourceButton_Click",
            "DeleteSourceButton_Click",
        ];
        foreach (string handler in crudHandlers)
        {
            StringAssert.Contains(managerCode, handler);
        }

        StringAssert.Contains(managerCode, "SourceAuthorizationCheckBox.IsChecked != true");
        StringAssert.Contains(managerCode, "ClearSensitiveEditorFields();");
        StringAssert.Contains(managerCode, "XtreamPasswordBox.Password = string.Empty;");
        StringAssert.Contains(managerCode, "Uri.TryCreate(locator, UriKind.Absolute");
        StringAssert.Contains(managerCode, "Uri.UriSchemeHttp");
        StringAssert.Contains(managerCode, "SourceOperationProgressRing.IsActive = busy;");
        StringAssert.Contains(managerCode, "internal ValueTask WaitForPendingOperationsAsync()");
        StringAssert.Contains(managerCode, "_activeOperations = checked(_activeOperations + 1);");
        StringAssert.Contains(windowCode, "await _sourceManagerPage.WaitForPendingOperationsAsync();");
        StringAssert.Contains(windowCode, "private readonly SemaphoreSlim _navigationGate");
        StringAssert.Contains(windowCode, "await _navigationGate.WaitAsync();");
        StringAssert.Contains(windowCode, "_navigationGate.Release();");
        StringAssert.Contains(contracts, "internal sealed class XtreamSourceInput(");
        StringAssert.Contains(contracts, "internal sealed class RemotePlaylistSourceInput(");
        StringAssert.Contains(contracts, "[DebuggerDisplay(\"[XTREAM-SOURCE-INPUT]\")]");
        StringAssert.Contains(contracts, "[DebuggerDisplay(\"[REMOTE-PLAYLIST-SOURCE-INPUT]\")]");
        StringAssert.Contains(contracts, "public override string ToString() => \"[XTREAM-SOURCE-INPUT]\";");
        StringAssert.Contains(contracts, "public override string ToString() => \"[REMOTE-PLAYLIST-SOURCE-INPUT]\";");
        Assert.IsFalse(contracts.Contains("record XtreamSourceInput", StringComparison.Ordinal));
        Assert.IsFalse(contracts.Contains("record RemotePlaylistSourceInput", StringComparison.Ordinal));
        StringAssert.Contains(windowCode, "_catalogServices.SourceManagement.ReadAsync(");
        Assert.IsFalse(
            Regex.IsMatch(
                windowCode,
                @"ReadSourcesAsync\s*=\s*cancellationToken\s*=>\s*_catalogServices\.Browser\.ReadSourcesAsync",
                RegexOptions.CultureInvariant),
            "Source Manager must include non-ready managed sources instead of using the Ready-only Live TV browser.");
        Assert.IsFalse(windowCode.Contains("is not available yet", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(windowCode.Contains("is being initialized", StringComparison.OrdinalIgnoreCase));

        XElement? legacyPanel = liveMarkup
            .Descendants()
            .SingleOrDefault(element => string.Equals(
                element.Attribute(x + "Name")?.Value,
                "RemotePlaylistOnboardingPanel",
                StringComparison.Ordinal));
        if (legacyPanel is not null)
        {
            Assert.AreEqual(
                "Collapsed",
                legacyPanel.Attribute("Visibility")?.Value,
                "The legacy Live TV onboarding surface must not be visible after Source Manager migration.");
        }
    }

    [TestMethod]
    public void M18AndM19LibrariesBrowseTypedContentAndRoutePlayableItems()
    {
        string windowsRoot = WindowsSourceRoot();
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        XDocument libraryMarkup = LoadMarkup(windowsRoot, "ContentLibraryPage.xaml");
        string libraryCode = File.ReadAllText(Path.Combine(windowsRoot, "ContentLibraryPage.xaml.cs"));
        string windowCode = File.ReadAllText(Path.Combine(windowsRoot, "MainWindow.xaml.cs"));
        string presentationCode = string.Concat(libraryCode, "\n", windowCode);
        string catalogContracts = File.ReadAllText(Path.Combine(
            RepositoryRoot,
            "apps",
            "windows",
            "src",
            "IptvSuite.Application",
            "ContentCatalogContracts.cs"));

        (string Name, string AutomationId)[] libraryControls =
        [
            ("LibrarySourceSelector", "LibrarySourceSelector"),
            ("LibraryCategorySelector", "LibraryCategorySelector"),
            ("LibrarySearchBox", "LibrarySearchBox"),
            ("LibraryItems", "ContentLibraryItems"),
            ("LibraryStatusText", "ContentLibraryStatusText"),
        ];
        foreach ((string name, string automationId) in libraryControls)
        {
            Assert.AreEqual(
                automationId,
                RequiredNamedElement(libraryMarkup, x, name)
                    .Attribute("AutomationProperties.AutomationId")?.Value);
        }

        Assert.IsFalse(string.IsNullOrWhiteSpace(
            RequiredNamedElement(libraryMarkup, x, "LibrarySourceSelector")
                .Attribute("SelectionChanged")?.Value));
        Assert.IsFalse(string.IsNullOrWhiteSpace(
            RequiredNamedElement(libraryMarkup, x, "LibraryCategorySelector")
                .Attribute("SelectionChanged")?.Value));
        Assert.IsTrue(
            !string.IsNullOrWhiteSpace(
                RequiredNamedElement(libraryMarkup, x, "LibrarySearchBox")
                    .Attribute("TextChanged")?.Value) ||
            !string.IsNullOrWhiteSpace(
                RequiredNamedElement(libraryMarkup, x, "LibrarySearchBox")
                    .Attribute("QuerySubmitted")?.Value),
            "The content search surface must trigger a bounded browse operation.");

        StringAssert.Contains(catalogContracts, "ReadMoviesAsync(");
        StringAssert.Contains(catalogContracts, "ReadSeriesAsync(");
        StringAssert.Contains(catalogContracts, "ReadCategoriesAsync(");
        StringAssert.Contains(catalogContracts, "ReadSeasonsAsync(");
        StringAssert.Contains(catalogContracts, "ReadEpisodesAsync(");
        StringAssert.Contains(presentationCode, "ReadCategoriesAsync(");
        StringAssert.Contains(libraryCode, "LibraryItems_ItemClick");
        StringAssert.Contains(presentationCode, "ReadMoviesAsync(");
        StringAssert.Contains(presentationCode, "ReadSeriesAsync(");
        StringAssert.Contains(presentationCode, "ReadSeasonsAsync(");
        StringAssert.Contains(presentationCode, "ReadEpisodesAsync(");
        StringAssert.Contains(presentationCode, "PlayMovieAsync(");
        StringAssert.Contains(presentationCode, "PlayEpisodeAsync(");
    }

    [TestMethod]
    public void PostMvpNavigationAndBrowseRequestsRejectStaleUiUpdates()
    {
        string windowsRoot = WindowsSourceRoot();
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        XDocument liveMarkup = LoadMarkup(windowsRoot, "MainPage.xaml");
        string libraryCode = File.ReadAllText(Path.Combine(
            windowsRoot,
            "ContentLibraryPage.xaml.cs"));
        string windowCode = File.ReadAllText(Path.Combine(
            windowsRoot,
            "MainWindow.xaml.cs"));

        StringAssert.Contains(libraryCode, "_loadCancellation");
        StringAssert.Contains(libraryCode, "_searchDebounceCancellation");
        Assert.IsFalse(libraryCode.Contains("_browseCancellation", StringComparison.Ordinal));
        StringAssert.Contains(libraryCode, "internal void Activate()");
        StringAssert.Contains(libraryCode, "internal void Deactivate()");
        StringAssert.Contains(libraryCode, "CancelPendingRequests();");
        StringAssert.Contains(libraryCode, "ShowCachedSeasonsAsync(_selectedSeries)");
        StringAssert.Contains(libraryCode, "LoadSeasonsAsync(series, refreshDetails: false)");
        Assert.IsFalse(
            libraryCode.Contains(
                ".Where(source => source.ItemCount > 0)",
                StringComparison.Ordinal),
            "Ready sources with zero items must remain selectable for a source-scoped empty state.");

        StringAssert.Contains(windowCode, "_suppressNavigationSelectionChanged");
        StringAssert.Contains(windowCode, "section == _activeSection && IsSectionPresented(section)");
        StringAssert.Contains(windowCode, "_visibleContentLibrary?.Deactivate();");
        StringAssert.Contains(windowCode, "library.Activate();");
        StringAssert.Contains(windowCode, "_dashboardCountGeneration");
        StringAssert.Contains(
            windowCode,
            "_catalogServices.SourceManagement.ReadAsync(cancellation.Token)");
        StringAssert.Contains(
            windowCode,
            "generation == Volatile.Read(ref _dashboardCountGeneration)");
        Assert.IsFalse(
            windowCode.Contains("_homePage.SetCounts(0, 0, 0);", StringComparison.Ordinal),
            "A transient or stale dashboard refresh failure must preserve the last authoritative totals.");

        Assert.IsFalse(
            liveMarkup.Descendants().Any(element => string.Equals(
                element.Attribute(x + "Name")?.Value,
                "RemotePlaylistOnboardingPanel",
                StringComparison.Ordinal)),
            "Source Manager must be the only source-onboarding presentation surface.");
        Assert.IsFalse(
            windowCode.Contains("ConfigureSourceOnboarding", StringComparison.Ordinal));
    }

    [TestMethod]
    public void M18OnDemandTimelineIsHiddenForLiveAndSeekIsCapabilityGated()
    {
        string windowsRoot = WindowsSourceRoot();
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        XDocument liveMarkup = LoadMarkup(windowsRoot, "MainPage.xaml");
        string liveCode = File.ReadAllText(Path.Combine(windowsRoot, "MainPage.xaml.cs"));
        string playbackContracts = File.ReadAllText(Path.Combine(
            RepositoryRoot,
            "apps",
            "windows",
            "src",
            "IptvSuite.Application",
            "PlaybackContracts.cs"));
        string coordinator = File.ReadAllText(Path.Combine(
            RepositoryRoot,
            "apps",
            "windows",
            "src",
            "IptvSuite.Application",
            "PlaybackSessionCoordinator.cs"));

        XElement timeline = RequiredNamedElement(liveMarkup, x, "PlaybackTimelinePanel");
        Assert.AreEqual("Collapsed", timeline.Attribute("Visibility")?.Value);
        Assert.AreEqual(
            "PlaybackTimelinePanel",
            timeline.Attribute("AutomationProperties.AutomationId")?.Value);
        XElement slider = RequiredNamedElement(liveMarkup, x, "PlaybackTimelineSlider");
        Assert.AreEqual("False", slider.Attribute("IsEnabled")?.Value);
        Assert.AreEqual(
            "Seek through on-demand content",
            slider.Attribute("AutomationProperties.Name")?.Value);
        _ = RequiredNamedElement(liveMarkup, x, "PlaybackStartTimeText");
        _ = RequiredNamedElement(liveMarkup, x, "PlaybackCurrentTimeText");
        _ = RequiredNamedElement(liveMarkup, x, "PlaybackEndTimeText");

        StringAssert.Contains(playbackContracts, "public bool CanSeek { get; }");
        StringAssert.Contains(playbackContracts, "if (canSeek && duration == TimeSpan.Zero)");
        StringAssert.Contains(coordinator, "PlaybackContentIntent.Live => target.Kind == PlaybackTargetKind.Live");
        StringAssert.Contains(
            coordinator,
            "PlaybackContentIntent.OnDemand => target.Kind is");
        StringAssert.Contains(
            coordinator,
            "_currentContentIntent == PlaybackContentIntent.OnDemand");
        StringAssert.Contains(
            coordinator,
            "_currentContentIntent != PlaybackContentIntent.OnDemand");
        StringAssert.Contains(
            liveCode,
            "_playback?.Current.ContentIntent == PlaybackContentIntent.OnDemand");
        StringAssert.Contains(liveCode, "snapshot.CanSeek &&");
        StringAssert.Contains(
            liveCode,
            "current.State is PlaybackState.Playing or PlaybackState.Paused");
        StringAssert.Contains(liveCode, "Math.Clamp(position.TotalSeconds, 0, duration.TotalSeconds)");
        StringAssert.Contains(liveCode, "await playback.SeekAsync(");
        StringAssert.Contains(liveCode, "timeline.SessionId");
        StringAssert.Contains(liveCode, "private static string FormatPlaybackTime(TimeSpan value)");
        StringAssert.Contains(liveCode, "_playback.TimelineChanged -= Playback_TimelineChanged;");
    }

    private static XDocument LoadMarkup(string windowsRoot, string fileName) =>
        XDocument.Load(Path.Combine(windowsRoot, fileName), LoadOptions.PreserveWhitespace);

    private static XElement RequiredNamedElement(
        XDocument document,
        XNamespace x,
        string name) => document
        .Descendants()
        .Single(element => string.Equals(
            element.Attribute(x + "Name")?.Value,
            name,
            StringComparison.Ordinal));

    private static XElement RequiredAutomationElement(
        XDocument document,
        string automationId) => document
        .Descendants()
        .Single(element => string.Equals(
            element.Attribute("AutomationProperties.AutomationId")?.Value,
            automationId,
            StringComparison.Ordinal));

    private static string WindowsSourceRoot() => Path.Combine(
        RepositoryRoot,
        "apps",
        "windows",
        "src",
        "IptvSuite.Windows");

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "global.json")) &&
                Directory.Exists(Path.Combine(directory.FullName, "apps", "windows")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("Repository root could not be located.");
    }
}
