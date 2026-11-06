# Phase 2 — NAS resiliency and operations engine

Phase 2 makes Leviathan safe to point at a NAS: one identity per server, health tracking that fails fast, a journaled copy and move
engine that survives a dropped share and a killed process, a queue UI, soft delete with a restorable bin, folder sizes in the UI, and a
split pane with copy and move between the panes.

## What is in it

| Area | Delivered |
| --- | --- |
| Network foundations (A) | One server identity across names and addresses; `ShareHealthTracker` fed by every provider call; fast-fail banners, greyed offline listing, Retry / Work offline; change detection by polling for shares and mapped drives; credentials and reconnect (`Sign in…`); Network section of the tree with Add connection and Nearby discovery; link speed in the status bar |
| Copy and move engine (B) | Planner (every file, its resolved name and fastest path, free-space check before any byte moves); chunked copier with adaptive buffers writing `*.leviathan-part` then renaming; per-file resume journal; verify policy (xxHash3, full up to 4 GB, sampled above); transient errors go to Waiting and resume by themselves, permanent ones ask Skip / Retry / Skip all like this |
| Queue and UI (C) | Scheduler with live limits, bandwidth limits and schedule; activity strip; Operations tab with groups, row actions, prompts, reports and queue ETA; speed sparklines; conflict dialog with Compare |
| Soft delete (D) | `#recycle` and Leviathan's own `.leviathan-trash` per share, manifest, restore, purge and retention; the bin view in the hub; the worker refuses a silent permanent delete |
| Folder sizes (E) | Size, Own size, Items, Total files and Total folders columns, a 2 px progress bar, deltas applied by the worker after copy, move, delete and restore, counting triggered by what is on screen and selected, optional daily re-check of mapped drives |
| Split pane (F) | Two panes per tab (side by side or stacked), each with its own header, path, history, view and selection; F5 / F6 copy and move to the other pane through the queue; swap panes; same folder; the preview follows the focused pane with Move and Copy buttons |
| Test lab (G) | `docker/compose.yml` (Samba, Toxiproxy); chaos suite; NAS benchmark commands; isolation of a run's worker and state by `LEVIATHAN_DATA_DIR` |

## Gate

| Gate | Result |
| --- | --- |
| 50 GB copy to the NAS survives 3 cable pulls and a killed UI, no corrupt files | **Not run: needs you at the machine.** Procedure below. The same behaviour was shown at small scale (see "How it was verified") |
| Sustained large-file copy ≥ 105 MB/s on the 1 GbE link | **Just under: 102 MB/s** (2 GiB in 21.0 s) and **101 MB/s** (4 GiB in 42.4 s), no read-back check, to `\\Diskstation\Public\Scratch` on 2026-11-06, one run each, Windows copier through the real job host. With the read-back check (the default for network copies): **53 MB/s**. Not a pass; see below |
| NAS first rows < 400 ms for 10,000 items | **Not measured at the budget's size**: the scratch folder holds 3 items (first rows cold 30 ms, p95 3 ms). A folder of 10,000 items is needed; procedure below |
| 10,000-item local folder, first rows < 100 ms (p95) | provider 4.1 ms; through the view model 3.6 ms; from the cache 0.0 ms: **PASS** |
| New tab < 150 ms | 1,000 items, first rows 4.1 ms p95: **PASS** |
| Cold start to the first folder < 600 ms | **FAIL (carried over from Phase 1): median 1,037 ms, worst 3,114 ms**, published ReadyToRun, six starts on a busy machine |
| Zero UI stalls over 50 ms | **Not shown** (see "Still unverified or open") |

### What the NAS runs showed

Run from this machine against `\\Diskstation\Public\Scratch` (named by Brett as a dedicated scratch folder; each copy wrote one temp file of random data into a new sub folder and removed it, the 3 items that were there are untouched). Single runs, other work on the machine and the NAS unknown.

- Copy throughput is **101 to 102 MB/s** against the 105 MB/s budget, the same at 2 GiB and 4 GiB, so it is steady rather than a warm-up effect. The link tops out near 118 MB/s, so there may be 10% left in buffer sizes, the number of requests in flight, or the NAS itself. Not investigated: I would first check whether a plain Explorer or `robocopy` copy of the same file does better, which says whether the limit is the NAS or the copier.
- With the read-back check the same copy runs at **53 MB/s**, as expected (the file is read back over the same link). The default policy verifies every copy to a network location, so a user copying one big file will see about half the raw speed. Whether the 105 MB/s budget should apply with or without the check is a decision for you; I measured it without.

### Procedure for the cable-pull gate (Brett, at the machine)

Use a dedicated test folder on the NAS, never one with data you care about. Leviathan stores no passwords; the share is whatever Windows
is already connected to.

1. Make a folder of about 50 GB on a local disk (a few large files plus a few thousand small ones is the most telling mix). Note a hash
   of everything in it, for example `Get-ChildItem -Recurse -File | Get-FileHash | Export-Csv before.csv`.
2. Start Leviathan, copy the folder to a new empty folder on the NAS (drag, or copy and paste).
3. Pull the network cable for about 20 seconds while a large file is half copied. Expect: the job shows **Waiting** with a countdown,
   not Failed; the rest of the app stays responsive; other tabs are not affected.
4. Plug it back in. Expect the job to continue by itself from the last confirmed chunk (the progress does not go back to zero).
5. Repeat step 3 and 4 two more times, once while small files are being copied.
6. While the copy runs, end the **Leviathan** process in Task Manager (the UI only, not the worker). Expect the copy to carry on in the
   worker; start Leviathan again and the job is in the Operations tab.
