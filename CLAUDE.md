# CLAUDE.md — building Leviathan

Instructions for Claude Code (and anyone else) working in this repository. Read this whole file before starting work.

## Read first

- `docs/spec.md` — the specification and source of truth: decisions log (section 1), architecture (2),
  performance/stability/resiliency budgets (3), every feature (4–17), reuse plan (18), testing (19), diagnostics (20),
  roadmap and gates (21).
- `docs/build-plan.md` — the phase-by-phase plan: scope, spec sections, code to reuse, tests and the gate for each phase.
- `docs/phase-0.md` — what Phase 0 delivered, what is still unverified on Windows, and deliberate deviations.
- UI mockups: https://claude.ai/artifact/TBbfv46mBRQszt7giterdm (main window, split view with preview, triage mode,
  operations panel, drive summary, space map). If you cannot open the link, follow the spec text; ask Brett for screenshots
  when a layout detail matters.
- `docs/decisions.md` — decisions made while building (create it on first use; see "Decisions" below).

## The job

Build Leviathan to the spec, **one phase at a time**, in the order of `docs/build-plan.md`. Each phase ends with a
working, tested, committed and pushed build, and a short review stop with Brett before the next phase starts.

The owner is Brett, a .NET developer who follows along in Visual Studio. He wants minimal interruptions while a phase
is in progress and a clear summary at the end of it.

## How to work a phase

1. **Plan.** Read the phase's spec sections and its entry in `docs/build-plan.md`. Write `docs/phase-N-plan.md`: a
   checklist of slices, each small enough to finish, test and commit in one sitting, in dependency order.
   Commit the plan.
2. **Build in slices.** For each slice:
   - Write or port the code (see "Reuse before writing").
   - Write its unit tests in the same slice, and integration tests where it touches the file system, the network,
     another process or the shell.
   - Build with zero warnings, run the **whole** test suite, and run the app when the slice touches UI or process behaviour.
   - Commit (see "Commits and pushing"), tick the slice in the plan.
   - **End the slice by asking Brett what next** (see "Slice-end choices and UAT playbooks"): continue, generate a
     playbook, or tweak something he found. Do not start the next slice until he answers.
3. **Finish the phase.**
   - Run the phase gate from `docs/build-plan.md` and record the numbers.
   - Write `docs/phase-N.md` in the same shape as `docs/phase-0.md`: what was delivered, how it was verified (with
     test counts and gate results), what is still unverified, and any deviations from the spec with reasons.
   - Update `README.md` status, tag `phase-N`, push the branch and the tag.
   - **Stop** and give Brett a summary: what works, how to try it, what to look at, known gaps. Wait for his go-ahead
     before starting the next phase. Tweaks he asks for after a phase follow the same slice → test → commit → push loop.

If a gate fails, fix it before finishing the phase. If it cannot be met, say so plainly in `docs/phase-N.md` and in the
summary, with the measured number and what would close the gap.

## Slice-end choices and UAT playbooks

Passing tests show a slice is technically correct; a playbook shows Brett whether it is **fit for purpose**. When a slice is
committed and pushed, close the turn with these choices (the `wrap_up` question and buttons):

1. **Continue to the next slice** (name it).
2. **Generate a playbook** for the slice just finished.
3. **Tweak something I found** (Brett describes it; it follows the slice → test → commit → push loop).

Offer the playbook for every slice whose result a person can see or use. For a slice with nothing to click or run (pure
engine, contracts, refactors), say so and offer continue first. If Brett picks the playbook, write it, commit and push it, then
ask again.

**Playbook rules** (`docs/uat/<slice-id>-<short-name>.md`; start from `docs/uat/TEMPLATE.md`):

- Plain and short: a goal line, setup, numbered steps each with the **expected result**, then **Known edge cases** (what is
  deliberately not handled, what looks odd but is right, what is unverified).
- **Never real data.** Steps use the folders built by `tools/New-UatFixtures.ps1` (`D:\Playbooks\uat\…`:
  odd names, long paths, hidden/read-only, a 6 GiB sparse file, conflicts, 10,000 items, bulk copy, empty targets). If a slice
  needs a file kind the fixtures lack, extend the script in the same commit.
- **Network steps** use `-Share` (an SMB share `\\localhost\LeviathanUat` on this machine) or are marked `[lab]` and aimed at a
  dedicated test share Brett names or the G1 containers. Never Brett's real shares (Y:, Z:, `\\192.168.1.10`).
- Say which steps need the app running with `LEVIATHAN_DATA_DIR` pointed at a scratch folder, so UAT does not touch his real
  settings, favourites or saved servers.
