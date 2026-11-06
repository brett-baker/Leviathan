namespace Leviathan.Core.Platform;

/// <summary>
/// Where Leviathan keeps its own state (settings, undo, favorites, caches, the job journal and plans, the soft-delete registry).
/// <c>LEVIATHAN_DATA_DIR</c> moves all of it, for a portable copy and for test runs, which must never touch the user's own files, jobs
/// or undo history (spec §17, §19). The App, the Worker and the databases all ask here, so none of them can keep a private idea of
/// "the data folder" that a test run forgets to move. The same variable also gives the worker and shell host pipes their own names
/// (see <c>PipeNaming</c>), so a run with its own folder talks to its own worker, never to the one the user is using.
/// </summary>
public static class DataLocation
{
    /// <summary>The environment variable that moves the data folder.</summary>
    public const string EnvironmentVariable = "LEVIATHAN_DATA_DIR";

    /// <summary>The data folder: the variable when set, else <c>%LOCALAPPDATA%\Leviathan</c>.</summary>
    public static string Root() => Root(Environment.GetEnvironmentVariable(EnvironmentVariable));

    /// <summary>The data folder for a given value of the variable (null or empty means the default); testable without touching the environment.</summary>
    public static string Root(string? custom) =>
        custom is { Length: > 0 }
            ? custom
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Leviathan");

    /// <summary>A file or folder under the data folder.</summary>
    public static string Combine(string name) => Path.Combine(Root(), name);
}
