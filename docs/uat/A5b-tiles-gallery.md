# UAT: A5b Tiles and Gallery layouts

**Goal:** see a folder of videos as Tiles (thumbnail, name, duration and resolution) and as Gallery (large thumbnails).
**Built in:** commit `99273e8`, spec §8.1.
**Time:** about 5 minutes.

## Setup

1. Build the fixtures (needs ffmpeg for the clips; use `pwsh`, not Windows PowerShell 5.1):
   ```powershell
   pwsh .\tools\New-UatFixtures.ps1
   ```
   Folder: `D:\Playbooks\uat\09-media` (3 clips, an HDR10 clip, a FLAC song, `broken.mp4`, `notes.txt`).
2. Start the app with scratch settings:
   ```powershell
   $env:LEVIATHAN_DATA_DIR = "D:\Playbooks\data"
   & .\src\Leviathan.App\bin\x64\Debug\net10.0-windows10.0.26100.0\Leviathan.exe
   ```

## Steps

| # | Do | Expect |
|---|----|--------|
| 1 | Open `09-media`. Open **View ▾**. | The list shows Details, List, **Tiles**, Thumbnails, **Gallery**. Tiles and Gallery are enabled (no "Phase 3" text). |
| 2 | Choose **Tiles** (or press Ctrl+Shift+4). | Cells in a wrapping grid: small thumbnail on the left; name, a facts line and a size · date line on the right. |
| 3 | Read the facts line of each file. | `short-3s.mp4`: `0:03 · 320×180 · 29.97 fps`. `medium-45s.mkv`: `0:45 · …`. `long-2m05.avi`: `2:05 · …`. `hdr10-5s.mkv`: `0:05 · 320×180 · …`. `song-20s.flac`: `0:20 · FLAC · <bit rate>`. A "…" may show for a moment first. |
| 4 | Look at `broken.mp4` and `notes.txt`. | `broken.mp4`: "—" on the facts line, then size · date. `notes.txt`: only size · date. |
| 5 | Choose **Gallery** (Ctrl+Shift+1). | Large thumbnails (about 288 px) with the name underneath, wrapping to the window width. |
| 6 | Click, Ctrl+click, arrow keys, Enter, F2 rename, drag a rubber band in both layouts. | Same behaviour as in Thumbnails: selection, focus outline, rename box in place, rubber band selects. |
| 7 | Scroll a big folder (`06-ten-thousand`) in Tiles. | Scrolling stays smooth. No facts are asked for `.txt` files. |
| 8 | Switch back to Details, then close and reopen the app. | The layout you left each tab in is restored. |

## Look at (tests cannot judge these)

- Is the Tiles cell the right size: is the name too close to the facts, is 320 px wide enough for long names?
- Are the facts and size lines readable without being louder than the name?
- Does Gallery look like what you expect from "large thumbnails"? It is a plain grid, not justified rows (see below).

## Known edge cases

- Gallery is a **uniform grid**, not the spec's justified rows. Justified rows need picture proportions, which come with the thumbnail work (B1 onwards); noted in `docs/decisions.md`.
- The size slider and Ctrl+wheel resize only apply to Thumbnails.
- The three small layout buttons in the status bar still show only Details, List and Thumbnails.
- Hover scrub (B3) and badges for tags (F3) are not here yet.
- Tiles ask the Media process for the files on screen, so the first Tiles view of a folder starts that process.

## Result

Pass / fail / notes, and anything to tweak.

## Clean up

```powershell
Remove-Item -Recurse -Force "D:\Playbooks\data"
```
