# Phase 2 plan — NAS resiliency and operations engine

Scope, spec sections and gate are in `docs/build-plan.md` (Phase 2). This file is the working checklist: each slice is small
enough to write, test, commit and push in one sitting, in dependency order. Tick a slice when it is committed and pushed.

Every slice follows `CLAUDE.md`: code and tests together, zero warnings in `src/`, whole suite green, app run by hand when
the slice touches UI or process behaviour, commit, push.

## Adjusted to what Phase 1 built (re-read of `docs/phase-1.md` and the engine code)

Phase 1 already has more of the engine than this plan first assumed, so several slices extend existing code instead of starting it:

- **Copy/move:** `CopyMoveExecutor` (Engine/Transfers) already writes `*.leviathan-part`, renames when complete, renames on one volume,
  hash-verifies a cross-volume move before deleting the source, and applies `ConflictPolicy` (KeepBoth / Replace / Skip, chosen up
  front). B1–B6 grow it in place behind `IFileCopier` / planner types; they do not add a second executor. It has no per-file
  journal, no resume, no waiting state, no server-side copy and no ask-the-user conflicts: those are the work.
- **Delete / restore:** `DeleteExecutor`, `RestoreExecutor` and `RecycleBinIndex` exist (Recycle Bin, share `#recycle`, undo).
  D1–D3 add `.leviathan-trash`, the manifest, the bin view and purge; D2 reuses `Restore`.
- **Folder sizes:** `FolderSizeService`, `ComputeFolderSizeExecutor` and `SqliteFolderSizeStore` exist and run in the background
  lane. E1–E2 are the UI wiring and deltas. E2 also fixes the duplicate folder-size job for one folder seen in Phase 1.
- **Queue and hub:** `JobScheduler` (limits, lanes, priorities), `JobJournal` (JSON lines), `SpeedMeter` and the status hub with
  an Operations tab exist; C1–C5 complete them rather than build them.
- **Network basics:** `ShareHealthTracker`, `BackoffPolicy`, `LocationErrors`, the listing cache (memory + `listings.db`) and
  `LocalFolderWatcher` exist. A2/A3/A4 wire and extend them; A3 reuses the saved listings as the offline cache.
- **Contracts:** `ConflictPolicy` needs Ask and the conditional rules (newer, larger, different) for C6; adding enum members and
  request fields is a `Leviathan.Contracts` change, so bump `ContractVersion.Major` if the wire shape breaks.
- **Phase 1 carry-over that touches this phase:** stalls over 50 ms on first show of the Background tab and a second window,
  the six failing UI smoke tests (H1), and cold start (about 1,010 ms against 600 ms). They are listed as G5 and are not a gate for
  starting; the tag `phase-1` stays where it is.

Start order: A1 → A2 → B1–B6 are the critical path to the gate; C and D can follow B; E and F are independent of each other.

## Shape of the work

| Project | Kind | Holds |
| --- | --- | --- |
| `Leviathan.Providers.Smb` | plain `net10.0` (+ Windows interop via `Leviathan.Interop`) | Server identity, share enumeration, discovery, credentials, change polling; the UNC listing itself stays in `Leviathan.Providers.Local`. |
| `Leviathan.Engine` (`Operations/`) | plain `net10.0` | Copy/move planner, chunked copier, resume journal, verify policy, conflict rules, soft-delete manifest. |
| `Leviathan.Interop` | Windows | `CopyFile2` with progress callback, alternate data streams, `WNetAddConnection2`, Credential Manager. |
| `Leviathan.Controls` | WinUI 3 | Activity strip, Operations panel, sparkline, conflict dialog. |
| `Leviathan.Chaos.Tests` | tests | Worker-kill and network-drop suites (nightly). |
| `docker/` | test infra | Samba and Toxiproxy compose file for integration/chaos tests. |

