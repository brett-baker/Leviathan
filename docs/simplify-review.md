# Simplify review — the reusable process

How to evaluate one feature of this app for complexity, and make it simpler and more readable **without changing what it
does**. Use it on any generated code: ours, a library's wrapper, a sample we ported. It is the process behind
[CLAUDE.md](../CLAUDE.md) "Simplify review". The first ranked list of candidates is at the end of this file.

The aim is not fewer lines. It is code a person can read top to bottom, where each decision lives in one place and has a
unit test.

## When to run it

- A feature has been fixed twice and still misbehaves (the tab drag was fixed three times before it was rewritten).
- A file passes about 600 lines, a method passes about 60, or a class has more than about 10 mutable fields.
- Before a phase starts to build on a feature, and after a phase ends (the stop point is a good time).
- A bug only shows in the real window, and the unit tests are green.

## The prompt

Paste this, fill in the feature, and send it.

```
Review one feature of this app for complexity, and simplify it without changing what it does.

Feature: <name, and the files or folders it lives in>

1. Inventory. List the files, classes and events that make up the feature. Draw, in a few lines, how a user action flows
   through them. Read the matching sections of docs/spec.md for the intended behaviour.
2. Measure. Fill the "Before" column of the table in docs/simplify-review.md (section "The report"): lines per file, longest
   methods, branch count per 100 lines, mutable fields, boolean flags, event handlers, and how much of the decision logic is
   reachable from a unit test.
3. Look for these smells, and quote the code for each one you find:
   - Decision logic inside UI event handlers instead of a pure function with unit tests.
   - State kept in several booleans or nullable fields instead of one explicit state (an enum or a small record).
   - The same decision made in more than one place.
   - Code that only exists to work around an earlier version of itself (flags, delayed dispatch, "put it back" fixes).
   - Comments that explain why something is odd rather than what it does.
   - Behaviour with no test, or tests that cover the model but not the code that drives it.
   - Names that need a comment, methods over about 40 lines, classes that do two jobs.
   - Copy-pasted blocks that differ in one or two values.
4. Write the report (section "The report"), with the expected gain of each proposed simplification, ranked by gain over risk.
   STOP and show me the report before changing any code.
5. For each simplification I approve: add a test that pins the current behaviour first, make the change, build with zero
   warnings, run the whole suite, run the app if the change touches UI or process behaviour, and commit it alone with a
   message that says what got simpler. Push after each commit.
6. Finish by filling the "After" column, saying what you left alone on purpose and why, and what still has no test.

Rules: no new features and no behaviour changes (if one is needed, stop and ask). Prefer deleting code to adding code.
Prefer plain methods and small records over new abstractions. Never leave the tests red between commits. Keep each commit
small enough to review in a few minutes.
```

## The smells, and what to do about each

| Smell | Why it hurts | Usual fix |
| --- | --- | --- |
| Decision in an event handler | It can only be tested by running the window | Move the decision into a pure function that takes plain values and returns a result; the handler calls it and applies the result (`TabDragPlanner`) |
| Several booleans that must be set in order | An implicit state machine nobody can read | One enum or record for the state, one method that moves between states |
| The same decision in two or three places | They drift apart; a fix lands in one | One function, called from each place; delete the copies |
| Workarounds for an earlier version | Each makes the next change harder | Remove the cause (usually the shape of the code), then the workaround |
| One class, two jobs (UI plus rules, or I/O plus parsing) | Cannot be tested or reused apart | Split by job: the rules into a class with no UI dependency |
| Very long method | Hard to see the steps; hard to test one step | Name each step as a method that returns a value; keep the long one as a list of steps |
| Copy-paste with small changes | A fix is made N times, or missed | A table or a loop over data (a list of records) instead of N blocks |
| Large partial class (`MainWindow.*`) | Everything can touch everything | Group by feature into a small class with its own state and an interface to the window |
| Wide `switch` or `if` chain on a kind | Every new kind edits it | A table from kind to behaviour, when the cases are alike; otherwise leave it, it is readable |

Not smells, leave them alone: a long method that is a flat list of independent steps (a composition root such as
`AppHost.Create`), a large table of data (command registrations), generated code, P/Invoke declarations.

## How to measure (no tools to install)

