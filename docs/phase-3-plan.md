# Phase 3 plan — Media and triage

Scope, spec sections and gate are in `docs/build-plan.md` (Phase 3). Working checklist; tick a slice when it is committed and pushed.
Every slice follows `CLAUDE.md`.

> Written before Phases 1–2 finished. At the start of Phase 3, re-read `docs/phase-1.md` and `docs/phase-2.md` and adjust
> to what exists; keep the scope and the gate.

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

- [ ] **A1. ffprobe runner** in `Leviathan.Media`: GPL ffmpeg build bundled (path configurable), `-show_format -show_streams
  -show_chapters -of json`, 10 s timeout, low priority, reads only headers on network files.
- [ ] **A2. Parser and model** (`Leviathan.Media.Core`): container, streams (video/audio/subtitle), chapters, tags, HDR, frame
  rate; snapshot tests (Verify) over the fixture set.
- [ ] **A3. Cache and quarantine.** SQLite `media_metadata` keyed by path + size + mtime; a file that times out or crashes
  ffprobe is marked unreadable until it changes; Media process crash → restart, no retry loop.
- [ ] **A4. Lazy probing.** Visible rows only, low priority, cancelled on navigate; sorting by a media column fills the whole
  folder in the background with progress in the column header (§8.2).
- [ ] **A5. Media columns** in Details: duration, resolution, frame rate, codecs, bitrate, container, HDR, channels, languages,
  subtitles; Tiles and Gallery layouts (deferred from Phase 1).

### B. Thumbnails, contact sheets, hover scrub (§10.4, §10.5)

- [ ] **B1. Video thumbnails** via ffmpeg when the shell has none; cached on disk; cancellation for off-screen items.
- [ ] **B2. Contact sheets**: 3×3 to 6×6 frames by keyframe seeking, cached as one JPEG, click a frame to play from there,
  background generation for a folder.
- [ ] **B3. Hover scrub**: 16–24 frame sprite strip generated on first hover, horizontal position → time, optional muted
  live playback after 1 s still.

### C. Playback (§9.1, §10)

- [ ] **C1. `IVideoEngine`** abstraction (open, play/pause, seek absolute/relative, speed, frame step, audio track, events) with
  a fake for view-model tests.
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
