# UAT: P2-C The queue: activity strip, Operations panel, sparklines and the conflict dialog

**Goal:** run several jobs at once, see them as a coloured strip, a list and a speed graph, steer them, and answer name conflicts once per job.
**Built in:** Phase 2 slices C1 to C6 (`docs/phase-2.md`), spec §12.3, §12.4, §12.6, §16.1.
**Time:** about 40 minutes.

## Setup

1. Fixtures: `.\tools\New-UatFixtures.ps1` (folders `05-conflicts`, `07-bulk-copy`, `08-targets`, `04-attributes`).
2. Scratch settings and start:
   ```powershell
   $env:LEVIATHAN_DATA_DIR = "D:\Playbooks\data"
   & .\src\Leviathan.App\bin\x64\Debug\net10.0-windows10.0.26100.0\Leviathan.exe
   ```
3. To get jobs that last long enough to look at, set **Settings > Operations > Speed limit (MB per second, 0 = none)** (or the same box in the status hub's **Quick settings**: *Limit for all copies and moves*) to about 20 MB/s (or copy the 6 GiB file in `04-attributes`).

## Steps

### Activity strip and the chip

| # | Do | Expect |
|---|----|--------|
| 1 | Start a copy of `07-bulk-copy` to `target-a`. | A thin line appears under the address bar for the job, in the job's own colour (one of eight). While the job enumerates files the line shimmers, then it fills with progress. |
| 2 | Start two more copies to `target-b` and a new folder. | One line per job; at most **four** lines, extra jobs merge into "+N". |
| 3 | Hover a line. | A tooltip shows the job name, percent, bytes, speed and time left. |
| 4 | Click a line. | The status hub opens on **Operations** with that job. |
| 5 | Look at the **operations chip** in the status bar. | Text like "2 jobs, 1 queued, 112 MB/s, 2 min left" and a small total sparkline. Clicking it opens Operations. |

### Operations panel

| # | Do | Expect |
|---|----|--------|
| 6 | Open Operations (**Ctrl+Shift+O**). | Groups **Running**, **Queued** and **Completed / Failed**; each row has name, bar, speed with a **sparkline**, and the job's colour. |
| 7 | Select a running row and use **Pause**, then **Resume**. | The strip line goes dashed while paused; the speed drops to 0 then recovers. |
| 8 | Select a queued row: **Run now**, then change its **priority** and use **Move up / down**. | The queue order changes; Run now starts it at once. |
| 9 | Select several rows (Ctrl+click) and press **Pause**, **Cancel**. | The action applies to all selected. |
| 10 | Right-click a row. | The context menu holds pause, resume, cancel, remove, move, priority, run now, retry failed, report, open source, open destination. |
| 11 | Use the toolbar: **Pause all / Resume all** and **Clear completed**. In **Quick settings** change **Operations at once** / **At once on one drive or share**. | Each works. Lowering the limit makes extra jobs queue; raising it starts them. |
| 12 | Set the speed limit to 5 MB/s (Quick settings, *Limit for all copies and moves*), then copy a big file. | The speed settles near the limit; changing it applies to the next chunk without restarting. Tick **Use a different limit at certain hours**, set **From / To (hh:mm)** and **Limit then (MB/s)**: that limit applies only in those hours. |
| 13 | Make a job fail (copy into a folder you cannot write to). | The strip shows a **red tip** until you look at it; the row shows the error and a **Retry failed** action. |
| 14 | Open a finished job's **report**. | The per-item list with Show destination and Show source folder. Reports are kept for 7 days. |
| 15 | Put two copies to the **same NAS or drive** and one **background** job (folder sizes) on it. | The background job yields to the copy on the same device (check **Background** tab). Jobs to different devices run side by side up to the limit. |

### Hub positions

| # | Do | Expect |
|---|----|--------|
| 16 | With jobs running, dock the hub right, then bottom, then pop it out. | Rows keep updating live in each position. The chosen position is remembered after a restart. |

### Conflict dialog

| # | Do | Expect |
|---|----|--------|
| 17 | Copy everything in `05-conflicts\source` into `05-conflicts\destination` (set **Ask** as the conflict choice if the settings offer it). | **One** dialog appears for the whole job listing each conflicting file with source and destination size and date. |
| 18 | Look at the choices. | Replace, Skip, Keep both, **If newer**, **If larger**, **If different (checksum)**, and a rule **apply to all**, with per-file overrides. |
| 19 | Open **Compare** on a row. | A flyout with both files' thumbnails and metadata side by side. |
| 20 | Choose *If newer* for all and continue. | `same-name-newer.txt` (source newer) is replaced; `same-name-older.txt` (destination newer) is kept; `identical.txt` is skipped as identical; `only-in-source.txt` is copied. Check the files and the report. |
| 21 | Repeat with **Keep both**. | Conflicting names get ` (2)` and nothing is overwritten. |
| 22 | Repeat with **Skip** and with **Cancel** in the dialog. | Skip leaves the destination as it was. Cancel stops the job before anything is changed. |
| 23 | Repeat and use a per-file override (replace one, skip another) on top of a rule. | The override wins for that file only. |

## Look at (tests cannot judge these)

- The **strip colours**: are eight colours distinct from each other and from the theme in both light and dark, and for common colour blindness? Do the tooltips make up for colour alone?
- Is the **shimmer** while enumerating calm or distracting?
- Is the **conflict dialog** clear enough to answer for 100 files at once?
- Do the **sparklines** read as speed at a glance?

## Known edge cases

- Resuming a paused or waiting job after the share returns is covered in P2-B.
- A job in Waiting pulses in the strip.
- "If different (checksum)" reads both files, so it is slow on big files over a network.
- The strip merges beyond four lines; you must open Operations to see them individually.

## Result

Pass / fail / notes, and anything to tweak.

## Clean up

```powershell
Remove-Item -Recurse -Force "D:\Playbooks\data"
.\tools\New-UatFixtures.ps1
```
