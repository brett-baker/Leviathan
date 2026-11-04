# Phase 1 — Core browser

Phase 1 turns the Phase 0 shell into a file manager you can use for local work: windows, tabs and tab groups, a breadcrumb address
bar, a sidebar tree, Details, List and Thumbnails views, clipboard operations with the Recycle Bin and undo, the shell context menu,
a listing cache that makes revisits instant, a status hub, settings, and an icon system that can swap packs.

## What is in it

| Area | Delivered |
| --- | --- |
| View models | `Leviathan.ViewModels` (no WinUI dependency): command layer with remappable keys, settings store (atomic JSON), per-folder view settings, navigation history, address bar model, streaming folder view with chunked UI updates, sort/group/filter, selection, tabs, tab groups, windows and tear-off, startup profiles |
| Interop | `Leviathan.Interop` (CsWin32, ported from Files with notices): known folders and drives, cloud drive detection, thumbnails, launch and file associations, ShellNew templates |
| Shell | `Leviathan.ShellHost`: STA pool, context menus in their own process, supervised and restartable |
| UI | Tab strip in the title bar with groups, drag to reorder and tear off; breadcrumb with siblings, edit mode and favourites star; Details, List and Thumbnails views (virtualized, recycled); sidebar tree with sections, resizable; command bar with New menu; inline rename; drag and drop between tabs, windows, tree and other apps; status bar and status hub (Operations, Activity, Background, Quick settings); drive summary card; settings sheet with search |
| Operations | Copy, move, rename, Recycle Bin delete and clipboard through the worker; undo/redo that survives a restart |
| Listing cache | In-memory LRU plus SQLite tier (`listings.db`): cached rows at once, then checked against the folder; folder watcher keeps the open folder current; invalidated by our own operations |
| Icons | Semantic icon keys with Segoe fallback, packs as folders of SVG (Fluent and Font Awesome included), switchable in settings |
| Appearance | Theme (fixed at start), density, accent (new windows), Mica, icon size |
| Tests | Unit, integration, stress, and UI smoke tests (FlaUI, opt-in) |

## Gate

| Budget | Result |
| --- | --- |
| 10,000-item folder, first rows < 100 ms (p95) | provider 3.4 ms; through the view model 3.7 ms; from the cache 0.0 ms: **PASS** |
| New tab < 150 ms | 1,000 items, first rows 5.1 ms p95: **PASS** |
| Cold start to the first folder < 600 ms | **FAIL: median about 1,010 ms, worst about 1,100 ms** published ReadyToRun (was 1,291 / 1,693 before the follow-up work below; Release, six starts, busy machine) |
| Zero UI stalls over 50 ms in the logs | Not yet shown: see below |
| One week of use instead of Explorer | Starts after this review |

Run with `dotnet run -c Release --project tests/Leviathan.Benchmarks -p:Platform=x64 -- --gate [--cold-start <Leviathan.exe>]`.

### Cold start: what would close the gap

From the app's own trace: about 480 ms from process start to `OnLaunched`, 160 ms `AppHost.Create`, about 850 ms building the main window.
Done after the `phase-1` tag (see `docs/decisions.md`): ReadyToRun (`tools/Publish-App.ps1`), listing the first folder while the window is
built, the status hub panel built after the window is up, undo file and thumbnail threads off the start path. What is left is mostly the
window itself (about 300 ms to `OnLaunched`, 90 ms creating the window, 140 ms XAML, 65 ms title bar, 47 ms chrome icons), so the budget
needs a lighter `MainWindow` (`x:Load`, lazy chrome icons), not one more fix. Use the published exe for the gate:
`--gate --cold-start artifacts\publish\Leviathan.exe`.

## How it was verified

All on Windows 11, `dotnet build Leviathan.slnx -p:Platform=x64` with zero warnings in `src/`.

| Suite | Passed | Skipped |
| --- | --- | --- |
| Core | 93 | 0 |
| Engine | 103 | 0 |
| Data | 27 | 0 |
| Diagnostics | 20 | 0 |
| Interop | 55 | 0 |
| Integration | 6 | 0 |
| Providers.Cloud | 20 | 0 |
| Providers.Local | 25 | 1 (100,000 real files, `LEVIATHAN_STRESS=1`) |
| ViewModels | 953 | 0 |
| ShellHost | 35 | 0 |
| **Total** | **1,337** | 1 |

- Coverage of `Leviathan.ViewModels`: **93.7 % of lines** (bar 85 %).
- Stress: a 100,000-entry folder through the tab view model (sort, filter, select all); names in every script; a dead share that does
  not stop another tab; on a real disk, odd names, a path over 260 characters, and (opt-in, passed) 100,000 files.
- UI smoke tests (`tests/Leviathan.UI.Tests`, FlaUI, `LEVIATHAN_UI_TESTS=1`, skipped otherwise because they take over mouse and
  keyboard): 12 written, **6 pass, 6 fail** (see below).
- Every UI slice was started and used by hand, including a second launch with saved state, which found two startup crashes before release.

## Still unverified or open

- **UI smoke tests**: 6 of 12 fail. Sidebar drag does not move under synthetic input (a real drag works); tab tear-off after a new tab
  stalled 185 ms (over the 150 ms budget, a real finding); status hub text not found in one run; delete then undo timed out waiting for the
  file to leave the folder (test setup with the worker needs a look); navigate by sidebar stalled 54 ms; settings sheet stalled 62 to 513 ms.
  The stall figures vary on a busy machine and want a quiet one. **H1 is therefore not ticked.**
- **UI stalls over 50 ms**: the first show of the settings sheet, the Background tab and opening a second window stall roughly 50 to 130 ms.
  The same `x:Load` work as for cold start addresses them.
- **Cold start** as above.
- **Flaky test**: one `Leviathan.Data.Tests` test failed once in a full run and did not repeat in 11 reruns; not identified.
- Duplicate folder-size jobs for the same folder were seen once (AppData counted twice).
- Ctrl+Shift+O (status hub) is swallowed on this machine by Claude's desktop app as a global hotkey; it can be remapped in settings.
- Tooltips still show the default keys, not remapped ones.

## Deviations from the spec

- **Theme and accent are fixed at start / per window**, not switched live: code-side brush lookups (about 16 places) kept stale colours
  after a live switch. Closing it needs those lookups to go through the theme resources.
- **Basic copy/move is not journaled** (acceptable in Phase 1 by the build plan; Phase 2 adds the journaled engine).
- The 100,000-file test on a real disk is opt-in because creating the files takes over a minute.

## How to try it

```powershell
dotnet build Leviathan.slnx -p:Platform=x64
dotnet run --project src/Leviathan.App -p:Platform=x64
```

Things worth looking at: open several tabs and drag one out; Ctrl+T, Ctrl+W, F2, Ctrl+Z after a delete; the status hub chevron at the
right of the status bar; Ctrl+, for settings (try an icon pack and the accent); revisit a big folder and notice the instant list.