Reused from Phase 0/1: `JobScheduler`, `JobHost`, journal, `SpeedMeter`, `ShareHealthTracker`, `BackoffPolicy`,
`LocationErrors`, `FolderSizeService`, `DeviceKeys`, Phase 1's basic copy/move (replaced here), `FaultInjectingLocationProvider`.

## Slices

### A. Network foundations (§3.3, §3.5, §7.1, §7.2)

- [x] **A1. Server identity.** Resolve one identity per SMB server across names and addresses (NetBIOS name, DNS name, IPs,
  DDNS): query the server's SMB identity where available, fall back to a normalized host key. `DeviceKeys` and caches use it.
  Unit tests with fake resolvers; manual check against the DS2419+ (two IPs, `Diskstation`, DDNS name).
  Done as `ServerIdentityMap` (Core/Resilience): names sharing a resolved address merge, first-seen key stays stable, loopback/link-local never merge, lookups time out. `DeviceKeys.For/ForJob` have identity overloads; the worker identifies UNC servers (1.5 s cap) before choosing a job's device. Not yet: tree, caches and server-side copy use it (A2, A6, B1); real-NAS check is for Brett's lab run.
- [x] **A2. Share health wiring.** One `ShareHealthTracker` per server, fed by every provider call's latency and errors;
  `ShouldAttempt` gates calls (fail fast < 2 s while Unreachable); health exposed to tabs, tree and status bar.
  Done as `ShareHealthRegistry` (one tracker per server identity, `HealthChanged`, `Snapshot`) and `HealthTrackingLocationProvider`, which wraps the app's provider: latency to the first batch gives Online/Slow, drops and timeouts count as failures, logon failures mean Auth required, "not found"/"denied" prove the server is alive, cancellation records nothing, an Unreachable server fails at once with "Trying again in N s". `AppHost.ShareHealth` exposes it. Not yet: health dots in the tree (A6), the banner and Work offline (A3); mapped drive letters are not tracked until A5; the worker's copy path does not feed health yet (B5).
