# UAT: A6 Media process on the Background tab

**Goal:** see what the media process is doing, which files it gave up on, and ask it to try one again.
**Built in:** commit `c403a47`, spec §9.3, §16.1.
**Time:** about 5 minutes.

## Setup

1. Build the fixtures (needs ffmpeg; use `pwsh`):
   ```powershell
   pwsh .\tools\New-UatFixtures.ps1
   ```
   Folder: `D:\Playbooks\uat\09-media` (`broken.mp4` is random bytes).
2. Start the app with scratch settings:
   ```powershell
   $env:LEVIATHAN_DATA_DIR = "D:\Playbooks\data"
   & .\src\Leviathan.App\bin\x64\Debug\net10.0-windows10.0.26100.0\Leviathan.exe
   ```

## Steps

| # | Do | Expect |
|---|----|--------|
| 1 | Before opening any media folder, open the status hub (**Ctrl+Shift+O**) and go to the **Background** tab. | A "Video and audio details" section says "Not running · starts when a folder shows video or audio details". Under "Programs this app runs", Media process is "Not started". |
| 2 | Close the hub. Open `09-media`, switch to **Tiles** (Ctrl+Shift+4), wait a few seconds. | Facts appear for the clips; `broken.mp4` shows "—". |
| 3 | Open the hub's Background tab again. | The section reads like "Idle · 6 files read since it started · 1 could not be read". Media process is "Running · process N". |
| 4 | Look at the list under that line. | `broken.mp4` with ffprobe's reason (for example "moov atom not found") and a **Retry this file** button. Hovering shows its full path. |
| 5 | Replace `broken.mp4` with a real clip of the same name (copy `short-3s.mp4` over it), then press **Retry this file**. | The button works without an error. The file leaves the list within a couple of seconds. |
| 6 | Press F5 in the folder. | `broken.mp4` now shows real facts (0:03 · 320×180 …). |
| 7 | Leave the app alone for about 3 minutes with the hub open on Background. | The media process exits by itself when idle; the section goes back to "Not running" and the list empties; the process row says it is not running. Nothing restarts it. |

## Look at (tests cannot judge these)

- Is the wording clear ("read", "could not be read")? Is "Retry this file" obvious?
- Does the section fit between Thumbnails and Network folders without crowding the page?

## Known edge cases

- The list of unreadable files lives in the database but is only shown while the media process is running.
- A tab that already shows "—" for a retried file keeps it until the folder is reloaded (step 6).
- Pause and priority for metadata probing are not here: the queue is short and inside the process. Thumbnail and contact-sheet queues with pause and priority come with B2.
- The counts are since the process started, so a folder read from the cache shows "0 files read".
- Step 7 depends on the 2-minute idle exit; if it does not happen, say so.

## Result

Pass / fail / notes, and anything to tweak.

## Clean up

```powershell
Remove-Item -Recurse -Force "D:\Playbooks\data"
```
