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
    private static readonly Windows.UI.Color[] Palette =
    [
        Windows.UI.Color.FromArgb(255, 0x0F, 0x6C, 0xBD),
        Windows.UI.Color.FromArgb(255, 0x10, 0x7C, 0x10),
        Windows.UI.Color.FromArgb(255, 0xC1, 0x9C, 0x00),
        Windows.UI.Color.FromArgb(255, 0xCA, 0x50, 0x10),
        Windows.UI.Color.FromArgb(255, 0x87, 0x64, 0xB8),
        Windows.UI.Color.FromArgb(255, 0x03, 0x83, 0x87),
        Windows.UI.Color.FromArgb(255, 0xD1, 0x34, 0x38),
        Windows.UI.Color.FromArgb(255, 0x5C, 0x6B, 0xC0),
    ];

    public static Brush Color(int index) =>
        new SolidColorBrush(index is >= 0 and < 8 ? Palette[index] : Windows.UI.Color.FromArgb(255, 0x8A, 0x8A, 0x8A));

    public static double Value(double? percent) => percent ?? 0;

    public static bool Indeterminate(double? percent, bool active) => active && percent is null;

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
    private bool _syncing;
    private bool _ready;

    public StatusHubPanel(StatusHubModel model, OperationsModel operations, ActivityLog activity)
    {
        _model = model;
        _operations = operations;
        _activity = activity;
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
        Sync();
        RefreshOperations();
    }

    /// <summary>The user dragged the top edge: the height they ask for.</summary>
    internal event EventHandler<double>? HeightRequested;

    /// <summary>The user dragged the left edge of the right dock: the width they ask for.</summary>
    internal event EventHandler<double>? WidthRequested;

    /// <summary>A row of the Activity log was clicked: go to its place.</summary>
    internal event EventHandler<string>? GoToRequested;

    /// <summary>"Undo last" was clicked.</summary>
    internal event EventHandler? UndoRequested;

    /// <summary>The user chose "Export to a file": the text and the suggested extension (<c>txt</c> or <c>json</c>).</summary>
    internal event EventHandler<(string Text, string Extension)>? ExportRequested;

    /// <summary>Tells whether there is a file action to undo; set by the window.</summary>
    internal Func<bool> CanUndo { get; set; } = () => false;

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

    /// <summary>Lists the operations again (the worker reported a change).</summary>
    internal void RefreshOperations()
    {
        var entries = _operations.Entries;
        OperationList.ItemsSource = entries;
        NothingText.Visibility = entries.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ClearButton.IsEnabled = entries.Any(e => !e.IsActive);
        var summary = _operations.Summary;
        OperationsSummary.Text = summary.Length == 0 ? (entries.Count == 0 ? string.Empty : "Nothing running") : summary;
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
