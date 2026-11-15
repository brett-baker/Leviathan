# UAT: P3-H Eager list updates, Properties, path in the pane header and slow-click rename

**Goal:** see the result of a delete, move or copy in the list at once (dimmed until the disk confirms), and use the small tweaks added after triage.
**Built in:** Phase 3 slices H1 and H2 (`docs/phase-3.md`), spec §12.5a.
**Time:** about 20 minutes.

## Setup

1. Fixtures: `.\tools\New-UatFixtures.ps1` (folders `01-basic`, `07-bulk-copy`, `08-targets`).
2. Scratch settings and start:
   ```powershell
   $env:LEVIATHAN_DATA_DIR = "D:\Playbooks\data"
   & .\src\Leviathan.App\bin\x64\Debug\net10.0-windows10.0.26100.0\Leviathan.exe
   ```
3. Throttle the worker so jobs last a few seconds: **Settings > Operations > Bandwidth limits** at about 20 MB/s.

## Steps

### Eager updates (setting: Files and folders > Show deletes, moves and copies at once)

| # | Do | Expect |
|---|----|--------|
| 1 | Make sure the setting is **on**. Copy `07-bulk-copy` into `target-a` and look at `target-a` while it runs. | The new folder appears **at once**, dimmed as pending, and becomes normal when the copy completes. |
| 2 | Delete a file with **Del**. | The row vanishes immediately (before the Recycle Bin call returns). If the job fails (for example a locked file), the row **comes back** with the reason shown. |
| 3 | Move a few files from one pane to the other with **F6** (split tab). | They leave the source list at once and appear dimmed in the target; the dim clears when the job ends. |
| 4 | Make a move fail on purpose (move a file that is open and locked in another program). | The row returns to the source list and the target's pending row disappears. |
| 5 | Turn the setting **off** and repeat steps 1 and 2. | Rows change only when the job has finished. |
| 6 | With the setting on, undo a delete (**Ctrl+Z**). | The row returns at once, dimmed, then normal. |

### Small tweaks

| # | Do | Expect |
|---|----|--------|
| 7 | Select a file and press **Alt+Enter** (or right-click > Properties). | The **Windows Properties** dialog for that file opens (through `SHObjectProperties`), including the Details and Security tabs. |
| 8 | Split the tab and look at the pane headers. | Each pane header shows its **path**; the focused pane's header is emphasised. |
| 9 | Click a selected file once, pause for about a second, then click again. | A slow second click starts an inline **rename** (like Explorer). A quick double-click opens the file instead. |
| 10 | Look at the pane header and command bar buttons. | They are icon buttons with tooltips; the tooltip names the key. |

## Look at (tests cannot judge these)

- Is the **dimmed pending** look clear? Does a row coming back after a failed move feel jarring or explained?
- Does the slow-click rename trigger by accident when you only meant to select?
- Is the path in the pane header readable when very long?

## Known edge cases

- Eager updates are shown only in the folders you are looking at; other tabs update when their jobs end.
- Steps 2 and 4 (a failed job giving the row back) follow the spec (§12.5a) but were mostly checked by unit tests; note exactly what you see.
- What happens to pending rows if the worker is killed mid-job is not specified here; the list is rebuilt from the disk on the next load.

## Result

Pass / fail / notes, and anything to tweak.

## Clean up

```powershell
Remove-Item -Recurse -Force "D:\Playbooks\data"
.\tools\New-UatFixtures.ps1
```
