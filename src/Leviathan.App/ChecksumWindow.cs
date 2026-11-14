using System.Text;
using Leviathan.Core.Hashing;
using Leviathan.Core.Paths;
using Leviathan.ViewModels.Checksums;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;

namespace Leviathan.App;

/// <summary>
/// Checksums (spec §15.4): MD5, SHA-1, SHA-256, SHA-512, xxHash3 and CRC32 for one file, several, or a whole folder, computed in the
/// worker in one read of each file; or every file a checksum file names, checked against it. The result can be copied or saved as a
/// checksum file (<c>.sha256</c>, <c>.md5</c>, ...), and a sum pasted from a download page finds the file it belongs to. The reading
/// runs in the worker; this class only builds controls and shows rows.
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001:Types that own disposable fields should be disposable",
    Justification = "The cancellation source is released when the window closes (Closed), which a Window cannot express as IDisposable.")]
internal sealed class ChecksumWindow : Window
{
    private readonly IReadOnlyList<LocationPath> _items;
    private readonly IChecksumScanner _scanner;
    private readonly CancellationTokenSource _closing = new();
    private readonly Dictionary<ChecksumKind, CheckBox> _kindBoxes = [];
    private readonly StackPanel _kindPanel = new() { Orientation = Orientation.Horizontal, Spacing = 12 };
    private readonly Button _run = new() { Content = "Compute", Style = (Style)Application.Current.Resources["AccentButtonStyle"] };
    private readonly Button _stop = new() { Content = "Stop", Visibility = Visibility.Collapsed };
    private readonly ProgressRing _busy = new() { Width = 20, Height = 20, IsActive = false, Visibility = Visibility.Collapsed };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
    private readonly ComboBox _shown = new() { Header = "Show", MinWidth = 140 };
    private readonly TextBox _compare = new() { Header = "Compare with a sum", PlaceholderText = "Paste a checksum from a download page", MinWidth = 360, FontFamily = new FontFamily("Consolas") };
    private readonly ListView _list = new() { SelectionMode = ListViewSelectionMode.Extended };
    private readonly Button _copy = new() { Content = "Copy as checksum file", IsEnabled = false };
    private readonly Button _save = new() { Content = "Save checksum file…", IsEnabled = false };
    private readonly Button _verify = new() { Content = "Verify against a checksum file…" };
    private IReadOnlyList<ChecksumRow> _rows = [];
    private IReadOnlyList<ChecksumRow> _listed = [];
    private CancellationTokenSource _running = new();
    private bool _verifying;
    private string _folder = string.Empty;
    private bool _updating;

