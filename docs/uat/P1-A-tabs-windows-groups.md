# UAT: P1-A Tabs, tab groups, windows and startup sessions

**Goal:** open, move, group, pin, lock, tear off and restore tabs and windows, and get the same session back after a restart or a crash.
**Built in:** Phase 1 slices B6, B7, D2, I4, I5, G1 (`docs/phase-1.md`), spec §4.1, §17.1.
**Time:** about 30 minutes.

## Setup

1. Build the fixtures (never test with real data):
   ```powershell
   .\tools\New-UatFixtures.ps1
   ```
   Folders used: `D:\Playbooks\uat\01-basic`, `02-odd-names`, `04-attributes`, `08-targets`.
2. Start the app with scratch settings so your real session and profiles are untouched:
   ```powershell
   $env:LEVIATHAN_DATA_DIR = "D:\Playbooks\data"
   & .\src\Leviathan.App\bin\x64\Debug\net10.0-windows10.0.26100.0\Leviathan.exe
   ```
3. Set **What to open at startup** to *Last session* (Settings, Ctrl+, , General).

## Steps

### Tabs

| # | Do | Expect |
|---|----|--------|
| 1 | Press **Ctrl+T**. | A new tab opens at your home folder and is selected. Opening it feels instant. |
| 2 | In it, go to `D:\Playbooks\uat\01-basic`. Press **Ctrl+T** again and go to `02-odd-names`. | Each tab shows its folder name as its title. |
| 3 | Press **Ctrl+Tab**, then **Ctrl+Shift+Tab**. | The selection cycles forward, then back. |
| 4 | Press **Ctrl+1**, **Ctrl+2** (digits). | The first, then the second tab is selected. |
| 5 | Press **Ctrl+W** on the third tab. Then press **Ctrl+Shift+T**. | The tab closes; Reopen brings it back with its folder and back/forward history. |
| 6 | Right-click a tab, choose **Duplicate tab** (or **Ctrl+Shift+K**). | A copy opens next to it with the same folder, selection and history. |
| 7 | Middle-click a folder (such as `Documents` in `01-basic`). | It opens in a **background** tab; the current tab stays selected. |
| 8 | Middle-click a tab. | The tab closes. |
| 9 | Right-click a tab: **Pin tab**. | The tab shrinks to an icon at the left of the strip. **Ctrl+W** on it does nothing (the command is greyed). |
| 10 | Right-click another tab: **Lock tab**. Try to navigate it to another folder. | A lock mark shows. Navigating opens the folder in a new tab instead of changing the locked one. |
| 11 | Right-click a tab: **Rename tab…** (type `Work`), then **Tab colour** (a submenu: None and the colours; pick one). | The title becomes `Work` and the tab gets the colour. |
| 12 | Right-click a tab: **Close other tabs**, then open three more and use **Close tabs to the right** on the first. | Only the intended tabs close. Pinned tabs survive "Close other tabs". |
| 13 | Drag a tab left and right in the strip. | It reorders. **Ctrl+Shift+PageUp / PageDown** moves the selected tab the same way. |
| 14 | Open the **all tabs** list at the right end of the strip and type part of a tab title. | The list filters; choosing a row selects that tab. |

### Groups

| # | Do | Expect |
|---|----|--------|
| 15 | Right-click a tab: **Add tab to new group** and name it `NAS inbox`. | A coloured chip `NAS inbox` appears in front of the tab and a coloured bar sits under it. |
| 16 | Right-click another tab: **Add tab to group** (a submenu), choose `NAS inbox`. | The tab joins and sits next to the first. A group's tabs are always side by side. |
| 17 | Click the chip. | The group folds: the chip shows the name and the tab count (`NAS inbox · 2`) and its tabs are hidden. Click again to unfold. |
| 18 | With the group folded, press **Ctrl+Tab** repeatedly. | Hidden tabs are skipped. |
| 19 | Right-click the chip: **Collapse group / Expand group**, **Rename group…**, **Group colour** (a submenu with the current one ticked), **New tab in group**, **Ungroup**, **Close group**, **Move group to new window**. | Each does what it says. **Close group** closes all its tabs; Reopen closed tab restores the tab and its group. |
| 20 | Try to put a **pinned** tab into a group. | It is refused (pinned tabs are never grouped). |
| 21 | Open 20 tabs, put them in one group, fold and unfold it five times. | No visible freeze while folding. Then open **Settings > Advanced > Diagnostics**: "UI stalls over 50 ms since start" does not rise because of these actions (it may already show the startup stalls). |

### Windows

| # | Do | Expect |
|---|----|--------|
| 22 | Press **Ctrl+N**. | A second window opens (it takes a moment; a stall of about 110 to 130 ms is known). |
| 23 | Drag a tab out of the strip and drop it on the desktop. | A new window opens at the drop point with that tab, the same size, kept on screen. |
| 24 | Drag a tab from one window onto the other window's strip. | It merges there; the empty source window closes. |
| 25 | Right-click a group chip: **Move group to new window**. | The whole group moves with its colour and name. |
| 26 | Press **Ctrl+Shift+P**, type `move tab to a new window` and run it. | The tab opens in its own window. (The tab's right-click menu has no entry for this; tearing off by dragging is the other way.) |

### Sessions and profiles

| # | Do | Expect |
|---|----|--------|
| 27 | With two windows and a split tab and a group, close the app with the X. Start it again. | All windows come back with their size and position, tabs, groups (folded or not), pinned and locked tabs, the selected tab and each tab's history. |
| 28 | Kill the app: `Stop-Process -Name Leviathan -Force`. Start it again. | The same session returns (Last session is saved continuously, within about 2 seconds of the last change). Anything you changed in the last 2 seconds may be missing. |
| 29 | Open a tab at `\\10.255.255.1\share` (a dead address), close and restart. | The tab restores **offline** and the window is up in about 2 seconds. The dead tab never delays startup. |
| 30 | Press **Ctrl+Shift+P** and run **Save current windows as profile**; name it `Two folders`. Close everything, set startup to **Ask me**, restart. | A picker lists *Last session*, *Two folders* and *A fresh window*. Choosing the profile opens those windows and tabs. |
| 31 | Run **Open profile** and **Delete profile** from the palette. | Open restores the profile; Delete removes it after you pick it. |

## Look at (tests cannot judge these)

- Does the **group chip** read clearly in light and dark themes, and does the underline colour match the chip?
- Is a **pinned** tab's icon-only form recognisable? Is the **lock** mark visible but quiet?
- Does dragging a tab out feel natural (no flicker, the new window under the pointer)?
- Is the second window's opening delay acceptable?

## Known edge cases

- Dragging tabs and chips with the mouse was only partly driven in the real app (synthetic input does not start WinUI's tab drag reliably); the decision logic is unit-tested. Treat steps 13, 23, 24 as the real test of drag.
- Ctrl+click on a folder does not open a tab (it would clash with multi-select); use middle-click.
- Double-clicking the empty strip maximises the window (Windows' own behaviour), it does not open a tab.
- A restore that crashes on every start has no safe mode; rename `D:\Playbooks\data\session.json` to recover.
- Multi-selecting tabs to group several at once is not built; group one tab and add the rest.

## Result

Pass / fail / notes, and anything to tweak.

## Clean up

```powershell
Remove-Item -Recurse -Force "D:\Playbooks\data"
```
