# Phase 3 plan — Media and triage

Scope, spec sections and gate are in `docs/build-plan.md` (Phase 3). Working checklist; tick a slice when it is committed and pushed.
Every slice follows `CLAUDE.md`.

> Written before Phases 1–2 finished. Re-read against `docs/phase-1.md` and `docs/phase-2.md` on 2026-11-06 (Phase 3 start).
> What already exists and is reused, not rebuilt:
> - Status hub with the Background tab (Phase 1 I7/Phase 2 C5b): A6 adds rows to it.
> - The preview pane shell with its Move/Copy buttons and "follows the focused pane" (Phase 2 F3): D1 adds collapse, sizes and renderers to it.
> - Soft delete with restore and undo (Phase 2 D): E3 Discard uses it.
> - The column chooser (Phase 1 I3): A5 adds columns to it.
> - `ProcessSupervisor` (Engine): supervises `Leviathan.Media` like the worker.
> - Tools on this machine: ffprobe/ffmpeg (jellyfin GPL build) in `C:\Tools\ffmpeg-jellyfin`; the runner finds it by setting,
>   `LEVIATHAN_FFMPEG_DIR`, a `ffmpeg` folder next to the app, then `PATH`.
> - The UI tests are on hold (see `docs/backlog.md`), so G2 (FlaUI triage flow) is written but run only on Brett's say-so.
> The scope and the gate are unchanged.

## Shape of the work

| Project | Kind | Holds |
| --- | --- | --- |
| `Leviathan.Media` | process | ffprobe runner, thumbnail and contact-sheet generation, sprite strips, perceptual hashes later; supervised, killable, restartable. |
| `Leviathan.Media.Core` | plain `net10.0` | ffprobe JSON parsing, metadata model, cache keys, quarantine rules (unit-tested off Windows). |
| `Leviathan.Video` | Windows | `IVideoEngine` with libmpv and LibVLCSharp implementations, swap chain hosting in WinUI. |
| `Leviathan.Tags` | plain `net10.0` | Tag model, ADS read/write behind `IAlternateStreamStore`, index sync. |
| `tests/fixtures/media` | test data | Small generated clips (several codecs/containers), corrupt and truncated files. Generate with ffmpeg in a script; never commit large or real media. |

## Slices

### A. Metadata (§9.3)

- [x] **A1. ffprobe runner** (done in `Leviathan.Media.Core`: `FfmpegLocator`, `SystemProcessRunner`, `FfprobeRunner`; the supervised `Leviathan.Media` process that hosts it arrives with A3/A4; 29 tests incl. the real ffprobe on generated clips) in `Leviathan.Media`: GPL ffmpeg build bundled (path configurable), `-show_format -show_streams
  -show_chapters -of json`, 10 s timeout, low priority, reads only headers on network files.
- [x] **A2. Parser and model** (done: `MediaInfo` and `FfprobeParser` in `Leviathan.Media.Core`; snapshot tests over six real ffprobe outputs (`tools/New-MediaSamples.ps1` makes them), 59 tests in all; Dolby Vision is tested from a hand-written record, since it cannot be generated) (`Leviathan.Media.Core`): container, streams (video/audio/subtitle), chapters, tags, HDR, frame
  rate; snapshot tests (Verify) over the fixture set.
- [x] **A3. Cache and quarantine** (done: `MediaMetadataService` in `Leviathan.Media.Core` and `SqliteMediaMetadataStore` in `Leviathan.Data` over the existing `media_metadata` table; 16 service tests and 9 store tests; the Media process that calls `RecoverFromCrash()` on start comes with A4). SQLite `media_metadata` keyed by path + size + mtime; a file that times out or crashes
  ffprobe is marked unreadable until it changes; Media process crash → restart, no retry loop.
