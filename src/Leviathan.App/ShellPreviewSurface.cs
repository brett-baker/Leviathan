using System.Runtime.InteropServices;
using Leviathan.Contracts;
using Leviathan.ViewModels.Preview;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Leviathan.App;

/// <summary>
/// The place in the pane where a shell preview handler's window floats (spec §9.1: Office documents, "hosted out of process in
/// ShellHost"). The handler's code and its window live in <c>Leviathan.ShellHost</c>, as a window owned by this one and kept over this
/// control's rectangle: the window is not a child of ours, so the two processes never share an input queue and a handler that hangs
/// cannot make the UI hang. This control asks the host to open the preview, keeps the host's window over the control as the pane, the
/// splitter or the window move or resize, and closes it when the pane shows something else. If the host cannot be reached or the
/// file has no handler, the control shows why in plain words. The cost of floating is that anything drawn over the pane (a flyout, a
/// tooltip) is covered by it.
/// </summary>
internal sealed partial class ShellPreviewSurface : Grid
{
    private readonly TextBlock _message = new()
    {
        TextWrapping = TextWrapping.Wrap,
        TextAlignment = TextAlignment.Center,
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
        Margin = new Thickness(12),
        Visibility = Visibility.Collapsed,
    };

    private IShellHostService? _service;
    private nint _owner;
    private Guid _id;
    private int _generation;
    private ShellRect? _sent;
    private bool _moving;

    public ShellPreviewSurface()
    {
        Children.Add(_message);
        LayoutUpdated += (_, _) => Reposition();
        SizeChanged += (_, _) => Reposition();
    }

    /// <summary>Opens <paramref name="path"/> in its handler; throws <see cref="OperationCanceledException"/> when the selection moves on first.</summary>
    public async Task OpenAsync(string path, nint owner, CancellationToken cancellationToken)
    {
        Clear();
        var generation = _generation;
        _owner = owner;
        try
        {
            // The control was only just made visible, so it has no size until the layout runs.
            UpdateLayout();
            var bounds = ScreenBounds();
            if (bounds is null)
            {
                Fail("The preview has no room to show in.");
                return;
            }

            _service = await App.Host.ShellHost.GetServiceAsync(TimeSpan.FromSeconds(3), cancellationToken);
            var result = await _service.OpenPreviewAsync(new ShellPreviewRequest(path, owner, bounds), cancellationToken).WaitAsync(PreviewLimits.LoadTimeout * 2, cancellationToken);
            if (generation != _generation)
            {
                if (result.Id != Guid.Empty)
                {
                    _ = _service.ClosePreviewAsync(result.Id, CancellationToken.None);
                }

                throw new OperationCanceledException(cancellationToken);
            }

            if (result.Id == Guid.Empty)
            {
                Fail(result.Error ?? "No preview is available for this file.");
                return;
            }

            _id = result.Id;
            _sent = bounds;
            Reposition();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (TimeoutException)
        {
            Fail("The preview handler took too long, so it was stopped. Its details are below.");
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or StreamJsonRpc.RemoteInvocationException or StreamJsonRpc.ConnectionLostException or ObjectDisposedException)
        {
            Fail($"The preview host could not show this file: {ex.Message}");
        }
    }

    /// <summary>Closes the preview, if there is one, and empties the control.</summary>
    public void Clear()
    {
        _generation++;
        _message.Visibility = Visibility.Collapsed;
        var id = _id;
        _id = Guid.Empty;
        _sent = null;
        if (id != Guid.Empty && _service is { } service)
        {
            _ = CloseQuietlyAsync(service, id);
        }
    }

    /// <summary>Puts the host's window over this control again; called when anything that moves the control happens.</summary>
    public void Reposition()
    {
        if (_id == Guid.Empty || ScreenBounds() is not { } bounds || bounds == _sent)
        {
            return;
        }

        _sent = bounds;
        if (!_moving)
        {
            _ = MoveLoopAsync();
        }
    }

    // One move at a time, always to the latest place, so a drag of the splitter sends a few calls and never a queue of them.
    private async Task MoveLoopAsync()
    {
        _moving = true;
        try
        {
            ShellRect? last = null;
            while (_id != Guid.Empty && _service is { } service && _sent is { } wanted && wanted != last)
            {
                last = wanted;
                await service.MovePreviewAsync(_id, wanted, CancellationToken.None);
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or StreamJsonRpc.RemoteInvocationException or StreamJsonRpc.ConnectionLostException or ObjectDisposedException or TimeoutException)
        {
            // The host went away; the next selection opens a new preview.
        }
        finally
        {
            _moving = false;
        }
    }

    private static async Task CloseQuietlyAsync(IShellHostService service, Guid id)
    {
        try
        {
            await service.ClosePreviewAsync(id, CancellationToken.None);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or StreamJsonRpc.RemoteInvocationException or StreamJsonRpc.ConnectionLostException or ObjectDisposedException or TimeoutException)
        {
            // The host is gone, and its windows with it.
        }
    }

    private void Fail(string text)
    {
        _message.Text = text;
        _message.Visibility = Visibility.Visible;
    }

    // This control's rectangle in screen pixels: its place in the window's content, scaled to pixels, plus where the window's client area is on screen.
    private ShellRect? ScreenBounds()
    {
        if (_owner == 0 || XamlRoot is not { } root || ActualWidth < 1 || ActualHeight < 1 || Visibility != Visibility.Visible)
        {
            return null;
        }

        var scale = root.RasterizationScale;
        var origin = TransformToVisual(null).TransformPoint(new Windows.Foundation.Point(0, 0));
        var point = new NativePoint { X = (int)Math.Round(origin.X * scale), Y = (int)Math.Round(origin.Y * scale) };
        if (!ClientToScreen(_owner, ref point))
        {
            return null;
        }

        return new ShellRect(point.X, point.Y, (int)Math.Round(ActualWidth * scale), (int)Math.Round(ActualHeight * scale));
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ClientToScreen(nint window, ref NativePoint point);
}
