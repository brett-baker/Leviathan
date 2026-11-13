# Phase 5 plan — Power features and polish

Scope, spec sections and gate are in `docs/build-plan.md` (Phase 5). Working checklist; tick a slice when it is committed and pushed.
Every slice follows `CLAUDE.md`.

> Written before Phases 1–4 finished. At the start of Phase 5, re-read the earlier `docs/phase-N.md` files and adjust;
> keep the scope and the gate.

## Slices

### A. Duplicate finder (§15.2)

- [x] **A1. Exact duplicates**: group by size → partial hash (first and last 1 MB) → full xxHash3 only for remaining
  candidates; scopes (folders, drives, shares), type and minimum-size filters; runs in the worker with progress.
- [x] **A2. Similar media**: perceptual hashes for images and for videos (frames at fixed percentages + duration) in
  `Leviathan.Media`, cached; tolerance setting.
- [x] **A3. Results UI** (exact duplicates; Alt+Shift+F): groups with thumbnails and metadata side by side; auto-select rules (highest resolution, largest,
  newest, oldest, preferred folder); actions (soft delete, move, replace with hard link on the same volume, tag).
- [x] **A4. Similar media in the results window**: scan images and videos through the Media process (A2 signatures), show them as lookalike groups (no hard link), with the tolerance setting; video thumbnails in the window.

### B. Bulk rename (§15.3)

- [x] **B1. Rename pipeline**: ordered operations (find/replace plain or regex, insert, remove, case, trim, numbering) and
  tokens (`{name}`, `{ext}`, `{date:…}`, `{taken}`, `{duration}`, `{resolution}`, `{codec}`, `{parent}`, `{counter}`);
  property tests (no collisions created silently, reversible).
- [ ] **B2. Dialog**: live preview table, conflicts highlighted, presets, one undo for the batch, runs as a worker job.

### C. Archives and checksums (§15.4)

- [ ] **C1. Archive provider**: browse zip, 7z, rar (read-only), tar, gz, iso as folders via 7-Zip (`7z.dll`, as Files does),
  with preview.
- [ ] **C2. Extract and compress jobs**: extract here / to folder / to location, create zip or 7z with level and optional
  password, through the queue with progress.
- [ ] **C3. Checksums**: MD5, SHA-1, SHA-256, SHA-512, xxHash3, CRC32 for one or many files; verify against `.sha256`/`.md5`;
  export checksum files.

### D. Folder compare and sync (§15.5)

- [ ] **D1. Compare engine**: by name + size + date or by checksum; left-only, right-only, newer left/right, identical.
- [ ] **D2. Sync modes**: mirror left→right, mirror right→left, two-way (newer wins, conflicts flagged), update; deletions go to
  soft delete / Recycle Bin; dry-run preview always first.
- [ ] **D3. Saved sync pairs**: re-run with one click, pinned to Favorites, optional schedule.

### E. Everything else in §15.6 and §6

- [ ] **E1. Command palette** (Ctrl+Shift+P) over every command with shortcuts; Ctrl+P "go to" palette (favorites, recent,
  tabs, commands).
- [ ] **E2. Open terminal here** (Windows Terminal, PowerShell, cmd, WSL).
- [ ] **E3. Quick move/copy targets**: 9 preset destinations bound to keys everywhere.
- [ ] **E4. Elevated operations helper**: only the action that needs admin rights runs elevated.
- [ ] **E5. Git status overlays** (optional; port from Files `Utils/Git`).
- [ ] **E6. Folder notes** in the details pane.

### F. Settings and keyboard (§17)

- [ ] **F1. All Settings pages** in the bottom settings sheet from Phase 1 slice I10 (General, Appearance, Files and folders, Tabs and windows, Network, Operations, Delete, Media
  and preview, Tags, Search and index, Keyboard, Advanced) bound to the settings store; every setting applies without restart.
- [ ] **F2. Keyboard page**: search, rebind, reset, import/export; conflict detection.
- [ ] **F3. Diagnostics view**: crash groups by signature, Copy for Claude, Open folder, Report a problem.

### G. Performance and hardening pass (§3)

- [ ] **G1. Budget sweep**: run every §3.1 benchmark; fix regressions; record results.
- [ ] **G2. Memory**: idle with 10 tabs < 250 MB; thumbnail LRU caps; leak tests for long sessions (open/close 1,000 tabs).
- [ ] **G3. Stress**: 500,000-file library browse and search, 100 GB file operations, long paths everywhere, names with
  every awkward character, dead shares during every operation.
- [ ] **G4. Accessibility pass**: keyboard-only use of every feature, screen-reader names on controls, contrast in both themes.

### H. Finish

- [ ] **H1. Gate and wrap-up**: `docs/phase-5.md`, README, tag `phase-5`, push, stop for review.

## Gate (from the build plan)

Feature-complete against the spec after a month of daily use. Keep a running list of issues Brett reports during that month
in `docs/phase-5.md` and close each with a commit and a regression test.
