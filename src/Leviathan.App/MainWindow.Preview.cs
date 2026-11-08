using Leviathan.Core.Model;
using Leviathan.Playback;
using Leviathan.ViewModels.Commands;
using Leviathan.ViewModels.Folders;
using Leviathan.ViewModels.Preview;
using Leviathan.ViewModels.Windows;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Leviathan.App;

/// <summary>
/// The preview and details pane (spec §9): its width and splitter, the S / M / L video size and the handle under the video, the two
/// collapsible parts, the folder-size lines (spec §12.7), and the floating quick preview. The window's view model holds the sizes (so
/// they are saved in sessions); this file connects them to the controls, within <see cref="PreviewLayout"/>'s limits. The renderers
/// for video, audio, pictures, text and Markdown/HTML fill the video area; the rest arrive in the next slices.
/// </summary>
public sealed partial class MainWindow
{
    private PreviewCoordinator? _coordinator;
    private PreviewPlayer? _player;
    private ImagePreview? _images;
    private TextPreview? _text;
    private WebPreview? _web;
    private PdfPreview? _pdf;
    private FontPreview? _font;
    private SummaryPreview? _summary;
    private ShellPreviewSurface? _shell;
    private bool _playerOpen;
    private string _previewKey = string.Empty;
    private bool _previewActive;
    private double _handleStartY;
    private double _handleStartHeight;
    private bool _handleDragging;

    private void InitializePreviewPane()
    {
        // Previews load after the selection has rested for 150 ms and are cancelled when it moves on (spec 9.1).
        _coordinator = new PreviewCoordinator(TimeProvider.System);
        _coordinator.Requested += (_, request) => OnUi(() => _ = ShowRequestedAsync(request));
        PreviewGrip.GrowsLeft = true;
        PreviewGrip.CurrentWidth = () => _viewModel.ShowPreview ? _viewModel.PreviewWidth : 0;
        PreviewGrip.WidthRequested += (_, width) =>
        {
            // Dragging the pane narrower than half its minimum hides it, like the sidebar.
            if (width < PreviewLayout.MinWidth / 2)
            {
                _viewModel.ShowPreview = false;
                return;
            }

            _viewModel.PreviewWidth = PreviewLayout.Clamp(width, WindowWidth());
        };
        PreviewGrip.ToggleRequested += (_, _) => Run(CommandIds.PreviewPane);
        PreviewGrip.ResetRequested += (_, _) => _viewModel.PreviewWidth = PreviewLayout.DefaultWidth;
        ToolTipService.SetToolTip(PreviewGrip, "Drag to resize the preview pane. Double-click to hide it (Alt+P).");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(PreviewGrip, "Resize preview pane");
        VideoHandle.PointerPressed += OnVideoHandlePressed;
        VideoHandle.PointerMoved += OnVideoHandleMoved;
        VideoHandle.PointerReleased += (_, e) => EndVideoHandleDrag(e.Pointer);
        VideoHandle.PointerCaptureLost += (_, _) => EndVideoHandleDrag(null);
        FileList.QuickPreviewRequested += OnQuickPreviewRequested;
        FileList2.QuickPreviewRequested += OnQuickPreviewRequested;
        FlowList.QuickPreviewRequested += OnQuickPreviewRequested;
        FlowList2.QuickPreviewRequested += OnQuickPreviewRequested;
        SizeChanged += (_, _) => ApplyPreviewPane();
    }

    /// <summary>Shows or hides the pane and gives its parts the saved width, video height and collapsed state.</summary>
    private void ApplyPreviewPane()
    {
        var shown = _viewModel.ShowPreview;
        PreviewPane.Visibility = shown ? Visibility.Visible : Visibility.Collapsed;
        PreviewGrip.Visibility = shown ? Visibility.Visible : Visibility.Collapsed;
        PreviewColumn.Width = new GridLength(shown ? PreviewLayout.Clamp(_viewModel.PreviewWidth, WindowWidth()) : 0);
        PreviewToggle.IsChecked = shown;

        VideoArea.Height = _viewModel.CurrentVideoHeight;
        VideoSmallButton.IsChecked = _viewModel.VideoSize == VideoSize.Small;
        VideoMediumButton.IsChecked = _viewModel.VideoSize == VideoSize.Medium;
        VideoLargeButton.IsChecked = _viewModel.VideoSize == VideoSize.Large;

        PreviewBody.Visibility = _viewModel.PreviewCollapsed ? Visibility.Collapsed : Visibility.Visible;
        PreviewSectionLabel.Text = (_viewModel.PreviewCollapsed ? "▸ " : "▾ ") + "Preview";
        DetailsBody.Visibility = _viewModel.DetailsCollapsed ? Visibility.Collapsed : Visibility.Visible;
        DetailsSectionLabel.Text = (_viewModel.DetailsCollapsed ? "▸ " : "▾ ") + "Details";

        // A hidden or collapsed preview shows nothing and plays nothing; showing it again previews the selection from scratch.
        var active = shown && !_viewModel.PreviewCollapsed;
        if (active != _previewActive)
        {
            _previewActive = active;
            if (active)
            {
                _previewKey = string.Empty;
                UpdatePreview();
            }
            else
            {
                _coordinator?.Cancel();
                ShowOnly(null);
            }
        }
    }

