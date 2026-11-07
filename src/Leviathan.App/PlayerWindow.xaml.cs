using Leviathan.Core.Platform;
using Leviathan.Playback;
using Leviathan.ViewModels.Folders;
using Leviathan.ViewModels.Playback;
using LibVLCSharp.Platforms.Windows;
using LibVLCSharp.Shared;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using VirtualKey = Windows.System.VirtualKey;
using Windows.UI.Core;

namespace Leviathan.App;

/// <summary>
/// A video in a window of its own, played by LibVLC (spec §10, D10): the picture, a play button, the time, a seek bar and the speed.
/// The keys are those of spec §10.2 (Space, jumps, Ctrl+1…9, Home/End, frame steps, [ ], J/K/L), worked out by
/// <see cref="PlaybackController"/> so every place that plays behaves the same. This is the first picture of the engine; the preview
/// pane and triage mode will reuse the same controller on their own surfaces. Nothing here touches the disk or the network on the UI
/// thread: every engine call is asynchronous.
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001:Types that own disposable fields should be disposable",
    Justification = "The cancellation source, timer and engine are released when the window closes (Closed), which a Window cannot express as IDisposable.")]
internal sealed partial class PlayerWindow : Window
{
    private readonly string _path;
    private readonly string _name;
    private readonly TimeSpan _start;
    private readonly CancellationTokenSource _closing = new();
    private readonly DispatcherQueueTimer _timer;
    private LibVlcEngine? _engine;
    private PlaybackController? _keys;
    private bool _updating;

    /// <summary>Opens a window that plays <paramref name="path"/> from <paramref name="start"/>.</summary>
    public PlayerWindow(string path, string name, TimeSpan start)
    {
        _path = path;
        _name = name;
        _start = start;
        InitializeComponent();
        Title = $"Play · {name}";
        AppWindow.Resize(new Windows.Graphics.SizeInt32(1100, 700));
        _timer = DispatcherQueue.GetForCurrentThread().CreateTimer();
        _timer.Interval = TimeSpan.FromMilliseconds(250);
        _timer.Tick += (_, _) => Refresh();
        Closed += OnClosed;
    }

    private async void OnVideoInitialized(object? sender, InitializedEventArgs e)
    {
        try
        {
            // The video surface hands over the options LibVLC needs to draw into it, so the instance is made only now.
            var libvlc = LibVlcEngine.CreateLibVlc(e.SwapChainOptions);
            _engine = new LibVlcEngine(libvlc, ownsLibVlc: true);
            _keys = new PlaybackController(_engine, _closing.Token);
            Video.MediaPlayer = _engine.Player;
            _timer.Start();
            Root.Focus(FocusState.Programmatic);
            await _engine.OpenAsync(_path, _start, autoplay: true, _closing.Token);
            Refresh();
        }
        catch (OperationCanceledException)
        {
            // The window was closed while it was opening.
        }
        catch (Exception ex) when (ex is VLCException or DllNotFoundException or BadImageFormatException)
        {
            Show($"The video engine could not start: {ex.Message}");
        }
    }

    private void Refresh()
    {
        if (_engine is not { } engine)
        {
            return;
        }

        var s = engine.Snapshot;
        if (s.State == PlaybackState.Failed)
        {
            Show($"{_name} could not be played.\n\n{s.Error}");
            return;
        }

        PlayButton.Content = s.State == PlaybackState.Playing ? "Pause" : "Play";
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

    private void Show(string text)
    {
        Message.Text = text;
        Message.Visibility = Visibility.Visible;
    }

    private async void OnSeekChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (_updating || _engine is not { Snapshot.Duration.Ticks: > 0 } engine)
        {
            return;
        }

        await engine.SeekAsync(engine.Snapshot.Duration * e.NewValue, _closing.Token);
    }

    private async void OnPlayClick(object sender, RoutedEventArgs e)
    {
        if (_keys is { } keys)
        {
            await keys.TogglePlayAsync();
            Refresh();
        }
    }

    private async void OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (_keys is not { } keys)
        {
            return;
        }

        var ctrl = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control).HasFlag(CoreVirtualKeyStates.Down);
        var shift = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift).HasFlag(CoreVirtualKeyStates.Down);
        var alt = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Menu).HasFlag(CoreVirtualKeyStates.Down);
        Task? work = e.Key switch
        {
            VirtualKey.Space => keys.TogglePlayAsync(),
            VirtualKey.Right => keys.JumpAsync(Jump(ctrl, shift, alt)),
            VirtualKey.Left => keys.JumpAsync(-Jump(ctrl, shift, alt)),
            VirtualKey.Home => keys.HomeAsync(),
            VirtualKey.End => keys.EndAsync(),
            (VirtualKey)190 => keys.StepFrameAsync(forward: true),
            (VirtualKey)188 => keys.StepFrameAsync(forward: false),
            (VirtualKey)221 => keys.SpeedUpAsync(),
            (VirtualKey)219 => keys.SlowDownAsync(),
            VirtualKey.J => keys.SkimBackAsync(),
            VirtualKey.K => keys.PauseAsync(),
            VirtualKey.L => keys.SkimForwardAsync(),
            >= VirtualKey.Number1 and <= VirtualKey.Number9 when ctrl => keys.JumpToTenthAsync(e.Key - VirtualKey.Number0),
            VirtualKey.Escape => CloseSoon(),
            _ => null,
        };
        if (work is null)
        {
            return;
        }

        e.Handled = true;
        try
        {
            await work;
        }
        catch (OperationCanceledException)
        {
            // The window is closing.
        }

        Refresh();
    }

    // Plain arrows jump 30 s, Shift 1 min, Ctrl 5 min and Alt 5 s (spec §10.2).
    private static TimeSpan Jump(bool ctrl, bool shift, bool alt) =>
        alt ? TimeSpan.FromSeconds(5) : ctrl ? TimeSpan.FromMinutes(5) : shift ? TimeSpan.FromMinutes(1) : TimeSpan.FromSeconds(30);

    private Task CloseSoon()
    {
        DispatcherQueue.TryEnqueue(Close);
        return Task.CompletedTask;
    }

    private async void OnClosed(object sender, WindowEventArgs args)
    {
        _timer.Stop();
        _closing.Cancel();
        if (_engine is { } engine)
        {
            _engine = null;
            Video.MediaPlayer = null;
            await engine.DisposeAsync();
        }

        _closing.Dispose();
    }
}
