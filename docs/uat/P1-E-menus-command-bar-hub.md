# UAT: P1-E Right-click menu, command bar, status bar, status hub and Activity

**Goal:** reach every action from the right-click menu or the command bar, and use the status hub (Operations, Activity, Background, Quick settings).
**Built in:** Phase 1 slices D7, D10, F1 to F3, I7 to I10 (`docs/phase-1.md`), spec §6, §8.5, §16.
**Time:** about 30 minutes.

## Setup

1. Fixtures: `.\tools\New-UatFixtures.ps1` (folders `01-basic`, `02-odd-names`, `08-targets`).
2. Scratch settings and start:
   ```powershell
   $env:LEVIATHAN_DATA_DIR = "D:\Playbooks\data"
   & .\src\Leviathan.App\bin\x64\Debug\net10.0-windows10.0.26100.0\Leviathan.exe
   ```

## Steps

### Right-click menu

| # | Do | Expect |
|---|----|--------|
| 1 | Right-click a file in `01-basic`. | Leviathan's own menu appears **at once**: Open (or Open with), Cut, Copy, Paste (greyed until the clipboard has files), Rename, Delete, context actions, Properties. |
| 2 | Look for **More options**. | After a moment (the shell is asked in a separate process) it lists the shell's items (Open with, Send to, 7-Zip and so on) with icons. The app never freezes while it loads. |
| 3 | Choose **Copy as path** from More options, paste into a text file. | The path is on the clipboard. |
| 4 | **Shift+right-click** a file. | The shell's full menu shows inline (Shift menu). |
| 5 | Select a file and press **Shift+F10** or the menu key. | The same menu opens at the selection. |
| 6 | Right-click empty space. | Paste, New folder, select commands, and the New menu items. The selection clears first. |
| 7 | Right-click a folder. | Open, **Open in new tab**, Cut/Copy/Rename/Delete, Properties. |
| 8 | Right-click two files, then a media file (`09-media\short-3s.mp4`). | The multi-selection menu hides single-file items; the video has **Contact sheet** and the triage entries. |
| 9 | Choose **Properties** (or **Alt+Enter**). | The Windows Properties dialog opens for the item. |
| 10 | Right-click on a file whose shell handler is slow or broken (if you have one). | The app keeps working; the shell items may be missing. A crashing handler never closes Leviathan. |

### Command bar

| # | Do | Expect |
|---|----|--------|
| 11 | Hover each button in the clipboard group (Cut, Copy, Paste, Rename, Delete, Undo, Redo). | Tooltips read "Name (Shortcut)". Buttons grey out when they cannot apply (no selection, empty history). |
| 12 | Open **New**. | Built-in, ShellNew and your templates; **Ctrl+Shift+N** makes a folder. |
| 13 | Select files, then look at the **context zone** of the bar. | It follows the selection and offers actions that fit (for example Extract for an archive, Compress for files). |
| 14 | Shrink the window. | Buttons move to the **overflow** menu rather than disappearing. |

### Status bar

| # | Do | Expect |
|---|----|--------|
| 15 | Read the parts. | Item count; selection and size; filter; location health (network); free space with a bar; link speed (network); layout buttons and thumbnail slider. Empty parts are hidden. |
| 16 | Click the item count, selection, and filter parts. | Toggle hidden items, select none, clear the filter. |

### Status hub

| # | Do | Expect |
|---|----|--------|
| 17 | Press **Ctrl+Shift+O**, or click the chevron at the right of the status bar. | A fly-up panel opens with tabs **Operations, Activity, Background, Quick settings, Space** (and the Soft-delete bin from the Options menu). |
| 18 | Drag its top edge to change height. Close with **Esc**, reopen. | The height is remembered. Clicking outside closes it unless it is **pinned**. |
| 19 | Choose dock bottom, dock right, and pop out. | The hub moves; popped out it is its own window. Restart: the position is remembered. |
| 20 | **Activity** tab: run a few commands (rename, delete, copy), then look. | Every command is listed with its arguments, newest first. Type in the search box to filter; choose a row's go-to-location, undo and copy/export actions. Entries older than 7 days are dropped. |
| 21 | **Background** tab. | Folder-size queue and rate, thumbnail generation, the worker and ShellHost processes with their restart counts. Pause, resume or priority where offered. |
| 22 | **Quick settings** tab. | Background threads, running jobs, folder sizes on network shares, thumbnails on network files, polling interval. Change one: it takes effect without a restart. |
| 23 | **Operations** tab during a copy of `07-bulk-copy`. | Rows with progress, speed and time left; a status-bar chip opens this tab. (Full operations behaviour: P2-C.) |
| 24 | **Space** tab. | A mini map for the current folder (the space map has its own playbook, `D-space-map`). |

### Options menu and settings sheet

| # | Do | Expect |
|---|----|--------|
| 25 | Click the **Options** gear, then choose **Settings** (or press **Ctrl+,**). | A sheet rises from the bottom (about 70% of the window) with the page list at the left and a search box on top; it can expand to full window. |
| 26 | Type `theme` in the search box. | Matching settings are listed; choosing one jumps to its page. |
| 27 | Open **Advanced > Diagnostics**. | The Diagnostics window opens (worker state, jobs, crashes, "UI stalls over 50 ms since start"). |

## Look at (tests cannot judge these)

- Time from right-click to Leviathan's own menu (should feel instant) and to **More options**.
- Is the **hub** panel cluttered or clear? Do the tab names make sense?
- Does the command bar overflow behave sensibly at narrow widths?

## Known edge cases

- Shift+F10 and the menu key were only checked through code paths (synthetic keys do not reach WinUI's context handling); this playbook is the real check.
- A menu on a network share (UNC path) was not verified.
- The empty-space menu has no "Open terminal here"; use **Ctrl+Alt+T** (see P5-E).
- Tag, Compress and Extract appear in the menu through later phases; Phase 1 had none.

## Result

Pass / fail / notes, and anything to tweak.

## Clean up

```powershell
Remove-Item -Recurse -Force "D:\Playbooks\data"
```
