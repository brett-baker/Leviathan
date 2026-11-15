# UAT: P5-G Hardening: budgets, stress, awkward names and the accessibility walk

**Goal:** check the things tests cannot judge: how fast and how light the app feels, whether huge folders and awkward names break the folder view, and whether it can be used from the keyboard and with a screen reader.
**Built in:** Phase 5 slices G1 to G4 (`docs/phase-5-budgets.md`, `docs/phase-5-stress.md`, `docs/phase-5.md`), spec §3, CLAUDE.md "non-negotiables".
**Time:** about 60 minutes. The budget runs take a few minutes each and are best done on a quiet PC.

## Setup

1. Fixtures: `.\tools\New-UatFixtures.ps1` (folders `02-odd-names`, `03-long-paths`, `04-attributes`, `06-ten-thousand`).
2. Scratch settings and start:
   ```powershell
   $env:LEVIATHAN_DATA_DIR = "D:\Playbooks\data"
   & .\src\Leviathan.App\bin\x64\Debug\net10.0-windows10.0.26100.0\Leviathan.exe
   ```
3. For the budget runs build the published (ReadyToRun) app: `.\tools\Publish-App.ps1` (the output folder is printed). A Debug build is slower and is not what the budget measures.

## Steps

### A. Budgets you can feel and measure

| # | Do | Expect |
|---|----|--------|
| 1 | Run the gate: `dotnet run -c Release --project tests/Leviathan.Benchmarks -p:Platform=x64 -- --gate`. | Every line says PASS (the ones needing a NAS or Everything say SKIPPED). Last measured: 10,000 items first rows 4.8 ms, 100,000 items 153 ms, new tab 5.9 ms, name search 0.4 to 20 ms. |
| 2 | Cold start: `... -- --cold-start-gate <published Leviathan.exe>`. | **Known miss:** about 920 ms on a quiet PC (budget 600 ms), 1.2 s or more under load. Write down your number and what else was running. |
| 3 | Idle memory: `... -- --idle-memory <published Leviathan.exe> 10`. | **Known miss:** about 262 to 274 MB private (budget 250 MB). Write down your number. |
| 4 | A big copy: `... -- --big-copy 24` (24 GiB sparse). | About 44 s at about 580 MB/s, memory peak about 46 MB; "CORRECT". Use a different number of GiB for a different size; the folders are removed afterwards. |
| 5 | **[lab]** NAS folder: `... -- --nas-rows <a NAS folder with about 10,000 items>` and copy speed `... -- --nas-copy <NAS folder>`. | First rows under 400 ms; copy at 105 MB/s or more on 1 GbE. Use only a dedicated test share you name, never Y:, Z: or `\\192.168.1.10`. Not yet measured by anyone. |
| 6 | By eye: start the published app cold three times. | Time from double-click to a usable first folder. Write down how it *feels*. |
| 7 | By eye: open `06-ten-thousand`, then scroll to the end quickly with the scrollbar and with **End**. | No stall, no blank rows. Open **Settings > Advanced > Diagnostics**: "UI stalls over 50 ms since start" does not climb during this. |

### B. Stress by hand (the engine is tested; the folder view is the gap)

| # | Do | Expect |
|---|----|--------|
| 8 | Open `02-odd-names`. | All files are listed with correct names, including the Unicode, emoji, the 200-character name, and the three that Explorer cannot make: `trailing-dot.`, `trailing-space ` and `CON.txt`. |
| 9 | On each of those three, try: select, **F2** rename, **Enter** (open), right-click, **Ctrl+C** and paste elsewhere, **Del**, **Ctrl+Z**. | **Known gap:** the engine copies, moves and deletes these correctly, but the list, rename, open and right-click menu may fail on them (they build paths the ordinary way). Write down exactly which actions fail; this is the input to fix the view. |
| 10 | Open `03-long-paths` and drill to the bottom (over 260 characters). Copy the bottom folder to `08-targets`, then delete the copy. | Everything works at every depth. |
| 11 | Select all of `06-ten-thousand` (**Ctrl+A**) and copy to `08-targets\target-a`. | One job; the window stays responsive; counts correct. |
| 12 | Open `04-attributes\big-sparse-6GiB.bin`'s folder. Compute its checksum (P5-C) and copy it. | Streams, progress, no memory growth. |
| 13 | Make a 500,000-file folder (PowerShell: ~5 minutes; skip if short of time) and open it. | Rows stream in; sorting by name and size works; the status bar counts. **Not yet looked at on screen** at this size, so note how it scrolls and how much memory the app holds. |
| 14 | Kill the share (P2-A) during a **delete** on it. | The job waits and retries (it no longer fails the item); it finishes when the share returns. |

### C. Memory and long sessions

| # | Do | Expect |
|---|----|--------|
| 15 | Open and close about 100 tabs and 20 windows over a few minutes. | Memory in Task Manager (Details, `Leviathan.exe` private bytes) returns close to where it began (tests prove nothing is kept; this checks the real thing). |
| 16 | Browse a folder of 2,000 pictures in Thumbnails, then another, then a third. | Memory rises then plateaus (the thumbnail cache is capped at 64 MB of pictures and 50,000 remembered answers). |

### D. Keyboard-only walk (no mouse)

