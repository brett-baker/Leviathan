# CLAUDE.md — working on Leviathan

Instructions for Claude (and anyone else) changing this repository.

## Read first

- `docs/spec.md` — the full specification (decisions log in section 1, roadmap and gates in section 21).
- `docs/phase-0.md` — what Phase 0 delivered, how it was verified, deviations from the spec.
- UI mockups: https://claude.ai/artifact/TBbfv46mBRQszt7giterdm (main window, split view with preview, triage mode,
  operations panel, drive summary, space map).
- Current phase: **Phase 1 — core browser** (spec section 21). Work phase by phase; commit as you go and push to
  `origin main` at the end of each phase and after each tweak.

## Build and test

```powershell
dotnet build Leviathan.slnx -p:Platform=x64
dotnet test Leviathan.slnx -p:Platform=x64
dotnet test tests/Leviathan.Engine.Tests -p:Platform=x64 --filter "FullyQualifiedName~JobScheduler"
```

`Leviathan.App` (WinUI 3) builds on Windows only. Everything else targets plain `net10.0`.

## Diagnosing a crash

Crash bundles are in `%LOCALAPPDATA%\Leviathan\crashes\<id>\`. Start with `report.md`; it has the exception chain, a stack with
repository paths and permalinks at the exact commit, the last user actions, surrounding log lines and starting-point files.

```powershell
dotnet run --project tools/Leviathan.Diag -- last        # latest report.md
dotnet run --project tools/Leviathan.Diag -- list        # grouped by signature
dotnet run --project tools/Leviathan.Diag -- show <id>
```

Every fixed bug gets a regression test, named after the crash signature when there is one.

## Architecture rules

- **Never block the UI thread.** No file, shell, network or COM call on it. Everything takes a `CancellationToken`.
- **Providers stream.** `ILocationProvider.ListAsync` yields batches; never materialize a whole folder before showing it.
- **Classify failures.** Wrap I/O errors with `LocationErrors.Wrap` so callers can tell transient (retry) from permanent.
  A cancelled listing must never look like an empty folder.
- **Workers own long work.** Copy, move, delete and folder sizes run in `Leviathan.Worker`, journaled, so a UI crash loses nothing.
- **Contracts are versioned.** Breaking change to `Leviathan.Contracts` → bump `ContractVersion.Major`.
- **No hand-written P/Invoke for large surfaces.** Small, blittable calls use `[LibraryImport]`; the shell/COM layer uses CsWin32.
- **System.IO goes through `IFileSystem`** (System.IO.Abstractions) where it is not performance-critical, so it can be unit-tested.
  Win32-specific paths sit behind small Leviathan interfaces with in-memory fakes (`tests/Leviathan.Testing`).
- **Time goes through `TimeProvider`**; tests use `ManualTimeProvider`.

## Conventions

- C# latest, nullable on, warnings as errors in `src/`. File-scoped namespaces, `_camelCase` private fields.
- Tests: xUnit v3, `Method_State_Expectation` names, plain `Assert`. Shared fakes in `Leviathan.Testing`:
  `FakeLocationProvider`, `FaultInjectingLocationProvider` (network drop, access denied, latency, hang), `ManualTimeProvider`.
- Package versions live only in `Directory.Packages.props`.
- Commit messages: imperative summary line, then what changed and why.
