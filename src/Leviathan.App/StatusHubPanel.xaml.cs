using System.ComponentModel;
using Leviathan.Contracts;
using Leviathan.ViewModels.Activity;
using Leviathan.ViewModels.Hub;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Leviathan.App;

/// <summary>Static helpers the Operations list template binds to.</summary>
internal static class HubFormat
{
    public static Brush Color(int index)
    {
        var argb = JobPalette.Argb(index);
        return new SolidColorBrush(Windows.UI.Color.FromArgb((byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb));
    }

    public static double Value(double? percent) => percent ?? 0;

    // Only a running job with an unknown total sweeps; a queued, paused or waiting job shows an empty bar.
    public static bool Indeterminate(double? percent, JobState state) => state == JobState.Running && percent is null;

    public static Visibility ShowBar(bool active) => active ? Visibility.Visible : Visibility.Collapsed;
    public static string When(DateTimeOffset time)
    {
        var local = time.ToLocalTime();
        return local.Date == DateTime.Today
            ? local.ToString("HH:mm:ss", System.Globalization.CultureInfo.CurrentCulture)
            : local.ToString("MMM d HH:mm", System.Globalization.CultureInfo.CurrentCulture);
    }

    public static string Where(string? target, string? location) => target ?? location ?? string.Empty;

    public static Visibility ShowText(string? target, string? location) => string.IsNullOrEmpty(target ?? location) ? Visibility.Collapsed : Visibility.Visible;

    public static Brush ActivityBrush(bool error) =>
        (Brush)Application.Current.Resources[error ? "SystemFillColorCriticalBrush" : "TextFillColorPrimaryBrush"];

    public static string ProcessLine(string state, int? pid, int restarts)
    {
        var line = state;
        if (pid is { } id)
        {
            line += $" · process {id}";
        }

        return restarts == 0 ? line : $"{line} · restarted {restarts} time{(restarts == 1 ? string.Empty : "s")} after crashes";
    }

    public static int PriorityIndex(JobPriority priority) => priority switch
    {
        JobPriority.Low => 0,
        JobPriority.High => 2,
        _ => 1,
    };

    public static string PauseText(bool canResume) => canResume ? "Resume" : "Pause";

    public static string StateText(JobState state) => state switch
    {
        JobState.Queued => "Queued",
        JobState.Running => "Running",
        JobState.Paused => "Paused",
        JobState.Waiting => "Waiting",
        JobState.Completed => "Done",
        JobState.Failed => "Failed",
        JobState.Cancelled => "Cancelled",
        _ => string.Empty,
    };
}

/// <summary>Picks the template of a line of the Operations list: a group heading or a job.</summary>
internal sealed partial class OperationTemplateSelector : DataTemplateSelector
{
    public DataTemplate? Row { get; set; }

    public DataTemplate? Header { get; set; }

    protected override DataTemplate? SelectTemplateCore(object item) => item is OperationHeader ? Header : Row;

    protected override DataTemplate? SelectTemplateCore(object item, DependencyObject container) => SelectTemplateCore(item);
}

/// <summary>
/// The status hub (spec §16.1, D34): the tabs, the pin, where it is shown, and the Operations list. It draws what
/// <see cref="StatusHubModel"/> says and reports what the user did (a tab, the pin, a new place, a drag of the edge); the window
/// decides where the panel sits and what it measures. The other four tabs say which slice brings them.
/// </summary>
internal sealed partial class StatusHubPanel : UserControl
{
    private readonly StatusHubModel _model;
    private readonly OperationsModel _operations;
    private readonly ActivityLog _activity;
    private readonly BackgroundModel _background;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _backgroundTimer;
    private BackgroundView? _shownBackground;
    private bool _syncing;
    private bool _ready;

