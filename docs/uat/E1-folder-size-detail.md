# UAT: E1 Folder size detail and age

**Goal:** see what a folder holds and how old its size is: in the Size column, the tooltip, the drive card and the preview pane.
**Built in:** commit `dc39cdb`, spec §12.7.
**Time:** about 10 minutes.

## Setup

1. Build the fixtures (never test with real data):
   ```powershell
   .\tools\New-UatFixtures.ps1 -Share      # elevated; the network steps need the share
   ```
   Fixture root: `%LOCALAPPDATA%\Temp\leviathan-uat\uat`.
2. Map the test share to a drive letter that is free (not Y: or Z:):
   ```powershell
   net use U: \\localhost\LeviathanUat
   ```
3. Start the app with scratch settings, so your real settings, favourites and saved servers are untouched:
   ```powershell
   $env:LEVIATHAN_DATA_DIR = "$env:LOCALAPPDATA\Temp\leviathan-uat-data"
   & .\src\Leviathan.App\bin\x64\Debug\net10.0-windows10.0.26100.0\Leviathan.exe
   ```
   Folder sizes must be on: Settings, Files, "Show folder sizes", and Quick settings, "Folder sizes on network drives".

## Steps

| # | Do | Expect |
|---|----|--------|
| 1 | Open `%LOCALAPPDATA%\Temp\leviathan-uat\uat` (local) and show the Size column. | Folders show `…`, then a size. A size that is still counting starts with `≥`. |
| 2 | Hover the size of `01-basic`. | Tip like `5 MB · 9 files in 4 folders · counted just now`. |
| 3 | Open `U:\` (the mapped share). | The drive card shows the usual usage lines. A "Counted:" line appears under them after a moment, with the total, file and folder counts and "counted … ago". |
| 4 | In `U:\`, look at the Size column. | Each folder's size ends with its age, for example `5 MB · now`. The local folders in step 1 had no age in the cell. |
| 5 | Click `01-basic` once (in `U:\` or locally). | The preview pane shows the folder name and the same size line. If it was not counted yet it says "Counting…" and then fills in without another click. |
| 6 | Click a file, then click empty space. | The preview pane's name and size lines disappear (the Phase 3 note stays). |
| 7 | Click `01-basic` on `U:\`, add a file to it from Explorer, go back and forward in the tab. | The size may be shown in italic until recounted. The tip says "out of date, counted …" while it is. |
| 8 | In Settings, turn "Show folder sizes" off. | Size cells for folders are empty, the tip is gone, the card line and preview size disappear. |
| 9 | Open `U:\` again after the app has been running an hour or more `[optional]`. | The age in the cell reads `1 h`, later `2 d`; the tip reads "counted 2 days ago". |
| 10 | Open `%LOCALAPPDATA%\Temp\leviathan-uat\uat\04-attributes`. | The 6 GiB sparse file is a file, so it shows its own size and no tip. The folder total uses what is on disk and does not hang the window. |

Steps 3, 4, 5 and 7 need the mapped share. Do not use Y:, Z: or `\\192.168.1.10`.

## Look at (tests cannot judge these)

- Is `412 GB · 3 d` readable in the Size column at its usual width, or does it get cut off?
- Is the tooltip wording clear: "at least", "out of date, counted …"?
- Does the "Counted:" line on the card look right under the usage line?
- Does the preview pane feel steady while the count finishes (no jumping, no flicker)?
- Does the window stay responsive while a big folder is counted?

## Known edge cases

- A raw `\\server\share` path is not counted (no drive letter). Only drive letters work, mapped ones included.
- Only the folders in the listing (first 64), the selected folder and a drive root are counted. Counting what is merely on screen at once, and updating sizes after your own copies and deletes, is E2; until then a size can be old after a copy.
- The age shows in the cell only for mapped drives. Local folders show it in the tip only.
- Not yet run against a real NAS or by eye: the layout is unverified.

## Result

Pass / fail / notes, and anything to tweak.

## Clean up

```powershell
net use U: /delete
.\tools\New-UatFixtures.ps1 -RemoveShare   # elevated
Remove-Item -Recurse -Force "$env:LOCALAPPDATA\Temp\leviathan-uat-data"
```
