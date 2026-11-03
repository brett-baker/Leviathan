# Phase 1 plan — Core browser

Scope, spec sections and gate are in `docs/build-plan.md` (Phase 1). This file is the working checklist: each slice is small
enough to write, test, commit and push in one sitting, in dependency order. Tick a slice when it is committed and pushed.

Every slice follows `CLAUDE.md`: code and tests together, zero warnings in `src/`, whole suite green, app run by hand when
the slice touches UI or process behaviour, commit, push.

## Shape of the work

New projects (all in `Leviathan.slnx`, each with a test project where it holds logic):

| Project | Kind | Holds |
| --- | --- | --- |
| `Leviathan.ViewModels` | plain `net10.0` | Command layer, tab/window/navigation/folder/sidebar view models, settings, profiles. No WinUI references, so everything here is unit-tested. |
| `Leviathan.Controls` | WinUI 3 | Breadcrumb bar, virtualized file grid, tab strip, status bar controls (spec §2.3). |
| `Leviathan.Interop` | Windows, CsWin32 | Typed Win32/COM: known folders, drive info, shell images, `IFileOperation`, `ShellNew` (ported from Files, spec §18). |
| `Leviathan.ShellHost` | process | Third-party context-menu handlers, hosted out of process, STA pool. |
| `Leviathan.ViewModels.Tests`, `Leviathan.ShellHost.Tests`, `Leviathan.UI.Tests` | tests | View-model unit tests, crash isolation, FlaUI smoke. |

Existing pieces reused as they are: `LocationPath` (parse, resolve, breadcrumbs), `ILocationProvider` streaming batches,
`NaturalStringComparer`, `ShareHealthTracker`, `ActionTrail`, `UiStallWatchdog`, `FolderSizeService`, `WorkerClient`.

## Slices

### A. Foundations

- [x] **A1. Scaffolding.** Create `Leviathan.ViewModels` + `.Tests` and `Leviathan.Interop`; add to the solution; CsWin32 pinned
  in `Directory.Packages.props`. `Leviathan.Controls` is created in D2, when its first control exists (no empty WinUI library).
- [x] **A2. Command layer** (§6, §17.3). `CommandId`, `ICommand` metadata (title, icon, default shortcut, enabled predicate),
  `CommandRegistry`, key-gesture parsing and a `KeyMap` with conflict rules (split-pane meaning wins only while split).
  Every execution is recorded in `ActionTrail`; the trail replays through `leviathan-diag replay`. Palette-ready search over titles.
- [x] **A3. Settings store** (§17.2). Typed JSON settings, atomic write (temp → flush → rename), daily backup, change events so
  settings apply without restart. Goes through `IFileSystem`.
- [x] **A4. Per-folder view settings** (§8.1). Layout, sort, group, columns stored per folder with inheritance
  ("this folder", "this folder and subfolders", "all folders of this type"); resolution order tested.

### B. Navigation and folder model

- [x] **B1. Navigation history.** Back/forward/up stacks, history lists for the right-click menus, "select the folder you came
  from" after Back/Up (§5.1).
- [x] **B2. Address bar model** (§5.2). Breadcrumb segments with sibling lists, edit mode, `LocationPath.TryResolve` for typed
  input (env vars, `shell:`, relative, file path → folder + select), copy-as variants (path, UNC, quoted, `file://`),
  autocomplete from file system, history and favorites, overflow collapse.
- [x] **B3. Folder view model, listing.** Streams `ListAsync` batches into a collection, marshalled every 16–50 ms; navigating
  away cancels the old listing; states Loading / Empty / AccessDenied / Offline / Error; a cancelled listing never looks empty (§8.6).
- [x] **B4. Sort, group, filter** (§8.3, §6.3). Natural sort, secondary sort, folders first/mixed/last, group with collapsible
  groups, filter box syntax (`*.mp4`, `size:>1GB`, `tag:red`, `duration:<2m`; tag and duration parse now, match when Phase 3 data exists).
- [x] **B5. Selection model** (§8.4). Single, ctrl/shift range, invert, select by pattern, type-ahead jump, rubber-band hit-test
  math, selection survives re-sort and batch arrival.