    public StatusHubPanel(StatusHubModel model, OperationsModel operations, ActivityLog activity, BackgroundModel background)
    {
        _model = model;
        _operations = operations;
        _activity = activity;
        _background = background;
        InitializeComponent();
        _ready = true;
        Icons.Bind(PinButton, static (b, i) => b.Content = i, "pin");
        Icons.Bind(PlaceButton, static (b, i) => b.Content = i, "split");
        Icons.Bind(CloseButton, static (b, i) => b.Content = i, "close");
        TopGrip.CurrentSize = () => ActualHeight;
        TopGrip.SizeRequested += (_, size) => HeightRequested?.Invoke(this, size);
        LeftGrip.CurrentSize = () => ActualWidth;
        LeftGrip.SizeRequested += (_, size) => WidthRequested?.Invoke(this, size);
        _model.PropertyChanged += OnModelChanged;
        App.Host.Settings.Changed += OnSettingsChanged;
        Sync();
        LoadQuick();
        RefreshOperations();
    }

    /// <summary>The user dragged the top edge: the height they ask for.</summary>
    internal event EventHandler<double>? HeightRequested;

    /// <summary>The user dragged the left edge of the right dock: the width they ask for.</summary>
    internal event EventHandler<double>? WidthRequested;

    /// <summary>A row of the Activity log was clicked: go to its place.</summary>
    internal event EventHandler<string>? GoToRequested;

    /// <summary>The cancel button of a job's row was clicked.</summary>
    internal event EventHandler<Guid>? CancelJobRequested;

