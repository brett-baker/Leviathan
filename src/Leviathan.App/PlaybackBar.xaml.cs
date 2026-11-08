using Leviathan.Core.Model;
using Leviathan.Core.Platform;
using Leviathan.ViewModels.Preview;
using Leviathan.ViewModels.Folders;
using Leviathan.ViewModels.Playback;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;

namespace Leviathan.App;

/// <summary>
/// The controls under the preview video (spec §9): back 5 min, 1 min, 30 s, play/pause, forward 30 s, 1 min, 5 min and skim, with the
/// position, a seek bar and the speed. It only asks the <see cref="PreviewPlayer"/>'s key logic to do things, so a click and the matching
/// key always do the same; it reads the engine's state on a 250 ms tick while it is shown.
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001:Types that own disposable fields should be disposable",
    Justification = "The strip request is cancelled and dropped by SetMedia; a control cannot express that as IDisposable.")]
internal sealed partial class PlaybackBar : UserControl
{
    private readonly DispatcherQueueTimer _timer;
    private PreviewPlayer? _player;
    private bool _updating;

    public PlaybackBar()
    {
        InitializeComponent();
        _timer = DispatcherQueue.GetForCurrentThread().CreateTimer();
        _timer.Interval = TimeSpan.FromMilliseconds(250);
        _timer.Tick += (_, _) => Refresh();
        Seek.AddHandler(PointerMovedEvent, new Microsoft.UI.Xaml.Input.PointerEventHandler(OnSeekHover), handledEventsToo: true);
        Seek.AddHandler(PointerExitedEvent, new Microsoft.UI.Xaml.Input.PointerEventHandler((_, _) => Hover.IsOpen = false), handledEventsToo: true);
        Film.SizeChanged += (_, _) => BuildFilmstrip();
    }

    private ScrubStrip? _strip;
    private CancellationTokenSource? _stripLoad;

    /// <summary>The player the bar controls.</summary>
    public void Attach(PreviewPlayer player)
    {
        _player = player;
        player.WaveformClicked += OnWaveformClicked;
    }

    /// <summary>
    /// Tells the bar which file the player is opening, so it can fetch the strip of frames behind the filmstrip and the frame shown over
    /// the seek bar (spec §9.1). Only videos have one; for anything else the bar shows just a time when the pointer rests on it.
    /// </summary>
    public void SetMedia(FileEntry? file, bool isVideo)
    {
        _stripLoad?.Cancel();
        _stripLoad = null;
        _strip = null;
        Hover.IsOpen = false;
        BuildFilmstrip();
        if (file is null || !isVideo)
        {
            return;
        }

        var load = _stripLoad = new CancellationTokenSource();
        _ = LoadStripAsync(file, load.Token);
    }

    private async Task LoadStripAsync(FileEntry file, CancellationToken cancellationToken)
    {
        try
        {
            var strip = await App.Host.Scrub.GetAsync(file, 256, cancellationToken);
            if (!cancellationToken.IsCancellationRequested)
            {
                _strip = strip;
                BuildFilmstrip();
            }
        }
        catch (OperationCanceledException)
        {
            // The pane moved on to another file.
        }
    }

    // A few frames spread over the whole video at the strip's own proportions; clicking one plays from there.
    private void BuildFilmstrip()
    {
        Film.Children.Clear();
        Film.ColumnDefinitions.Clear();
        // The strip has no height until there is one to show; a collapsed element would never be measured, so it could never learn its width.
        if (_strip is not { } strip || Film.ActualWidth < 40)
        {
            Film.Height = _strip is null ? 0 : FilmHeight;
            return;
        }

        Film.Height = FilmHeight;
        var cellHeight = FilmHeight;
        var cellWidth = cellHeight * strip.CellWidth / strip.CellHeight;
        var fit = Math.Max(1, (int)((Film.ActualWidth + 2) / (cellWidth + 2)));
        var picks = ScrubPicker.Spread(strip.Frames, fit);
        for (var i = 0; i < picks.Count; i++)
        {
            Film.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var frame = picks[i];
            var cell = Cell(strip, frame, cellHeight);
            cell.HorizontalAlignment = HorizontalAlignment.Center;
            Microsoft.UI.Xaml.Controls.ToolTipService.SetToolTip(cell, MediaColumns.Duration(TimeSpan.FromSeconds(strip.Seconds[Math.Min(frame, strip.Seconds.Length - 1)])));
            cell.Tapped += (_, _) => Do(k => k.SeekToAsync(TimeSpan.FromSeconds(strip.Seconds[Math.Min(frame, strip.Seconds.Length - 1)])));
            SetColumn(cell, i);
            Film.Children.Add(cell);
        }

    }

    private const double FilmHeight = 44;

    private static void SetColumn(FrameworkElement element, int column) => Grid.SetColumn(element, column);