- Record in the playbook what Brett should look at that tests cannot judge: layout, wording, colour, feel, speed.
- A slice's playbook is retroactive-friendly: if an earlier slice has none, it can be written later on request.

## Definition of done (every commit)

- `dotnet build Leviathan.slnx -c Debug -p:Platform=x64` succeeds with **zero warnings** in `src/`.
- `dotnet test Leviathan.slnx -p:Platform=x64` passes. No test is skipped to get green, except tests that genuinely need
  something absent (e.g. a NAS), which use `Assert.Skip*` with the reason.
- New behaviour has tests. Bug fixes have a regression test that fails before the fix.
- The app starts and the changed feature works when exercised by hand (say what you checked in the commit message
  when it matters).
- No I/O, shell, COM or network call on the UI thread (spec §3.2).
- Spec section referenced in the code (doc comment) or commit message where it explains *why*.

## Commits and pushing

- Commit after each meaningful, green change: a finished slice, a working fix, a tweak Brett asked for. Never commit a
  broken build or failing tests.
- **Push to `origin main` after every commit** so Brett can pull in Visual Studio. Tag the end of each phase
  (`phase-1`, `phase-2`, …) and push the tag.
- Message format: imperative summary line (≤ 72 chars), blank line, then what changed and why, and how it was verified.
- Never force-push, never rewrite pushed history, never commit secrets, credentials, `bin/`, `obj/` or user data.
- Keep commits focused: one slice or one fix each. Formatting-only changes go in their own commit.

## Robustness, stability, performance — the non-negotiables

These come from spec §3 and are the reason this app exists. They outrank feature count.

- **The UI never freezes.** UI thread touches view models and controls only. All I/O is async, cancellable, and has a
  timeout. Results reach the UI in batches every 16–50 ms, never per item. Lists are virtualized and recycle containers.
  The `UiStallWatchdog` runs in every UI build; stalls over 50 ms are bugs.
- **Budgets are tested.** Keep the §3.1 budgets (cold start < 600 ms to first folder, first rows of a 10,000-item local
  folder < 100 ms, NAS first rows < 400 ms, new tab < 150 ms, idle memory with 10 tabs < 250 MB, NAS copy ≥ 105 MB/s on the
  current 1 GbE link) as benchmarks in `tests/Leviathan.Benchmarks`, and add each new budget when its feature lands.
- **Network failure degrades one tab, not the app.** Dead shares fail fast with a clear message (spec §3.3), retry with
  back-off, and never block other tabs, the tree or the status bar. Use `ShareHealthTracker` and `LocationErrors`.
- **Nothing is ever lost.** Long operations run in `Leviathan.Worker`, journaled, resumable after a crash or reboot.
  Data is written to `*.leviathan-part` and renamed when complete. A cross-volume move deletes the source only after the copy
  is verified (§12.1). Deletes on shares are soft deletes (`#recycle` when present, §12.5). Every destructive action is undoable.
- **Crashy things live out of process.** Shell extensions and preview handlers run in `Leviathan.ShellHost`; ffprobe,
  thumbnails and contact sheets in `Leviathan.Media`. A crash there restarts that process (via `ProcessSupervisor`) and
  quarantines the bad file; it never takes down the UI.
- **Every failure is diagnosable.** Log through `ILogger` (flight recorder + JSON logs). Unhandled exceptions, hangs and
  native crashes produce a crash bundle (§20). Record user commands in `ActionTrail`. When you fix a crash, read its
  `report.md` first and name the regression test after its signature.
- **Long paths, odd names, huge folders, 100 GB files** are normal inputs, not edge cases. Test them.

## Reuse before writing

Prefer proven code over new code. In order:

1. **Brett's fork of Files** (`https://github.com/brett-baker/Files`, dual MIT / MPL-2.0). Spec §18 lists what to port:
   CsWin32 setup and COM helpers, shell context menus (move into ShellHost), preview handlers, cloud-drive detection,
   Win32 enumerators, Open With / launch / file associations, FTP storage, Git integration, 7-Zip approach. Port the logic,
   adapt it to Leviathan's interfaces, keep it testable. **Check each file's license header**; record every ported file in
   `THIRD-PARTY-NOTICES.md` (original path, license). MPL-2.0 files keep MPL-2.0 for their contents.
2. **Well-maintained libraries** already chosen in the spec (§2.4): CommunityToolkit (MVVM, WinUI controls), CsWin32,
   Microsoft.Data.Sqlite, StreamJsonRpc, SSH.NET, FluentFTP, 7-Zip via SevenZipExtractor, libmpv and LibVLCSharp, ffprobe (GPL build),
   System.IO.Abstractions, the Everything SDK, OpenTelemetry. Before adding any other package: check it is actively maintained,
   widely used, has a compatible license, and works with .NET 10 / trimming. Pin its version in `Directory.Packages.props`
   and add it to `THIRD-PARTY-NOTICES.md`.
