using System.Runtime.InteropServices.WindowsRuntime;
using Leviathan.Core.Model;
using Leviathan.Core.Platform;
using Leviathan.Media;
using Leviathan.Playback;
using Leviathan.ViewModels.Commands;
using Leviathan.ViewModels.Folders;
using Leviathan.ViewModels.Triage;
using LibVLCSharp.Platforms.Windows;
using LibVLCSharp.Shared;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using Windows.UI.Core;
using VirtualKey = Windows.System.VirtualKey;

namespace Leviathan.App;

/// <summary>
/// Triage mode (spec §10.1): a window that goes through a queue of videos with one key per decision. The file plays full size with an
/// overlay (name, size, duration, resolution, codec, folder) that fades after two seconds; a slim list shows the queue with each file's
/// decision; the progress, the decisions made and the space reclaimed run across the top; when the queue ends a summary lets discarded files
/// be looked over and restored. The work is <see cref="TriageSession"/>'s (decisions, undo, advancing), the keys are
/// <see cref="TriageDispatcher"/>'s (the map of §10.2, remappable), and the file work is the worker's through
/// <see cref="TriageFileActions"/>; this window only shows them. It reads the next two files of a share ahead (header and index) while the
/// current one plays. Nothing here touches the disk or the network on the UI thread.
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001:Types that own disposable fields should be disposable",
    Justification = "The cancellation source, timers, session and engine are released when the window closes (Closed), which a Window cannot express as IDisposable.")]
internal sealed partial class TriageWindow : Window
{

    private readonly IReadOnlyList<FileEntry> _queue;
    private readonly int _startIndex;
    private readonly CancellationTokenSource _closing = new();
    private readonly DispatcherQueueTimer _timer;
    private readonly HashSet<string> _prefetched = new(StringComparer.OrdinalIgnoreCase);
    private LibVlcEngine? _engine;
    private TriageSession? _session;
    private Leviathan.ViewModels.Triage.TriageRecorder? _recorder;
    private TriageDispatcher? _dispatcher;
    private bool _updating;
    private bool _syncingQueue;
    private bool _overlayHidden;
    private TimeSpan _shownDuration;
    private MediaInfo? _info;
    private string? _infoFor;
    private FileEntry? _last;
    private readonly Func<string?> _destination;
    private ITriageActions? _actions;
    private bool _handOffDone;

    /// <summary>The inbox session this window works through, or null for a plain queue; decisions are saved to the inbox when the window closes (spec §10.1).</summary>
    public InboxSession? Inbox { get; set; }

    /// <summary>Opens triage over <paramref name="queue"/>, starting at <paramref name="startIndex"/>.</summary>
    /// <param name="queue">The videos to go through.</param>
    /// <param name="startIndex">Where in the queue to start.</param>
    /// <param name="title">The folder's name for the window title.</param>
    /// <param name="destination">Where kept files go: the folder of the other pane of a split tab, or null for none (the keep folder of the settings is used then).</param>
    public TriageWindow(IReadOnlyList<FileEntry> queue, int startIndex, string title, Func<string?>? destination = null)
    {
        _queue = queue;
        _startIndex = startIndex;
        _destination = destination ?? (() => null);
        InitializeComponent();
        Title = $"Triage · {title}";

        // The video surface and the player take about a second to come up; show the file and the count at once instead of an empty window.
        if (queue.Count > 0)
        {
            NameText.Text = queue[Math.Clamp(startIndex, 0, queue.Count - 1)].Name;
            ProgressText.Text = $"Opening · {queue.Count:N0} videos";
        }

        AppWindow.Resize(new Windows.Graphics.SizeInt32(1400, 860));
        _timer = DispatcherQueue.GetForCurrentThread().CreateTimer();
        _timer.Interval = TimeSpan.FromMilliseconds(250);
        _timer.Tick += (_, _) => Refresh();
        Overlay.OpacityTransition = new ScalarTransition { Duration = TimeSpan.FromMilliseconds(400) };
        // A filmstrip cell: a thumbnail in a rounded frame with a round mark for the decision in its corner. Children: 0 frame, 1 picture, 2 mark.
        Queue.ItemTemplate = (DataTemplate)XamlReader.Load(
            "<DataTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'>" +
            "<Grid Width='120' Height='68'><Border Background='#2B2B2B' CornerRadius='6' />" +
            "<Image Stretch='UniformToFill' /><Border Width='18' Height='18' CornerRadius='9' HorizontalAlignment='Right' VerticalAlignment='Top' Margin='4' Visibility='Collapsed'>" +
            "<TextBlock HorizontalAlignment='Center' VerticalAlignment='Center' FontSize='11' FontWeight='Bold' /></Border></Grid></DataTemplate>");
        Queue.ItemContainerStyle = (Style)XamlReader.Load(
            "<Style xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' TargetType='ListViewItem'>" +
            "<Setter Property='MinHeight' Value='0' /><Setter Property='MinWidth' Value='0' /><Setter Property='Padding' Value='4' /></Style>");
        Queue.ContainerContentChanging += (_, args) =>
        {
            if (args.ItemContainer.ContentTemplateRoot is Grid row && args.Item is TriageItem item)
            {
                RenderRow(row, item);
            }
        };
        BuildOrderMenu();
        Closed += OnClosed;
        AppWindow.Closing += OnAppWindowClosing;
    }

