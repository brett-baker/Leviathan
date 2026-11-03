using Windows.Win32;

namespace Leviathan.Interop;

/// <summary>
/// Lets another process bring a window to the front. Windows only lets the process the user is working in do that, so before the
/// shell host runs a command that opens a dialog (Properties, "Open with"), Leviathan hands it that right (spec §8.5).
/// </summary>
public static class ForegroundPermission
{
    /// <summary>Allows process <paramref name="processId"/> to take the foreground once. Returns false when Windows refuses (the process is gone).</summary>
    public static bool Allow(int processId) => processId > 0 && PInvoke.AllowSetForegroundWindow((uint)processId);
}
