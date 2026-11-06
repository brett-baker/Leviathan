using Leviathan.ViewModels.Commands;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;

namespace Leviathan.App;

/// <summary>The extra mouse buttons: back and forward, like in a browser and in Explorer.</summary>
public sealed partial class MainWindow
{
    private void InitMouseNavigation() =>
        Root.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler(OnMouseNavigation), handledEventsToo: true);

    // Back (button 4) and forward (button 5) go through the same commands as Alt+Left and Alt+Right, in the pane under the pointer: a pane
    // takes the focus when it is pressed, and that happens before this handler runs (it is below this one in the tree).
    private void OnMouseNavigation(object sender, PointerRoutedEventArgs e)
    {
        if (e.Pointer.PointerDeviceType != Microsoft.UI.Input.PointerDeviceType.Mouse)
        {
            return;
        }

        var properties = e.GetCurrentPoint(Root).Properties;
        if (properties.IsXButton1Pressed)
        {
            Run(CommandIds.NavBack);
        }
        else if (properties.IsXButton2Pressed)
        {
            Run(CommandIds.NavForward);
        }
    }
}
