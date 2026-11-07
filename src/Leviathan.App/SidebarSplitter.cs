using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.System;

namespace Leviathan.App;

/// <summary>
/// The splitter between the sidebar and the file view (spec §4) and, with <see cref="GrowsLeft"/>, the one on the left edge of the
/// preview pane (spec §9): drag to resize, double-click to collapse or restore, Left and Right (with Home to restore the default
/// width) when it has the keyboard. It only reports what the user did; the window decides the width, applies the limits of
/// <c>SidebarLayout</c> or <c>PreviewLayout</c> and remembers it.
/// </summary>
internal sealed partial class SidebarSplitter : Grid
{
    private readonly Rectangle _line = new() { Width = 1, HorizontalAlignment = HorizontalAlignment.Center, Fill = new SolidColorBrush(Microsoft.UI.Colors.Transparent) };
    private double _startX;
    private double _startWidth;
    private bool _dragging;

    public SidebarSplitter()
    {
        Width = 7;
        Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        IsTabStop = true;
        UseSystemFocusVisuals = true;
        Children.Add(_line);
        ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.SizeWestEast);
        AutomationProperties.SetName(this, "Resize sidebar");
        ToolTipService.SetToolTip(this, "Drag to resize the sidebar. Double-click to hide or show it.");
        PointerEntered += (_, _) => Highlight(true);
        PointerExited += (_, _) => Highlight(_dragging);
        PointerPressed += OnPointerPressed;
        PointerMoved += OnPointerMoved;
        PointerReleased += OnPointerReleased;
        PointerCaptureLost += OnPointerCaptureLost;
        DoubleTapped += OnDoubleTapped;
        KeyDown += OnKeyDown;
        GotFocus += (_, _) => Highlight(true);
        LostFocus += (_, _) => Highlight(false);
    }

    /// <summary>True for a pane on the right of the splitter: dragging left makes it wider.</summary>
    internal bool GrowsLeft { get; set; }

    /// <summary>The width the pane has right now, read when a drag starts.</summary>
    internal Func<double> CurrentWidth { get; set; } = () => 0;

    /// <summary>The pointer moved: the width the sidebar should have, before the limits.</summary>
    internal event EventHandler<double>? WidthRequested;

    /// <summary>The user asked to collapse or restore the sidebar (double-click, Enter).</summary>
    internal event EventHandler? ToggleRequested;

    /// <summary>The user asked for the default width (Home).</summary>
    internal event EventHandler? ResetRequested;

    private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }

        Focus(FocusState.Pointer);
        _dragging = true;
        _startX = e.GetCurrentPoint(null).Position.X;
        _startWidth = CurrentWidth();
        CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_dragging)
        {
            var delta = e.GetCurrentPoint(null).Position.X - _startX;
            WidthRequested?.Invoke(this, _startWidth + (GrowsLeft ? -delta : delta));
            e.Handled = true;
        }
    }

    private void OnPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        EndDrag(e.Pointer);
    }

    private void OnPointerCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        EndDrag(null);
    }

    private void OnDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        e.Handled = true;
        ToggleRequested?.Invoke(this, EventArgs.Empty);
    }

    private void OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
        switch (e.Key)
        {
            case VirtualKey.Left:
                e.Handled = true;
                WidthRequested?.Invoke(this, CurrentWidth() + (GrowsLeft ? 1 : -1) * ViewModels.Windows.SidebarLayout.KeyStep);
                break;
            case VirtualKey.Right:
                e.Handled = true;
                WidthRequested?.Invoke(this, CurrentWidth() + (GrowsLeft ? -1 : 1) * ViewModels.Windows.SidebarLayout.KeyStep);
                break;
            case VirtualKey.Home:
                e.Handled = true;
                ResetRequested?.Invoke(this, EventArgs.Empty);
                break;
            case VirtualKey.Enter or VirtualKey.Space:
                e.Handled = true;
                ToggleRequested?.Invoke(this, EventArgs.Empty);
                break;
        }
    }

    private void EndDrag(Pointer? pointer)
    {
        if (!_dragging)
        {
            return;
        }

        _dragging = false;
        if (pointer is not null)
        {
            ReleasePointerCapture(pointer);
        }

        Highlight(false);
    }

    private void Highlight(bool on) =>
        _line.Fill = on
            ? (Brush)Application.Current.Resources["AccentFillColorDefaultBrush"]
            : new SolidColorBrush(Microsoft.UI.Colors.Transparent);
}
