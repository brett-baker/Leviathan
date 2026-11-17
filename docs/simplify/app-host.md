# Simplify review: AppHost

Second candidate of [docs/simplify-review.md](../simplify-review.md). Report only: **no code has changed.** Stop here for Brett's pick.

## What it is

`src/Leviathan.App/AppHost.cs`, the composition root of the UI process (spec §2). It builds every shared service once at startup
(providers, stores, the worker and media connections, the settings, the cloud probes) and then keeps them as properties that the
windows read (`App.Host.X`). It is also where settings are applied to the running app, and it holds four small classes that are
not the host (`KeyTranslator`, `SearchHooks`, two lazy stores, `TrustPrompt`).

Flow: `App.xaml.cs` calls `AppHost.Create(ui)` → `Create` makes about 80 objects in order and passes some to the private constructor and the
rest through an object initializer (`private init` properties) → `StartApplyingSettings` hooks the settings file to the app → windows read
properties; `PlanStartupAsync` and `LoadBackgroundDataAsync` run later off the UI thread.

Pinned today by: the UI smoke tests only (`Leviathan.UI.Tests`, which start the real app). No unit test touches AppHost, because it is in the
WinUI project and its constructor needs the real services.

## Measures

| Measure | Before | Target |
| --- | --- | --- |
| Lines | 1,117 | largest file under about 350 |
| `Create` length | 344 lines (655 to 998) | under 60 |
| Properties on the class | 73 (45 `null!` placeholders set by the initializer) | 0 `null!` |
| References to the half-built host (`built`, `built!`) | 22 (11 `built!`) | 0 |
| Classes in the file | 6 (host, key translator, search hooks, 2 lazy stores, trust prompt) plus the UI context | 1 per file |
| `UiTrace` calls | 15 inside `Create` | kept (startup timing is a budget) |
| Pure logic with a unit test | 0 (all of it is in the WinUI project) | about 5 functions in a testable project |
| Branches per 100 lines | about 3 (29 `if`/`??`) | no change expected |

## Smells found

1. **A 344-line factory with a back door.** `Create` builds services in dependency order but many need the host that is not built yet, so they
   capture `built` (a local set to the host at the end) and write `built!.Settings`, `built!.MediaHost`. Eleven `built!` uses; if one runs
   before `built = host`, it throws a null reference. It works today only because those lambdas run later.
2. **A service locator with 73 properties.** 45 are `null!` and `private init`: a half-valid object by design. Windows reach in for what they
   need (`App.Host.Settings`, `.Transfers`, ...) so nothing says which part of the window needs which service.
3. **Groups that belong together are spread out.** The worker group (worker, transfers, archives, sizes, pending, undo, bin, operations, elevation),
   the remote group (secrets, provider, trust, connections, saved servers), the search group (hooks, index, space scanner), the settings group
   (settings, apply, key bindings, appearance, limits) and the background-data group (favourites, cloud) are interleaved in both `Create`
   and the property list.
4. **Settings application is app logic in the composition root.** `ApplySettings`, `ApplyAppearance`, `ApplyKeyBindings`, `ApplyLimitsAsync`
   and the View-menu write-back (about 140 lines) decide what each setting changes. The decisions (key signature, appearance key,
   accent-changed) have no test.
5. **Process-start logic duplicated.** `ShellHostLauncher` and `MediaHostLauncher` are the same function with different names (exe if present,
   else `dotnet name.dll`). The two `Lazy` + `IsStarted` + `Reenable` pairs and `ReenableProcess` repeat the shape a third time for the worker.
6. **Process status text in the host.** `ProcessEntryFor` maps a supervised state to the wording of the Background tab: a pure function with no test.
7. **Misplaced doc comments.** Two summaries stacked on one property (`TriageActionsFor`, `Inboxes`/`TriageJournal`): the first describes
   something else. Easy to fix, but a sign the properties were added by appending.
8. **Timers and background loops started inside `Create`.** The idle-connection and cloud-state loop, the sync timer, the bin purge, the size
   recheck and the search-index run are started from the factory, so building the host has side effects and cannot be done in a test.
9. **Unrelated classes share the file.** `KeyTranslator` (WinUI key to gesture), `SearchHooks`, `LazyNameIndexStore`, `LazySpaceScanStore` and
   `TrustPrompt` each have their own job.

## Proposed simplifications, ranked

