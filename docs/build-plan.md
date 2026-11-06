# Build plan

One phase at a time, in this order. Each phase: plan → slices (code + tests + commit + push) → gate → `docs/phase-N.md`
→ tag → stop for Brett's review. Spec references are to `docs/spec.md`.

## Phase 0b — Finish Phase 0 on Windows (do this first)

Phase 0 was written off Windows; parts have never been built or run (see `docs/phase-0.md`, "How it was verified").

- Restore and build the whole solution on Windows. Fix package versions in `Directory.Packages.props` if any don't exist.
- Build `Leviathan.App` and fix XAML or WinUI issues.
- Run every test project, including `Leviathan.Data.Tests` and `Leviathan.Integration.Tests` (never run yet), and the
  Windows-only Win32 lister test.
- F5 the app: connect to the worker, compute a folder size, kill `Leviathan.Worker.exe` mid-job, confirm restart and journal recovery.
- Run the enumeration gate on NTFS and record it in `docs/phase-0.md`.
- Push the `phase-0` tag if it is not on GitHub yet.

**Gate:** clean build with zero warnings in `src/`, all tests green on Windows, enumeration gate passes on NTFS.

## Phase 1 — Core browser (daily driver for local work)

**Spec:** §4 (window, tabs), §5 (navigation, address bar), §6 (command bar), §7 (sidebar tree incl. look-ahead loading),
§8 (file view, columns, sort/group, selection, right-click, empty/error states, drive summary §8.7), §16 (status bar),
§17.1 (startup profiles incl. Last session), §17.3 (keyboard map). Mockups: Main window, Drive summary.

**Build:**
- App shell: custom title bar with tab strip (all §4.1 behaviours: duplicate, reorder, tear-off to a new window in the same
  process, merge, pin, lock, reopen closed), Mica, multiple windows.
- Navigation row: back/forward/up/reload with history menus; breadcrumb address bar with segment drop-downs, edit mode,
  copy-as variants, autocomplete, `LocationPath.TryResolve` for typed input.
- Command bar: New menu (built-in + `ShellNew` registry + user templates), icon buttons with tooltips for clipboard actions,
  context-aware actions by selection type, layout/sort/filter, pane toggles.
- Sidebar: one virtualized tree (Favorites, Common folders via Known Folder IDs, Tags placeholder, Local drives with usage bars,
  Network placeholder, Internet via cloud detection), icons, expand arrows, one-level look-ahead.
- File view: Details (configurable columns), List, Thumbnails (shell thumbnails via `IThumbnailCache`/`IShellItemImageFactory`),
  natural sort, grouping, rubber-band and keyboard selection, inline rename, drag and drop with copy/move/link rules,
  per-folder view settings with inheritance.
- Clipboard and simple operations through the worker: copy/paste (basic, non-journaled version is acceptable for local disks
  in this phase), rename, delete to Recycle Bin (`IFileOperation`), undo for rename/delete.
