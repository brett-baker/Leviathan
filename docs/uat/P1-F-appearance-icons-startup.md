# UAT: P1-F Appearance, icon sets, listing cache and startup choice

**Goal:** change how the app looks and what it opens at startup, and see that revisiting a folder is instant and always correct.
**Built in:** Phase 1 slices C4, G1 to G3, I6, I10, listing cache (`docs/phase-1.md`), spec §4.3, §17.1, §17.2.
**Time:** about 25 minutes.

## Setup

1. Fixtures: `.\tools\New-UatFixtures.ps1` (folders `01-basic`, `06-ten-thousand`).
2. Scratch settings and start:
   ```powershell
   $env:LEVIATHAN_DATA_DIR = "D:\Playbooks\data"
   & .\src\Leviathan.App\bin\x64\Debug\net10.0-windows10.0.26100.0\Leviathan.exe
   ```
3. Press **Ctrl+,** to open the settings sheet and keep it handy.

## Steps

### Appearance page

| # | Do | Expect |
|---|----|--------|
| 1 | **Appearance > Theme**: System, Light, Dark. | The window changes. The page says the theme takes effect **the next time the app starts**, so restart and check it holds. Note any other setting that needs a restart. |
| 2 | **Density**: Compact, then Comfortable. | Row height and spacing change in the file list and sidebar. |
| 3 | **Accent colour**: pick a colour. | New windows use it (selection, focus frame, drop highlight). Open a new window with **Ctrl+N** to see it. |
| 4 | **Window backdrop (Mica)**: off, then on. | The window background turns solid, then transparent to the wallpaper. |
| 5 | **Icon size**: small to large. | Icons in lists and the tree change size. |
| 6 | **Icon set**: Fluent, Font Awesome, Colour (and any pack you drop in the icons folder). | Command bar, tree, tabs, status bar and menus swap icons. In dark theme the glyphs stay readable. File-view icons come from the shell and do not change. |
| 7 | Turn on Windows **High contrast** (Settings > Accessibility > Contrast themes) and look at the app. | Icons fall back to monochrome glyphs; text stays readable. Turn it back off. |

### Settings sheet

| # | Do | Expect |
|---|----|--------|
| 8 | Click through **General, Appearance, Files and folders** and the other pages. | Each page opens without delay. Search finds settings by title and keywords. |
| 9 | **Files and folders**: turn **Show hidden files** on and off. | The folder list updates at once (same as **Ctrl+H**). |
| 10 | **General**: change **Where a new tab opens** and press **Ctrl+T**. | The new tab opens there. |
| 11 | Open the settings file from **Advanced** and look at the JSON. | Settings are human-readable; a change in the UI appears in the file. Edit a value there, save, and the app picks it up. |

### Listing cache

| # | Do | Expect |
|---|----|--------|
| 12 | Open `06-ten-thousand`, go away, come back. | The second visit shows rows **instantly** from the cache and then checks the folder. |
| 13 | In File Explorer, delete one file from `06-ten-thousand` while Leviathan shows it. | Leviathan's list drops the file within a second or so (the folder watcher). |
| 14 | Close Leviathan, delete another file in Explorer, start the app and open the folder. | It shows the cached list for an instant and then corrects itself; the deleted file is never there once the check ends. |
| 15 | **Advanced > Clear caches**. | Listings and thumbnails clear; the next visit is slower once, then fast again. |

### Startup choice

| # | Do | Expect |
|---|----|--------|
| 16 | **General > What to open at startup**: Last session, a profile, A fresh window, Ask me. | Restart after each choice: you get what you chose. (Profiles are covered in P1-A.) |
| 17 | With **Last session**, open three tabs, close with X, start again. | The tabs return. |

### Keyboard map (basics; the editor is in P5-F)

| # | Do | Expect |
|---|----|--------|
| 18 | Press **Ctrl+Shift+P** and type `new tab`. | The palette lists *New tab* with its shortcut `Ctrl+T`. |
| 19 | Compare a few shortcuts against the tooltip text of buttons. | Every tooltip shows the key currently bound to the command. |

## Look at (tests cannot judge these)

- Do the **icon sets** look consistent and crisp at each size and theme?
- Is **Mica** subtle enough that text stays readable?
- Do **Compact** and **Comfortable** feel different enough to be useful?
- Does switching theme or accent ever leave part of the window in the old look?

## Known edge cases

- Some settings may apply only to new windows (accent) or after a restart (theme); the page should say so. Note any that do not.
- High-contrast support is the fallback to monochrome glyphs only; no custom high-contrast layout exists.
- Three settings (show extensions, single click to open, Ctrl+Tab in recent order) were left without a control on the page because they do nothing yet. The search may still list "single click" and "Ctrl+Tab" entries; if a control exists and has no effect, note it.

## Result

Pass / fail / notes, and anything to tweak.

## Clean up

```powershell
Remove-Item -Recurse -Force "D:\Playbooks\data"
```
