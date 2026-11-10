# Leviathan

A Windows 11 file manager built for moving and triaging large media collections on a NAS. WinUI 3 / .NET 10, multi-process, with performance, stability and resiliency ahead of feature count.

The full specification lives in the **Leviathan File Manager — Software Specification** doc; layouts are in the **Leviathan UI mockups** canvas.

## Status

**Phase 3 — Media and triage** is built and waiting for review ([docs/phase-3.md](docs/phase-3.md): what is in it, how it was verified, the procedure for the 100-video NAS gate that needs a person at the machine, and the open items: the FlaUI triage flow never run, UI added late in the phase not yet seen on screen, UI stalls while a video plays). Phase 2 is in [docs/phase-2.md](docs/phase-2.md), Phase 1 in [docs/phase-1.md](docs/phase-1.md) and Phase 0 in [docs/phase-0.md](docs/phase-0.md).

| Phase | Scope | Gate |
| --- | --- | --- |
| 0 Foundations | Solution, interop, provider model, IPC, journal, diagnostics, test harness | 10,000-item folder: first rows < 100 ms (p95) |
| 1 Core browser | Window, tabs, address bar, tree, file views, clipboard, Recycle Bin | One week replacing Explorer for local work |
| 2 NAS resiliency + operations | Share health, journaled copy/move, queue UI, soft delete, split pane | 50 GB NAS copy survives 3 cable pulls and a killed UI |
| **3 Media + triage** | ffprobe, previews, libmpv/LibVLC, triage mode, tags | 100 NAS videos at < 10 s each |
| 4 Search, space map, protocols | Everything/USN search, NAS index, space map, SFTP/FTP/NFS | Local search < 50 ms |
| 5 Power features | Duplicates, bulk rename, archives, compare/sync, palette | Feature-complete after a month of use |

## Getting started (Visual Studio)

1. Install **Visual Studio 2026** (version 18.x) with the **.NET desktop development** and **WinUI application development** workloads. It includes the **.NET 10 SDK**, which this solution needs (Visual Studio 2022 cannot load .NET 10 projects). Check with `dotnet --list-sdks`: a `10.0.1xx` line must be there.
2. Open `Leviathan.slnx`. Choose the **x64** platform.
3. Set **Leviathan.App** as the startup project and press F5. The app starts `Leviathan.Worker` next to it and connects over a named pipe.
4. In the window, type a folder (e.g. `C:\Users` or `\\Diskstation\media`) and click **Compute folder size**: the job runs in the worker and its progress streams back.
5. **Test → Run All Tests** runs the xUnit suites.

From a terminal:

```powershell
dotnet build Leviathan.slnx -p:Platform=x64
dotnet test Leviathan.slnx -p:Platform=x64
dotnet run -c Release --project tests/Leviathan.Benchmarks -- --gate   # listing, view-model and new-tab budgets
dotnet run -c Release --project tests/Leviathan.Benchmarks -- --gate --cold-start <path to Leviathan.exe>   # adds the real app's cold start (opens windows)
dotnet run --project tools/Leviathan.Diag -- last                      # latest crash report
```

## Layout

| Path | What it is |
| --- | --- |
| `src/Leviathan.App` | WinUI 3 UI process (Phase 0: a shell proving the worker connection) |
| `src/Leviathan.Worker` | File-operations worker: job queue, journal, folder sizes, pipe server |
| `src/Leviathan.Core` | Locations, natural sort, provider model, error classification, share health |
| `src/Leviathan.Contracts` | IPC contracts shared by UI and workers |
| `src/Leviathan.Engine` | Scheduler, job host, journal, supervisor, folder-size service |
| `src/Leviathan.Diagnostics` | Flight recorder, JSON logs, crash bundles, UI-stall watchdog |
| `src/Leviathan.Providers.Local` | Local disks and SMB shares (Win32 large-fetch enumeration) |
| `src/Leviathan.Data` | SQLite (WAL) stores and migrations |
| `src/Leviathan.Ipc` | JSON-RPC over current-user-only named pipes |
| `tools/Leviathan.Diag` | `leviathan-diag` crash-report CLI |
| `tests/*` | xUnit v3 suites, shared fakes (`Leviathan.Testing`), benchmarks |

## Data locations

Everything lives under `%LOCALAPPDATA%\Leviathan`: `leviathan.db` (SQLite), `jobs.journal`, `logs\*.jsonl`, `crashes\<id>\`.
