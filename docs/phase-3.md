# Phase 3 — Media and triage

Phase 3 makes Leviathan a place to go through a large pile of video: media information from ffprobe, thumbnails, contact sheets and
hover scrub, a LibVLC player, a preview and details pane, triage mode with inboxes, colour tags that travel with the file, a duplicates
finder, and the corrupt-file and benchmark suites that keep all of it from taking the window down.

## What is in it

| Area | Delivered |
| --- | --- |
| Media information (A) | ffprobe runner and parser in `Leviathan.Media.Core`; a supervised `Leviathan.Media` process (restarted after a crash, switched off after three in five minutes); SQLite cache keyed by path, size and modified time; a file that hangs or crashes ffprobe is marked unreadable until it changes; lazy probing of the rows on screen only; 11 media columns, sort by one fills the folder in the background; Tiles and Gallery layouts; a "Video and audio details" section on the Background tab with Retry per file |
| Thumbnails and scrub (B) | A frame drawn by ffmpeg when the shell has none (disk cache, least recently used trimmed, two at a time, cancelled when the row scrolls away); contact sheets 3×3 to 6×6 with the time of each frame, one or a batch; hover scrub with a 20-frame strip |
| Playback (C) | `IVideoEngine` with the spec §10.2 key map worked out in the view model; LibVLC engine (libmpv was dropped, see decisions); player window; header and index pre-read for files on a share; a spare engine kept warm |
| Preview and details (D) | Pane with collapse, splitter, S/M/L video, floating preview on Space; renderers for video and audio, images, text and code, Markdown and HTML (sandboxed WebView2), PDF, fonts, archives, folders, several items, Office through the shell preview handler hosted in ShellHost; filmstrip, waveform and cover art; details with general, media, image, audio and document properties, checksums on demand and in-place editing of title, comments and rating |
| Triage (E) | Queue from a selection or a folder, Keep (green tag, optional move) and Discard (soft delete), Undo, Skip, Previous, the full §10.2 keys, remappable; full-window player with a large file name, progress, reclaimed space, and a queue list that can be ordered by name, age or size; saved inboxes with batches of N and resume; skim playback; hand-off of kept files to the other pane or a keep folder |
| Colour tags (F) | Seven default tags (Green is Keep), rename, recolour, reorder, add; keys 1–9 and Ctrl+0; stored in the `:leviathan.tags` stream with the modified time restored, mirrored in a SQLite index by file ID for places without streams; dots in the Tags column, badges on picture layouts, optional row tint, the Tags sidebar section, names in the details pane, the `tag:` filter, a Tags settings page, a right-click Tag menu, and a "tags may be lost" warning before a copy or move to a place without streams |
| Duplicates (H4) | Find duplicates within a folder (Alt+Shift+D) and source against destination across the two panes of a split tab |
| Verification (G) | Corrupt-file suite (11 kinds of damage, 33 files through the real media process); a UAT fixture script; FlaUI triage flow test (written, not run); media benchmarks |

## Gate

| Gate | Result |
| --- | --- |
| A corrupt-file test set causes no crash | **PASS**: `CorruptMediaSuiteTests` sends 33 damaged files through probe, thumbnail, contact sheet and scrub strip in the real media process; every answer is clean, the process is the same one at the end, and intact clips still work afterwards. The suite found one bug, fixed: an empty `.flac` was reported readable |
| Triage 100 NAS videos at under 10 s each | **Not run: needs you at the machine.** Procedure below. What was measured: opening triage on a NAS folder of 1,251 videos took 1.1 s from Alt+T to the first frame (window 0.06 s, video surface 0.26 s, LibVLC 0.54 s, file playing 1.14 s); the session's own work per decision is under 1 ms |
| Preview ready < 300 ms | **PASS as defined below**: the preview is asked for 163 to 165 ms after the selection rests (budget 200 ms), the media information reads in 91 to 137 ms with a cold ffprobe (budget 300 ms), five runs each. The first picture needs LibVLC and cannot be measured headless; it was not timed |
| Thumbnail < 200 ms cached, < 1 s generated | **PASS**: 0.7 to 0.9 ms from the disk cache; 140 to 145 ms drawn by ffmpeg from a 6 s clip |
| Triage advance without a stall | **PASS**: Skip, Keep and Discard under 1 ms and a re-order of 2,000 files 6 to 7 ms (budget 50 ms), with instant fakes for the player and the file actions |
| Phase 0 and 1 budgets | Listing 10,000 items: first rows 4.2 ms p95 (budget 100 ms); through the view model 5.4 ms; from the cache 0.0 ms; new tab 4.6 ms (budget 150 ms): **PASS**. Cold start is still about 1.0 s against 600 ms (open since Phase 1) |

### Procedure for the 100-video gate (Brett, at the machine)

