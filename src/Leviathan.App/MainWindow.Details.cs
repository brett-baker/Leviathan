using Leviathan.Core.Hashing;
using Leviathan.Core.Model;
using Leviathan.ViewModels.Folders;
using Leviathan.ViewModels.Preview;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;

namespace Leviathan.App;

/// <summary>The details part of the preview pane (spec §9.2): General, Media and Integrity for the one selected item.</summary>
public sealed partial class MainWindow
{
    private CancellationTokenSource? _detailsCts;
    private string _detailsKey = string.Empty;
    private MediaInfoSource? _detailsMedia;

    // Called with the selection each time it changes; rebuilds only when the item itself changed, so a size update does not flicker.
    private void UpdateDetails(IReadOnlyList<FileEntry> chosen)
    {
        var key = chosen.Count == 1 ? chosen[0].Location.Value + "|" + chosen[0].Size + "|" + chosen[0].Modified.UtcTicks : string.Empty;
        if (key == _detailsKey)
        {
            return;
        }

        _detailsKey = key;
        _detailsCts?.Cancel();
        _detailsCts = new CancellationTokenSource();
        DetailsSections.Children.Clear();
        DetailsHint.Visibility = chosen.Count == 1 ? Visibility.Collapsed : Visibility.Visible;
        if (chosen.Count != 1)
        {
            return;
        }

        var entry = chosen[0];
        var token = _detailsCts.Token;
        AddSection(FileDetails.General(entry));
        if (entry.Kind != FileEntryKind.File)
        {
            return;
        }

        var media = new StackPanel { Spacing = 4 };
        DetailsSections.Children.Add(media);
        if (IsMediaFile(entry))
        {
            media.Children.Add(Muted("Reading media information…"));
            _ = LoadMediaAsync(entry, media, token);
        }

        var props = new StackPanel { Spacing = 4 };
        DetailsSections.Children.Add(props);
        _ = LoadPropertiesAsync(entry, props, token);

        DetailsSections.Children.Add(IntegritySection(entry, token));
    }

