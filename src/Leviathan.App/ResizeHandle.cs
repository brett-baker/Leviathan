using Microsoft.UI.Input;
using Microsoft.UI.Xaml.Controls;

namespace Leviathan.App;

/// <summary>A thin bar that shows the up-down resize cursor; the window handles the drag (the handle under the preview pane's video, spec §9).</summary>
internal sealed partial class ResizeHandle : Grid
{
    public ResizeHandle() => ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.SizeNorthSouth);
}
