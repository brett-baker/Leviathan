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
- [x] **A2. Matcher** (done: `QueryMatcher`, `ISearchFacts`, `FileTypes`; 65 tests) — evaluates a query against a file entry (and media/tag data through small interfaces with fakes). Size and date
  units, relative dates (`lastweek`), case rules.
- [x] **A3. Live search** (done: `LiveSearch` engine, 13 unit tests and 4 on-disk tests; hosted in-process for now, see decisions) — parallel recursive enumeration in the worker, streamed in batches, cancellable, bounded parallelism per share;
  results appear in an ordinary file-view tab with every layout and column.
- [x] **A4. Search UI** (done: results folder, search box, Ctrl+F, status line; 14 + 20 tests; the UI is built and starts but is not yet seen by hand) — search box and filter bar, results tab, status ("searched N folders"), cancel; no I/O on the UI thread.
- [x] **A5. Saved searches** (done: Favorites entries over the search folder, save command, sidebar look; 6 tests) — save, pin to Favorites, open as a self-updating folder.

### B. Local index (§13)

- [x] **B1. Everything SDK** (done and tested with a fake; the real library calls are unverified: no Everything on this machine) — `IEverythingClient` wrapper (fake in `Leviathan.Testing`), detection of a running instance, query translation
  from the parser, name search under 50 ms measured as a benchmark.
- [x] **B2. Local index without Everything** (done as a crawl-built in-memory `NameIndex` + `IndexCrawler` + `NameIndexSearch`; 40 tests; first results of a million-name search 3 to 28 ms. The MFT/USN service is B2b below) — a name index filled by crawling, shared with the NAS index (C).
- [ ] **B2b. `Leviathan.Indexer` MFT + USN fast path** — needs an elevated process or a VHD to test, so it is left open: enumerate the MFT and tail the change journal into `NameIndex.ReplaceChildren`; optional service install; Indexer status in the Background tab.
- [x] **B3. Source selection and merge** (done: `SearchCoordinator`, sources in the status line; 11 + 1 tests) — pick Everything, own indexer, NAS index or live search per scope; merge and de-duplicate results.

### C. NAS index (§13)

- [x] **C1. Index fill** (done as its own crawl with an SQLite copy and an opt-in command, see decisions; 20 tests) — the same pass writes name, size, date to a SQLite index for folders the user opts in.
- [x] **C2. Re-crawl and change detection** (done: schedule, pause, held back during a copy to the same server, folders you open refresh the index, Search index line and Pause button in the Background tab; 20 tests) — low-priority schedule, pause while a job copies to the same NAS, crawl status in Background.

### D. Space map (§14)

- [x] **D1. Layout** (done: `Leviathan.SpaceMap.Core` model, squarified layout with remainder tiles, colour groups, legend; 33 tests) — squarified treemap, colour by type or age, minimum rectangle size, layout tests including degenerate trees.
- [x] **D2. Scanner** (done: progressive tree, pause, checkpoints, resume, SQLite copy; 15 + 5 tests. Rescan of changed folders only: not built, see decisions) — streams in the worker, limited parallelism on network, pause, resume from a checkpoint, cached with date, rescan changed folders only.
- [x] **D3. Space map window** (done as a window; scan to frame to bitmap verified on a 252 GB folder; the picture has not been looked at by a person) — sortable tree with size, percentage, file count and modified; treemap with linked selection, zoom, Backspace out, legend.
- [x] **D4. Actions and extras** (done except compress, find duplicates here and hard links; see decisions) — open, reveal, tag, soft delete, compress, find duplicates here; colour by age; size filter; hard links and
  placeholders counted once.
- [x] **D5. Mini map and Status hub Space tab** (mini map existed; Space tab done; 5 tests; the tab has not been looked at by a person) — treemap for a selected folder in the preview pane; compact map plus largest items in the hub.

### E. More locations (§7.1)

- [x] **E1. Connection model and dialog** (done: model, list, input checks, Credential Manager secrets, known hosts, the dialog with five types; 32 + 7 tests; the dialog has not been looked at by a person) — Add connection for SMB, SFTP, FTP(S), NFS; secrets in Windows Credential Manager; health dot.
- [x] **E2. SFTP** (done: listing, read, write, rename, delete, folders, host keys; against a real server in Docker. Not wired to copy/move in the worker, see decisions) — SSH.NET provider: list (streamed), read, write via `*.leviathan-part`, rename, delete, host-key check, timeouts; integration tests against a container.
- [x] **E3. FTP(S)** (done like SFTP against a Docker FTP server; FTPS is built with certificate trust but was not run against a server; Files' FTP storage was not compared) — FluentFTP provider; compare Files' FTP storage first; integration tests against a container.
- [x] **E4. NFS** (done as a network path through Windows' Client for NFS plus a clear message when it is missing; unverified: no NFS client here) — through the Windows Client for NFS (UNC or mapped drive), detection of the feature, clear message when it is not installed.
- [x] **E5. Sidebar integration** (done: connections under Network, expand through the provider, remove, sign in again; health dots are not shown for them) — saved connections in Network, drill-down in the tree with the §7.2 rules, offline messages.
- [x] **E6. Internet section** (done: sync client running / not running / folder missing; not per-file progress) — sync status for detected cloud folders (provider state from the cloud-provider registry and file attributes).

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
