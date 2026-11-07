using Leviathan.Core.Platform;
using Leviathan.Playback;
using Leviathan.ViewModels.Playback;
using LibVLCSharp.Platforms.Windows;
using LibVLCSharp.Shared;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using VirtualKey = Windows.System.VirtualKey;
using Windows.UI.Core;

namespace Leviathan.App;

/// <summary>
/// The picture of a video or the name of an audio file in the preview pane (spec §9.1), played by the same LibVLC engine as the player
/// window. It is made the first time a media file is selected and then kept, so the engine is already warm for the next one (spec §3.1:
/// "the engine is pre-warmed while the preview pane is open"); a file opens paused on its first frame, never with sound, because
/// arrowing through a folder must not play every file. Nothing here touches the disk or the network on the UI thread: every engine call
/// is asynchronous and takes the token of the preview request, so moving to another file cancels the open.
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001:Types that own disposable fields should be disposable",
    Justification = "The engine is released by ReleaseAsync when the window closes, which needs to await; a control cannot express that as IDisposable.")]
internal sealed partial class PreviewPlayer : Grid
{
    private readonly VideoView _video = new();
    private readonly TextBlock _label = new()
    {
        TextWrapping = TextWrapping.Wrap,
        TextAlignment = TextAlignment.Center,
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
        Margin = new Thickness(12),
        Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.White),
    };

    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private LibVlcEngine? _engine;
    private PlaybackController? _keys;

    public PreviewPlayer()
    {
        Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Black);
        IsTabStop = true;
        Children.Add(_video);
        Children.Add(_label);
        _video.Initialized += OnVideoInitialized;
        KeyDown += OnKeyDown;
    }

    /// <summary>The key logic over the engine; null until the engine has started.</summary>
    public PlaybackController? Keys => _keys;

    /// <summary>The engine; null until it has started.</summary>
    public IVideoEngine? Engine => _engine;

    /// <summary>Raised on the UI thread when the engine could not start (no LibVLC, a bad install).</summary>
    public event EventHandler<string>? Failed;

    private void OnVideoInitialized(object? sender, InitializedEventArgs e)
    {
        try
        {
            // The video surface hands over the options LibVLC needs to draw into it, so the instance is made only now.
            var libvlc = LibVlcEngine.CreateLibVlc(e.SwapChainOptions);
            _engine = new LibVlcEngine(libvlc, ownsLibVlc: true);
            _keys = new PlaybackController(_engine);
            _video.MediaPlayer = _engine.Player;
            _ready.TrySetResult();
        }
        catch (Exception ex) when (ex is VLCException or DllNotFoundException or BadImageFormatException)
        {
            _ready.TrySetException(ex);
            _label.Text = $"The video engine could not start: {ex.Message}";
            _label.Visibility = Visibility.Visible;
            Failed?.Invoke(this, ex.Message);
        }
    }

    /// <summary>Opens <paramref name="path"/> paused at the start. <paramref name="label"/> is shown over the picture when <paramref name="showLabel"/> (the name of an audio file).</summary>
    public async Task OpenAsync(string path, string label, bool showLabel, CancellationToken cancellationToken)
    {
        _label.Text = label;
        _label.Visibility = showLabel ? Visibility.Visible : Visibility.Collapsed;
        await _ready.Task.WaitAsync(cancellationToken);
        await _engine!.OpenAsync(path, TimeSpan.Zero, autoplay: false, cancellationToken);
    }

    /// <summary>Closes the file (the selection moved to something that is not media) and stops any sound.</summary>
    public async Task CloseFileAsync()
    {
        if (_engine is { } engine)
        {
            await engine.CloseAsync(CancellationToken.None);
        }
    }

    /// <summary>Releases the engine when the window closes.</summary>
    public async Task ReleaseAsync()
    {
        if (_engine is { } engine)
        {
            _engine = null;
            _video.MediaPlayer = null;
            await engine.DisposeAsync();
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
        if (PlaybackKeyMap.Handle(keys, e.Key, ctrl, shift, alt) is not { } work)
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
            // The preview moved on.
        }
    }
}