    // The Windows property system (spec 9.2): EXIF, audio tags, document properties and the rest, read through the shell's handlers.
    private async Task LoadPropertiesAsync(FileEntry entry, StackPanel into, CancellationToken token)
    {
        try
        {
            await Task.Delay(250, token); // arrowing through a folder must not read every file
            var values = await Task.Run(async () =>
            {
                var file = await Windows.Storage.StorageFile.GetFileFromPathAsync(entry.Location.ToWin32Path());
                var raw = await file.Properties.RetrievePropertiesAsync(WindowsProperties.Keys);
                return raw.ToDictionary(p => p.Key, p => (object?)p.Value, StringComparer.Ordinal);
            }, token);
            if (token.IsCancellationRequested)
            {
                return;
            }

            var sections = WindowsProperties.Sections(values, entry.Extension);
            for (var i = 0; i < sections.Count; i++)
            {
                if (sections[i].Title == "All properties")
                {
                    var expander = new Expander { Header = "All properties", HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch };
                    var inner = new StackPanel();
                    AddSection(sections[i] with { Title = string.Empty }, inner);
                    expander.Content = inner;
                    into.Children.Add(expander);
                }
                else
                {
                    AddSection(sections[i], into);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // The selection moved on.
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or System.Runtime.InteropServices.COMException)
        {
            // A path the shell cannot open (very long, or a provider without property handlers) simply has no extra properties.
        }
    }

    private static bool IsMediaFile(FileEntry entry) =>
        entry.Extension.ToLowerInvariant() is ".mp4" or ".mkv" or ".avi" or ".mov" or ".wmv" or ".m4v" or ".webm" or ".flv" or ".ts" or ".mpg" or ".mpeg" or ".m2ts" or ".mp3" or ".flac" or ".m4a" or ".ogg" or ".opus" or ".wav" or ".aac";

    private async Task LoadMediaAsync(FileEntry entry, StackPanel into, CancellationToken token)
    {
        try
        {
            await Task.Delay(250, token);
            _detailsMedia ??= new MediaInfoSource(() => App.Host.MediaHost);
            var result = await _detailsMedia.ProbeAsync(new MediaFileRef(entry.Location.ToWin32Path(), entry.Size, entry.Modified.UtcDateTime.Ticks, entry.Location.IsNetwork), token);
            if (token.IsCancellationRequested)
            {
                return;
            }

            into.Children.Clear();
            if (result is { State: MediaInfoState.Ok, Info: { } info })
            {
                AddSection(FileDetails.Media(info), into);
            }
            else
            {
                into.Children.Add(Muted(result.Message is { Length: > 0 } why ? $"Media information is unreadable: {why}" : "Media information is unreadable."));
            }
        }
        catch (OperationCanceledException)
        {
            // The selection moved on.
        }
        catch (Exception ex) when (ex is MediaInfoUnavailableException or MediaInfoTransientException)
        {
            if (!token.IsCancellationRequested)
            {
                into.Children.Clear();
                into.Children.Add(Muted(ex.Message));
            }
        }
    }

    private void AddSection(DetailSection section, Panel? into = null)
    {
        var host = into ?? DetailsSections;
        host.Children.Add(new TextBlock { Text = section.Title, Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"], Margin = new Thickness(0, 8, 0, 0) });
        if (!string.IsNullOrEmpty(section.Summary))
        {
            host.Children.Add(new TextBlock { Text = section.Summary, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true });
        }

        var grid = new Grid { ColumnSpacing = 8, RowSpacing = 2 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(84) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        foreach (var row in section.Rows)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var label = new TextBlock { Text = row.Label, Opacity = 0.65, TextTrimming = TextTrimming.CharacterEllipsis };
            Grid.SetRow(label, grid.RowDefinitions.Count - 1);
            var value = new TextBlock { Text = row.Value, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = !row.Copyable };
            if (row.Copyable)
            {
                ToolTipService.SetToolTip(value, "Click to copy");
                value.Tapped += (_, _) =>
                {
                    CopyText(row.Value);
                    MessageText.Text = $"Copied {row.Label.ToLowerInvariant()}.";
                };
            }

            Grid.SetRow(value, grid.RowDefinitions.Count - 1);
            Grid.SetColumn(value, 1);
            grid.Children.Add(label);
            grid.Children.Add(value);
        }

        host.Children.Add(grid);
    }

    private StackPanel IntegritySection(FileEntry entry, CancellationToken token)
    {
        var panel = new StackPanel { Spacing = 4 };
        panel.Children.Add(new TextBlock { Text = "Integrity", Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"], Margin = new Thickness(0, 8, 0, 0) });
        var button = new Button { Content = "Compute MD5, SHA-1, SHA-256" };
        var status = Muted(string.Empty);
        status.Visibility = Visibility.Collapsed;
        var results = new StackPanel { Spacing = 2 };
        panel.Children.Add(button);
        panel.Children.Add(status);
        panel.Children.Add(results);
        button.Click += async (_, _) =>
        {
            button.IsEnabled = false;
            status.Visibility = Visibility.Visible;
            status.Text = "Reading…";
            results.Children.Clear();
            try
            {
                var progress = new Progress<long>(read => status.Text = $"Reading… {DisplayFormat.Size(read)} of {DisplayFormat.Size(entry.Size)}");
                var sums = await Task.Run(async () =>
                {
                    await using var stream = new FileStream(entry.Location.ToWin32Path(), new FileStreamOptions { Mode = FileMode.Open, Access = FileAccess.Read, Share = FileShare.ReadWrite | FileShare.Delete, Options = FileOptions.Asynchronous | FileOptions.SequentialScan, BufferSize = 1 });
                    return await FileChecksums.ComputeAsync(stream, [ChecksumKind.Md5, ChecksumKind.Sha1, ChecksumKind.Sha256], progress, token);
                }, token);
                status.Visibility = Visibility.Collapsed;
                foreach (var (kind, hex) in sums.OrderBy(s => s.Key))
                {
                    var name = kind switch { ChecksumKind.Md5 => "MD5", ChecksumKind.Sha1 => "SHA-1", _ => "SHA-256" };
                    var line = new Grid { ColumnSpacing = 6 };
                    line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                    line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    var copy = new Button { Content = $"Copy {name}", Padding = new Thickness(6, 2, 6, 2), FontSize = 11 };
                    copy.Click += (_, _) =>
                    {
                        CopyText(hex);
                        MessageText.Text = $"Copied the {name} of {entry.Name}.";
                    };
                    var text = new TextBlock { Text = hex, IsTextSelectionEnabled = true, FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas"), FontSize = 11, TextWrapping = TextWrapping.WrapWholeWords, VerticalAlignment = VerticalAlignment.Center };
                    Grid.SetColumn(text, 1);
                    line.Children.Add(copy);
                    line.Children.Add(text);
                    results.Children.Add(line);
                }
            }
            catch (OperationCanceledException)
            {
                // The selection moved on.
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                status.Text = $"Could not read the file: {ex.Message}";
            }
            finally
            {
                button.IsEnabled = true;
            }
        };
        return panel;
    }

    private static TextBlock Muted(string text) => new() { Text = text, TextWrapping = TextWrapping.Wrap, Opacity = 0.65 };

    private static void CopyText(string text)
    {
        var package = new DataPackage();
        package.SetText(text);
        Clipboard.SetContent(package);
    }
}
