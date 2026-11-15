# UAT: P3-A Reading media details: the probe, the cache, the quarantine and the media process

**Goal:** see video and audio facts appear as you browse without slowing the list, and see that a bad file or a crashing helper never takes the app down or retries forever.
**Built in:** Phase 3 slices A1 to A4 (`docs/phase-3.md`), spec §9.3, §3.2. The columns themselves are in `A5a-media-columns`, the status page in `A6-media-background`, damaged files in `G1-corrupt-media`.
**Time:** about 30 minutes. Needs ffmpeg/ffprobe (see Setup).

## Setup

1. ffmpeg on the PATH (or set `LEVIATHAN_FFMPEG_DIR` to the folder holding `ffmpeg.exe` and `ffprobe.exe`). Without it the fixtures skip the clips.
2. Fixtures: `.\tools\New-UatFixtures.ps1` (folders `09-media`, `10-triage\inbox`, `14-corrupt`).
3. Scratch settings and start:
   ```powershell
   $env:LEVIATHAN_DATA_DIR = "D:\Playbooks\data"
   & .\src\Leviathan.App\bin\x64\Debug\net10.0-windows10.0.26100.0\Leviathan.exe
   ```
4. Open `D:\Playbooks\uat\09-media`, right-click a column header and show **Duration**, **Resolution**, **Video codec**, **Audio codec**, **Frame rate** (View > Choose columns > Media).

## Steps

### Lazy reading

| # | Do | Expect |
|---|----|--------|
| 1 | With the media columns shown, open `09-media`. | The list appears at once with the file names and sizes. The media cells fill in a moment later for the rows **on screen**. The list never waits for them. |
| 2 | Hide all media columns, open `09-media` in a new tab, and watch the **Background** tab of the status hub. | The media process reads **nothing** when no media column is shown. |
| 3 | Show a media column again. | Reading starts for the visible rows only. |
| 4 | Sort by **Duration**. | The whole folder is read (the column header shows progress such as `Duration · 45%`), then it sorts. Cancelling by changing the sort does not hang. |
| 5 | Read the values for the generated clips: `short-3s.mp4` about 0:03, `medium-45s.mkv` 0:45, `long-2m05.avi` 2:05, `song-20s.flac` audio only (no resolution), `hdr10-5s` shows an HDR hint if the column exists. | The numbers match the generator (320x180, about 29.97 fps, h264 for the mp4). |
| 6 | Select a video and open the **details pane**. | Container, streams, chapters, tags and HDR facts. |
| 7 | Close the app and open it again on the same folder. | The columns fill **instantly** from the cache (keyed by path, size and modified time). |
| 8 | Replace a clip with another file of the same name (copy over `short-3s.mp4` with `medium-45s.mkv` renamed). | The cache entry is dropped because size and time changed; the new facts appear. |

### Quarantine

| # | Do | Expect |
|---|----|--------|
| 9 | Open `09-media\broken.mp4` (random bytes). | Its media cells say it is unreadable; nothing is shown as zero or invented. |
| 10 | Open the **Background** tab. | `broken.mp4` is listed under files the media process gave up on, with **Retry this file**. |
| 11 | Scroll away and back, and reload the folder. | The file is **not tried again** (no retry loop) until it changes. |
| 12 | Fix it: copy a good clip over `broken.mp4`. Press **Retry this file**. | It is read and the facts appear; the file leaves the list of unreadable ones. |
| 13 | Open `14-corrupt` (eleven kinds of damage). | Every damaged file shows as unreadable or partial, quickly (probing stops after about 10 seconds per file), and the app keeps working. Playbook G1 has the full list. |

### The media process

| # | Do | Expect |
|---|----|--------|
| 14 | Open the **Background** tab and find the **Media** process row. | It shows running, with restart counts. |
| 15 | Kill it: `Stop-Process -Name Leviathan.Media -Force`. Scroll the list so new rows need reading. | The row shows a restart; the process comes back by itself; the columns fill again. The window never freezes and no error dialog appears. |
| 16 | Kill it **three times in five minutes**. | After the third the process is **disabled** and the Background tab says so; media columns stay empty but everything else works. Restarting the app brings it back. |
| 17 | Open a folder of videos on the share `\\localhost\LeviathanUat\09-media` (see P2-A for the share). | Reading is limited to the file header (it does not pull whole videos across the network); it is slower than local but never blocks. |

## Look at (tests cannot judge these)

- Does the list feel as fast with media columns on as without?
- Is the **"unreadable"** wording for a bad file clear and calm?
- Is the progress in the column header while sorting noticeable but not noisy?

## Known edge cases

- A file that times out or crashes the probe is marked unreadable until its size or time changes; a file that was only temporarily locked needs **Retry this file** or a change.
- The probe runs at low priority, so on a very busy PC values arrive later.
- Dolby Vision detection was tested from a hand-written record, not a real file.

## Result

Pass / fail / notes, and anything to tweak.

## Clean up

```powershell
Remove-Item -Recurse -Force "D:\Playbooks\data"
```