Unplug the mouse or just do not touch it. Note every place you get stuck.

| # | Do | Expect |
|---|----|--------|
| 17 | **Tab** and **Shift+Tab** through the window. | Focus moves through the tab strip, the navigation row, the sidebar, the file list and the status bar in an order that makes sense, with a **visible** focus indicator everywhere. Write down any place it vanishes or loops. |
| 18 | Navigate: **Ctrl+L**, type a path, Enter; **Alt+Left/Right/Up**; **Ctrl+P**; sidebar with arrows. | All work without the mouse. |
| 19 | Select with **arrows, Shift+arrows, Ctrl+A**; **Enter**, **F2**, **Del**, **Ctrl+C/X/V**, **Ctrl+Z**. | All work. |
| 20 | Open the context menu with **Shift+F10** or the menu key. | The menu opens at the selection and you can use it with arrows and Enter. |
| 21 | Tabs: **Ctrl+T/W/Tab/Shift+T**, **Ctrl+1..9**; groups from the tab's context menu (**Shift+F10** on the strip). | Reachable by keyboard. Note anything only a mouse can do (known: drag a tab, drag a tab chip). |
| 22 | Open the **status hub** (**Ctrl+Shift+O**), move between its tabs and rows, use row actions; **Esc** closes. | Reachable. The space-map picture itself is mouse-only, but its **Largest items** list beside it is the keyboard route. |
| 23 | Open **Settings** (**Ctrl+,**), search, change a value, edit a key binding (capture box), close with Esc. | All keyboard-reachable. |
| 24 | Open each window: **Bulk rename** (Shift+F2), **Duplicates** (Alt+Shift+F), **Checksums**, **Compare and sync**, **Triage** (Alt+T), the space map (Ctrl+Shift+M), **Diagnostics**. | Each can be filled in and operated with Tab, arrows, Space and Enter, and closed with Esc or Alt+F4. Note any control you cannot reach. |

### E. Screen reader (Narrator, **Win+Ctrl+Enter**)

| # | Do | Expect |
|---|----|--------|
| 25 | Tab through the main window with Narrator on. | Each button, slider, progress bar and list announces a **name** (examples fixed in G4: "Seek", "Space used on this drive", "Free space on this drive", "Thumbnail size", "Operations", "Recent activity", "Soft-deleted items", "Settings pages", "Key bindings"). Write down anything announced as just "button" or the control type alone. |
| 26 | Move through the file list with arrows. | Each row announces its **name** and ideally type, size and date. **Unchecked:** whether the templated rows announce a useful name; note exactly what is read. |
| 27 | Open the status bar parts. | Their text (item count, selection) is read as the button's name. |
| 28 | Open the Duplicates, Bulk rename and Sync windows. | Fields announce their headers (Find, Replace with, Left folder, Sync mode…). Red conflict rows should be announced as a conflict, not only coloured: **note if not**. |
| 29 | Hear the **Git** letters in the details/list. | The tooltip gives the state in words; check whether Narrator reads it. |

### F. Contrast and themes

| # | Do | Expect |
|---|----|--------|
| 30 | Look at the whole app in **Light**, then **Dark**. | All text readable. The Git letters and checksum OK / mismatch / missing marks are readable in both (checked by test at 4.5:1 or better). |
| 31 | Turn on a Windows **contrast theme** (Settings > Accessibility > Contrast themes). | **Unchecked.** Note what is unreadable or invisible (selection, focus frames, the activity strip lines, the hub). Icons should switch to monochrome. |
| 32 | Look at the **activity strip colours**, tag colours and sparklines. | Distinct from each other; the meaning is also in a tooltip or text, not colour alone. |
| 33 | Zoom Windows text to 150% and 200% (Settings > Accessibility > Text size) and the display scaling to 150%. | The layout still works; nothing is cut off; the settings sheet and dialogs remain usable. |
| 34 | In **Triage** (always dark) read the overlay text and the buttons. | The Keep and Discard buttons and the text are readable (measured above 8:1). |

## Look at (tests cannot judge these)

- All of section A: **speed and lightness as you experience them**, and the two known misses: is 0.9 to 1.2 s to the first folder acceptable to you? Is about 270 MB acceptable?
- Is the **focus order** logical? Is the **focus indicator** visible on every control?
- Does Narrator give you enough to use the app, or is it noise?

## Known edge cases

- The two budget misses (cold start, idle memory) are explained in `docs/phase-5-budgets.md`, with what would close them.
- A 100 GB file was extrapolated from 24 GiB, not run.
- Real SMB drops during copy, move and delete were tested with injected failures; the local share removal in P2-A and P2-B is the closest real check.
- Odd names in the folder view are a **known gap** (step 9).
- Mouse-only: dragging tabs and chips, the space-map picture, rubber-band selection.
- High-contrast themes, focus order and Narrator output were never checked by anyone; this section is the first pass. Add what you find to the table at the end of `docs/phase-5.md`.

## Result

Pass / fail / notes. Record: cold start __ ms; idle memory __ MB; where the keyboard walk broke; what Narrator could not name.

## Clean up

```powershell
Remove-Item -Recurse -Force "D:\Playbooks\data"
.\tools\New-UatFixtures.ps1
```