- [x] **A4. Lazy probing** (done as A4a and A4b. A4a: the `Leviathan.Media` process, `IMediaService` (contract minor 10), `MediaClient`, `MediaHostConnection` (on demand, restarted, disabled after three crashes in five minutes), listed in the Background tab; 6 tests against the real process. A4b: `MediaInfoTracker` on every tab (`TabViewModel.Media`): visible rows after one cache read, then the rest, whole-folder fill with progress, cancel on navigate, 19 tests, and the app adapter `MediaInfoSource`. The grid does not call `SetVisible` / `FillFolder` yet: that is A5, when there are media columns to show, so no file is probed for nothing.) Visible rows only, low priority, cancelled on navigate; sorting by a media column fills the whole
  folder in the background with progress in the column header (§8.2).
- [x] **A5a. Media columns** (done: the 11 columns of spec 8.2 in the chooser and the Details view, read lazily for the rows on screen only when a media column is shown, sorting by one reads the whole folder with "Duration · 45%" in its header; seen on screen with a test folder, 47 tests)
- [x] **A5b. Tiles and Gallery layouts** (done: both are flow layouts in the existing wrapping view; Tiles show a small thumbnail, the name, the media facts and size and date, read lazily for the files on screen; Gallery shows large thumbnails with the name; Ctrl+Shift+4 and Ctrl+Shift+1, enabled in View ▾; seen on screen; true justified rows and hover scrub wait for B1/B3, see decisions)

