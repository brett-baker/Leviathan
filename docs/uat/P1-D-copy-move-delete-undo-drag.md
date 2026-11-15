# UAT: P1-D Clipboard, copy and move, delete, undo, and drag and drop

**Goal:** copy, move, delete and restore files with the keyboard, the clipboard and the mouse, with the Recycle Bin and undo behaving like a safety net.
**Built in:** Phase 1 slices E1 to E5, D9 (`docs/phase-1.md`), Phase 2 B6 (the engine behind them), spec §8.4, §12.1, §12.5.
**Time:** about 40 minutes.

## Setup

1. Fixtures: `.\tools\New-UatFixtures.ps1` (folders `01-basic`, `05-conflicts`, `07-bulk-copy`, `08-targets`, `02-odd-names`).
2. Scratch settings and start:
   ```powershell
   $env:LEVIATHAN_DATA_DIR = "D:\Playbooks\data"
   & .\src\Leviathan.App\bin\x64\Debug\net10.0-windows10.0.26100.0\Leviathan.exe
   ```
3. Open two tabs: `D:\Playbooks\uat\01-basic` and `D:\Playbooks\uat\08-targets\target-a`.

## Steps

### Clipboard

| # | Do | Expect |
|---|----|--------|
| 1 | In `01-basic` select `notes.txt` and `todo.md`, press **Ctrl+C**, go to the `target-a` tab, press **Ctrl+V**. | Both files appear in `target-a` (the list reloads) and the status bar says how it went. The originals stay. |
| 2 | Select `report.docx`, **Ctrl+X**, paste in `target-a`. | It moves: gone from `01-basic`, present in `target-a`. A second **Ctrl+V** does nothing. |
| 3 | Paste into the **same** folder you copied from. | A numbered copy is made (`notes (2).txt`), never an overwrite. |
| 4 | Copy a file in Leviathan, then paste into **Explorer**. Copy a file in Explorer, then paste in Leviathan. | Both directions work. In Explorer, a Leviathan *cut* moves the file. |
| 5 | Check the **Paste** button and the Edit/command bar with an empty clipboard. | Paste is greyed with a tooltip that says why. |
| 6 | Copy files, then use **Paste into new folder** (from the palette). | A new folder is created and the files go into it. |

### Delete, Recycle Bin and undo

| # | Do | Expect |
|---|----|--------|
| 7 | Select `todo.md` in `target-a`, press **Del**. | It disappears from the list and is in the **Windows Recycle Bin**. No prompt. |
| 8 | Press **Ctrl+Z**. | It returns to the same place. **Ctrl+Y** deletes it again. |
| 9 | Press **Shift+Del** on a file. | A confirmation dialog appears; **Cancel** leaves the file; confirming deletes it permanently (not in the Recycle Bin). |
| 10 | Delete a read-only file (`04-attributes\read-only.txt`) with **Shift+Del** (copy it first). | It is deleted anyway. |
| 11 | Rename a file with **F2**, then **Ctrl+Z**. | The old name comes back. |
| 12 | Delete a file, close the app, start it again, press **Ctrl+Z**. | The delete is undone even after a restart (the undo list is journaled). |
| 13 | Delete, undo, delete a folder with sub-folders and undo. | The folder and its contents return. |

### Conflicts

| # | Do | Expect |
|---|----|--------|
| 14 | Copy the contents of `05-conflicts\source` into `05-conflicts\destination`. | A conflict dialog opens once for the job (see the conflict dialog playbook, P2-C, for all of its choices). Choose **Keep both**: new names like `same-name-newer (2).txt`, nothing is lost. |

### Drag and drop

| # | Do | Expect |
|---|----|--------|
| 15 | Drag a file from `01-basic` onto a folder row **on the same drive**. | The tooltip says **Move**. The folder row highlights; the file moves. |
| 16 | Drag with **Ctrl** held. | The tooltip says **Copy**. The file is copied. |
| 17 | Drag with **Alt** held. | The tooltip says shortcut (link) if links are supported; otherwise it says it is not available. |
| 18 | Hold **Shift** while dragging between drives (if you have a second drive) or onto a network share. | Move is forced; the tooltip says the original is removed **only after the copy is checked**. |
| 19 | Drag a folder onto itself and onto its own sub-folder. | The drop is refused with a reason. Dragging items onto the folder they are already in is also refused. |
| 20 | Drag a file over a **tab** and hold for about 0.6 s. | The tab is selected. Drop into that tab's folder. |
| 21 | Drag over a **sidebar** folder and hold about 0.7 s. | The folder expands. Drop it into a folder row. |
| 22 | Drag onto a **breadcrumb segment**. | It highlights; dropping puts the file in that folder. |
| 23 | Drag onto the **Favorites** header. | A folder is pinned; files are not (refused). |
| 24 | Drag several selected files by pressing on one of them. | The whole selection is dragged. Dragging from empty space draws a rubber band instead. |
| 25 | Drag a file from Leviathan to Explorer and the desktop, and a file from Explorer into Leviathan. | Both directions work. |
| 26 | Drop on the empty area of the list. | The files go into the folder being shown. |

### Larger jobs

| # | Do | Expect |
|---|----|--------|
| 27 | Copy `07-bulk-copy` (five 64 MB files and 200 small files) to `target-b`. | A job runs in the worker; the activity strip shows a line under the address bar and the status bar shows an operations chip. The window stays responsive throughout. |
| 28 | Kill the app while the job runs (`Stop-Process -Name Leviathan -Force`), start it again. | The job continues (it lives in the worker), and finishes without duplicates or half-written files (`*.leviathan-part` files are gone at the end). |

## Look at (tests cannot judge these)

- Do the drag **tooltips** (Move, Copy, Shortcut) read clearly and update as you press keys?
- Do the highlight, tab switch and tree expansion feel responsive?
- Is the wording after a paste or delete (status bar) helpful?

## Known edge cases

- Undo of a **copy** or **move** is not built (only rename and delete); a permanent delete cannot be undone by design.
- Pasting text paths is not supported; the Windows clipboard must hold files.
- Cut items are not dimmed.
- Dropping files on another window's tab strip is not built (tabs themselves move between windows).
- `Y:`, `Z:` and the real NAS are never used here; network drops are covered in P2-A and P2-B.

## Result

Pass / fail / notes, and anything to tweak.

## Clean up

```powershell
Remove-Item -Recurse -Force "D:\Playbooks\data"
.\tools\New-UatFixtures.ps1      # rebuilds the fixtures for the next playbook
```
