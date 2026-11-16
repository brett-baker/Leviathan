# Simplify report: window commands

Measured 2026-11-16 at commit 7085452. Nothing has been changed yet; this is the report that comes before the work
(see [simplify-review.md](../simplify-review.md)).

## What it is

Every user action is a command (spec, build plan Phase 1): tabs, navigation, view, selection, files, archives, edit,
tools. The buttons, menus, shortcuts and the command palette all run them through one `CommandRegistry`, so the action
trail sees everything. `WindowCommands.Create(...)` builds that registry for one window.

Flow of one action: key or menu item → `CommandRegistry.ExecuteAsync(id, context)` → the registered `run` lambda (checked first
by its `can` lambda) → the tab, strip, window manager or a service → a message through `IShellActions` when the UI must speak.

Everything lives in one file, [WindowCommands.cs](../../src/Leviathan.ViewModels/Commands/WindowCommands.cs):

| Part | Lines | What |
| --- | --- | --- |
| `IShellActions` | 19–98 (80) | what commands need from the UI layer |
| `CommandIds` | 100–543 (444) | one string constant (with a doc comment) per command |
| Parameter records | 545–584 (40) | `TabMove`, `TabTearOff`, `TabGroupAdd`, ... |
| `WindowCommands.Create` | 592–1650 (**1,059**) | all 124 registrations, plus the local helper functions |
| `DefaultKeyMap` | 1652–1732 (81) | the default shortcuts |

## Measures

| Measure | Before | Target | After |
| --- | --- | --- | --- |
| Files / total lines | 1 / 1,754 | 10–12 / about the same | 19 / 2,060 (logic 1,482 in 16 files; constants, interface and records 578 in 3) |
| Largest file (lines) | 1,754 | under 350 | 446 (`CommandIds.cs`, constants); largest logic file 180 (`EditCommands.cs`) |
| Longest method (lines) | 1,059 (`Create`) | under 60 | `Create` 48; longest area `Register` 164 (`EditCommands`), 140 (`TabCommands`), 123 (`FileCommands`) |
| Longest single registration | 54 (`RenameCommit`) | under 30 | 54 (unchanged; item 5 would address it) |
| Branch density (per 100 lines) | 16.7 (293 branches in the file, about 270 inside `Create`) | under 12 in each file | 3.8 to 25 per area file, 293 branches in total: the same decisions spread over more files (the count over-reads `?` in nullable types in `CommandArea`) |
| Cyclomatic complexity of `Create` | about 270 | under 10 (it only calls the area registrars) | about 8 (the seven argument checks; the rest is a straight list of calls) |
| Parameters of `Create` | 20 (12 optional services) | 7 plus one services record | 20 (item 6 not done) |
| Mutable fields / flags | 0 / 0 | 0 / 0 | 0 / 0 |
| Registrations | 124 in 14 categories (Tabs 34, View 30, Edit 11, Files 10, Navigation 9, Tools 7, ...) | same, same ids, titles, shortcuts | same: 151 commands in the snapshot, same ids, titles, shortcuts |
| Repeats: `Run(() => ...)` wrappers | 76 | 0 | 0 (one private `Run` in `CommandArea`) |
| Repeats: `Current() is not null` / `Current()!` | 31 / 42 | 0 / 0 | 0 / 1 (`Properties`), outside the helpers |
| Repeats: `(Type)c.Parameter!` casts | 15 | 0 | 2 (`FavoriteStyle`, `DropItems`: they also use the target) |
| Repeats: "location is a drive or share" test | 7 (3 slightly different) | 1 named function | 7 (item 5 not done) |
| Decisions a unit test can reach without the registry | few (the logic sits in lambdas) | the rules for rename, create, archive, colour and group parsing | unchanged (item 5 not done) |
| Tests covering the feature | `WindowCommandsTests` (30 tests, 602 lines) plus at least 10 other test files that run commands | same, plus a registry snapshot | same, plus `WindowCommandsSnapshotTests` (151 commands and 76 key bindings) |

## Smells found

1. **One method does everything.** `Create` is 1,059 lines and holds 124 registrations and the local helpers they share
   (`Hook`, `PasteAsync`, `RunCompress`, `RunExtract`, `ShowUndoOutcome`, `HasSelection`, `SendToOtherPane`, ...). It reads as a
   wall; a person looking for "how does Extract work" has to scroll through tabs, view and archives.