- [x] **B6. Tabs** (§4.1). `TabViewModel` (path, history, view, sort, selection), new/duplicate/close, reopen stack of 25,
  reorder, pin, lock, colour/rename, close others/right, overflow list.
- [x] **B7. Windows** (§4.2). `WindowViewModel`/`WindowManager`: several windows in one process, tear-off, merge, saved bounds with
  a monitor-gone fallback.

### C. Windows interop and services

- [x] **C1. Interop base.** CsWin32 setup ported from `Files.App.CsWin32` (licence headers checked, `THIRD-PARTY-NOTICES.md` updated).
- [x] **C2. Known folders and drives.** Known Folder IDs for Common folders (relocated Documents works), drive list with
  type/label/free space, SSD vs HDD, mapped-drive UNC; behind `IKnownFolders` / `IVolumeInfo` with fakes.
- [x] **C3. Cloud drive detection** (Internet section). Port `Utils/Cloud`; behind `ICloudProviderCatalog` with a fake.
- [x] **C4. Thumbnails.** `IThumbnailCache` over `IShellItemImageFactory`, off the UI thread, cancellable, size-capped LRU,
  requests for off-screen items cancelled (§3.1: < 200 ms cached, < 1 s generated).
- [x] **C5. Launch and file associations.** Launcher (open, run as administrator, open with, system app chooser, terminal here, properties) and
  default-app lookup. Files' `OpenWithMenu` builds its app list from the shell context menu, so the "Open with" list arrives with the
  context-menu port in F2.
- [x] **C6. ShellNew and templates** (§6.1). `ShellNewCatalog` reads `ShellNew` registrations, built-in items and the user templates folder;
  `NewItemCreator` makes the item with Explorer-style numbering. The built-in Shortcut item waits for the target picker in D7.

### D. Shell UI

- [x] **D1. App shell.** Tab strip in the title bar (WinUI `TabView`, replaced by the custom control in D2), navigation row, sidebar, file list,
  preview placeholder, status bar, Mica; every button and shortcut goes through the window's command registry. The Phase 0 worker demo is
  now the Diagnostics window (Options). Placeholders: address text box (D3), plain list (D4), common folders and drives list (D6).
  Stalls: the 2 startup stalls were the window being built (~270 ms) and its first frame (~130 ms); the watchdog now starts once the first
  folder and sidebar are ready and stops when the last window starts closing. Navigating, scrolling and closing show none (checked with
  LEVIATHAN_UI_TRACE=<file>). Open: cold start measures 0.95 to 1.2 s against the 600 ms budget (spec 3.1); that is H2's work.
- [x] **D2. Tab strip** (§4.1): drag to reorder, drag out to a new window at the drop point (same size, kept on screen), drop onto another window's
  strip to merge, pin (icon only, left), lock, rename, colour, close others / to the right, middle-click close, lock/busy/offline marks, an
  all-tabs list with search, Ctrl+Shift+PgUp/PgDn, middle-click a folder to open it in a background tab. Built on WinUI's `TabView`, not a
  custom control. Not done: Ctrl+click a folder (conflicts with multi-select; decided in D8), double-click the empty strip (Windows uses it to
  maximize), dropping files on a tab (D9). Opening or closing a window stalls the UI ~110-130 ms (second window onward); lighter XAML in H3.
- [x] **D3. Breadcrumb address bar** (§5.2): segment buttons (middle-click or Ctrl+click opens a new tab), a chevron after each segment with its subfolders and
  type-to-filter, edit mode (click empty space, Ctrl+L, Alt+D) with autocomplete from the typed folder, recent folders and favorites, Tab to accept,
  Esc to leave, a "…" menu for segments that do not fit, right-click Copy path / UNC / quoted / file:// URI, and lock / warning / cloud icons.
  It is a `UserControl` in the app project, not yet in a separate `Leviathan.Controls` library. Not done: dropping files onto a segment (D9).
