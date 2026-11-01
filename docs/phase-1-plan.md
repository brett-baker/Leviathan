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
- [ ] **C5. Open With / launch / file associations.** Port from Files `OpenWithMenu`, `LaunchHelper`, `FileAssociationHelpers`.
- [ ] **C6. ShellNew and templates** (§6.1). Read `ShellNew` registry entries, user templates folder, built-in New items.

### D. Shell UI

- [ ] **D1. App shell.** Replace the Phase 0 window: custom title bar with the tab strip, Mica, navigation row, body (sidebar |
  file view | preview placeholder), status bar. Phase 0's worker status and compute-size demo move to Options ▸ Diagnostics so
  the supervised worker connection stays testable.
- [ ] **D2. Tab strip control** (§4.1): drag reorder, tear-off to a new window, merge, pin, middle-click close, overflow.
- [ ] **D3. Breadcrumb address bar control**: segment buttons, chevron drop-downs, edit mode, drop onto a segment, icons for lock/cloud/offline.
- [ ] **D4. File view, Details.** `ItemsRepeater`-based virtualized grid, configurable columns (right-click header, reorder,
  auto-fit), skeleton rows while loading, empty/denied/offline states, folder sizes via `FolderSizeService` in the Size column.
- [ ] **D5. List and Thumbnails layouts**, Ctrl+wheel resize 64–512 px, Tiles/Gallery deferred to Phase 3 where they need media data.
- [ ] **D6. Sidebar tree** (§7, §7.2): Favorites (pin, drag, Ctrl+D), Common folders, Tags placeholder, Local drives with usage
  bars, Network placeholder, Internet; spinner child + 5 s timeout, optimistic expand arrows, one-level look-ahead at low priority, sync with view.
- [ ] **D7. Command bar** (§6): New menu, clipboard icon group with tooltips, context actions by selection type, overflow,
  View/Sort/Group, filter box, pane toggles (split deferred to Phase 2; the buttons exist and are disabled).
- [ ] **D8. Interaction:** inline rename (F2, name without extension), rubber-band, keyboard selection, type-ahead.
- [ ] **D9. Drag and drop** between tabs, windows, the tree, the breadcrumb and other apps, with the move/copy/link rules and the tooltip (§8.4).
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