    // What the pane shows for a selection that has rested: one renderer at a time fills the video area (the player, pictures, text or
    // a web page); everything else keeps its placeholder until its own renderer exists (the next slices).
    private async Task ShowRequestedAsync(PreviewRequest request)
    {
        if (!_previewActive || _closed)
        {
            return;
        }

        try
        {
            var entry = request.Entries.Count > 0 ? request.Entries[0] : null;
            var path = entry?.Location.ToWin32Path() ?? string.Empty;
            switch (request.Kind)
            {
                case PreviewKind.Video or PreviewKind.Audio:
                    var player = EnsurePlayer();
                    ShowOnly(player);
                    PreviewBar.Visibility = Visibility.Visible;
                    PreviewBar.Start();
                    _playerOpen = true;

                    // A file on a share is read ahead (its header and index) while the engine opens it (spec 10.1).
                    if (entry!.Location.IsNetwork)
                    {
                        _ = Task.Run(() => MediaPrefetcher.PrefetchAsync(path, request.Cancellation));
                    }

                    PreviewBar.SetMedia(entry, request.Kind == PreviewKind.Video);
                    if (request.Kind == PreviewKind.Audio)
                    {
                        _ = LoadAudioArtAsync(player, entry, request.Cancellation);
                    }

                    await player.OpenAsync(path, entry.Name, request.Kind == PreviewKind.Audio, request.Cancellation);
                    break;
                case PreviewKind.Image:
                    var images = _images ??= AddSurface(new ImagePreview());
                    ShowOnly(images);
                    await images.OpenAsync(path, request.Cancellation);
                    break;
                case PreviewKind.Text:
                    var text = _text ??= AddSurface(new TextPreview());
                    ShowOnly(text);
                    await text.OpenAsync(path, entry!.Extension, request.Cancellation);
                    break;
                case PreviewKind.Markdown or PreviewKind.Html:
                    var web = _web ??= AddSurface(new WebPreview());
                    ShowOnly(web);
                    var html = await Task.Run(() => LoadWebPage(path, request.Kind), CancellationToken.None).WaitAsync(PreviewLimits.LoadTimeout, request.Cancellation);
                    await web.ShowAsync(html, request.Cancellation);
                    break;
                case PreviewKind.Pdf:
                    var pdf = _pdf ??= AddSurface(new PdfPreview());
                    ShowOnly(pdf);
                    await pdf.OpenAsync(path, request.Cancellation);
                    break;
                case PreviewKind.Font:
                    var font = _font ??= AddSurface(new FontPreview());
                    ShowOnly(font);
                    await font.OpenAsync(path, request.Cancellation);
                    break;
                case PreviewKind.Office:
                    var shell = _shell ??= AddShellSurface();
                    ShowOnly(shell);
                    await shell.OpenAsync(path, WinRT.Interop.WindowNative.GetWindowHandle(this), request.Cancellation);
                    break;
                case PreviewKind.Archive when ArchiveListing.CanList(entry!.Extension):
                    await ShowArchiveAsync(entry, path, request.Cancellation);
                    break;
                case PreviewKind.Folder:
                    await ShowFolderAsync(entry!, path, request.Cancellation);
                    break;
                case PreviewKind.Multiple:
                    await ShowMultipleAsync(request.Entries, request.Cancellation);
                    break;
                case PreviewKind.Archive:
                    VideoPlaceholder.Text = $"{entry!.Name}\nListing {entry.Extension} archives needs 7-Zip support, which comes with the archive feature.";
                    ShowOnly(null);
                    break;
                default:
                    ShowOnly(null);
                    break;
            }
        }
        catch (OperationCanceledException)
        {
            // The selection moved on while this one was opening.
        }
        catch (Exception ex) when (ex is NotSupportedException or InvalidDataException)
        {
            VideoPlaceholder.Text = ex.Message;
            ShowOnly(null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or TimeoutException)
        {
            VideoPlaceholder.Text = ex is TimeoutException ? "This file took too long to read." : $"This file could not be read: {ex.Message}";
            ShowOnly(null);
        }
    }

    private async Task ShowArchiveAsync(FileEntry entry, string path, CancellationToken cancellationToken)
    {
        var contents = await Task.Run(() => ArchiveListing.Read(path), CancellationToken.None).WaitAsync(PreviewLimits.LoadTimeout, cancellationToken);
        var summary = _summary ??= AddSurface(new SummaryPreview());
        ShowOnly(summary);
        var rows = contents.Items.Select(i => i.IsFolder ? i.Path + "/" : $"{i.Path}  ·  {DisplayFormat.Size(i.Size)}").ToList();
        var more = contents.Truncated ? $" · first {ArchiveListing.MaxItems:N0} shown" : string.Empty;
        summary.Show($"{entry.Name}: {contents.Items.Count:N0} items · {DisplayFormat.Size(contents.TotalBytes)} unpacked{more}", rows);
    }

    private async Task ShowFolderAsync(FileEntry folder, string path, CancellationToken cancellationToken)
    {
        var children = await Task.Run(() => ListChildren(path), CancellationToken.None).WaitAsync(PreviewLimits.LoadTimeout, cancellationToken);
        var sizes = _bound?.Sizes;
        var rows = new List<(string Text, double Weight)>();
        var map = new List<MapItem>();
        var uncounted = 0;
        foreach (var (name, isFolder, size) in children.Items)
        {
            double weight = size;
            if (isFolder)
            {
                var child = new FileEntry(name, folder.Location.Combine(name), FileEntryKind.Folder, 0, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, FileAttributes.Directory);
                if (sizes?.ValueOf(child, FolderMetric.TotalBytes) is { } total)
                {
                    weight = total;
                }
                else
                {
                    weight = 0;
                    uncounted++;
                }
            }

            map.Add(new MapItem(isFolder ? name + "/" : name, weight, isFolder));
            rows.Add((isFolder ? $"{name}/  ·  {(weight > 0 ? DisplayFormat.Size((long)weight) : "not counted yet")}" : $"{name}  ·  {DisplayFormat.Size(size)}", weight));
        }

        var summary = _summary ??= AddSurface(new SummaryPreview());
        ShowOnly(summary);
        var files = children.Items.Count(i => !i.IsFolder);
        var folders = children.Items.Count - files;
        var note = (children.Capped ? $" · first {children.Items.Count:N0} listed" : string.Empty) + (uncounted > 0 ? $" · {uncounted:N0} folders not counted yet" : string.Empty);
        summary.Show(
            $"{folder.Name}: {files:N0} files, {folders:N0} folders{note}",
            [.. rows.OrderByDescending(r => r.Weight).Select(r => r.Text)],
            [.. map.Where(m => m.Weight > 0)]);
    }

    private async Task ShowMultipleAsync(IReadOnlyList<FileEntry> entries, CancellationToken cancellationToken)
    {
        var summary = SelectionSummary.From(entries);
        var rows = summary.Types.Select(t => $"{t.Label}  ·  {t.Count:N0} files  ·  {DisplayFormat.Size(t.Bytes)}").ToList();

        // A strip of the first few pictures, opened off the UI thread and decoded small.
        var pictures = entries.Where(e => e.Kind == FileEntryKind.File && PreviewClassifier.Classify(e) == PreviewKind.Image && e.Size < 32L << 20).Take(12).Select(e => e.Location.ToWin32Path()).ToList();
        var thumbnails = new List<Microsoft.UI.Xaml.Media.Imaging.BitmapImage>();
        foreach (var picture in pictures)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                using var file = await Task.Run(() => new FileStream(picture, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete), CancellationToken.None).WaitAsync(PreviewLimits.LoadTimeout, cancellationToken);
                var bitmap = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage { DecodePixelWidth = 112 };
                await bitmap.SetSourceAsync(file.AsRandomAccessStream()).AsTask(cancellationToken);
                thumbnails.Add(bitmap);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or TimeoutException or System.Runtime.InteropServices.COMException or ArgumentException)
            {
                // A picture that will not open is left out of the strip.
            }
        }

