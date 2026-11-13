using Leviathan.Core.Rename;
using Leviathan.ViewModels.Rename;
using Leviathan.ViewModels.Undo;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Leviathan.App;

/// <summary>
/// The bulk-rename dialog (spec §15.3): an ordered list of steps on top, the live preview table below (old name, new name, conflicts
/// in red, a tick to leave an item out), saved presets, and Rename. The preview is computed on the thread pool a moment after the
/// last change and answers that arrive late are dropped; the rename itself is the worker's journaled restore job, and one undo entry
/// covers the batch. This class only builds controls; the logic is in <see cref="BulkRenameModel"/>.
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001:Types that own disposable fields should be disposable",
    Justification = "The cancellation source is released when the window closes (Closed), which a Window cannot express as IDisposable.")]
internal sealed class BulkRenameWindow : Window
{
    private readonly BulkRenameModel _model;
    private readonly IUndoActions _files;
    private readonly Action<IReadOnlyList<ItemMove>> _record;
    private readonly Action<BulkRenameResult> _done;
    private readonly RenamePresets _presets;
    private readonly IRenameMediaSource? _media;
    private readonly CancellationTokenSource _closing = new();
    private readonly StackPanel _steps = new() { Spacing = 8, Padding = new Thickness(16, 8, 16, 8) };
    private readonly ListView _list = new() { SelectionMode = ListViewSelectionMode.None };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
    private readonly Button _apply = new() { Content = "Rename", IsEnabled = false };
    private readonly ComboBox _presetBox = new() { Header = "Preset", Width = 200 };
    private readonly DispatcherTimerLike _debounce;
    private RenameView? _view;
    private int _version;
    private bool _mediaLoaded;
    private bool _loadingPreset;

    /// <summary>Opens the dialog for the items in <paramref name="model"/>.</summary>
    public BulkRenameWindow(BulkRenameModel model, IUndoActions files, Action<IReadOnlyList<ItemMove>> record, RenamePresets presets, IRenameMediaSource? media, Action<BulkRenameResult> done)
    {
        _model = model;
        _files = files;
        _record = record;
        _presets = presets;
        _media = media;
        _done = done;
        Title = $"Rename {model.Items.Count:N0} item{(model.Items.Count == 1 ? string.Empty : "s")}";
        AppWindow.Resize(new Windows.Graphics.SizeInt32(1000, 780));
        _debounce = new DispatcherTimerLike(DispatcherQueue, TimeSpan.FromMilliseconds(150), () => _ = RecomputeAsync());

        var add = new Button { Content = "Add step", VerticalAlignment = VerticalAlignment.Bottom };
        var menu = new MenuFlyout();
        foreach (var (kind, label) in new (DraftKind, string)[]
        {
            (DraftKind.FindReplace, "Find and replace"), (DraftKind.Insert, "Insert text"), (DraftKind.Remove, "Remove characters"),
            (DraftKind.Case, "Change case"), (DraftKind.Trim, "Trim"), (DraftKind.Number, "Add a number"),
            (DraftKind.Template, "Name from a pattern (tokens)"), (DraftKind.NameList, "Names from a list"),
        })
        {
            var item = new MenuFlyoutItem { Text = label };
            item.Click += (_, _) => AddStep(RenameDraft.Create(kind));
            menu.Items.Add(item);
        }

        add.Flyout = menu;
        var save = new Button { Content = "Save as preset…", VerticalAlignment = VerticalAlignment.Bottom };
        save.Click += async (_, _) => await SavePresetAsync();
        var delete = new Button { Content = "Delete preset", VerticalAlignment = VerticalAlignment.Bottom };
        delete.Click += async (_, _) => await DeletePresetAsync();
        _presetBox.SelectionChanged += (_, _) => LoadPreset();
        FillPresets();

        var bar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Padding = new Thickness(16, 8, 16, 0), Children = { add, _presetBox, save, delete } };
        var hint = new TextBlock
        {
            Text = "Tokens: {name} {ext} {date:yyyy-MM-dd} {taken} {duration} {resolution} {codec} {parent} {counter:000}",
            Opacity = 0.7, FontSize = 12, Margin = new Thickness(16, 0, 16, 0), TextWrapping = TextWrapping.Wrap,
        };

        _list.ContainerContentChanging += OnContainerContentChanging;
        var cancel = new Button { Content = "Cancel" };
        cancel.Click += (_, _) => Close();
        _apply.Click += async (_, _) => await ApplyAsync();
        var footer = new Grid { Padding = new Thickness(16, 8, 16, 12), ColumnSpacing = 8 };
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(_apply, 1);
        Grid.SetColumn(cancel, 2);
        footer.Children.Add(_status);
        footer.Children.Add(_apply);
        footer.Children.Add(cancel);

