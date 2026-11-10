# UAT: G1 Corrupt media

**Goal:** open a folder full of damaged videos and songs and see that nothing crashes, nothing hangs, and every bad file says plainly that it is unreadable.
**Built in:** the commit that adds `tests/Leviathan.Testing/CorruptMedia.cs` and the `14-corrupt` fixtures, spec §9.3, §19 (the Phase 3 gate: a corrupt-file test set causes no crash).
**Time:** about 10 minutes.

## Setup

1. Build the fixtures (needs ffmpeg; Windows PowerShell or `pwsh`):
   ```powershell
   .\tools\New-UatFixtures.ps1
   ```
   `D:\Playbooks\uat\14-corrupt` holds 34 files: for each of `video` (.mkv), `clip` (.mp4) and `song` (.flac) eleven damaged copies named `<name>.<damage><ext>` (Empty, OneByte, TruncatedTenth, TruncatedHalf, TruncatedTail, HeaderZeroed, MiddleFlips, RandomGarbage, ZeroFilled, TextInDisguise, HugeFirstSize), and `intact-control.mp4`.
2. Start the app with scratch settings:
   ```powershell
   $env:LEVIATHAN_DATA_DIR = "D:\Playbooks\data"
   & .\src\Leviathan.App\bin\x64\Debug\net10.0-windows10.0.26100.0\Leviathan.exe
   ```
3. Open `D:\Playbooks\uat\14-corrupt`. Show the Duration, Resolution and Codec columns (right-click a column header) and the preview pane (**Alt+P**).

## Steps

| # | Do | Expect |
|---|----|--------|
| 1 | Wait a few seconds with the folder open. | The window stays responsive. Duration cells fill in or show a dash; none shows a spinner for ever. |
| 2 | Look at `intact-control.mp4`. | Duration, resolution (320×180) and codec (h264) are shown. |
| 3 | Look at the `Empty`, `OneByte`, `ZeroFilled`, `RandomGarbage` and `TextInDisguise` files. | Dashes in the media columns. Hovering a dash gives a reason (for `Empty`: "The file is empty."). |
| 4 | Look at `*.TruncatedHalf.*`, `*.MiddleFlips.*`, `*.HeaderZeroed.*`. | Either a dash with a reason, or real values if enough of the file is left to read. Either is fine; a crash is not. |
| 5 | Switch to **Thumbnails**. | The damaged files show a plain icon, not a broken picture; scrolling stays smooth. |
| 6 | Click each damaged video in turn with the pane open. | The pane says it cannot play the file, or shows its first frame for the lightly damaged ones. The app never freezes; clicking the next file works at once. |
| 7 | Click `video.TruncatedHalf.mkv`, press the pane's play button. | It plays as far as the file goes, then stops or shows an error; the window is still responsive. |
| 8 | Select `clip.TextInDisguise.mp4` and press **Alt+T** (triage). | The triage window opens and shows an error line for that file; Skip and Keep/Discard keys still work. Close it with **Esc**. |
| 9 | Select a damaged video and press the contact-sheet command (right-click → **Contact sheet**). | A message that the sheet could not be made (note the wording); no window stuck open. |
| 10 | Open the status hub (the Background tab) and look at "Video and audio details". | A list of the files it gave up on, each with a reason and **Retry this file**. The intact control is not on it. |
| 11 | Press **Retry this file** on `clip.Empty.mp4`. | It is read again and goes straight back to "The file is empty."; nothing else changes. |
| 12 | In Task Manager look for `Leviathan.Media`. Note its process id, work through steps 1 to 10 again, and look again. | The same process id: the media process never restarted. |
| 13 | End the `Leviathan.Media` process in Task Manager. | The app keeps running. A moment later the columns work again (the process is started again) and the intact control still shows its values. |

## Look at (tests cannot judge these)

- Are the reasons in the tooltips understandable to a person ("The file is empty", "No playable audio or video was found in the file")?
- Does a folder of 34 bad files feel as fast as a folder of good ones?
- Is a dash the right way to show an unreadable file, or would you like an icon?

## Known edge cases

- A damaged file can still be partly readable (a middle bit flip often is); the playbook accepts either answer for those. The automated suite makes the same allowance but requires a clean answer.
- The five kinds with no usable content (Empty, OneByte, ZeroFilled, RandomGarbage, TextInDisguise) must always be unreadable.
- This found one real bug while it was written: an empty `.flac` was reported as readable (ffprobe guessed the format from the name). Empty files and files with only a zero-length stream are now unreadable.
- The suite does not damage the file system itself (permission errors, vanishing files); the chaos tests cover some of that.

## Result

Pass / fail / notes, and anything to tweak.

## Clean up

```powershell
Remove-Item -Recurse -Force "D:\Playbooks\data"
```
