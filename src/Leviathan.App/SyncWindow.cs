using Leviathan.Core.Compare;
using Leviathan.Core.Paths;
using Leviathan.ViewModels.Folders;
using Leviathan.ViewModels.Sync;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Leviathan.App;

/// <summary>
/// Compare and sync two folders (spec §15.5): pick the folders and how to tell files apart, see every item as left only, right only,
/// newer on one side, different or identical, choose a sync mode and look at exactly what it would do (the dry run is the list itself),
/// override single items, and only then run it. A sync is the worker's ordinary copy and delete jobs, so it shows in the Operations
/// panel, is verified and can be undone; deletions are soft deletes. Changing the mode re-plans at once; changing how files are
/// compared needs a new comparison, and until it is made the Sync button stays off. The comparison runs in the worker; this class builds
/// controls and shows rows.
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001:Types that own disposable fields should be disposable",
    Justification = "The cancellation source is released when the window closes (Closed), which a Window cannot express as IDisposable.")]
internal sealed class SyncWindow : Window
{
    private static readonly (SyncMode Mode, string Title)[] Modes =
    [
        (SyncMode.MirrorLeftToRight, "Mirror left → right (right becomes a copy of left; extras on the right are deleted)"),
        (SyncMode.MirrorRightToLeft, "Mirror right → left (left becomes a copy of right; extras on the left are deleted)"),
        (SyncMode.TwoWay, "Two-way (newer wins; conflicts are flagged; nothing is deleted)"),
        (SyncMode.UpdateLeftToRight, "Update right from left (copy new and newer only; never delete)"),
        (SyncMode.UpdateRightToLeft, "Update left from right (copy new and newer only; never delete)"),
    ];

    private static readonly CompareStatus[] Shown = [CompareStatus.LeftOnly, CompareStatus.RightOnly, CompareStatus.NewerLeft, CompareStatus.NewerRight, CompareStatus.Different, CompareStatus.TypeMismatch, CompareStatus.Identical];