- Right-click menu: Leviathan items + third-party shell items loaded asynchronously from a new **`Leviathan.ShellHost`**
  process (port Files' `ContextMenu`/`ContextMenuWorkerPool`).
- Drive summary card (§8.7) on drive roots.
- Status bar (§16) minus operations details.
- Added 2026-11-02 (Brett's feedback, see `docs/phase-1-plan.md` group I): favorite star in the address bar (§5.2), resizable
  sidebar, column chooser (§8.2), tab groups (§4.1, before startup profiles), colour icon set (§4.3), status hub fly-up with
  Activity, Background and Quick settings tabs (§16.1), settings sheet from the bottom (§17.2).
- Startup profiles with Last session saved continuously.
- Command layer: every user action is a command (palette-ready, recorded in `ActionTrail`, replayable by `leviathan-diag replay`).

**Reuse from Files (§18):** CsWin32 setup, context menus, cloud drive detection, Open With / launch / file associations,
enumerators (compare with Leviathan's), known folders.

**Tests:** view-model unit tests (navigation history, breadcrumb, selection, sort/group, tab model, profiles); ShellHost
integration (crash isolation: a deliberately crashing handler must not affect the app); FlaUI smoke tests (launch, tabs,
navigate, tear off, rename, delete, undo); stall watchdog assertion in UI tests; benchmarks for cold start and new tab.

**Gate:** one week of Brett using it instead of Explorer for local work; zero UI stalls over 50 ms in the logs; cold start
< 600 ms, new tab < 150 ms, 10,000-item folder first rows < 100 ms.

## Phase 2 — NAS resiliency and operations engine

**Spec:** §3.3 (network resiliency), §3.5 (reference NAS), §7.1–7.2 (network section, tree rules), §12 (operations engine:
copy pipeline, network failures, queue, conflicts, soft delete, activity strip and Operations panel, folder-size service),
§15.1 (split pane). Mockups: Split panes with preview, operations panel.

**Build:**
- SMB provider hardening: share health per server (one server, several names: resolve SMB server identity), fast-fail banners,
  offline listing cache, polling change detection with back-off, credentials via Windows Credential Manager, WS-Discovery/mDNS
  "Nearby" discovery.
- Copy/move engine in the worker: plan → fastest path per item (rename, `CopyFile2` server-side copy, chunked copier with
  adaptive buffers) → `*.leviathan-part` + rename → per-chunk resume journal → verify (full ≤ 4 GB, sampled above) → delete source
  only after verify. Preserve timestamps, attributes, alternate data streams.
- Waiting state and automatic resume on network drops; permanent-error prompts; safe renames for invalid names.
- Queue UI: activity strip (1–2 px line per job, color per job), Operations panel as the Operations tab of the status hub
  fly-up built in Phase 1 (§16.1; fly-up by default, dock bottom/right, pop out), speed sparklines, row actions, live concurrency limits, bandwidth limit with schedule, reports.
- Conflict dialog with Compare.
- Soft delete: `#recycle` when the share's Recycle Bin is on, else `.leviathan-trash`; bin view, restore, purge, retention.
- Folder-size service wired to the UI (Size column, drive/folder summaries) with delta updates and staleness.
- Split pane per tab with F5/F6 and the preview pane following the focused pane.
- Status hub additions: copy limits and bandwidth in Quick settings; share crawls, polling and offline-cache refresh in Background;
  Activity entries link to their jobs.

**Tests:** chaos suite (kill worker during 1,000 copies; Toxiproxy network drops mid-copy against a Samba container); copier
unit tests with fault injection (drop after N bytes, disk full, access denied); verify policy; conflict rules; soft-delete
manifest and restore; folder-size delta propagation end to end.

**Gate:** a 50 GB copy to the NAS survives 3 cable pulls and a killed UI with no corrupt files; sustained large-file copy
≥ 105 MB/s on the 1 GbE link.

## Phase 3 — Media and triage

**Spec:** §9 (preview and details, ffmpeg integration), §10 (video triage: triage mode, keys, skim, contact sheets, hover
scrub, working at scale), §11 (color tags), §17.1 (profiles). Mockups: Main window preview pane, Triage mode.

**Build:**
- **`Leviathan.Media`** process: ffprobe (GPL build) metadata with timeouts, quarantine of bad files, SQLite cache keyed by
  path + size + mtime; thumbnails; contact sheets via keyframe seeking; hover-scrub sprite strips.
- Video engines behind `IVideoEngine`: libmpv and LibVLC (switchable in Settings); pre-warming; next-file pre-buffer
  (header/index only on NAS).
- Preview pane renderers (§9.1) with collapse, resize, S/M/L video size, icon seek controls (30 s / 1 min / 5 min).
- Triage mode with inboxes, sessions, keys (§10.2), Keep = green tag (optional move), Discard = soft delete, undo, summary.
- Color tags: alternate data stream + SQLite index, timestamp preservation, import of Files' `:files` stream, Tags sidebar,
  1–9 keys.
- Media columns (duration, resolution, codecs…) filled lazily for visible rows, and added to the column chooser.
- Status hub Background tab: metadata probing, thumbnails and contact sheets with pause/priority; Media process status.

**Tests:** ffprobe parsing on a fixture set including corrupt/truncated files; Media process crash isolation; tag storage
round-trip incl. timestamp preservation and copy; triage key flows (view-model tests + FlaUI); pre-buffer logic.

**Gate:** triage 100 NAS videos at under 10 s each; a corrupt-file test set causes no crash.

## Phase 4 — Search, space map and more locations

**Spec:** §13 (search: Everything SDK for local when installed, own USN indexer fallback, NAS index from the folder-size crawl,
query syntax, saved searches), §14 (space map), §7.1 (SFTP, FTP(S), NFS), Internet section via sync clients. Mockups: Space map.

**Build:** Everything SDK integration; `Leviathan.Indexer` fallback (optional service); NAS index; query parser; saved searches;
space map (squarified treemap, linked tree, cached scans, color by type/age); SFTP (SSH.NET), FTP(S) (FluentFTP, compare Files'
FTP storage), NFS via Windows Client for NFS; cloud sync-client folders with status. Status hub: the Space tab (compact treemap of
the current folder, §16.1) and indexer / NAS crawl status in Background.

**Tests:** query parser property tests; indexer against a temp NTFS volume (VHD) where possible; providers against SFTP/FTP/NFS
containers; space-map layout tests.

**Gate:** local name search < 50 ms; a space map of a 1 TB share completes and can resume.

## Phase 5 — Power features and polish

**Spec:** §15 (duplicate finder, bulk rename, archives and checksums, folder compare/sync, other features), §6 command palette,
§17 (all settings pages in the bottom settings sheet, full keyboard remapping), §3.1 (performance pass on every budget).

**Gate:** feature-complete against the spec after a month of daily use.

## Phases 6 to 10 — Extensions (only after Phase 5's gate)

Brett's rule: get the original concept solid first, then add these, **in this order**, one phase at a time with the same
plan → slices → UAT playbook → gate → `docs/phase-N.md` → tag → stop. Each phase's first step is to write
`docs/phase-N-plan.md` from the notes below. Set aside for now (see spec §21): window-level dual pane, smart folders across
drives, deep Git integration, hash / properties extras.

## Phase 6 — Workspaces

**Spec:** §17.1 (D37), §4.1 (tabs, groups), §15.1 (split). Replaces the profile code of Phase 1 (G1).

**Build:** `Workspace` model (schema-versioned) holding everything in the §17.1 table, including each tab's split orientation
and ratio and each pane's own state; Last session as the built-in workspace; store with atomic writes, `.bak` copies and
damaged / newer-schema handling; **migration** from `last-session.json` and `profiles.json` (kept as `.migrated`); Save
current windows as workspace (all windows / this window), rename, duplicate, Save over, delete, export / import with an
unresolvable-locations report; startup setting (Last session / a workspace / nothing / ask) and `--workspace` argument;
open modes Replace, Open in new windows, Add tabs to this window; title-bar menu, picker (Ctrl+Alt+W) and command palette
entries; staged opening (selected tab first, network tabs last); offline / missing tab state; dirty marker on an edited workspace.
Slices: model + store + migration; capture and restore (window, tab, split, view); open modes + window manager; UI (menu, picker,
settings, command line); import / export; polish.

**Tests:** round trips per level; migration fixtures from real Phase 1–2 files; damaged / truncated / future files;
offline and missing locations; the three open modes (never zero windows); `--workspace`; 50-tab open with no stall over 50 ms;
FlaUI smoke: save, close, reopen restores a split in the right orientation.

**Gate:** a 50-tab, 3-window workspace opens with the first window usable under 1 s; killing the app mid-save never loses
the previous workspace; migration of the existing files is lossless.

## Phase 7 — Column view

**Spec:** §8.8 (D38), §8.1, §7.2 (listing rules), §3.1 budgets.

**Build:** `ColumnsViewModel` (path-to-columns mapping, selection rules, re-anchoring, per-column listing with
cancellation and the 150 ms keyboard debounce, width clamping and the visible-count formula); WinUI view: horizontally
scrolling strip of virtualized lists with draggable dividers, min / max / default width settings, per-depth remembered
widths, header context menu, quiet highlight for non-focused columns; Columns layout in the View menu, layout switcher and
Ctrl+Shift+3, saved per folder and in workspaces; interplay with split panes, the preview pane and drag and drop.
Slices: view model + tests; static view with one folder per column; selection and keyboard; resizing and re-flow; polish and
network behaviour.

**Tests:** per §8.8; a fault-injecting provider makes one column's share fail while the others work; FlaUI smoke for
resize and horizontal scroll; stall watchdog during rapid keyboard selection.

**Gate:** first rows of a new column < 100 ms (10,000 local items) and < 400 ms on the NAS; a 20-deep path stays smooth; no
UI stall over 50 ms while holding Down in a 5,000-folder column.

## Phase 8 — Terminal drawer

**Spec:** §16.2 (D39), §16.1 (status hub), §3.4 (process isolation).

**Build:** **Slice H1 is a feasibility spike** (dock Windows Terminal in the hub: resize, focus, keyboard, DPI, multi-monitor);
its result and the chosen approach (docked Windows Terminal, or the ConPTY fallback in §16.2) go in `docs/decisions.md` before
anything else. Then: terminal host process under `ProcessSupervisor`; Terminal tab in the hub with session strip, Go to
current folder, `pushd` for UNC, home fallback, shell list setting, Ctrl+` toggle, pop-out and dock; close-confirmation
when programs are running; crash isolation message; Activity entries (open / shell, never keystrokes).

**Tests:** command building and quoting; session list; integration with real cmd in a temp folder; chaos: kill the host
mid-program; stall watchdog while output floods; FlaUI smoke: hotkey opens a prompt in the current folder.

**Gate:** drawer shows a prompt < 600 ms; killing the terminal host leaves the app and other hub tabs usable; no stall over
50 ms during a 10 MB output flood.

## Phase 9 — Always-available tools

**Spec:** §15.7 (D40), §6, §8.5, §15.6 (command palette from Phase 5).

**Build:** one command implementation each for Copy path (all variants, default-format setting), Open terminal here (drawer
by default, external window with Shift) and Open in editor (default app / VS Code / custom command, validation, folder
rule), registered once and surfaced in the right-click menu, the command bar and the command palette with matching names,
icons and shortcuts; settings; Activity entries; disabled states with tooltips (registry, search results, archives).

**Tests:** per §15.7, including "same command id in all three places" and every copy variant on the fixture folders.

**Gate:** each command reachable from all three places and by its shortcut, in a split pane and in a multi-selection.

## Phase 10 — Registry explorer (read-only)

**Spec:** §7.3 (D41), §2.3 (contract bump for the `Registry` location kind).

**Build:** `LocationKind.Registry` and address forms in `LocationPath` (contract Major bump); `IRegistry` with the real Win32
implementation (CsWin32, streaming enumeration in batches) and an in-memory fake; `RegistryProvider` implementing the
read-only subset; Registry sidebar section behind the Settings toggle; value columns and rendering for every type; preview
of value data; read-only command states; Copy data, Open in Regedit, Export key to `.reg` (worker job); access-denied and
huge-key handling; change detection by `RegNotifyChangeKeyValue` with a poll fallback; workspaces and columns support.

**Tests:** address-form parsing; value rendering for every type; huge and vanishing keys; locked keys; scratch-key
integration tests under `HKCU\Software\LeviathanTests`; export output compared to Regedit's format; proof that no write API is
reachable (the provider type does not implement the write interfaces).

**Gate:** a key with 10,000 subkeys shows rows < 100 ms; browsing `HKLM` as a standard user never errors the tree; a
repository-wide search shows no registry write call in `src/`.

## Later (decide after Phase 4)

- NAS agent in a container on the DS2419+ for server-side hashing, folder sizes and change notifications (spec §21).