7. When it finishes, compute the same hashes on the NAS copy and compare with `before.csv`. Expect identical, and no
   `*.leviathan-part` files left anywhere. The Operations report should say which files were verified.

Write down what you see for each step (and anything odd), especially any step where the job went to **Failed** or asked a question.

### Procedure for the two NAS budgets

Both take the path you give them, and nothing runs them by default. They never write anywhere you did not name.

```powershell
# Read only: first rows of a listing. Use a folder with about 10,000 items.
dotnet run -c Release --project tests/Leviathan.Benchmarks -p:Platform=x64 -- --nas-rows \\diskstation\test\ten-thousand

# Writes a 2 GiB temp file locally, copies it into a new sub folder of the target through the real job host, executor and Windows
# copier, prints MB/s, and removes what it made. Add --verify to include the read-back check the job applies to network copies.
dotnet run -c Release --project tests/Leviathan.Benchmarks -p:Platform=x64 -- --nas-copy \\diskstation\test 2048
```

The 105 MB/s budget is for moving bytes, so the default run does not include the read-back. Run it twice if you want both numbers. A 1 GbE
link tops out near 118 MB/s.

## How it was verified

All on Windows 11, `dotnet build Leviathan.slnx -p:Platform=x64` with zero warnings in `src/` (the full solution build cannot finish
while Visual Studio has the App's Debug output locked; the App was built into a scratch folder for these runs).

| Suite | Passed | Skipped |
| --- | --- | --- |
| Core | 190 | 0 |
| Engine | 321 | 0 |
| Data | 31 | 0 |
| Integration | 17 | 4 (the Samba lab, when it is not running) |
| ViewModels | 1,158 | 0 |
| Diagnostics | 20 | 0 |
| Interop | 75 | 0 |
| ShellHost | 35 | 0 |
| Providers.Local | 36 | 1 (100,000 real files, `LEVIATHAN_STRESS=1`) |
| Providers.Cloud | 20 | 0 |
| Chaos | 3 | 1 (the cable-pull test, when the lab is not running) |
| **Total** | **1,906** | 6 |

- **Chaos suite** (`tests/Leviathan.Chaos.Tests`): a harness process running the real job host, copy executor and file journals is
  killed at random points during a copy and restarted; after every kill no file with a finished name differs from its source, and the
  last run completes with no part files (120 files by default; `CHAOS_FILES=1000` ran in 1 m 51 s and passed). Disk full and access
  denied on chosen files. The cable pull is done with Toxiproxy against the Samba lab through the Windows SMB client: the job goes to
  Waiting and finishes whole when the line is back (passed four times in a row, about 8 s each, at 24 files, not 50 GB).
- The chaos suite found a real defect, fixed in G2: one bad file in a folder stopped the rest of that folder; now that file is skipped
  and reported, the rest copies, and a move keeps the source folder while a skipped file is in it.
- The copy path is covered by unit tests with fault injection (drop after N bytes, disk full, access denied), verify policy, conflict
  rules, soft-delete manifest and restore, and folder-size delta propagation.
- Every UI slice was started, but **most of the Phase 2 UI has not been seen on screen by me**: the progress bar, the panes and headers,
  the mouse back and forward buttons, F5 / F6 / swap against a real copy, and the preview buttons were checked by compile, tests and a
  start with no crash bundle. The UAT playbooks (`docs/uat/`) exist for E1, F1; E1b, E2, F2 and F3 have none yet.

## Still unverified or open

- **The two NAS gates and the cable-pull gate** above: they need you.
- **UI smoke tests** (`tests/Leviathan.UI.Tests`): 4 to 6 of 12 failed in each of three runs on this busy machine, with different tests failing each time, so the numbers are noise rather than evidence. They need a quiet machine. They were stopped after a run sent a real folder (`.aws`, empty) to your Recycle Bin; it was restored. Since then a run with its own `LEVIATHAN_DATA_DIR` gets its own worker and state, and Delete waits for a verified selection, but the suite has **not been re-run** since. The 40,000-file view-switching test showed 5 to 15 s freezes in a Debug build and was not investigated.
- **UI stalls over 50 ms**: the hub and settings pages are now laid out invisibly at idle; an A/B run showed the hub-open stall (100 to 110 ms in every run of the old build) gone. Tab tear-off after a new tab (262 ms in one run) and sidebar navigation were not reproduced reliably and are not fixed.
- **Cold start** is still about 1.0 s against 600 ms. It needs a lighter `MainWindow` (`x:Load`, lazy chrome icons), which was not attempted.
- **C3 (Operations panel)** is built and was left unticked in the plan; the plan entry should be ticked once you have used it.
- Network paths of the form `\\server\share` without a drive letter are not counted for folder sizes.
- The panes are fixed half-and-half (no draggable gap); the second pane has no Sign-in button.
- A running folder count is not interrupted when the selection changes; rename is not covered by folder-size deltas; Items goes blank after a delta that changes folders until the folder is recounted.
- Undo of a partly copied folder (some files skipped) does not remove what arrived.
- Two tests are flaky and not fixed: `LiveRefreshTests` and `ConcurrentWriters_DoNotFail`.
- Not built: Shift+Backspace for Up, middle-click Back for the history list, double-click empty area for Up.

## Deviations from the spec

See `docs/decisions.md` (entries marked Phase 2). The main ones: folder sizes of network folders are shown with their age and counted
on demand; the split pane is per tab, each pane a full tab view model; the lab test harness is a small stand-in for the worker, because
the real worker takes a per-user mutex and the user's own data folder.
