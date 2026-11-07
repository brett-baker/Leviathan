using Leviathan.Core.Platform;
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
    }

    /// <summary>The player the bar controls.</summary>
    public void Attach(PreviewPlayer player) => _player = player;

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