    // The list order (spec 10.1): a menu of radio items on a button; the window keeps the arrow keys for itself, so a combo box would be unusable.
    private void BuildOrderMenu()
    {
        var menu = new MenuFlyout();
        foreach (var order in TriageListOrdering.All)
        {
            var item = new RadioMenuFlyoutItem { Text = TriageListOrdering.Title(order), GroupName = "triage-order", Tag = order, IsChecked = order == TriageListOrder.AsOpened };
            item.Click += async (_, _) => await ChooseOrderAsync(order);
            menu.Items.Add(item);
        }

        OrderButton.Flyout = menu;
    }

    private async Task ChooseOrderAsync(TriageListOrder order)
    {
        if (_session is not { } session)
        {
            return;
        }

        await session.SortAsync(order);
        Root.Focus(FocusState.Programmatic); // the keys of the window carry on where they were
        try
        {
            await App.Host.Settings.UpdateAsync(s => s with { Media = s.Media with { TriageOrder = order } }, CancellationToken.None);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The order still applies to this window; it is only not remembered.
        }
    }

    // The list was put in another order: show it again, mark the order in the menu and on the button, keep the file on screen selected.
    private void OnOrderChanged()
    {
        if (_session is not { } session)
        {
            return;
        }

        Queue.ItemsSource = null;
        Queue.ItemsSource = session.Items;
        OrderButton.Content = "Order: " + TriageListOrdering.Title(session.Order) + " ▾";
        if (OrderButton.Flyout is MenuFlyout menu)
        {
            foreach (var radio in menu.Items.OfType<RadioMenuFlyoutItem>())
            {
                radio.IsChecked = radio.Tag is TriageListOrder o && o == session.Order;
            }
        }

        OnSessionChanged();
    }

    /// <summary>Raised when the window closes with a line to show in the folder window (what became of the kept files).</summary>
    public event EventHandler<string>? Notice;

    /// <summary>The folder kept files were (or would be) moved to, so the folder windows can show them once this one closes.</summary>
    public string? KeptFolder => Destination();

    // Where the kept files go: the other pane's folder, else the keep folder of the settings.
    private string? Destination() =>
        _destination() is { Length: > 0 } pane ? pane
        : App.Host.Settings.Current.Media.TriageKeepFolder is { Length: > 0 } keep ? keep : null;

    // Closing (Esc, the Close button, the X): when the session kept files and there is somewhere to put them, ask once, then close.
    private async void OnAppWindowClosing(Microsoft.UI.Windowing.AppWindow sender, Microsoft.UI.Windowing.AppWindowClosingEventArgs args)
    {
        if (_handOffDone)
        {
            return;
        }

        args.Cancel = true;
        _handOffDone = true;
        try
        {
            if (_recorder is { } recorder)
            {
                await recorder.Flushed;
                recorder.Dispose();
            }

            if (Inbox is { } inbox && _session is { } finished)
            {
                await App.Host.Inboxes.RecordAsync(inbox.Inbox, finished, CancellationToken.None);
            }
            else
            {
                await Task.Run(() => App.Host.TriageJournal.Compact(r => r.Scope.Length == 0 && !r.IsPendingMove));
            }

            await HandOffAsync();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            Notice?.Invoke(this, $"The kept files could not be moved: {ex.Message}");
        }
        finally
        {
            Close();
        }
    }

