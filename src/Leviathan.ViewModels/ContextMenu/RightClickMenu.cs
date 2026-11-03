using Leviathan.Contracts;
using Leviathan.Core.Model;
using Leviathan.Core.Paths;
using Leviathan.ViewModels.CommandBar;
using Leviathan.ViewModels.Commands;

namespace Leviathan.ViewModels.ContextMenu;

/// <summary>What a line of the right-click menu is.</summary>
public enum MenuEntryKind
{
    /// <summary>A command.</summary>
    Command,

    /// <summary>A divider.</summary>
    Separator,
}

/// <summary>One line of Leviathan's own part of the right-click menu.</summary>
/// <param name="Kind">Command or divider.</param>
/// <param name="Title">Text shown.</param>
/// <param name="Command">The command it runs, null for a divider.</param>
/// <param name="Context">What the command receives.</param>
/// <param name="Shortcut">Key text shown at the right, or null.</param>
/// <param name="Enabled">False when the command cannot run now; it is shown greyed rather than hidden so the menu keeps its shape.</param>
public sealed record MenuEntry(MenuEntryKind Kind, string Title, CommandId? Command, CommandContext? Context, string? Shortcut, bool Enabled)
{
    /// <summary>A divider.</summary>
    public static MenuEntry Separator { get; } = new(MenuEntryKind.Separator, string.Empty, null, null, null, true);
}

/// <summary>
/// Leviathan's own part of the right-click menu (spec §8.5): Open, Open in new tab, Open with, Cut, Copy, Paste, Rename, Delete,
/// Properties, then the context actions of spec §6.2. It lists only commands that are registered, so it fills in as features
/// land, and it never waits for anything: the shell's items arrive separately and later (<see cref="ShellMenuFilter"/>).
/// </summary>
public static class RightClickMenu
{
    /// <summary>Builds the menu for <paramref name="selected"/> in the folder <paramref name="current"/>; an empty selection is the folder's empty space.</summary>
    public static IReadOnlyList<MenuEntry> Build(IReadOnlyList<FileEntry> selected, LocationPath current, CommandRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(selected);
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(registry);

        var selection = ContextActions.Classify(selected);
        var single = selected.Count == 1 ? selected[0] : null;
        var entries = new List<MenuEntry>();
        var used = new HashSet<CommandId>();

        void Add(CommandId command, string title, string? shortcut = null, CommandContext? context = null)
        {
            if (registry.TryGet(command, out _) && used.Add(command))
            {
                entries.Add(new MenuEntry(MenuEntryKind.Command, title, command, context, shortcut, registry.CanExecute(command, context)));
            }
        }

        CommandContext OnEntry() => new(single!.Location.Value, single);

        // Opening.
        if (single is not null)
        {
            var isFolder = selection == SelectionClass.Folder;
            Add(CommandIds.ItemOpen, isFolder ? "Open" : OpenTitle(selection), "Enter", OnEntry());
            if (isFolder)
            {
                Add(CommandIds.TabOpenInNewTab, "Open in new tab", null, new CommandContext(single.Location.Value, true));
            }
            else
            {
                Add(CommandIds.OpenWith, "Open with…", null, OnEntry());
            }
        }

        entries.Add(MenuEntry.Separator);

        // The clipboard group and rename / delete.
        if (selected.Count > 0)
        {
            Add(CommandIds.EditCut, "Cut", "Ctrl+X");
            Add(CommandIds.EditCopy, "Copy", "Ctrl+C");
        }

        Add(CommandIds.EditPaste, "Paste", "Ctrl+V");
        if (single is not null)
        {
            Add(CommandIds.EditRename, "Rename", "F2");
        }

        if (selected.Count > 0)
        {
            Add(CommandIds.EditDelete, "Delete", "Del");
        }
        else
        {
            Add(CommandIds.NewFolder, "New folder", "Ctrl+Shift+N");
        }

        entries.Add(MenuEntry.Separator);

        // What the selection offers, from the same table as the command bar.
        foreach (var button in ContextActions.For(selection, registry))
        {
            if (button.Command == CommandIds.Properties)
            {
                continue;
            }

            var context = ContextActions.ContextOf(button, selected, current);
            Add(button.Command, button.Title, button.Shortcut, context);
        }

        entries.Add(MenuEntry.Separator);
        if (single is not null)
        {
            Add(CommandIds.Properties, "Properties", "Alt+Enter", OnEntry());
        }

        return Tidy(entries);
    }

