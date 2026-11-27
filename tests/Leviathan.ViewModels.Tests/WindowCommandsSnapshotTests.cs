using System.Runtime.CompilerServices;
using System.Text;
using Leviathan.ViewModels.Commands;

namespace Leviathan.ViewModels.Tests;

/// <summary>
/// Pins what the window's command registry holds: every command's id, title, category, icon and shortcut, whether it is enabled with no
/// context and with a folder as its target, and the default key map. A refactor of <c>WindowCommands</c> (docs/simplify/window-commands.md)
/// that loses, renames or re-gates a command shows up here as a one-line difference. When a change to the commands is meant, regenerate the
/// file with the environment variable <c>LEVIATHAN_UPDATE_SNAPSHOTS=1</c> and read the diff before committing it.
/// </summary>
public sealed class WindowCommandsSnapshotTests
{
    private static string SnapshotPath([CallerFilePath] string source = "")
    {
        var beside = Path.Combine(Path.GetDirectoryName(source)!, "WindowCommandsSnapshot.txt");
        if (File.Exists(beside))
        {
            return beside;
        }

        // A continuous-integration build rewrites source paths to "/_/...", so the caller path points nowhere on the runner; find the
        // file by walking up from the test binaries to the repository.
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "tests", "Leviathan.ViewModels.Tests", "WindowCommandsSnapshot.txt");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return beside;
    }

    private static async Task<string> TakeAsync()
    {
        var h = new WindowCommandsTests.Harness();
        await h.Ready;
        var withFolder = new CommandContext(@"C:\a");
        var text = new StringBuilder();
        text.AppendLine("# id | title | category | icon | shortcut | enabled with no context | enabled with a folder target");
        foreach (var command in h.Commands.Commands.OrderBy(c => c.Definition.Id.Value, StringComparer.Ordinal))
        {
            var d = command.Definition;
            text.AppendLine($"{d.Id.Value} | {d.Title} | {d.Category} | {d.Icon} | {d.DefaultShortcut} | {command.CanExecute(CommandContext.Empty)} | {command.CanExecute(withFolder)}");
        }

        text.AppendLine();
        text.AppendLine("# default key map: gesture | command | scope");
        foreach (var b in WindowCommands.DefaultKeyMap().Bindings.OrderBy(b => b.Gesture.ToString(), StringComparer.Ordinal).ThenBy(b => b.Scope))
        {
            text.AppendLine($"{b.Gesture} | {b.Command.Value} | {b.Scope}");
        }

        return text.ToString();
    }

    [Fact]
    public async Task TheRegistry_AndTheDefaultKeyMap_AreWhatTheSnapshotSays()
    {
        var actual = await TakeAsync();
        var path = SnapshotPath();
        if (Environment.GetEnvironmentVariable("LEVIATHAN_UPDATE_SNAPSHOTS") == "1")
        {
            await File.WriteAllTextAsync(path, actual);
            return;
        }

        var expected = (await File.ReadAllTextAsync(path)).ReplaceLineEndings("\n");
        Assert.Equal(expected, actual.ReplaceLineEndings("\n"));
    }
}
