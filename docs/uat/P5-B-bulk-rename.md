# UAT: P5-B Bulk rename

**Goal:** rename many files at once with a stack of steps, see every new name before anything changes, and undo the whole batch in one go.
**Built in:** Phase 5 slices B1, B2 (`docs/phase-5.md`), spec §15.3.
**Time:** about 35 minutes.

## Setup

1. Fixtures: `.\tools\New-UatFixtures.ps1` (folder `16-rename`: `IMG_0001.jpg` to `IMG_0006.jpg` with different dates, two `Summer Trip 2025 - photo` files, `Mixed Case Name.Md`, `notes.TXT`, `draft 1.txt`, `draft 2.txt`, `take-1.txt`, `take-2.txt`, a file with spaces around its name, and a folder). For the media tokens also use `09-media`.
2. Scratch settings and start:
   ```powershell
   $env:LEVIATHAN_DATA_DIR = "D:\Playbooks\data"
   & .\src\Leviathan.App\bin\x64\Debug\net10.0-windows10.0.26100.0\Leviathan.exe
   ```
3. Open `D:\Playbooks\uat\16-rename`.

## Steps

### Opening and the preview

| # | Do | Expect |
|---|----|--------|
| 1 | Select the six `IMG_*.jpg` files and press **Shift+F2** (or run **Bulk rename…** from the palette or the right-click menu). | A window **Rename 6 items** opens. The steps area is empty with the hint "Add a step to start. The preview shows each new name before anything is renamed." The table below lists the six names unchanged. |
| 2 | Press **Add step**. | A menu: **Find and replace, Insert text, Remove characters, Change case, Trim, Add a number, Name from a pattern, Names from a list**. |
| 3 | Add **Find and replace**; Find `IMG_`, Replace with `Holiday `. | The preview updates as you type (a short pause after each key) and shows `Holiday 0001.jpg` and so on. Nothing on disk changes. |
| 4 | Tick **Match case**, then change Find to `img_`. | With Match case on, nothing matches and the names stay; turn it off and they match again. |
| 5 | Tick **Regex** and use Find `IMG_0*(\d+)` with Replace `P$1`. | `P1.jpg` to `P6.jpg`. An invalid regex shows an error in the status line, not a crash. |
| 6 | Use the target box of the step (name, extension or both). | Switching to *extension* applies the step only to `.jpg`; to *name* only to the part before it. |

### The other steps

| # | Do | Expect |
|---|----|--------|
| 7 | Remove the first step (**✕**). Add **Insert text** with Text `2025-`, **Characters in** `0`. | `2025-IMG_0001.jpg`. With **From the end** the text goes before the extension (counting from the end of the name). |
| 8 | Add **Remove characters**: Start at `0`, How many `4`. | The first four characters are removed from each name. **Count from the end** counts from the other side. |
| 9 | Select `Mixed Case Name.Md` and `notes.TXT`; open a new rename, add **Change case** and cycle *lower case*, *UPPER CASE*, *Title Case*, *Sentence case*. | Each style applies to the name (and the extension if that target is chosen); **Title Case** gives `Mixed Case Name`, Sentence gives `Mixed case name`. |
| 10 | Select the `draft` and `take` files and the padded-name file. Add **Trim** with *Collapse repeated spaces*. | Leading and trailing spaces and runs of spaces are removed; the padded file loses its surrounding spaces (the preview shows the exact result). |
| 11 | Select the six `IMG` files again. Add **Add a number**: Start `1`, Step `1`, Digits `3`, Separator `_`, then try **At the start**. | `IMG_0001_001.jpg` ... `IMG_0006_006.jpg`; at the start gives `001_IMG_0001.jpg`. |
| 12 | Add **Name from a pattern** with `{name}-{counter:000}`. | Tokens are replaced: the hint at the bottom lists `{name} {ext} {date:yyyy-MM-dd} {taken} {duration} {resolution} {codec} {parent} {counter:000}`. |
| 13 | Use the pattern `{date:yyyy-MM-dd} {parent} {counter:000}`. | The date is each file's modified date (the fixture dates are 3 days apart), `{parent}` is `16-rename`. |
| 14 | Add **Names from a list** and type six lines of new names. | The first line goes to the first item in the list order, and so on. Fewer lines than items shows a clear problem. |
| 15 | Select clips in `09-media` and use `{duration} {resolution} {codec}`. | The status shows "Reading dates, lengths and sizes from the files…" then the media values fill in. |

