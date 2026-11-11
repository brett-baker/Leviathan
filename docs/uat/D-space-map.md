# UAT: D Space map and the Space tab

**Goal:** see where the space went in a folder, drive or share, find the big and the stale, and act on it.
**Built in:** Phase 4 slices D1–D5 (commits `8983197`, `71ee13a`, `1dec8f5`, `720e1e9`), spec §14.
**Time:** about 15 minutes.

## Setup

1. Build the fixtures: `.\tools\New-UatFixtures.ps1` (the `15-search` folder has files of many kinds, three-year-old ones, a hidden file, an empty folder and a 3 GiB sparse "disk image").
2. Start the app from Visual Studio (F5) with `LEVIATHAN_DATA_DIR` = `D:\Playbooks\data`. `LEVIATHAN_OPEN_SPACEMAP=<folder>` in the debug environment opens a map at start if you want to skip the clicks.

## Steps

| # | Do | Expect |
|---|----|--------|
| 1 | In a tab on `D:\Playbooks\uat`, press **Ctrl+Shift+M** (or right-click a folder → "Space map of this folder"). | A "Space map" window opens and fills in while it scans. |
| 2 | Look at the map. | One rectangle per file, sized by bytes; the 3 GiB disk image is by far the biggest. Colours are by kind; the legend underneath lists each with its total. |
| 3 | Click a rectangle. | It is outlined, its row is selected and opened in the tree on the left, the status line names it and its size. |
| 4 | Click a row in the tree. | The matching rectangle is outlined. |
| 5 | Double-click a rectangle of a video. | The map zooms into that file's folder. **Zoom out** (or **Backspace**) goes back. |
| 6 | Choose "Colour by age". | Old files (three years back) turn to the stale colour; the legend says "Over 2 years" with its size. |
| 7 | Choose "Over 1 MB" in the size box. | Small files disappear from the map; the tree keeps them. |
| 8 | Tick "Legend by extension". | The legend lists extensions by total size. |
| 9 | Click **Pause**, then **Resume**, then close and reopen the map of the same folder. | Pause holds the scan; reopening a finished scan shows it at once without listing again. **Rescan** lists everything again. |
| 10 | On the 3 GiB image: right-click → **Show in a tab**, then **Delete…** and confirm. | A tab shows the file; the file goes to the Recycle Bin and the map drops it at once. Restore it from the Bin afterwards. |
| 11 | Right-click a small file → **Tag** → a colour. | The file gets the tag (check in the file view). |
| 12 | Close the map. In the main window open the status hub, **Space** tab. | A compact map of the folder shown, the largest items beside it. Click a rectangle: that item is selected in the file view. **Open space map** opens the full one. |
| 13 | [lab] Map a real share (a dedicated test share, never Y:, Z: or `\\192.168.1.10`) and stop it half way with **Stop**. Reopen the map. | It continues from where it stopped, listing only the folders it had not listed. |

## Look at (tests cannot judge these)

- The picture itself: colours in light and dark mode, tile borders, readability of the legend.
- Whether the tree and the map feel linked, and the window's layout at different sizes.
- Speed on a big folder (try `C:\Tools` or another large folder): the first picture should come within a second.

## Known edge cases

- It is a **window**, not a tab (recorded in decisions).
- Cloud placeholders count as 0 bytes. Hard links are counted per name, not once.
- Not built: Compress, "find duplicates in this folder" from the map, "Rescan only changed folders" (a rescan lists everything).
- Small items of a folder share one block that selects nothing in particular.
- The scan runs inside the app, not the worker; the UI shows 50 to 150 ms stalls while a big scan runs (cause not found).
- Delete goes through the worker like the file view; the map drops the item as soon as the job is queued.

## Result

Pass / fail / notes, and anything to tweak.

## Clean up

```powershell
Remove-Item -Recurse -Force "D:\Playbooks\data"
```