        var surface = _summary ??= AddSurface(new SummaryPreview());
        ShowOnly(surface);
        surface.Show(summary.Headline, rows, null, thumbnails);
    }

    private sealed record Children(List<(string Name, bool IsFolder, long Size)> Items, bool Capped);

    // The direct children of a folder with their sizes, off the UI thread; at most 2,000, so a huge folder cannot stall the pane.
    private static Children ListChildren(string path)
    {
        const int max = 2000;
        var items = new List<(string, bool, long)>();
        var capped = false;
        foreach (var info in new DirectoryInfo(path).EnumerateFileSystemInfos())
        {
            if (items.Count >= max)
            {
                capped = true;
                break;
            }

            items.Add(info is FileInfo file ? (file.Name, false, file.Length) : (info.Name, true, 0L));
        }

        return new Children(items, capped);
    }

    // Reads the start of a Markdown or HTML file and makes it a page the sandbox accepts (off the UI thread).
    private static string LoadWebPage(string path, PreviewKind kind)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.SequentialScan);
        var buffer = new byte[(int)Math.Min(file.Length, PreviewLimits.MaxWebBytes)];
        var read = file.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);
        var text = TextDecoder.Decode(buffer.AsSpan(0, read), file.Length > read).Text;
        return kind == PreviewKind.Markdown ? WebPreviewHtml.FromMarkdown(text) : WebPreviewHtml.FromHtml(text);
    }

    // The shell surface keeps the handler's window over itself when the app window moves, which no layout event reports.
    private ShellPreviewSurface AddShellSurface()
    {
        var surface = AddSurface(new ShellPreviewSurface());
        AppWindow.Changed += (_, e) =>
        {
            if (e.DidPositionChange || e.DidSizeChange)
            {
                surface.Reposition();
            }
        };
        return surface;
    }

    private T AddSurface<T>(T surface)
        where T : FrameworkElement
    {
        VideoHost.Children.Add(surface);
        return surface;
    }

    private PreviewPlayer EnsurePlayer()
    {
        if (_player is { } existing)
        {
            return existing;
        }

        var player = new PreviewPlayer();
        player.Failed += (_, message) => OnUi(() =>
        {
            VideoPlaceholder.Text = $"The video engine could not start: {message}";
            VideoPlaceholder.Visibility = Visibility.Visible;
        });
        VideoHost.Children.Add(player);
        PreviewBar.Attach(player);
        return _player = player;
    }

    // The cover art and waveform of an audio file arrive after it has opened (ffmpeg draws them in the media process); each shows when it
    // is there, and a file with no cover or no media process simply keeps the plain name.
    private async Task LoadAudioArtAsync(PreviewPlayer player, FileEntry audio, CancellationToken cancellationToken)
    {
        try
        {
            var scale = Content.XamlRoot?.RasterizationScale ?? 1;
            var width = (int)(Math.Clamp(_viewModel.PreviewWidth - 32, 200, 1000) * scale);
            var client = new AudioArtClient(() => App.Host.MediaHost);
            var cover = client.GetAsync(audio, cover: true, (int)(256 * scale), 0, cancellationToken);
            var wave = client.GetAsync(audio, cover: false, width, (int)(44 * scale), cancellationToken);
            await Task.WhenAll(cover, wave);
            if (!cancellationToken.IsCancellationRequested)
            {
                player.ShowAudioArt(await cover, await wave);
            }
        }
        catch (OperationCanceledException)
        {
            // The pane moved on to another file.
        }
    }

    // Shows one renderer in the video area and clears and hides the others; null shows the placeholder text instead.
    private void ShowOnly(FrameworkElement? next)
    {
        foreach (FrameworkElement? surface in new FrameworkElement?[] { _player, _images, _text, _web, _pdf, _font, _summary, _shell })
        {
            if (surface is null)
            {
                continue;
            }

            if (surface == next)
            {
                surface.Visibility = Visibility.Visible;
                continue;
            }

            surface.Visibility = Visibility.Collapsed;
            switch (surface)
            {
                case ImagePreview images:
                    images.Clear();
                    break;
                case TextPreview text:
                    text.Clear();
                    break;
                case WebPreview web:
                    web.Clear();
                    break;
                case PdfPreview pdf:
                    pdf.Clear();
                    break;
                case FontPreview font:
                    font.Clear();
                    break;
                case SummaryPreview summary:
                    summary.Clear();
                    break;
                case ShellPreviewSurface shell:
                    shell.Clear();
                    break;
            }
        }

        VideoPlaceholder.Visibility = next is null ? Visibility.Visible : Visibility.Collapsed;
        if (next != _player)
        {
            PreviewBar.Stop();
            PreviewBar.Visibility = Visibility.Collapsed;
            if (_playerOpen && _player is { } player)
            {
                _playerOpen = false;
                _ = player.CloseFileAsync();
            }
        }
    }

    /// <summary>Stops the preview and releases the engine when the window closes.</summary>
    private void ClosePreview()
    {
        _coordinator?.Dispose();
        _images?.Clear();
        _text?.Clear();
        _web?.Release();
        _pdf?.Clear();
        _shell?.Clear();
        if (_player is { } player)
        {
            _player = null;
            _ = player.ReleaseAsync();
        }
    }

    private void OnQuickPreviewRequested(object? sender, EventArgs e) => Run(CommandIds.PreviewQuick);

    private void OnVideoSmallClick(object sender, RoutedEventArgs e) => ChooseVideoSize(sender, CommandIds.PreviewVideoSmall);

    private void OnVideoMediumClick(object sender, RoutedEventArgs e) => ChooseVideoSize(sender, CommandIds.PreviewVideoMedium);

    private void OnVideoLargeClick(object sender, RoutedEventArgs e) => ChooseVideoSize(sender, CommandIds.PreviewVideoLarge);

    // Clicking the button that is already on would switch it off; the size stays, so the button stays on.
    private void ChooseVideoSize(object sender, CommandId command)
    {
        ((Microsoft.UI.Xaml.Controls.Primitives.ToggleButton)sender).IsChecked = true;
        Run(command);
    }

    private void OnPreviewSectionClick(object sender, RoutedEventArgs e) => _viewModel.PreviewCollapsed = !_viewModel.PreviewCollapsed;

    private void OnDetailsSectionClick(object sender, RoutedEventArgs e) => _viewModel.DetailsCollapsed = !_viewModel.DetailsCollapsed;

    private void OnVideoHandlePressed(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (!e.GetCurrentPoint(VideoHandle).Properties.IsLeftButtonPressed)
        {
            return;
        }

        _handleDragging = true;
        _handleStartY = e.GetCurrentPoint(null).Position.Y;
        _handleStartHeight = _viewModel.CurrentVideoHeight;
        VideoHandle.CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void OnVideoHandleMoved(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (_handleDragging)
        {
            _viewModel.DragVideoHeight(_handleStartHeight + (e.GetCurrentPoint(null).Position.Y - _handleStartY));
            e.Handled = true;
        }
    }

    private void EndVideoHandleDrag(Microsoft.UI.Xaml.Input.Pointer? pointer)
    {
        if (!_handleDragging)
        {
            return;
        }

        _handleDragging = false;
        if (pointer is not null)
        {
            VideoHandle.ReleasePointerCapture(pointer);
        }
    }

    /// <summary>Shows the size of the one selected folder, and asks for it to be counted when it is not known.</summary>
    private void UpdatePreview()
    {
        UpdatePreviewActions();
        var tab = _viewModel.Tabs.Selected;
        PreviewTitle.Text = PreviewText.Title(tab is { IsSplit: true } ? tab.Panes : ViewModels.Tabs.PaneLayout.Single, tab?.FocusedPane ?? 0);
        var chosen = _bound?.Selection.SelectedEntries ?? [];
        VideoPlaceholder.Text = PreviewText.Placeholder(chosen);
        var key = string.Join('|', chosen.Select(e => e.Location.Value));
        if (key != _previewKey)
        {
            _previewKey = key;
            if (_previewActive)
            {
                _coordinator?.Select(chosen);
            }
        }

        if (_bound?.Selection.SelectedEntries is not { Count: 1 } selected || selected[0].Kind == FileEntryKind.File || _bound.Sizes is not { } sizes)
        {
            PreviewName.Visibility = Visibility.Collapsed;
            PreviewSize.Visibility = Visibility.Collapsed;
            return;
        }

        var folder = selected[0];
        sizes.RequestNow(folder);
        PreviewName.Text = folder.Name;
        PreviewName.Visibility = Visibility.Visible;
        var detail = sizes.DetailFor(folder.Location);
        PreviewSize.Text = detail.Length > 0 ? detail : sizes.TextFor(folder).Length > 0 ? "Counting…" : string.Empty;
        PreviewSize.Visibility = PreviewSize.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    // Move and Copy under the preview send the focused pane's selection to the other pane's folder, like F6 and F5.
    private void UpdatePreviewActions()
    {
        var tab = _viewModel.Tabs.Selected;
        var split = tab is { IsSplit: true };
        PreviewActions.Visibility = split ? Visibility.Visible : Visibility.Collapsed;
        if (!split)
        {
            return;
        }

        PreviewMoveButton.IsEnabled = _commands.CanExecute(CommandIds.PaneMoveToOther, new CommandContext(null));
        PreviewCopyButton.IsEnabled = _commands.CanExecute(CommandIds.PaneCopyToOther, new CommandContext(null));
        var target = tab!.Other?.History.Current.ToString() ?? string.Empty;
        ToolTipService.SetToolTip(PreviewMoveButton, $"Move the selection to {target}");
        ToolTipService.SetToolTip(PreviewCopyButton, $"Copy the selection to {target}");
    }

    private void OnPreviewMoveClick(object sender, RoutedEventArgs e) => Run(CommandIds.PaneMoveToOther);

    private void OnPreviewCopyClick(object sender, RoutedEventArgs e) => Run(CommandIds.PaneCopyToOther);

    // A total arrived for a folder the tab asked about: the pane and the card show it.
    private void OnFolderSizesChanged(object? sender, IReadOnlyList<Core.Paths.LocationPath> folders) => OnUi(() =>
    {
        UpdatePreview();
        UpdateHeaders();
    });

    /// <summary>
    /// Alt+T or "Triage mode from here" (spec §10.1): a triage window over the videos of the folder view in their sort order, starting at the
    /// selected one, or over the selected videos when several are. When it closes, the file it was on is selected in the folder again.
    /// </summary>
    public void StartTriage() => OnUi(() =>
    {
        var rows = _bound?.Items.Rows ?? [];
        var selected = _bound?.Selection.SelectedEntries ?? [];
        var (queue, start) = ViewModels.Triage.TriageQueue.FromHere(rows, selected, App.Host.Settings.Current.Media.ToTriageOptions());
        if (queue.Count == 0)
        {
            MessageText.Text = "There are no videos here to triage.";
            return;
        }

        var tab = _bound;

        // The other pane of a split tab is where kept files go (spec §10.1); looked up when asked, as the user may move that pane meanwhile.
        string? OtherPaneFolder()
        {
            var shown = _viewModel.Tabs.Selected;
            var other = shown?.Secondary is null ? null : ReferenceEquals(tab, shown) ? shown.Secondary : shown;
            return other?.History.Current is { Kind: Core.Paths.LocationKind.Drive or Core.Paths.LocationKind.Unc } here && here != tab?.History.Current
                ? here.ToWin32Path()
                : null;
        }

        var window = new TriageWindow(queue, start, queue[0].Location.Parent?.Name ?? "videos", OtherPaneFolder);
        window.Notice += (_, text) => OnUi(() => MessageText.Text = text);
        window.Exited += (_, file) => OnUi(() =>
        {
            if (file is not null && tab?.Items.Rows.FirstOrDefault(r => r.Location == file.Location) is { } row)
            {
                tab.Selection.Click(row);
            }
        });
        window.Activate();
    });

    private QuickPreviewWindow? _quick;

    /// <summary>
    /// Space (spec §9): the large floating preview of the selection, or of the focused item when nothing is selected; the same key
    /// closes it. It follows the focused pane's selection like the pane does.
    /// </summary>
    public void ToggleQuickPreview() => OnUi(() =>
    {
        if (_quick is not null)
        {
            _quick.Close();
            return;
        }

        var selection = _bound?.Selection;
        IReadOnlyList<FileEntry> entries = selection?.SelectedEntries is { Count: > 0 } chosen
            ? chosen
            : selection?.Focus is { } focus && _bound!.Items.Rows.FirstOrDefault(r => r.Location == focus) is { } focused ? [focused] : [];
        if (entries.Count == 0)
        {
            return;
        }

        _quick = new QuickPreviewWindow(entries);
        _quick.Closed += (_, _) => _quick = null;
        _quick.Activate();
    });
}
