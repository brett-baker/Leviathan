# UAT: E1 to E4 Triage mode

**Goal:** go through a folder of videos one at a time with the keyboard, keeping some, discarding others, undoing mistakes, and see a summary at the end.
**Built in:** commits `bceea50` (queue and session), `3b81212` (keys), `cfb7f4f` (Keep and Discard), `0bbfc15` (window), spec §10.1, §10.2.
**Time:** about 20 minutes.

## Setup

1. Build the fixtures (needs ffmpeg; use `pwsh`):
   ```powershell
   pwsh .\tools\New-UatFixtures.ps1
   ```
   Folder: `D:\Playbooks\uat\10-triage` with `inbox` (six clips, one called `clip5-odd name (1) [x].mp4`) and an empty `keep`.
2. Point Keep at the scratch keep folder. Create `D:\Playbooks\data\settings.json` before the first start (or edit it after closing the app):
   ```json
   { "Media": { "TriageKeepFolder": "D:\\Playbooks\\uat\\10-triage\\keep", "TriageMoveWhen": "OnKeep" } }
   ```
   `TriageMoveWhen` is also a switch in Settings (Files page: "Triage: move kept files when it ends"). The default, `AtEnd`, only
   tags a file when you keep it and moves the kept files together when triage closes (steps 19 to 23). `OnKeep` moves each file at once,
   which steps 4 to 14 assume. Without a keep folder (and outside a split tab) Keep only tags the file.
3. Start the app with scratch settings:
   ```powershell
   $env:LEVIATHAN_DATA_DIR = "D:\Playbooks\data"
   & .\src\Leviathan.App\bin\x64\Debug\net10.0-windows10.0.26100.0\Leviathan.exe
   ```
4. Open `D:\Playbooks\uat\10-triage\inbox`.

## Steps