    private async Task HandOffAsync()
    {
        if (_session is not { } session || _actions is not { } actions)
        {
            return;
        }

        var media = App.Host.Settings.Current.Media;
        if (TriageHandOff.Plan(session, media, Destination()) is not { } plan)
        {
            return;
        }

        if (plan.Ask)
        {
            var always = new CheckBox { Content = "Always move them without asking" };
            var count = plan.Files.Count;
            var dialog = new ContentDialog
            {
                Title = count == 1 ? "Move the kept file?" : $"Move {count:N0} kept files?",
                Content = new StackPanel
                {
                    Spacing = 12,
                    Children =
                    {
                        new TextBlock { Text = $"To {plan.Folder}", TextWrapping = TextWrapping.Wrap },
                        always,
                    },
                },
                PrimaryButtonText = "Move",
                CloseButtonText = "Not now",
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = Root.XamlRoot,
            };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            {
                return;
            }

            if (always.IsChecked == true)
            {
                try
                {
                    await App.Host.Settings.UpdateAsync(s => s with { Media = s.Media with { TriageAskBeforeMove = false } }, CancellationToken.None);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // The choice still applies to this move; the file could not be written.
                }
            }
        }

        // The file on screen is still open in the player; a rename needs it released.
        if (_engine is { } engine)
        {
            await engine.CloseAsync(CancellationToken.None);
        }

        var result = await Task.Run(() => actions.MoveKeptAsync(plan.Files, plan.Folder, CancellationToken.None));
        Notice?.Invoke(this, result.Describe(plan.Folder));

        // Moved at once, or handed to the queue as a job (which is journaled by the worker): either way no longer waiting.
        await Task.Run(() => App.Host.TriageJournal.AppendMoved(TriageHandOff.Handled(plan, result, File.Exists)));
    }

    /// <summary>Raised when the window closes, with the file triage was on, so the folder view can select it again (spec §10.2, Esc).</summary>
    public event EventHandler<FileEntry?>? Exited;

    private async void OnVideoInitialized(object? sender, InitializedEventArgs e)
    {
        try
        {
            // The video surface hands over the options LibVLC needs to draw into it, so the instance is made only now.
            UiTrace.Mark("Triage: video surface ready");
            // Loading LibVLC and scanning its plugins takes seconds the first time (longer with a virus scanner watching), so it is done
            // on the pool: the window and the app stay responsive, and the spinner turns, while it starts (spec §3.2).
            var swapChainOptions = e.SwapChainOptions;
            var engine = await Task.Run(() => new LibVlcEngine(LibVlcEngine.CreateLibVlc(swapChainOptions), ownsLibVlc: true));
            UiTrace.Mark("Triage: LibVLC created");
            if (_closing.IsCancellationRequested)
            {
                await engine.DisposeAsync();
                return;
            }

            _engine = engine;
            Video.MediaPlayer = _engine.Player;
            LoadingText.Text = "Opening the first video…";
            var settings = App.Host.Settings.Current.Media;
            _actions = App.Host.TriageActionsFor(_destination);
            await ApplyVolumeAsync(settings);
            var session = _session = new TriageSession(_queue, _engine, _actions, settings.ToTriageOptions(), null, FileGoneAsync);
            // Every decision goes to the journal as it is made (spec §10.1, §3.4), so a crash loses none of them; a kept file that is to
            // be moved when triage ends is remembered with its folder, and offered again on the next start if the move never happened.
            _recorder = new Leviathan.ViewModels.Triage.TriageRecorder(
                session,
                App.Host.TriageJournal,
                Inbox?.Inbox.Id ?? string.Empty,
                () => App.Host.Settings.Current.Media.TriageMoveWhen == Leviathan.ViewModels.Settings.TriageMoveMode.AtEnd ? Destination() : null,
                App.Host.TriageHistory);
            _dispatcher = new TriageDispatcher(session, App.Host.TriageKeys);
            _dispatcher.WindowCommand += OnWindowCommand;
            session.DecisionChanged += OnDecisionChanged;
            session.PropertyChanged += (_, _) => DispatcherQueue.TryEnqueue(OnSessionChanged);
            session.Finished += (_, _) => DispatcherQueue.TryEnqueue(OnSessionChanged);
            session.OrderChanged += (_, _) => DispatcherQueue.TryEnqueue(OnOrderChanged);
            Queue.ItemsSource = session.Items;
            _timer.Start();
            Root.Focus(FocusState.Programmatic);
            UiTrace.Mark("Triage: opening the first file");
            await session.StartAsync(_startIndex, _closing.Token);
            UiTrace.Mark("Triage: first file playing");
            Loading.Visibility = Visibility.Collapsed;

            // The order used last time (not for an inbox, which has its own: oldest, newest, largest or random).
            if (Inbox is null && settings.TriageOrder != TriageListOrder.AsOpened)
            {
                await session.SortAsync(settings.TriageOrder);
            }

            OnSessionChanged();
        }
        catch (OperationCanceledException)
        {
            // The window was closed while it was starting.
        }
        catch (Exception ex) when (ex is VLCException or DllNotFoundException or BadImageFormatException)
        {
            // The video surface is drawn over everything and is white until LibVLC draws: hide it, or the message cannot be seen.
            Video.Visibility = Visibility.Collapsed;
            Loading.Visibility = Visibility.Collapsed;
            NameText.Text = "The video engine could not start";
            Message.Text = $"The video engine could not start: {ex.Message}";
            Message.Visibility = Visibility.Visible;
        }
    }