    // One frame cut out of the strip picture, scaled to the height: the strip is shown whole, moved so the frame sits in a clipped box.
    private static Canvas Cell(ScrubStrip strip, int index, double height)
    {
        var scale = height / strip.CellHeight;
        var width = strip.CellWidth * scale;
        var canvas = new Canvas { Width = width, Height = height, Clip = new Microsoft.UI.Xaml.Media.RectangleGeometry { Rect = new Windows.Foundation.Rect(0, 0, width, height) } };
        var image = new Image { Source = strip.Image, Stretch = Microsoft.UI.Xaml.Media.Stretch.Fill, Width = strip.CellWidth * strip.Frames * scale, Height = height };
        Canvas.SetLeft(image, -index * width);
        canvas.Children.Add(image);
        return canvas;
    }

    private void OnSeekHover(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        var duration = _player?.Engine?.Snapshot.Duration ?? TimeSpan.Zero;
        if (duration <= TimeSpan.Zero || Seek.ActualWidth <= 0)
        {
            Hover.IsOpen = false;
            return;
        }

        var x = e.GetCurrentPoint(Seek).Position.X;
        var time = ScrubPicker.TimeAt(ScrubPicker.FractionAt(x, Seek.ActualWidth, 8), duration);
        var card = new StackPanel { Spacing = 2, Padding = new Thickness(3), Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(230, 20, 20, 20)) };
        double width = 56;
        if (_strip is { } strip && ScrubPicker.NearestFrame(strip.Seconds, time.TotalSeconds) is var frame and >= 0)
        {
            var cell = Cell(strip, frame, 90);
            width = cell.Width;
            card.Children.Add(cell);
        }

        card.Children.Add(new TextBlock { Text = MediaColumns.Duration(time), FontSize = 11, HorizontalAlignment = HorizontalAlignment.Center, Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.White) });
        Hover.XamlRoot = XamlRoot;
        Hover.Child = card;
        Hover.HorizontalOffset = Math.Clamp(x - (width / 2), -Seek.Margin.Left, Math.Max(0, Seek.ActualWidth - width));
        Hover.VerticalOffset = -(card.Children.Count > 1 ? 124 : 30);
        Hover.IsOpen = true;
    }

    private void OnWaveformClicked(object? sender, double fraction)
    {
        var duration = _player?.Engine?.Snapshot.Duration ?? TimeSpan.Zero;
        if (duration > TimeSpan.Zero)
        {
            Do(k => k.SeekToAsync(duration * fraction));
        }
    }

    /// <summary>Starts following the player (the pane shows a media file).</summary>
    public void Start()
    {
        _timer.Start();
        Refresh();
    }

    /// <summary>Stops following the player.</summary>
    public void Stop() => _timer.Stop();

    private void Refresh()
    {
        var s = _player?.Engine?.Snapshot ?? PlaybackSnapshot.Closed;
        foreach (var button in Buttons.Children.OfType<Button>())
        {
            button.IsEnabled = s.IsOpen;
        }

        Seek.IsEnabled = s.IsOpen;
        PlayGlyph.Glyph = s.State == PlaybackState.Playing ? "" : "";
        TimeText.Text = $"{MediaColumns.Duration(s.Position)} / {MediaColumns.Duration(s.Duration)}";
        if (s.Duration > TimeSpan.Zero)
        {
            _player?.SetProgress(s.Position / s.Duration);
        }

        SpeedText.Text = $"{s.Speed:0.##}×";
        if (s.Duration > TimeSpan.Zero)
        {
            // Setting the bar from the player must not look like the user dragging it.
            _updating = true;
            Seek.Value = Math.Clamp(s.Position / s.Duration, 0, 1);
            _updating = false;
        }
    }

    private async void OnSeekChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_updating || _player?.Engine is not { Snapshot.Duration.Ticks: > 0 } engine)
        {
            return;
        }

        try
        {
            await engine.SeekAsync(engine.Snapshot.Duration * e.NewValue, CancellationToken.None);
        }
        catch (OperationCanceledException)
        {
            // Nothing to do: the file was closed.
        }
    }

    private void OnBack5(object sender, RoutedEventArgs e) => Do(k => k.JumpAsync(-TimeSpan.FromMinutes(5)));

    private void OnBack1(object sender, RoutedEventArgs e) => Do(k => k.JumpAsync(-TimeSpan.FromMinutes(1)));

    private void OnBack30(object sender, RoutedEventArgs e) => Do(k => k.JumpAsync(-TimeSpan.FromSeconds(30)));

    private void OnPlay(object sender, RoutedEventArgs e) => Do(k => k.TogglePlayAsync());

    private void OnFwd30(object sender, RoutedEventArgs e) => Do(k => k.JumpAsync(TimeSpan.FromSeconds(30)));

    private void OnFwd1(object sender, RoutedEventArgs e) => Do(k => k.JumpAsync(TimeSpan.FromMinutes(1)));

    private void OnFwd5(object sender, RoutedEventArgs e) => Do(k => k.JumpAsync(TimeSpan.FromMinutes(5)));

    private void OnSkim(object sender, RoutedEventArgs e) => Do(k => k.SkimForwardAsync());

    private async void Do(Func<PlaybackController, Task> action)
    {
        if (_player?.Keys is not { } keys)
        {
            return;
        }

        try
        {
            await action(keys);
        }
        catch (OperationCanceledException)
        {
            // The preview moved on.
        }

        Refresh();
    }
}
