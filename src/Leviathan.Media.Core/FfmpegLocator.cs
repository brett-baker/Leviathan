namespace Leviathan.Media;

/// <summary>Where the ffmpeg tools are, and why that place was chosen (for the diagnostics and for "Show ffprobe info").</summary>
/// <param name="FfprobePath">Full path of ffprobe, or null when none was found.</param>
/// <param name="FfmpegPath">Full path of ffmpeg, or null when none was found.</param>
/// <param name="Source">Which rule found them: "setting", "LEVIATHAN_FFMPEG_DIR", "app folder", "PATH" or "not found".</param>
public sealed record FfmpegTools(string? FfprobePath, string? FfmpegPath, string Source)
{
    /// <summary>True when ffprobe was found.</summary>
    public bool HasFfprobe => FfprobePath is not null;
}

/// <summary>
/// Finds ffprobe and ffmpeg (spec §9.3): the folder set in Settings, then <c>LEVIATHAN_FFMPEG_DIR</c>, then an <c>ffmpeg</c> folder next
/// to the app (where the bundled GPL build goes), then the <c>PATH</c>. The first folder that has the tool wins, so a user's own choice
/// always beats what ships with the app. Everything it touches goes through delegates so it is tested without a disk or an environment.
/// </summary>
public sealed class FfmpegLocator(
    Func<string, bool> fileExists,
    Func<string, string?> environment,
    string appFolder,
    string? settingFolder = null)
{
    /// <summary>The environment variable that names an ffmpeg folder (a test run, or a portable copy with its own build).</summary>
    public const string EnvironmentVariable = "LEVIATHAN_FFMPEG_DIR";

    private static readonly string Suffix = OperatingSystem.IsWindows() ? ".exe" : string.Empty;

    /// <summary>A locator over the real file system and environment.</summary>
    public static FfmpegLocator Create(string? settingFolder = null) =>
        new(File.Exists, Environment.GetEnvironmentVariable, AppContext.BaseDirectory, settingFolder);

    /// <summary>Looks for the tools now (cheap; call again after the setting changes).</summary>
    public FfmpegTools Locate()
    {
        foreach (var (source, folder) in Candidates())
        {
            var probe = Path.Combine(folder, "ffprobe" + Suffix);
            if (fileExists(probe))
            {
                var ffmpeg = Path.Combine(folder, "ffmpeg" + Suffix);
                return new FfmpegTools(probe, fileExists(ffmpeg) ? ffmpeg : null, source);
            }
        }

        return new FfmpegTools(null, null, "not found");
    }

    private IEnumerable<(string Source, string Folder)> Candidates()
    {
        if (!string.IsNullOrWhiteSpace(settingFolder))
        {
            yield return ("setting", settingFolder);
        }

        if (environment(EnvironmentVariable) is { Length: > 0 } fromEnvironment)
        {
            yield return (EnvironmentVariable, fromEnvironment);
        }

        yield return ("app folder", Path.Combine(appFolder, "ffmpeg"));
        foreach (var folder in (environment("PATH") ?? string.Empty).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            yield return ("PATH", folder.Trim('"'));
        }
    }
}