    private static string OpenTitle(SelectionClass selection) => selection switch
    {
        SelectionClass.Video or SelectionClass.Audio => "Play",
        SelectionClass.Executable => "Run",
        _ => "Open",
    };

    /// <summary>Removes dividers that lead, trail or sit side by side, which appear when the lines between them are not registered yet.</summary>
    private static List<MenuEntry> Tidy(List<MenuEntry> entries)
    {
        var result = new List<MenuEntry>(entries.Count);
        foreach (var entry in entries)
        {
            if (entry.Kind == MenuEntryKind.Separator && (result.Count == 0 || result[^1].Kind == MenuEntryKind.Separator))
            {
                continue;
            }

            result.Add(entry);
        }

        while (result.Count > 0 && result[^1].Kind == MenuEntryKind.Separator)
        {
            result.RemoveAt(result.Count - 1);
        }

        return result;
    }
}

/// <summary>
/// Prepares the shell's items for Leviathan's menu (spec §8.5): leaves out the commands Leviathan offers itself, so the menu does
/// not say Cut twice, and tidies the dividers that leaves behind. Pure, so the rules are tested without a shell.
/// </summary>
public static class ShellMenuFilter
{
    /// <summary>Verbs Leviathan's own part of the menu covers. Compared without case.</summary>
    private static readonly HashSet<string> Own = new(StringComparer.OrdinalIgnoreCase)
    {
        "open", "cut", "copy", "paste", "rename", "delete", "properties",
    };

    /// <summary>Returns <paramref name="items"/> without the items Leviathan shows itself, and with tidy dividers; submenus are filtered too.</summary>
    public static IReadOnlyList<ShellMenuItem> Prepare(IReadOnlyList<ShellMenuItem> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        var kept = new List<ShellMenuItem>(items.Count);
        foreach (var item in items)
        {
            if (item.Kind == ShellMenuItemKind.Separator)
            {
                if (kept.Count > 0 && kept[^1].Kind != ShellMenuItemKind.Separator)
                {
                    kept.Add(item);
                }

                continue;
            }

            if (item.Verb is { } verb && Own.Contains(verb))
            {
                continue;
            }

            kept.Add(item.Children is null ? item : item with { Children = Prepare(item.Children) });
        }

        while (kept.Count > 0 && kept[^1].Kind == ShellMenuItemKind.Separator)
        {
            kept.RemoveAt(kept.Count - 1);
        }

        return kept;
    }
}

/// <summary>The text of a shell menu label: <c>&amp;Open\tCtrl+O</c> is the title "Open" and the shortcut text "Ctrl+O".</summary>
public static class ShellMenuText
{
    /// <summary>Splits a label into its title (mnemonic marks removed) and the shortcut text after the tab, if any.</summary>
    public static (string Title, string? Shortcut) Split(string? label)
    {
        if (string.IsNullOrEmpty(label))
        {
            return (string.Empty, null);
        }

        var tab = label.IndexOf('\t', StringComparison.Ordinal);
        var title = tab >= 0 ? label[..tab] : label;
        var shortcut = tab >= 0 && tab + 1 < label.Length ? label[(tab + 1)..] : null;
        return (RemoveMnemonic(title), shortcut);
    }

    private static string RemoveMnemonic(string text)
    {
        if (!text.Contains('&', StringComparison.Ordinal))
        {
            return text;
        }

        var result = new System.Text.StringBuilder(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] != '&')
            {
                result.Append(text[i]);
            }
            else if (i + 1 < text.Length && text[i + 1] == '&')
            {
                // "&&" is a literal ampersand.
                result.Append('&');
                i++;
            }
        }

        return result.ToString();
    }
}
