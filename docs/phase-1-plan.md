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
- [ ] **D10. Status bar** (§16) minus operations details: item count, selection size, filter state, tag summary placeholder,
  location health, free space, background work, layout switcher.
- [ ] **D11. Drive summary card** (§8.7): usage bar by file type from the folder-size store, facts, actions, hide per drive.

### E. Operations (basic, local)

- [ ] **E1. Worker contract for simple operations.** Copy, move, rename, delete-to-Recycle-Bin requests in
  `Leviathan.Contracts`; bump `ContractVersion.Major`. Journaled through the existing job host.
- [ ] **E2. Basic copy/move for local disks** (non-journaled chunking is acceptable this phase; Phase 2 replaces it). Writes
  `*.leviathan-part` and renames, conflict handling limited to Replace / Skip / Keep both.
- [ ] **E3. Recycle Bin delete** via `IFileOperation` in the worker (not on the UI thread), Shift+Del permanent with confirmation.
- [ ] **E4. Undo/redo** for rename and delete (Ctrl+Z / Ctrl+Y), journaled so undo survives a UI restart.
- [ ] **E5. Clipboard** commands wired to E1–E4: cut, copy, paste, paste-as variants, disabled state with tooltip.

### F. ShellHost and the right-click menu

- [ ] **F1. `Leviathan.ShellHost` process**: STA worker pool, named-pipe contract, supervised by `ProcessSupervisor`.
- [ ] **F2. Context-menu port** from Files `ContextMenu*` / `ContextMenuWorkerPool`, hosted in ShellHost.
- [ ] **F3. Right-click menu** (§8.5): Leviathan items first, third-party items loaded asynchronously into "More options ▸",
  Shift+right-click inline. A deliberately crashing handler must not affect the app (integration test).

### G. Sessions and polish

- [ ] **G1. Startup profiles** (§17.1): Last session saved continuously (debounced 2 s) and on exit, survives a crash; named
  profiles; startup choice; unreachable tabs restore offline and never delay startup.
- [ ] **G2. Keyboard map** (§17.3) complete for Phase 1 commands, remappable through the settings file.
- [ ] **G3. Appearance:** theme, Compact/Comfortable density, accent, Mica on/off (§17.2 Appearance page, minimal UI).

### H. Verification and finish

- [ ] **H1. FlaUI smoke tests** (`Leviathan.UI.Tests`): launch, tabs, navigate, tear off, rename, delete, undo; the stall
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
