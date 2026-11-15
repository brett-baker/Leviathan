# Phase 5 — Power features and polish

Phase 5 adds the tools that make Leviathan more than a browser (duplicates, bulk rename, archives and checksums, compare and
sync, the command palette and the rest of §15.6), finishes the settings and keyboard pages (§17), and ends with a hardening pass
over performance, memory, stress and accessibility (§3). Plan: `docs/phase-5-plan.md`. Numbers: `docs/phase-5-budgets.md`
and `docs/phase-5-stress.md`.

## What is in it

| Area | Delivered |
| --- | --- |
| Duplicate finder (A) | Exact duplicates by size, then first-and-last-megabyte hash, then full xxHash3 only for what is left; scopes, type and size filters; runs in the worker with progress. Lookalike images and videos through `Leviathan.Media` perceptual hashes with a tolerance setting. Results window (Alt+Shift+F): groups with thumbnails and metadata side by side, auto-select rules (highest resolution, largest, newest, oldest, preferred folder), actions (soft delete, move, hard-link replace on one volume, tag) |
| Bulk rename (B) | Ordered operations (find/replace plain or regex, insert, remove, case, trim, numbering) and tokens (`{name}`, `{ext}`, `{date:…}`, `{taken}`, `{duration}`, `{resolution}`, `{codec}`, `{parent}`, `{counter}`); property tests for no silent collisions and reversibility; dialog with live preview, conflicts, presets, one undo for the batch, runs as a worker job |
| Archives and checksums (C) | zip, 7z, rar (read-only), tar, gz, iso browsed as folders through 7-Zip, with preview; extract and compress jobs through the queue; MD5, SHA-1, SHA-256, SHA-512, xxHash3, CRC32, verify against `.sha256`/`.md5`, export checksum files |
| Compare and sync (D) | Compare by name, size and date or by checksum; mirror either way, two-way (newer wins, conflicts flagged), update; deletions go to the Recycle Bin or soft delete; dry-run preview first; saved pairs re-run in one click, pinned to Favorites, optional schedule |
| The rest of §15.6 (E) | Command palette (Ctrl+Shift+P) and go-to palette (Ctrl+P); open terminal here (Windows Terminal, PowerShell, cmd, WSL); nine quick move/copy targets; elevated-operations helper (only the action that needs admin runs elevated); Git status overlays; folder notes in the details pane |
| Settings and keyboard (F) | Every Settings page of §17 in the bottom sheet, all applied without restart; Keyboard page with search, rebind, reset, import/export and conflict detection; Diagnostics view that groups crashes with Copy for Claude |
| Hardening (G) | Budget sweep (G1); bounded thumbnail cache and leak tests (G2); stress with long paths, awkward names, 500,000 files, a 24 GiB file, a share that drops mid-delete (G3); names for screen readers on every control and contrast-tested status colours (G4) |

## Gate

> Feature-complete against the spec after a month of daily use.

**Not closed, and cannot be by me.** The feature list is built; the month of daily use is Brett's. The running list of issues from
that month goes in the section "Issues from the month of use" below; each is closed with a commit and a regression test, as the
plan says.