Visual Studio gives most of it: **Analyze → Calculate Code Metrics for Solution** shows cyclomatic complexity, maintainability
index, lines of code and class coupling per method, class and project. For a quick, repeatable count the review uses these
proxies, all of which can be scripted:

| Measure | How | Healthy | Look at it when |
| --- | --- | --- | --- |
| Lines per file | line count | under 400 | over 600 |
| Longest method | lines between its braces | under 40 | over 60 |
| Branch density | `if`, `else if`, `case`, loops, `catch`, `&&`, `\|\|`, `?:` per 100 lines | under 12 | over 20 |
| Cyclomatic complexity of one method | Visual Studio Code Metrics (or branches in it + 1) | under 10 | over 15 |
| Mutable fields per class | non-readonly fields | under 6 | over 10 |
| Boolean flags kept as fields | `private bool _x` | 0 to 2 | 4 or more |
| Event handlers per class | methods named `On...` | under 10 | over 20 |
| Pure share | portion of the feature's decisions reachable from a test that creates no window | most | the logic is only in `App` |

Branch count and complexity measure the same thing at different sizes: a method's complexity is about its branch count plus
one. For a file, the branch count is the sum over its methods.

## The report

One report per feature, saved as `docs/simplify/<feature>.md` (create the folder on first use), written **before** any code
changes and finished after them. Plain words, short.

```
# Simplify report: <feature>

Date, commit it was measured at.

## What it is
Two or three lines: what the user sees, which files make it up, the flow of one action through them.

## Measures
| Measure                               | Before | Target | After |
| ------------------------------------- | ------ | ------ | ----- |
| Files / total lines                   |        |        |       |
| Largest file (lines)                  |        |        |       |
| Longest method (lines)                |        |        |       |
| Branch density (per 100 lines)        |        |        |       |
| Highest cyclomatic complexity (method)|        |        |       |
| Mutable fields / boolean flags        |        |        |       |
| Event handlers                        |        |        |       |
| Decisions reachable by a unit test    |        |        |       |
| Tests covering the feature            |        |        |       |

## Smells found
Each one: name, the file and method, a short quote, the effect it has had (bugs, confusion).

## Proposed simplifications, ranked
For each:
| # | Change | Gain | Risk | Test that pins it |
Gain is written in the report's five terms, with a number where there is one:
  - Complexity: cyclomatic complexity or branch count removed, and from where.
  - Size: lines removed, or the file that gets smaller and by how much.
  - Pure functions: decisions that become unit-testable (name them).
  - Separation of concerns: which job moves out of which class.
  - DRY: copies that become one.
Risk: low (covered by tests today), medium (needs a new pinning test), high (only the real window shows it).

## Leave alone
What looks complex but is not worth touching, and why.

## Result (after the work)
The "After" column, the commits, what is still untested, and anything that surprised us.
```

## Stop points

1. After the report: Brett picks which items go ahead. Nothing changes before that.
2. After each commit: tests green, zero warnings, pushed. If a step turns out riskier than the report said, stop and say so.
3. At the end: the filled table, and one line on whether the next feature in the list is still the right one.

## Ranked candidates (first scan, 2026-11-16)

Method: file size, branch density, how much of the code sits in `Leviathan.App` (which unit tests cannot reach), how often the
file changed since September (a proxy for "keeps needing fixes"), and the smell checklist above. Numbers are lines in the file,
including blanks, and branch count per 100 lines. Re-run the scan when this list is acted on; do not trust it for long.

Project sizes: `Leviathan.App` 136 files, 24,900 lines; `Leviathan.ViewModels` 162 files, 24,500; `Leviathan.Engine` 6,600;
`Leviathan.Core` 5,400; the rest each under 3,200. `MainWindow` is 34 partial files with 7,750 lines, about 130 event
handlers, 51 mutable fields and 13 boolean flags in one class.