- [x] **A6. Status hub Background tab** (done for what exists: a "Video and audio details" section with the media process's counts (reading, waiting, files read) and the files it gave up on, each with "Retry this file"; the Media process row was already there from A4a. Thumbnail and contact-sheet queues with pause and priority come with B1/B2, where those queues exist; see decisions)

### B. Thumbnails, contact sheets, hover scrub (§10.4, §10.5)

- [x] **B1. Video thumbnails** (done: for a video the shell has no thumbnail for, the Media process draws a frame with ffmpeg (keyframe seek to a tenth in, scaled to the cell), keeps it on disk as a JPEG (512 MB cap, least recently used removed), two at a time, cancelled when the row scrolls away; the shell's own thumbnail still wins when it has one; `IMediaService.GetThumbnailAsync`, contract minor 11; seen on screen with `LEVIATHAN_VIDEO_FRAMES=1`)
- [x] **B2a. Contact sheet engine** (done: `IMediaService.GetContactSheetAsync` (contract minor 12) draws a 3×3 to 6×6 grid in one ffmpeg run with a keyframe seek per frame, cached as one JPEG in `sheets` (256 MB cap, apart from the thumbnails), one at a time, cancellable, with the moment each frame was taken so a click can play from there)
- [x] **B2b. Contact sheets you can see** (done: "Contact sheet" in the right-click menu and the command bar for one video opens a window with the grid (3×3 to 6×6), hover shows where in the video each frame is from, and Save as… exports the JPEG; the same command on a selection of videos asks for a folder and makes a sheet of each there in the background, with progress and Cancel on the Background tab; click a frame to play from there waits for the player, C1/C2)
- [x] **B3. Hover scrub** (done: resting the mouse on a video's picture in Tiles, Thumbnails or Gallery for 250 ms asks the media process for a 20-frame strip, then moving across the picture slides through the video with a progress line and the time of the frame; strips are cached on disk beside the sheets and the last 24 in memory; the `HoverScrub` setting turns it off, and network videos follow "thumbnails on network"; the optional muted live playback after 1 s waits for the player, C1/C2)

### C. Playback (§9.1, §10)

- [x] **C1. `IVideoEngine`** (done: `IVideoEngine`, `PlaybackSnapshot` and `AudioTrack` in Core; `FakeVideoEngine` in the testing project; `PlaybackController` in the view models works out the spec §10.2 keys on top of any engine: jumps, percent keys, Home/End, frame step, the speed ladder and J/K/L; nothing to see on screen yet, the first picture is C2)
- [ ] **C2. libmpv engine** hosted in WinUI (swap chain panel), hardware decode, precise seeking.
- [ ] **C3. LibVLC engine** via LibVLCSharp; switch engines in Settings without restart.
- [ ] **C4. Pre-warm and pre-buffer**: engine warm while the preview pane is open; next file pre-opened in a hidden instance;
  on NAS read only header/index (MP4 tail when `moov` is at the end).

### D. Preview and details pane (§9)

- [ ] **D1. Pane shell**: collapse (Alt+P), splitter width, S/M/L video size, drag handle under the video, floating preview
  (Space), follows the focused split pane.
- [ ] **D2. Renderers**: video (with icon seek controls 5 min / 1 min / 30 s / play / 30 s / 1 min / 5 min / skim, filmstrip,
  hover frame preview), audio, images (WIC, zoom/pan, EXIF orientation), PDF, text/code (first 2 MB), Markdown/HTML in a
  sandboxed WebView2, Office via shell preview handlers in ShellHost, archives, fonts, folders, multi-selection.
- [ ] **D3. Details**: grouped metadata (general, tags, media streams, image EXIF/IPTC/XMP, audio tags, document, all Windows
  properties), checksums on demand, in-place editing of tags/rating/title/comments.

### E. Triage (§10.1–§10.3, §10 "working at scale")

- [ ] **E1. Triage view model**: queue from selection or folder (optionally recursive), untagged-only filter, start offset,
  auto-advance, decisions, undo stack, summary; fully unit-tested with the fake engine.
- [ ] **E2. Key map** (§10.2, remappable): Space, ←/→ 30 s, Shift ±1 min, Ctrl ±5 min, Alt ±5 s, `.`/`,` frames, Ctrl+1–9
  percentage, Home/End, J/K/L, `[`/`]`, S skim, Enter/G keep, Del/X discard, 1–9 tags, ↓/Tab skip, ↑ previous, C contact
  sheet, R rename, M move to preset, Ctrl+Z undo, Esc exit.
- [ ] **E3. Keep / Discard actions**: Keep = green tag, optionally also move to a chosen folder (same-share rename, otherwise
  a queued job); Discard = soft delete via the Phase 2 engine.
- [ ] **E4. Triage window**: full-window player, overlay, progress bar (session and inbox totals, kept/discarded/skipped,
  space reclaimable), queue side list.
- [ ] **E5. Inboxes and sessions**: saved named queues over a folder, new arrivals appended and marked New, batch of N
  (default 1,000) ordered oldest/newest/largest/random; resume across sessions.
- [ ] **E6. Skim playback**: 3 s bursts every 5%, next point decoded ahead; in triage and the preview pane.

### F. Colour tags (§11)

- [ ] **F1. Tag model and settings**: 7 defaults (Green = Keep), rename/recolour/reorder up to 32, keys 1–9, Ctrl+0 clears.
- [ ] **F2. Storage**: `:leviathan.tags` alternate data stream (JSON), original modified time restored after writing, SQLite
  index keyed by volume serial + file ID, fallback index for FAT/SFTP/FTP/NFS; import the Files app's `:files` stream;
  optional Windows "Tags" property for supported formats.
- [ ] **F3. Display and filters**: dots in Details, badges on thumbnails, optional row tint, Tags sidebar section listing
  everything with a tag, `tag:` filter and query support, "tags may be lost" warning for destinations without streams.

### G. Verification and finish

- [ ] **G1. Fixture generator script** and parser snapshot tests; corrupt-file suite (Media restarts, files quarantined, UI unaffected).
- [ ] **G2. FlaUI triage flow**: open triage, keep/discard/skip/undo with keys, verify tags and soft deletes.
- [ ] **G3. Benchmarks**: preview start < 300 ms local, thumbnail < 200 ms cached / < 1 s generated, triage advance latency.
- [ ] **G4. Gate and wrap-up**: `docs/phase-3.md`, README, tag `phase-3`, push, stop for review.

## Gate (from the build plan)

Triage 100 NAS videos at under 10 s each; a corrupt-file test set causes no crash. The 100-video run needs Brett: time it
and record the result in `docs/phase-3.md`.
