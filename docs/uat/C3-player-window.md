# UAT: C3 and C4 Player window and read-ahead

**Goal:** open a video from its contact sheet, play it in a window with the spec's keys, and know that files on a share are warmed up before they play.
**Built in:** commits `00657fc` (LibVLC engine, player window) and `0427988` (read-ahead, spare engine), spec §10.1, §10.2.
**Time:** about 15 minutes.

## Setup

1. Build the fixtures (needs ffmpeg; use `pwsh`):
   ```powershell
   pwsh .\tools\New-UatFixtures.ps1
   ```
   Folder: `D:\Playbooks\uat\09-media`.
2. Start the app with scratch settings:
   ```powershell
   $env:LEVIATHAN_DATA_DIR = "D:\Playbooks\data"
   & .\src\Leviathan.App\bin\x64\Debug\net10.0-windows10.0.26100.0\Leviathan.exe
   ```
3. Open `09-media`. The only way in for now is the contact sheet: right-click `medium-45s.mkv`, **Contact sheet**, then click a frame. (The preview pane and triage mode will open the player directly later.)

## Steps

| # | Do | Expect |
|---|----|--------|
| 1 | In the contact sheet of `medium-45s.mkv`, click a frame from the middle. | A "Play · medium-45s.mkv" window opens and plays from about that point, with sound (a steady tone). |
| 2 | Look at the controls. | Play/Pause button (it says Pause while playing), a seek bar, the position and length, and the speed. |
| 3 | Press **Space** twice. | Pauses, then plays again. |
| 4 | Press **Right**, then **Left**. | Jumps 30 s forward, then 30 s back (clamped to the file, which is only 45 s long). |
| 5 | Press **Shift+Right**, **Ctrl+Right**, **Alt+Right**. | 1 minute, 5 minutes and 5 seconds. On the 45 s clip the big ones go to the end (it stops). |
| 6 | Press **Home**, then **End**. | Start of the video; then the last 10 seconds. |
| 7 | Press **Ctrl+3**, **Ctrl+5**. | 30% and 50% of the way through. |
| 8 | Press **.** (period) then **,** (comma) a few times while paused. | One frame forward each time; comma goes back by a small step. A playing video pauses when you step. |
| 9 | Press **]** a few times, then **[**. | Speed goes up a rung (1.25×, 1.5×, 2×, 3×, 4×) and down again; the speed text updates. |
| 10 | Press **L** three times, then **K**. | Plays at 1×, 2×, 4×; K pauses. |
| 11 | Press **J** a few times. | Pauses and goes back a stride each press (5 s, then more). |
| 12 | Drag the seek bar and click on it. | The video moves there; the picture updates. |
| 13 | Let `short-3s.mp4` play to the end, then press Space. | It stops at the end; Space restarts it from the beginning. |
| 14 | Open `long-2m05.avi` and `hdr10-5s.mkv` from their sheets. | Both play. HDR colours may look flat. |
| 15 | Open `broken.mp4`'s sheet. | No sheet, so no player. Nothing to play. |
| 16 | Close the window with **Esc**. | The window closes and the sound stops. The main window is still responsive. |
| 17 | Open and close the player ten times quickly. | No leftover sound, no hang, no growing memory in Task Manager. |
| 18 | `[lab]` Copy `medium-45s.mkv` to a test share you own, open its contact sheet from there, and click a frame. | It plays; the start-up wait is short because the head of the file was read ahead. Never use Y:, Z: or `\\192.168.1.10`. |

## Look at (tests cannot judge these)

- How quickly the picture and sound appear after the click.
- Do the keys feel right, and is the seek bar easy to use?
- Any flicker or black frame when the window opens, resizes or is dragged between monitors.
- Is the speed text and the position readable?
- Sync of sound and picture, and any stutter.

## Known edge cases

- Frame-step **back** is a short seek, not an exact frame (LibVLC 3 steps forward only).
- Volume, mute, full screen, audio and subtitle tracks have no controls yet; they arrive with the preview pane.
- **J** does not play in reverse; it jumps back.
- libmpv is not used: Brett chose LibVLC only (decision in `docs/decisions.md`).
- LibVLC runs inside the app, so a crash in a codec would take the app down; moving it out of process is later work.
- The spare engine and the hidden "next file" preload (C4) are built and tested but **not used by any window yet**, so there is nothing to see for them. Step 18 only shows the read-ahead for files on a share, and that has not been tried by hand.
- Untested by hand: very long files (an hour or more), 4K, files with several audio tracks.

## Result

Pass / fail / notes, and anything to tweak.

## Clean up

```powershell
Remove-Item -Recurse -Force "D:\Playbooks\data"
```
