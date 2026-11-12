using Leviathan.Core.Duplicates;
using Leviathan.ViewModels.Duplicates;
using Leviathan.ViewModels.Folders;
using Leviathan.ViewModels.Tags;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;

namespace Leviathan.App;

/// <summary>
/// The results of a duplicate search (spec §15.2): every group with its copies side by side (a thumbnail for pictures, size, date,
/// folder), a rule that picks the copy to keep, a radio button to override it, and one action for all the extra copies: soft delete,
/// move to a folder, replace with a hard link, or tag. Nothing is changed until the action button is pressed, and the last action
/// can be undone from the window; deletes can also be undone with Ctrl+Z in the main window. The search and every file change run in
/// the worker or on the thread pool; this class only builds controls.
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001:Types that own disposable fields should be disposable",
    Justification = "The cancellation source is released when the window closes (Closed), which a Window cannot express as IDisposable.")]
internal sealed class DuplicateReviewWindow : Window
{
    private static readonly HashSet<string> Pictures = new(StringComparer.OrdinalIgnoreCase) { ".jpg", ".jpeg", ".png", ".gif", ".bmp", ".webp", ".tif", ".tiff" };

    private readonly IReadOnlyList<string> _roots;
    private readonly IDuplicateScanner _scanner;
    private readonly DuplicateCleanup _cleanup;
    private readonly IReadOnlyList<ColorTag> _tags;
    private readonly CancellationTokenSource _closing = new();
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap };
    private readonly ProgressRing _busy = new() { IsActive = true, Width = 20, Height = 20 };
    private readonly ListView _list = new() { SelectionMode = ListViewSelectionMode.None };
    private readonly ComboBox _rule = new() { Header = "Keep" };
    private readonly TextBox _preferred = new() { Header = "Preferred folder", PlaceholderText = @"D:\Library", Width = 220, Visibility = Visibility.Collapsed };
    private readonly ComboBox _action = new() { Header = "Extra copies" };
    private readonly TextBox _target = new() { Header = "Move to", PlaceholderText = @"D:\Duplicates", Width = 220, Visibility = Visibility.Collapsed };
    private readonly ComboBox _tag = new() { Header = "Tag", Visibility = Visibility.Collapsed };
    private readonly Button _go = new() { Content = "Apply", IsEnabled = false };
    private readonly Button _undo = new() { Content = "Undo last", IsEnabled = false };
    private readonly Button _cancel = new() { Content = "Stop searching" };
    private readonly Func<Func<Task>, Task> _guard;
    private DuplicateReview? _review;
    private Func<Task>? _undoLast;

    /// <summary>Opens the window and starts searching <paramref name="roots"/> at once.</summary>
    public DuplicateReviewWindow(IReadOnlyList<string> roots, IDuplicateScanner scanner, DuplicateCleanup cleanup, IReadOnlyList<ColorTag> tags, Func<Func<Task>, Task>? guard = null)
    {
        _roots = roots;
        _scanner = scanner;
        _cleanup = cleanup;
        _tags = tags;
        _guard = guard ?? (work => work());
        Title = "Duplicate files";
        AppWindow.Resize(new Windows.Graphics.SizeInt32(1100, 760));

        foreach (var name in new[] { "Oldest copy", "Newest copy", "Largest file", "Highest resolution", "Copy in the preferred folder" })
        {
            _rule.Items.Add(name);
        }

        _rule.SelectedIndex = 0;
        _rule.SelectionChanged += (_, _) => ApplyRule();
        _preferred.TextChanged += (_, _) =>
        {
            if (_rule.SelectedIndex == 4)
            {
                ApplyRule();
            }
        };

        foreach (var name in new[] { "Move to the Recycle Bin", "Move to a folder", "Replace with a hard link", "Put a tag on them" })
        {
            _action.Items.Add(name);
        }

        _action.SelectedIndex = 0;
        _action.SelectionChanged += (_, _) =>
        {
            _target.Visibility = _action.SelectedIndex == 1 ? Visibility.Visible : Visibility.Collapsed;
            _tag.Visibility = _action.SelectedIndex == 3 ? Visibility.Visible : Visibility.Collapsed;
        };

        foreach (var tag in tags)
        {
            _tag.Items.Add(tag.Name);
        }

        _tag.SelectedIndex = tags.Count > 0 ? 0 : -1;
        _go.Click += async (_, _) => await RunActionAsync();
        _undo.Click += async (_, _) => await UndoLastAsync();
        _cancel.Click += (_, _) => _closing.Cancel();
        _list.ContainerContentChanging += OnContainerContentChanging;

        var top = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, Padding = new Thickness(16, 8, 16, 8) };
        top.Children.Add(_rule);
        top.Children.Add(_preferred);
        top.Children.Add(_action);
        top.Children.Add(_target);
        top.Children.Add(_tag);
        top.Children.Add(new StackPanel { VerticalAlignment = VerticalAlignment.Bottom, Orientation = Orientation.Horizontal, Spacing = 8, Children = { _go, _undo } });

        var statusRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Padding = new Thickness(16, 8, 16, 8), Children = { _busy, _status, _cancel } };
        var root = new Grid { RowDefinitions = { new RowDefinition { Height = GridLength.Auto }, new RowDefinition { Height = GridLength.Auto }, new RowDefinition { Height = new GridLength(1, GridUnitType.Star) } } };
        Grid.SetRow(statusRow, 0);
        Grid.SetRow(top, 1);
        Grid.SetRow(_list, 2);
        root.Children.Add(statusRow);
        root.Children.Add(top);
        root.Children.Add(_list);
        Content = root;
        Closed += (_, _) =>
        {
            _closing.Cancel();
            _closing.Dispose();
        };

        _status.Text = "Searching…";
        _ = StartAsync();
    }

    private async Task StartAsync()
    {
        var progress = new Progress<string>(text => _status.Text = "Searching: " + text);
        try
        {
            var scan = await _scanner.ScanAsync(_roots, 1, null, includeHidden: false, progress, _closing.Token);
            _review = new DuplicateReview(scan.Groups);
            _busy.IsActive = false;
            _busy.Visibility = Visibility.Collapsed;
            _cancel.Visibility = Visibility.Collapsed;
            Refresh(scan.Notes.Count > 0 ? $" {scan.Notes.Count:N0} item(s) could not be read." : string.Empty);
        }
        catch (OperationCanceledException)
        {
            _busy.IsActive = false;
            _busy.Visibility = Visibility.Collapsed;
            _cancel.Visibility = Visibility.Collapsed;
            _status.Text = "Search stopped.";
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or TimeoutException)
        {
            _busy.IsActive = false;
            _busy.Visibility = Visibility.Collapsed;
            _cancel.Visibility = Visibility.Collapsed;
            _status.Text = $"The search failed: {ex.Message}";
        }
    }

    private void ApplyRule()
    {
        if (_review is null)
        {
            return;
        }

        _preferred.Visibility = _rule.SelectedIndex == 4 ? Visibility.Visible : Visibility.Collapsed;
        var rule = _rule.SelectedIndex switch
        {
            1 => KeepRule.Newest,
            2 => KeepRule.Largest,
            3 => KeepRule.HighestResolution,
            4 => KeepRule.PreferredFolder,
            _ => KeepRule.Oldest,
        };

        _review.Apply(rule, _preferred.Text);
        Refresh(string.Empty);
    }

    private void Refresh(string extra)
    {
        if (_review is null)
        {
            return;
        }

        _list.ItemsSource = null;
        _list.ItemsSource = _review.Groups;
        _go.IsEnabled = _review.Groups.Count > 0;
        _status.Text = _review.Groups.Count == 0
            ? "No duplicates found." + extra
            : $"{_review.Groups.Count:N0} group{(_review.Groups.Count == 1 ? string.Empty : "s")} of identical files, {_review.ExtraCount:N0} extra cop{(_review.ExtraCount == 1 ? "y" : "ies")} taking {DisplayFormat.Size(_review.ExtraBytes)}.{extra}";
    }

    // The group views are made when the list shows them, so thousands of groups stay light.
    private void OnContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (args.InRecycleQueue)
        {
            ((ListViewItem)args.ItemContainer).Content = null;
            return;
        }

        if (args.Item is ReviewGroup group)
        {
            ((ListViewItem)args.ItemContainer).Content = BuildGroup(group);
        }
    }

    private static StackPanel BuildGroup(ReviewGroup group)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 6, 0, 10), Spacing = 4 };
        panel.Children.Add(new TextBlock
        {
            Text = $"{group.Files.Count} {(group.Exact ? "identical" : "similar")} files · {DisplayFormat.Size(group.Files[0].Size)} each · {DisplayFormat.Size(group.ExtraBytes)} to free",
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
        });

        var choice = new RadioButtons { MaxColumns = 1 };
        foreach (var file in group.Files)
        {
            choice.Items.Add(BuildFile(file));
        }

        choice.SelectedIndex = group.Files.ToList().IndexOf(group.Keep);
        choice.SelectionChanged += (_, _) =>
        {
            if (choice.SelectedIndex >= 0)
            {
                group.Keep = group.Files[choice.SelectedIndex];
            }
        };

        panel.Children.Add(choice);
        return panel;
    }

    private static Grid BuildFile(ReviewFile file)
    {
        var row = new Grid { ColumnSpacing = 12 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(72) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        if (Pictures.Contains(Path.GetExtension(file.Path)))
        {
            // Decoded small and off the UI thread by the image pipeline; an unreadable file just shows nothing.
            var image = new Image { Width = 72, Height = 54, Stretch = Microsoft.UI.Xaml.Media.Stretch.UniformToFill };
            image.Source = new BitmapImage { DecodePixelWidth = 144, UriSource = new Uri(file.Path) };
            row.Children.Add(image);
        }

        var text = new StackPanel { Spacing = 0 };
        text.Children.Add(new TextBlock { Text = file.Name, TextTrimming = TextTrimming.CharacterEllipsis });
        text.Children.Add(new TextBlock { Text = file.Folder, Opacity = 0.7, FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis });
        var detail = $"{DisplayFormat.Size(file.Size)} · modified {file.ModifiedUtc.ToLocalTime():yyyy-MM-dd HH:mm}" + (file.Pixels > 0 ? $" · {file.Width}×{file.Height}" : string.Empty);
        text.Children.Add(new TextBlock { Text = detail, Opacity = 0.7, FontSize = 12 });
        Grid.SetColumn(text, 1);
        row.Children.Add(text);
        return row;
    }

    private async Task RunActionAsync()
    {
        if (_review is not { Groups.Count: > 0 } review)
        {
            return;
        }

        var kind = _action.SelectedIndex switch
        {
            1 => CleanupKind.Move,
            2 => CleanupKind.HardLink,
            3 => CleanupKind.Tag,
            _ => CleanupKind.Delete,
        };

        var tagId = _tag.SelectedIndex >= 0 && _tag.SelectedIndex < _tags.Count ? _tags[_tag.SelectedIndex].Id : 0;
        if (kind == CleanupKind.Tag && tagId == 0)
        {
            _status.Text = "There is no tag to put on the copies.";
            return;
        }

        var confirm = new ContentDialog
        {
            Title = "Apply to the extra copies?",
            Content = $"{review.ExtraCount:N0} file{(review.ExtraCount == 1 ? string.Empty : "s")} ({DisplayFormat.Size(review.ExtraBytes)}) will be {Describe(kind)}. The copy marked Keep in each group is never touched.",
            PrimaryButtonText = "Apply",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = Content.XamlRoot,
        };

        if (await confirm.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        _go.IsEnabled = false;
        _status.Text = "Working…";
        var groups = review.ToCleanup();
        var folder = _target.Text;
        CleanupResult result;
        try
        {
            result = await Task.Run(() => _cleanup.RunAsync(groups, kind, folder, tagId, CancellationToken.None));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _status.Text = $"Could not finish: {ex.Message}";
            _go.IsEnabled = true;
            return;
        }

        _undoLast = result.Undo;
        _undo.IsEnabled = result.Undo is not null;
        if (kind != CleanupKind.Tag && result.DonePaths is { Count: > 0 } done)
        {
            review.Resolve(done);
        }

        Refresh(string.Empty);
        var problems = result.Problems.Count > 0 ? $" {result.Problems.Count:N0} could not be done: {result.Problems[0]}" : string.Empty;
        _status.Text = $"{result.Done:N0} file{(result.Done == 1 ? string.Empty : "s")} {Past(kind)} ({DisplayFormat.Size(result.Bytes)}).{problems}  " + _status.Text;
        _go.IsEnabled = review.Groups.Count > 0;
    }

    private async Task UndoLastAsync()
    {
        if (_undoLast is not { } undo)
        {
            return;
        }

        _undo.IsEnabled = false;
        _undoLast = null;
        try
        {
            await Task.Run(undo);
            _status.Text = "Undone. Search again to see the groups as they are now.";
        }
        catch (IOException ex)
        {
            _status.Text = $"Could not undo everything: {ex.Message}";
        }
    }

    private static string Describe(CleanupKind kind) => kind switch
    {
        CleanupKind.Delete => "moved to the Recycle Bin (or the share's recycle folder)",
        CleanupKind.Move => "moved to the folder you chose",
        CleanupKind.HardLink => "replaced by hard links to the kept copy (same contents, no extra space)",
        _ => "tagged and left where they are",
    };

    private static string Past(CleanupKind kind) => kind switch
    {
        CleanupKind.Delete => "moved to the bin",
        CleanupKind.Move => "moved",
        CleanupKind.HardLink => "linked",
        _ => "tagged",
    };
}