- [x] **D4. File view, Details.** `DetailsGrid` (`ItemsRepeater` over fixed-height `GridRow`s, recycled): header click sorts (Shift adds a secondary sort, arrow shows direction),
  drag a header edge to resize, double-click it to auto-fit, drag a header to reorder, right-click for the column list (Name cannot be hidden); widths, order and visible
  columns live in `ColumnLayout` and stay with the tab. Selection is the `SelectionModel` (click, Ctrl, Shift, arrows, Home/End, PageUp/PageDown, type-ahead, Enter opens).
  Skeleton rows while the first rows load; empty / denied / not found / offline states with a Try again button. Folder sizes: `FolderSizeTracker` asks `WorkerFolderSizes`,
  which reads the shared SQLite store and submits low-priority `ComputeFolderSize` jobs to the worker (so counting never runs in the UI process); values arrive as they finish,
  stale ones in italic. Found on the way: `LocationPath.Parse` expanded `%VARS%` in listed paths, so System32's folder `%userprofile%` could not be opened or sized; only typed
  addresses expand now. Not done: sorting by folder size (folders sort as 0 bytes), rubber-band and inline rename (D8), context menu (F), skeleton rows not seen on screen
  (local folders load too fast), Size column for network folders (not counted unasked).
- [x] **D5. List and Thumbnails layouts** (§8.1). `FlowView` (virtualizing `UniformGridLayout` of recycled `FlowCell`s) shows List (columns of names, top to bottom, scrolls sideways)
  and Thumbnails (wrapping grid, scrolls down) over the same rows and `SelectionModel` as the Details grid. Ctrl+Shift+6 / 5 / 2 switch layout (commands `view.details`, `view.list`,
  `view.thumbnails`; `Ctrl+Shift+digit` no longer selects a tab), Ctrl+wheel resizes thumbnails 64–512 px (`view.zoom`, saved with the tab's view, and in session state). Arrow keys, Page Up/Down,
  Home/End follow the layout's direction (`ItemFlow`, unit-tested: moving across a short last line lands on the last item). Thumbnails come from the shared `ThumbnailCache` over
  `ShellThumbnailSource` (request sizes bucketed so zooming does not regenerate; cancelled when a cell is recycled; network files get icons only). Found on the way: the shell serves
  cached thumbnails as bottom-up bitmaps, so they showed upside-down on the second run: `CopyPixels` now reads rows through `GetDIBits` with a negative height; and a click left the focus
  on the window's root scroll viewer, so arrow keys never reached the view (it takes the focus back after the click). Tiles and Gallery stay for Phase 3 (they need media data).
  Not done: thumbnails run in the UI process on two STA threads (the Media process is Phase 3), no layout switcher or size slider until D7/D10, folder thumbnails are the folder glyph,
  Shift+F5 does not yet invalidate the cache, type-ahead and rubber-band in the flow layouts follow in D8.
- [x] **D6. Sidebar tree** (§7, §7.2): `SidebarTree` (one flat list of rows built from sections, in the view-model project, unit-tested with a fake lister and `ManualTimeProvider`):
  Favorites (Ctrl+D pins or unpins the current folder, kept in `%LOCALAPPDATA%\Leviathan\favorites.json`, right-click to add, remove or rename), Common folders, Tags placeholder,
  Local drives with usage bars (red when low on space), Network (mapped drives), Internet (detected sync clients). Expanding shows a spinner row at once, lists with a 5 s timeout
  ("Not responding — Retry" or "Access denied — Retry"), shows arrows without checking and removes one that proves empty, and follows the current folder (local folders expand on
  the way down, network paths are never listed to follow the view). Left/Right expand and collapse, middle-click and the context menu open in a new tab. Found on the way: bulk row
  inserts stalled the list for 350 to 420 ms, so rows go in 40 at a time. Not done: one-level look-ahead (arrows are optimistic instead), drag onto the tree and onto Favorites (D9),
  Recycle Bin and Settings entries, saved servers and discovery (Phase 2), per-folder icons and colors for favorites, remembered expanded state, Ctrl+Alt+1–9 jumps.
- [x] **D7. Command bar** (§6): WinUI `CommandBar` (dynamic overflow) with New ▾ (built-in, ShellNew and user templates, Manage templates…; Ctrl+Shift+N makes a folder and selects it), the clipboard group (Cut, Copy, Paste split button, Rename, Delete, Undo, Redo: icon buttons with "Name (Shortcut)" tooltips, disabled until their commands land in D8/E3-E5), a context zone that follows the selection (`ContextActions` table of §6.2; only actions whose command exists are shown: Select all/none/invert/by pattern, New tab here, Open terminal here for nothing selected; Open in new tab, Add to favorites, Properties for a folder; Open/Play/Run, Run as administrator, Open with… for files), View ▾ (layouts, thumbnail size, show hidden, preview pane), Sort ▾, Group ▾, a debounced filter box (Ctrl+Shift+F, Esc clears, navigating clears), Options ⚙, and the pane group beside the address bar (one pane active, splits disabled, preview toggle Alt+P). New commands: select.*, new.folder/new.item, sort.by/direction/folders, group.by, filter.set, view.hidden, pane.preview, favorite.add, terminal.here, item.openWith/runAsAdmin/properties. Not done: Shortcut in the New menu (needs link creation in Interop), inline rename after New (D8), Show extensions, density, Tiles/Gallery (Phase 3), Paste-as variants (E5), pin/hide context actions per type, labels-under-icons setting, the rest of §6.2 (needs Phase 2/3 features), hidden-items switch reaches other tabs only when they next list.
- [x] **D8. Interaction** (§8.4): inline rename (`RenameBox`: F2 or the Rename button opens it with the name selected without its extension; Enter or a click away renames, Esc cancels, Tab renames and moves on to the next item; a refused name keeps the box open, a disk failure closes it with the reason; New items open with the box focused). Commands `edit.rename` and `edit.renameCommit`, `RenameService` and `RenameRules` (unit-tested on `MockFileSystem`, including case-only renames). Rubber band in Details, List and Thumbnails (`BandDrag` over `ItemFlow.IndexesIn`, only the cells under the band are visited; edge scrolling; Ctrl keeps the earlier selection). Keyboard: Space toggles the focused item, Esc clears the selection, Ctrl+A selects all; type-ahead was already there. Found on the way: both file views stay bound while only one is shown, and the hidden one threw from `GetOrCreateElement` when F2 was pressed in Details (crash signature 182fdb45375a, now guarded in `FlowView`). Ctrl+click folder decision in `docs/decisions.md`. Not done: rename by slow double-click, rubber band from the name or icon of a row (that is drag and drop, D9), undo of a rename (E4), group headings in Details (rows are ordered by group but no heading is drawn; to do).
- [x] **D9. Drag and drop** between tabs, windows, the tree, the breadcrumb and other apps, with the move/copy/link rules and the tooltip (§8.4).
  `DropRules.Decide` (pure, tested) picks move / copy / shortcut: move on the same volume, copy across volumes, Shift forces move, Ctrl copy, Alt shortcut; it refuses
  a folder onto itself or into its own subfolder, a move into the folder the items are already in, and a server that is not a share. The tooltip says which one will happen
  (and, for a cross-volume move, that the original is removed only after the copy is checked). Drag from the icon or name in Details, List and Thumbnails (a press on an item
  inside a larger selection takes the whole selection; the rubber band stays on blank space). Drop on a folder row / cell (highlighted), the empty area (the current folder), a tab
  (hovering 600 ms switches to it), a sidebar folder (hovering 700 ms expands it), a breadcrumb segment, and the Favorites header (pins). Files go out to other apps as storage items,
  made only when someone drops them; storage items dropped from other apps are read when they land. The drop runs the `item.drop` command, which calls `ITransferService`.
  **Not done:** the copy / move itself. `UnavailableTransferService` says "Copying files arrives with the next update. Nothing was changed." until E2 supplies the journaled
  implementation, so a drop never half-copies outside the journal. Also not done: dragging onto another window's tab strip for files (tabs between windows already worked), dropping
  from another app was not tried (only in-app drags were driven), reordering favorites by drag, drag-hover on folder rows in the file view (no spring-loading there, not in the spec).
- [x] **D10. Status bar** (§16) minus operations details: item count, selection size, filter state, tag summary placeholder,
  location health, free space, background work, layout switcher.
  Parts: item count (folders, files; click toggles hidden), selection and size (click selects none), filter state (click clears), location health for network folders,
  free space with a bar, layout switcher and thumbnail slider (new `view.thumbnailSize` command). Parts with nothing to say are collapsed. Not done: tag summary and
  background work (nothing to report until Phase 3 and the worker jobs), per-part hiding (settings, G3), latency in health (Phase 2), media total (Phase 3). One "UI stalls: 1"
  showed up while switching layouts in the drive test; to look at in H3.
- [x] **D11. Drive summary card** (§8.7): usage bar by file type from the folder-size store, facts, actions, hide per drive.
  Card above the list at the top of a drive or share: name, type, file system, SSD/HDD, usage bar with used / free / total (warning colour under 15% free), Open terminal here, Properties,
  and a hide button remembered per drive (View > Drive summary shows it again). Not done: split by file type, SMART / share health, search status, Recycle Bin size, tagged count,
  "share of drive" bar on folders, space map / duplicates / empty Recycle Bin actions (later phases).

### E. Operations (basic, local)

- [x] **E1. Worker contract for simple operations.** Copy, move, rename, delete-to-Recycle-Bin requests in
  `Leviathan.Contracts`; bump `ContractVersion.Major`. Journaled through the existing job host.
  Done: `JobKind.Rename` and `Link`, `ConflictPolicy` (Keep both default / Replace / Skip), `DeleteMode` (Recycle Bin / Permanent), `NewName`, per-item `ItemOutcome` and
  `GetResultAsync`, contract major 2 and pipe `v2`, `JobRequestValidator` at the worker door; the new fields survive the journal. No executor yet: a copy submitted now is queued and fails
  with "not supported yet" until E2/E3 add the executors. Not done: the UI does not submit jobs yet (E2 swaps `UnavailableTransferService` for a worker-backed one).
- [x] **E2. Basic copy/move for local disks** (non-journaled chunking is acceptable this phase; Phase 2 replaces it). Writes
  `*.leviathan-part` and renames, conflict handling limited to Replace / Skip / Keep both.
  Done: `CopyMoveExecutor` in the worker (chunked copy to a part file then rename, timestamps and attributes kept, folder trees, free-space check up front, same-volume move = rename,
  cross-volume move deletes the source only after the copy is read back and its SHA-256 matches, Keep both / Replace (merges folders) / Skip), `WorkerTransfers` in the app replaces
  `UnavailableTransferService` so drops now copy and move for real, the view reloads and the status bar says how it went. Verified by dragging in the real app (move and Ctrl-copy).
  Not done: byte-level resume after a crash (Phase 2), shortcuts, choosing the conflict policy in the UI, progress shown in the UI (Operations panel, Phase 2), cancel, Paste (E5).
- [x] **E3. Recycle Bin delete** via `IFileOperation` in the worker (not on the UI thread), Shift+Del permanent with confirmation.
  Done: `DeleteExecutor` (Recycle Bin via `IFileOperation` on an STA thread in the worker; soft delete into `#recycle` / `@Recycle` or a hidden `.leviathan-trash` with a manifest on shares; permanent delete clears read-only and never follows a junction), `edit.delete` (Del) and `edit.deletePermanent` (Shift+Del, confirmation dialog), `IShellActions.ConfirmAsync`, `IDeleteService` + `WorkerTransfers`. Verified in the real app: Del put a file in the Recycle Bin, a declined Shift+Del kept it, a confirmed one deleted it, and Del on a `\localhostD$` path soft-deleted into `.leviathan-trash`. Not done: Empty Recycle Bin, drives without a Recycle Bin get Windows' error message rather than a permanent-delete prompt.
- [x] **E4. Undo/redo** for rename and delete (Ctrl+Z / Ctrl+Y), journaled so undo survives a UI restart.
  Done: `UndoService` (one history for all windows; newest first, redo ends on a new action, expires after 7 days, capped at 100, written to `undo.json` off the UI thread), `JobKind.Restore` + `RestoreExecutor` in the worker (moves back without ever overwriting, recreates the folder, removes the `$I` record), `RecycleBinIndex` (finds where Windows put a recycled item from its `$I`/`$R` pair, versions 1 and 2), `edit.undo` / `edit.redo` (Ctrl+Z, Ctrl+Y, Ctrl+Shift+Z) with the bar buttons enabled and their tooltips naming the action, rename and delete record themselves. Fixed on the way: Del, Shift+Del, Ctrl+Z and Ctrl+Y typed in a text box (filter, address bar, rename box) used to reach the files; they now edit the text. Verified in the real app: rename undone and redone, a delete undone from the Recycle Bin, a delete undone after restarting the app; and against the real Windows Recycle Bin in the integration tests.
  Not done: undo of copy and move (the results are there, the entries are not recorded yet), undo of a permanent delete (impossible by design), forward rename still runs in-process (see decisions), a manifest line stays behind after a share restore.
- [x] **E5. Clipboard** commands wired to E1–E4: cut, copy, paste, paste-as variants, disabled state with tooltip.
  Done: `IFileClipboard` (view-model seam) with `WindowsFileClipboard` over the WinRT clipboard: shell storage items made on demand plus the paths as text, cut = requested operation Move, so files move to and from Explorer both ways; inside the app the list is kept and no shell round trip is needed. `edit.cut` / `edit.copy` / `edit.paste` (Ctrl+X / C / V, bar buttons) and `edit.pasteIntoNewFolder`. Paste goes through the same `DropRules` and `ITransferService` as a drop: a cut moves, a copy copies (also on one volume, and into its own folder, which makes `name (2)`), refusals say why and leave the clipboard alone, a started cut empties the clipboard. Paste is disabled with a tooltip ("Nothing to paste: copy or cut files first" / "Files cannot be pasted into this location") and follows the clipboard live. The clipboard keys stay with text boxes.
  Found and fixed on the way: switching tabs (Ctrl+1) onto a tab with a selection crashed the app, because the hidden List / Thumbnails view tried to scroll to the selection (`FlowView.EnsureVisible` created an element in a collapsed repeater, `COMException` 0x800F1000). It now skips while hidden and scrolls when it is shown.
  Verified live: copy and paste into another tab, cut and paste (source gone, second paste does nothing), same-folder paste (`a (2).txt`), files placed on the clipboard by another program pasted in, Ctrl+V in the filter box pastes text only, Explorer-style file list visible to other programs after Ctrl+C.
  Not done: Paste as shortcut, hard link and symbolic link (disabled with a tooltip; the transfer service has no link mode yet, Phase 2), undo of a paste (with copy and move undo), a dimmed look for cut items, clipboard contents surviving the app closing (the shell items are made on demand), pasting text paths.

### F. ShellHost and the right-click menu

- [x] **F1. `Leviathan.ShellHost` process**: STA worker pool, named-pipe contract, supervised by `ProcessSupervisor`.
  Done: `Leviathan.ShellHost.exe` (one per user, pipe `Leviathan.ShellHost.v2.<userhash>`, current-user-only like the worker; exits after 2 minutes with no client, so it runs on demand) with `StaPool` (2 STA threads, a time limit on every request counted from when the work starts; a request over its limit gives the caller a `TimeoutException`, the stuck thread is written off and replaced, and 8 stuck threads make the host exit with code 2 so the supervisor starts a clean one), `IShellHostService` (`PingAsync`, `GetStatusAsync`, in `Leviathan.Contracts`, same `ContractVersion` as the worker), `ShellHostClient` (Ipc) and `ShellHostConnection` (Engine): starts the host on the first call, reconnects to a restarted host, fails at once with `ShellHostUnavailableException` when the host is disabled (3 crashes in 5 minutes) or does not come up within the limit. `AppHost.ShellHost` is lazy; the app project copies the exe next to `Leviathan.exe`. Verified: 16 tests in the new `Leviathan.ShellHost.Tests`, including the real exe on a private pipe (`--pipe`), killed and restarted, and disabled after repeated crashes; the built app output starts the host (43 MB, 22 threads idle).
  Not done: the host has no menu or preview work yet (F2, F3); a status line for it on the Background tab (I9).
- [x] **F2. Context-menu port** from Files `ContextMenu*` / `ContextMenuWorkerPool`, hosted in ShellHost.
  Done: `ShellContextMenu` (Interop; ported logic from Files `ContextMenu.cs`, MIT) builds the shell menu for one or many files, a folder's empty-space ("background") menu, or the Shift menu; reads items with label, verb, enabled / checked / default, BGRA icon and submenu flag; loads submenus on request (`WM_INITMENUPOPUP` through `IContextMenu2`); invokes by command id or verb with an owner window. `StaMessageThread` gives each open menu its own STA thread with a message loop, since the handler objects must be used where they were made and dialogs need the loop. `ShellMenuManager` in the host keeps up to four open menus, closes idle ones after 2 minutes, writes a menu off on a timeout (10 s to build or read) and raises the host's unhealthy exit after eight; an invoke that opens a dialog is reported started after 10 s without killing the menu. Contract (minor 1): `OpenContextMenuAsync`, `LoadSubMenuAsync`, `InvokeAsync`, `CloseContextMenuAsync`. Verified: 34 ShellHost tests (manager with a fake shell, real exe over the pipe building the real menu), 13 new Interop tests against this machine's shell; the real menu shows 7-Zip, TextPad, PowerRename, File Locksmith with their icons.
  Not done: the UI (F3); invoking a real command is only proven for a refused verb (a successful verb changes the clipboard or opens programs; F3 checks it in the app); no warm-thread reuse (each menu starts a fresh thread, so the first handlers load cold: measure in F3).
- [ ] **F3. Right-click menu** (§8.5): Leviathan items first, third-party items loaded asynchronously into "More options ▸",
  Shift+right-click inline. A deliberately crashing handler must not affect the app (integration test).

### I. Brett's feedback (added 2026-11-02; do after F and before G)

Spec §4.1 (tab groups), §4.3 (icons), §5.2 (favorite star), §8.2 (column chooser), §16.1 (status hub), §17.2 (settings sheet);
decisions D32–D36. I1–I5 must land before G1 so startup profiles save tab groups from the start. The letter is I so the
existing slice names stay as they are; work order is E, F, I, G, H.

- [ ] **I1. Favorite star in the address bar** (§5.2): outlined/filled star at the right end of the breadcrumb, bound to the
  favorites store so it updates when Ctrl+D, the tree or drag-to-Favorites change it; click toggles, right-click rename/icon/colour.
- [ ] **I2. Resizable sidebar** (§4): splitter between the tree and the file view, minimum/maximum widths, double-click to collapse
  and restore, width remembered per window (and in session state); keyboard-accessible. The spec already required this; it was missed.
- [ ] **I3. Column chooser** (§8.2): View → Columns… and a "More columns…" item at the bottom of the header's right-click list open
  a dialog with grouped columns, search, tick boxes, drag to reorder, Reset to default and Apply to all folders of this type,
  writing the existing `ColumnLayout`. Columns whose data arrives later (media, tags) are listed but greyed with "available in a later update".
- [ ] **I4. Tab groups, model** (§4.1): `TabGroup` (id, name, colour, collapsed) on `WindowViewModel`; add/remove/move tabs and
  groups, ungroup, close group, reopen-closed restores group membership, Ctrl+Tab and Ctrl+1–9 skip collapsed tabs, tear-off and
  merge of a whole group between windows; included in the session state that G1 will persist. Unit-tested.
- [ ] **I5. Tab groups, strip UI** (§4.1): label chip and coloured underline per group, collapse to the chip with a count, group
  context menu, drag a tab into/out of a group, drag the chip to move the group (within the strip, out to a new window, onto another
  window), groups in the all-tabs list. WinUI `TabView` has no grouping: render chips as non-selectable items in the strip, or replace
  the strip with a custom control if that proves cleaner; record the choice in `docs/decisions.md`. No stalls over 50 ms when
  collapsing or expanding a group of 20 tabs.
- [ ] **I6. Colour icon set** (§4.3): pick a permissively licensed colour set (Fluent UI System Icons colour variants or similar),
  record it in `THIRD-PARTY-NOTICES.md`, add `IconCatalog` mapping commands and node kinds to icons, and switch the command bar, tree,
  tabs, status bar and menus over; light/dark variants, high-contrast fallback to monochrome glyphs. File-view icons stay from the shell.
- [ ] **I7. Status hub shell** (§16.1): chevron at the right of the status bar, fly-up panel (height draggable and remembered, Esc /
  click-outside closes, pin keeps it open), dock bottom / right and pop-out window, tabs Operations, Activity, Background, Quick
  settings, Space. Operations shows the worker jobs that exist now (folder sizes, E2/E3 copies and deletes) in a simple list until
  Phase 2 builds the full panel; Space says "arrives with the space map" until Phase 4. Status-bar parts open the matching tab.
- [ ] **I8. Activity tab** (§16.1): session activity log fed by the command layer (every executed command with its arguments
  summarised) and the diagnostics `ActionTrail`; stored in SQLite (7 days), filter, search, go to location, open the job, undo where
  possible, copy/export. Logging must cost < 1 ms per action on the UI thread (batched writes off-thread). Unit-tested.
- [ ] **I9. Background tab** (§16.1): folder-size queue and rate, thumbnail generation, worker / ShellHost process status and restart
  counts, with pause / resume / priority where the task supports it.
- [ ] **I10. Quick settings tab and settings sheet** (§16.1, §17.2): Quick settings for background threads / worker processes, running
  jobs, folder sizes on network shares, thumbnails on network files, polling interval, each applied live through the settings store.
  The settings sheet (Options ⚙ and Ctrl+,) rises from the bottom (about 70% of the window), page list left, search on top, expandable
  to full window; Phase 1 fills General, Appearance (G3 builds its page here) and Files and folders; the other pages show what they
  will hold. Options ⚙ no longer opens the Diagnostics window directly; Diagnostics moves to the Advanced page and a menu entry.

### G. Sessions and polish

- [ ] **G1. Startup profiles** (§17.1): Last session saved continuously (debounced 2 s) and on exit, survives a crash; named
  profiles; startup choice; unreachable tabs restore offline and never delay startup. Includes tab groups (I4) and the status hub
  height, tab and dock position.
- [ ] **G2. Keyboard map** (§17.3) complete for Phase 1 commands, remappable through the settings file.
- [ ] **G3. Appearance:** theme, Compact/Comfortable density, accent, Mica on/off, icon size (§17.2 Appearance page in the settings sheet from I10).

### H. Verification and finish

- [ ] **H1. FlaUI smoke tests** (`Leviathan.UI.Tests`): launch, tabs, tab groups (create, collapse, drag out), favorite star,
  sidebar resize, status hub open/close/tabs, settings sheet, navigate, tear off, rename, delete, undo; the stall
  watchdog fails the test on any stall over 50 ms.
- [ ] **H2. Benchmarks:** cold start to first folder, new tab, 10,000-item folder first rows through the view model, added to
  `tests/Leviathan.Benchmarks` and the `--gate` mode.
- [ ] **H3. Coverage pass** on `Leviathan.ViewModels` (≥ 85 % lines), and a stress pass: 100,000-item folder, long paths,
  odd names, dead share while browsing.
- [ ] **H4. Gate and wrap-up:** run the gate numbers, write `docs/phase-1.md` (delivered, verified with counts, unverified,
  deviations), update `README.md` status, tag `phase-1`, push, stop for Brett's review.

## Gate (from the build plan)

One week of Brett using it instead of Explorer for local work; zero UI stalls over 50 ms in the logs; cold start < 600 ms,
new tab < 150 ms, 10,000-item folder first rows < 100 ms. The week of use starts after H4; the gate numbers are recorded in
`docs/phase-1.md`, and the phase is marked closed once the week has passed.

## Decisions and questions (also kept in `docs/decisions.md` once made)

Defaults I will take unless Brett says otherwise; none blocks the first slices.

- View models live in a plain `net10.0` project (`Leviathan.ViewModels`) so they can be tested off Windows and in CI.
- Tiles and Gallery layouts wait for Phase 3 (they need media metadata and hover scrub); Details, List and Thumbnails ship now.
- Split pane buttons exist but are disabled until Phase 2 (§15.1 is in Phase 2's scope).
- Basic copy/paste in this phase is local-disk only and not resumable; the full engine is Phase 2.
- Third-party shell items in ShellHost need Brett's real machine to verify (7-Zip, Git, VS Code entries); I can only test crash
  isolation with a fake handler.