| Rank | Feature | Where | Why it ranks here |
| --- | --- | --- | --- |
| 1 | The main window's glue | `MainWindow.*` (34 files, 7,750 lines), `MainWindow.xaml.cs` (1,207 lines, 62 commits since September, longest method 117) | One class holds the state of tabs, preview, hub, network, columns, palette and inbox. Most logic is only reachable by running the window. Biggest gain in separation of concerns; do it a feature at a time (see below), not as one rewrite |
| 2 | The composition root | `AppHost.cs` (1,117 lines, 73 commits, `Create` is 344 lines) | Most-changed file in the project. It mixes wiring (fine as a flat list) with settings application, startup planning, cloud state refresh and hub layout saving (separate jobs). Split the jobs out and leave the wiring alone |
| 3 | The window commands | `WindowCommands.cs` (1,754 lines, 50 commits, 293 branches, 191 command references) | Pure view-model code, so it is already testable, which makes it the safest to improve. One file registers and runs every command. Group by area (navigation, tabs, file operations, view) with a shared base, and replace repeated can-execute / run pairs with data |
| 4 | The details grid and its rows | `DetailsGrid.xaml.cs` (1,144 lines, 213 branches, 31 handlers), `GridRow.cs` (533, 22.7 branches per 100), `FlowView` and `FlowCell` (749 and 535, branch density 21.6) | The file list is the heart of the app and has three views of the same items that duplicate selection, hit-testing, keyboard and drag logic. Same decision in several places: the strongest DRY gain |
| 5 | The preview pane | `MainWindow.Preview.cs` (755 lines, 16 mutable fields, longest method 110, 20 commits) | The densest state in the window: 16 mutable fields and 3 flags that choose between image, video, text and document previews. A good case for one explicit state and one pure "what to show for this file" function |
| 6 | The settings sheet | `SettingsSheet.xaml.cs` (1,094 lines, 51 handlers, longest method 107) | 51 handlers that each read a control and write one setting. A table of setting descriptions would replace most of it, and the same table could drive search in the sheet |
| 7 | The status hub | `StatusHubPanel.xaml.cs` (929 lines, 47 handlers, 19.6 branches per 100), `MainWindow.Hub.cs` | The most branch-heavy UI file after the grid. Operations, background tasks and activity are three jobs in one panel; `OperationsModel` already exists, so push the rest down into it |
| 8 | The sidebar tree | `SidebarTree.cs` (819 lines, 161 branches, longest method 87) | Pure view model, so safe. Drives, favourites, cloud, network and nearby are all built and refreshed by one class; one provider-per-group shape would shrink it |
| 9 | Triage mode | `TriageWindow.xaml.cs` (800 lines, 13 mutable fields), `TriageSession.cs` (602) | The session is already a model; the window repeats key handling and state that the model could own |
| 10 | The copy engine | `CopyMoveExecutor.cs` (469, longest method 154), `ChunkedFileCopier.cs` (`CopyAsync` 197 lines), `RemoteTransferExecutor.cs` (798) | Highest stakes (data safety, spec §12), so highest risk: only after better pinning tests (the chaos tests help). The gain is readability and testability of resume and verify steps |
| 11 | Path parsing | `LocationPath.cs` (648, 23.9 branches per 100, one method 105 lines) | Dense but pure and likely well tested, so it is cheap to simplify; low user-visible gain |
| 12 | The query parser | `QueryParser.cs` (519, 25.2 branches per 100) | Same: dense, pure, testable. A grammar table or a small tokenizer step would read better |
| 13 | The tab strip (remaining glue) | `MainWindow.TabStrip.cs` (571), `TabViewModel.cs` (821, 26 commits), `TabStripViewModel.Groups.cs` | Already improved by the drag rewrite; `TabViewModel` mixes history, view settings and selection. A small pass, not a project |

### Where to start

1. **`WindowCommands`** is the best first trial of the process: the code is pure, so every change is covered by existing tests, the
   file is the second most-changed one, and a good result shows the report's numbers moving.
2. **`AppHost`** next, because it changes the most and its jobs split cleanly.
3. **The details grid / flow view** for the largest DRY gain in what the user touches all day.
4. **`MainWindow`** last and in slices: preview first (most state), then hub, then network. Each slice moves one feature's state and
   decisions into a class with no WinUI dependency, with a test, and leaves the window a thin shell.

Not recommended now: the copy engine (risk over gain until the chaos tests pin it), generated and interop code, anything
under 300 lines.

### Expected total gain, if items 1 to 5 are done

Rough, to be replaced by measured numbers in each report: the four most-changed files lose about a third of their lines;
the highest method length in `AppHost` falls from 344 to under 60; `MainWindow` drops from about 130 handlers and 51 mutable
fields to a shell around a handful of small classes; and the share of the file list, preview and command logic that a unit
test can reach goes from partial to most of it, which is where the tab drag bugs came from.
