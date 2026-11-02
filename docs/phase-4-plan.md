# Phase 4 plan — Search, space map and more locations

Scope, spec sections and gate are in `docs/build-plan.md` (Phase 4). Working checklist; tick a slice when it is committed and pushed.
Every slice follows `CLAUDE.md`.

> Written before Phases 1–3 finished. At the start of Phase 4, re-read the earlier `docs/phase-N.md` files and adjust;
> keep the scope and the gate.

## Shape of the work

| Project | Kind | Holds |
| --- | --- | --- |
| `Leviathan.Search` | plain `net10.0` | Query parser, search sources, result merging, saved searches. |
| `Leviathan.Search.Everything` | Windows | voidtools Everything SDK client (IPC; Everything must be running). |
| `Leviathan.Indexer` | process / optional Windows service | USN change-journal indexer fallback when Everything is absent; NAS index fed by the folder-size crawl. |
| `Leviathan.Providers.Sftp` | plain `net10.0` | SSH.NET provider. |
| `Leviathan.Providers.Ftp` | plain `net10.0` | FluentFTP provider (compare with Files' `Files.App.Storage/Ftp`). |
| `Leviathan.Providers.Nfs` | Windows | NFS via Windows Client for NFS (mount and browse). |
| `Leviathan.Providers.Cloud` | Windows | Sync-client folders (OneDrive, Google Drive, Dropbox, Box, Synology Drive…) with status, from Phase 1's cloud detection. |
| `Leviathan.SpaceMap` | plain `net10.0` + controls | Squarified treemap layout, scan model, cached scans. |

## Slices

### A. Search (§13)

- [ ] **A1. Query parser**: words, wildcards, quoted phrases, `-exclude`, `regex:`, `ext:`, `size:`, `modified:`, `created:`,
  `tag:`, `type:`, `duration:`, `res:`, `codec:`, `content:`, `path:`; property-style tests over many generated queries.
- [ ] **A2. Search results tab**: streams results into a normal file view (all layouts, columns, tags, actions), cancellable.
- [ ] **A3. Live search** source: parallel recursive enumeration in the worker, throttled per share.
- [ ] **A4. Everything source**: Everything SDK when installed and running (local NTFS), detection and fallback; merge results
  if Everything also indexes a share.
- [ ] **A5. `Leviathan.Indexer` fallback**: MFT read + USN journal follow (optional service, admin install), only when
  Everything is not available.
- [ ] **A6. NAS index**: built from the folder-size service's crawl (same pass), opt-in folders, scheduled re-crawl, stored in
  SQLite; search results carry tags, sizes and media metadata.
- [ ] **A7. Content search**: Windows Search where the location is indexed by Windows, otherwise a live text scan.
- [ ] **A8. Saved searches**: save, pin to Favorites, behave like self-updating folders (e.g. "Untriaged videos on NAS").

### B. Space map (§14)

- [ ] **B1. Scan model**: streaming scan in the worker (from the folder-size store and index when available, live otherwise),
  pause/resume, limited parallelism on shares, cached with date, "Rescan changed".
- [ ] **B2. Treemap layout**: squarified algorithm, colour by type or by age, hard links and cloud placeholders counted once;
  layout unit tests (areas proportional, no overlaps).
- [ ] **B3. Space map tab**: linked tree (size, share bar, files) and treemap, zoom in/out, legend, actions (open, reveal, tag,
  soft delete, compress, find duplicates), filter by size.
- [ ] **B4. Mini map** in the preview pane for a selected folder once its size is known.
- [ ] **B5. Status hub Space tab** (§14, §16.1): compact treemap of the current folder with the largest items beside it, click selects
  the item in the file view, "Open space map"; free-space status-bar part opens it.
- [ ] **B6. Status hub Background tab:** Everything / indexer status, NAS index crawls with pause and schedule, space-map scans.

### C. More locations (§7.1, §2.4)

- [ ] **C1. SFTP provider** (SSH.NET): listing, streaming reads/writes for the copy engine, host key handling, credentials via
  Credential Manager, health tracking.
- [ ] **C2. FTP/FTPS provider** (FluentFTP).
- [ ] **C3. NFS provider** via Windows Client for NFS (detect the feature; clear message when missing).
- [ ] **C4. Add connection dialog** completed for SFTP, FTP(S) and NFS; saved connections in the Network section.
- [ ] **C5. Internet section**: sync-client folders with sync status overlays, "Always keep on this device" / "Free up space"
  where the provider supports it.
- [ ] **C6. Copy engine across providers**: SFTP/FTP sources and destinations through the Phase 2 engine (resume where the
  protocol allows; verify by hash).

### D. Verification and finish

- [ ] **D1. Provider integration tests** against SFTP, FTP and NFS containers (`docker/compose.yml`), skipping with a reason
  when Docker is unavailable.
- [ ] **D2. Benchmarks**: local name search < 50 ms (Everything and fallback), NAS index query time, space-map scan rate.
- [ ] **D3. Gate and wrap-up**: `docs/phase-4.md`, README, tag `phase-4`, push, stop for review.

## Gate (from the build plan)

Local name search under 50 ms; a space map of a 1 TB share completes and can resume.
