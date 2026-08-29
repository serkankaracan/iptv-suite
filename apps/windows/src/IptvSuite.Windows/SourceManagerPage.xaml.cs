using IptvSuite.Application;
using IptvSuite.Domain;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System.Runtime.InteropServices;

namespace IptvSuite.Windows;

public sealed partial class SourceManagerPage : Page, IDisposable
{
    private readonly CancellationTokenSource _lifetime = new();
    private readonly object _operationSync = new();
    private SourceManagerOperations? _operations;
    private CancellationTokenSource? _reloadCancellation;
    private SourceManagementSummary? _replacementSource;
    private ContentDialog? _sourceDeletionDialog;
    private bool _operationInProgress;
    private int _activeOperations;
    private TaskCompletionSource? _operationsDrained;
    private long _reloadGeneration;
    private bool _lifetimeDisposed;
    private bool _disposed;

    public SourceManagerPage()
    {
        InitializeComponent();
    }

    internal event EventHandler? SourcesChanged;

    internal async Task ConfigureAsync(SourceManagerOperations operations)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _operations = operations ?? throw new ArgumentNullException(nameof(operations));
        await ReloadAsync();
    }

    internal Task RefreshAsync() => ReloadAsync();

    private async Task ReloadAsync(SourceId? selectSource = null)
    {
        if (_disposed || _operations is null || _operationInProgress)
        {
            return;
        }

        using OperationLease operation = BeginOperation();
        (long generation, CancellationTokenSource cancellation) = BeginReload();
        SetListBusy(true);
        try
        {
            IReadOnlyList<SourceManagementSummary> sources =
                await _operations.ReadSourcesAsync(cancellation.Token);
            if (!IsCurrentReload(generation, cancellation))
            {
                return;
            }

            SourceList.ItemsSource = sources;
            SourceManagementSummary? selection = selectSource.HasValue
                ? sources.FirstOrDefault(item => item.SourceId.Equals(selectSource.Value))
                : sources.Count == 0 ? null : sources[0];
            selection ??= sources.Count == 0 ? null : sources[0];
            SourceList.SelectedItem = selection;
            SourceStatusText.Text = sources.Count == 0
                ? "No authorized source has been added."
                : $"{sources.Count:N0} source(s).";
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (IsRecoverable(exception))
        {
            if (IsCurrentReload(generation, cancellation))
            {
                SourceStatusText.Text = "Sources could not be loaded safely.";
            }
        }
        finally
        {
            EndReload(generation, cancellation);
        }
    }

    private void SourceList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateSelectionPanel();
    }

    private void UpdateSelectionPanel()
    {
        if (SourceList.SelectedItem is not SourceManagementSummary source)
        {
            EmptySelectionPanel.Visibility = Visibility.Visible;
            SourceDetailsPanel.Visibility = Visibility.Collapsed;
            return;
        }

        RenameSourceTextBox.Text = source.Name;
        SourceKindText.Text = source.Kind == SourceKind.XtreamCompatible
            ? "Xtream-compatible account"
            : "Remote M3U playlist";
        SourceCountsText.Text =
            $"{source.Counts.LiveTvCount:N0} live · {source.Counts.MovieCount:N0} movies · {source.Counts.SeriesCount:N0} series";
        SourceTransportWarningText.Text = source.UsesInsecureHttp
            ? "Warning: this source uses cleartext HTTP. Credentials and media traffic can be observed or modified in transit (MITM)."
            : string.Empty;
        SourceTransportWarningText.Visibility = source.UsesInsecureHttp
            ? Visibility.Visible
            : Visibility.Collapsed;
        EmptySelectionPanel.Visibility = Visibility.Collapsed;
        SourceEditorPanel.Visibility = Visibility.Collapsed;
        SourceDetailsPanel.Visibility = Visibility.Visible;
    }

    private void AddSourceButton_Click(object sender, RoutedEventArgs e)
    {
        _replacementSource = null;
        ShowEditor("Add an authorized source", null);
    }

    private void ReplaceSourceButton_Click(object sender, RoutedEventArgs e)
    {
        if (SourceList.SelectedItem is not SourceManagementSummary source)
        {
            return;
        }

        _replacementSource = source;
        ShowEditor("Replace or convert source", source.Name);
        bool xtream = source.Kind == SourceKind.XtreamCompatible;
        XtreamRadioButton.IsChecked = xtream;
        RemotePlaylistRadioButton.IsChecked = !xtream;
        RemotePlaylistRadioButton.IsEnabled = !xtream;
    }

    private void ShowEditor(string title, string? sourceName)
    {
        ClearSensitiveEditorFields();
        RemotePlaylistRadioButton.IsEnabled = true;
        XtreamRadioButton.IsEnabled = true;
        RemotePlaylistRadioButton.IsChecked = true;
        SourceEditorTitle.Text = title;
        SourceNameTextBox.Text = sourceName ?? string.Empty;
        SourceAuthorizationCheckBox.IsChecked = false;
        RemotePlaylistHttpConsentCheckBox.IsChecked = false;
        XtreamHttpConsentCheckBox.IsChecked = false;
        SourceDetailsPanel.Visibility = Visibility.Collapsed;
        EmptySelectionPanel.Visibility = Visibility.Collapsed;
        SourceEditorPanel.Visibility = Visibility.Visible;
        _ = SourceNameTextBox.Focus(FocusState.Programmatic);
    }

    private void CancelEditorButton_Click(object sender, RoutedEventArgs e)
    {
        _replacementSource = null;
        ClearSensitiveEditorFields();
        SourceEditorPanel.Visibility = Visibility.Collapsed;
        UpdateSelectionPanel();
    }

    private void SourceKind_Changed(object sender, RoutedEventArgs e)
    {
        if (RemotePlaylistFields is null || XtreamFields is null)
        {
            return;
        }

        bool xtream = XtreamRadioButton.IsChecked == true;
        if (xtream)
        {
            RemotePlaylistLocatorTextBox.Text = string.Empty;
        }
        else
        {
            ClearXtreamEditorFields();
        }

        RemotePlaylistFields.Visibility = xtream ? Visibility.Collapsed : Visibility.Visible;
        XtreamFields.Visibility = xtream ? Visibility.Visible : Visibility.Collapsed;
        SourceAuthorizationCheckBox.IsChecked = false;
        RemotePlaylistHttpConsentCheckBox.IsChecked = false;
        XtreamHttpConsentCheckBox.IsChecked = false;
        SourceAuthorizationText.Text = xtream
            ? "I am authorized to access this Xtream-compatible account. If it resolves to a private or local endpoint, I trust only the exact server and port I entered. HTTP requires the separate Xtream MITM consent above."
            : "I am authorized to access this Remote M3U playlist. If it resolves to a private or local endpoint, I trust only the exact server and port I entered. HTTP requires the separate M3U MITM consent above.";
        UpdateHttpConsentVisibility();
    }

    private void XtreamM3uBootstrapCheckBox_Changed(
        object sender,
        RoutedEventArgs e)
    {
        if (XtreamStructuredFields is null || XtreamM3uBootstrapFields is null)
        {
            return;
        }

        bool useBootstrap = XtreamM3uBootstrapCheckBox.IsChecked == true;
        if (useBootstrap)
        {
            XtreamServerTextBox.Text = string.Empty;
            XtreamUsernameTextBox.Text = string.Empty;
            XtreamPasswordBox.Password = string.Empty;
        }
        else
        {
            XtreamM3uBootstrapUrlPasswordBox.Password = string.Empty;
        }

        XtreamStructuredFields.Visibility = useBootstrap
            ? Visibility.Collapsed
            : Visibility.Visible;
        XtreamM3uBootstrapFields.Visibility = useBootstrap
            ? Visibility.Visible
            : Visibility.Collapsed;
        SourceAuthorizationCheckBox.IsChecked = false;
        XtreamHttpConsentCheckBox.IsChecked = false;
        UpdateHttpConsentVisibility();
    }

    private void SourceEndpointTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (SourceAuthorizationCheckBox is not null)
        {
            SourceAuthorizationCheckBox.IsChecked = false;
        }

        if (ReferenceEquals(sender, RemotePlaylistLocatorTextBox) &&
            RemotePlaylistHttpConsentCheckBox is not null)
        {
            RemotePlaylistHttpConsentCheckBox.IsChecked = false;
        }
        else if (ReferenceEquals(sender, XtreamServerTextBox) &&
                 XtreamHttpConsentCheckBox is not null)
        {
            XtreamHttpConsentCheckBox.IsChecked = false;
        }
        UpdateHttpConsentVisibility();
    }

    private void XtreamM3uBootstrapUrl_Changed(object sender, RoutedEventArgs e)
    {
        if (SourceAuthorizationCheckBox is not null)
        {
            SourceAuthorizationCheckBox.IsChecked = false;
        }

        if (XtreamHttpConsentCheckBox is not null)
        {
            XtreamHttpConsentCheckBox.IsChecked = false;
        }

        UpdateHttpConsentVisibility();
    }

    private void XtreamCredentials_Changed(object sender, RoutedEventArgs e)
    {
        if (SourceAuthorizationCheckBox is not null)
        {
            SourceAuthorizationCheckBox.IsChecked = false;
        }

        if (XtreamHttpConsentCheckBox is not null)
        {
            XtreamHttpConsentCheckBox.IsChecked = false;
        }
    }

    private void UpdateHttpConsentVisibility()
    {
        if (RemotePlaylistHttpConsentCheckBox is null || XtreamHttpConsentCheckBox is null)
        {
            return;
        }

        RemotePlaylistHttpConsentCheckBox.Visibility =
            RemotePlaylistRadioButton.IsChecked == true &&
            IsHttp(RemotePlaylistLocatorTextBox.Text)
                ? Visibility.Visible
                : Visibility.Collapsed;
        XtreamHttpConsentCheckBox.Visibility =
            XtreamRadioButton.IsChecked == true && IsHttp(CurrentXtreamLocator())
                ? Visibility.Visible
                : Visibility.Collapsed;
    }

    private async void SaveSourceButton_Click(object sender, RoutedEventArgs e)
    {
        if (_operations is null || _operationInProgress)
        {
            return;
        }

        bool xtreamSelected = XtreamRadioButton.IsChecked == true;
        bool xtreamM3uBootstrap =
            xtreamSelected && XtreamM3uBootstrapCheckBox.IsChecked == true;
        bool usesHttp = IsHttp(
            xtreamSelected
                ? CurrentXtreamLocator()
                : RemotePlaylistLocatorTextBox.Text);
        bool hasKindSpecificHttpConsent = !usesHttp ||
            (xtreamSelected
                ? XtreamHttpConsentCheckBox.IsChecked == true
                : RemotePlaylistHttpConsentCheckBox.IsChecked == true);
        if (SourceAuthorizationCheckBox.IsChecked != true || !hasKindSpecificHttpConsent)
        {
            SourceStatusText.Text = usesHttp
                ? xtreamSelected
                    ? "Confirm authorization and accept the fresh Xtream HTTP MITM risk for this configuration."
                    : "Confirm authorization and accept the fresh Remote M3U HTTP MITM risk for this configuration."
                : "Confirm that you are authorized to access this source.";
            return;
        }

        using OperationLease operation = BeginOperation();
        string displayName = SourceNameTextBox.Text;
        SourceManagerOperationResult result;
        SourceManagementSummary? replacement = _replacementSource;
        SourceStatusText.Text = "Validating and importing the authorized source.";
        SetOperationBusy(true);
        try
        {
            if (xtreamSelected)
            {
                string password = XtreamPasswordBox.Password;
                var input = new XtreamSourceInput(
                    displayName,
                    CurrentXtreamLocator(),
                    xtreamM3uBootstrap ? string.Empty : XtreamUsernameTextBox.Text,
                    xtreamM3uBootstrap ? string.Empty : password,
                    usesHttp && XtreamHttpConsentCheckBox.IsChecked == true,
                    xtreamM3uBootstrap);
                ValueTask<SourceManagerOperationResult> pending = replacement is null
                    ? _operations.AddXtreamAsync(input, _lifetime.Token)
                    : _operations.ReplaceXtreamAsync(replacement.SourceId, input, _lifetime.Token);
                if (xtreamM3uBootstrap)
                {
                    XtreamM3uBootstrapUrlPasswordBox.Password = string.Empty;
                }

                result = await pending;
            }
            else
            {
                var input = new RemotePlaylistSourceInput(
                    displayName,
                    RemotePlaylistLocatorTextBox.Text,
                    usesHttp && RemotePlaylistHttpConsentCheckBox.IsChecked == true);
                result = replacement is null
                    ? await _operations.AddRemotePlaylistAsync(input, _lifetime.Token)
                    : await _operations.ReplaceRemotePlaylistAsync(replacement.SourceId, input, _lifetime.Token);
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            return;
        }
        catch (Exception exception) when (IsRecoverable(exception))
        {
            result = SourceManagerOperationResult.Failure("The source operation could not be completed safely.");
        }
        finally
        {
            if (!_disposed)
            {
                ClearSensitiveEditorFields();
                SetOperationBusy(false);
            }
        }

        if (_disposed)
        {
            return;
        }

        SourceStatusText.Text = result.Message;
        if (!result.IsSuccess)
        {
            return;
        }

        _replacementSource = null;
        SourceEditorPanel.Visibility = Visibility.Collapsed;
        await ReloadAsync();
        if (!_disposed)
        {
            SourceStatusText.Text = result.Message;
            SourcesChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private async void RenameSourceButton_Click(object sender, RoutedEventArgs e)
    {
        if (_operations is null || SourceList.SelectedItem is not SourceManagementSummary source)
        {
            return;
        }

        await RunSelectedOperationAsync(
            source,
            "Renaming the authorized source.",
            token => _operations.RenameAsync(source.SourceId, RenameSourceTextBox.Text, token));
    }

    private async void RefreshSourceButton_Click(object sender, RoutedEventArgs e)
    {
        if (_operations is null || SourceList.SelectedItem is not SourceManagementSummary source)
        {
            return;
        }

        await RunSelectedOperationAsync(
            source,
            "Refreshing and importing the authorized source.",
            token => _operations.RefreshAsync(source.SourceId, token));
    }

    private async void DeleteSourceButton_Click(object sender, RoutedEventArgs e)
    {
        if (_operations is null || SourceList.SelectedItem is not SourceManagementSummary source ||
            _operationInProgress)
        {
            return;
        }

        using OperationLease operation = BeginOperation();
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "Delete source?",
            Content = $"Delete {source.Name} and its imported catalog from this Windows user?",
            PrimaryButtonText = "Delete",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
        };
        _sourceDeletionDialog = dialog;
        ContentDialogResult dialogResult;
        try
        {
            dialogResult = await dialog.ShowAsync();
        }
        catch (Exception exception) when (
            exception is COMException or InvalidOperationException)
        {
            if (!_disposed)
            {
                SourceStatusText.Text = "The delete confirmation could not be shown safely.";
            }

            return;
        }
        finally
        {
            if (ReferenceEquals(_sourceDeletionDialog, dialog))
            {
                _sourceDeletionDialog = null;
            }
        }

        if (_disposed || dialogResult != ContentDialogResult.Primary)
        {
            return;
        }

        await RunSelectedOperationAsync(
            source,
            "Deleting the authorized source.",
            token => _operations.DeleteAsync(source.SourceId, token));
    }

    private async Task RunSelectedOperationAsync(
        SourceManagementSummary source,
        string busyMessage,
        Func<CancellationToken, ValueTask<SourceManagerOperationResult>> operation)
    {
        if (_operationInProgress)
        {
            return;
        }

        using OperationLease lease = BeginOperation();
        SourceStatusText.Text = busyMessage;
        SetOperationBusy(true);
        SetListBusy(true);
        SourceManagerOperationResult result;
        try
        {
            result = await operation(_lifetime.Token);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            return;
        }
        catch (Exception exception) when (IsRecoverable(exception))
        {
            result = SourceManagerOperationResult.Failure("The source operation could not be completed safely.");
        }
        finally
        {
            if (!_disposed)
            {
                SetOperationBusy(false);
                SetListBusy(false);
            }
        }

        if (_disposed)
        {
            return;
        }

        SourceStatusText.Text = result.Message;
        if (result.IsSuccess)
        {
            await ReloadAsync(source.SourceId);
            if (!_disposed)
            {
                SourceStatusText.Text = result.Message;
                SourcesChanged?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    private void SetOperationBusy(bool busy)
    {
        _operationInProgress = busy;
        SourceOperationProgressRing.IsActive = busy;
        SourceOperationProgressRing.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        AddSourceButton.IsEnabled = !busy;
        SourceList.IsEnabled = !busy;
        SaveSourceButton.IsEnabled = !busy;
        CancelEditorButton.IsEnabled = !busy;
        RenameSourceButton.IsEnabled = !busy;
        RefreshSourceButton.IsEnabled = !busy;
        ReplaceSourceButton.IsEnabled = !busy;
        DeleteSourceButton.IsEnabled = !busy;
    }

    private void SetListBusy(bool busy)
    {
        SourceListProgressRing.IsActive = busy;
        SourceListProgressRing.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ClearSensitiveEditorFields()
    {
        RemotePlaylistLocatorTextBox.Text = string.Empty;
        ClearXtreamEditorFields();
        RemotePlaylistHttpConsentCheckBox.IsChecked = false;
        RemotePlaylistHttpConsentCheckBox.Visibility = Visibility.Collapsed;
    }

    private void ClearXtreamEditorFields()
    {
        XtreamServerTextBox.Text = string.Empty;
        XtreamUsernameTextBox.Text = string.Empty;
        XtreamPasswordBox.Password = string.Empty;
        XtreamM3uBootstrapUrlPasswordBox.Password = string.Empty;
        XtreamM3uBootstrapCheckBox.IsChecked = false;
        XtreamHttpConsentCheckBox.IsChecked = false;
        XtreamHttpConsentCheckBox.Visibility = Visibility.Collapsed;
    }

    private static bool IsHttp(string? locator) =>
        Uri.TryCreate(locator, UriKind.Absolute, out Uri? uri) &&
        string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase);

    private string CurrentXtreamLocator() =>
        XtreamM3uBootstrapCheckBox.IsChecked == true
            ? XtreamM3uBootstrapUrlPasswordBox.Password
            : XtreamServerTextBox.Text;

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
        _lifetime.Cancel();
        Volatile.Read(ref _reloadCancellation)?.Cancel();
        try
        {
            _sourceDeletionDialog?.Hide();
        }
        catch (Exception exception) when (
            exception is COMException or InvalidOperationException)
        {
        }

        _sourceDeletionDialog = null;
        ClearSensitiveEditorFields();
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
        lock (_operationSync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _activeOperations = checked(_activeOperations + 1);
        }

        return new OperationLease(this);
    }

    private (long Generation, CancellationTokenSource Cancellation) BeginReload()
    {
        long generation = Interlocked.Increment(ref _reloadGeneration);
        CancellationTokenSource cancellation =
            CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        CancellationTokenSource? previous = Interlocked.Exchange(
            ref _reloadCancellation,
            cancellation);
        previous?.Cancel();
        return (generation, cancellation);
    }

    private bool IsCurrentReload(
        long generation,
        CancellationTokenSource cancellation) =>
        !_disposed &&
        !cancellation.IsCancellationRequested &&
        generation == Volatile.Read(ref _reloadGeneration) &&
        ReferenceEquals(Volatile.Read(ref _reloadCancellation), cancellation);

    private void EndReload(long generation, CancellationTokenSource cancellation)
    {
        if (ReferenceEquals(
            Interlocked.CompareExchange(ref _reloadCancellation, null, cancellation),
            cancellation))
        {
            if (!_disposed && generation == Volatile.Read(ref _reloadGeneration))
            {
                SetListBusy(false);
            }
        }

        cancellation.Dispose();
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
        lock (_operationSync)
        {
            if (!_disposed || _activeOperations != 0 || _lifetimeDisposed)
            {
                return;
            }

            _lifetimeDisposed = true;
        }

        Interlocked.Exchange(ref _reloadCancellation, null)?.Dispose();
        _lifetime.Dispose();
    }

    private sealed class OperationLease(SourceManagerPage owner) : IDisposable
    {
        private SourceManagerPage? _owner = owner;

        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.EndOperation();
    }
}
