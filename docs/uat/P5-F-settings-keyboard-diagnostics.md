# UAT: P5-F Settings pages, the Keyboard editor and the Diagnostics window

**Goal:** change any setting from the sheet and see it apply without a restart, rebind and share keys, and read crashes and reports ready to hand to Claude.
**Built in:** Phase 5 slices F1 to F3 (`docs/phase-5.md`), spec §17, §20.
**Time:** about 45 minutes.

## Setup

1. Fixtures: `.\tools\New-UatFixtures.ps1`.
2. Scratch settings and start (never your real settings):
   ```powershell
   $env:LEVIATHAN_DATA_DIR = "D:\Playbooks\data"
   & .\src\Leviathan.App\bin\x64\Debug\net10.0-windows10.0.26100.0\Leviathan.exe
   ```
3. Press **Ctrl+,** to open the settings sheet.

## Steps

### The sheet and search

| # | Do | Expect |
|---|----|--------|
| 1 | Look at the page list. | **General, Appearance, Files and folders, Tabs and windows, Network, Operations, Delete, Media and preview, Tags, Search and index, Keyboard, Advanced**. Every page opens (none says "arrives later"). |
| 2 | Type `bandwidth` in the search box. | The matching setting is listed with its page; choosing it jumps there. Try `terminal`, `hover`, `retention`, `ctrl alt`, `index`. |
| 3 | Make the sheet full-window (expand button) and back; close with **Esc**. | It rises from the bottom (about 70%), expands, and closes. |

### One setting from each page (each applies without a restart)

| # | Page and setting | Expect |
|---|------------------|--------|
| 4 | **General:** what to open at startup; where a new tab opens; the terminal. | New tab opens where chosen; the terminal choice is used by **Ctrl+Alt+T** at once. |
| 5 | **Appearance:** theme, density, accent, Mica, icon size and set. | See P1-F. Note any that need a restart. |
| 6 | **Files and folders:** show hidden files, system files, folder sizes, show deletes at once, Git status, quick targets, recheck drive sizes daily. | Hidden files toggle at once; the Git column appears in a repository (P5-E). |
| 7 | **Tabs and windows:** closing the last tab (close the window, or go home). | Closing the last tab does what you chose. |
| 8 | **Network:** folder sizes and thumbnails on network drives. | With thumbnails on for network drives, a picture folder on the share shows thumbnails (slower); off shows icons only. |
| 9 | **Operations:** *Operations at once*, *At once on one drive or share*, *Background jobs at once*, *Speed limit (MB per second, 0 = none)* and *Use a different limit at certain hours* (From, To, Limit then). Copy verification is in the status hub's Quick settings (*Check copies against the original*). | Setting the bandwidth to 5 MB/s slows a running copy within a moment (P2-C). Setting the number of jobs at once to 1 queues a second job. |
| 10 | **Delete:** how long deleted items are kept, size limit, hidden recycle folder on shares. | Persisted; used by the Soft-delete bin (P2-D). |
| 11 | **Media and preview:** triage options, skim playback, hover scrub. | Turn **Scrub videos by hovering** off: hovering a video thumbnail no longer scrubs. |
| 12 | **Tags:** tag names, colours, order and keys 1 to 9, tinting rows. | See `F-colour-tags`. |
| 13 | **Search and index:** folders to index (add `D:\Playbooks\uat\15-search`) and the indexer status. | The folder is crawled; the status shows progress and a pause/now control (see `B-C-search-index`). |
| 14 | **Advanced:** open the settings file; diagnostics; clear caches. | The JSON file opens; **Show the settings file** reveals it; **Read the file again** applies an edit made by hand; **Clear remembered folders / thumbnails / the activity log** each ask no question and do what they say. |
| 15 | Edit the settings file by hand (change a value), save, choose **Read the file again**. | The app applies it without a restart. Corrupt the JSON on purpose: the app keeps its last good settings and reports it rather than crashing. |

### Keyboard editor

