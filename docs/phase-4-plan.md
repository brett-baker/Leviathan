# Phase 4 plan — Search, space map and more locations

Scope, spec sections and gate are in `docs/build-plan.md` (Phase 4). Spec: §13 (search), §14 (space map), §7.1 (SFTP, FTP(S), NFS),
§7 Internet section, §16.1 (Space tab), §12.7 (folder-size service). Working checklist; tick a slice when it is committed and pushed.
Every slice follows `CLAUDE.md`. Run and test from Visual Studio (F5, Test Explorer, Output window) when asking Brett to try something.

**Gate:** local name search < 50 ms; a space map of a 1 TB share completes and can resume.

## Reused, not rebuilt

- Folder-size service and its SQLite store (Phase 2): the space map and the NAS index ride the same crawl (§13, §14).
- `ILocationProvider`, `LocationErrors`, `ShareHealthTracker` (Phase 2): SFTP, FTP and NFS are new providers behind them.
- Tags index and `tag:` filter (Phase 3): the query parser's `tag:` and `type:` filters reuse them. Media fields (`duration:`, `res:`, `codec:`)
  read the `media_metadata` cache.
- Background tab and Status hub (Phase 1/3): indexer and crawl rows are added there.
- Sidebar sections (Phase 3): Network and Internet gain saved connections and cloud sync status.
- Cloud-drive detection (`Leviathan.Providers.Cloud`): the Internet section adds sync status.

## Shape of the work

| Project | Kind | Holds |
| --- | --- | --- |
| `Leviathan.Search.Core` | plain `net10.0` | Query model, parser (property-tested), matcher, saved searches, result merge. |
| `Leviathan.Indexer` | Windows (optional service) | USN/MFT index fallback when Everything is absent; named-pipe query API. |
| `Leviathan.Providers.Sftp` | plain `net10.0` | SSH.NET provider behind `ILocationProvider`. |
| `Leviathan.Providers.Ftp` | plain `net10.0` | FluentFTP provider (FTP and FTPS). |
| `Leviathan.SpaceMap.Core` | plain `net10.0` | Squarified treemap layout, scan tree, colour by type or age. |

## Slices

### A. Query and search core (§13, §13.1)

- [x] **A1. Query model and parser** (done: `Leviathan.Search.Core`, 53 tests incl. seeded property tests) — words, quoted phrases, wildcards, `-exclude`, `regex:`, `ext:`, `size:`, `modified:`, `created:`,
  `tag:`, `type:`, `path:`, `duration:`, `res:`, `codec:`, `content:`. Errors become a message with a position, never an exception. FsCheck
  property tests: parse never throws, printing then parsing is stable, filters combine as AND.
- [ ] **A2. Matcher** — evaluates a query against a file entry (and media/tag data through small interfaces with fakes). Size and date
  units, relative dates (`lastweek`), case rules.
- [ ] **A3. Live search** — parallel recursive enumeration in the worker, streamed in batches, cancellable, bounded parallelism per share;
  results appear in an ordinary file-view tab with every layout and column.
- [ ] **A4. Search UI** — search box and filter bar, results tab, status ("searched N folders"), cancel; no I/O on the UI thread.
- [ ] **A5. Saved searches** — save, pin to Favorites, open as a self-updating folder.

### B. Local index (§13)

- [ ] **B1. Everything SDK** — `IEverythingClient` wrapper (fake in `Leviathan.Testing`), detection of a running instance, query translation
  from the parser, name search under 50 ms measured as a benchmark.
- [ ] **B2. `Leviathan.Indexer` fallback** — MFT enumeration and USN journal tail for local NTFS volumes, SQLite or memory-mapped store, optional
  service install; tested against a temp VHD where the machine allows it. Indexer status in the Background tab.
- [ ] **B3. Source selection and merge** — pick Everything, own indexer, NAS index or live search per scope; merge and de-duplicate results.

### C. NAS index (§13)

- [ ] **C1. Index fill from the folder-size crawl** — the same pass writes name, size, date to a SQLite index for folders the user opts in.
- [ ] **C2. Re-crawl and change detection** — low-priority schedule, pause while a job copies to the same NAS, crawl status in Background.

### D. Space map (§14)

- [ ] **D1. Layout** — squarified treemap, colour by type or age, minimum rectangle size, layout tests including degenerate trees.
- [ ] **D2. Scanner** — streams in the worker, limited parallelism on network, pause, resume from a checkpoint, cached with date, rescan changed folders only.
- [ ] **D3. Space map tab** — sortable tree with size, percentage, file count and modified; treemap with linked selection, zoom, Backspace out, legend.
- [ ] **D4. Actions and extras** — open, reveal, tag, soft delete, compress, find duplicates here; colour by age; size filter; hard links and
  placeholders counted once.
- [ ] **D5. Mini map and Status hub Space tab** — treemap for a selected folder in the preview pane; compact map plus largest items in the hub.

### E. More locations (§7.1)

- [ ] **E1. Connection model and dialog** — Add connection for SMB, SFTP, FTP(S), NFS; secrets in Windows Credential Manager; health dot.
- [ ] **E2. SFTP** — SSH.NET provider: list (streamed), read, write via `*.leviathan-part`, rename, delete, host-key check, timeouts; integration tests against a container.
- [ ] **E3. FTP(S)** — FluentFTP provider; compare Files' FTP storage first; integration tests against a container.
- [ ] **E4. NFS** — through the Windows Client for NFS (UNC or mapped drive), detection of the feature, clear message when it is not installed.
- [ ] **E5. Sidebar integration** — saved connections in Network, drill-down in the tree with the §7.2 rules, offline messages.
- [ ] **E6. Internet section** — sync status for detected cloud folders (provider state from the cloud-provider registry and file attributes).

### F. Gate and wrap-up

- [ ] **F1. Benchmarks** — name search < 50 ms (Everything and own index), space-map layout of 1 million rectangles, scan resume.
- [ ] **F2. Space map of a large share** — 1 TB share completes and resumes after a killed worker (chaos test with a synthetic tree; lab run on the NAS by Brett).
- [ ] **F3. UI smoke test** (written; run only on Brett's say-so) and UAT playbooks for slices a person can see.
- [ ] **F4. Wrap-up** — gate numbers, `docs/phase-4.md`, README status, tag `phase-4`.

## Order

A → D1/D2 (pure engine) → B → C → D3–D5 → E → F. Search and the space map share the crawl, so C1 and D2 are built to use one scanner.
Slices with nothing to see (A1, A2, B3, D1) are offered "continue" first; the rest are offered a playbook.

## Open questions (decide as built; log in `docs/decisions.md`)

- Everything SDK is a native DLL from voidtools: ship it, or load it from the user's installation only.
- Whether `Leviathan.Indexer` ships in Phase 4 as a service or only as an in-process fallback; the build plan says optional service.
- SFTP and FTP test containers need Docker on this machine; if absent, the integration tests skip with a reason and Brett runs the lab.
