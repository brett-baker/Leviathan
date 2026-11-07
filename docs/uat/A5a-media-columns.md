# UAT: A5a Media columns

**Goal:** show Duration, Resolution, Frame rate, codecs and the other media facts as columns in the Details view, and sort by them.
**Built in:** commit `21f6cc5`, spec §8.2, §9.3.
**Time:** about 10 minutes.

## Setup

1. Build the fixtures (never test with real data). Use `pwsh`, not Windows PowerShell 5.1. Folder `09-media` needs ffmpeg on the PATH or in `LEVIATHAN_FFMPEG_DIR`:
   ```powershell
   pwsh .\tools\New-UatFixtures.ps1
   ```
   Fixture folder: `D:\Playbooks\uat\09-media`. It holds `short-3s.mp4`, `medium-45s.mkv`, `long-2m05.avi`, `hdr10-5s.mkv`, `song-20s.flac`, `broken.mp4` (random bytes) and `notes.txt`.
2. Start the app with scratch settings, so your real settings and favourites are untouched:
   ```powershell
   $env:LEVIATHAN_DATA_DIR = "D:\Playbooks\data"
   & .\src\Leviathan.App\bin\x64\Debug\net10.0-windows10.0.26100.0\Leviathan.exe
   ```

## Steps

| # | Do | Expect |
|---|----|--------|
| 1 | Open `09-media` in Details view. | Plain columns only (Name, Size, Modified…). No Duration column. Nothing is read yet. |
| 2 | Right-click a column header and open the column chooser. | The list includes Duration, Resolution, Frame rate, Video codec, Audio codec, Bitrate, Container, HDR, Audio channels, Audio languages and Subtitles. |
| 3 | Tick Duration, Resolution, Frame rate, Video codec, Audio codec, HDR. | The columns appear. Cells show a dim "…" for a moment, then fill in. |
| 4 | Check `short-3s.mp4`. | Duration 0:03, Resolution 320×180, Frame rate 29.97 fps, video H.264, audio AAC, HDR "SDR". |
| 5 | Check `hdr10-5s.mkv`. | Duration 0:05, video HEVC, HDR "HDR10". No audio codec (blank). |
| 6 | Check `long-2m05.avi` and `medium-45s.mkv`. | 2:05 and 0:45. |
| 7 | Check `song-20s.flac`. | Duration 0:20, audio FLAC. Resolution, Frame rate, Video codec and HDR are blank, not "—". |
| 8 | Check `broken.mp4`. Hover the "—". | Shows "—" and a tooltip "Could not be read: …" with a reason. The app stays responsive. |
| 9 | Check `notes.txt`. | All media cells blank. It is not a media file. |
| 10 | Click the Duration header. | Rows sort by number: 0:03, 0:05, 0:20, 0:45, 2:05 (not as text). Click again for descending. Unreadable and non-media files sit together at one end. |
| 11 | Click the Resolution or Frame rate header. | Sorts by number too. |
| 12 | Right-click the header, untick all media columns, then reopen the folder. | No reading happens (no "…" appears). |
| 13 | Close and restart the app, open `09-media`. | The media columns you chose are still shown; values appear again, fast (they are cached). |

## Look at (tests cannot judge these)

- Is "…" dim enough to read as "waiting" and not as data?
- Are number columns right-aligned and wide enough (Audio languages and Subtitles were cut off once; widths were widened)?
- Does the Duration header show progress ("Duration · 45%") when sorting a large folder? Fixtures are too small to see it; try a folder with several hundred clips of your own copies if you want to see it. Not in the fixtures.
- Does scrolling stay smooth while values arrive?
- Wording of the tooltip for an unreadable file.

## Known edge cases

- Tiles and Gallery layouts (A5b) do not exist yet. Only Details has media columns.
- Thumbnails, preview details and the Background tab rows for probing are later slices (A6, B, D).
- ffprobe must be found (setting, `LEVIATHAN_FFMPEG_DIR`, an `ffmpeg` folder next to the app, or PATH). If it is missing, the media cells stay blank and nothing is retried in a loop.
- A network folder is read with limits (4 MB probe, 3 s analysis), so a few rare files may show less detail there. Not tested here: use a `[lab]` share if you want to try.
- The column chooser was not clicked through by me; I enabled columns by a saved view rule. Step 2 and 3 are the first real check.
- The header progress while a big sort runs has not been seen on screen.

## Result

Pass / fail / notes, and anything to tweak.

## Clean up

```powershell
Remove-Item -Recurse -Force "D:\Playbooks\data"
```
