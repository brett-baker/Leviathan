namespace Leviathan.Engine.Supervision;

/// <summary>
/// How to start one of the app's helper processes (worker, shell host, media process; spec §2.2): the executable next to the app when it
/// exists, else the same program through <c>dotnet</c> (a build run from Visual Studio has the dll but not the exe). One rule, three users.
/// </summary>
public static class HelperProcess
{
    /// <summary>What to run and with which arguments.</summary>
    public readonly record struct StartCommand(string Path, string? Arguments);

    /// <summary>The command that starts <paramref name="name"/> (no extension) found in <paramref name="baseDirectory"/>.</summary>
    public static StartCommand CommandFor(string name, string baseDirectory, Func<string, bool> fileExists)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(fileExists);
        var exe = Path.Combine(baseDirectory, name + ".exe");
        return fileExists(exe)
            ? new StartCommand(exe, null)
            : new StartCommand("dotnet", $"\"{Path.Combine(baseDirectory, name + ".dll")}\"");
    }

    /// <summary>A launcher for the helper <paramref name="name"/> in the app's own folder.</summary>
    public static ExecutableLauncher LauncherFor(string name)
    {
        var command = CommandFor(name, AppContext.BaseDirectory, File.Exists);
        return new ExecutableLauncher(command.Path, command.Arguments);
    }

    /// <summary>What the Background tab says about a helper: its state in words, and the detail line (spec §16.1).</summary>
    public static (string State, string Detail, bool CanReenable) Describe(SupervisedState state, string? disabledReason, string purpose) => (
        state switch
        {
            SupervisedState.Running => "Running",
            SupervisedState.Restarting => "Restarting after a crash",
            SupervisedState.Disabled => "Switched off",
            _ => "Stopped until it is needed",
        },
        state == SupervisedState.Disabled ? disabledReason ?? purpose : purpose,
        state == SupervisedState.Disabled);
}