- [x] **A3. Fast-fail banners and offline cache** (done: the listing cache and its SQLite store already existed; added the greyed offline listing + banner with Retry / Work offline, `WorkOfflineRegistry`, tab warning badge; the banner by eye needs a real dead share, Brett's lab run). Last listing of each visited network folder cached (SQLite), shown greyed
  "offline — as of 14:02" with Retry / Work offline when the share is unreachable (§3.3, §8.6).
- [x] **A4. Change detection** (done: local watching already existed; `PollingFolderWatcher` + `CompositeFolderWatcher` for network folders and mapped drives; real-share behaviour is Brett's lab run). `ReadDirectoryChangesW` for local folders; polling with back-off for network folders (folder
  last-write time + listing diff), cancelled when the folder is no longer shown.
- [x] **A5. Credentials and reconnect** (done: `IShareConnector` + `WindowsShareConnector`, `ReconnectingLocationProvider`, "Sign in…" on the state panel; the Win32 calls and the dialog are unverified against a real disconnected drive or sign-in, Brett's lab run; not done: worker-side reconnect, mapped-drive health tracking). Windows Credential Manager for stored credentials; reconnect disconnected mapped drives
  on first use; Leviathan never stores passwords (§3.3).
- [x] **A6. Network section of the tree** (done: `ServerSharesProvider` + `WindowsShareEnumerator`, `SavedConnectionList`, health dots, Add connection… with optional drive letter, Nearby via `MdnsDiscovery` + `WsDiscovery` + `NearbyService`; by eye and against a real NAS is Brett's lab run; IPv6 discovery and SFTP/FTP/NFS entries come later) (§7.1): saved servers with health dots, shares, mapped drives, Add connection dialog
  (SMB now; SFTP/FTP/NFS fields arrive in Phase 4), "Nearby" discovery via WS-Discovery and mDNS `_smb._tcp` in the background.
- [x] **A7. Throughput tuning (done: buffers and per-destination memory already existed; added link speed in the status bar) (§3.5).** Measure throughput per share, adaptive buffer size (1–4 MB at 1 Gb/s, up to 8 MB on
  faster links), negotiated link speed in the status bar.

### B. Copy/move engine (§12.1, §12.2)

- [x] **B1. Planner.** Enumerate sources into the journal before copying; free-space check at the destination; fastest path per
  item: same-volume rename, same-server `CopyFile2` (server-side copy), otherwise chunked copier. Unit tests with fakes.
  Done as `TransferPlanner` (plan = items with resolved names and a path per file, exact totals), `TransferPlan` + `ITransferPlanStore` (JSON file per job, atomic; saved before the first byte, deleted when the job ends) and `CopyMoveExecutor` now running the plan step by step. Existing behaviour and tests unchanged. Not yet: `ServerSideCopy` is chosen but runs through the chunked copier until `CopyFile2` lands (B2); a restart plans afresh, resuming from the saved plan needs the per-file journal (B3).
- [x] **B2. Chunked copier.** `IFileCopier` with adaptive buffers, unbuffered I/O for files over 256 MB, writes to
  `*.leviathan-part`, renames when complete; preserves timestamps, attributes and alternate data streams (tags), ACLs optional.
  Done: `IFileCopier`; portable `ChunkedFileCopier` (buffer 1-8 MB from measured throughput per destination, unbuffered I/O from 256 MB with hand-aligned buffers and a padded last sector cut back to size, part file + rename, times, attributes incl. read-only, alternate streams via `IAlternateStreams`); `WindowsFileCopier` in Interop (`CopyFile2` with progress and cancel for server-side copy, real stream copy via `FindFirstStreamW`). The worker uses it. Not done: ACLs (optional in the spec), a server-side move is verified by size only until B4, `CopyFile2` against a real NAS is for the lab run.
- [x] **B3. Resume journal.** Per-file confirmed-bytes records; resume from the last confirmed chunk after a network drop,
  worker restart or reboot. Torn-write tests.
  Done: `ITransferJournal` (append-only JSON lines per job: steps done, bytes of a part file flushed to disk, item outcomes; torn or garbage lines skipped) and the executor now continues a saved plan whose request matches (same numbered names, finished steps skipped, a half-copied file continued from its last confirmed byte, a move whose source is gone and target present counted as done). A transient network error leaves plan and journal and reaches the host as a transient `LocationException`, which moves the job to Waiting. Tests: resume after a mid-file drop, after a simulated worker restart with a new host, torn/garbage journal lines, a stale plan not reused. Not done: server-side copy restarts the file from 0; leftover plan/journal/part files of cancelled jobs are not cleaned up yet.
- [x] **B4. Verify policy.** xxHash3 (`System.IO.Hashing`): full verify up to 4 GB, sampled above (first/last 64 MB + 16 random
  4 MB blocks), full verify per job/destination option; cross-volume move deletes the source only after verify passes.
  Done: `VerifyPolicy.Decide` (moves always checked, copies when a network location is involved or Full is asked; Full up to 4 GB else sampled), `HashFileVerifier` (xxHash3; a full check reuses the hash made while copying so only the target is read back; sampled = first/last 64 MB + 16 random 4 MB blocks, same blocks for the same file), `JobRequest.Verify` (Default / Full / Off; Off never applies to moves), contract minor 2. A bad copy is removed and a move keeps its original. Server-side copies are now verified too (both sides read). Not done: choosing the policy per destination in settings (C5b), reporting the method used in the job report (B5).
- [x] **B5. Failure handling.** Transient errors → Waiting with countdown, automatic resume when the share returns (default keep
  trying 30 minutes); permanent errors → one prompt with Skip / Retry / Skip all like this; safe rename for names invalid on
  the destination; end-of-job report.
  Done: transient drops -> Waiting with `RetryAt` (countdown) and a 30-minute limit that then fails the job (kept work stays); permanent errors (access denied, disk full, invalid name, failed verify) -> `JobPrompt` in the snapshot, answered with `AnswerPromptAsync` (Skip / Retry / Skip all like this), or `OnError = SkipAndReport`; names a network share would refuse are changed at plan time (`SafeNames`) and listed in the report; `JobReport` (counts, every item with its reason, notes) kept 7 days (`GetReportAsync`). Not done: the prompt UI and the countdown display (C3), a Skip-all choice survives only within one run, the app submits with SkipAndReport until C3 exists.
- [x] **B6. Replace Phase 1's basic copy/move** with the engine; contract version bump if the request shape changes.
  Done by evolving in place: the app has one transfer path (`WorkerTransfers` -> worker -> `CopyMoveExecutor`), so there was no second implementation to remove. Contract changes are additive (`Verify`, `OnError`, `RetryAt`, `Prompt`, `AnswerPromptAsync`, `GetReportAsync`), contract minor 3; the major stays 2.

### C. Queue and UI (§12.3, §12.6)

- [x] **C1. Scheduler features.** Live limits from the UI, bandwidth limit per job/destination with schedule, background lane
  pauses during a copy to the same NAS (already in the scheduler; wire and test end to end), 7-day reports.
  Done: `BandwidthSettings` (global + per-device `BandwidthRule`, each with scheduled `BandwidthWindow`s incl. past-midnight and day filters) + `JobRequest.BytesPerSecondLimit`; `BandwidthController` and `ByteRateLimiter` pace every chunk of the chunked copier, shared across jobs on the same budget, slowest limit wins, changes apply to the next chunk; worker `Get/SetBandwidthAsync` (contract minor 4); end-to-end tests for pacing, shared NAS budget, live change, the lane yielding to a copy on the same NAS, and settings crossing the pipe. Live concurrency limits and 7-day reports already existed (Phase 1 / B5). Not done: UI to edit limits and schedule (C5b), the app does not push saved limits to the worker yet, server-side copies are not throttled (they do not cross this PC).
- [x] **C2. Activity strip control.** 1–2 px line per job under the address bar, per-job colour from the 8-colour palette,
  states (shimmer while enumerating, dashed paused, pulse waiting, red tip on failures), tooltip, click opens the panel,
  up to 4 lines then merged "+N".
  Done: `ActivityStripModel` (lines from the running, paused, waiting and unseen-failed jobs; at most 4, the rest merged with a count; shimmer / dashed / pulse / red tip chosen from the job state), `JobPalette` (the 8 colours, shared with the Operations rows), tooltip text (name, percent, bytes, speed, time left) from `OperationsModel`, and the `ActivityStrip` control in a new row under the address bar that draws them and opens the Operations tab on click. 13 new tests. Not done: line height / category settings (C5b), grey lines for background tasks, queue number in the tooltip and scrolling to the job (C3); not yet looked at with a live job in the real window.
- [ ] **C3. Operations panel** (C3a done: groups, row and toolbar actions, multi-select, queue ETA, context menu; C3b done: Skip/Retry/Skip all prompt on the row, waiting countdown, Retry failed, report viewer with Show destination/source folder; copy and move now ask on a permanent error) as the Operations tab of the status hub from Phase 1 slice I7 (spec §12.6, §16.1), replacing
  its simple job list. Running / Queued / Completed-Failed groups; row actions (pause, resume, cancel, remove, move,
  priority, run now, retry failed, report, open source/destination); multi-select; toolbar (pause/resume all, limits, bandwidth,
  clear completed); queue ETA.
- [x] **C4. Hub positions and chip** (already built in I7; confirmed in code and `StatusHubTests`: the chip opens Operations, placement/size are remembered, a popped-out hub is restored as a fly-up; the total sparkline in the chip is C5; live-job check by eye is Brett's). The status-bar operations chip with total sparkline opens the hub on Operations; fly-up
  (default), docked bottom, docked right and pop-out (built in I7) checked with live jobs; position remembered.
- [x] **C5. Speed sparklines** (done: `Sparkline` geometry + `SparklineView`; running rows and the status-bar chip). Per-job sparkline from `SpeedHistory` next to the speed; total sparkline in the status-bar chip.
- [x] **C5b. Status hub additions** (done: Quick settings; Activity rows for copies, moves and deletes open the job's report, kept in the history file; the Background tab has a Network folders line with folders watched by polling and listings kept for offline use. A per-share crawl does not exist yet, it arrives with the NAS index in a later phase). Quick settings: running jobs per NAS volume, bandwidth limit and schedule, verify policy;
  Background: share crawls, change polling, offline-cache refresh; Activity: copy/move/delete entries link to their jobs and reports.
- [x] **C6. Conflict dialog** (done: `Ask` and the conditional policies, one question per job, `ConflictDialogContent` with rule for all, per-file rules and a Compare flyout with thumbnails; the dialog by eye is Brett's) (§12.4): one dialog per job, Replace / Skip / Keep both / if newer / if larger / if different
  (checksum), Compare view with thumbnails and metadata, apply-to-all, per-file overrides.

### D. Soft delete (§12.5)

- [ ] **D1. Recycle-folder detection.** Per share: Synology `#recycle` (when DSM Recycle Bin is enabled for that shared folder),
  QNAP `@Recycle`, else `.leviathan-trash` at the highest writable folder; cached per share.
- [ ] **D2. Soft delete and manifest.** Rename into the recycle folder (instant), manifest with original path and time; Delete vs
  Shift+Delete table per location type; Ctrl+Z restores.
- [ ] **D3. Soft-delete bin view.** All soft-deleted items across shares, restore, purge now; automatic purge by age (30 days)
  and size cap as background jobs with reports.

### E. Folder sizes in the UI (§12.7)

- [ ] **E1. Size column and summaries.** Folder sizes from the store in the Size column, drive/share summary card and preview
  pane; "≥" for partial, italic for stale, age shown on network folders.
- [ ] **E2. Triggers and deltas.** Lazy computation for visible/selected folders at background priority; deltas applied by every
  engine operation (copy, move, delete, restore); staleness from folder last-write time on visit; scheduled re-validation of
  opted-in shares.

### F. Split pane (§15.1)

- [ ] **F1. Split per tab.** Single / side by side / top and bottom buttons; each pane with its own path, history, layout and
  selection; address and command bars follow the focused pane; saved with tabs and profiles.
- [ ] **F2. Pane operations.** F5 copy / F6 move to the other pane through the queue; swap panes; "same folder in other pane".
- [ ] **F3. Preview follows focus.** Preview pane shows the focused pane's selection with Move (F6) and Copy (F5) under it.

### G. Verification and finish

- [ ] **G1. Test infrastructure.** `docker/compose.yml` with Samba and Toxiproxy; integration tests use it when Docker is
  available and skip with a reason otherwise.
- [ ] **G2. Chaos suite** (`Leviathan.Chaos.Tests`, nightly): kill the worker at random points during 1,000 copies → no corrupt
  or half-named files, every job resumes; Toxiproxy drops mid-copy → Waiting then resume; disk full; access denied mid-job.
- [ ] **G3. Benchmarks:** large-file copy throughput to the NAS (manual lab run), NAS folder first rows (< 400 ms for 10,000 items).
- [ ] **G5. Phase 1 carry-over:** make the six failing UI smoke tests pass or record why not, remove the first-show stalls
  (Background tab, second window) with `x:Load`/idle building, and continue cold start toward 600 ms (lighter `MainWindow`).
- [ ] **G4. Gate and wrap-up:** run the gate, write `docs/phase-2.md`, update `README.md`, tag `phase-2`, push, stop for review.

## Gate (from the build plan)

A 50 GB copy to the NAS survives 3 cable pulls and a killed UI with no corrupt files; sustained large-file copy ≥ 105 MB/s
on the 1 GbE link. The cable-pull test needs Brett at the machine: write a short procedure in `docs/phase-2.md` and ask him to run it.