### Several steps, order and conflicts

| # | Do | Expect |
|---|----|--------|
| 16 | Stack Find and replace, then Change case, then Add a number. Use **▲ ▼** on a step. | The steps run top to bottom; moving a step changes the result at once. |
| 17 | Select `take-1.txt` and `take-2.txt`; add **Find and replace** `-1` → `` (empty) and `-2` → `` (empty) with a second step. | Both become `take.txt`: the rows turn **red** (conflict) and the status says "N names are in conflict or not allowed (red). Fix the steps or untick those items." **Rename** is blocked until fixed. |
| 18 | Untick one of the conflicting rows. | The conflict clears and Rename is allowed for the rest. |
| 19 | Use a step that produces an invalid name (insert `a:b`). | The row is red as not allowed. |
| 20 | Rename `draft 1.txt` to the same name as an existing file outside the selection (add a step that makes it `notes.TXT`). | A conflict with the existing file is shown, not an overwrite. |

### Presets and applying

| # | Do | Expect |
|---|----|--------|
| 21 | With steps set, press **Save as preset…** and name it `Holiday photos`. | "Saved the preset 'Holiday photos'." It is in the **Preset** list; picking it loads the steps in a fresh window. |
| 22 | Pick the preset, then press **Delete preset**. | "Deleted the preset 'Holiday photos'." Add a step first: with no steps, Save as preset says to add one. |
| 23 | Press **Rename**. | "Renaming…" then the window closes; the list in the folder shows the new names at once. |
| 24 | Press **Ctrl+Z** in the main window. | **All** the names go back in one step (one undo for the batch). **Ctrl+Y** repeats it. |
| 25 | Run a batch that swaps names (rename `a` to `b` and `b` to `a` using two files). | Neither file is overwritten or lost; note exactly what happens (it may be refused as a conflict, or done through a safe order). |
| 26 | Rename a mixed selection with a **folder** (`a folder to rename`) and files. | Both are renamed; the extension step affects files only. |
| 27 | Rename items on a share `\\localhost\LeviathanUat\16-rename` (see P2-A). | It works; a name the share refuses is flagged in the preview before you press Rename. |
| 28 | Run the batch while a file in it is open in another program (lock it with `[IO.File]::Open(...)` in PowerShell). | The window says "Nothing was renamed." with the first problem, **or** renames the rest and reports the failure; no half-renamed state you cannot undo. Note which. |
| 29 | Rename 1,000 files (use `06-ten-thousand`, select 1,000). | The preview updates without freezing; Rename runs as a job and finishes. |

## Look at (tests cannot judge these)

- Is the **preview table** clear (old name, new name, red conflicts)? Is the delay after typing right?
- Are the step names and their field labels understandable? Is the hint about tokens easy to find?
- Does the batch **undo** feel like one action?

## Known edge cases

- Rename runs as a worker job but the single-item F2 rename still runs in the app.
- A rename that only changes letter case (`a.txt` to `A.txt`) and a swap of two names are edge cases the rename pipeline was designed for (no overwrite, no loss) but not checked by hand; steps 25 and the case styles in step 9 are the first look.
- `{taken}` (the photo's date taken) and the media tokens are blank for files that have none.
- The property tests guarantee no silent collisions, and that a reversible batch reverses; the preview is the real defence, so look at it.

## Result

Pass / fail / notes, and anything to tweak.

## Clean up

```powershell
Remove-Item -Recurse -Force "D:\Playbooks\data"
.\tools\New-UatFixtures.ps1
```