| # | Do | Expect |
|---|----|--------|
| 1 | Click `clip1-red.mp4`, press **Alt+T**. | A "Triage" window opens. The first clip plays from about 10% in. Top: "1 / 6" and a progress bar. Right: a list of six clips. Bottom: buttons and a seek bar. |
| 2 | Look at the top left of the video. | An overlay with the file name and a line like size, length, resolution, codec, "in D:\...\inbox". It fades after about 2 seconds. |
| 3 | Press **I**. | The overlay comes back and stays. Press **I** again and it fades. |
| 4 | Press **Enter** (Keep). | The clip is moved to `keep`, gets a green tick in the list, and the next clip starts. The counts at the top say 1 kept. |
| 5 | Press **Delete** (Discard) on clip 2. | Clip 2 disappears from `inbox` (it goes to the Recycle Bin), shows a cross in the list, and clip 3 starts. The top shows the space reclaimed. |
| 6 | Press **Ctrl+Z**. | Clip 2 is back in `inbox` and the view returns to clip 2. Its cross is gone. |
| 7 | Press **Down** (Skip) on clip 2. | Moves to clip 3 with no change on disk; clip 2 shows a skip arrow. |
| 8 | Press **Up** (Previous), then **Space**, **Right**, **Left**, **]**. | Back on clip 2. Space pauses and plays; Right/Left jump 30 s (the clip is short, so it clamps); **]** raises the speed shown at the bottom right. |
| 9 | Click `clip4-short.mp4` in the right-hand list. | Jumps to it at once. |
| 10 | Click the **✓ Keep** and **✕ Discard** buttons on the next two clips. | Same result as the keys. |
| 11 | Decide the last clip. | A "Queue finished" screen: counts, and the discarded files in a list. |
| 12 | Select a discarded file, press **Restore selected**. | It returns to `inbox`; the count drops. |
| 13 | Press **Back to the queue**, then **Close**. | The triage window closes; the main window selects the file triage was on. |
| 14 | In Explorer (or the properties of a kept file), run `Get-Content "D:\Playbooks\uat\10-triage\keep\clip1-red.mp4:leviathan.tags"` in PowerShell. | JSON with the green tag, e.g. `{"v":1,"tags":[4],...}`. |
| 15 | Select two or three clips in `inbox`, press **Alt+T**. | The queue holds only the selected clips. |
| 16 | Select nothing, use the command bar button **Triage mode from here**. | Starts at the top with every video in the folder. |
| 17 | Open `09-media`, select `broken.mp4`, start triage. | The window shows an error line at the bottom for the broken file but the buttons and keys still work; Skip moves on. |
| 18 | Press **Esc**. | The window closes (with `OnKeep`, or with nothing kept, with no question). |
| 19 | Rebuild the fixtures. Remove `TriageMoveWhen` from `settings.json` (or switch the Settings option back on). Open `10-triage\inbox` in one pane and `10-triage\keep` in the other (Ctrl+\\ for a split), click a clip in the inbox pane, **Alt+T**. | Triage opens as before. |
| 20 | Keep two clips, discard one, then close with **Esc**. | Kept clips stay in `inbox` while you work (they only get the green tag). On close a question: "Move 2 kept files?", the other pane's folder named, a box "Always move them without asking", **Move** and **Not now**. |
| 21 | Press **Not now**. | The window closes; nothing moved. Repeat 19 and 20, then press **Move**. |
| 22 | After **Move**. | The two kept clips are in `keep` (the other pane shows them at once); the status bar says "Moved 2 kept files to keep". The discarded clip is in the Recycle Bin. |
| 23 | Repeat, tick **Always move them without asking**, press **Move**; repeat once more. | The second time there is no question; the kept files simply move. (Switch "Triage: ask before moving kept files" back on afterwards.) |
| 24 | Open triage on the `inbox` clips (any start). Look above the video. | The name of the current file in large letters, always visible (it does not fade like the overlay). When the queue ends it reads "Queue finished". || 25 | Press **Down** twice and look at the name. | It changes with each file; long names are cut with an ellipsis, not wrapped. || 26 | Above the list on the right press **Order: As opened ▾**. | A menu: As opened, Name A to Z, Name Z to A, Oldest first, Newest first, Largest first, Smallest first. The current one is ticked. || 27 | Choose **Largest first** while a middle clip is playing. | The clip on screen keeps playing and stays selected at the top of the undecided files; the rest are listed largest first. The keys carry on working (the focus returns to the window). || 28 | Keep one clip and skip another, then choose **Name A to Z**. | The kept and skipped clips stay at the top in the order you handled them; the undecided ones follow in name order (`clip2` before `clip10`). || 29 | Press **Down** (Skip). | It goes to the next clip of the new order. || 30 | Close triage and open it again. | The window starts with the last order you chose (inbox sessions keep their own order). |

## Look at (tests cannot judge these)

- Does the overlay read well over bright and dark footage? Is two seconds the right fade time?
- Is the list on the right easy to scan; do the tick, cross and skip marks read at a glance?
- Does moving from one clip to the next feel instant enough on local files? (On a share it will be slower; that is measured at the end of the phase.)
- Are the key choices comfortable: Enter/Delete/Down/Up/Ctrl+Z? Alt+T instead of the spec's plain T is a decision to review.
- Is the file name big enough and in the right place? Is the order button easy to find, and would you like a key for it?

## Known edge cases

- **Rename (R), Move to preset (M), Purge discarded now:** show "not in this build yet".
- **Alt+T, not T:** a bare letter is type-ahead in the file list. Remappable in the key bindings.
- **No settings page** for `Media` (keep folder, start offset, auto-advance); edit `settings.json`.
- **Keep to a folder on another drive** is a queued move; its undo only removes the tag, it does not move the file back.
- **Tags on FAT or SFTP** drives fail Keep (no alternate streams) until the tag index lands (F2).
- **No second pre-opened player yet,** so switching clips is not instant on slow shares. The next two files are read ahead.
- **Not verified:** real shares, very long queues, dark theme, a 100-video run.

## Result

Pass / fail / notes, and anything to tweak.

## Clean up

```powershell
Remove-Item -Recurse -Force "D:\Playbooks\data"
pwsh .\tools\New-UatFixtures.ps1   # rebuilds 10-triage to start again
```
Discarded clips are in the Recycle Bin.
