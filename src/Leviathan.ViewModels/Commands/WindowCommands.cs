// View models are UI-thread affine: everything a control can observe is changed on the thread that started the command, so the
// awaits in this file deliberately resume on the caller's context (crash signature 52a0ec04c34d: RPC_E_WRONG_THREAD from a
// continuation that ran on the thread pool). UiThreadAffinityTests guards this.
#pragma warning disable CA2007

using System.Text.RegularExpressions;
using Leviathan.Core.Model;
using Leviathan.Core.Paths;
using Leviathan.Core.Platform;
using Leviathan.Core.Providers;
using Leviathan.Diagnostics;
using Leviathan.ViewModels.Navigation;
using Leviathan.ViewModels.Tabs;
using Leviathan.ViewModels.Windows;

namespace Leviathan.ViewModels.Commands;

/// <summary>
/// Registers the commands of one window against its tabs (build plan, Phase 1: every user action is a command). The UI
/// buttons, menus, shortcuts and, later, the command palette all call these, so the action trail sees everything the user does.
/// </summary>
public static partial class WindowCommands
{
    /// <summary>Creates the registry for <paramref name="window"/>.</summary>
    public static CommandRegistry Create(
        ActionTrail trail,
        WindowViewModel window,
        WindowManager windows,
        AddressResolver resolver,
        IFileLauncher launcher,
        IShellActions shell,
        Func<LocationPath> home,
        Sidebar.FavoritesList? favorites = null,
        NewMenu.NewItemService? newItems = null,
        Views.ViewOptions? viewOptions = null,
        Folders.RenameService? renamer = null,
        Transfers.ITransferService? transfers = null,
        Drives.DriveCardPreferences? driveCards = null,
        Transfers.IDeleteService? deleter = null,
        Undo.UndoService? undo = null,
        Transfers.IFileClipboard? clipboard = null,
        Sessions.ProfileService? profiles = null,
        Archives.IArchiveService? archives = null,
        Func<TerminalKind>? terminal = null,
        IQuickTargets? quickTargets = null)
    {
        ArgumentNullException.ThrowIfNull(trail);
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(windows);
        ArgumentNullException.ThrowIfNull(resolver);
        ArgumentNullException.ThrowIfNull(launcher);
        ArgumentNullException.ThrowIfNull(shell);
        ArgumentNullException.ThrowIfNull(home);

        var registry = new CommandRegistry(trail);
        var strip = window.Tabs;

        // A locked tab never navigates: its folder opens in a new tab (spec 4.1). Hooks follow tabs as they enter and leave this window.
        var hooked = new Dictionary<TabViewModel, EventHandler<LocationPath>>();
        void Hook(TabViewModel tab)
        {
            if (hooked.ContainsKey(tab))
            {
                return;
            }

            void OnRequested(object? sender, LocationPath location) =>
                _ = ReportFailures(registry.ExecuteAsync(CommandIds.TabNew, new CommandContext(location.Value), CancellationToken.None), shell);
            hooked[tab] = OnRequested;
            tab.OpenInNewTabRequested += OnRequested;
        }

        foreach (var existing in strip.Tabs)
        {
            Hook(existing);
        }

        strip.Tabs.CollectionChanged += (_, e) =>
        {
            foreach (var tab in e.NewItems?.OfType<TabViewModel>() ?? [])
            {
                Hook(tab);
            }

            foreach (var tab in e.OldItems?.OfType<TabViewModel>() ?? [])
            {
                if (hooked.Remove(tab, out var handler))
                {
                    tab.OpenInNewTabRequested -= handler;
                }
            }
        };

        // The pane the user is working in: the focused one of a split tab (spec §15.1). Tab-level commands (close, pin, duplicate) use TabOf.
        TabViewModel? Current() => strip.Selected?.Focused;
        TabViewModel? TabOf(CommandContext c) => c.Parameter as TabViewModel ?? strip.Selected;

        void Add(CommandId id, string title, string category, Func<CommandContext, CancellationToken, Task> run, Func<CommandContext, bool>? can = null, string? icon = null, string? shortcut = null) =>
            registry.Register(new AppCommand(new CommandDefinition(id, title, category, icon, shortcut), run, can));

        static int Steps(CommandContext context) => context.Parameter is int steps and > 0 ? steps : 1;

        Add(CommandIds.TabNew, "New tab", "Tabs", async (c, ct) =>
        {
            var location = LocationPath.TryParse(c.Target, out var parsed) ? parsed : home();
            var tab = strip.NewTab(location);
            await tab.ReloadAsync(ct);
        }, shortcut: "Ctrl+T", icon: "Add");

        Add(CommandIds.TabNewRight, "New tab to the right", "Tabs", async (c, ct) =>
        {
            var tab = strip.NewTab(home(), placement: NewTabPlacement.Right, relativeTo: TabOf(c));
            await tab.ReloadAsync(ct);
        }, c => TabOf(c) is not null);

        Add(CommandIds.TabNewLeft, "New tab to the left", "Tabs", async (c, ct) =>
        {
            var tab = strip.NewTab(home(), placement: NewTabPlacement.Left, relativeTo: TabOf(c));
            await tab.ReloadAsync(ct);
        }, c => TabOf(c) is not null);

        Add(CommandIds.TabClose, "Close tab", "Tabs", (c, _) =>
        {
            if (TabOf(c) is { } tab)
            {
                strip.Close(tab);
            }

            return Task.CompletedTask;
        }, c => TabOf(c) is { IsPinned: false }, shortcut: "Ctrl+W");

        Add(CommandIds.TabReopen, "Reopen closed tab", "Tabs", async (_, ct) =>
        {
            if (strip.Reopen() is { } tab)
            {
                await tab.ReloadAsync(ct);
            }
        }, _ => strip.CanReopen, shortcut: "Ctrl+Shift+T");

        Add(CommandIds.TabDuplicate, "Duplicate tab", "Tabs", async (c, ct) =>
        {
            if (TabOf(c) is { } tab)
            {
                await strip.Duplicate(tab).ReloadAsync(ct);
            }
        }, c => TabOf(c) is not null, shortcut: "Ctrl+Shift+K");

        Add(CommandIds.TabNext, "Next tab", "Tabs", (_, _) => Run(() => strip.Cycle(forward: true)), _ => strip.Tabs.Count > 1, shortcut: "Ctrl+Tab");
        Add(CommandIds.TabPrevious, "Previous tab", "Tabs", (_, _) => Run(() => strip.Cycle(forward: false)), _ => strip.Tabs.Count > 1, shortcut: "Ctrl+Shift+Tab");
        Add(CommandIds.TabSelect, "Go to tab", "Tabs", (c, _) => Run(() => strip.SelectByNumber(c.Parameter is int n ? n : 0)), c => c.Parameter is int);

        Add(CommandIds.TabTogglePin, "Pin tab", "Tabs", (c, _) => Run(() =>
        {
            if (TabOf(c) is { } tab)
            {
                strip.SetPinned(tab, !tab.IsPinned);
            }
        }), c => TabOf(c) is not null);

        Add(CommandIds.TabToggleLock, "Lock tab", "Tabs", (c, _) => Run(() =>
        {
            if (TabOf(c) is { } tab)
            {
                tab.IsLocked = !tab.IsLocked;
            }
        }), c => TabOf(c) is not null);

        // Only a locked tab can be named: an unlocked tab always shows its folder, and unlocking takes the name away again.
        Add(CommandIds.TabRename, "Rename tab", "Tabs", (c, _) => Run(() =>
        {
            if (TabOf(c) is { IsLocked: true } tab)
            {
                var label = c.Target?.Trim();
                tab.CustomTitle = string.IsNullOrEmpty(label) ? null : label.Length > MaxTabLabel ? label[..MaxTabLabel] : label;
            }
        }), c => TabOf(c) is { IsLocked: true });

        Add(CommandIds.TabColor, "Colour tab", "Tabs", (c, _) => Run(() =>
        {
            if (TabOf(c) is not { } tab)
            {
                return;
            }

            var value = c.Target?.Trim();
            if (string.IsNullOrEmpty(value))
            {
                tab.Accent = null;
            }
            else if (HexColor().IsMatch(value))
            {
                tab.Accent = value.ToUpperInvariant();
            }
            else
            {
                shell.ShowMessage($"'{value}' is not a colour. Use the form #RRGGBB.");
            }
        }), c => TabOf(c) is not null);

        Add(CommandIds.TabCloseOthers, "Close other tabs", "Tabs", (c, _) => Run(() =>
        {
            if (TabOf(c) is { } tab)
            {
                strip.CloseOthers(tab);
            }
        }), c => TabOf(c) is { } tab && strip.Tabs.Any(t => t != tab && !t.IsPinned));

        Add(CommandIds.TabCloseRight, "Close tabs to the right", "Tabs", (c, _) => Run(() =>
        {
            if (TabOf(c) is { } tab)
            {
                strip.CloseToRight(tab);
            }
        }), c => TabOf(c) is { } tab && strip.Tabs.Skip(strip.Tabs.IndexOf(tab) + 1).Any(t => !t.IsPinned));

        Add(CommandIds.TabMoveLeft, "Move tab left", "Tabs", (c, _) => Run(() => strip.Nudge(TabOf(c)!, -1)), c => TabOf(c) is not null, shortcut: "Ctrl+Shift+PageUp");
        Add(CommandIds.TabMoveRight, "Move tab right", "Tabs", (c, _) => Run(() => strip.Nudge(TabOf(c)!, 1)), c => TabOf(c) is not null, shortcut: "Ctrl+Shift+PageDown");
        Add(CommandIds.TabMove, "Move tab", "Tabs", (c, _) => Run(() => strip.Move(((TabMove)c.Parameter!).Tab, ((TabMove)c.Parameter!).Index)), c => c.Parameter is TabMove);

        // Tab groups (spec §4.1, D32). Parameters are the strip's own objects, so a group menu or a drag hands them straight over.
        TabGroup? GroupOfParameter(CommandContext c) => c.Parameter is TabGroup g && strip.Groups.Contains(g) ? g : null;

        Add(CommandIds.TabGroupNew, "Add tab to new group", "Tabs", (c, _) => Run(() => strip.CreateGroup([TabOf(c)!], c.Target)), c => TabOf(c) is { IsPinned: false });
        Add(CommandIds.TabGroupAdd, "Add tab to group", "Tabs", (c, _) => Run(() =>
        {
            var add = (TabGroupAdd)c.Parameter!;
            strip.AddToGroup(add.Tab, add.Group, add.Position);
        }), c => c.Parameter is TabGroupAdd { Tab.IsPinned: false } add && strip.Groups.Contains(add.Group) && strip.Tabs.Contains(add.Tab));
        Add(CommandIds.TabGroupRemove, "Remove tab from group", "Tabs", (c, _) => Run(() => strip.RemoveFromGroup(TabOf(c)!)), c => TabOf(c)?.GroupId is not null);
        Add(CommandIds.TabGroupRename, "Rename group", "Tabs", (c, _) => Run(() => GroupOfParameter(c)!.Name = (c.Target ?? string.Empty).Trim()), c => GroupOfParameter(c) is not null);
        Add(CommandIds.TabGroupColor, "Change group colour", "Tabs", (c, _) => Run(() => GroupOfParameter(c)!.Color = c.Target!.ToUpperInvariant()),
            c => GroupOfParameter(c) is not null && TabGroupColors.IsColor(c.Target));
        Add(CommandIds.TabGroupCollapse, "Collapse or expand group", "Tabs", (c, _) => Run(() =>
        {
            var group = GroupOfParameter(c)!;
            var collapse = c.Target is null ? !group.IsCollapsed : !c.Target.Equals("false", StringComparison.OrdinalIgnoreCase);
            if (!strip.SetCollapsed(group, collapse))
            {
                shell.ShowMessage("The group holds every tab in this window, so it cannot be folded away.");
            }
        }), c => GroupOfParameter(c) is not null);
        Add(CommandIds.TabGroupUngroup, "Ungroup", "Tabs", (c, _) => Run(() => strip.Ungroup(GroupOfParameter(c)!)), c => GroupOfParameter(c) is not null);
        Add(CommandIds.TabGroupClose, "Close group", "Tabs", (c, _) => Run(() => strip.CloseGroup(GroupOfParameter(c)!)), c => GroupOfParameter(c) is not null);
        Add(CommandIds.TabGroupNewTab, "New tab in group", "Tabs", async (c, ct) =>
        {
            var tab = strip.NewTabInGroup(GroupOfParameter(c)!, LocationPath.TryParse(c.Target, out var parsed) ? parsed : null);
            if (tab is not null)
            {
                await tab.ReloadAsync(ct);
            }
        }, c => GroupOfParameter(c) is not null);
        Add(CommandIds.TabGroupToWindow, "Move group to new window", "Tabs", (c, _) => Run(() =>
        {
            var bounds = window.Bounds.Rect;
            if (windows.TearOffGroup(window, GroupOfParameter(c)!, bounds.X + 48, bounds.Y + 48) is null)
            {
                shell.ShowMessage("The group holds every tab in this window, so there is nothing to move.");
            }
        }), c => GroupOfParameter(c) is not null);
        Add(CommandIds.TabGroupMove, "Move group", "Tabs", (c, _) => Run(() =>
        {
            var move = (TabGroupMove)c.Parameter!;
            strip.MoveGroup(move.Group, move.Index);
        }), c => c.Parameter is TabGroupMove move && strip.Groups.Contains(move.Group));
        Add(CommandIds.TabGroupTearOff, "Move group to a new window", "Tabs", (c, _) => Run(() =>
        {
            var drag = (TabGroupTearOff)c.Parameter!;
            windows.TearOffGroup(window, drag.Group, drag.ScreenX, drag.ScreenY);
        }), c => c.Parameter is TabGroupTearOff);
        Add(CommandIds.TabGroupDrop, "Drop group here", "Tabs", (c, _) => Run(() =>
        {
            var drop = (TabGroupDrop)c.Parameter!;
            var from = windows.Windows.FirstOrDefault(w => w.Tabs.Groups.Contains(drop.Group));
            if (from == window)
            {
                strip.MoveGroup(drop.Group, drop.Index ?? strip.Tabs.Count);
            }
            else if (from is not null)
            {
                windows.MergeGroup(from, drop.Group, window, drop.Index);
            }
        }), c => c.Parameter is TabGroupDrop);

        Add(CommandIds.TabTearOff, "Move tab to a new window", "Tabs", (c, _) => Run(() =>
        {
            var drag = (TabTearOff)c.Parameter!;
            windows.TearOff(window, drag.Tab, drag.ScreenX, drag.ScreenY);
        }), c => c.Parameter is TabTearOff);

        Add(CommandIds.TabDrop, "Drop tab here", "Tabs", (c, _) => Run(() =>
        {
            var drop = (TabDrop)c.Parameter!;
            var from = windows.FindWindowOf(drop.Tab);
            if (from is null)
            {
                return;
            }

            if (from == window)
            {
                strip.Move(drop.Tab, drop.Index ?? strip.Tabs.Count - 1);
            }
            else
            {
                windows.Merge(from, drop.Tab, window, drop.Index);
            }
        }), c => c.Parameter is TabDrop);

        Add(CommandIds.TabOpenInNewTab, "Open in new tab", "Tabs", async (c, ct) =>
        {
            if (!LocationPath.TryParse(c.Target, out var location))
            {
                return;
            }

            // A folder opened from the tab you are in sits next to it, like a link opened in a browser.
            var tab = strip.NewTab(location, select: c.Parameter is true, placement: NewTabPlacement.Right);
            await tab.ReloadAsync(ct);
        }, c => !string.IsNullOrWhiteSpace(c.Target));

        Add(CommandIds.WindowNew, "New window", "Windows", (c, _) =>
        {
            windows.OpenWindow(LocationPath.TryParse(c.Target, out var parsed) ? parsed : home());
            return Task.CompletedTask;
        }, shortcut: "Ctrl+N", icon: "NewWindow");

        // Startup profiles (spec §17.1). Saving and opening are the user's own actions, so a refusal says why in the status bar.
        async Task ReportProfile(Func<Task<string>> action)
        {
            try
            {
                shell.ShowMessage(await action());
            }
            catch (Sessions.ProfileException ex)
            {
                shell.ShowMessage(ex.Message);
            }
        }

        Add(CommandIds.ProfileSave, "Save current windows as profile", "Profiles", (c, ct) => ReportProfile(async () =>
        {
            await profiles!.SaveCurrentAsync(c.Target!, ct);
            return $"Saved the open windows as the profile \"{c.Target!.Trim()}\".";
        }), c => profiles is not null && !string.IsNullOrWhiteSpace(c.Target));
        Add(CommandIds.ProfileOpen, "Open profile", "Profiles", (c, _) => ReportProfile(() =>
        {
            var opened = profiles!.Open(c.Target!, replace: c.Parameter is not false);
            return Task.FromResult($"Opened the profile \"{c.Target!.Trim()}\" ({opened} window{(opened == 1 ? string.Empty : "s")}).");
        }), c => profiles is not null && profiles.Catalog.Find(c.Target) is not null);
        Add(CommandIds.ProfileDelete, "Delete profile", "Profiles", (c, ct) => ReportProfile(async () =>
            await profiles!.DeleteAsync(c.Target!, ct) ? $"Deleted the profile \"{c.Target!.Trim()}\"." : "There is no such profile."),
            c => profiles is not null && profiles.Catalog.Find(c.Target) is not null);

        Add(CommandIds.NavBack, "Back", "Navigation", (c, ct) => Current()!.GoBackAsync(ct, Steps(c)), _ => Current()?.History.CanGoBack == true, "Back", "Alt+Left");
        Add(CommandIds.NavForward, "Forward", "Navigation", (c, ct) => Current()!.GoForwardAsync(ct, Steps(c)), _ => Current()?.History.CanGoForward == true, "Forward", "Alt+Right");
        Add(CommandIds.NavUp, "Up", "Navigation", (_, ct) => Current()!.GoUpAsync(ct), _ => Current()?.History.CanGoUp == true, "Up", "Alt+Up");
        // F5 means "look again": the cached rows are not shown first. A tab that was never listed (restored, or just opened) is not a refresh.
        Add(CommandIds.NavReload, "Reload", "Navigation", (_, ct) => Current()!.ReloadAsync(ct, forceFresh: Current()!.Folder.State is not Folders.FolderLoadState.Idle), _ => Current() is not null, "Refresh", "F5");

        Add(CommandIds.NavGo, "Go to address", "Navigation", async (c, ct) =>
        {
            if (Current() is not { } tab)
            {
                return;
            }

            var result = await resolver.ResolveAsync(c.Target, tab.History.Current, ct);
            switch (result.Outcome)
            {
                case AddressOutcome.Navigate or AddressOutcome.NavigateAndSelect:
                    await tab.NavigateAsync(result.Folder!, ct);
                    if (result.SelectItem is { } item && tab.Items.Rows.FirstOrDefault(r => r.Location == item) is { } entry)
                    {
                        tab.Selection.Click(entry);
                    }

                    break;
                default:
                    shell.ShowMessage(result.Message ?? "That location could not be opened.");
                    break;
            }
        }, _ => Current() is not null);

        Add(CommandIds.ItemOpen, "Open", "Files", async (c, ct) =>
        {
            if (c.Parameter is not FileEntry entry || Current() is not { } tab)
            {
                return;
            }

            // An archive opens as a folder (spec §15.4); "Open with" is how to hand it to another program. An archive inside an
            // archive is only a file, and is unpacked and opened by its program.
            if (Folders.FileColumns.IsFolderLike(entry) || (entry.Kind == FileEntryKind.File && Core.Archives.ArchiveKinds.IsBrowsable(entry.Extension) && !Core.Archives.ArchiveKinds.IsInside(entry.Location)))
            {
                await tab.NavigateAsync(entry.Location, ct);
                return;
            }

            var launched = await launcher.OpenAsync(entry.Location, ct);
            if (!launched.Started && !launched.Cancelled)
            {
                shell.ShowMessage(launched.Error ?? $"{entry.Name} could not be opened.");
            }
        }, c => c.Parameter is FileEntry, shortcut: "Enter");

        Add(CommandIds.ViewLayout, "Change layout", "View", (c, _) => Run(() =>
        {
            Current()!.SetLayout((Views.ViewLayout)c.Parameter!);
        }), c => Current() is not null && c.Parameter is Views.ViewLayout);

        Add(CommandIds.ViewDetails, "Details layout", "View", (_, _) => Run(() => Current()!.SetLayout(Views.ViewLayout.Details)), _ => Current() is not null, shortcut: "Ctrl+Shift+6");
        Add(CommandIds.ViewList, "List layout", "View", (_, _) => Run(() => Current()!.SetLayout(Views.ViewLayout.List)), _ => Current() is not null, shortcut: "Ctrl+Shift+5");
        Add(CommandIds.ViewThumbnails, "Thumbnails layout", "View", (_, _) => Run(() => Current()!.SetLayout(Views.ViewLayout.Thumbnails)), _ => Current() is not null, shortcut: "Ctrl+Shift+2");
        Add(CommandIds.ViewTiles, "Tiles layout", "View", (_, _) => Run(() => Current()!.SetLayout(Views.ViewLayout.Tiles)), _ => Current() is not null, shortcut: "Ctrl+Shift+4");
        Add(CommandIds.ViewGallery, "Gallery layout", "View", (_, _) => Run(() => Current()!.SetLayout(Views.ViewLayout.Gallery)), _ => Current() is not null, shortcut: "Ctrl+Shift+1");

        // Archives (spec §15.4): extract and compress are worker jobs. The items are the one the command was run on, else the selection.
        IReadOnlyList<LocationPath> ArchiveTargets(CommandContext c) =>
            c.Parameter is FileEntry one ? [one.Location] : Current() is { } here ? [.. here.Selection.SelectedEntries.Select(e => e.Location)] : [];
        async Task RunExtract(CommandContext c, CancellationToken ct, bool subfolder, bool pick)
        {
            var items = ArchiveTargets(c);
            var why = archives is null ? "Archives are not available yet." : Archives.ArchiveNaming.WhyNotExtract(items);
            if (archives is null || why is not null)
            {
                shell.ShowMessage(why!);
                return;
            }

            LocationPath? destination = items[0].Parent;
            if (pick)
            {
                destination = await shell.PickFolderAsync("Extract to");
            }

            if (destination is null)
            {
                return;
            }

            var outcome = await archives.ExtractAsync(items, destination, subfolder, ct);
            if (!outcome.Started)
            {
                shell.ShowMessage(outcome.Message);
            }
        }

        async Task RunCompress(CommandContext c, CancellationToken ct, bool ask)
        {
            var items = ArchiveTargets(c);
            var why = archives is null ? "Archives are not available yet." : Archives.ArchiveNaming.WhyNotCompress(items);
            if (archives is null || why is not null)
            {
                shell.ShowMessage(why!);
                return;
            }

            var choice = new Archives.CompressChoice(Archives.ArchiveNaming.Suggest(items, ".zip"), Contracts.ArchiveFormat.Zip, Contracts.ArchiveLevel.Normal, null);
            if (ask)
            {
                if (await shell.AskCompressAsync(choice.FileName) is not { } chosen)
                {
                    return;
                }

                choice = chosen with { FileName = Archives.ArchiveNaming.WithExtension(chosen.FileName, chosen.Format) };
            }

            var folder = items[0].Parent;
            if (folder is null)
            {
                return;
            }

            var outcome = await archives.CompressAsync(items, folder.Combine(choice.FileName), choice, ct);
            if (!outcome.Started)
            {
                shell.ShowMessage(outcome.Message);
            }
        }

        // Checksums (spec §15.4): of the item the command was run on, else the selection; a lone checksum file (.sha256, .md5, .sfv...) is verified.
        Add(CommandIds.ShowChecksum, "Checksums...", "Files", (c, _) => Run(() =>
        {
            var items = ArchiveTargets(c);
            if (items.Count == 0 || items.Any(i => i.Kind is not (LocationKind.Drive or LocationKind.Unc)))
            {
                shell.ShowMessage("Checksums can be made for files and folders on this PC and on Windows shares.");
                return;
            }

            var verify = items.All(i => Core.Hashing.ChecksumKinds.FromExtension(Path.GetExtension(i.Name)) is not null);
            shell.OpenChecksums(items, verify);
        }), c => Current() is not null);

        // Compare and sync (spec §15.5): this folder against the other pane of a split tab, else the person picks the second one.
        Add(CommandIds.SyncFolders, "Compare and sync folders...", "Files", (_, _) => Run(() =>
        {
            var tab = window.Tabs.Selected;
            var here = tab?.Focused.History.Current;
            var other = tab?.Other?.History.Current;
            static LocationPath? Usable(LocationPath? p) => p is { Kind: LocationKind.Drive or LocationKind.Unc } ? p : null;
            shell.OpenSync(Usable(here), Usable(other));
        }), _ => Current() is not null);

        Add(CommandIds.PaletteCommands, "Command palette", "Navigation", (_, _) => Run(() => shell.OpenPalette(Palette.PaletteMode.Commands)), shortcut: "Ctrl+Shift+P");
        Add(CommandIds.PaletteGoTo, "Go to...", "Navigation", (_, _) => Run(() => shell.OpenPalette(Palette.PaletteMode.GoTo)), shortcut: "Ctrl+P");

        bool CanArchive(CommandContext c) => archives is not null && Current() is not null;
        Add(CommandIds.ExtractHere, "Extract here", "Archives", (c, ct) => RunExtract(c, ct, subfolder: false, pick: false), CanArchive);
        Add(CommandIds.ExtractToFolder, "Extract to folder", "Archives", (c, ct) => RunExtract(c, ct, subfolder: true, pick: false), CanArchive);
        Add(CommandIds.ExtractTo, "Extract to...", "Archives", (c, ct) => RunExtract(c, ct, subfolder: false, pick: true), CanArchive);
        Add(CommandIds.Compress, "Compress", "Archives", (c, ct) => RunCompress(c, ct, ask: false), CanArchive);
        Add(CommandIds.CompressWith, "Compress with options...", "Archives", (c, ct) => RunCompress(c, ct, ask: true), CanArchive);

        // A contact sheet of the video the command was run on, or of the videos in the selection (spec §10.4).
        static bool IsVideo(FileEntry e) => e.Kind == FileEntryKind.File && Leviathan.Media.MediaFileKinds.IsVideo(e.Extension);
        Add(CommandIds.ContactSheet, "Contact sheet", "Media", (c, _) => Run(() =>
        {
            IReadOnlyList<FileEntry> videos = c.Parameter is FileEntry one ? (IsVideo(one) ? [one] : []) : [.. Current()!.Selection.SelectedEntries.Where(IsVideo)];
            if (videos.Count == 1)
            {
                shell.OpenContactSheet(videos[0]);
            }
            else if (videos.Count > 1)
            {
                shell.ExportContactSheets(videos);
            }
        }), c => Current() is not null && (c.Parameter is FileEntry entry ? IsVideo(entry) : Current()!.Selection.SelectedEntries.Any(IsVideo)));

        Add(CommandIds.ViewZoom, "Resize thumbnails", "View", (c, _) => Run(() => Current()!.ZoomThumbnails((int)c.Parameter!)),
            c => Current() is { Layout: Views.ViewLayout.Thumbnails } && c.Parameter is int);

        Add(CommandIds.ViewThumbnailSize, "Set thumbnail size", "View", (c, _) => Run(() => Current()!.SetThumbnailSize((int)c.Parameter!)),
            c => Current() is { Layout: Views.ViewLayout.Thumbnails } && c.Parameter is int);

        // Selection (spec §6.2, nothing selected).
        Add(CommandIds.SelectAll, "Select all", "Selection", (_, _) => Run(() => Current()!.Selection.SelectAll()), _ => Current() is not null, shortcut: "Ctrl+A");
        Add(CommandIds.SelectNone, "Select none", "Selection", (_, _) => Run(() => Current()!.Selection.Clear()), _ => Current() is not null, shortcut: "Ctrl+Shift+A");
        Add(CommandIds.SelectInvert, "Invert selection", "Selection", (_, _) => Run(() => Current()!.Selection.Invert()), _ => Current() is not null, shortcut: "Ctrl+I");
        Add(CommandIds.SelectByPattern, "Select by pattern", "Selection", (c, _) => Run(() =>
        {
            var matched = Current()!.Selection.SelectByPattern(c.Target!);
            shell.ShowMessage(matched == 1 ? "1 item matches" : $"{matched} items match");
        }), c => Current() is not null && !string.IsNullOrWhiteSpace(c.Target));

        // New menu (spec §6.1). Creating is file I/O, so it runs on the thread pool inside NewItemService.
        async Task CreateNew(string templateId, CancellationToken ct)
        {
            var tab = Current()!;
            try
            {
                var created = await newItems!.CreateAsync(templateId, tab.History.Current, ct);
                shell.ShowMessage($"Created {created.Name}");
                await tab.ReloadAndSelectAsync(created, ct);

                // New items open with the rename box focused (spec §6.1).
                if (renamer is not null && tab.Items.Rows.FirstOrDefault(r => r.Location == created) is { } row)
                {
                    tab.BeginRename(row);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or KeyNotFoundException)
            {
                shell.ShowMessage($"Could not create it: {ex.Message}");
            }
        }

        bool CanCreateHere() => newItems is not null && Current() is { } tab && (tab.History.Current.Kind is LocationKind.Drive or LocationKind.Unc || newItems.Remote?.Handles(tab.History.Current) == true);

        Add(CommandIds.NewFolder, "New folder", "New", (_, ct) => CreateNew(NewMenu.NewItemService.FolderTemplate.Id, ct), _ => CanCreateHere(), shortcut: "Ctrl+Shift+N", icon: "NewFolder");
        Add(CommandIds.NewItem, "New item", "New", (c, ct) => CreateNew(c.Target!, ct), c => CanCreateHere() && !string.IsNullOrEmpty(c.Target));

        // Inline rename (spec §8.4): F2 shows the box, the box's Enter / Tab / click-away runs RenameCommit.
        bool CanBulkRenameHere() => Current() is { } tab && tab.History.Current.Kind is LocationKind.Drive or LocationKind.Unc && tab.Selection.SelectedEntries.Count > 0;

        bool CanRenameHere() => renamer is not null && Current() is { } tab && tab.History.Current.Kind is LocationKind.Drive or LocationKind.Unc or LocationKind.Sftp or LocationKind.Ftp or LocationKind.Ftps && tab.RenameTarget() is not null;

        // F2 renames the one selected item in place; with several selected it opens the bulk-rename dialog (spec §15.3).
        Add(CommandIds.EditRename, "Rename", "Edit", (_, _) => Run(() =>
        {
            if (Current()!.RenameTarget() is { } one)
            {
                Current()!.BeginRename(one);
            }
            else
            {
                shell.BulkRename();
            }
        }), _ => CanRenameHere() || (CanBulkRenameHere() && Current()!.Selection.SelectedEntries.Count > 1), shortcut: "F2", icon: "Rename");
        Add(CommandIds.BulkRename, "Bulk rename…", "Edit", (_, _) => Run(shell.BulkRename), _ => CanBulkRenameHere(), shortcut: "Shift+F2");

        Add(CommandIds.RenameCommit, "Finish renaming", "Edit", async (c, ct) =>
        {
            var tab = Current()!;
            var location = tab.Renaming!;
            var text = c.Target?.Trim() ?? string.Empty;
            var goNext = c.Parameter is true;
            var renamed = location;
            if (text != location.Name)
            {
                if (Folders.RenameRules.Validate(text) is { } problem)
                {
                    // The box stays open so the name can be fixed.
                    shell.ShowMessage(problem);
                    return;
                }

                try
                {
                    renamed = await renamer!.RenameAsync(location, text, ct);
                    undo?.RecordRename(location.Value, renamed.Value);
                    shell.ShowMessage($"Renamed to {renamed.Name}");
                    await tab.ReloadAndSelectAsync(renamed, ct);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
                {
                    tab.CancelRename();
                    shell.ShowMessage($"Could not rename it: {ex.Message}");
                    return;
                }
            }

            tab.CancelRename();

            // Tab goes on to rename the next item (spec §8.4).
            if (goNext)
            {
                var rows = tab.Items.Rows;
                var at = -1;
                for (var i = 0; i < rows.Count; i++)
                {
                    if (rows[i].Location == renamed)
                    {
                        at = i;
                        break;
                    }
                }

                if (at >= 0 && at + 1 < rows.Count)
                {
                    tab.BeginRename(rows[at + 1]);
                }
            }
        }, c => renamer is not null && Current()?.Renaming is not null && c.Target is not null);

        // Quick move and copy targets (spec §15.6): nine slots, the same queued transfer as a drop, in every folder.
        async Task SendToQuickTarget(CommandContext c, int slot, Transfers.TransferMode mode, CancellationToken ct)
        {
            if (Current() is not { } tab)
            {
                return;
            }

            if (QuickTargets.FolderOf(quickTargets?.Slots ?? [], slot) is not { } destination)
            {
                shell.ShowMessage($"Quick target {slot} is not set. Open the folder you want and run \"Make this folder quick target {slot}\".");
                return;
            }

            var sources = new List<LocationPath>((c.Parameter is FileEntry one ? [one] : tab.Selection.SelectedEntries).Select(e => e.Location));
            var decision = Transfers.DropRules.Decide(sources, destination, mode == Transfers.TransferMode.Copy ? Transfers.DropModifiers.Ctrl : Transfers.DropModifiers.Shift);
            if (!decision.Allowed)
            {
                shell.ShowMessage(decision.Tooltip);
                return;
            }

            var outcome = await (transfers ?? new Transfers.UnavailableTransferService()).StartAsync(new Transfers.TransferRequest(sources, destination, decision.Mode), ct);
            shell.ShowMessage(outcome.Message);
        }

        bool HasSelection(CommandContext c) => c.Parameter is FileEntry || Current() is { Selection.Count: > 0 };
        for (var n = 1; n <= QuickTargets.Count; n++)
        {
            var slot = n;
            Add(QuickTargets.MoveId(slot), $"Move to quick target {slot}", "Files", (c, ct) => SendToQuickTarget(c, slot, Transfers.TransferMode.Move, ct), HasSelection, shortcut: $"Ctrl+Alt+{slot}");
            Add(QuickTargets.CopyId(slot), $"Copy to quick target {slot}", "Files", (c, ct) => SendToQuickTarget(c, slot, Transfers.TransferMode.Copy, ct), HasSelection, shortcut: $"Ctrl+Alt+Shift+{slot}");
            Add(QuickTargets.SetId(slot), $"Make this folder quick target {slot}", "Files", async (_, ct) =>
            {
                if (quickTargets is null || Current() is not { } tab)
                {
                    return;
                }

                var folder = tab.History.Current;
                if (!TerminalTargets.IsUsable(folder) && folder.Kind is not (LocationKind.Sftp or LocationKind.Ftp or LocationKind.Ftps))
                {
                    shell.ShowMessage("This location cannot be a quick target.");
                    return;
                }

                await quickTargets.SetAsync(slot, folder.Value, ct);
                shell.ShowMessage($"Quick target {slot} is now {folder.Value}.");
            }, _ => quickTargets is not null && Current() is not null);
        }

        // Open terminal here (spec §15.7): the selected folder, else the selected file's folder, else the folder shown; the terminal is the one
        // chosen in the settings, and Windows Terminal falls back to PowerShell when it is not installed.
        LocationPath? TerminalFolder(CommandContext c) =>
            Current() is { } tab ? TerminalTargets.FolderFor(c.Parameter is FileEntry one ? [one] : [.. tab.Selection.SelectedEntries], tab.History.Current) : null;
        Add(CommandIds.TerminalHere, "Open terminal here", "Files", async (c, ct) =>
        {
            if (TerminalFolder(c) is not { } folder)
            {
                return;
            }

            var wanted = terminal?.Invoke() ?? TerminalKind.WindowsTerminal;
            var launched = await launcher.OpenTerminalAsync(folder, wanted, ct);
            if (!launched.Started && !launched.Cancelled && TerminalTargets.FallbackFor(wanted) is { } fallback)
            {
                launched = await launcher.OpenTerminalAsync(folder, fallback, ct);
                if (launched.Started)
                {
                    shell.ShowMessage($"{TerminalTargets.Title(wanted)} could not be started, so {TerminalTargets.Title(fallback)} was opened instead.");
                    return;
                }
            }

            if (!launched.Started && !launched.Cancelled)
            {
                shell.ShowMessage(launched.Error ?? "The terminal could not be started.");
            }
        }, c => TerminalFolder(c) is not null, shortcut: "Ctrl+Alt+T");

        // Commands on the selected item; the bar passes the entry as the parameter (spec §6.2).
        async Task Launch(Task<LaunchResult> task, FileEntry entry)
        {
            var launched = await task;
            if (!launched.Started && !launched.Cancelled)
            {
                shell.ShowMessage(launched.Error ?? $"{entry.Name} could not be started.");
            }
        }

        Add(CommandIds.RunAsAdministrator, "Run as administrator", "Files", (c, ct) => Launch(launcher.RunAsAdministratorAsync(((FileEntry)c.Parameter!).Location, ct), (FileEntry)c.Parameter!), c => c.Parameter is FileEntry { Kind: FileEntryKind.File });
        Add(CommandIds.OpenWith, "Open with…", "Files", (c, ct) => Launch(launcher.ChooseAppAsync(((FileEntry)c.Parameter!).Location, ct), (FileEntry)c.Parameter!), c => c.Parameter is FileEntry { Kind: FileEntryKind.File });

        // From a menu or button the item comes with the call; from the keyboard it is the one selected item.
        Add(CommandIds.Properties, "Properties", "Files", (c, ct) =>
        {
            var entry = c.Parameter as FileEntry ?? Current()!.RenameTarget()!;
            return Launch(launcher.ShowPropertiesAsync(entry.Location, ct), entry);
        }, c => c.Parameter is FileEntry || Current()?.RenameTarget() is not null, shortcut: "Alt+Enter");

        Add(CommandIds.FavoriteAdd, "Add to favorites", "Favorites", (c, _) => Run(() =>
        {
            var location = LocationPath.Parse(c.Target!);
            if (favorites!.Contains(location))
            {
                shell.ShowMessage($"{location.Name} is already in Favorites");
            }
            else
            {
                favorites.Toggle(location);
                shell.ShowMessage("Added to Favorites");
            }
        }), c => favorites is not null && LocationPath.TryParse(c.Target, out _));

        // Delete (spec §12.5): Del recycles, Shift+Del deletes for good after a confirmation.
        bool CanDeleteHere() => deleter is not null && Current() is { } tab && tab.History.Current.Kind is LocationKind.Drive or LocationKind.Unc or LocationKind.Sftp or LocationKind.Ftp or LocationKind.Ftps && tab.Selection.Count > 0;

        async Task DeleteSelection(bool permanent, CancellationToken ct)
        {
            var tab = Current()!;
            var items = tab.Selection.SelectedEntries.Select(e => e.Location).ToList();
            var what = items.Count == 1 ? items[0].Name : $"these {items.Count} items";

            // Plain Delete on a drive with no Recycle Bin (a USB stick) is permanent, so it is confirmed like Shift+Del (spec §12.5).
            var noBin = !permanent && await deleter!.BehaviorAsync(items, ct) == Core.Platform.DeleteBehavior.PermanentAfterConfirmation;
            if (permanent || noBin)
            {
                var why = noBin ? (items[0].Kind is LocationKind.Sftp or LocationKind.Ftp or LocationKind.Ftps ? "A server has no Recycle Bin. " : "This drive has no Recycle Bin. ") : string.Empty;
                if (!await shell.ConfirmAsync("Delete permanently?", $"{why}{what} will be deleted for good and cannot be restored.", "Delete"))
                {
                    return;
                }

                permanent = true;
            }

            shell.ShowMessage((await deleter!.DeleteAsync(items, permanent, ct)).Message);
        }

        Add(CommandIds.EditDelete, "Delete", "Edit", (_, ct) => DeleteSelection(false, ct), _ => CanDeleteHere(), shortcut: "Del", icon: "Delete");
        Add(CommandIds.EditDeletePermanent, "Delete permanently", "Edit", (_, ct) => DeleteSelection(true, ct), _ => CanDeleteHere(), shortcut: "Shift+Del");

        // Clipboard (spec §6.1): cut and copy put the selection on the Windows clipboard, paste hands it to the transfer service,
        // so a paste is the same journaled copy or move a drop is. Pasting a cut moves; pasting a copy copies, even in the same volume.
        bool InFolder() => Current() is { } tab && tab.History.Current.Kind is LocationKind.Drive or LocationKind.Unc or LocationKind.Sftp or LocationKind.Ftp or LocationKind.Ftps;
        bool CanTakeSelection() => clipboard is not null && InFolder() && Current()!.Selection.Count > 0;
        bool CanPasteHere() => clipboard is { HasFiles: true } && InFolder();

        Task PutOnClipboard(Transfers.ClipboardOperation operation)
        {
            var items = Current()!.Selection.SelectedEntries.Select(e => e.Location).ToList();
            clipboard!.Set(items, operation);
            var what = items.Count == 1 ? items[0].Name : $"{items.Count} items";
            shell.ShowMessage(operation == Transfers.ClipboardOperation.Cut ? $"Cut {what}. Paste to move." : $"Copied {what}.");
            return Task.CompletedTask;
        }

        async Task PasteAsync(bool intoNewFolder, CancellationToken ct)
        {
            var tab = Current()!;
            var here = tab.History.Current;
            var contents = await clipboard!.GetAsync(ct);
            if (contents is not { Items.Count: > 0 })
            {
                shell.ShowMessage("There are no files on the clipboard to paste.");
                return;
            }

            var cut = contents.Operation == Transfers.ClipboardOperation.Cut;
            var modifiers = cut ? Transfers.DropModifiers.Shift : Transfers.DropModifiers.Ctrl;

            // Judge before creating anything, so a refused paste does not leave an empty folder behind.
            var decision = Transfers.DropRules.Decide(contents.Items, intoNewFolder ? here.Combine("New folder") : here, modifiers);
            if (!decision.Allowed)
            {
                shell.ShowMessage(decision.Tooltip);
                return;
            }

            var destination = here;
            if (intoNewFolder)
            {
                try
                {
                    destination = await newItems!.CreateAsync(NewMenu.NewItemService.FolderTemplate.Id, here, ct);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or KeyNotFoundException)
                {
                    shell.ShowMessage($"Could not create the folder: {ex.Message}");
                    return;
                }
            }

            var outcome = await (transfers ?? new Transfers.UnavailableTransferService())
                .StartAsync(new Transfers.TransferRequest(contents.Items, destination, decision.Mode), ct);
            shell.ShowMessage(outcome.Message);
            if (outcome.Started && cut)
            {
                // Like Explorer: a cut can be pasted once; the items are on their way and the clipboard no longer offers them.
                clipboard.Clear();
            }
        }

        Add(CommandIds.EditCut, "Cut", "Edit", (_, _) => PutOnClipboard(Transfers.ClipboardOperation.Cut), _ => CanTakeSelection(), shortcut: "Ctrl+X", icon: "Cut");
        Add(CommandIds.EditCopy, "Copy", "Edit", (_, _) => PutOnClipboard(Transfers.ClipboardOperation.Copy), _ => CanTakeSelection(), shortcut: "Ctrl+C", icon: "Copy");
        Add(CommandIds.EditPaste, "Paste", "Edit", (_, ct) => PasteAsync(false, ct), _ => CanPasteHere(), shortcut: "Ctrl+V", icon: "Paste");
        Add(CommandIds.EditPasteIntoNewFolder, "Paste into new folder", "Edit", (_, ct) => PasteAsync(true, ct), _ => CanPasteHere() && newItems is not null);

        // Undo and redo (spec §12.5): one history for the whole app. The view shows the result: the restored item is selected.
        async Task ShowUndoOutcome(Undo.UndoOutcome outcome, CancellationToken ct)
        {
            shell.ShowMessage(outcome.Message);
            if (Current() is { } changed)
            {
                // Items came back or went away in other folders too: whatever the cache holds for them is out of date.
                foreach (var touched in outcome.Touched.Select(LocationPath.ParseExact))
                {
                    changed.Folder.Cache?.Invalidate(touched, includeDescendants: true);
                    if (touched.Parent is { } parent)
                    {
                        changed.Folder.Cache?.Invalidate(parent);
                    }
                }
            }

            if (Current() is { } tab)
            {
                var here = tab.History.Current;
                var inHere = outcome.Touched.Select(LocationPath.ParseExact).Where(p => p.Parent == here).ToList();
                if (inHere.Count > 0)
                {
                    await tab.ReloadAndSelectAsync(inHere[0], ct);
                }
            }
        }

        Add(CommandIds.EditUndo, "Undo", "Edit", async (_, ct) => await ShowUndoOutcome(await undo!.UndoAsync(ct), ct), _ => undo is { CanUndo: true }, shortcut: "Ctrl+Z", icon: "Undo");
        Add(CommandIds.EditRedo, "Redo", "Edit", async (_, ct) => await ShowUndoOutcome(await undo!.RedoAsync(ct), ct), _ => undo is { CanRedo: true }, shortcut: "Ctrl+Y", icon: "Redo");

        // Fold or unfold the summary above every pane (spec §8.7); one choice for all panes, remembered.
        Add(CommandIds.DriveCardToggle, "Fold or unfold the drive and folder summary", "View",
            (_, _) => Run(() => driveCards!.SetCollapsed(!driveCards.IsCollapsed)), _ => driveCards is not null);

        // Dropped items (spec §8.4): the rules decide, the transfer service does the work. A refused drop says why.
        Add(CommandIds.DropItems, "Drop items here", "Files", async (c, ct) =>
        {
            var drop = (ItemDrop)c.Parameter!;
            var destination = LocationPath.Parse(c.Target!);
            var decision = Transfers.DropRules.Decide(drop.Sources, destination, drop.Modifiers);
            if (!decision.Allowed)
            {
                shell.ShowMessage(decision.Tooltip);
                return;
            }

            var outcome = await (transfers ?? new Transfers.UnavailableTransferService())
                .StartAsync(new Transfers.TransferRequest(drop.Sources, destination, decision.Mode), ct);
            shell.ShowMessage(outcome.Message);
        }, c => c.Parameter is ItemDrop { Sources.Count: > 0 } && LocationPath.TryParse(c.Target, out _));

        // Sort, group, filter (spec §6.3).
        Add(CommandIds.SortBy, "Sort by", "View", (c, _) => Run(() => Current()!.SetSort(c.Target!)),
            c => Current() is not null && Folders.FileColumns.TryGet(c.Target ?? string.Empty, out _));
        Add(CommandIds.SortDirection, "Sort direction", "View", (c, _) => Run(() =>
        {
            var tab = Current()!;
            tab.SetSort(tab.Items.Sort[0].Column, c.Target!.Equals("desc", StringComparison.OrdinalIgnoreCase) ? Views.SortDirection.Descending : Views.SortDirection.Ascending);
        }), c => Current() is not null && c.Target is not null && (c.Target.Equals("asc", StringComparison.OrdinalIgnoreCase) || c.Target.Equals("desc", StringComparison.OrdinalIgnoreCase)));
        Add(CommandIds.GroupBy, "Group by", "View", (c, _) => Run(() => Current()!.SetGroupBy(string.IsNullOrEmpty(c.Target) ? null : c.Target)),
            c => Current() is not null && (string.IsNullOrEmpty(c.Target) || Folders.FileColumns.TryGet(c.Target, out var column) && column.Group is not null));
        Add(CommandIds.FolderPlacement, "Folders position", "View", (c, _) => Run(() => Current()!.SetFolderPlacement(Enum.Parse<Views.FolderPlacement>(c.Target!, ignoreCase: true))),
            c => Current() is not null && Enum.TryParse<Views.FolderPlacement>(c.Target, ignoreCase: true, out _));
        Add(CommandIds.FilterSet, "Filter this folder", "View", (c, _) => Run(() => Current()!.SetFilter(c.Target)), _ => Current() is not null);
        Add(CommandIds.FilterFocus, "Focus filter box", "View", (_, _) => Run(shell.FocusFilterBox), shortcut: "Ctrl+Shift+F");
        Add(CommandIds.SearchFocus, "Search", "Tools", (_, _) => Run(shell.FocusSearchBox), shortcut: "Ctrl+F");
        Add(CommandIds.SearchRun, "Search this folder and below", "Tools", async (c, ct) =>
        {
            var plan = Searches.SearchStarter.Plan(c.Target, Current()!.History.Current, viewOptions?.ShowHidden == true);
            if (plan.Results is null)
            {
                shell.ShowMessage(plan.Message ?? "That search could not be started.");
                return;
            }

            await Current()!.NavigateAsync(plan.Results, ct);
        }, _ => Current() is not null);

        Add(CommandIds.ViewHidden, "Show hidden items", "View", async (_, ct) =>
        {
            viewOptions!.ShowHidden = !viewOptions.ShowHidden;
            await Current()!.ReloadAsync(ct);
        }, _ => viewOptions is not null && Current() is not null, shortcut: "Ctrl+H");

        Add(CommandIds.ViewColumns, "Choose columns", "View", (c, _) => Run(() =>
        {
            var choice = (Folders.ColumnChoice)c.Parameter!;
            Current()!.ApplyColumns(choice.Ids, choice.ForAllOfType);
        }), c => Current() is not null && c.Parameter is Folders.ColumnChoice);
        Add(CommandIds.SettingsOpen, "Settings", "View", (c, _) => Run(() => window.SettingsSheet.Toggle(c.Parameter as string)), shortcut: "Ctrl+,");
        Add(CommandIds.HubToggle, "Status hub", "View", (c, _) => Run(() => window.Hub.Toggle(c.Parameter as Hub.HubTab?)), shortcut: "Ctrl+Shift+O");
        Add(CommandIds.ViewSidebar, "Sidebar", "View", (_, _) => Run(() => window.SidebarCollapsed = !window.SidebarCollapsed), shortcut: "Ctrl+B");
        Add(CommandIds.PreviewPane, "Preview pane", "View", (_, _) => Run(() => window.ShowPreview = !window.ShowPreview), shortcut: "Alt+P");
        // Alt+T, not the spec's plain T: a bare letter is type-ahead in the file list and would be eaten in the filter and address boxes.
        Add(CommandIds.TriageFromHere, "Triage mode from here", "Media", (_, _) => Run(shell.StartTriage), _ => Current() is not null, shortcut: "Alt+T");
        Add(CommandIds.TriageInbox, "Triage an inbox…", "Media", (_, _) => Run(shell.StartInboxTriage), _ => Current() is not null, shortcut: "Alt+Shift+T");
        Add(CommandIds.FindDuplicates, "Find duplicates…", "Tools", (_, _) => Run(shell.FindDuplicates), _ => Current() is not null, shortcut: "Alt+Shift+D");
        Add(CommandIds.FindDuplicateFiles, "Find duplicate files by contents…", "Tools", (_, _) => Run(shell.FindDuplicateFiles), _ => Current() is not null, shortcut: "Alt+Shift+F");
        Add(CommandIds.PreviewQuick, "Quick preview", "View", (_, _) => Run(shell.ToggleQuickPreview), shortcut: "Space");
        Add(CommandIds.PreviewVideoSmall, "Preview video: small", "View", (_, _) => Run(() => window.SetVideoSize(VideoSize.Small)));
        Add(CommandIds.PreviewVideoMedium, "Preview video: medium", "View", (_, _) => Run(() => window.SetVideoSize(VideoSize.Medium)));
        Add(CommandIds.PreviewVideoLarge, "Preview video: large", "View", (_, _) => Run(() => window.SetVideoSize(VideoSize.Large)));

        // Split panes (spec §15.1, §6.3). The same key again closes the split, which keeps the pane that has the focus.
        Add(CommandIds.PaneSingle, "One pane", "View", (c, ct) => TabOf(c)!.SetPanesAsync(PaneLayout.Single, ct), c => TabOf(c) is not null);
        Add(CommandIds.PaneSideBySide, "Split side by side", "View",
            (c, ct) => TabOf(c)!.SetPanesAsync(TabOf(c)!.Panes == PaneLayout.SideBySide ? PaneLayout.Single : PaneLayout.SideBySide, ct),
            c => TabOf(c) is not null, shortcut: @"Ctrl+\");
        Add(CommandIds.PaneTopBottom, "Split top and bottom", "View",
            (c, ct) => TabOf(c)!.SetPanesAsync(TabOf(c)!.Panes == PaneLayout.TopBottom ? PaneLayout.Single : PaneLayout.TopBottom, ct),
            c => TabOf(c) is not null, shortcut: @"Ctrl+Shift+\");
        // Two panes work together (spec §15.1): F5 and F6 send the selection to the other pane's folder through the normal queue.
        async Task SendToOtherPane(CommandContext c, Transfers.TransferMode mode, CancellationToken ct)
        {
            if (TabOf(c) is not { Other: { } other } tab)
            {
                return;
            }

            var sources = new List<LocationPath>(tab.Focused.Selection.SelectedEntries.Select(e => e.Location));
            var destination = other.History.Current;

            // The same rules as a drop with Ctrl (copy) or Shift (move) held: refuses a folder into itself, an item already there, a server root.
            var decision = Transfers.DropRules.Decide(sources, destination, mode == Transfers.TransferMode.Copy ? Transfers.DropModifiers.Ctrl : Transfers.DropModifiers.Shift);
            if (!decision.Allowed)
            {
                shell.ShowMessage(decision.Tooltip);
                return;
            }

            var outcome = await (transfers ?? new Transfers.UnavailableTransferService())
                .StartAsync(new Transfers.TransferRequest(sources, destination, decision.Mode), ct);
            shell.ShowMessage(outcome.Message);
        }

        static bool CanSendToOtherPane(TabViewModel? tab) => tab is { IsSplit: true } && tab.Focused.Selection.Count > 0;

        Add(CommandIds.PaneCopyToOther, "Copy to the other pane", "Files", (c, ct) => SendToOtherPane(c, Transfers.TransferMode.Copy, ct),
            c => CanSendToOtherPane(TabOf(c)), shortcut: "F5");
        Add(CommandIds.PaneMoveToOther, "Move to the other pane", "Files", (c, ct) => SendToOtherPane(c, Transfers.TransferMode.Move, ct),
            c => CanSendToOtherPane(TabOf(c)), shortcut: "F6");
        Add(CommandIds.PaneSwap, "Swap the panes", "View", (c, ct) => TabOf(c)!.SwapPanesAsync(ct), c => TabOf(c) is { IsSplit: true }, shortcut: "Ctrl+Shift+S");
        Add(CommandIds.PaneSameFolder, "Show this folder in the other pane", "View",
            (c, ct) => TabOf(c)!.Other!.NavigateAsync(TabOf(c)!.Focused.History.Current, ct), c => TabOf(c) is { IsSplit: true }, shortcut: "Alt+=");

        Add(CommandIds.OpenInSplitPane, "Open in split pane", "Navigation", async (c, ct) =>
        {
            // The folder opens in the pane that does not have the focus; a tab with one pane splits first.
            if (TabOf(c) is not { } tab || !LocationPath.TryParse(c.Target, out var folder))
            {
                return;
            }

            if (!tab.IsSplit)
            {
                await tab.SetPanesAsync(PaneLayout.SideBySide, ct);
            }

            if (tab.Other is { } other)
            {
                await other.NavigateAsync(folder, ct);
            }
        }, c => TabOf(c) is not null && LocationPath.TryParse(c.Target, out _));

        Add(CommandIds.FavoriteRename, "Rename favorite", "Favorites", (c, _) => Run(() => favorites!.Rename(LocationPath.Parse(c.Target!), c.Parameter as string)),
            c => favorites is not null && LocationPath.TryParse(c.Target, out var l) && favorites.Contains(l));
        Add(CommandIds.FavoriteStyle, "Change favorite icon and colour", "Favorites", (c, _) => Run(() =>
        {
            var change = (Sidebar.FavoriteStyleChange)c.Parameter!;
            favorites!.SetStyle(LocationPath.Parse(c.Target!), change.Icon, change.Color);
        }), c => favorites is not null && c.Parameter is Sidebar.FavoriteStyleChange && LocationPath.TryParse(c.Target, out var l) && favorites.Contains(l));
        Add(CommandIds.FavoriteToggle, "Pin or unpin folder", "Favorites", (_, _) => Run(() =>
        {
            var pinned = favorites!.Toggle(Current()!.History.Current);
            shell.ShowMessage(pinned ? "Added to Favorites" : "Removed from Favorites");
        }), _ => favorites is not null && Current() is not null, shortcut: "Ctrl+D");
        Add(CommandIds.SpaceMap, "Space map", "Tools", (c, _) => Run(() =>
        {
            var tab = Current()!;
            LocationPath? folder = null;
            if (LocationPath.TryParse(c.Target, out var named))
            {
                folder = named;
            }
            else if (tab.Selection.SelectedEntries is { Count: 1 } one && one[0].Kind == FileEntryKind.Folder)
            {
                folder = one[0].Location;
            }
            else
            {
                folder = tab.History.Current;
            }

            if (folder.Kind is LocationKind.Drive or LocationKind.Unc && !(folder.Kind == LocationKind.Unc && folder.Share is null))
            {
                shell.OpenSpaceMap(folder);
            }
            else
            {
                shell.ShowMessage("Open a folder, a drive or a share first: the space map shows what is in it.");
            }
        }), _ => Current() is not null, shortcut: "Ctrl+Shift+M");
        Add(CommandIds.SearchIndexToggle, "Index this folder for search (or stop)", "Tools", (_, _) => Run(() =>
        {
            var here = Current()!.History.Current;
            shell.ShowMessage(shell.ToggleSearchIndex(here)
                ? "This folder is now kept in the search index. It is listed in the background, a little at a time; see Background in the status hub."
                : "This folder is no longer kept in the search index.");
        }), _ => Current() is { } tab && tab.History.Current.Kind is LocationKind.Drive or LocationKind.Unc);
        Add(CommandIds.SearchSave, "Save this search to Favorites", "Tools", (_, _) => Run(() =>
        {
            var here = Current()!.History.Current;
            if (favorites!.Contains(here))
            {
                shell.ShowMessage("This search is already in Favorites.");
                return;
            }

            favorites.Toggle(here);
            shell.ShowMessage("Search saved to Favorites. It runs again each time you open it; right-click it to rename it.");
        }), _ => favorites is not null && Current() is { } tab && Search.SearchLocations.IsSearchLocation(tab.History.Current));

        Add(CommandIds.AddressFocus, "Focus address bar", "Navigation", (_, _) => Run(shell.FocusAddressBar), shortcut: "Ctrl+L");
        Add(CommandIds.AppDiagnostics, "Diagnostics", "App", (_, _) => Run(shell.OpenDiagnostics));

        return registry;
    }

    /// <summary>The key bindings out of the box (spec §17.3). Users change them in the settings file.</summary>
    public static KeyMap DefaultKeyMap()
    {
        var map = new KeyMap();
        foreach (var (gesture, command) in new (string, CommandId)[]
        {
            ("Ctrl+T", CommandIds.TabNew),
            ("Ctrl+W", CommandIds.TabClose),
            ("Ctrl+Shift+T", CommandIds.TabReopen),
            ("Ctrl+Shift+K", CommandIds.TabDuplicate),
            ("Ctrl+Tab", CommandIds.TabNext),
            ("Ctrl+Shift+Tab", CommandIds.TabPrevious),
            ("Ctrl+N", CommandIds.WindowNew),
            ("Alt+Left", CommandIds.NavBack),
            ("Alt+Right", CommandIds.NavForward),
            ("Alt+Up", CommandIds.NavUp),
            ("F5", CommandIds.NavReload),
            ("Ctrl+R", CommandIds.NavReload),
            ("Ctrl+L", CommandIds.AddressFocus),
            ("Alt+D", CommandIds.AddressFocus),
            ("Alt+T", CommandIds.TriageFromHere),
            ("Alt+Shift+T", CommandIds.TriageInbox),
            ("Alt+Shift+D", CommandIds.FindDuplicates),
            ("Alt+Shift+F", CommandIds.FindDuplicateFiles),
            ("Ctrl+Shift+PageUp", CommandIds.TabMoveLeft),
            ("Ctrl+Shift+PageDown", CommandIds.TabMoveRight),
            ("Ctrl+D", CommandIds.FavoriteToggle),
            ("Ctrl+Alt+T", CommandIds.TerminalHere),
            ("Ctrl+Alt+1", QuickTargets.MoveId(1)), ("Ctrl+Alt+2", QuickTargets.MoveId(2)), ("Ctrl+Alt+3", QuickTargets.MoveId(3)),
            ("Ctrl+Alt+4", QuickTargets.MoveId(4)), ("Ctrl+Alt+5", QuickTargets.MoveId(5)), ("Ctrl+Alt+6", QuickTargets.MoveId(6)),
            ("Ctrl+Alt+7", QuickTargets.MoveId(7)), ("Ctrl+Alt+8", QuickTargets.MoveId(8)), ("Ctrl+Alt+9", QuickTargets.MoveId(9)),
            ("Ctrl+Alt+Shift+1", QuickTargets.CopyId(1)), ("Ctrl+Alt+Shift+2", QuickTargets.CopyId(2)), ("Ctrl+Alt+Shift+3", QuickTargets.CopyId(3)),
            ("Ctrl+Alt+Shift+4", QuickTargets.CopyId(4)), ("Ctrl+Alt+Shift+5", QuickTargets.CopyId(5)), ("Ctrl+Alt+Shift+6", QuickTargets.CopyId(6)),
            ("Ctrl+Alt+Shift+7", QuickTargets.CopyId(7)), ("Ctrl+Alt+Shift+8", QuickTargets.CopyId(8)), ("Ctrl+Alt+Shift+9", QuickTargets.CopyId(9)),
            ("Ctrl+Shift+P", CommandIds.PaletteCommands),
            ("Ctrl+P", CommandIds.PaletteGoTo),
            ("Ctrl+A", CommandIds.SelectAll),
            ("Ctrl+I", CommandIds.SelectInvert),
            ("Ctrl+Shift+A", CommandIds.SelectNone),
            ("Ctrl+H", CommandIds.ViewHidden),
            ("Alt+Enter", CommandIds.Properties),
            ("Ctrl+Shift+N", CommandIds.NewFolder),
            ("Ctrl+Shift+F", CommandIds.FilterFocus),
            ("Ctrl+F", CommandIds.SearchFocus),
            ("Ctrl+Shift+M", CommandIds.SpaceMap),
            ("Del", CommandIds.EditDelete),
            ("Shift+Del", CommandIds.EditDeletePermanent),
            ("Ctrl+X", CommandIds.EditCut),
            ("Ctrl+C", CommandIds.EditCopy),
            ("Ctrl+V", CommandIds.EditPaste),
            ("Ctrl+Z", CommandIds.EditUndo),
            ("Ctrl+Y", CommandIds.EditRedo),
            ("Ctrl+Shift+Z", CommandIds.EditRedo),
            ("Alt+P", CommandIds.PreviewPane),
            (@"Ctrl+\", CommandIds.PaneSideBySide),
            (@"Ctrl+Shift+\", CommandIds.PaneTopBottom),
            ("Ctrl+B", CommandIds.ViewSidebar),
            ("Ctrl+Shift+O", CommandIds.HubToggle),
            ("Ctrl+,", CommandIds.SettingsOpen),
            ("F2", CommandIds.EditRename),
            ("Shift+F2", CommandIds.BulkRename),
            ("Ctrl+Shift+6", CommandIds.ViewDetails),
            ("Ctrl+Shift+5", CommandIds.ViewList),
            ("Ctrl+Shift+2", CommandIds.ViewThumbnails),
            ("Ctrl+Shift+4", CommandIds.ViewTiles),
            ("Ctrl+Shift+1", CommandIds.ViewGallery),
            ("Ctrl+Shift+S", CommandIds.PaneSwap),
            ("Alt+=", CommandIds.PaneSameFolder),
        })
        {
            map.TryBind(new KeyBinding(KeyGesture.Parse(gesture), command));
        }

        // F5 and F6 mean reload in one pane and copy or move to the other pane while split; the split meaning wins only then (spec §17.3).
        map.TryBind(new KeyBinding(KeyGesture.Parse("F5"), CommandIds.PaneCopyToOther, KeyScope.SplitOnly));
        map.TryBind(new KeyBinding(KeyGesture.Parse("F6"), CommandIds.PaneMoveToOther, KeyScope.SplitOnly));

        return map;
    }

    private const int MaxTabLabel = 60;

    [GeneratedRegex("^#[0-9a-fA-F]{6}$", RegexOptions.CultureInvariant)]
    private static partial Regex HexColor();

    /// <summary>Runs a command nobody awaits (an event handler): expected failures reach the status bar, anything else is a bug and surfaces.</summary>
    private static async Task ReportFailures(Task<CommandResult> task, IShellActions shell)
    {
        try
        {
            await task;
        }
        catch (LocationException ex)
        {
            shell.ShowMessage(ex.Message);
        }
    }

    private static Task Run(Action action)
    {
        action();
        return Task.CompletedTask;
    }
}