    // Everything that changes with the session (the file, the counts, the error): the window is redrawn from it, never kept in step by hand.
    private void OnSessionChanged()
    {
        if (_session is not { } session)
        {
            return;
        }

        ProgressText.Text = Inbox is { } inbox
            ? InboxSession.Progress(session.Items.Count(i => i.Decision != TriageDecision.None), session.Items.Count, InboxCatalog.TriagedCount(inbox.Inbox, session), inbox.Total)
              + $" · {session.Kept:N0} kept · {session.Discarded:N0} discarded"
            : session.ProgressText;
        ReclaimedText.Text = session.ReclaimedText;
        ShowDestination();
        Bar.Value = session.Items.Count == 0 ? 0 : (double)(session.IsFinished ? session.Items.Count : Math.Max(0, session.Index)) / session.Items.Count;
        ErrorText.Text = session.LastError ?? string.Empty;
        ErrorBox.Visibility = session.LastError is null ? Visibility.Collapsed : Visibility.Visible;
        if (session.Current is { } item)
        {
            _last = item.File;
            NameText.Text = item.File.Name;
            ShowCurrent(item);
        }
        else if (session.IsFinished)
        {
            NameText.Text = "Queue finished";
            ShowSummary(session);
        }

        Summary.Visibility = session.IsFinished ? Visibility.Visible : Visibility.Collapsed;
        RefreshQueue();
    }

    // Where Keep puts files, always on screen: the folder, or a warning in amber when there is none and Keep only tags.
    private void ShowDestination()
    {
        var destination = Destination();
        var moveOnKeep = App.Host.Settings.Current.Media.TriageMoveWhen == Leviathan.ViewModels.Settings.TriageMoveMode.OnKeep;
        var line = Leviathan.ViewModels.Triage.TriageNotes.DestinationLine(destination, moveOnKeep);
        if (DestinationText.Text == line)
        {
            return;
        }

        // The other pane can be moved to another folder while triage runs, so this is looked at again with every refresh.
        DestinationText.Text = line;
        DestinationText.Foreground = Leviathan.ViewModels.Triage.TriageNotes.IsWarning(destination)
            ? new SolidColorBrush(Windows.UI.Color.FromArgb(255, 255, 208, 96))
            : new SolidColorBrush(Windows.UI.Color.FromArgb(255, 201, 201, 201));
    }

    /// <summary>Raised after a kept or discarded file moved, so the folder views can show the change at once, not only when this window closes.</summary>
    public event EventHandler? FilesMoved;

    // Every decision is written to the Activity tab with where the file went (spec §16.1), and the folder views are told to reload.
    private void OnDecisionChanged(object? sender, TriageItem item)
    {
        var name = item.File.Name;
        var from = item.Original.Location.Parent?.ToWin32Path() ?? string.Empty;
        var now = item.File.Location.Parent?.ToWin32Path() ?? from;
        var moveOnKeep = App.Host.Settings.Current.Media.TriageMoveWhen == Leviathan.ViewModels.Settings.TriageMoveMode.OnKeep;
        var note = item.Decision switch
        {
            TriageDecision.Kept => Leviathan.ViewModels.Triage.TriageNotes.Kept(name, from, now, Destination(), moveOnKeep),
            TriageDecision.Discarded => Leviathan.ViewModels.Triage.TriageNotes.Discarded(name, from),
            TriageDecision.None => Leviathan.ViewModels.Triage.TriageNotes.TakenBack(item.Original.Name, from),
            _ => null,
        };
        if (note is not null)
        {
            App.Host.Activity.Note(note, Leviathan.ViewModels.Activity.ActivityKind.Files, item.File.Location.Value);
            DispatcherQueue.TryEnqueue(() => FilesMoved?.Invoke(this, EventArgs.Empty));
        }
    }