What I could close is the measured part of §3, run on 2026-11-15 (Release, Brett's PC):

| Budget (§3.1) | Measured | Result |
| --- | --- | --- |
| 10,000 items, first rows < 100 ms | 4.8 ms provider; 3.8 ms through the view model; 0.0 ms from the cache (p95) | PASS |
| 100,000 items fully listed < 1.5 s | 153 ms (p95) | PASS |
| New tab < 150 ms | 5.9 ms (p95) | PASS |
| Triage decisions < 50 ms | 0.0 to 5.5 ms (p95) | PASS |
| Preview after the 150 ms rest < 200 ms; media info < 300 ms | 164.7 ms; 110 ms (p95) | PASS |
| Thumbnail from cache < 200 ms; drawn < 1 s | 0.7 ms; 200 ms (p95) | PASS |
| Name search over 1,020,100 names, first results < 50 ms | 0.4 to 20.2 ms (p95) | PASS |
| Space map of 400,000 files / 1 TB: layout < 1 s, resumable | 199 ms (p95); resumed totals equal | PASS |
| 24 GiB file copy streams | 584 MB/s, memory peak 46 MB | PASS (G3) |
| 1,000 tabs and 200 windows opened and closed | nothing left behind | PASS (tests) |
| **Cold start to the first folder < 600 ms** | **about 920 ms quiet, 1,200 to 1,290 ms under load** (published ReadyToRun) | **FAIL** |
| **Idle memory, 10 tabs < 250 MB** | **262 to 274 MB private** (managed heap 26 MB; the rest is WinUI and the GPU driver) | **FAIL by 12 to 24 MB** |
| NAS first rows < 400 ms; NAS copy ≥ 105 MB/s | needs a share Brett names (`--nas-rows`, `--nas-copy`) | **not measured** |
| Local search through Everything | needs Everything installed | skipped |

Both failures are explained, with what would close them, in `docs/phase-5-budgets.md`. Cold start needs a lighter `MainWindow`
(build the hub, settings sheet and preview pane on first use; make chrome icons after the first folder is painted), which is UI work
to be judged on screen. Idle memory is a choice for Brett: measure the budget as managed plus our own native memory (passes), or cut
WinUI's own use (visible changes).

## How it was verified

| What | How | Result |
| --- | --- | --- |
| Build | `dotnet build Leviathan.slnx -c Debug -p:Platform=x64` | 0 warnings, 0 errors |
| Whole suite | `dotnet test Leviathan.slnx -p:Platform=x64`, 2026-11-15 | **3,853 passed, 6 skipped, 0 failed** across 17 test projects (ViewModels 2,144; Engine 374; Core 367; Search 219; Media 154; Interop 108; Archive 87; Integration 75; Data 58; ShellHost 57; Remote 39; Local 36; Cloud 32; Playback 31; SpaceMap 49; Diagnostics 20; Chaos 3). The skips need Docker, the lab share or a NAS and say so |
| Stress (G3) | real temp folders: 400+ character paths, 15 awkward names, 500,000-file view model, 24 GiB copy | pass; 5 bugs found and fixed (`docs/phase-5-stress.md`) |
| Leaks (G2) | `WeakReference` reachability after 1,000 tabs and 200 windows | pass, stable over repeated runs |
| Accessibility (G4) | `XamlAccessibilityTests` reads every XAML file; `StatusColorsTests` checks 4.5 to 1 in both themes | pass |
| The App | built in a copy synced by PowerShell and checked to hold the new code | 0 warnings |

A caution on the App build: for four slices (E6 to F3) I reported the App as building when a stale copy was being built, and two
compile errors reached `main` before G2 found them (fixed in c241717). Every slice since builds from a verified fresh copy. Please
do a **Clean + Rebuild** in Visual Studio when you pull.

## Still unverified or open

- **Never seen on screen:** the Settings pages added in F1, the Keyboard editor (F2), the crash section of Diagnostics (F3), folder
  notes (E6), the new accessibility names, the duplicate results window and the sync windows. They compile, their logic is tested,
  they have started without error; nobody has looked at them.
- **UI smoke tests (FlaUI) have not been run** this phase. They use the real mouse and keyboard and once deleted a real folder;
  they run only on Brett's say-so (see the memory note and `docs/backlog.md`).
- **Accessibility:** focus order, a keyboard-only walk of every feature, Narrator output for templated list rows, and high-contrast
  themes were not checked. A Git letter picks its colour when its row is built, so a theme switch shows the old colour until refresh.
- **Odd names in the folder view:** the engine copies, moves and deletes names ending in a dot or space and device names; the file
  list, rename, open and the context menu still build paths the ordinary way and may fail on them.
- **Not run:** a 100 GB file (24 GiB was run), dropped real SMB shares during an operation, 500,000 rows scrolled on screen, the
  NAS budgets, Everything.
- **Folder notes** do not follow a rename or delete (`Move` and `Remove` exist, not wired); no palette command or sidebar marker.
- **Report a problem** (Ctrl+Shift+F12) is not wired.
- **Settings left out because they would do nothing yet:** show extensions, single click to open, Ctrl+Tab in most-recently-used order.
- **UAT playbooks:** `docs/uat/` has one for finding duplicates (H4) and none for bulk rename, archives, checksums, sync, the palette, the settings pages or the keyboard editor. Say which you want.
- The parked items in `docs/backlog.md` are unchanged: the cable-pull gate, copy speed against robocopy, the stalls on tab tear-off,
  the MFT indexer, FTPS and NFS against real servers.

## Deviations from the spec

| Spec | What was built | Why |
| --- | --- | --- |
| §3.1 cold start < 600 ms | about 0.9 to 1.2 s | needs a lighter main window, to be looked at on screen |
| §3.1 idle memory < 250 MB | 262 to 274 MB | native WinUI and GPU driver floor; managed heap is 26 MB |
| §17 every setting | three settings left out | no behaviour behind them yet |
| §15.2 duplicates, similar media | lookalikes are never replaced by a hard link | different files by definition |
| LocationPath | new `ParseExact`, `ForIo`, wider `ToWin32Path` | names Windows rewrites need the `\\?\` prefix (G3) |

Smaller decisions are one line each in `docs/decisions.md`.

## Issues from the month of use

*(Brett: add what you find; each gets a commit and a regression test.)*

| # | Found | Issue | Fixed in |
| --- | --- | --- | --- |
| | | | |