3. **Microsoft samples and docs** for Windows APIs (WinUI Gallery, Windows App SDK samples) when neither of the above covers it.
4. **New code** only when nothing proven fits. Keep it small and covered by tests.

Wrap every third-party dependency and every Win32/COM surface behind a small Leviathan interface with an in-memory fake in
`tests/Leviathan.Testing`, so the logic around it can be unit-tested and the dependency can be swapped.

## Testing rules (spec §19)

- **xUnit v3**, plain `Assert`, `Method_State_Expectation` names. One test project per production project; shared fakes in
  `tests/Leviathan.Testing` (`FakeLocationProvider`, `FaultInjectingLocationProvider`, `ManualTimeProvider` — extend these
  rather than writing one-off fakes).
- **Unit tests** never touch the real disk, network or clock: use `IFileSystem` + `MockFileSystem`, the fakes, and
  `ManualTimeProvider`.
- **Integration tests** use real temp folders, real processes and real pipes; containers (Samba, SFTP, NFS) and Toxiproxy for
  network faults once those providers exist. Never touch Brett's real shares; a manual "lab" run may target a dedicated test share.
- **Chaos tests** (nightly): kill workers mid-operation, drop the network mid-copy, corrupt media files; assert no corruption,
  no half-named files, correct resume.
- **UI smoke tests** with FlaUI from Phase 1: launch, tabs, navigate, tear off, copy/paste, triage keys; the stall watchdog
  fails the test on any stall over 50 ms.
- **Coverage**: Core, Engine, Data and Worker logic ≥ 85% lines; every job state transition and journal recovery path covered.
- Tests must be deterministic. No sleeps to "wait for" things except bounded polling helpers with a timeout.

## Architecture rules

- **Providers stream.** `ILocationProvider.ListAsync` yields batches; never materialize a whole folder before showing it.
- **Classify failures** with `LocationErrors.Wrap` so callers can tell transient (retry) from permanent. A cancelled listing
  must never look like an empty folder.
- **Contracts are versioned.** Breaking change to `Leviathan.Contracts` → bump `ContractVersion.Major`.
- **P/Invoke:** small, blittable calls use `[LibraryImport]`; the shell/COM layer uses CsWin32.
- **System.IO goes through `IFileSystem`** where it is not performance-critical. Hot paths (enumeration, copy) sit behind
  Leviathan interfaces (`IDirectoryLister`, `IFileCopier`, …) with fakes.
- **Time goes through `TimeProvider`.**
- **MVVM** with CommunityToolkit.Mvvm in the UI; view models have no WinUI dependencies so they can be unit-tested.
- **Settings** are JSON, written atomically (temp file → flush → rename), applied without restart (§17).

## Decisions

When the spec is silent or ambiguous, decide the way that best serves robustness and Brett's NAS workflow, write the decision
and the reason as one line in `docs/decisions.md`, and carry on. Ask Brett only for decisions that are expensive to reverse
or that change what he will see or do (a new dependency with licensing implications, a UI behaviour the spec contradicts,
anything destructive to data). Batch questions into the end-of-phase summary when they can wait.

## Build and test commands

```powershell
dotnet build Leviathan.slnx -p:Platform=x64
dotnet test Leviathan.slnx -p:Platform=x64
dotnet test tests/Leviathan.Engine.Tests -p:Platform=x64 --filter "FullyQualifiedName~JobScheduler"
dotnet run -c Release --project tests/Leviathan.Benchmarks -p:Platform=x64 -- --gate
```

`Leviathan.App` (WinUI 3) builds on Windows only, with Visual Studio 2026 / .NET 10 SDK.

## Diagnosing a crash

Crash bundles are in `%LOCALAPPDATA%\Leviathan\crashes\<id>\`. Start with `report.md`: exception chain, stack with repository
paths and permalinks at the exact commit, last user actions, surrounding log lines, starting-point files.

```powershell
dotnet run --project tools/Leviathan.Diag -- last        # latest report.md
dotnet run --project tools/Leviathan.Diag -- list        # grouped by signature
dotnet run --project tools/Leviathan.Diag -- show <id>
```

## Conventions

- C# latest, nullable on, warnings as errors in `src/`. File-scoped namespaces, `_camelCase` private fields.
- Package versions live only in `Directory.Packages.props`.
- Public APIs get XML doc comments that say *why*, citing the spec section.