    private void OnCancelJobClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: Guid id })
        {
            CancelJobRequested?.Invoke(this, id);
        }
    }

    /// <summary>"Undo last" was clicked.</summary>
    internal event EventHandler? UndoRequested;

    /// <summary>The user chose "Export to a file": the text and the suggested extension (<c>txt</c> or <c>json</c>).</summary>
    internal event EventHandler<(string Text, string Extension)>? ExportRequested;

    /// <summary>Tells whether there is a file action to undo; set by the window.</summary>
    internal Func<bool> CanUndo { get; set; } = () => false;

    /// <summary>"All settings" was clicked.</summary>
    internal event EventHandler? AllSettingsRequested;

    /// <summary>The user chose where the hub is shown.</summary>
    internal event EventHandler<HubPlacement>? PlaceRequested;

    /// <summary>Which edge carries the grip: the left one for the right dock, else the top one.</summary>
    internal void ShowGripFor(HubPlacement placement)
    {
        var right = placement == HubPlacement.DockRight;
        LeftGrip.Visibility = right ? Visibility.Visible : Visibility.Collapsed;
        TopGrip.Visibility = right || placement == HubPlacement.PoppedOut ? Visibility.Collapsed : Visibility.Visible;
        Frame.BorderThickness = placement == HubPlacement.PoppedOut ? new Thickness(0) : right ? new Thickness(1, 0, 0, 0) : new Thickness(0, 1, 0, 0);
        Frame.CornerRadius = placement == HubPlacement.Flyout ? new CornerRadius(8, 8, 0, 0) : new CornerRadius(0);
    }

    /// <summary>Called when the window closes, so the panel stops listening to the shared settings.</summary>
    internal void Detach() => App.Host.Settings.Changed -= OnSettingsChanged;

    private void OnSettingsChanged(object? sender, Leviathan.ViewModels.Settings.AppSettings settings) => DispatcherQueue.TryEnqueue(LoadQuick);

    // The controls take their values from the settings; their change events are ignored while they are being filled.
    private bool _loadingQuick;

    private void LoadQuick()
    {
        if (!_ready)
        {
            return;
        }

        var performance = App.Host.Settings.Current.Performance;
        _loadingQuick = true;
        try
        {
            RunningBox.Value = performance.MaxRunningJobs;
            PerDeviceBox.Value = performance.MaxPerDevice;
            BackgroundBox.Value = performance.MaxBackgroundJobs;
            NetworkSizesSwitch.IsOn = performance.FolderSizesOnNetwork;
            NetworkThumbsSwitch.IsOn = performance.ThumbnailsOnNetwork;
        }
        finally
        {
            _loadingQuick = false;
        }
    }

    private async void OnQuickNumberChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        // Clearing the box gives NaN; the old number is put back on the next load.
        if (!_ready || _loadingQuick || double.IsNaN(args.NewValue))
        {
            return;
        }

        var running = (int)RunningBox.Value;
        var perDevice = (int)PerDeviceBox.Value;
        var background = (int)BackgroundBox.Value;
        await SaveQuickAsync(p => p with { MaxRunningJobs = running, MaxPerDevice = perDevice, MaxBackgroundJobs = background });
    }

    private async void OnQuickToggled(object sender, RoutedEventArgs e)
    {
        if (!_ready || _loadingQuick)
        {
            return;
        }

        var sizes = NetworkSizesSwitch.IsOn;
        var thumbnails = NetworkThumbsSwitch.IsOn;
        await SaveQuickAsync(p => p with { FolderSizesOnNetwork = sizes, ThumbnailsOnNetwork = thumbnails });
    }

    private static async Task SaveQuickAsync(Func<Leviathan.ViewModels.Settings.PerformanceSettings, Leviathan.ViewModels.Settings.PerformanceSettings> change)
    {
        try
        {
            await App.Host.Settings.UpdateAsync(s => s with { Performance = change(s.Performance) }, CancellationToken.None);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The change still applies for this run; the file could not be written.
        }
    }

    private void OnAllSettingsClick(object sender, RoutedEventArgs e) => AllSettingsRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>Shows the Background tab again: jobs, thumbnails, processes. Lists are only replaced when something changed, so an open menu is not closed under the user.</summary>
    internal void RefreshBackground()
    {
        if (!_ready || _model.Tab != HubTab.Background || !_model.IsOpen)
        {
            return;
        }

        var view = _background.View();
        FolderSizeText.Text = view.FolderSizes;
        ThumbText.Text = view.Thumbnails;
        PauseAllButton.IsEnabled = view.AnyActive && !view.AllPaused;
        ResumeAllButton.IsEnabled = view.AllPaused || _background.PausedIds().Count > 0;
        TasksEmpty.Visibility = view.Tasks.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (_shownBackground is null || !_shownBackground.Tasks.SequenceEqual(view.Tasks))
        {
            TaskList.ItemsSource = view.Tasks;
        }

        if (_shownBackground is null || !_shownBackground.Processes.SequenceEqual(view.Processes))
        {
            ProcessList.ItemsSource = view.Processes;
        }

        _shownBackground = view;
    }

    private void OnPauseAllClick(object sender, RoutedEventArgs e) => ForEachJob(_background.RunningOrQueuedIds(), App.Host.PauseJobAsync);

    private void OnResumeAllClick(object sender, RoutedEventArgs e) => ForEachJob(_background.PausedIds(), App.Host.ResumeJobAsync);

    private void OnTaskPauseResumeClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: Guid id } && _shownBackground?.Tasks.FirstOrDefault(t => t.Id == id) is { } task)
        {
            ForEachJob([id], task.CanResume ? App.Host.ResumeJobAsync : App.Host.PauseJobAsync);
        }
    }

    private async void OnTaskPriorityChanged(object sender, SelectionChangedEventArgs e)
    {
        // Setting the box up raises this too; only a change the user made (an item was replaced) counts.
        if (e.RemovedItems.Count == 0 || sender is not ComboBox { Tag: Guid id } box)
        {
            return;
        }

        var priority = box.SelectedIndex switch { 0 => JobPriority.Low, 2 => JobPriority.High, _ => JobPriority.Normal };
        try
        {
            await App.Host.SetJobPriorityAsync(id, priority);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or StreamJsonRpc.RemoteInvocationException or StreamJsonRpc.ConnectionLostException)
        {
            // The worker went away; the next refresh shows what it reports.
        }
    }

    private void OnReenableClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string name })
        {
            App.Host.ReenableProcess(name);
            RefreshBackground();
        }
    }

    private static async void ForEachJob(IEnumerable<Guid> ids, Func<Guid, Task> action)
    {
        try
        {
            foreach (var id in ids.ToList())
            {
                await action(id);
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or StreamJsonRpc.RemoteInvocationException or StreamJsonRpc.ConnectionLostException)
        {
            // The worker went away; the next refresh shows what it reports.
        }
    }

    // The numbers of the Background tab (processes, thumbnails) change without an event: look again every two seconds while it is shown.
    private void SyncBackgroundTimer()
    {
        var wanted = _model.IsOpen && _model.Tab == HubTab.Background;
        if (wanted && _backgroundTimer is null)
        {
            _backgroundTimer = DispatcherQueue.CreateTimer();
            _backgroundTimer.Interval = TimeSpan.FromSeconds(2);
            _backgroundTimer.Tick += (_, _) => RefreshBackground();
            _backgroundTimer.Start();
            RefreshBackground();
        }
        else if (!wanted && _backgroundTimer is not null)
        {
            _backgroundTimer.Stop();
            _backgroundTimer = null;
            _shownBackground = null;
        }
    }

    /// <summary>Lists the activity again (something was logged, or a filter changed).</summary>
    internal void RefreshActivity()
    {
        // Controls raise their change events while the XAML is still being built; nothing is ready to list into yet.
        if (!_ready || _model.Tab != HubTab.Activity)
        {
            return;
        }

        var entries = _activity.Find(CurrentQuery());
        ActivityList.ItemsSource = entries;
        ActivityEmpty.Visibility = entries.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        UndoActivityButton.IsEnabled = CanUndo();
    }

    private ActivityQuery CurrentQuery() => new(
        (KindBox.SelectedItem as ComboBoxItem)?.Tag is string tag && Enum.TryParse<ActivityKind>(tag, out var kind) ? kind : null,
        ActivitySearch.Text,
        EarlierBox.IsChecked == true);

    private void OnActivityFilterChanged(object sender, SelectionChangedEventArgs e) => RefreshActivity();

    private void OnActivitySearchChanged(object sender, TextChangedEventArgs e) => RefreshActivity();

    private async void OnEarlierClick(object sender, RoutedEventArgs e)
    {
        if (EarlierBox.IsChecked == true)
        {
            await _activity.LoadEarlierAsync();
        }

        RefreshActivity();
    }

    private void OnActivityItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is ActivityEntry { Location: { Length: > 0 } where })
        {
            GoToRequested?.Invoke(this, where);
        }
    }

    private void OnUndoLastClick(object sender, RoutedEventArgs e) => UndoRequested?.Invoke(this, EventArgs.Empty);

    private void OnCopyTextClick(object sender, RoutedEventArgs e) => CopyToClipboard(ActivityLog.ExportText(_activity.Find(CurrentQuery())));

    private void OnCopyJsonClick(object sender, RoutedEventArgs e) => CopyToClipboard(ActivityLog.ExportJson(_activity.Find(CurrentQuery())));

    private void OnExportClick(object sender, RoutedEventArgs e) =>
        ExportRequested?.Invoke(this, (ActivityLog.ExportText(_activity.Find(CurrentQuery())), "txt"));

    private async void OnClearActivityClick(object sender, RoutedEventArgs e)
    {
        await _activity.ClearAsync();
        RefreshActivity();
    }

    private static void CopyToClipboard(string text)
    {
        var package = new Windows.ApplicationModel.DataTransfer.DataPackage();
        package.SetText(text);
        Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(package);
    }

    private readonly System.Collections.ObjectModel.ObservableCollection<OperationListItem> _lines = [];
    private bool _linesBound;

    /// <summary>Lists the operations again (the worker reported a change). The lines are updated in place, so the scroll position and the selection stay.</summary>
    internal void RefreshOperations()
    {
        if (!_linesBound)
        {
            OperationList.ItemsSource = _lines;
            _linesBound = true;
        }

        OperationLines.Sync(_lines, _operations.Groups);
        var any = _lines.Count > 0;
        NothingText.Visibility = any ? Visibility.Collapsed : Visibility.Visible;
        var summary = _operations.Summary;
        OperationsSummary.Text = summary.Length == 0 ? (any ? "Nothing running" : string.Empty) : summary;
        var eta = _operations.QueueEta;
        QueueEtaText.Text = eta ?? string.Empty;
        QueueEtaText.Visibility = eta is null ? Visibility.Collapsed : Visibility.Visible;
        UpdateOperationButtons();
        SyncCountdownTimer();
    }

    private IReadOnlyList<OperationEntry> SelectedOperations() =>
        [.. OperationList.SelectedItems.OfType<OperationRow>().Select(r => r.Entry)];

    private OperationEntry? EntryOf(Guid id) => _lines.OfType<OperationRow>().Select(r => r.Entry).FirstOrDefault(e => e.Id == id);

    // Each button is on when its action fits at least one of the selected jobs; with nothing selected the toolbar is off.
    private void UpdateOperationButtons()
    {
        var selected = SelectedOperations();
        foreach (var (button, action) in new[]
        {
            (OpPauseButton, OperationAction.Pause), (OpResumeButton, OperationAction.Resume), (OpRunNowButton, OperationAction.RunNow),
            (OpUpButton, OperationAction.MoveUp), (OpDownButton, OperationAction.MoveDown), (OpCancelButton, OperationAction.Cancel),
            (OpRemoveButton, OperationAction.Remove),
        })
        {
            button.IsEnabled = OperationActions.CanDo(action, selected);
        }

        var all = _lines.OfType<OperationRow>().Select(r => r.Entry).ToList();
        PauseAllOpsButton.IsEnabled = all.Any(e => e.CanPause);
        ResumeAllOpsButton.IsEnabled = all.Any(e => e.CanResume);
        ClearButton.IsEnabled = all.Any(e => !e.IsActive);
    }

    private void OnOperationSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // The group headings are lines of the list but not jobs: they cannot be selected.
        foreach (var header in e.AddedItems.OfType<OperationHeader>())
        {
            OperationList.SelectedItems.Remove(header);
        }

        UpdateOperationButtons();
    }

    private async void OnSelectionActionClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string name } && Enum.TryParse<OperationAction>(name, out var action))
        {
            await App.Host.JobActions.RunAsync(action, SelectedOperations());
        }
    }

    private async Task RunOnAsync(OperationAction action, Guid id)
    {
        if (EntryOf(id) is { } entry)
        {
            await App.Host.JobActions.RunAsync(action, [entry]);
        }
    }

    private async void OnPauseJobClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: Guid id })
        {
            await RunOnAsync(OperationAction.Pause, id);
        }
    }

    private async void OnResumeJobClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: Guid id })
        {
            await RunOnAsync(OperationAction.Resume, id);
        }
    }

    private async void OnRemoveJobClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: Guid id })
        {
            await RunOnAsync(OperationAction.Remove, id);
        }
    }

    private async Task AnswerAsync(object sender, ErrorDecision decision)
    {
        if (sender is FrameworkElement { Tag: Guid id } && EntryOf(id) is { } entry)
        {
            await App.Host.JobActions.AnswerAsync(entry, decision);
        }
    }

    private async void OnPromptSkipClick(object sender, RoutedEventArgs e) => await AnswerAsync(sender, ErrorDecision.Skip);

    private async void OnPromptRetryClick(object sender, RoutedEventArgs e) => await AnswerAsync(sender, ErrorDecision.Retry);

    private async void OnPromptSkipAllClick(object sender, RoutedEventArgs e) => await AnswerAsync(sender, ErrorDecision.SkipAllLikeThis);

    private async void OnRetryFailedClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: Guid id } && await App.Host.JobActions.LoadReportAsync(id) is { } report)
        {
            await App.Host.Transfers.RetryFailedAsync(report, CancellationToken.None);
        }
    }

    private async void OnReportClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: Guid id })
        {
            await ShowReportAsync(id);
        }
    }

    // The report of a finished job (spec §12.3): counts, then every item with its reason, failures first, with the ways on from there.
    private async Task ShowReportAsync(Guid id)
    {
        var report = await App.Host.JobActions.LoadReportAsync(id);
        if (report is null)
        {
            var gone = new ContentDialog { XamlRoot = XamlRoot, Title = "No report", Content = "The worker no longer has a report for this job. Reports are kept for seven days.", CloseButtonText = "Close" };
            await gone.ShowAsync();
            return;
        }

        var list = new ListView { SelectionMode = ListViewSelectionMode.None, MaxHeight = 360, MinWidth = 520 };
        list.ItemsSource = report.Lines.Select(l => $"{l.Glyph}  {l.Name}{(l.Detail.Length > 0 ? "   —   " + l.Detail : string.Empty)}").ToList();
        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(new TextBlock { Text = report.Summary, Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"] });
        if (report.Error is { Length: > 0 } error)
        {
            panel.Children.Add(new TextBlock { Text = error, TextWrapping = TextWrapping.Wrap });
        }

        foreach (var note in report.Notes)
        {
            panel.Children.Add(new TextBlock { Text = note, TextWrapping = TextWrapping.Wrap, Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"] });
        }

        panel.Children.Add(list);
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = report.Title,
            Content = panel,
            CloseButtonText = "Close",
            PrimaryButtonText = report.CanRetryFailed ? "Retry failed" : string.Empty,
            SecondaryButtonText = "Copy",
        };
        var links = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        string? go = null;
        if (report.Destination is { } destination)
        {
            var link = new HyperlinkButton { Content = "Show destination", Padding = new Thickness(0) };
            link.Click += (_, _) => { go = destination; dialog.Hide(); };
            links.Children.Add(link);
        }

        if (report.SourceFolder is { } source)
        {
            var link = new HyperlinkButton { Content = "Show source folder", Padding = new Thickness(0) };
            link.Click += (_, _) => { go = source; dialog.Hide(); };
            links.Children.Add(link);
        }

        if (links.Children.Count > 0)
        {
            panel.Children.Add(links);
        }

        var result = await dialog.ShowAsync();
        if (result == ContentDialogResult.Primary)
        {
            await App.Host.Transfers.RetryFailedAsync(report, CancellationToken.None);
        }
        else if (result == ContentDialogResult.Secondary)
        {
            CopyToClipboard(report.ToText());
        }
        else if (go is not null)
        {
            GoToRequested?.Invoke(this, go);
        }
    }

    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _countdownTimer;

    // A job that waits for a share counts down to its next try: refresh once a second while one does.
    private void SyncCountdownTimer()
    {
        var counting = _lines.OfType<OperationRow>().Any(r => r.Entry.RetryAt is not null);
        if (counting && _countdownTimer is null)
        {
            _countdownTimer = DispatcherQueue.CreateTimer();
            _countdownTimer.Interval = TimeSpan.FromSeconds(1);
            _countdownTimer.Tick += (_, _) => RefreshOperations();
            _countdownTimer.Start();
        }
        else if (!counting && _countdownTimer is not null)
        {
            _countdownTimer.Stop();
            _countdownTimer = null;
        }
    }

    private async void OnPauseAllJobsClick(object sender, RoutedEventArgs e) => await App.Host.JobActions.PauseAllAsync();

    private async void OnResumeAllJobsClick(object sender, RoutedEventArgs e) => await App.Host.JobActions.ResumeAllAsync();

    // Right-click: the menu acts on the selection; a job that is not selected becomes the selection first, as in a file list.
    private void OnOperationRightTapped(object sender, Microsoft.UI.Xaml.Input.RightTappedRoutedEventArgs e)
    {
        if ((e.OriginalSource as FrameworkElement)?.DataContext is not OperationRow row)
        {
            return;
        }

        if (!OperationList.SelectedItems.Contains(row))
        {
            OperationList.SelectedItems.Clear();
            OperationList.SelectedItems.Add(row);
        }

        var selected = SelectedOperations();
        var menu = new MenuFlyout();
        foreach (var (text, action) in new[]
        {
            ("Pause", OperationAction.Pause), ("Resume", OperationAction.Resume), ("Run now", OperationAction.RunNow),
            ("Move up", OperationAction.MoveUp), ("Move down", OperationAction.MoveDown), ("Cancel", OperationAction.Cancel),
            ("Remove from the list", OperationAction.Remove),
        })
        {
            var what = action;
            var item = new MenuFlyoutItem { Text = text, IsEnabled = OperationActions.CanDo(what, selected) };
            item.Click += async (_, _) => await App.Host.JobActions.RunAsync(what, SelectedOperations());
            menu.Items.Add(item);
        }

        menu.Items.Add(new MenuFlyoutSeparator());
        var priority = new MenuFlyoutSubItem { Text = "Priority", IsEnabled = selected.Any(s => s.IsActive) };
        foreach (var (text, value) in new[] { ("Low", JobPriority.Low), ("Normal", JobPriority.Normal), ("High", JobPriority.High) })
        {
            var chosen = value;
            var item = new MenuFlyoutItem { Text = text };
            item.Click += async (_, _) => await App.Host.JobActions.SetPriorityAsync(chosen, SelectedOperations());
            priority.Items.Add(item);
        }

        menu.Items.Add(priority);
        menu.ShowAt(OperationList, e.GetPosition(OperationList));
        e.Handled = true;
    }

    private void OnModelChanged(object? sender, PropertyChangedEventArgs e) => Sync();

    private void Sync()
    {
        _syncing = true;
        try
        {
            var wanted = _model.Tab.ToString();
            foreach (var item in Tabs.Items.OfType<SelectorBarItem>().Where(i => (string?)i.Tag == wanted))
            {
                Tabs.SelectedItem = item;
            }

            OperationsPage.Visibility = Show(HubTab.Operations);
            ActivityPage.Visibility = Show(HubTab.Activity);
            SyncBackgroundTimer();
            if (_model.Tab == HubTab.Activity)
            {
                DispatcherQueue.TryEnqueue(RefreshActivity);
            }

            BackgroundPage.Visibility = Show(HubTab.Background);
            QuickSettingsPage.Visibility = Show(HubTab.QuickSettings);
            SpacePage.Visibility = Show(HubTab.Space);
            PinButton.IsChecked = _model.IsPinned;
            PinButton.IsEnabled = _model.Placement == HubPlacement.Flyout;
            ShowGripFor(_model.Placement);
        }
        finally
        {
            _syncing = false;
        }

        Visibility Show(HubTab tab) => _model.Tab == tab ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnTabSelected(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        if (!_syncing && sender.SelectedItem?.Tag is string name && Enum.TryParse<HubTab>(name, out var tab))
        {
            _model.Tab = tab;
        }
    }

    private void OnPinClick(object sender, RoutedEventArgs e) => _model.IsPinned = PinButton.IsChecked == true;

    private void OnCloseClick(object sender, RoutedEventArgs e) => _model.Close();

    private void OnClearClick(object sender, RoutedEventArgs e)
    {
        _operations.ClearFinished();
        RefreshOperations();
    }

    private void OnPlaceMenuOpening(object? sender, object e)
    {
        PlaceMenu.Items.Clear();
        foreach (var (placement, title) in new[]
        {
            (HubPlacement.Flyout, "Fly up from the status bar"),
            (HubPlacement.DockBottom, "Dock at the bottom"),
            (HubPlacement.DockRight, "Dock on the right"),
            (HubPlacement.PoppedOut, "Open in its own window"),
        })
        {
            var target = placement;
            var item = new RadioMenuFlyoutItem { Text = title, GroupName = "hubplace", IsChecked = _model.Placement == placement };
            item.Click += (_, _) => PlaceRequested?.Invoke(this, target);
            PlaceMenu.Items.Add(item);
        }
    }
}