2. **Decision logic inside lambdas.** The rules for rename (`CanRenameHere`, `CanBulkRenameHere`), create (`CanCreateHere`), archives
   (`ArchiveTargets`, the "why not" messages), tab colour (`HexColor`), the group fold toggle, and the 54-line `RenameCommit` are
   closures over `Create`'s parameters. They cannot be called without building the whole registry.
3. **The same shape, repeated.** 76 `Run(() => ...)` wrappers exist only to turn a synchronous action into a `Task`. Five view
   layout commands differ only in the layout, title and shortcut. 31 `Current() is not null` guards and 42 `Current()!` calls
   are the same "there must be a tab" rule written twice, with the `!` hiding the link between the guard and the use.
4. **Casts that repeat the can-execute check.** 15 times a registration checks `c.Parameter is X` in `can` and then writes
   `(X)c.Parameter!` in `run`. If the two ever disagree, it throws.
5. **The same predicate in several spellings.** "Drive or share" (`Kind is Drive or Unc`) appears 7 times; rename widens it to include
   SFTP and FTP, create adds a remote check. A new location kind has to be found in seven places.
6. **Optional services handled one by one.** Twelve services are nullable; each command repeats `service is not null` in `can` and
   `service!` in `run` (about 20 and 18 uses). It works, but the reader cannot tell which commands exist only when a service does.
7. **A 20-parameter factory.** The one production call (`MainWindow.xaml.cs:62`) is a 400-character line; four test call sites repeat
   a shorter version. Adding a service means editing all of them.
8. **Unrelated types share the file.** `CommandIds` (444 lines of constants), the shell interface and the parameter records are
   not the logic, but they make the logic file 1,754 lines.

## Proposed simplifications, ranked

Gain is in the five terms of the process. "Pin" is the test that must exist and pass before the change.

