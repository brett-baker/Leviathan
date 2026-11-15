# UAT: P2-D Soft delete on shares and the Soft-delete bin

**Goal:** delete on a network share without fear: items are moved aside, can be restored, and are purged on a schedule you control.
**Built in:** Phase 2 slices D1 to D3 (`docs/phase-2.md`), spec §12.5.
**Time:** about 30 minutes. Needs the local test share (elevated PowerShell).

## Setup

1. Fixtures and share (elevated): `.\tools\New-UatFixtures.ps1 -Share`
2. Scratch settings and start:
   ```powershell
   $env:LEVIATHAN_DATA_DIR = "D:\Playbooks\data"
   & .\src\Leviathan.App\bin\x64\Debug\net10.0-windows10.0.26100.0\Leviathan.exe
   ```
3. Open `\\localhost\LeviathanUat\08-targets\target-a` in a tab and copy `01-basic` files there so there is something to delete.
4. Never test delete on a real NAS here.

## Steps

### Deleting on a share

| # | Do | Expect |
|---|----|--------|
| 1 | Select `notes.txt` on the share, press **Del**. | The file leaves the list **instantly** (a rename into a trash folder, not a copy), even for a big file. |
| 2 | Look at the share root in Explorer: `D:\Playbooks\uat`. | A hidden folder **`.leviathan-trash`** holds a dated batch folder with the file and a `manifest.jsonl` with the original path. (If the share is a Synology/QNAP with a recycle bin, that folder is used instead.) |
| 3 | Press **Ctrl+Z**. | The file returns to its original path. The manifest line is pruned. |
| 4 | Delete a **folder** with sub-folders on the share and undo. | The whole folder returns. |
| 5 | Press **Shift+Del** on a share file. | A confirmation; confirming deletes it permanently (no trash copy). |
| 6 | In **Settings > Delete**, turn off the hidden recycle folder on shares, then delete a file. | A permanent delete happens (nothing to undo), and the result says so, unless the NAS keeps its own bin. Turn it back on. |
| 7 | Try to delete a share's **root** (the share itself). | Refused: "A drive or share cannot be deleted." |

### Plain Delete by location type

| # | Do | Expect |
|---|----|--------|
| 8 | On a **local drive**, press **Del**. | Windows **Recycle Bin**. |
| 9 | On a **mapped network drive** (map the test share to `X:` with `net use X: \\localhost\LeviathanUat`), press **Del**. | Soft delete through the share (not the Windows bin, which does not exist for a mapped drive). |
| 10 | On a drive with no Recycle Bin (a USB stick, if you have one). | Del is refused with the message to use **Shift+Del**; Shift+Del asks first. |
| 11 | Try to delete something that is already in a recycle folder. | It says it is already there and suggests Shift+Del. |
| 12 | Delete an item that disappeared a second ago (delete in Explorer, then in Leviathan before it refreshes). | The job is not red: it reports "was already gone". |

### The Soft-delete bin

| # | Do | Expect |
|---|----|--------|
| 13 | Click the **Options** gear and choose the **Soft-delete bin** (opens in the status hub). | All items soft-deleted across shares, with original path, size and time, newest first. |
| 14 | Select two items and choose **Restore**. | They return to their original folders. The rows leave the list. |
| 15 | Select one and choose **Delete permanently**. | A confirmation, then it is removed from the trash. |
| 16 | Choose **Delete old now** (purge by policy). | The Worker reports a purge job with how many items and bytes it removed. Items younger than the retention stay. |
| 17 | **Settings > Delete**: set retention to 1 day and the size cap to 1 GiB. Then check the setting persists after restart. | The values are kept (defaults: 30 days, 100 GiB). |
| 18 | Leave the app running for 2 minutes after start. | A policy purge runs by itself (and then daily) as a **background** job with a report in the Activity tab. |
| 19 | Delete the same name twice (two files called `notes.txt` from different folders) and restore both. | Neither overwrites the other: a restore never replaces a file that exists; it makes a numbered name or refuses. |

## Look at (tests cannot judge these)

- Is the **bin view** clear about where each item came from? Is restore obvious?
- Is it clear from the **status bar or message** after a delete on a share that it is a soft delete and can be undone?
- Does anything about the hidden `.leviathan-trash` surprise you when you look at the share in Explorer?

## Known edge cases

- A restore leaves the manifest line behind in one case (restore from the plain Ctrl+Z path on a share); the bin ignores lines whose item is gone.
- Auto-purge needs the worker to be running (it starts with the app).
- The Windows Recycle Bin is Windows' own; Leviathan only sends items to it.

## Result

Pass / fail / notes, and anything to tweak.

## Clean up

```powershell
# elevated
net use X: /delete
.\tools\New-UatFixtures.ps1 -RemoveShare
Remove-Item -Recurse -Force "D:\Playbooks\data"
```
