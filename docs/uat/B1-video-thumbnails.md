# UAT: B1 Video thumbnails from ffmpeg

**Goal:** a video the shell has no thumbnail for still gets a picture, drawn by ffmpeg, cached on disk and never freezing the window.
**Built in:** commit `61f1fe0`, spec §10.4, §3.1.
**Time:** about 10 minutes.

## Setup

1. Build the fixtures (needs ffmpeg; use `pwsh`):
   ```powershell
   pwsh .\tools\New-UatFixtures.ps1
   ```
   Folder: `D:\Playbooks\uat\09-media`.
2. Windows may already have thumbnails for your video types (a codec pack or VLC does). To see the ffmpeg path regardless, start the app with video frames forced on, and with scratch settings:
   ```powershell
   $env:LEVIATHAN_DATA_DIR = "D:\Playbooks\data"
   $env:LEVIATHAN_VIDEO_FRAMES = "1"
   & .\src\Leviathan.App\bin\x64\Debug\net10.0-windows10.0.26100.0\Leviathan.exe
   ```
   Without `LEVIATHAN_VIDEO_FRAMES` the app uses the shell's thumbnail whenever Windows has one (that is the normal case, and step 7 checks it).

## Steps

| # | Do | Expect |
|---|----|--------|
| 1 | Open `09-media` and switch to **Thumbnails** (Ctrl+Shift+2). | Icons first, then pictures replace them within about a second: a test pattern for each clip and the HDR clip. No window freeze. |
| 2 | Look at the pictures. | Each shows a frame from partway in, not the very first frame (the test pattern has a running clock). |
| 3 | Look at `broken.mp4`, `song-20s.flac` and `notes.txt`. | They keep their icons. No error dialogs. |
| 4 | Open `D:\Playbooks\data\thumbs`. | Sub-folders of small `.jpg` files, one per drawn clip (4), none for the broken file. No `.part.jpg` files left. |
| 5 | Close the app, start it again the same way, open `09-media` in Thumbnails. | Pictures appear at once, from the disk cache. |
| 6 | Switch to **Gallery** (Ctrl+Shift+1). | Larger pictures; they take a moment the first time (a bigger size is a new thumbnail), then stay. |
| 7 | Close the app, start it **without** `LEVIATHAN_VIDEO_FRAMES` (`Remove-Item Env:LEVIATHAN_VIDEO_FRAMES`), open the folder. | If Windows has thumbnails for these types you see Windows' own (possibly a different frame). If not, you see the ffmpeg frames as before. Either is correct. |
| 8 | Copy the five clips in `09-media` into a scratch folder about 100 times (any names) and scroll quickly up and down in Thumbnails. | Scrolling stays smooth. Pictures for rows you scrolled past quickly may never appear until you stop there. |
| 9 | Open Task Manager during step 8. | At most two `ffmpeg.exe` at a time, low priority, gone after a moment. |
| 10 | Open the status hub's Background tab. | Thumbnails line shows the pictures held and any "files could not be drawn". |

## Look at (tests cannot judge these)

- Is the chosen frame a good one for real videos (a tenth in)? Try a movie of your own copied into a scratch folder, not a share.
- Is the delay before a picture appears acceptable, and does it feel quicker the second time?
- Do pictures look sharp enough in Gallery (they are scaled to fit 288 px)?

## Known edge cases

- Thumbnails of files on network drives only appear if "Make thumbnails of files" is on in Quick settings (existing setting). Not tested against a real share here; use `-Share` or a `[lab]` share if you want to.
- The frame time on network files is a fixed 10 s guess (the file is not read twice).
- Not yet timed against the plan's gate (under 1 s generated, under 200 ms cached); the benchmark slice does that.
- The preview pane does not use these pictures yet.
- The thumbnail cache is capped at 512 MB; trimming happens after every 50 new thumbnails and is not visible.

## Result

Pass / fail / notes, and anything to tweak.

## Clean up

```powershell
Remove-Item Env:LEVIATHAN_VIDEO_FRAMES -ErrorAction SilentlyContinue
Remove-Item -Recurse -Force "D:\Playbooks\data"
```
