using Leviathan.Core.Platform;

namespace Leviathan.App;

/// <summary>
/// An always-on, small log of what the tab strip's drag does: every event, the pointer, the index it computed, the tab order before and
/// after. A drag that does the wrong thing is then explained by <c>logs\tab-drag.log</c> in the data folder, with nothing to switch on first
/// (spec §20: every failure is diagnosable). Capped at 512 KiB; when it grows past that the file starts again.
/// </summary>
internal static class TabDragTrace
{
    private const long MaxBytes = 512 * 1024;
    private static readonly Lock Gate = new();
    private static string? _path;

    /// <summary>Where the log is written.</summary>
    public static string Path => _path ??= DataLocation.Combine(System.IO.Path.Combine("logs", "tab-drag.log"));

    /// <summary>Records one line with the time of day.</summary>
    public static void Log(string text)
    {
        UiTrace.Mark("tab drag: " + text);
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
                if (File.Exists(Path) && new FileInfo(Path).Length > MaxBytes)
                {
                    File.Delete(Path);
                }

                File.AppendAllText(Path, $"{DateTime.Now:HH:mm:ss.fff}  {text}{Environment.NewLine}");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A trace must never break a drag.
        }
    }
}
