using System.ComponentModel;
using System.Diagnostics;

namespace Leviathan.Engine.Supervision;

/// <summary>
/// Stops the helper processes that run from the program folder (release plan §6), so an update can replace their files. The worker keeps
/// running after the UI closes while it has jobs (spec 4.2), which is right for closing the app and wrong for an update: its files would
/// stay locked. Its jobs are journaled and resume when the new version starts it again (spec §3.4). Only processes whose file is inside the
/// program folder are touched, so another copy of Leviathan elsewhere is left alone.
/// </summary>
public static class HelperStopper
{
    /// <summary>The helper programs, by name without extension.</summary>
    public static readonly string[] Names = ["Leviathan.Worker", "Leviathan.Media", "Leviathan.ShellHost", "Leviathan.Elevated"];

    /// <summary>True when <paramref name="file"/> is inside <paramref name="folder"/> (or a folder below it); case-insensitive, as Windows is.</summary>
    public static bool IsInside(string file, string folder)
    {
        if (string.IsNullOrWhiteSpace(file) || string.IsNullOrWhiteSpace(folder))
        {
            return false;
        }

        var root = System.IO.Path.TrimEndingDirectorySeparator(folder) + System.IO.Path.DirectorySeparatorChar;
        return file.StartsWith(root, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Ends every helper running from <paramref name="programFolder"/>, waiting a few seconds for each to go. Returns how many were stopped.</summary>
    public static async Task<int> StopAllAsync(string programFolder, CancellationToken cancellationToken)
    {
        var stopped = 0;
        foreach (var name in Names)
        {
            foreach (var process in Process.GetProcessesByName(name))
            {
                using (process)
                {
                    if (!RunsFrom(process, programFolder))
                    {
                        continue;
                    }

                    try
                    {
                        process.Kill(entireProcessTree: true);
                        using var wait = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                        wait.CancelAfter(TimeSpan.FromSeconds(5));
                        await process.WaitForExitAsync(wait.Token).ConfigureAwait(false);
                        stopped++;
                    }
                    catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                    {
                        // It did not go in five seconds; the installer will say which file it could not replace.
                    }
                    catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
                    {
                        // Already gone, or not ours to stop.
                    }
                }
            }
        }

        return stopped;
    }

    private static bool RunsFrom(Process process, string programFolder)
    {
        try
        {
            return process.MainModule?.FileName is { } file && IsInside(file, programFolder);
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
        {
            return false;
        }
    }
}
