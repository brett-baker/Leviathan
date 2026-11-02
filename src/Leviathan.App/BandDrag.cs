using Leviathan.Core.Paths;
using Leviathan.ViewModels.Folders;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;

namespace Leviathan.App;

/// <summary>
/// Rubber-band selection for a scrolling item view (spec §8.4): press on empty space and drag. The band is kept in the
/// coordinates of the item area, so it keeps growing while the view scrolls under it, and the view only has to say which items
/// lie under a rectangle. Dragging past the edge scrolls. Ctrl at the start keeps the earlier selection.
/// </summary>
internal sealed class BandDrag
{
    private const double StartDistance = 4;
    private const double EdgeScrollStep = 28;

    private readonly ScrollViewer _scroller;
    private readonly FrameworkElement _items;
    private readonly Rectangle _band;
    private bool _vertical;
    private readonly Func<Rect, IEnumerable<int>> _under;
    private SelectionModel? _selection;
    private IReadOnlySet<LocationPath>? _before;
    private Point _start;
    private bool _pressed;
    private bool _active;

    /// <summary>Watches <paramref name="scroller"/>; <paramref name="under"/> lists the item indexes under a rectangle in the coordinates of <paramref name="items"/>.</summary>
    public BandDrag(ScrollViewer scroller, FrameworkElement items, Rectangle band, bool vertical, Func<Rect, IEnumerable<int>> under)
    {
        _scroller = scroller;
        _items = items;
        _band = band;
        _vertical = vertical;
        _under = under;
        // The scroll viewer may mark moves handled while it holds the pointer, so listen even then.
        scroller.AddHandler(UIElement.PointerMovedEvent, new PointerEventHandler(OnMoved), handledEventsToo: true);
        scroller.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler(OnReleased), handledEventsToo: true);
        scroller.PointerCaptureLost += (_, _) => Stop();
    }

    /// <summary>True while a band is being drawn; a click that ends it must not also count as a click on empty space.</summary>
    public bool IsActive => _active;

    /// <summary>True when the view scrolls up and down (Thumbnails); false when it scrolls sideways (List). Decides which edge scrolls.</summary>
    public bool Vertical
    {
        get => _vertical;
        set => _vertical = value;
    }

    /// <summary>
    /// A press on empty space of the item area. Returns false when the press is on a scroll bar (or outside the items) and so
    /// starts nothing.
    /// </summary>
    public bool Press(PointerRoutedEventArgs e, SelectionModel selection, bool additive)
    {
        var inView = e.GetCurrentPoint(_scroller).Position;
        if (inView.X >= _scroller.ViewportWidth || inView.Y >= _scroller.ViewportHeight)
        {
            return false;
        }

        _selection = selection;
        _before = additive ? selection.Snapshot() : null;
        _start = e.GetCurrentPoint(_items).Position;
        _pressed = true;
        _active = false;
        _scroller.CapturePointer(e.Pointer);
        return true;
    }

    private void OnMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_pressed || _selection is null)
        {
            return;
        }

        var now = e.GetCurrentPoint(_items).Position;
        var inView = e.GetCurrentPoint(_scroller).Position;
        if (!_active)
        {
            if (Math.Abs(now.X - _start.X) < StartDistance && Math.Abs(now.Y - _start.Y) < StartDistance)
            {
                return;
            }

            _active = true;
        }

        AutoScroll(inView);

        // Scrolling moved the items under the pointer: read the position again.
        now = e.GetCurrentPoint(_items).Position;
        var rect = new Rect(
            new Point(Math.Min(_start.X, now.X), Math.Min(_start.Y, now.Y)),
            new Point(Math.Max(_start.X, now.X), Math.Max(_start.Y, now.Y)));
        _selection.ApplyRubberBand(_under(rect), _before);
        Draw(inView);
    }

    private void AutoScroll(Point inView)
    {
        var (position, extent, offset) = _vertical
            ? (inView.Y, _scroller.ViewportHeight, _scroller.VerticalOffset)
            : (inView.X, _scroller.ViewportWidth, _scroller.HorizontalOffset);
        var delta = position < 0 ? -EdgeScrollStep : position > extent ? EdgeScrollStep : 0;
        if (delta != 0)
        {
            var target = Math.Max(0, offset + delta);
            _scroller.ChangeView(_vertical ? null : target, _vertical ? target : null, null, true);
        }
    }

    private void Draw(Point inView)
    {
        // The start corner is fixed to the items, so it follows them when the view scrolls.
        var corner = _items.TransformToVisual(_scroller).TransformPoint(_start);
        var left = Math.Max(0, Math.Min(corner.X, inView.X));
        var top = Math.Max(0, Math.Min(corner.Y, inView.Y));
        var right = Math.Min(_scroller.ViewportWidth, Math.Max(corner.X, inView.X));
        var bottom = Math.Min(_scroller.ViewportHeight, Math.Max(corner.Y, inView.Y));
        Canvas.SetLeft(_band, left);
        Canvas.SetTop(_band, top);
        _band.Width = Math.Max(0, right - left);
        _band.Height = Math.Max(0, bottom - top);
        _band.Visibility = Visibility.Visible;
    }

    private void OnReleased(object sender, PointerRoutedEventArgs e)
    {
        _scroller.ReleasePointerCapture(e.Pointer);
        Stop();
    }

    private void Stop()
    {
        _pressed = false;
        _band.Visibility = Visibility.Collapsed;

        // IsActive stays true until the click that ended the drag has passed, then drops.
        if (_active)
        {
            _scroller.DispatcherQueue.TryEnqueue(() => _active = false);
        }
    }
}