| # | Change | Gain | Risk | Pin |
| --- | --- | --- | --- | --- |
| 1 | **Move the small classes out**: `KeyTranslator`, `SearchHooks`, the two lazy stores, `TrustPrompt`, `DispatcherQueueUiContext` each to its own file; fix the two stacked doc comments. | Size: 1,117 to about 960. Separation: one class per file. | Very low (a move) | Build; UI smoke tests |
| 2 | **One launcher function** for the two helper processes (`HelperProcess.Launcher("Leviathan.ShellHost")`), and move `ProcessEntryFor` into a pure `ProcessStatus` class in `Leviathan.ViewModels`. | DRY: 2 copies to 1. Pure function with tests (the wording of 4 states, disabled reason). | Low | Test table for the 4 states and the two launcher choices (exe present or not, with a fake `IFileSystem`) written first |
| 3 | **Move settings application out** to `SettingsApplier` (in `Leviathan.App`, taking the few objects it changes), with the key-binding and appearance decisions as pure functions (`KeyBindingSignature`, `AppearanceChange.Compare`) in `Leviathan.ViewModels` with tests. | Size: about 140 lines leave. Complexity: decisions testable without WinUI. Separation: the host stops knowing what each setting means. | Medium: the settings take effect live; order matters (icon pack before appearance) | Tests of the pure functions; hand-check Settings changes in the app |
| 4 | **Split `Create` by group** into `WorkerServices`, `RemoteServices`, `SearchServices`, `SettingsServices`, `StorageServices` (plain classes built by small factory methods), each file under about 150 lines. `Create` builds the groups and the host holds them (`Host.Worker.Transfers`). | Complexity: `Create` 344 to under 60. Separation: each group has one job and lists its own dependencies. | Medium-high: many callers read `App.Host.X` (every window file), so each property move touches them; the compiler finds each, but the diff is wide | Build with zero warnings; UI smoke tests; app start by hand |
| 5 | **Remove `built`**: services that need settings or the media host take a small `Func<AppSettings>` or `Lazy<MediaHostConnection>` made before the host, instead of reaching into the half-built host. | Safety: 11 `built!` to 0; `null!` placeholders drop. | Medium: startup ordering; a wrong lazy can deadlock or build twice | UI smoke tests; startup timing budget (cold start < 600 ms) |
| 6 | **Take the background loops out of `Create`** into `BackgroundSchedules.Start(host)`, called once by `App` after the host exists. | `Create` has no side effects; each loop is one named call | Low-medium: they must still start at the same point of startup | Startup timing; hand-check |

Suggested order: 1 → 2 → 3 → 6 → 4 → 5. Items 1, 2 and 6 are cheap and give most of the readability. Item 3 is the one with the clearest
new tests. Items 4 and 5 are the big structural ones and touch every window, so they should wait until the smaller ones have landed and
the app has been hand-checked.

### Expected gain if 1 to 3 and 6 are done (4 and 5 are optional)

| Measure | Before | After (estimate) |
| --- | --- | --- |
| Largest file | 1,117 lines | about 800 |
| `Create` | 344 lines | about 300 (only 4 and 5 shrink it much) |
| Classes in the file | 7 | 1 |
| Pure functions with tests | 0 | about 5 (process wording, launcher choice, key signature, appearance change, key-binding build) |
| Duplicated shapes | 3 | 1 |

If 4 and 5 are done as well: `Create` under 60, `null!` placeholders 45 to about 5, `built!` 11 to 0, largest file about 250.

## Leave alone

- **The `UiTrace` marks** in `Create`: they are the startup budget's measurements (spec §3.1).
- **`LoadBackgroundDataAsync` / `RefreshCloudStatesAsync`**: small, correct, use `Volatile` on purpose for cross-thread reads.
- **The comments that cite a spec section or a crash**: they say why, which is what they should do.
- **`AppHost` as the one place that knows every concrete service**: that is a composition root's job. The aim is to split it by group,
  not to hide it behind a container.

## Risks to say plainly

- No unit test can run `Create`, so items 3 to 6 rely on the UI smoke tests and a hand check. The UI tests have once deleted a real folder
  when run unattended (see memory), so they should only run with Brett present, and Leviathan has to be closed for the build to copy.
- Items 4 and 5 change what every window file writes (`App.Host.Settings` to `App.Host.Settings...`); they are mechanical but wide.

## Result (items 1 to 3, 2026-11-17)

- **Item 1.** `KeyTranslator`, `SearchHooks`, `LazyStores` (two classes), `TrustPrompt` and `DispatcherQueueUiContext` now have their own files; the two stacked doc comments are fixed. `AppHost.cs` 1,117 → 989 lines.
- **Item 2.** `HelperProcess` (Engine) holds the one rule for starting a helper (exe if present, else `dotnet name.dll`), used by the shell host, the media process and the worker, plus the Background tab's state wording. 7 tests (Engine tests 387 pass).
- **Item 3, partly.** The decisions moved out as pure functions with tests: `AppKeyBindings.SignatureOf` / `Build` and `AppearanceKey` (8 tests; ViewModels 2,190 pass). `ApplySettings` and its callers still live in `AppHost`: they set host properties and UI objects, so moving them needs the group split of item 5 first.
- **Not done:** the numbering of "item 6" in the chat message differed from this table (chat: remove `built`; table: take the background loops out of `Create`). Waiting for Brett to say which he meant.
- **Verified:** App project compiles with no errors or warnings; all tests above pass. The app was not started, and the UI tests were not run.

### Background loops (the report's item 6, 2026-11-17)

The six schedules (archive scratch sweep, idle-connection close and cloud refresh each minute, search index crawl, sync pair check each
minute, bin purge, size recount) moved from `AppHost.Create` to `BackgroundSchedules.Start`, which `App.OnLaunched` calls right after
the host is built. `Create` no longer starts anything; it is about 60 lines shorter (`AppHost.cs` now 917). The `built is not null` guard in the
minute loop is gone (the host exists by then). Two properties were opened to the new class: `Sizes` and `BinService`.
Verified: App compiles with no errors or warnings. **Not verified:** the app was not started, so the order of startup (the loops now begin a moment later,
after `Create` returns) and the minute timers were not watched running. Brett: start the app once and confirm the status hub's Background
tab still shows the search index and that a saved sync pair is still checked.