    private void ShowCurrent(TriageItem item)
    {
        var file = item.File;
        if (_infoFor != file.Location.Value)
        {
            _info = null;
            _infoFor = file.Location.Value;
            _shownDuration = TimeSpan.Zero;
            _ = LoadInfoAsync(file);
            PrefetchAhead();
            ShowOverlay();
        }

        var text = TriageOverlayText.Describe(file, _engine?.Snapshot.Duration, _info);
        OverlayTitle.Text = text.Title;
        OverlayDetail.Text = text.Detail;
        OverlayState.Text = item.Decision switch
        {
            TriageDecision.Kept => "Kept",
            TriageDecision.Discarded => "Discarded (in the bin): Ctrl+Z or the list below restores it",
            TriageDecision.Skipped => "Skipped",
            _ => string.Empty,
        };
        OverlayState.Visibility = OverlayState.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        if (item.Decision != TriageDecision.None)
        {
            ShowOverlay();
        }

        Message.Visibility = item.IsPlayable ? Visibility.Collapsed : Visibility.Visible;
        Message.Text = item.IsPlayable ? string.Empty : $"{file.Name}\nwas discarded. Press Ctrl+Z to bring it back.";
        _syncingQueue = true;
        Queue.SelectedIndex = _session!.Index;
        if (_session.Index >= 0)
        {
            Queue.ScrollIntoView(item);
        }

        _syncingQueue = false;
    }

