# UAT: B2 Contact sheets

**Goal:** see a video as a grid of frames taken evenly across it, save that picture, and make one for every video in a selection without the window freezing.
**Built in:** commits `99a2e7e` (engine) and `35912de` (window, export, folder batch), spec §10.4.
**Time:** about 10 minutes.

## Setup

1. Build the fixtures (needs ffmpeg; use `pwsh`):
   ```powershell
   pwsh .\tools\New-UatFixtures.ps1
   ```
   Folder: `D:\Playbooks\uat\09-media`. Also make an empty scratch folder for the exports, for example `D:\Playbooks\sheets-out`.
2. Start the app with scratch settings:
   ```powershell
   $env:LEVIATHAN_DATA_DIR = "D:\Playbooks\data"
   & .\src\Leviathan.App\bin\x64\Debug\net10.0-windows10.0.26100.0\Leviathan.exe
   ```

## Steps

| # | Do | Expect |
|---|----|--------|
| 1 | Open `09-media`. Right-click `medium-45s.mkv` and choose **Contact sheet**. | A window titled "Contact sheet · medium-45s.mkv" opens at once, then the picture fills in within a few seconds. The main window stays responsive meanwhile. |
| 2 | Look at the grid. | 4 × 4 frames of the test pattern. The running clock in each frame rises from left to right and top to bottom, from near 0 s to near the end. |
| 3 | Move the mouse over several frames. | The text at the bottom says where in the video that frame is from (a time). |
| 4 | Change the grid to **3 × 3**, then **6 × 6**. | The picture is made again with that many frames. Switching back to a size you already saw is instant (cached). |
| 5 | Click **Save as…** and save into the scratch export folder. | A `.jpg` is written, and it opens in an image viewer showing the same grid. Saving over an existing name asks you in the system dialog. |
| 6 | Close the window. Right-click `long-2m05.avi`, **Contact sheet**. | Same behaviour for an AVI. |
| 7 | Right-click `hdr10-5s.mkv`, **Contact sheet**. | A grid appears. Colours may look a little flat (HDR is tone-mapped to ordinary video); that is accepted for now. |
| 8 | Right-click `short-3s.mp4`, **Contact sheet**. | A grid appears; the frames are close together in time. |
| 9 | Right-click `broken.mp4`, **Contact sheet**. | The window says no contact sheet could be made, with a short reason. No crash, no hang. |
| 10 | Right-click `notes.txt` or `song-20s.flac`. | No **Contact sheet** entry (only videos). |
| 11 | Select all five videos (`short-3s.mp4`, `medium-45s.mkv`, `long-2m05.avi`, `hdr10-5s.mkv`, `broken.mp4`) and choose **Contact sheets of the videos…** from the context menu. Choose the scratch export folder. | A message says it is making 5 contact sheets into that folder and that progress is on the Background tab. |
| 12 | Open the status hub's **Background** tab. | A Contact sheets line shows progress, with a Cancel button. When done it reports that four were made and one failed (the broken file). |
| 13 | Look in the export folder. | Files named `<name> - contact sheet.jpg`, four of them. |
| 14 | Run step 11 again into the same folder. | New files get ` (2)` in the name. Nothing is overwritten. |
| 15 | Run step 11 again and, while it works, press Cancel on the Background tab. | It stops after the sheet in progress; the sheets already made stay. |
| 16 | Start step 11 and, while it runs, try it a second time. | A message says contact sheets are already being made and to cancel them on the Background tab first. |

## Look at (tests cannot judge these)

- Are the frames well spread (first at about 5%, last just before the end)? Try a real film of your own, copied to a scratch folder.
- Is the window quick enough to feel instant on a short clip, and tolerable on a long one?
- Wording of the failure message in step 9 and the message in step 11.
- Image quality of the saved JPEG.

## Known edge cases

- Contact sheets of files on a network drive have not been tried by hand; use `-Share` or a `[lab]` share if you want to.
- Only one sheet is made at a time, so a second window waits for the first.
- The HDR clip is not colour-managed beyond ffmpeg's default conversion.
- Seen on screen only for the single-video window; **Background tab, Save as and the folder batch have not been seen by hand yet**. Steps 5 and 11 to 16 are the first time.
- The cache is capped at 256 MB and trimmed in the background.

## Result

Pass / fail / notes, and anything to tweak.

## Clean up

```powershell
Remove-Item -Recurse -Force "D:\Playbooks\data", "D:\Playbooks\sheets-out"
```