| # | Do | Expect |
|---|----|--------|
| 16 | Open the **Keyboard** page. | "Every command and the keys that run it." A filter box (**Filter the list**) and a list of every command with its category and key. |
| 17 | Filter by `new tab`. Select it. | A capture box **Click here, then press the keys** and the buttons **Use these keys**, **No key**, **Reset**. |
| 18 | Click the box and press **Ctrl+Alt+J**. | The box shows the chord. Press **Use these keys**: the row shows `Ctrl+Alt+J` and the shortcut works at once; the palette and tooltips show it. |
| 19 | Try a chord that another command uses (for example `Ctrl+W`). | The page tells you which command has it, and on **Use** the key **moves** to this one; the other row now shows no key. |
| 20 | Select a command and press **No key**. | The row has no key; the command still works from the palette. |
| 21 | Select a changed command and press **Reset**. | Its default key returns (and a key it took is given back if free). |
| 22 | Press **Reset all keys**. | Everything returns to the defaults. |
| 23 | Rebind two commands, then **Export keys…**. | A save picker; the file is JSON with only your changes. |
| 24 | **Reset all keys**, then **Import keys…** and choose the file. | Your bindings return. Import a file with an unknown command or a clash: unknown ones are skipped and listed, clashes are reported, nothing crashes. |
| 25 | Restart the app. | Your bindings stay. Look at the settings file: only your changes are stored, not every default. |
| 26 | Press a chord that Windows or the app reserves (Alt+F4, F10) in the capture box. | It is refused or handled sensibly; you cannot lock yourself out of closing the window. |

### Diagnostics window

| # | Do | Expect |
|---|----|--------|
| 27 | **Advanced > Diagnostics** (or the Options menu). | The **Leviathan diagnostics** window: worker connection state, **Compute folder size** box, **Operations** (jobs), UI stalls over 50 ms since start, and **Crashes and reports**. |
| 28 | Type `D:\Playbooks\uat\01-basic` in **Folder** and press **Compute folder size**. | "Queued: …" then a job row with progress. |
| 29 | In **Something is wrong but nothing crashed? Say what you saw (optional)** type a note and press **Report a problem**. | "Saved a report to {folder}. Select it and choose Copy for Claude." A new row appears in the crash list. |
| 30 | Select that row. | Rows are **grouped by signature** with a count and when it last happened. The buttons **Copy for Claude**, **Open folder**, **All crash folders** and **Refresh** work. |
| 31 | Press **Copy for Claude** and paste into a text editor. | `report.md`: exception chain (if any), your note, the stack with repository permalinks, the last user actions, surrounding log lines. The message "Copied the report. Paste it to Claude with the repository open." appears. |
| 32 | **Open folder** and **All crash folders**. | Explorer opens at the bundle (`%LOCALAPPDATA%\Leviathan\crashes`, or your scratch data folder). |
| 33 | Delete a bundle folder in Explorer, press **Refresh** and **Copy for Claude** on the vanished row. | "That crash report is gone. Refresh the list." |
| 34 | With no crashes: | "No crashes or reports yet." |
| 35 | Force a real crash (only in a throwaway scratch run): kill the UI with `Stop-Process -Name Leviathan -Force`, start the app, open Diagnostics. | A hard kill is not an app crash and leaves no bundle; an unhandled exception would. (Real crash capture is exercised by the Diagnostics tests; do not try to crash the app on purpose beyond this.) |
| 36 | **Ctrl+Shift+F12** (Report a problem). | **Known gap:** this key is not wired yet; use the button in this window. |

## Look at (tests cannot judge these)

- Are the settings pages **well organised and worded**? Can you find a setting without the search?
- Is the Keyboard editor **clear about clashes** before it moves a key?
- Is the crash list readable? Would you know which row to hand to Claude?
- Does the sheet feel quick to open and to switch pages?

## Known edge cases

- Settings that depend on something installed (Everything, 7-Zip, git, WSL) show their effect only when that is present.
- Three settings were left out because they do nothing yet (show file extensions, single click to open, Ctrl+Tab in recent order).
- Only user changes are stored for keys; if a future version changes a default, an unchanged key follows it.
- Hardware-specific keys (media keys, AltGr combinations) may not be captured.

## Result

Pass / fail / notes, and anything to tweak.

## Clean up

```powershell
Remove-Item -Recurse -Force "D:\Playbooks\data"
```
