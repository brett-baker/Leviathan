using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;

namespace Leviathan.App;

/// <summary>
/// The edge of the status hub you drag to resize it (spec §16.1): a thin bar on the top edge (drag up to grow) or on the left edge
/// of a right dock (drag left to grow), also with the arrow keys. It only reports the size the pointer asks for; the model applies
/// the limits and remembers the result.
/// </summary>
internal sealed partial class HubGrip : Grid
{
    private const double KeyStep = 24;

    private double _start;
    private double _startSize;
    private bool _dragging;
    private bool _vertical;

    public HubGrip()
    {
        Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        IsTabStop = true;
        UseSystemFocusVisuals = true;
        PointerPressed += OnPointerPressed;
        PointerMoved += OnPointerMoved;
        PointerReleased += (_, e) => EndDrag(e.Pointer);
        PointerCaptureLost += (_, _) => EndDrag(null);
        KeyDown += OnKeyDown;
        ApplyShape(false);
    }

    /// <summary>True for the bar on the left edge of a right dock (resizes the width); false for the top edge (resizes the height).</summary>
    public bool Vertical
    {
        get => _vertical;
        set => ApplyShape(value);
    }

    /// <summary>The size of the hub now, read when a drag starts.</summary>
    internal Func<double> CurrentSize { get; set; } = () => 0;

    /// <summary>The pointer or key asks for this size (before the limits).</summary>
    internal event EventHandler<double>? SizeRequested;

    private void ApplyShape(bool vertical)
    {
        _vertical = vertical;
        Width = vertical ? 6 : double.NaN;
        Height = vertical ? double.NaN : 6;
        HorizontalAlignment = vertical ? HorizontalAlignment.Left : HorizontalAlignment.Stretch;
        VerticalAlignment = vertical ? VerticalAlignment.Stretch : VerticalAlignment.Top;
        ProtectedCursor = InputSystemCursor.Create(vertical ? InputSystemCursorShape.SizeWestEast : InputSystemCursorShape.SizeNorthSouth);
        AutomationProperties.SetName(this, vertical ? "Resize the status hub width" : "Resize the status hub height");
        ToolTipService.SetToolTip(this, "Drag to resize");
    }

    private double Position(PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(null).Position;
        return _vertical ? point.X : point.Y;
    }

    private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }

        Focus(FocusState.Pointer);
        _dragging = true;
        _start = Position(e);
        _startSize = CurrentSize();
        CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_dragging)
        {
            // Up and left make the hub bigger.
            SizeRequested?.Invoke(this, _startSize + (_start - Position(e)));
            e.Handled = true;
        }
    }

    private void EndDrag(Pointer? pointer)
    {
        if (_dragging)
        {
            _dragging = false;
            if (pointer is not null)
            {
                ReleasePointerCapture(pointer);
            }
        }
    }

    private void OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
        var grow = _vertical ? VirtualKey.Left : VirtualKey.Up;
        var shrink = _vertical ? VirtualKey.Right : VirtualKey.Down;
        if (e.Key == grow)
        {
            e.Handled = true;
            SizeRequested?.Invoke(this, CurrentSize() + KeyStep);
        }
        else if (e.Key == shrink)
        {
            e.Handled = true;
            SizeRequested?.Invoke(this, CurrentSize() - KeyStep);
        }
    }
}
