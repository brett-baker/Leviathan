using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Velopack;

namespace Leviathan.App;

/// <summary>
/// Entry point (release plan §4). Velopack runs first: when the installer, an update or the uninstaller starts the exe with its own
/// arguments it does that work and exits before any window exists; a normal start falls through. The rest repeats what the XAML
/// compiler generates, which this file replaces so the hook can come first.
/// </summary>
public static class Program
{
    [STAThread]
    private static void Main()
    {
        VelopackApp.Build().Run();

        WinRT.ComWrappersSupport.InitializeComWrappers();
        Application.Start(p =>
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread()));
            _ = new App();
        });
    }
}