    private readonly ICompareScanner _scanner;
    private readonly SyncRunner _runner;
    private readonly SyncPairStore? _pairs;
    private SyncPair? _pair;
    private readonly CancellationTokenSource _closing = new();
    private readonly TextBox _left = new() { Header = "Left folder", MinWidth = 380, HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly TextBox _right = new() { Header = "Right folder", MinWidth = 380, HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly ComboBox _mode = new() { Header = "Sync mode", HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly ComboBox _by = new() { Header = "Compare by" };
    private readonly NumberBox _tolerance = new() { Header = "Same time within (seconds)", Minimum = 0, Maximum = 3600, Value = 2, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact, Width = 170 };
    private readonly Button _compare = new() { Content = "Compare", Style = (Style)Application.Current.Resources["AccentButtonStyle"], VerticalAlignment = VerticalAlignment.Bottom };
    private readonly Button _stop = new() { Content = "Stop", Visibility = Visibility.Collapsed, VerticalAlignment = VerticalAlignment.Bottom };
    private readonly ProgressRing _busy = new() { Width = 20, Height = 20, IsActive = false, Visibility = Visibility.Collapsed };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
    private readonly Dictionary<CompareStatus, CheckBox> _filters = [];
    private readonly ListView _list = new() { SelectionMode = ListViewSelectionMode.Extended };
    private readonly TextBlock _preview = new() { TextWrapping = TextWrapping.Wrap };
    private readonly Button _save = new() { Content = "Save as sync pair…" };
    private readonly Button _remove = new() { Content = "Remove this pair", Visibility = Visibility.Collapsed };
    private readonly Button _sync = new() { Content = "Sync…", IsEnabled = false, Style = (Style)Application.Current.Resources["AccentButtonStyle"] };
    private readonly Dictionary<string, SyncChoice> _choices = new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource _running = new();
    private CompareResult? _result;
    private SyncPlan? _plan;
    private IReadOnlyList<CompareEntry> _listed = [];
    private string _comparedLeft = string.Empty;
    private string _comparedRight = string.Empty;
    private bool _stale;

    /// <summary>Opens the window; when both folders are given the comparison starts at once.</summary>
    public SyncWindow(LocationPath? left, LocationPath? right, ICompareScanner scanner, SyncRunner runner, SyncPairStore? pairs = null, SyncPair? pair = null)
    {
        _scanner = scanner;
        _runner = runner;
        _pairs = pairs;
        _pair = pair;
        Title = pair is null ? "Compare and sync folders" : $"Sync pair: {pair.Name}";
        AppWindow.Resize(new Windows.Graphics.SizeInt32(1150, 820));
        _left.Text = left?.Value ?? string.Empty;
        _right.Text = right?.Value ?? string.Empty;
        foreach (var (_, title) in Modes)
        {
            _mode.Items.Add(title);
        }

        _mode.SelectedIndex = pair is null ? 2 : Math.Max(0, Array.FindIndex(Modes, m => m.Mode == pair.Mode));
        _by.Items.Add("Name, size and date");
        _by.Items.Add("Contents (checksum; slower)");
        _by.SelectedIndex = pair?.By == CompareBy.Checksum ? 1 : 0;
        _tolerance.Value = pair?.ToleranceSeconds ?? 2;
        _save.Visibility = pairs is null ? Visibility.Collapsed : Visibility.Visible;
        _save.Content = pair is null ? "Save as sync pair…" : "Save changes to this pair…";
        _remove.Visibility = pair is null || pairs is null ? Visibility.Collapsed : Visibility.Visible;

        _compare.Click += (_, _) => _ = CompareAsync();
        _stop.Click += (_, _) => _running.Cancel();
        _mode.SelectionChanged += (_, _) => Replan();
        _by.SelectionChanged += (_, _) => MarkStale();
        _tolerance.ValueChanged += (_, _) => MarkStale();
        _left.TextChanged += (_, _) => MarkStale();
        _right.TextChanged += (_, _) => MarkStale();
        _sync.Click += async (_, _) => await ConfirmAndSyncAsync();
        _save.Click += async (_, _) => await SavePairAsync();
        _remove.Click += (_, _) =>
        {
            if (_pair is not null)
            {
                _pairs?.Remove(_pair.Id);
                Close();
            }
        };
        _list.ItemContainerStyle = new Style(typeof(ListViewItem)) { Setters = { new Setter(Control.MinHeightProperty, 0.0), new Setter(Control.PaddingProperty, new Thickness(8, 3, 8, 3)) } };

        var folders = new Grid { ColumnSpacing = 12 };
        folders.ColumnDefinitions.Add(new ColumnDefinition());
        folders.ColumnDefinitions.Add(new ColumnDefinition());
        folders.Children.Add(Pick(_left, "Browse…"));
        var rightPick = Pick(_right, "Browse…");
        Grid.SetColumn(rightPick, 1);
        folders.Children.Add(rightPick);

        var options = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        _mode.MinWidth = 560;
        options.Children.Add(_mode);
        options.Children.Add(_by);
        options.Children.Add(_tolerance);
        options.Children.Add(_compare);
        options.Children.Add(_stop);

        var status = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        status.Children.Add(_busy);
        status.Children.Add(_status);

        var filters = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        filters.Children.Add(new TextBlock { Text = "Show", VerticalAlignment = VerticalAlignment.Center, Opacity = 0.7 });
        foreach (var kind in Shown)
        {
            var box = new CheckBox { Content = StatusTitle(kind), IsChecked = kind != CompareStatus.Identical, MinWidth = 0 };
            box.Checked += (_, _) => ShowRows();
            box.Unchecked += (_, _) => ShowRows();
            _filters[kind] = box;
            filters.Children.Add(box);
        }

        var overrides = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        overrides.Children.Add(new TextBlock { Text = "For the selected items", VerticalAlignment = VerticalAlignment.Center, Opacity = 0.7 });
        overrides.Children.Add(Choice("Skip", SyncChoice.Skip));
        overrides.Children.Add(Choice("Left → right", SyncChoice.LeftToRight));
        overrides.Children.Add(Choice("Right → left", SyncChoice.RightToLeft));
        overrides.Children.Add(Choice("Use the mode", SyncChoice.Default));

        var top = new StackPanel { Spacing = 10, Margin = new Thickness(16, 14, 16, 8) };
        top.Children.Add(folders);
        top.Children.Add(options);
        top.Children.Add(status);
        top.Children.Add(filters);

        var bottom = new StackPanel { Spacing = 8, Margin = new Thickness(16, 8, 16, 14) };
        bottom.Children.Add(overrides);
        bottom.Children.Add(new TextBlock { Text = "What a sync would do", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Margin = new Thickness(0, 6, 0, 0) });
        bottom.Children.Add(_preview);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        actions.Children.Add(_sync);
        actions.Children.Add(_save);
        actions.Children.Add(_remove);
        bottom.Children.Add(actions);

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.Children.Add(top);
        _list.Margin = new Thickness(16, 0, 16, 0);
        Grid.SetRow(_list, 1);
        root.Children.Add(_list);
        Grid.SetRow(bottom, 2);
        root.Children.Add(bottom);
        Content = root;
        Closed += (_, _) =>
        {
            _closing.Cancel();
            _running.Cancel();
            _closing.Dispose();
        };

        _preview.Text = "Compare two folders to see what a sync would do. Nothing is changed until you press Sync and confirm.";
        if (left is not null && right is not null)
        {
            _ = CompareAsync();
        }
    }

    private static string StatusTitle(CompareStatus kind) => kind switch
    {
        CompareStatus.LeftOnly => "Left only",
        CompareStatus.RightOnly => "Right only",
        CompareStatus.NewerLeft => "Newer left",
        CompareStatus.NewerRight => "Newer right",
        CompareStatus.Different => "Different",
        CompareStatus.TypeMismatch => "File vs folder",
        _ => "Identical",
    };

    private static string Glyph(CompareStatus kind) => kind switch
    {
        CompareStatus.LeftOnly => "◀ L",
        CompareStatus.RightOnly => "R ▶",
        CompareStatus.NewerLeft => "L >",
        CompareStatus.NewerRight => "> R",
        CompareStatus.Different => "≠ ",
        CompareStatus.TypeMismatch => "⇄ ",
        _ => "= ",
    };

    private Grid Pick(TextBox box, string label)
    {
        var button = new Button { Content = label, VerticalAlignment = VerticalAlignment.Bottom };
        button.Click += async (_, _) =>
        {
            try
            {
                var picker = new Windows.Storage.Pickers.FolderPicker();
                picker.FileTypeFilter.Add("*");
                WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
                if (await picker.PickSingleFolderAsync() is { } folder)
                {
                    box.Text = folder.Path;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
            {
                _status.Text = $"Could not open the folder picker: {ex.Message}";
            }
        };
        var row = new Grid { ColumnSpacing = 8 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.Children.Add(box);
        Grid.SetColumn(button, 1);
        row.Children.Add(button);
        return row;
    }

    private Button Choice(string label, SyncChoice choice)
    {
        var button = new Button { Content = label };
        button.Click += (_, _) =>
        {
            foreach (var index in _list.SelectedItems.Select(i => _list.Items.IndexOf(i)).Where(i => i >= 0 && i < _listed.Count).ToList())
            {
                var path = _listed[index].Path;
                if (choice == SyncChoice.Default)
                {
                    _choices.Remove(path);
                }
                else
                {
                    _choices[path] = choice;
                }
            }

            Replan();
        };
        return button;
    }

    private CompareOptions Options() => new()
    {
        By = _by.SelectedIndex == 1 ? CompareBy.Checksum : CompareBy.NameSizeDate,
        TimeTolerance = TimeSpan.FromSeconds(double.IsNaN(_tolerance.Value) ? 2 : _tolerance.Value),
    };

    private static string? ToOsPath(string text) => LocationPath.TryParse(text.Trim(), out var path) && path.Kind is LocationKind.Drive or LocationKind.Unc && !path.IsRoot ? path.ToWin32Path() : LocationPath.TryParse(text.Trim(), out var root) && root.Kind == LocationKind.Drive ? root.ToWin32Path() : null;

    // Anything that changes what a comparison would find makes the one on screen out of date; the Sync button waits for a new one.
    private void MarkStale()
    {
        if (_result is null || _stale)
        {
            return;
        }

        _stale = true;
        _sync.IsEnabled = false;
        _status.Text = "The folders or the way of comparing changed: compare again before syncing.";
    }

    private async Task CompareAsync()
    {
        var left = ToOsPath(_left.Text);
        var right = ToOsPath(_right.Text);
        if (left is null || right is null)
        {
            _status.Text = "Both folders must be folders on this PC or on a Windows share.";
            return;
        }

        _running.Cancel();
        var source = _running = CancellationTokenSource.CreateLinkedTokenSource(_closing.Token);
        _compare.IsEnabled = false;
        _sync.IsEnabled = false;
        _stop.Visibility = Visibility.Visible;
        _busy.Visibility = Visibility.Visible;
        _busy.IsActive = true;
        _status.Text = "Comparing…";
        try
        {
            var result = await _scanner.CompareAsync(left, right, Options(), new Progress<string>(text => _status.Text = text), source.Token);
            _result = result;
            _comparedLeft = left;
            _comparedRight = right;
            _stale = false;
            _choices.Clear();
            var note = result.Problems.Count > 0 ? $"\n{result.Problems.Count:N0} things could not be read: {result.Problems[0]}" : string.Empty;
            _status.Text = $"{result.Entries.Count:N0} items · {result.Count(CompareStatus.Identical):N0} identical{note}";
            Replan();
            if (_pair is not null && _pairs is not null && _plan is not null)
            {
                _pairs.RecordCheck(_pair.Id, DateTimeOffset.UtcNow, SyncSchedule.Summarize(_plan));
                _pair = _pairs.Find(_pair.Id) ?? _pair;
            }
        }
        catch (OperationCanceledException)
        {
            _status.Text = "Stopped.";
        }
        catch (IOException ex)
        {
            _status.Text = ex.Message;
        }
        finally
        {
            _busy.IsActive = false;
            _busy.Visibility = Visibility.Collapsed;
            _stop.Visibility = Visibility.Collapsed;
            _compare.IsEnabled = true;
        }
    }

    // The plan is pure, so a new mode or choice shows its steps at once; this is the dry run the person confirms.
    private void Replan()
    {
        if (_result is null)
        {
            return;
        }

        _plan = SyncPlanner.Plan(_result, Modes[Math.Max(0, _mode.SelectedIndex)].Mode, _choices);
        _preview.Text = Describe(_plan);
        _sync.IsEnabled = !_stale && !_plan.IsEmpty;
        ShowRows();
    }

    private static string Describe(SyncPlan plan)
    {
        if (plan.IsEmpty && plan.Conflicts.Count == 0)
        {
            return "Nothing to do: the folders already match for this mode.";
        }

        var lines = new List<string>();
        var toRight = plan.Of(SyncActionKind.CopyToRight).ToList();
        var toLeft = plan.Of(SyncActionKind.CopyToLeft).ToList();
        if (toRight.Count > 0)
        {
            lines.Add($"Copy {toRight.Count:N0} items ({DisplayFormat.Size(toRight.Sum(a => a.Size))}) to the right folder.");
        }

        if (toLeft.Count > 0)
        {
            lines.Add($"Copy {toLeft.Count:N0} items ({DisplayFormat.Size(toLeft.Sum(a => a.Size))}) to the left folder.");
        }

        var rightDeletes = plan.Of(SyncActionKind.DeleteRight).Count();
        var leftDeletes = plan.Of(SyncActionKind.DeleteLeft).Count();
        if (rightDeletes + leftDeletes > 0)
        {
            lines.Add($"Delete {rightDeletes:N0} items from the right and {leftDeletes:N0} from the left (to the Recycle Bin or the share's recycle folder; undoable).");
        }

        if (plan.NewerOverwritten > 0)
        {
            lines.Add($"{plan.NewerOverwritten:N0} newer files will be overwritten with older ones, because a mirror makes the target match.");
        }

        if (plan.Conflicts.Count > 0)
        {
            lines.Add($"{plan.Conflicts.Count:N0} conflicts are left alone (same time, different contents): choose a side for them above.");
        }

        lines.AddRange(plan.Notes);
        return string.Join("\n", lines);
    }

    private void ShowRows()
    {
        _list.Items.Clear();
        if (_result is null)
        {
            _listed = [];
            return;
        }

        var wanted = _filters.Where(f => f.Value.IsChecked == true).Select(f => f.Key).ToHashSet();
        var steps = _plan?.Actions.ToLookup(a => a.Path, StringComparer.OrdinalIgnoreCase);
        var conflicts = _plan?.Conflicts.Select(c => c.Path).ToHashSet(StringComparer.OrdinalIgnoreCase) ?? [];
        _listed = [.. _result.Entries.Where(e => wanted.Contains(e.Status))];
        foreach (var entry in _listed)
        {
            var what = steps is not null && steps[entry.Path].Any()
                ? string.Join(" and ", steps[entry.Path].Select(a => $"{ActionText(a.Kind)} ({a.Reason})"))
                : conflicts.Contains(entry.Path) ? "conflict: nothing is done unless you choose a side" : "no action";
            if (_choices.TryGetValue(entry.Path, out var choice))
            {
                what += $"  [your choice: {choice}]";
            }

            var text = new TextBlock
            {
                Text = $"{Glyph(entry.Status)}  {entry.Path}{(entry.IsDirectory ? "\\" : string.Empty)}   {Sides(entry)}\n       {what}",
                FontSize = 12,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Opacity = entry.Status == CompareStatus.Identical ? 0.6 : 1,
            };
            if (entry.Status is CompareStatus.Different or CompareStatus.TypeMismatch)
            {
                text.Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 255, 208, 96));
            }

            ToolTipService.SetToolTip(text, entry.Path);
            _list.Items.Add(text);
        }
    }

    private static string ActionText(SyncActionKind kind) => kind switch
    {
        SyncActionKind.CopyToRight => "copy to the right",
        SyncActionKind.CopyToLeft => "copy to the left",
        SyncActionKind.DeleteRight => "delete on the right",
        _ => "delete on the left",
    };

    private static string Sides(CompareEntry entry)
    {
        static string One(CompareSide? side) => side is { } s ? $"{DisplayFormat.Size(s.Size)} · {s.Modified.LocalDateTime:yyyy-MM-dd HH:mm}" : "—";
        return entry.IsDirectory ? string.Empty : $"left {One(entry.Left)}   right {One(entry.Right)}";
    }

    // Saves the folders and the way of comparing as a pair for Favorites; the schedule only checks, except an update pair that may run on its own.
    private async Task SavePairAsync()
    {
        if (_pairs is null)
        {
            return;
        }

        var left = ToOsPath(_left.Text);
        var right = ToOsPath(_right.Text);
        if (left is null || right is null)
        {
            _status.Text = "Both folders must be folders on this PC or on a Windows share before the pair can be saved.";
            return;
        }

        var mode = Modes[Math.Max(0, _mode.SelectedIndex)].Mode;
        var name = new TextBox { Header = "Name", Text = _pair?.Name ?? $"{Path.GetFileName(left.TrimEnd('\\'))} ⇄ {Path.GetFileName(right.TrimEnd('\\'))}", MinWidth = 340 };
        var every = new ComboBox { Header = "Check for changes", MinWidth = 340 };
        foreach (var minutes in SyncSchedule.Intervals)
        {
            every.Items.Add(SyncSchedule.Describe(minutes));
        }

        every.SelectedIndex = Math.Max(0, SyncSchedule.Intervals.ToList().IndexOf(_pair?.CheckEveryMinutes ?? 0));
        var update = mode is SyncMode.UpdateLeftToRight or SyncMode.UpdateRightToLeft;
        var auto = new CheckBox { Content = "Sync on its own after a check", IsChecked = update && _pair?.AutoRun == true, IsEnabled = update };
        var note = new TextBlock
        {
            Text = update
                ? "An update only adds new files and replaces older ones with newer ones, so it can run without you."
                : "A mirror deletes and a two-way sync can meet conflicts, so these are only checked on a schedule: you see the preview and press Sync.",
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.7,
            MaxWidth = 340,
        };
        var panel = new StackPanel { Spacing = 12 };
        panel.Children.Add(name);
        panel.Children.Add(every);
        panel.Children.Add(auto);
        panel.Children.Add(note);
        var dialog = new ContentDialog { Title = "Sync pair", Content = panel, PrimaryButtonText = "Save", CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Primary, XamlRoot = Content.XamlRoot };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        var pair = (_pair ?? new SyncPair()) with
        {
            Name = string.IsNullOrWhiteSpace(name.Text) ? "Sync pair" : name.Text.Trim(),
            Left = left,
            Right = right,
            Mode = mode,
            By = _by.SelectedIndex == 1 ? CompareBy.Checksum : CompareBy.NameSizeDate,
            ToleranceSeconds = double.IsNaN(_tolerance.Value) ? 2 : (int)_tolerance.Value,
            CheckEveryMinutes = SyncSchedule.Intervals[Math.Max(0, every.SelectedIndex)],
            AutoRun = auto.IsChecked == true && update,
        };
        _pairs.Save(pair);
        _pair = pair;
        Title = $"Sync pair: {pair.Name}";
        _save.Content = "Save changes to this pair…";
        _remove.Visibility = Visibility.Visible;
        _status.Text = $"Saved \"{pair.Name}\"; it is in Favorites.";
    }

    private async Task ConfirmAndSyncAsync()
    {
        if (_plan is not { IsEmpty: false } plan || _stale)
        {
            return;
        }

        var dialog = new ContentDialog
        {
            Title = "Run this sync?",
            Content = new ScrollViewer { MaxHeight = 360, Content = new TextBlock { Text = Describe(plan) + "\n\nThe copies and deletions are queued as jobs and shown in the Operations panel.", TextWrapping = TextWrapping.Wrap } },
            PrimaryButtonText = "Sync",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = Content.XamlRoot,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        _sync.IsEnabled = false;
        try
        {
            var outcome = await _runner.RunAsync(plan, _comparedLeft, _comparedRight, _closing.Token);
            _status.Text = outcome.Started
                ? $"Started: {outcome.CopyJobs:N0} copy jobs and {outcome.Deleted:N0} deletions are in the Operations panel. Compare again when they are done."
                : "Nothing was started.";
            if (outcome.Left.Count > 0)
            {
                _status.Text += "\n" + string.Join("\n", outcome.Left.Take(5));
            }

            _stale = true;
        }
        catch (OperationCanceledException)
        {
            _status.Text = "Stopped.";
        }
    }
}