    /// <summary>Opens the window and starts at once: computing <paramref name="items"/> (SHA-256 to begin with), or checking them as checksum files when <paramref name="verify"/>.</summary>
    public ChecksumWindow(IReadOnlyList<LocationPath> items, bool verify, IChecksumScanner scanner)
    {
        _items = items;
        _scanner = scanner;
        _verifying = verify;
        Title = verify ? "Verify checksums" : "Checksums";
        AppWindow.Resize(new Windows.Graphics.SizeInt32(1000, 700));

        foreach (var kind in ChecksumKinds.All)
        {
            var box = new CheckBox { Content = ChecksumKinds.Title(kind), IsChecked = kind == ChecksumKind.Sha256, MinWidth = 0 };
            _kindBoxes[kind] = box;
            _kindPanel.Children.Add(box);
        }

        _run.Click += (_, _) => _ = StartAsync(verify: false, _items.Select(i => i.Value).ToList());
        _stop.Click += (_, _) => _running.Cancel();
        _verify.Click += async (_, _) => await VerifyAgainstFileAsync();
        _shown.SelectionChanged += (_, _) => ShowRows();
        _compare.TextChanged += (_, _) => HighlightMatches();
        _copy.Click += (_, _) => CopyFile();
        _save.Click += async (_, _) => await SaveFileAsync();
        _list.ItemContainerStyle = new Style(typeof(ListViewItem)) { Setters = { new Setter(Control.MinHeightProperty, 0.0), new Setter(Control.PaddingProperty, new Thickness(8, 3, 8, 3)) } };

        var top = new StackPanel { Spacing = 10, Margin = new Thickness(16, 14, 16, 8) };
        var kinds = new StackPanel { Spacing = 4 };
        kinds.Children.Add(new TextBlock { Text = "Compute", Opacity = 0.7 });
        kinds.Children.Add(_kindPanel);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Bottom };
        buttons.Children.Add(_run);
        buttons.Children.Add(_stop);
        buttons.Children.Add(_verify);
        var controls = new Grid { ColumnSpacing = 24 };
        controls.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        controls.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        controls.Children.Add(kinds);
        Grid.SetColumn(buttons, 1);
        controls.Children.Add(buttons);
        var status = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        status.Children.Add(_busy);
        status.Children.Add(_status);
        var filters = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 16 };
        filters.Children.Add(_shown);
        filters.Children.Add(_compare);
        top.Children.Add(controls);
        top.Children.Add(status);
        top.Children.Add(filters);

        var bottom = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(16, 8, 16, 14) };
        bottom.Children.Add(_copy);
        bottom.Children.Add(_save);

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.Children.Add(top);
        Grid.SetRow(_list, 1);
        _list.Margin = new Thickness(16, 0, 16, 0);
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

        if (verify)
        {
            _kindPanel.Visibility = Visibility.Collapsed;
            kinds.Visibility = Visibility.Collapsed;
            _run.Visibility = Visibility.Collapsed;
        }

        _ = StartAsync(verify, _items.Select(i => i.Value).ToList());
    }

    private IReadOnlyList<ChecksumKind> ChosenKinds() => [.. _kindBoxes.Where(p => p.Value.IsChecked == true).Select(p => p.Key)];

    private async Task StartAsync(bool verify, IReadOnlyList<string> sources)
    {
        var kinds = ChosenKinds();
        if (!verify && kinds.Count == 0)
        {
            _status.Text = "Tick at least one kind of sum.";
            return;
        }

        _running.Cancel();
        var source = _running = CancellationTokenSource.CreateLinkedTokenSource(_closing.Token);
        var token = source.Token;
        _verifying = verify;
        Title = verify ? "Verify checksums" : "Checksums";
        _run.IsEnabled = false;
        _verify.IsEnabled = false;
        _copy.IsEnabled = false;
        _save.IsEnabled = false;
        _stop.Visibility = Visibility.Visible;
        _busy.Visibility = Visibility.Visible;
        _busy.IsActive = true;
        _status.Text = verify ? "Checking…" : "Reading…";
        try
        {
            var progress = new Progress<string>(text => _status.Text = text);
            var run = await _scanner.RunAsync(sources, kinds, verify, progress, token);
            _rows = run.Rows;
            _folder = verify ? FolderOfChecksumFile(sources) : ChecksumView.DefaultFolder(_items, _rows);
            var note = run.Notes.Count > 0 ? "\n" + string.Join("\n", run.Notes) : string.Empty;
            FillShown();
            ShowRows();
            _status.Text = ChecksumView.Summary(_rows) + note;
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
            _run.IsEnabled = true;
            _verify.IsEnabled = true;
            var any = _rows.Any(r => r.Sums.Count > 0);
            _copy.IsEnabled = any;
            _save.IsEnabled = any;
        }
    }

    private static string FolderOfChecksumFile(IReadOnlyList<string> sources) => sources.Count > 0 ? Path.GetDirectoryName(sources[0]) ?? string.Empty : string.Empty;

    // The kinds that have a sum in the results, in the order of the list; the one shown stays when it is still there.
    private void FillShown()
    {
        var kept = _shown.SelectedItem as string;
        _updating = true;
        _shown.Items.Clear();
        foreach (var kind in ChecksumKinds.All.Where(k => _rows.Any(r => r.Sums.ContainsKey(k))))
        {
            _shown.Items.Add(ChecksumKinds.Title(kind));
        }

        _shown.SelectedItem = kept is not null && _shown.Items.Contains(kept) ? kept : _shown.Items.Count > 0 ? _shown.Items[0] : null;
        _updating = false;
    }

    private ChecksumKind? ShownKind() => ChecksumKinds.All.Cast<ChecksumKind?>().FirstOrDefault(k => ChecksumKinds.Title(k!.Value) == _shown.SelectedItem as string);

    private void ShowRows()
    {
        if (_updating)
        {
            return;
        }

        var kind = ShownKind();
        _listed = _rows;
        _list.Items.Clear();
        foreach (var row in _rows)
        {
            var name = ChecksumView.RelativeName(row.Path, _folder);
            var glyph = row.State switch
            {
                ChecksumState.Ok => _verifying ? "✓" : "·",
                ChecksumState.Mismatch => "✕",
                ChecksumState.Missing => "?",
                _ => "!",
            };
            var hash = kind is { } k && row.Sums.TryGetValue(k, out var value) ? value : string.Empty;
            var text = new TextBlock
            {
                Text = $"{glyph}  {name}\n      {(row.Message is { Length: > 0 } message ? message : hash)}",
                FontFamily = new FontFamily("Consolas"),
                FontSize = 12,
                TextTrimming = TextTrimming.CharacterEllipsis,
                IsTextSelectionEnabled = false,
            };
            if (row.State is ChecksumState.Mismatch or ChecksumState.Failed)
            {
                text.Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 255, 143, 148));
            }
            else if (row.State == ChecksumState.Missing)
            {
                text.Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 255, 208, 96));
            }

            ToolTipService.SetToolTip(text, row.Path);
            _list.Items.Add(text);
        }

        HighlightMatches();
    }

    // A pasted sum selects the file or files it belongs to; the status line says so in words.
    private void HighlightMatches()
    {
        if (_updating)
        {
            return;
        }

        _list.SelectedItems.Clear();
        if (string.IsNullOrWhiteSpace(_compare.Text))
        {
            return;
        }

        var found = ChecksumView.Matching(_rows, _compare.Text);
        for (var i = 0; i < _listed.Count; i++)
        {
            if (found.Contains(_listed[i]))
            {
                _list.SelectedItems.Add(_list.Items[i]);
            }
        }

        _status.Text = found.Count > 0
            ? $"That sum belongs to {ChecksumView.RelativeName(found[0].Path, _folder)}{(found.Count > 1 ? $" and {found.Count - 1:N0} more" : string.Empty)}."
            : ChecksumView.KindOfPasted(_compare.Text) is { } kind
                ? $"No file has that {ChecksumKinds.Title(kind)} sum{(_rows.Any(r => r.Sums.ContainsKey(kind)) ? "." : "; compute it first.")}"
                : "That is not the length of any kind of sum.";
    }

    private string FileText(ChecksumKind kind) => ChecksumView.ChecksumFile(_rows, kind, _folder);

    private void CopyFile()
    {
        if (ShownKind() is not { } kind)
        {
            return;
        }

        var data = new DataPackage();
        data.SetText(FileText(kind));
        Clipboard.SetContent(data);
        _status.Text = $"Copied the {ChecksumKinds.Title(kind)} sums as a checksum file.";
    }

    private async Task SaveFileAsync()
    {
        if (ShownKind() is not { } kind)
        {
            return;
        }

        try
        {
            var picker = new Windows.Storage.Pickers.FileSavePicker { SuggestedFileName = ChecksumView.DefaultFileName(_items, kind) };
            picker.FileTypeChoices.Add($"{ChecksumKinds.Title(kind)} checksums", [ChecksumKinds.Extension(kind)]);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
            if (await picker.PickSaveFileAsync() is not { } file)
            {
                return;
            }

            // Written beside the files so the names in it are relative to where it lives, as a checksum file's names are meant to be.
            var folder = Path.GetDirectoryName(file.Path) ?? string.Empty;
            var text = ChecksumView.ChecksumFile(_rows, kind, folder.Length > 0 ? folder : _folder);
            await Task.Run(() => File.WriteAllText(file.Path, text, new UTF8Encoding(false)));
            _status.Text = $"Saved {file.Name}.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
        {
            _status.Text = $"Could not save the checksum file: {ex.Message}";
        }
    }

    private async Task VerifyAgainstFileAsync()
    {
        try
        {
            var picker = new Windows.Storage.Pickers.FileOpenPicker();
            foreach (var kind in ChecksumKinds.All)
            {
                picker.FileTypeFilter.Add(ChecksumKinds.Extension(kind));
            }

            picker.FileTypeFilter.Add(".sfv");
            picker.FileTypeFilter.Add(".txt");
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
            if (await picker.PickSingleFileAsync() is { } file)
            {
                await StartAsync(verify: true, [file.Path]);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
        {
            _status.Text = $"Could not open the checksum file: {ex.Message}";
        }
    }
}
