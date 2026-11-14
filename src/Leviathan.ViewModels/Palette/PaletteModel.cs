using Leviathan.Core.Paths;
using Leviathan.ViewModels.Commands;

namespace Leviathan.ViewModels.Palette;

/// <summary>Which palette is open.</summary>
public enum PaletteMode
{
    /// <summary>Ctrl+Shift+P: every command, with its shortcut (spec §6, §15.6).</summary>
    Commands,

    /// <summary>Ctrl+P: favorites, recent folders, open tabs and commands (spec §5.3). A leading <c>&gt;</c> narrows it to commands.</summary>
    GoTo,
}

/// <summary>What a palette row is.</summary>
public enum PaletteKind
{
    /// <summary>A command.</summary>
    Command,

    /// <summary>A pinned folder.</summary>
    Favorite,

    /// <summary>A folder visited recently.</summary>
    Recent,

    /// <summary>A tab that is open.</summary>
    Tab,
}

/// <summary>An open tab as the palette needs to know it.</summary>
/// <param name="Number">Its place in the strip, from 1 (what <c>tab.select</c> takes).</param>
/// <param name="Title">The tab's title.</param>
/// <param name="Location">The folder it shows.</param>
public sealed record PaletteTab(int Number, string Title, LocationPath Location);

/// <summary>One row of a palette.</summary>
/// <param name="Kind">What it is.</param>
/// <param name="Title">The main text.</param>
/// <param name="Detail">The grey text beside it: a command's category, a folder's full path.</param>
/// <param name="Shortcut">A command's key, or null.</param>
/// <param name="Score">How well it matches what was typed.</param>
/// <param name="TitleMatches">Indexes in <see cref="Title"/> of the letters that matched, for drawing them bold.</param>
public sealed record PaletteItem(PaletteKind Kind, string Title, string Detail, string? Shortcut, int Score, IReadOnlyList<int> TitleMatches)
{
    /// <summary>The command to run (commands only).</summary>
    public CommandId? Command { get; init; }

    /// <summary>The folder to go to (folders only).</summary>
    public LocationPath? Location { get; init; }

    /// <summary>The tab to go to (tabs only).</summary>
    public int TabNumber { get; init; }
}

