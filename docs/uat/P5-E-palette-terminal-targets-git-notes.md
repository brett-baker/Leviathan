# UAT: P5-E Command palette, Open terminal here, quick targets, elevation, Git overlays and folder notes

**Goal:** reach any command by name, open a terminal where you are, send files to nine preset folders with one chord, run one action as administrator, see Git state in the list, and keep a note on a folder.
**Built in:** Phase 5 slices E1 to E6 (`docs/phase-5.md`), spec §6, §15.6, §15.7, §5.3.
**Time:** about 40 minutes. The elevation step shows a Windows UAC prompt.

## Setup

1. Fixtures: `.\tools\New-UatFixtures.ps1` (folders `01-basic`, `08-targets` (`target-a`, `target-b`), `19-git`, `10-triage`).
2. Scratch settings and start:
   ```powershell
   $env:LEVIATHAN_DATA_DIR = "D:\Playbooks\data"
   & .\src\Leviathan.App\bin\x64\Debug\net10.0-windows10.0.26100.0\Leviathan.exe
   ```
3. `19-git` is a real Git repository with one of each state; it needs `git` on the PATH when built. Skip the Git steps if the fixture says it could not make it.

## Steps

### Command palette (Ctrl+Shift+P) and Go to (Ctrl+P)

| # | Do | Expect |
|---|----|--------|
| 1 | Press **Ctrl+Shift+P**. | A palette opens with a search box. Every command is listed with its category and its shortcut. |
| 2 | Type `newt`. | **New tab** (Ctrl+T) is at the top; matching letters are drawn in bold (fuzzy matching: `nt`, `ntab` also find it). |
| 3 | Move with **Down/Up**, press **Enter** on *New tab*. | The command runs and the palette closes. **Esc** closes it without running anything. |
| 4 | Open it, type a command that is disabled right now (for example *Paste* with nothing copied). | Disabled commands are not run; they are greyed or hidden (note which). |
| 5 | Press **Ctrl+P**. | The **Go to** palette: favourites, recent folders, open tabs and commands, in that order. |
| 6 | Type part of a folder you visited (`basic`). | `01-basic` appears as a recent folder with its full path in grey. **Enter** opens it. |
| 7 | Type part of an open tab's name. | The tab row appears; **Enter** switches to that tab. |
| 8 | Type `>` then a command name in Go to. | A leading `>` narrows to commands only. |
| 9 | Run the same command from the palette, its shortcut and its menu or button. | All three do the same thing. |
| 10 | Open the Activity tab of the status hub. | The palette-run commands are recorded like any other. |

### Open terminal here (Ctrl+Alt+T)

| # | Do | Expect |
|---|----|--------|
| 11 | In `01-basic` press **Ctrl+Alt+T**. | **Windows Terminal** opens in `01-basic` (or the terminal you chose in Settings > General). |
| 12 | Select the `Documents` folder and press **Ctrl+Alt+T**. | The terminal opens **in that selected folder**. With a file selected it opens in that file's folder. |
| 13 | **Settings > General > Terminal that Open terminal here uses**: choose PowerShell, then cmd, then WSL (if installed). Repeat step 11. | Each opens the chosen shell in the right folder; a missing one gives a clear message instead of nothing. |
| 14 | Open the drive summary card at a drive root and press **Open terminal here**. | The same command, at the drive root. |
| 15 | Try a folder with a space and a Unicode name (`02-odd-names`). | The terminal's prompt shows the exact path. |
| 16 | Try on a network share `\\localhost\LeviathanUat` (P2-A). | The terminal opens at the UNC path (PowerShell works; cmd may map it) or explains. |

### Quick move and copy targets (Ctrl+Alt+1..9)