Use a folder on the NAS of at least 100 videos you are happy to decide on, and start the app from Visual Studio (F5) so the Output
window shows the "Triage:" trace lines.

1. Set a keep folder in Settings > Media, or open a second pane on an empty target folder (it becomes the keep folder).
2. Select the first video and press **Alt+T**. Note the time to the first frame (the Output window shows it).
3. Time yourself through 100 videos with the keys only: Enter keeps, Delete discards, Down skips. Aim to decide each in under 10 s. Note
   any file where the next video was slow to start, and which kind of file it was.
4. When the queue ends, check the summary counts, press Esc, and confirm the kept files arrived in the target folder and the discarded
   ones are in the share's `#recycle` or the Recycle Bin. Write the total time and the slowest files in this file.

## How it was verified

All on Windows 11 on 2026-11-10. `Leviathan.App` was built into a scratch folder with zero warnings and zero errors (the normal output is
locked while Leviathan and Visual Studio have it open); each test project was run with `dotnet test`. The benchmark project builds with
zero warnings.

| Suite | Passed | Skipped |
| --- | --- | --- |
| Core | 209 | 0 |
| Engine | 325 | 0 |
| Data | 45 | 0 |
| Integration | 39 | 4 (the Samba lab, when it is not running) |
| ViewModels | 1,792 | 0 |
| Diagnostics | 20 | 0 |
| Interop | 86 | 0 |
| ShellHost | 57 | 0 |
| Media | 128 | 0 |
| Playback | 31 | 0 |
| Providers.Local | 36 | 1 (100,000 real files, `LEVIATHAN_STRESS=1`) |
| Providers.Cloud | 20 | 0 |
| Chaos | 3 | 1 (the cable-pull test, when the lab is not running) |
| **Total** | **2,791** | 6 |

- `Leviathan.UI.Tests` was not run (see below). A flaky `LibVlcEngineTests.StepForward_PausesAndMovesTheFrame` failed once in a full-solution run with a changed output folder and passed on its own and in the normal run.
- Bugs found by a person running the app during the phase and fixed with a regression test where one could be written: saving properties of a previewed file (the shell's property handler opens the file exclusively, so the pane now lets go of the preview first), arrow keys not leaving the details pane, the fixture script failing in Windows PowerShell 5.1, and the files moved by triage not showing in the target pane until it was reloaded.
- User-acceptance playbooks in `docs/uat/` for A5a, A5b, A6, B1, B2, B3, C3, D1, D2, D3, E1 to E4, E5, E6, F colour tags, G1 and H4, all built on `tools/New-UatFixtures.ps1` folders. **Brett has run none of D1, D2, D3, E5, E6, H4, F and G1 yet.**

## Still unverified or open

- **The 100-video gate** above, and with it whether the NAS feel matches the numbers.
- **G2, the FlaUI triage flow** (`TriageFlowTests`) compiles but has **never been run**. It sends real keys to the desktop and an earlier UI run sent a real folder to the Recycle Bin, so it runs only on your say-so, on a quiet machine, with `LEVIATHAN_UI_TESTS=1`. It uses its own data folder, worker and folders.
- **Most of the UI added in the second half of the phase has been compiled and tested but not seen by me**: the Tags page, badges and tint, the sidebar changes, the Tag menu, the details editor, the triage name bar and Order menu.
- **File-type icons**: `.mkv` rows showed the generic page icon beside VLC's for `.mp4`. A shell answer that came back empty used to be remembered until restart; it is now asked again after 20 s. Whether that was the cause is not confirmed.
- **First picture of a preview** and the player start were not timed; triage opens the first file in about 1.1 s on the NAS (0.54 s of that is starting LibVLC).
- **UI stalls over 50 ms**: the trace of a triage run shows 300 to 540 ms freezes every few seconds while a video plays (watchdog thread); the cause was not found. Tag reads on a share throw and catch a `FileNotFoundException` per file without a tag stream, one network round trip and one exception for each row in view.
- **D3**: IPTC and XMP beyond what Windows reads, and xxHash in the checksums. **F2**: the import of the Files app's `:files` stream and the optional Windows "Tags" property.
- No keyboard shortcut for the triage Order menu.
- The E1–E4 playbook still says tags on FAT or SFTP fail "until F2", which is out of date.
- Cold start about 1.0 s against 600 ms, carried over from Phase 1.

## Deviations from the spec

See `docs/decisions.md` (entries marked Phase 3 and the 2026-11-08 to 2026-11-10 ones). The main ones: libmpv was dropped for LibVLC only (the engine cannot be hosted in WinUI 3 and is not installed); tags are not deletable (ids are never reused); the preview budget is split into "asked for after the rest" and "media information", because the pane does not wait for ffprobe; the keep folder and the other pane are the two places kept files go.