        var stepsScroll = new ScrollViewer { Content = _steps, MaxHeight = 280, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        var root = new Grid();
        for (var i = 0; i < 5; i++)
        {
            root.RowDefinitions.Add(new RowDefinition { Height = i == 3 ? new GridLength(1, GridUnitType.Star) : GridLength.Auto });
        }

        Place(root, bar, 0);
        Place(root, stepsScroll, 1);
        Place(root, hint, 2);
        Place(root, _list, 3);
        Place(root, footer, 4);
        Content = root;
        Closed += (_, _) =>
        {
            _debounce.Stop();
            _closing.Cancel();
            _closing.Dispose();
        };

        _status.Text = "Add a step to start. The preview shows each new name before anything is renamed.";
        _ = RecomputeAsync();
    }

    private static void Place(Grid grid, FrameworkElement element, int row)
    {
        Grid.SetRow(element, row);
        grid.Children.Add(element);
    }

    private void AddStep(RenameDraft draft)
    {
        _model.Drafts.Add(draft);
        RebuildSteps();
        Changed();
    }

    private void Changed() => _debounce.Restart();

    // ---- the steps ----

    private void RebuildSteps()
    {
        _steps.Children.Clear();
        for (var i = 0; i < _model.Drafts.Count; i++)
        {
            _steps.Children.Add(BuildStep(_model.Drafts[i], i));
        }
    }

