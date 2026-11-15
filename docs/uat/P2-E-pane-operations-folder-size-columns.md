# UAT: P2-E Pane operations (F5, F6, swap) and the folder-size columns and deltas

**Goal:** move work between two panes with one key, and trust the folder-size columns to stay right after you copy, move or delete.
**Built in:** Phase 2 slices F2, F3, E1b, E2 (`docs/phase-2.md`), spec §15.1, §12.7. The split itself is in `F1-split-pane`; the Size column basics are in `E1-folder-size-detail`.
**Time:** about 30 minutes.

## Setup

1. Fixtures: `.\tools\New-UatFixtures.ps1` (folders `01-basic`, `07-bulk-copy`, `08-targets`).
2. Scratch settings and start:
   ```powershell
   $env:LEVIATHAN_DATA_DIR = "D:\Playbooks\data"
   & .\src\Leviathan.App\bin\x64\Debug\net10.0-windows10.0.26100.0\Leviathan.exe
   ```
3. Split a tab side by side (**Ctrl+\\**). Left pane: `D:\Playbooks\uat\01-basic`. Right pane: `D:\Playbooks\uat\08-targets\target-a`.

## Steps

### Pane operations

| # | Do | Expect |
|---|----|--------|
| 1 | Select `notes.txt` and `todo.md` in the left pane and press **F5**. | They are **copied** into the right pane's folder through the normal queue; the right list updates when the job finishes. The originals stay. |
| 2 | Select `report.docx` in the left pane and press **F6**. | It is **moved** to the right pane's folder. |
| 3 | Press **F6** in a single (unsplit) tab. | Nothing happens (F6 works only while split). **F5** in an unsplit tab is **Reload**, as usual; only while split does it copy to the other pane. |
| 4 | Press **F5** with nothing selected. | Nothing happens; no empty job is created. |
| 5 | Press **Ctrl+Shift+S**. | The two panes **swap** folders; selections and layouts travel with the pane. |
| 6 | Focus one pane and press **Alt+=**. | The other pane shows the **same folder** as the focused one. |
| 7 | Use the **View** menu. | One pane, Split side by side, Split top and bottom, Swap the panes, Show this folder in the other pane, each with its key. |
| 8 | Focus the left pane and select a file; look at the **preview pane** (Alt+P). | The preview follows the focused pane. Under it, **Move (F6)** and **Copy (F5)** buttons show only while split, and are greyed with no selection. |
| 9 | Click those buttons. | They run the same commands as the keys. |
| 10 | Copy a folder to the other pane when a name already exists there. | The conflict dialog opens (see P2-C). |

### Folder-size columns

| # | Do | Expect |
|---|----|--------|
| 11 | In the list, right-click a header: choose the columns **Own size**, **Items**, **Total files** and **Total folders** (or use View > Choose columns, group Leviathan). | Four new columns. |
| 12 | Open `D:\Playbooks\uat` (many folders). | Folder sizes arrive as the worker counts them; sizes appear one by one. A folder being counted shows a thin **2 px bar** in its Size cell (finished subfolders of all, or a sliding bar when it has none). A folder waiting in the queue is **dimmed** with a tooltip. |
| 13 | Sort by **Size**, **Items**, **Total files**. | Folders sort by their real numbers, not as zero. |
| 14 | Hover a folder's size. | The tip says what it holds and when it was counted. A partial count shows `≥` and a stale one is italic. |
| 15 | Note the size and file count of `07-bulk-copy` (5 files of 64 MB and 200 of 64 KB, about 332.5 MiB, 205 files). Copy it into `target-b`. | `target-b` gains the same bytes and 205 files **immediately** (the worker applies exact deltas up the folders), without a re-count of everything. |
| 16 | Delete the copy (Del), then press **Ctrl+Z**. | The ancestors' sizes drop, then return by the same amount. |
| 17 | Move a folder from `target-a` to `target-b`. | Both sides change by the exact amount. |
| 18 | Copy into a folder using **Replace** where sizes differ. | The result may show as stale (italic) until re-counted: the worker marks a chain stale when it cannot know the exact change. It is never silently wrong. |
| 19 | Change a folder's contents with Explorer (add a big file), then open its parent. | On visit the parent's size is noticed as stale (by last-write time) and recounted. |
| 20 | Open the drive summary card for `D:\` (P1-B) and the preview pane on a folder. | The "Counted" line and the folder size agree with the column. |

## Look at (tests cannot judge these)

- Is the **count bar** visible enough, and is the dim for waiting folders understandable?
- Do the sizes feel live while you copy, or do they lag?
- Is it clear what `≥` and italic mean without reading the tooltip?

## Known edge cases

- Network folders are not counted unless you ask or enable it in settings; their size cell shows its age.
- A share's recycle or trash folder is left out of the size of the share.
- `\\server\share` paths without a drive letter are not counted for folder sizes (known gap, `docs/backlog.md`).
- Window-level dual pane is not built (set aside in the spec): only per-tab splits exist.

## Result

Pass / fail / notes, and anything to tweak.

## Clean up

```powershell
Remove-Item -Recurse -Force "D:\Playbooks\data"
.\tools\New-UatFixtures.ps1
```
