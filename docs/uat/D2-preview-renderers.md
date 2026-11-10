# UAT: D2a to D2f Preview renderers

**Goal:** select almost any file and see a sensible preview in the pane without the app slowing down or running anything it should not.
**Built in:** commits `5209e04` (D2a video/audio), `a9c47c1` (D2b images), `e077ca4` (D2c text/code/Markdown/HTML), `938ad8e` (D2d PDF/fonts/archives/folders/several items), `a86806c` (D2e Office), `d6ebf6b` (D2f video extras), spec §9.1.
**Time:** about 30 minutes.

## Setup

1. Build the fixtures (needs ffmpeg on the PATH for the clips and the GIF; use `pwsh`):
   ```powershell
   pwsh .\tools\New-UatFixtures.ps1
   ```
   This playbook uses `D:\Playbooks\uat\11-preview` (picture.png, photo.jpg, animated.gif, notes.txt, utf16.txt, sample.cs, readme.md, page.html, big.log, sample.pdf, sample.zip, arial-copy.ttf, letter.docx, a-folder) and `09-media`.
2. Start the app with scratch settings:
   ```powershell
   $env:LEVIATHAN_DATA_DIR = "D:\Playbooks\data"
   & .\src\Leviathan.App\bin\x64\Debug\net10.0-windows10.0.26100.0\Leviathan.exe
   ```
3. Open `11-preview` and show the pane with **Alt+P**.

## Steps

Each preview starts about 150 ms after the selection stops moving; arrowing quickly through the list must not start one per file.

| # | Do | Expect |
|---|----|--------|
| 1 | In `09-media`, click `medium-45s.mkv`. | The video is loaded paused on its first frame, with play/pause, a seek bar and a time readout. No sound until you press play. |
| 2 | Press the play button, then drag the seek bar. | It plays with sound; dragging jumps. Space in the pane's player pauses; the keys of the player window (arrows, `[` `]`) also work. |
| 3 | Click `song-20s.flac`. | An audio view with controls, a waveform, and cover art if the file has any (the fixture has none: a placeholder or none, not an error). |
| 4 | Click `long-2m05.avi`. Hover the mouse along the seek bar. | A **filmstrip** of frames appears; hovering shows a small frame preview above the pointer for that moment. |
| 5 | Click `broken.mp4`. | A clear message in the pane ("could not be played" or similar); the rest of the window is unaffected. |
| 6 | Back in `11-preview`, click `picture.png`. | The picture fits the pane. Mouse wheel zooms, drag pans, double-click resets (or the visible reset control). |
| 7 | Click `photo.jpg`, then `animated.gif`. | The JPEG shows; the GIF animates. |
| 8 | Click `notes.txt`, `utf16.txt`. | Text shows; the UTF-16 file shows `café` and the two Chinese characters correctly (encoding detected). |
| 9 | Click `sample.cs`. | Syntax colours (keywords, strings) in the text. |
| 10 | Click `big.log`. | Shows the start of the file only, with a note that it was cut at 2 MB; opening it is quick. |
| 11 | Click `readme.md`. | Rendered Markdown: a heading, bold text, a link, a bulleted list. |
| 12 | Click `page.html`. | Rendered HTML with the heading **but no "SCRIPT RAN" line**: scripts are off. No web request is made. |
| 13 | Click `sample.pdf`. | The page renders ("Leviathan PDF"). The pane shows a page count/pager if the PDF has several pages. |
| 14 | Click `arial-copy.ttf`. | A font sample (alphabet, digits, a sentence) in that font. |
| 15 | Click `sample.zip`. | A listing of the archive: `one.txt` and a folder `inner` with `two.txt`; sizes shown. Nothing is extracted. |
| 16 | Click the folder `a-folder`. | A summary: item count, total size, a mini space map or type breakdown. |
| 17 | Select three files with Ctrl+click. | A summary of the selection: count, total size, types, and a strip of thumbnails. |
| 18 | Click `letter.docx`. | The Word document is shown through its registered preview handler (it needs Word or another handler installed; without one, a "no preview available" message and nothing else breaks). |
| 19 | Hold **Down** and arrow quickly through the whole `11-preview` folder. | The list stays responsive. The pane lags behind and settles on the file you stopped on; it does not flash every file. |
| 20 | Select a video, press play, then select a picture. | The video stops (no sound continues behind the picture). |
| 21 | In Task Manager, look for `Leviathan.ShellHost` while the .docx previews, then end that process. | The pane shows a message or goes blank for that file; the app does not crash and other previews still work (ShellHost is restarted). |

## Look at (tests cannot judge these)

- Is the 150 ms wait right? Does arrowing feel smooth, or do you want it longer or shorter?
- Colours and fonts of the code view, the Markdown and the font sample in both light and dark theme.
- Zoom and pan feel on the picture; whether the filmstrip is useful on a 2-minute clip.
- Whether the "cut at 2 MB" note is noticeable.

## Known edge cases

- HEIC, AVIF and RAW pictures only show where Windows has the codec; none of those are in the fixtures.
- Pictures with an EXIF rotation are handled in code and unit tests but the fixtures have none; try a phone photo copy of your own only if you do not mind it being read.
- Office previews depend on installed handlers; encrypted PDFs and password-protected archives are not covered.
- Archive listing is a flat read; very large archives (tens of thousands of entries) are not in the fixtures.

## Result

Pass / fail / notes, and anything to tweak.

## Clean up

```powershell
Remove-Item -Recurse -Force "D:\Playbooks\data"
```
