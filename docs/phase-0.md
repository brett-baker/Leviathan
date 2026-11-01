# Phase 0 — Foundations

Phase 0 builds the parts every later phase stands on: the solution layout, the location-provider model, IPC between the UI
and the worker, the job engine with its journal, diagnostics and crash reports, and the test harness. The UI is a shell
whose only job is to prove the pieces work together end to end.

## What is in it

| Area | Delivered |
| --- | --- |
| Solution | `Leviathan.slnx`, central package versions, deterministic builds with SourceLink and embedded PDBs, warnings as errors in `src/`, CI on Windows |
| Locations | `LocationPath`: drive, UNC (incl. `\\?\` and `\\?\UNC\`), `sftp://`, `ftp(s)://`, `nfs://`, `shell:`, env vars, relative input, breadcrumbs, long-path conversion; natural sort |
| Providers | Streaming `ILocationProvider` (batches, flushed after 50 ms); local provider with .NET's NtQueryDirectoryFile enumerator for local disks and Win32 `FindFirstFileExW` + large fetch for SMB; a dead share fails in 5 s as Unreachable instead of hanging |
| Resilience | Error classification (transient vs permanent, Win32 names), share health tracker (Online / Slow / Unreachable / Auth), back-off with jitter |
| Engine | Scheduler (global + per-device limits, background lane that yields to operations on the same NAS, priorities, manual order, Run now, 8-color palette); job host (pause, resume, cancel, Waiting + automatic retry on network drops); JSON-lines journal with crash-safe replay; speed meter with a 2-minute sparkline history; restart policy and process supervisor (3 crashes in 5 min → disabled); folder-size service (bottom-up, stores every subfolder, partial lower bounds, delta propagation, never follows links) |
| Data | SQLite in WAL mode with ordered migrations; folder-size store; tag and media-metadata tables ready for Phase 3 |
| IPC | StreamJsonRpc over named pipes created with `CurrentUserOnly`; per-user pipe name; contract version check; several UI clients at once |
| Worker | Generic-host process: single instance per user, job host, journal, folder-size jobs, pipe server, client fan-out, idle shutdown after 2 min with no clients and no jobs, OTLP traces when `OTEL_EXPORTER_OTLP_ENDPOINT` is set |
| Diagnostics | Flight recorder (last 2,000 events), action trail (last 50 commands), JSON-lines logs (7 days), crash bundles with `report.md` written for Claude, crash handlers, UI-stall watchdog (50 ms stalls, 5 s hangs) |
| Tools | `leviathan-diag last | list | show <id> | replay <id> | path` |
| App | WinUI 3 shell: starts and supervises the worker, reconnects after a worker crash, submits a folder-size job and shows live progress, watchdog running |
| Tests | xUnit v3 suites for Core, Engine, Diagnostics, Providers.Local, Data, Integration; shared fakes: `FakeLocationProvider`, `FaultInjectingLocationProvider` (network drop, access denied, latency, hang), `ManualTimeProvider`; BenchmarkDotNet suite plus a quick `--gate` mode |

## Gate

> A 10,000-item local folder shows its first rows in under 100 ms (p95); no I/O on the UI thread.

Run it on Windows:

```powershell
dotnet run -c Release --project tests/Leviathan.Benchmarks -p:Platform=x64 -- --gate
```

Indicative result from the build machine (Linux container, same managed enumerator, not NTFS):
**first rows p95 5.7 ms, full listing p95 60 ms — PASS.** The Windows number is the one that counts; CI runs the gate nightly.

"No I/O on the UI thread" holds by construction in Phase 0: the shell's only I/O is through the worker over the pipe, and
the watchdog reports any stall over 50 ms.

## How it was verified

The code was written and verified in a Linux container without access to nuget.org, so verification was split:

| What | How | Result |
| --- | --- | --- |
| Core, Contracts, Engine, Diagnostics, Providers.Local, Diag tool | Built with the real .NET 10 SDK and analyzers (warnings as errors) | Clean |
| Core, Engine, Diagnostics, Providers.Local tests | Run with a local xUnit-compatible runner (same `Assert` API) | 159 passed, 1 skipped (Windows-only), 0 failed, stable over repeated runs |
| Data, Ipc, Worker, Data/Integration tests | Compiled against API-compatible stand-ins for Microsoft.Data.Sqlite, StreamJsonRpc and OpenTelemetry | Compile clean; **not run** |
| Leviathan.App (WinUI 3) | Cannot build off Windows | **Not built** — first build is yours in Visual Studio |
| Win32 large-fetch lister | Compiles; its test runs on Windows only | **Not run** |
| Crash bundles end to end | Generated real bundles and read them with `leviathan-diag` | Works; found and fixed a bad-permalink case |

The tests caught three real bugs during the build, all fixed with regression tests: an HRESULT mask that never matched
(so network errors were classified as Unknown), a cancelled listing that could look like a complete empty folder, and
permalinks generated for files outside the repository.

## Phase 0b: verified on Windows (2026-11-01)

| What | Result |
| --- | --- |
| Restore and build, `-p:Platform=x64` | Clean, 0 warnings in `src/` (Leviathan.App builds) |
| Tests | Core 84, Engine 48, Diagnostics 20, Data 5, Integration 3, Providers.Local 9: all pass, none skipped |
| Enumeration gate (NTFS, Release) | 10,000 items: first rows p95 **3.1 ms** (budget 100 ms), full listing p95 18.7 ms: PASS |
| Run the app, kill the worker mid-job | **Not yet checked by hand** |

Found and fixed on Windows:

- `Win32FindData` used default alignment, so the 8-byte timestamps shifted every field and real listings threw "Not a valid
  Win32 FileTime". It now packs at 4 like `WIN32_FIND_DATAW`; `Win32FindData_Layout_MatchesWin32FindDataW` guards it.
- NuGet audit failed the build: StreamJsonRpc 2.20.20 -> 2.26.150 (removes the vulnerable MessagePack) and OpenTelemetry
  1.12.0 -> 1.20.1.
- Two analyzer errors in Leviathan.App (CA1001, CA1859).
- `Leviathan.slnx` maps every project's build type and platform explicitly, for Visual Studio's "configuration does not exist" load error.

## What to check first on Windows

1. **Restore.** Package versions were chosen from known releases but could not be checked against nuget.org. If restore
   fails, bump the version in `Directory.Packages.props`.
2. **Build `Leviathan.App`.** If the XAML compiler complains, the error will point at `MainWindow.xaml`.
3. **Run the tests** (Test Explorer). Data and Integration tests run for the first time here.
4. **F5 the app**, enter a folder, click **Compute folder size**. Kill `Leviathan.Worker.exe` in Task Manager while a big
   folder is counting: the status line should show the restart and the job should come back from the journal.
5. **Run the gate** (above).

## Deviations from the spec, on purpose

| Spec | Phase 0 | Why |
| --- | --- | --- |
| MessagePack on the pipe | System.Text.Json | No file data crosses IPC; JSON is easier to debug. Formatter swap is one line in `RpcChannel`. |
| Journal in SQLite | JSON-lines file | Simpler and crash-safe for append-only records; can move to SQLite with the per-file copy journal in Phase 2. |
| CsWin32 everywhere | `[LibraryImport]` for the three enumeration calls | Tiny surface; CsWin32 arrives with the shell/COM layer in Phase 1. |
| FsCheck property tests | Seeded randomized invariant test for the scheduler | Same intent without another dependency; FsCheck can be added when properties grow. |
| Shouldly | Plain xUnit `Assert` | Fewer moving parts for now. |

## Next: Phase 1 — core browser

Window shell with tabs, address bar and breadcrumbs, navigation, sidebar (Favorites, Common folders, Local with drive
summaries), Details/List/Thumbnails layouts streaming from the provider, clipboard operations, Recycle Bin delete,
right-click menu with third-party shell items (ShellHost), Last-session restore, and the command layer that the action
trail and replay hook into.
