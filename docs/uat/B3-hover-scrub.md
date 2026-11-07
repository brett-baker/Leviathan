# UAT: B3 Hover scrub

**Goal:** hold the mouse over a video's picture and move it left and right to skim through the video, without opening anything.
**Built in:** commit `2105d86`, spec §10.4.
**Time:** about 5 minutes.

## Setup

1. Build the fixtures (needs ffmpeg; use `pwsh`):
   ```powershell
   pwsh .\tools\New-UatFixtures.ps1
   ```
2. Start the app with scratch settings, forcing video frames so the pictures come from ffmpeg:
   ```powershell
   $env:LEVIATHAN_DATA_DIR = "D:\Playbooks\data"
   $env:LEVIATHAN_VIDEO_FRAMES = "1"
   & .\src\Leviathan.App\bin\x64\Debug\net10.0-windows10.0.26100.0\Leviathan.exe
   ```
3. Open `09-media` and switch to **Thumbnails** (Ctrl+Shift+2) or **Tiles**. Wait for the pictures.

## Steps

| # | Do | Expect |
|---|----|--------|
| 1 | Rest the mouse over the picture of `medium-45s.mkv` for about a second. | After a short pause (about a quarter second to start, then a moment to draw) the picture changes to a frame, with a thin line and a time badge. |
| 2 | Move the mouse slowly left to right across the picture. | The frame changes with the mouse; the clock in the test pattern rises. The line follows the pointer and the badge shows the time. |
| 3 | Move off the picture. | It returns to the normal picture at once. |
| 4 | Do the same on `long-2m05.avi` and `hdr10-5s.mkv`. | Works the same; the frame matches the position (left edge near the start, right edge near the end). |
| 5 | Hover `short-3s.mp4`. | Works; the frames are close together. |
| 6 | Hover `broken.mp4`, `notes.txt` and `song-20s.flac`. | Nothing happens. No errors. |
| 7 | Sweep the mouse quickly down the whole list over several videos. | No scrub starts for pictures you only passed over. Scrolling stays smooth. |
| 8 | Hover the same video again after leaving it. | The strip appears at once (cached). |
| 9 | Open **Settings**, turn off hover scrub (Performance), then hover a video. | Nothing happens. Turn it back on and it works without a restart. |
| 10 | Open the Task Manager while hovering many videos in a row. | At most two `ffmpeg.exe` at a time, low priority. |

## Look at (tests cannot judge these)

- The delay before it starts, and whether it feels natural or jumpy.
- Whether the line and the time badge are readable on both light and dark pictures.
- Is 20 frames smooth enough? Videos with a portrait or very wide shape should be letterboxed, not stretched.
- Does it get in the way when you only wanted to click or drag?

## Known edge cases

- The strip is drawn the first time you hover a file, so the first hover waits for ffmpeg (a second or two); later hovers are instant.
- Only the pictures in the cell layouts (Thumbnails, Tiles) scrub; the Details list does not. Whether Gallery scrubs was not checked.
- A file on a network drive is only scrubbed if network thumbnails are on; not tried by hand here.
- It shows still frames drawn ahead of time, not live playback.

## Result

Pass / fail / notes, and anything to tweak.

## Clean up

```powershell
Remove-Item Env:LEVIATHAN_VIDEO_FRAMES -ErrorAction SilentlyContinue
Remove-Item -Recurse -Force "D:\Playbooks\data"
```