    private async Task LoadInfoAsync(FileEntry file)
    {
        try
        {
            var source = new MediaInfoSource(() => App.Host.MediaHost);
            var result = await source.ProbeAsync(new MediaFileRef(file.Location.ToWin32Path(), file.Size, file.Modified.UtcDateTime.Ticks, file.Location.IsNetwork), _closing.Token);
            if (result.Info is { } info && _infoFor == file.Location.Value)
            {
                _info = info;
                DispatcherQueue.TryEnqueue(OnSessionChanged);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or MediaInfoUnavailableException or MediaInfoTransientException)
        {
            // The overlay keeps what it knows without the resolution and codec.
        }
    }

    // The next two files of a share have their header and index read while this one plays, so opening them is quick (spec §10.1).
    private void PrefetchAhead()
    {
        foreach (var next in _session!.Upcoming(2).Where(f => f.Location.IsNetwork))
        {
            var path = next.Location.ToWin32Path();
            if (_prefetched.Add(path))
            {
                _ = Task.Run(() => MediaPrefetcher.PrefetchAsync(path, _closing.Token));
            }
        }
    }

    // The overlay (title, resolution, codec, state) stays on screen for as long as the file is on screen; only the overlay key hides it.
    private void ShowOverlay() => Overlay.Opacity = _overlayHidden ? 0 : 1;

    private static void RenderRow(Grid row, TriageItem item)
    {
        var picture = (Image)row.Children[1];
        var mark = (Border)row.Children[2];
        var glyph = (TextBlock)mark.Child;

        // Kept and discarded files carry a round mark; a discarded one is dimmed. Undecided files are plain.
        mark.Visibility = item.Decision is TriageDecision.Kept or TriageDecision.Discarded or TriageDecision.Skipped ? Visibility.Visible : Visibility.Collapsed;
        glyph.Text = item.Decision switch
        {
            TriageDecision.Kept => "✓",
            TriageDecision.Discarded => "✕",
            _ => "↷",
        };
        (mark.Background, glyph.Foreground) = item.Decision switch
        {
            TriageDecision.Kept => ((Brush)new SolidColorBrush(Windows.UI.Color.FromArgb(255, 76, 209, 138)), (Brush)new SolidColorBrush(Windows.UI.Color.FromArgb(255, 6, 33, 15))),
            TriageDecision.Discarded => (new SolidColorBrush(Windows.UI.Color.FromArgb(255, 255, 143, 148)), new SolidColorBrush(Windows.UI.Color.FromArgb(255, 58, 15, 18))),
            _ => (new SolidColorBrush(Windows.UI.Color.FromArgb(200, 90, 90, 90)), new SolidColorBrush(Windows.UI.Color.FromArgb(255, 235, 235, 235))),
        };
        row.Opacity = item.Decision == TriageDecision.Discarded ? 0.45 : 1;
        ToolTipService.SetToolTip(row, item.File.Name);
        AutomationProperties.SetName(row, item.File.Name);

        // The thumbnail is fetched once per file, whatever the decision; a recycled cell for another file starts over.
        if (!ReferenceEquals(picture.Tag, item))
        {
            picture.Tag = item;
            picture.Source = null;
            _ = LoadThumbnailAsync(picture, item);
        }
    }

    private static async Task LoadThumbnailAsync(Image target, TriageItem item)
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            var file = item.File;
            var image = await App.Host.Thumbnails.GetAsync(new ThumbnailRequest(file.Location, file.Modified, file.Size, 160, ThumbnailMode.Thumbnail), timeout.Token);
            if (image is null || !ReferenceEquals(target.Tag, item))
            {
                return;
            }

            var bitmap = new Microsoft.UI.Xaml.Media.Imaging.WriteableBitmap(image.Width, image.Height);
            using (var pixels = bitmap.PixelBuffer.AsStream())
            {
                pixels.Write(image.Bgra.Span);
            }

            target.Source = bitmap;
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or OperationCanceledException or System.Runtime.InteropServices.COMException)
        {
            // No picture for this file: the grey frame and the tooltip still say which one it is.
        }
    }

    // The filmstrip starts collapsed so the picture has the room (spec 10.1); Q or the button at the right shows it.
    private void SetStrip(bool visible)
    {
        Strip.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        StripToggle.IsChecked = visible;
        if (visible && _session?.Current is { } current)
        {
            Queue.ScrollIntoView(current);
        }
    }

    private void OnStripToggleClick(object sender, RoutedEventArgs e) => SetStrip(StripToggle.IsChecked == true);

    // Only the rows on screen exist, so a decision redraws just those: the list can hold thousands of files.
    private void RefreshQueue()
    {
        if (Queue.ItemsPanelRoot is null)
        {
            return;
        }

        foreach (var child in Queue.ItemsPanelRoot.Children.OfType<ListViewItem>())
        {
            if (child.ContentTemplateRoot is Grid row && child.Content is TriageItem item)
            {
                RenderRow(row, item);
            }
        }
    }

    private void ShowSummary(TriageSession session)
    {
        var s = session.Summary();
        SummaryText.Text = $"{s.Kept:N0} kept · {s.Discarded:N0} discarded · {s.Skipped:N0} skipped · {s.Undecided:N0} not reached\n{session.ReclaimedText}";
        DiscardedList.ItemsSource = session.Items.Where(i => i.Decision == TriageDecision.Discarded).ToList();
        DiscardedList.DisplayMemberPath = "File.Name";
        RestoreButton.IsEnabled = s.Discarded > 0;
    }

    private void Refresh()
    {
        ShowDestination();
        if (_engine is not { } engine)
        {
            return;
        }

        var s = engine.Snapshot;
        PlayIcon.Glyph = s.State == PlaybackState.Playing ? "" : "";
        TimeText.Text = $"{MediaColumns.Duration(s.Position)} / {MediaColumns.Duration(s.Duration)}";
        SpeedText.Text = $"{s.Speed:0.##}×";
        if (s.Duration > TimeSpan.Zero)
        {
            // Setting the bar from the player must not look like the user dragging it.
            _updating = true;
            Seek.Value = Math.Clamp(s.Position / s.Duration, 0, 1);
            _updating = false;
            if (_shownDuration != s.Duration && _session?.Current is { } item)
            {
                _shownDuration = s.Duration;
                var text = TriageOverlayText.Describe(item.File, s.Duration, _info);
                OverlayDetail.Text = text.Detail;
            }
        }
    }

    // Keys ----------------------------------------------------------------------------------------------------------

    private async void OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (_dispatcher is not { } dispatcher || KeyNames.IsModifier((int)e.Key))
        {
            return;
        }

        var ctrl = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control).HasFlag(CoreVirtualKeyStates.Down);
        var shift = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift).HasFlag(CoreVirtualKeyStates.Down);
        var alt = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Menu).HasFlag(CoreVirtualKeyStates.Down);
        // Q shows or hides the filmstrip (the summary's own controls keep their keys).
        if (e.Key == VirtualKey.Q && !ctrl && !shift && !alt && Summary.Visibility != Visibility.Visible)
        {
            e.Handled = true;
            SetStrip(Strip.Visibility != Visibility.Visible);
            return;
        }

        if (KeyNames.GestureFor((int)e.Key, ctrl, shift, alt) is not { } gesture || dispatcher.Resolve(gesture) is null)
        {
            return;
        }

        // The summary's own buttons and list keep Enter, Space and the arrows for themselves.
        if (Summary.Visibility == Visibility.Visible && e.OriginalSource is Button or ListViewItem)
        {
            return;
        }

        e.Handled = true;
        try
        {
            await dispatcher.HandleAsync(gesture);
        }
        catch (OperationCanceledException)
        {
            // The window is closing.
        }

        Refresh();
    }

    private void OnWindowCommand(object? sender, CommandId command)
    {
        if (command == TriageCommands.Exit)
        {
            DispatcherQueue.TryEnqueue(Close);
        }
        else if (command == TriageCommands.ToggleOverlay)
        {
            _overlayHidden = !_overlayHidden;
            ShowOverlay();
        }
        else if (command == TriageCommands.ContactSheet && _session?.Current is { IsPlayable: true } item)
        {
            new ContactSheetWindow(item.File, App.Host.SheetClient).Activate();
        }
        else if (command == TriageCommands.ToggleSkim && _session is not null)
        {
            var media = App.Host.Settings.Current.Media;
            _ = _session.Keys.ToggleBurstSkimAsync(media.SkimBurst, media.SkimInterval);
        }
        else if (command == TriageCommands.MoveToPreset)
        {
            _ = MoveToPresetAsync();
        }
        else
        {
            ErrorText.Text = "Renaming here is not in this build yet.";
            ErrorBox.Visibility = Visibility.Visible;
        }
    }

    // M: choose one of the quick targets (1 to 9 on the keyboard, or click), then keep the file and move it there (spec §15.6).
    private async Task MoveToPresetAsync()
    {
        if (_session is not { Current: not null } session)
        {
            return;
        }

        var slots = App.Host.QuickTargets.Slots;
        var set = Enumerable.Range(1, Leviathan.ViewModels.Commands.QuickTargets.Count).Where(n => Leviathan.ViewModels.Commands.QuickTargets.FolderOf(slots, n) is not null).ToList();
        if (set.Count == 0)
        {
            ErrorText.Text = "No quick targets are set. In a folder window, run \"Make this folder quick target 1\" from the command palette (Ctrl+Shift+P).";
            ErrorBox.Visibility = Visibility.Visible;
            return;
        }

        var list = new ListView { SelectionMode = ListViewSelectionMode.Single, IsItemClickEnabled = true };
        foreach (var n in set)
        {
            list.Items.Add(new TextBlock { Text = $"{n}   {slots[n - 1]}", Tag = n });
        }

        var dialog = new ContentDialog { Title = "Move to a quick target", Content = list, CloseButtonText = "Cancel", XamlRoot = Root.XamlRoot };
        var chosen = 0;
        list.ItemClick += (_, e) =>
        {
            chosen = (int)((TextBlock)e.ClickedItem).Tag;
            dialog.Hide();
        };
        dialog.KeyDown += (_, e) =>
        {
            var digit = e.Key - Windows.System.VirtualKey.Number0;
            if (digit is >= 1 and <= 9 && set.Contains(digit))
            {
                chosen = digit;
                e.Handled = true;
                dialog.Hide();
            }
        };
        await dialog.ShowAsync();
        if (chosen > 0)
        {
            await session.KeepToAsync(slots[chosen - 1]);
            Refresh();
        }

        Root.Focus(FocusState.Programmatic);
    }

    // Buttons do what their keys do, through the same dispatcher.
    private Task RunAsync(CommandId command) => _dispatcher is { } d ? d.RunAsync(command) : Task.CompletedTask;

    private async void OnPreviousClick(object sender, RoutedEventArgs e) => await ClickAsync(TriageCommands.Previous);

    private async void OnKeepClick(object sender, RoutedEventArgs e) => await ClickAsync(TriageCommands.Keep);

    private async void OnDiscardClick(object sender, RoutedEventArgs e) => await ClickAsync(TriageCommands.Discard);

    private async void OnSkipClick(object sender, RoutedEventArgs e) => await ClickAsync(TriageCommands.Skip);

    private async void OnUndoClick(object sender, RoutedEventArgs e) => await ClickAsync(TriageCommands.Undo);

    private async void OnPlayClick(object sender, RoutedEventArgs e) => await ClickAsync(TriageCommands.PlayPause);

    private async Task ClickAsync(CommandId command)
    {
        await RunAsync(command);
        Refresh();
        Root.Focus(FocusState.Programmatic); // the keys must keep working after a click
    }

    private async void OnSeekChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (_updating || _engine is not { Snapshot.Duration.Ticks: > 0 } engine)
        {
            return;
        }

        try
        {
            await engine.SeekAsync(engine.Snapshot.Duration * e.NewValue, _closing.Token);
        }
        catch (OperationCanceledException)
        {
            // The window is closing.
        }
    }

    private async void OnQueueSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingQueue || _session is not { } session || Queue.SelectedIndex < 0)
        {
            return;
        }

        await session.GoToAsync(Queue.SelectedIndex);
        Root.Focus(FocusState.Programmatic);
    }

    private async void OnRestoreClick(object sender, RoutedEventArgs e)
    {
        if (_session is not { } session)
        {
            return;
        }

        foreach (var item in DiscardedList.SelectedItems.OfType<TriageItem>().ToList())
        {
            await session.RestoreAsync(item);
        }

        OnSessionChanged();
    }

    private async void OnBackToQueueClick(object sender, RoutedEventArgs e)
    {
        if (_session is { } session)
        {
            await session.PreviousAsync();
            Root.Focus(FocusState.Programmatic);
        }
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    // ---- Volume (spec §10.1): a slider and a mute button, remembered for the next session.

    private double _volume = 100;
    private bool _muted;

    private async Task ApplyVolumeAsync(Leviathan.ViewModels.Settings.MediaSettings media)
    {
        _volume = media.TriageVolumePercent;
        _muted = media.TriageMuted;
        _updating = true;
        try
        {
            VolumeSlider.Value = _volume;
            ShowMute();
        }
        finally
        {
            _updating = false;
        }

        if (_engine is { } engine)
        {
            await engine.SetVolumeAsync(_volume / 100.0, CancellationToken.None);
            await engine.SetMutedAsync(_muted, CancellationToken.None);
        }
    }

    private void ShowMute() => MuteGlyph.Glyph = _muted || _volume <= 0 ? "\uE74F" : "\uE767";

    private async void OnVolumeChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (_updating || _engine is not { } engine)
        {
            return;
        }

        _volume = e.NewValue;
        if (_muted && _volume > 0)
        {
            _muted = false; // moving the slider up is asking to hear it
            await engine.SetMutedAsync(false, CancellationToken.None);
        }

        ShowMute();
        await engine.SetVolumeAsync(_volume / 100.0, CancellationToken.None);
    }

    private async void OnMuteClick(object sender, RoutedEventArgs e)
    {
        await ToggleMuteAsync();
        Root.Focus(FocusState.Programmatic); // the keys must keep working after a click
    }

    private async Task ToggleMuteAsync()
    {
        if (_engine is not { } engine)
        {
            return;
        }

        _muted = !_muted;
        ShowMute();
        await engine.SetMutedAsync(_muted, CancellationToken.None);
    }

    private async Task SaveVolumeAsync()
    {
        var percent = (int)Math.Round(_volume);
        var muted = _muted;
        try
        {
            await App.Host.Settings.UpdateAsync(s => s with { Media = s.Media with { TriageVolumePercent = percent, TriageMuted = muted } }, CancellationToken.None);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The volume is only a convenience; the settings file could not be written.
        }
    }

    // A file deleted or moved away while triage runs is skipped, never waited for (spec §10.1). Only a definite answer counts: when the
    // folder itself cannot be reached (a share that is off) nothing is called missing, and the player gets to try.
    private static async Task<bool> FileGoneAsync(FileEntry file, CancellationToken cancellationToken)
    {
        var path = file.Location.ToWin32Path();
        var check = Task.Run(() => !System.IO.File.Exists(path) && System.IO.Directory.Exists(System.IO.Path.GetDirectoryName(path)), CancellationToken.None);
        var finished = await Task.WhenAny(check, Task.Delay(TimeSpan.FromSeconds(3), cancellationToken));
        return finished == check && await check;
    }

    private async void OnClosed(object sender, WindowEventArgs args)
    {
        _timer.Stop();
        _closing.Cancel();
        await SaveVolumeAsync();
        Exited?.Invoke(this, _session?.Current?.File ?? _last);
        if (_session is { } session)
        {
            _session = null;
            await session.DisposeAsync();
        }

        if (_engine is { } engine)
        {
            _engine = null;
            Video.MediaPlayer = null;
            await engine.DisposeAsync();
        }

        _closing.Dispose();
    }
}