| # | Change | Gain | Risk | Pin |
| --- | --- | --- | --- | --- |
| 1 | **Pin the registry.** A snapshot test lists every command id with title, category, icon and shortcut, and `DefaultKeyMap`. | Tests: catches a lost or renamed command during the moves below (today's test only checks the title is not empty) | None (adds a test) | It is the pin |
| 2 | **Move `CommandIds`, `IShellActions` and the parameter records into their own files** (`CommandIds.cs`, `IShellActions.cs`, `CommandParameters.cs`). Same namespace, no code change. | Size: `WindowCommands.cs` 1,754 → about 1,190 lines. Separation: constants and interface apart from logic | Very low (a move) | Existing suite and #1 |
| 3 | **Split `Create` by area** into `TabCommands`, `GroupCommands`, `NavigationCommands`, `ViewCommands`, `SelectionCommands`, `FileCommands` (open, delete, paste, send to), `ArchiveCommands`, `EditCommands` (rename, create), `ToolCommands`. Each is `internal static void Register(CommandScope scope)`. `Create` builds the scope (registry, strip, window, windows, shell, services, `Current()`, `TabOf()`, `Add()`) and calls each registrar. | Complexity: `Create` from about 270 to under 10; each file under about 40. Size: no file over about 350 lines. Separation: one area per file, helpers next to the commands that use them. DRY: the shared helpers live once on `CommandScope` | Low. Mechanical, ids and behaviour unchanged, #1 and the 40+ tests catch a slip | #1 |
| 4 | **Remove the repeated wrappers** with `Add` overloads: a synchronous one (`Action<CommandContext>`, removes 76 `Run(() =>`), one that hands the current tab to the action and requires it in `can` (removes 31 guards and 42 `!`), and a typed-parameter one (`AddWith<TabMove>(...)`, removes 15 casts and the paired `is` checks). Make the five view layouts a table. | Size: about 250 lines fewer. Complexity: the guards and casts leave the lambdas. DRY: three repeated shapes become three helpers. Safety: no `!` that depends on a different lambda | Low to medium: the guard moves from each `can` into one place; pin the enabled/disabled state with a tab and without one | #1, plus "no tab → disabled" per command kind |
| 5 | **Name the rules and make them pure**: `LocationKinds.IsLocalOrShare / CanRenameIn / CanCreateIn`, `ArchiveTargets`, `TabLabels.Clamp`, `TabColors.TryNormalize`, the group-fold toggle, and a plan for `RenameCommit` (text, go-next, outcome) as plain functions in `Leviathan.ViewModels` with unit tests. | Pure functions: about 8 decisions become testable without a registry; the 7 repeats of the drive-or-share test become 1 | Medium: the three spellings of the predicate differ; each must keep its exact set. Pin with one test per variant | A test table of kinds → allowed, per variant, written first |
| 6 | **Pass the services as one record** (`WindowServices`) instead of 12 optional parameters. | Separation: one place lists what a window needs. DRY: 5 call sites shrink to one line each | Low (signature change, compiler-checked) | Existing suite |
| 7 | **Say which commands need which service** (`Requires(archives)`) so the `is not null` / `!` pairs go. | Complexity: about 20 guards and 18 `!` removed | Medium: the registry's contents must not change when a service is missing, so the snapshot must also run with services absent | Snapshot with and without services |

Suggested order: 1 → 2 → 3 → 4 → 6 → 5 → 7. Items 1 to 3 are pure moves and give most of the readability. Item 4 gives most of the line
reduction. Items 5 and 7 are where behaviour could change, so they come last and each needs its table of cases first.

### Expected gain if 1 to 6 are done

| Measure | Before | After (estimate) |
| --- | --- | --- |
| Largest file | 1,754 lines | about 350 |
| Longest method | 1,059 lines | under 60 |
| Cyclomatic complexity of `Create` | about 270 | under 10 |
| Total lines | 1,754 | about 1,500 (the moves add headers; the repeats go) |
| Wrappers, guards, casts | 76 + 73 + 15 | 0 |
| `Create` parameters | 20 | 8 |

## Leave alone

- **`DefaultKeyMap`** (81 lines): a flat table of data, already readable.
- **`CommandIds` constants**: dull, but the right shape (compile-checked ids). It only moves.
- **The `#pragma warning disable CA2007`** and the comment on thread affinity: it records a real crash (signature `52a0ec04c34d`),
  and `UiThreadAffinityTests` guards it. Keep the comment with the code that needs it when it moves.
- **Command bodies that call services** (paste, delete, undo): the logic is in the services and is tested there; the lambda is the glue.
- **Splitting into more types than the registrars.** No command base classes or a command-per-class scheme: it would add 124 files
  for no gain over named functions.

## Result (items 1 to 4, 2026-11-16)

Commits: `7085452` is the process; the work is `f942906` (item 2), `f476891` (item 3), `05af543` (item 4), and the snapshot test
(item 1) just before them. Each commit built with zero warnings in `src/` and passed every non-UI test (ViewModels 2,165). The
snapshot of the registry (ids, titles, categories, icons, shortcuts, enabled with no context and with a folder target, and the
default key map) was generated before any change and did not change.

What got simpler:

- `WindowCommands.Create` is 48 lines and only builds a scope and calls thirteen area registrars. Nobody has to scroll through
  tabs to find archives.
- The 76 `Run(() => ...)` wrappers, 31 `Current() is not null` guards and 42 `Current()!` calls are gone from the command bodies. A
  command that needs a pane or a tab says so by the helper it uses (`AddOnCurrent`, `AddOnTab`), and the helper hands it the tab.
  The same helpers made the `can` expressions shorter (`(tab, _) => tab.History.CanGoBack`).
- Typed parameters (`AddWith<TabMove>`) replace 13 casts, each of which paired a check in `can` with a cast in `run`.
- The "judge with the drop rules, then queue the transfer" tail was copied in four commands; it is `TransferAsync` now (drops,
  quick targets, the other-pane keys). Paste keeps its own because it creates a folder between the two steps.
- The five layout commands are a table.

What did not get simpler, or got worse:

- **Total lines went up** (logic 1,176 before, 1,482 after): thirteen file headers, usings, doc comments, the scope, the base class
  and its helpers. The gain is per file and per method, not in total.
- **The area `Register` methods are still long** (164, 140 and 123 lines in `EditCommands`, `TabCommands` and `FileCommands`). They are
  flat lists of registrations plus the local helper functions those registrations share, which is the readable shape for this, but
  the helpers (`PasteAsync`, `DeleteSelection`, `RenameCommit`'s body) should become named methods of the class. That is item 5.
- **Branch density did not fall**: the same decisions are spread over more files. The density measure is not the right one here;
  the right ones are the method length of `Create` and how many places a rule lives in.
- Some `using` lines are probably now unused (the build does not flag them).
- The registry snapshot runs with no optional services, so commands that exist only for a service (archives, clipboard, favorites)
  are pinned by id and title but not by their enabled state. The existing tests for those cover it.
- The app was not started: the change is in view-model code behind an unchanged `WindowCommands.Create` signature, but nothing was
  clicked by hand.

Still open from the report: item 5 (pure functions for the drive-or-share predicates, archive targets, colour, `RenameCommit`),
item 6 (a services record for the 20 parameters) and item 7 (`Requires(service)`).