/// <summary>
/// The two palettes (spec §5.3: "Ctrl+P opens a go to palette that fuzzy-searches favorites, recent folders, open tabs and commands";
/// §15.6: "Command palette (Ctrl+Shift+P) for every command, with its shortcut shown"). It searches what is there now (the registry, the
/// key map, the favorites, the recent list, the open tabs) and runs the chosen row through the ordinary command registry, so a palette
/// command is recorded and refused exactly like the same command from a key. Commands that cannot run in the current context are left
/// out rather than shown dead. Pure apart from reading those sources.
/// </summary>
public sealed class PaletteModel(
    CommandRegistry commands,
    KeyMap keys,
    Func<IReadOnlyList<LocationPath>> favorites,
    Func<IReadOnlyList<LocationPath>> recent,
    Func<IReadOnlyList<PaletteTab>> tabs)
{
    /// <summary>How many rows a search returns.</summary>
    public const int MaxRows = 60;

    private static readonly HashSet<CommandId> Hidden = [CommandIds.PaletteCommands, CommandIds.PaletteGoTo];

    /// <summary>The rows for <paramref name="query"/> in <paramref name="mode"/>, best first.</summary>
    public IReadOnlyList<PaletteItem> Search(PaletteMode mode, string query, CommandContext? context = null)
    {
        ArgumentNullException.ThrowIfNull(query);
        var trimmed = query.TrimStart();
        var commandsOnly = mode == PaletteMode.Commands;
        if (mode == PaletteMode.GoTo && trimmed.StartsWith('>'))
        {
            commandsOnly = true;
            trimmed = trimmed[1..];
        }

        var items = new List<PaletteItem>();
        items.AddRange(CommandItems(trimmed, context ?? CommandContext.Empty));
        if (!commandsOnly)
        {
            items.AddRange(TabItems(trimmed));
            items.AddRange(FolderItems(trimmed));
        }

        // An empty query keeps the order the sources give (tabs, favorites, recent, then commands by category); a typed one ranks by score.
        var ordered = trimmed.Trim().Length == 0
            ? items.OrderBy(i => KindOrder(i.Kind)).ThenBy(i => i.Kind == PaletteKind.Command ? i.Detail : string.Empty, StringComparer.OrdinalIgnoreCase).ThenBy(i => i.Kind == PaletteKind.Command ? i.Title : string.Empty, StringComparer.OrdinalIgnoreCase)
            : items.OrderByDescending(i => i.Score).ThenBy(i => KindOrder(i.Kind)).ThenBy(i => i.Title, StringComparer.OrdinalIgnoreCase);
        return [.. ordered.Take(MaxRows)];
    }

    /// <summary>Does what the row says: runs the command, goes to the folder (<paramref name="inNewTab"/> opens it in a new tab instead), or switches to the tab.</summary>
    public async Task<CommandResult> RunAsync(PaletteItem item, bool inNewTab, CommandContext? context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(item);
        switch (item.Kind)
        {
            case PaletteKind.Command when item.Command is { } id:
                return await commands.ExecuteAsync(id, context ?? CommandContext.Empty, cancellationToken).ConfigureAwait(false);
            case PaletteKind.Tab:
                return await commands.ExecuteAsync(CommandIds.TabSelect, new CommandContext(null, item.TabNumber), cancellationToken).ConfigureAwait(false);
            default:
                if (item.Location is not { } where)
                {
                    return CommandResult.Disabled;
                }

                return await commands.ExecuteAsync(inNewTab ? CommandIds.TabNew : CommandIds.NavGo, new CommandContext(where.Value), cancellationToken).ConfigureAwait(false);
        }
    }

    private static int KindOrder(PaletteKind kind) => kind switch
    {
        PaletteKind.Tab => 0,
        PaletteKind.Favorite => 1,
        PaletteKind.Recent => 2,
        _ => 3,
    };

    private IEnumerable<PaletteItem> CommandItems(string query, CommandContext context)
    {
        foreach (var command in commands.Commands)
        {
            var definition = command.Definition;
            if (Hidden.Contains(definition.Id) || string.IsNullOrWhiteSpace(definition.Title) || !command.CanExecute(context))
            {
                continue;
            }

            var matched = new List<int>();
            var titleScore = FuzzyMatcher.Score(query, definition.Title, matched);
            var detailScore = titleScore is null ? FuzzyMatcher.Score(query, definition.Category + " " + definition.Title) : null;
            if (titleScore is null && detailScore is null)
            {
                continue;
            }

            var gestures = keys.GesturesFor(definition.Id);
            var shortcut = gestures.Count > 0 ? gestures[0].ToString() : null;
            yield return new PaletteItem(PaletteKind.Command, definition.Title, definition.Category, shortcut, (titleScore ?? (detailScore!.Value - 20)), titleScore is null ? [] : [.. matched]) { Command = definition.Id };
        }
    }

    private IEnumerable<PaletteItem> TabItems(string query)
    {
        foreach (var tab in tabs())
        {
            var matched = new List<int>();
            var score = Best(query, tab.Title, tab.Location.Value, matched);
            if (score is { } s)
            {
                yield return new PaletteItem(PaletteKind.Tab, tab.Title, tab.Location.Value, null, s + 20, [.. matched]) { TabNumber = tab.Number, Location = tab.Location };
            }
        }
    }

    private IEnumerable<PaletteItem> FolderItems(string query)
    {
        var seen = new HashSet<LocationPath>();
        foreach (var (location, kind, bonus) in favorites().Select(l => (l, PaletteKind.Favorite, 15)).Concat(recent().Select(l => (l, PaletteKind.Recent, 10))))
        {
            if (!seen.Add(location))
            {
                continue;
            }

            var matched = new List<int>();
            var title = location.Name;
            if (Best(query, title, location.Value, matched) is { } score)
            {
                yield return new PaletteItem(kind, title, location.Value, null, score + bonus, [.. matched]) { Location = location };
            }
        }
    }

    // A folder matches on its name first (worth double) and on its whole path second.
    private static int? Best(string query, string title, string detail, List<int> matched)
    {
        var onTitle = FuzzyMatcher.Score(query, title, matched);
        if (onTitle is { } t)
        {
            return t * 2;
        }

        matched.Clear();
        return FuzzyMatcher.Score(query, detail);
    }
}