| # | Do | Expect |
|---|----|--------|
| 17 | **Settings > Files and folders > Quick move and copy targets**: set slot 1 to `D:\Playbooks\uat\08-targets\target-a` and slot 2 to `D:\Playbooks\uat\08-targets\target-b`. | The boxes accept folders; an empty slot stays unset; the list keeps nine slots. |
| 18 | In `01-basic` select `notes.txt` and press **Ctrl+Alt+1**. | The file **moves** to `target-a` through the normal queue (verified, resumable). |
| 19 | Select `todo.md` and press **Ctrl+Alt+Shift+2**. | It is **copied** to `target-b`. |
| 20 | Press **Ctrl+Alt+5** (unset slot). | Nothing happens or a short notice says the slot is empty; no job is made. |
| 21 | Press **Ctrl+Z** after a quick move. | If undo of a move is not offered (it is not built for copies and moves), nothing changes; note it. |
| 22 | In **triage** (Alt+T in `10-triage\inbox`), press **Ctrl+Alt+1**. | The current video moves to slot 1's folder and the queue advances. |
| 23 | Use the triage dialog "Move to a quick target" if present. | A list of the nine targets to pick from. |

### Run as administrator

| # | Do | Expect |
|---|----|--------|
| 24 | Select `01-basic\script.ps1` and use right-click > **Run as administrator** (or the palette). | A **Windows UAC prompt** appears. Declining it runs nothing and shows no error. |
| 25 | Do an action on a protected folder (for example copy a file into `C:\Windows\Temp` is allowed; try `C:\Program Files`). | Leviathan offers to **repeat that one action elevated**, then a UAC prompt; **only that action** runs elevated, not the whole app. Decline and nothing happens. |

### Git status overlays (needs a repository)

| # | Do | Expect |
|---|----|--------|
| 26 | Open `D:\Playbooks\uat\19-git`. Turn on **Settings > Files and folders > Git status in repositories**. | A **Git** column (and/or branch information) appears in Details. |
| 27 | Read each file's letter. | `modified.txt` **Modified** (M); `staged-new.txt` **Added** (A); `deleted.txt` **Deleted** (D); `new-name.txt` **Renamed** (R); `untracked.txt` **Not tracked** (U or ?); `ignored.log` **Ignored**; `clean.txt` and `.gitignore` **Unchanged** (blank). The tooltip names the state. |
| 28 | Switch the theme between light and dark. | The letters stay readable in both (they use theme-specific colours, 4.5:1 or better). The meaning is also in the letter and the tooltip, not the colour alone. |
| 29 | Edit `clean.txt` in a text editor and save. | Within a moment (the folder watcher) it becomes **Modified**. Commit it from a terminal; it returns to blank. |
| 30 | Turn the setting **off**. | The column and the git work stop; opening a big non-repository folder is not slowed down. |
| 31 | Open `01-basic` (not a repository). | No Git column content, and no delay. |

### Folder notes (details pane)

| # | Do | Expect |
|---|----|--------|
| 32 | Show the **details pane**, select the folder `Documents` in `01-basic`. | A **Folder note** box (labelled for screen readers) is under the facts. |
| 33 | Type a note and click elsewhere. | It is saved when the box loses focus. Select another item and come back: the note is there. |
| 34 | Restart the app. | The note persists (it is in `folder-notes.json` in the data folder). |
| 35 | Clear the box. | The note is removed. A very long note is cut at 2,000 characters. |
| 36 | Rename the folder in Leviathan, then select it. | **Known gap:** the note does *not* follow the rename (it stays with the old path). Note it. |

## Look at (tests cannot judge these)

- Does the palette feel as fast and forgiving as VS Code's? Is the ranking what you expect?
- Is **Open terminal here** opening exactly where you expect, with the shell you prefer?
- Do the **quick target chords** feel safe (an accidental press moves a file)? Would you want a confirmation or a toast with Undo?
- Is the **Git column** useful in the folder list? Does it appear only when relevant?

## Known edge cases

- A palette command that needs a selection or a prompt may open that prompt rather than finish.
- The empty-space right-click menu has no "Open terminal here"; use the key, the palette or the drive card.
- Quick moves and copies cannot be undone with Ctrl+Z (copy and move undo is not built).
- Git states come from the repository on disk and may lag the editor by a moment; a repository with thousands of changes may take longer.
- Folder notes do not follow a rename or delete (`Move` and `Remove` exist but are not wired).
- There is no palette command or sidebar marker for notes yet.

## Result

Pass / fail / notes, and anything to tweak.

## Clean up

```powershell
Remove-Item -Recurse -Force "D:\Playbooks\data"
.\tools\New-UatFixtures.ps1
```
