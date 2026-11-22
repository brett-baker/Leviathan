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

    /// <summary>The New submenu, which the window fills from the New item catalog (spec §6.1).</summary>
    NewMenu,

    /// <summary>A submenu whose lines are <see cref="MenuEntry.Children"/>.</summary>
    SubMenu,
}

/// <summary>One icon button of the strip along the top of the right-click menu.</summary>
/// <param name="Command">The command it runs.</param>
/// <param name="Title">Tooltip text, with the shortcut.</param>
/// <param name="Glyph">The Segoe Fluent Icons character shown.</param>
/// <param name="Context">What the command receives, or null.</param>
/// <param name="Enabled">False when the command cannot run now (Paste with an empty clipboard); shown greyed so the strip keeps its shape.</param>
public sealed record StripButton(CommandId Command, string Title, string Glyph, CommandContext? Context, bool Enabled);

/// <summary>One line of Leviathan's own part of the right-click menu.</summary>
/// <param name="Kind">Command or divider.</param>
/// <param name="Title">Text shown.</param>
/// <param name="Command">The command it runs, null for a divider.</param>
/// <param name="Context">What the command receives.</param>
/// <param name="Shortcut">Key text shown at the right, or null.</param>
/// <param name="Enabled">False when the command cannot run now; it is shown greyed rather than hidden so the menu keeps its shape.</param>
/// <param name="Children">The lines of a <see cref="MenuEntryKind.SubMenu"/>, or null.</param>
public sealed record MenuEntry(MenuEntryKind Kind, string Title, CommandId? Command, CommandContext? Context, string? Shortcut, bool Enabled, IReadOnlyList<MenuEntry>? Children = null)
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

        // Empty space: the New submenu (folder, text document, templates), as in Explorer. Cut, Copy, Paste, Rename and Delete are
        // the icon strip at the top (BuildStrip), not lines here.
        if (selected.Count == 0 && registry.TryGet(CommandIds.NewFolder, out _))
        {
            entries.Add(new MenuEntry(MenuEntryKind.NewMenu, "New", null, null, null, registry.CanExecute(CommandIds.NewFolder, null)));
            AddPasteSpecial(entries, registry);
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

    /// <summary>"Paste special": paste into a new folder, text as a file, a picture as a file. Greyed as a whole when none of them can run now.</summary>
    private static void AddPasteSpecial(List<MenuEntry> entries, CommandRegistry registry)
    {
        var children = new List<MenuEntry>();
        void Add(CommandId command, string title)
        {
            if (registry.TryGet(command, out _))
            {
                children.Add(new MenuEntry(MenuEntryKind.Command, title, command, null, null, registry.CanExecute(command, null)));
            }
        }

        Add(CommandIds.EditPasteIntoNewFolder, "Paste into new folder");
        Add(CommandIds.EditPasteTextAsFile, "Paste text as new file");
        Add(CommandIds.EditPasteImageAsFile, "Paste image as new file");
        if (children.Count > 0)
        {
            entries.Add(new MenuEntry(MenuEntryKind.SubMenu, "Paste special", null, null, null, children.Any(c => c.Enabled), children));
        }
    }

    /// <summary>
    /// The icon strip along the top (as in Windows 11): Cut, Copy, Rename (one item) and Delete for a selection, Paste for empty
    /// space. Only registered commands appear. Paste is disabled until the clipboard holds something usable.
    /// </summary>
    public static IReadOnlyList<StripButton> BuildStrip(IReadOnlyList<FileEntry> selected, CommandRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(selected);
        ArgumentNullException.ThrowIfNull(registry);

        var buttons = new List<StripButton>();
        void Add(CommandId command, string title, string glyph)
        {
            if (registry.TryGet(command, out _))
            {
                buttons.Add(new StripButton(command, title, glyph, null, registry.CanExecute(command, null)));
            }
        }

        if (selected.Count == 0)
        {
            Add(CommandIds.EditPaste, "Paste (Ctrl+V)", "");
            return buttons;
        }

        Add(CommandIds.EditCut, "Cut (Ctrl+X)", "");
        Add(CommandIds.EditCopy, "Copy (Ctrl+C)", "");
        if (selected.Count == 1)
        {
            Add(CommandIds.EditRename, "Rename (F2)", "");
        }

        Add(CommandIds.EditDelete, "Delete (Del)", "");
        return buttons;
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

    /// <summary>
    /// Takes the shell's "Open with" submenu out of <paramref name="items"/> (already prepared), so the window can show it in the top part of
    /// the menu, under Open, instead of leaving it among the other shell items. The shell builds that list itself, from the apps
    /// registered for the file type, including packaged apps and the "Choose another app" line, so Leviathan does not have to. When the
    /// shell has no such submenu, <c>OpenWith</c> is null and the list is returned as it was. Matched by its title.
    /// </summary>
    public static (ShellMenuItem? OpenWith, IReadOnlyList<ShellMenuItem> Others) TakeOpenWith(IReadOnlyList<ShellMenuItem> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        var found = items.FirstOrDefault(i => i.Kind == ShellMenuItemKind.SubMenu && string.Equals(ShellMenuText.Split(i.Label).Title, "Open with", StringComparison.OrdinalIgnoreCase));
        if (found is null)
        {
            return (null, items);
        }

        return (found, Tidy([.. items.Where(i => !ReferenceEquals(i, found))]));
    }

    private static List<ShellMenuItem> Tidy(List<ShellMenuItem> items)
    {
        var result = new List<ShellMenuItem>(items.Count);
        foreach (var item in items)
        {
            if (item.Kind == ShellMenuItemKind.Separator && (result.Count == 0 || result[^1].Kind == ShellMenuItemKind.Separator))
            {
                continue;
            }

            result.Add(item);
        }

        while (result.Count > 0 && result[^1].Kind == ShellMenuItemKind.Separator)
        {
            result.RemoveAt(result.Count - 1);
        }

        return result;
    }

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
