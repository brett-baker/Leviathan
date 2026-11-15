# UAT: P2-B The copy engine: part files, resume, verify and failures

**Goal:** copy and move large and awkward data so that nothing is ever half-written, lost or silently wrong, even if the app, the worker or the share dies.
**Built in:** Phase 2 slices B1 to B6, A7, G5 (`docs/phase-2.md`), Phase 5 G3 fixes (`docs/phase-5-stress.md`), spec §12.1, §12.2, §3.3.
**Time:** about 45 minutes. The network steps need the local share.

## Setup

1. Fixtures (elevated for the share): `.\tools\New-UatFixtures.ps1 -Share`.
2. Scratch settings and start:
   ```powershell
   $env:LEVIATHAN_DATA_DIR = "D:\Playbooks\data"
   & .\src\Leviathan.App\bin\x64\Debug\net10.0-windows10.0.26100.0\Leviathan.exe
   ```
3. Folders: sources `07-bulk-copy`, `04-attributes` (the 6 GiB sparse file), `02-odd-names`, `03-long-paths`; targets `08-targets\target-a`, `target-b`. Open the status hub (**Ctrl+Shift+O**) on **Operations** so you can watch.

## Steps

### Part files and completeness

| # | Do | Expect |
|---|----|--------|
| 1 | Copy `07-bulk-copy` into `target-a` and, while it runs, look in `target-a\07-bulk-copy` with Explorer. | Files being written have the name `*.leviathan-part`; a file gets its real name only when complete. No half-named files at the end. |
| 2 | When done, compare: `Get-ChildItem D:\Playbooks\uat\07-bulk-copy -Recurse -File \| Measure-Object Length -Sum` against the copy. | The same file count and total bytes. Timestamps and attributes are kept (check one file's *Modified* and a read-only flag). |
| 3 | Copy `04-attributes` (it has `hidden.txt`, `read-only.txt`, the empty file, the 1-byte file and the **6 GiB sparse file**) into `target-b`. | All items arrive. The big file shows progress, finishes, and is verified by sampling (it is over 4 GB). Used disk space of the copy stays small if the volume supports sparse files. |

### Stop, pause, kill

| # | Do | Expect |
|---|----|--------|
| 4 | Start a copy of `07-bulk-copy` to `target-b`. In **Operations** press **Pause**, wait, then **Resume**. | Progress and speed freeze, then continue from where they were. |
| 5 | Start it again to a new target folder and press **Cancel** half way. | The job ends as cancelled. Part files for unfinished items are removed; finished items stay. No `*.leviathan-part` remains. |
| 6 | Start a copy and kill the **UI**: `Stop-Process -Name Leviathan -Force`. Wait 20 seconds, start the app, open **Operations**. | The job kept running in the worker and is finished (or still running). It completes without duplicates. |
| 7 | Start a big copy and kill the **worker**: `Stop-Process -Name Leviathan.Worker -Force`. | The UI notices, restarts the worker, and the job **resumes from the last confirmed byte** rather than starting again (the log line and progress show it). |
| 8 | Start a copy and **restart Windows** (or sign out) if you can spare it. | After sign-in the job is in the journal and resumes when the app starts. |

### Verify

| # | Do | Expect |
|---|----|--------|
| 9 | In the status hub (**Ctrl+Shift+O**) open **Quick settings** and find **Check copies against the original**: choose *Every file in full* (the others are *Moves and network copies (recommended)* and *Moves only*). Copy `07-bulk-copy`. | Each file is hashed (xxHash3) and compared; a verification failure would show as an error row. A mismatch is simulated by test only; here you confirm there is no error and speed stays acceptable. |
| 10 | Move a folder to **another drive** (cross-volume) if you have one. | The source is deleted **only after** the copy is verified. While it runs both exist. |
| 11 | Move a folder within the same drive. | It happens at once (a rename), with no copying of data. |

### Failures and names

| # | Do | Expect |
|---|----|--------|
| 12 | Copy a file into a folder that is **read-only for you** (deny write permission via Explorer's Security tab on a folder you made in `08-targets`). | The job pauses with a **prompt** on the row: Retry, Skip, **Skip all like this**, and **Skip, do as administrator** (which tries that one item elevated, with a UAC prompt). Choose **Retry** after fixing the permission; it continues. |
| 13 | Fill a small target (a tiny VHD or a nearly full USB stick, if you have one). | The job fails early with a "not enough free space" message **before** copying. |
| 14 | Copy `02-odd-names` (spaces, Unicode, emoji, a trailing dot, a trailing space, `CON.txt`, a 200-character name) into `target-a` and then into the share `\\localhost\LeviathanUat\08-targets\target-b`. | Everything copies on a local disk with the exact names. On the share, a name the share refuses is changed safely (and the report says so); nothing is dropped. |
| 15 | Copy `03-long-paths` (a path over 260 characters) and delete the copy afterwards with Shift+Del. | It copies and deletes. The list shows long paths without error. |
| 16 | Open the **report** of a finished job (row menu, Report). | A list of items with Done, Skipped, Failed, and where each went. *Show destination* and *Show source folder* open Explorer. |

### Over the network (local share)

| # | Do | Expect |
|---|----|--------|
| 17 | Copy `07-bulk-copy` from the PC **to** `\\localhost\LeviathanUat\08-targets\target-a`. | It runs. Status shows the link speed; throughput is high on loopback. Files are verified (network copies are always checked). |
| 18 | During a share copy, **remove the share** (elevated): `Remove-SmbShare -Name LeviathanUat -Force`. | The job goes to **Waiting** with a countdown ("retrying in …"), not Failed. The rest of the app stays responsive. |
| 19 | Re-create the share **without** rebuilding the fixtures (elevated): `New-SmbShare -Name LeviathanUat -Path D:\Playbooks\uat -FullAccess "$env:USERDOMAIN\$env:USERNAME"`. (Do not use `-Share` on the fixture script here: it deletes and rebuilds the fixtures mid-test.) | The waiting job resumes by itself and completes without duplicates or part files left. |
| 20 | Leave the share removed for more than 30 minutes (optional, or shorten the limit in the settings file). | The job gives up and fails with a clear message; the work already copied stays. |

## Look at (tests cannot judge these)

- The **speed** you see against Explorer or `robocopy` for the same copy (the budget is 105 MB/s on a 1 GbE NAS; local disks are faster).
- Is the **prompt** on a permanent error understandable? Does "Skip all like this" do what it says?
- Is the waiting countdown reassuring?

## Known edge cases

- A crash mid-file on an **SFTP or FTP** server restarts that file (no resume), and server transfers are checked by size only (see `E-remote-locations`).
- Resume needs the same job request; changing the job's options starts clean.
- Alternate data streams (tags) are copied; ACLs are optional and not copied by default.
- The 100 GB and real-NAS cable-pull runs are the Phase 2 gate for you at the machine (`docs/phase-2.md`); this playbook uses the local share only.

## Result

Pass / fail / notes, and anything to tweak.

## Clean up

```powershell
# elevated
.\tools\New-UatFixtures.ps1 -RemoveShare
Remove-Item -Recurse -Force "D:\Playbooks\data"
```
