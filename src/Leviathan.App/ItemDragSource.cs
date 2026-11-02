using Leviathan.Core.Model;
using Leviathan.ViewModels.Folders;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Windows.Foundation;

namespace Leviathan.App;

/// <summary>
/// Starts a drag when the pointer is pressed on the icon or name of an item and moves a few pixels (spec §8.4). The press has
/// already been turned into a selection by the view; this decides when it has become a drag. A press on an item that is part of
/// a larger selection leaves that selection alone, so the whole selection can be dragged; if the button comes up without a drag it
/// is a plain click on that one item.
/// </summary>
internal sealed class ItemDragSource
{
    private const double StartDistance = 5;

    private readonly UIElement _scroller;
    private readonly Func<SelectionModel?> _selection;
    private UIElement? _source;
    private FileEntry? _entry;
    private PointerPoint? _press;
    private bool _collapseOnRelease;

    /// <summary>Watches <paramref name="scroller"/> for the pointer moving away from a press that <see cref="Arm"/> noted.</summary>
    public ItemDragSource(UIElement scroller, Func<SelectionModel?> selection)
    {
        _scroller = scroller;
        _selection = selection;
        scroller.AddHandler(UIElement.PointerMovedEvent, new PointerEventHandler(OnMoved), handledEventsToo: true);
        scroller.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler(OnReleased), handledEventsToo: true);
        scroller.PointerCanceled += (_, _) => Disarm();
        scroller.PointerCaptureLost += (_, _) => Disarm();
    }

    /// <summary>
    /// Notes a left-button press on <paramref name="source"/> (the row or cell showing <paramref name="entry"/>). Returns true when
    /// the press must not change the selection yet: a plain press on an already selected item among others.
    /// </summary>
    public bool Arm(PointerRoutedEventArgs e, UIElement source, FileEntry entry, bool plain)
    {
        _source = source;
        _entry = entry;
        _press = e.GetCurrentPoint(null);
        _collapseOnRelease = plain && _selection() is { } selection && selection.IsSelected(entry) && selection.Count > 1;
        return _collapseOnRelease;
    }

    private void OnMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_source is null || _press is null || _selection() is not { } selection)
        {
            return;
        }

        var now = e.GetCurrentPoint(null).Position;
        if (Math.Abs(now.X - _press.Position.X) < StartDistance && Math.Abs(now.Y - _press.Position.Y) < StartDistance)
        {
            return;
        }

        if (!e.GetCurrentPoint(null).Properties.IsLeftButtonPressed)
        {
            Disarm();
            return;
        }

        var source = _source;
        var press = _press;
        var entries = selection.IsSelected(_entry!) ? selection.SelectedEntries : [_entry!];
        Disarm();
        FileDrag.Finish();
        _ = StartAsync(source, press, entries);
    }

    private static async Task StartAsync(UIElement source, PointerPoint press, IReadOnlyList<FileEntry> entries)
    {
        // The drag source element raises DragStarting; the view's row or cell is wired to hand its entries over (see FileDragSource).
        FileDragSource.Pending = entries;
        try
        {
            await source.StartDragAsync(press);
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException or ArgumentException)
        {
            UiTrace.Mark($"drag could not start: {ex.Message}");
            FileDrag.Finish();
        }
        finally
        {
            FileDragSource.Pending = null;
        }
    }

    private void OnReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_collapseOnRelease && _entry is { } entry && _selection() is { } selection)
        {
            selection.Click(entry);
        }

        Disarm();
    }

    private void Disarm()
    {
        _source = null;
        _entry = null;
        _press = null;
        _collapseOnRelease = false;
    }
}

/// <summary>Hands the items of a drag that <see cref="ItemDragSource"/> is starting to the row or cell that raises <c>DragStarting</c>.</summary>
internal static class FileDragSource
{
    /// <summary>The items of the drag being started, set only for the moment between asking for the drag and its first event.</summary>
    internal static IReadOnlyList<FileEntry>? Pending { get; set; }

    /// <summary>Wires <paramref name="element"/> so a drag started on it carries <see cref="Pending"/>.</summary>
    internal static void Attach(UIElement element)
    {
        element.DragStarting += (_, args) =>
        {
            if (Pending is { Count: > 0 } entries)
            {
                FileDrag.Begin(args, entries);
            }
            else
            {
                args.Cancel = true;
            }
        };
        element.DropCompleted += (_, _) => FileDrag.Finish();
    }
}
