using System.IO.Abstractions;
using Leviathan.Core.Paths;
using Leviathan.Core.Platform;
using Leviathan.Core.Providers;
using Leviathan.Interop;
using Leviathan.Providers.Local;
using Leviathan.ViewModels.Commands;
using Leviathan.ViewModels.Folders;
using Leviathan.ViewModels.Navigation;
using Leviathan.ViewModels.Tabs;
using Leviathan.ViewModels.Views;
using Leviathan.ViewModels.Windows;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Input;
using VirtualKey = Windows.System.VirtualKey;
using Windows.UI.Core;

namespace Leviathan.App;

/// <summary>Runs view-model work on the WinUI dispatcher (spec §3.2: results reach the UI thread in batches).</summary>
internal sealed class DispatcherQueueUiContext(DispatcherQueue queue) : IUiContext
{
    public void Post(Action action) => queue.TryEnqueue(() =>
    {
        using var scope = UiTrace.Begin($"UI post {action.Target?.GetType().Name}.{action.Method.Name}", thresholdMs: 5);
        action();
    });
}

/// <summary>
/// The services every window shares: location providers, the window manager, the launcher and the platform lookups. Built
/// once at startup; the composition root of the UI process.
/// </summary>
internal sealed class AppHost
{
    private AppHost(
        WindowManager windows,
        AddressResolver resolver,
        IFileLauncher launcher,
        IKnownFolders knownFolders,
        IVolumeInfo volumes,
        KeyMap keys,
        LocationPath home)
    {
        Windows = windows;
        Resolver = resolver;
        Launcher = launcher;
        KnownFolders = knownFolders;
        Volumes = volumes;
        Keys = keys;
        Home = home;
    }

    public WindowManager Windows { get; }

    public AddressResolver Resolver { get; }

    public IFileLauncher Launcher { get; }

    public IKnownFolders KnownFolders { get; }

    public IVolumeInfo Volumes { get; }

    public KeyMap Keys { get; }

    /// <summary>What a new tab opens. The user profile folder; resolved from the environment, so no disk access at startup.</summary>
    public LocationPath Home { get; }

    public static AppHost Create(IUiContext ui)
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var home = LocationPath.TryParse(profile, out var parsed) ? parsed : LocationPath.Parse(@"C:\");
        var providers = new LocationProviderRegistry([new LocalLocationProvider()]);
        var services = new TabServices(providers, ui, new FolderViewRules(), TimeProvider.System, () => home);
        return new AppHost(
            new WindowManager(services),
            new AddressResolver(new FileSystemEntryProbe(new FileSystem())),
            new WindowsFileLauncher(),
            new WindowsKnownFolders(),
            new WindowsVolumeInfo(),
            WindowCommands.DefaultKeyMap(),
            home);
    }
}

/// <summary>Turns WinUI key presses into <see cref="KeyGesture"/>s for the key map.</summary>
internal static class KeyTranslator
{
    public static KeyGesture? ToGesture(VirtualKey key)
    {
        var name = NameOf(key);
        if (name is null)
        {
            return null;
        }

        var modifiers = KeyModifiers.None;
        if (IsDown(VirtualKey.Control))
        {
            modifiers |= KeyModifiers.Ctrl;
        }

        if (IsDown(VirtualKey.Menu))
        {
            modifiers |= KeyModifiers.Alt;
        }

        if (IsDown(VirtualKey.Shift))
        {
            modifiers |= KeyModifiers.Shift;
        }

        return KeyGesture.Create(modifiers, name);
    }

    /// <summary>The number 1 to 9 for Ctrl+1..9 (tab shortcuts), or 0.</summary>
    public static int DigitOf(VirtualKey key) => key is >= VirtualKey.Number1 and <= VirtualKey.Number9 ? key - VirtualKey.Number0 : 0;

    public static bool IsDown(VirtualKey key) =>
        InputKeyboardSource.GetKeyStateForCurrentThread(key).HasFlag(CoreVirtualKeyStates.Down);

    private static string? NameOf(VirtualKey key) => key switch
    {
        >= VirtualKey.A and <= VirtualKey.Z => key.ToString(),
        >= VirtualKey.Number0 and <= VirtualKey.Number9 => ((int)(key - VirtualKey.Number0)).ToString(System.Globalization.CultureInfo.InvariantCulture),
        >= VirtualKey.F1 and <= VirtualKey.F24 => key.ToString(),
        VirtualKey.Left or VirtualKey.Right or VirtualKey.Up or VirtualKey.Down => key.ToString(),
        VirtualKey.Tab or VirtualKey.Enter or VirtualKey.Escape or VirtualKey.Delete or VirtualKey.Space or VirtualKey.Home or VirtualKey.End
            or VirtualKey.Insert or VirtualKey.PageUp or VirtualKey.PageDown => key.ToString(),
        VirtualKey.Back => "Backspace",
        _ => null,
    };
}