    private Border BuildStep(RenameDraft draft, int index)
    {
        var fields = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        switch (draft.Kind)
        {
            case DraftKind.FindReplace:
                fields.Children.Add(Text("Find", draft.Text, v => draft.Text = v, 180));
                fields.Children.Add(Text("Replace with", draft.Replace, v => draft.Replace = v, 180));
                fields.Children.Add(Check("Regex", draft.UseRegex, v => draft.UseRegex = v));
                fields.Children.Add(Check("Match case", draft.MatchCase, v => draft.MatchCase = v));
                fields.Children.Add(TargetBox(draft));
                break;
            case DraftKind.Insert:
                fields.Children.Add(Text("Text", draft.Text, v => draft.Text = v, 220));
                fields.Children.Add(Number("Characters in", draft.Position, v => draft.Position = v));
                fields.Children.Add(Check("From the end", draft.Flag, v => draft.Flag = v));
                break;
            case DraftKind.Remove:
                fields.Children.Add(Number("Start at", draft.Position, v => draft.Position = v));
                fields.Children.Add(Number("How many", draft.Count, v => draft.Count = v));
                fields.Children.Add(Check("Count from the end", draft.Flag, v => draft.Flag = v));
                break;
            case DraftKind.Case:
                fields.Children.Add(Combo("Case", ["lower case", "UPPER CASE", "Title Case", "Sentence case"], (int)draft.Style, i => draft.Style = (CaseStyle)i));
                fields.Children.Add(TargetBox(draft));
                break;
            case DraftKind.Trim:
                fields.Children.Add(Text("Also trim these characters", draft.Text, v => draft.Text = v, 200));
                fields.Children.Add(Check("Collapse repeated spaces", draft.Flag, v => draft.Flag = v));
                fields.Children.Add(TargetBox(draft));
                break;
            case DraftKind.Number:
                fields.Children.Add(Number("Start", draft.Position, v => draft.Position = v));
                fields.Children.Add(Number("Step", draft.Count, v => draft.Count = v));
                fields.Children.Add(Number("Digits", draft.Padding, v => draft.Padding = v));
                fields.Children.Add(Text("Separator", draft.Replace, v => draft.Replace = v, 80));
                fields.Children.Add(Check("At the start", draft.Flag, v => draft.Flag = v));
                break;
            case DraftKind.Template:
                fields.Children.Add(Text("Pattern", draft.Text, v => draft.Text = v, 460));
                break;
            default:
                var box = new TextBox { Header = "One new name per line, in the order of the items", Text = draft.Text, AcceptsReturn = true, Width = 460, Height = 100, TextWrapping = TextWrapping.NoWrap };
                box.TextChanged += (_, _) =>
                {
                    draft.Text = box.Text;
                    Changed();
                };
                fields.Children.Add(box);
                break;
        }

        var up = SmallButton("▲", index > 0, () => Move(index, -1));
        var down = SmallButton("▼", index < _model.Drafts.Count - 1, () => Move(index, 1));
        var remove = SmallButton("✕", true, () =>
        {
            _model.Drafts.RemoveAt(index);
            RebuildSteps();
            Changed();
        });

        var row = new Grid { ColumnSpacing = 8 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var heading = new TextBlock { Text = $"{index + 1}. {StepName(draft.Kind)}", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 4) };
        var left = new StackPanel { Children = { heading, fields } };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, VerticalAlignment = VerticalAlignment.Top, Children = { up, down, remove } };
        Grid.SetColumn(buttons, 1);
        row.Children.Add(left);
        row.Children.Add(buttons);
        return new Border { Child = row, Padding = new Thickness(10, 6, 10, 8), CornerRadius = new CornerRadius(6), BorderThickness = new Thickness(1), BorderBrush = new SolidColorBrush(Colors.Gray) { Opacity = 0.4 } };
    }

    private void Move(int index, int delta)
    {
        var to = index + delta;
        if (to < 0 || to >= _model.Drafts.Count)
        {
            return;
        }

        (_model.Drafts[index], _model.Drafts[to]) = (_model.Drafts[to], _model.Drafts[index]);
        RebuildSteps();
        Changed();
    }

    private static string StepName(DraftKind kind) => kind switch
    {
        DraftKind.FindReplace => "Find and replace",
        DraftKind.Insert => "Insert text",
        DraftKind.Remove => "Remove characters",
        DraftKind.Case => "Change case",
        DraftKind.Trim => "Trim",
        DraftKind.Number => "Add a number",
        DraftKind.Template => "Name from a pattern",
        _ => "Names from a list",
    };

    private TextBox Text(string header, string value, Action<string> set, double width)
    {
        var box = new TextBox { Header = header, Text = value, Width = width };
        box.TextChanged += (_, _) =>
        {
            set(box.Text);
            Changed();
        };
        return box;
    }

    private NumberBox Number(string header, int value, Action<int> set)
    {
        var box = new NumberBox { Header = header, Value = value, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact, Width = 110, Minimum = -1_000_000, Maximum = 1_000_000 };
        box.ValueChanged += (_, e) =>
        {
            if (!double.IsNaN(e.NewValue))
            {
                set((int)e.NewValue);
                Changed();
            }
        };
        return box;
    }

    private CheckBox Check(string label, bool value, Action<bool> set)
    {
        var box = new CheckBox { Content = label, IsChecked = value, VerticalAlignment = VerticalAlignment.Bottom };
        RoutedEventHandler handler = (_, _) =>
        {
            set(box.IsChecked == true);
            Changed();
        };
        box.Checked += handler;
        box.Unchecked += handler;
        return box;
    }

    private ComboBox Combo(string header, string[] items, int selected, Action<int> set)
    {
        var box = new ComboBox { Header = header, Width = 160 };
        foreach (var item in items)
        {
            box.Items.Add(item);
        }

        box.SelectedIndex = selected;
        box.SelectionChanged += (_, _) =>
        {
            if (box.SelectedIndex >= 0)
            {
                set(box.SelectedIndex);
                Changed();
            }
        };
        return box;
    }

    private ComboBox TargetBox(RenameDraft draft) => Combo("Applies to", ["Name without extension", "Extension", "Whole name"], (int)draft.Target, i => draft.Target = (RenameTarget)i);

    private static Button SmallButton(string text, bool enabled, Action click)
    {
        var button = new Button { Content = text, IsEnabled = enabled, Padding = new Thickness(8, 2, 8, 2) };
        button.Click += (_, _) => click();
        return button;
    }

    // ---- presets ----

    private void FillPresets(string? select = null)
    {
        _loadingPreset = true;
        _presetBox.Items.Clear();
        foreach (var preset in _presets.All)
        {
            _presetBox.Items.Add(preset.Name);
        }

        _presetBox.SelectedItem = select;
        _loadingPreset = false;
    }

    private void LoadPreset()
    {
        if (_loadingPreset || _presetBox.SelectedItem is not string name || _presets.Steps(name) is not { } steps)
        {
            return;
        }

        _model.Drafts.Clear();
        _model.Drafts.AddRange(steps);
        RebuildSteps();
        Changed();
    }

    private async Task SavePresetAsync()
    {
        if (_model.Drafts.Count == 0)
        {
            _status.Text = "Add a step first; a preset is a saved list of steps.";
            return;
        }

        var name = new TextBox { PlaceholderText = "Preset name", Text = _presetBox.SelectedItem as string ?? string.Empty };
        var dialog = new ContentDialog { Title = "Save as preset", Content = name, PrimaryButtonText = "Save", CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Primary, XamlRoot = Content.XamlRoot };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary || !_presets.Save(name.Text, _model.Drafts))
        {
            return;
        }

        FillPresets(name.Text.Trim());
        await _presets.SaveAsync();
        _status.Text = $"Saved the preset '{name.Text.Trim()}'.";
    }

    private async Task DeletePresetAsync()
    {
        if (_presetBox.SelectedItem is not string name || !_presets.Delete(name))
        {
            return;
        }

        FillPresets();
        await _presets.SaveAsync();
        _status.Text = $"Deleted the preset '{name}'.";
    }

    // ---- the preview ----

    private async Task RecomputeAsync()
    {
        var version = ++_version;
        if (_model.UsesMediaTokens && !_mediaLoaded && _media is not null)
        {
            _mediaLoaded = true;
            _status.Text = "Reading dates, lengths and sizes from the files…";
            try
            {
                var facts = await _media.GetAsync([.. _model.Items.Select(i => i.Path)], _closing.Token);
                _model.SetMedia(facts);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }

        var input = _model.Snapshot();
        RenameView view;
        try
        {
            view = await Task.Run(() => _model.Compute(input));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _status.Text = $"Could not check the folder: {ex.Message}";
            return;
        }

        if (version != _version || _closing.IsCancellationRequested)
        {
            return;
        }

        _view = view;
        _list.ItemsSource = view.Rows;
        _apply.IsEnabled = view.CanApply;
        _status.Text = !view.Rows.Any(r => r.Included) ? "No item is ticked."
            : view.ProblemCount > 0 ? $"{view.ProblemCount:N0} name{(view.ProblemCount == 1 ? " is" : "s are")} in conflict or not allowed (red). Fix the steps or untick those items."
            : view.ChangeCount == 0 ? "No names would change."
            : $"{view.ChangeCount:N0} item{(view.ChangeCount == 1 ? string.Empty : "s")} will be renamed. You can undo it with Ctrl+Z.";
    }

    private void OnContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (args.InRecycleQueue)
        {
            ((ListViewItem)args.ItemContainer).Content = null;
            return;
        }

        if (args.Item is RenameRow row)
        {
            ((ListViewItem)args.ItemContainer).Content = BuildRow(row);
        }
    }

    private Grid BuildRow(RenameRow row)
    {
        var grid = new Grid { ColumnSpacing = 12 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(40) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var tick = new CheckBox { IsChecked = row.Included, MinWidth = 0 };
        tick.Click += (_, _) =>
        {
            _model.SetIncluded(row.Item.Path, tick.IsChecked == true);
            Changed();
        };

        var problem = row.Status is RenameStatus.Conflict or RenameStatus.Invalid;
        var old = new TextBlock { Text = row.OldName, TextTrimming = TextTrimming.CharacterEllipsis, Opacity = row.Included ? 1 : 0.5 };
        var next = new StackPanel();
        next.Children.Add(new TextBlock
        {
            Text = row.Included ? (row.Status == RenameStatus.Unchanged ? "(unchanged)" : row.NewName) : "(left out)",
            TextTrimming = TextTrimming.CharacterEllipsis,
            Opacity = row.Included && row.Status != RenameStatus.Unchanged ? 1 : 0.5,
            Foreground = problem ? new SolidColorBrush(Colors.IndianRed) : null,
            FontWeight = row.Status == RenameStatus.Ok ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal,
        });

        if (problem && row.Problem is { } text)
        {
            next.Children.Add(new TextBlock { Text = text, FontSize = 12, Foreground = new SolidColorBrush(Colors.IndianRed), TextWrapping = TextWrapping.Wrap });
        }

        Grid.SetColumn(old, 1);
        Grid.SetColumn(next, 2);
        grid.Children.Add(tick);
        grid.Children.Add(old);
        grid.Children.Add(next);
        return grid;
    }

    // ---- applying ----

    private async Task ApplyAsync()
    {
        if (_view is not { CanApply: true } view)
        {
            return;
        }

        _apply.IsEnabled = false;
        _status.Text = "Renaming…";
        BulkRenameResult result;
        try
        {
            result = await Task.Run(() => BulkRenameModel.ApplyAsync(view, _files, _record, CancellationToken.None));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or TimeoutException)
        {
            _status.Text = $"Could not rename: {ex.Message}";
            _apply.IsEnabled = true;
            return;
        }

        _done(result);
        if (result.Problems.Count == 0 || result.Renamed > 0)
        {
            Close();
            return;
        }

        _status.Text = $"Nothing was renamed. {result.Problems[0]}";
        _apply.IsEnabled = true;
    }

    /// <summary>Runs an action once, a moment after the last <see cref="Restart"/>, on the UI thread.</summary>
    private sealed class DispatcherTimerLike
    {
        private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _timer;

        public DispatcherTimerLike(Microsoft.UI.Dispatching.DispatcherQueue queue, TimeSpan delay, Action action)
        {
            _timer = queue.CreateTimer();
            _timer.Interval = delay;
            _timer.IsRepeating = false;
            _timer.Tick += (_, _) => action();
        }

        public void Restart()
        {
            _timer.Stop();
            _timer.Start();
        }

        public void Stop() => _timer.Stop();
    }
}
