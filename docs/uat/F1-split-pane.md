# UAT: F1 Split pane

**Goal:** split a tab into two panes, work in each independently, and close the split again without losing the pane you want.
**Built in:** commit `ff4485e`, spec §15.1.
**Time:** about 15 minutes.

## Setup

1. Build the fixtures (never test with real data):
   ```powershell
   .\tools\New-UatFixtures.ps1
   ```
   Fixture root: `D:\Playbooks\uat` (folders `01-basic`, `02-odd-names`, `04-attributes` are used below).
2. Start the app with scratch settings, so your real settings, favourites and saved servers are untouched:
   ```powershell
   $env:LEVIATHAN_DATA_DIR = "D:\Playbooks\data"
   & .\src\Leviathan.App\bin\x64\Debug\net10.0-windows10.0.26100.0\Leviathan.exe
   ```
3. In the first tab, go to `D:\Playbooks\uat\01-basic`.

## Steps

| # | Do | Expect |
|---|----|--------|
| 1 | Click the **side by side** button (second of the three pane buttons, left of the preview button). | The file area becomes two panes with a thin gap. Both show `01-basic`. The side-by-side button is highlighted and the others are not. The left pane has a thin accent frame. |
| 2 | In the **right** pane, double-click the `Documents` folder. | Only the right pane changes. The address bar now shows `...\01-basic\Documents` and the frame moves to the right pane. The left pane still shows `01-basic`. |
| 3 | Click an empty row area in the **left** pane. | The frame moves back to the left. The address bar shows `01-basic` again. The status bar counts match the left pane. |
| 4 | Click a file in the left pane, then click a file in the right pane. | Each pane keeps its own selection. Selecting in one does not clear the other. The status bar shows the selection of the pane you clicked last. |
| 5 | Focus the right pane. Press **Alt+Left**. | The right pane goes back to `01-basic`. The left pane does not move. |
| 6 | Focus the right pane. Use the **Layout** buttons (Details / List / Thumbnails) in the status bar to pick Thumbnails. | Only the right pane switches to thumbnails. The left stays in Details. Focus the left pane: the layout buttons show Details. |
| 7 | Click the **top and bottom** button. | The same two panes now sit one above the other. Their folders, selections and layouts are unchanged. |
| 8 | Press **Ctrl+\\** (backslash). | The layout changes to side by side. |
| 9 | Press **Ctrl+\\** again. | The split closes and one pane remains. |
| 10 | Press **Ctrl+Shift+\\**. | The tab splits again, top and bottom. Press it again: back to one pane. |
| 11 | Split side by side, go to `02-odd-names` in the right pane, focus the right pane, then click the **one pane** button. | One pane remains and it shows `02-odd-names` (the focused pane is kept, including its Thumbnails or Details layout). The tab title reads `02-odd-names`. |
| 12 | Split again, focus the **left** pane, and click the one pane button. | The left pane's folder is kept, and the other pane's folder is closed. |
| 13 | Split side by side. In the right pane go to `04-attributes`. Close the window with the X, then start the app again (same scratch data folder). | The tab comes back split, both panes show the same folders as before, the same pane has the frame, and both list their files. |
| 14 | Right-click the tab and choose **Duplicate tab** (or press Ctrl+Shift+K). | The copy is also split with the same two folders and the same focused pane. |
| 15 | With one pane showing, right-click a folder (such as `Documents`) and choose **Open in split pane**, if the menu has it. | `[optional]` The tab splits and the other pane shows that folder. If the item is missing from the menu, note it: the command works but the menu entry may not be wired yet. |
| 16 | Open the **View** menu. | It has One pane, Split side by side and Split top and bottom, with the shortcut text next to the last two. Each does what its name says. |
| 17 | Split, then open the folder `04-attributes` in the right pane (it has the 6 GiB sparse file). Scroll and sort by Size in each pane. | Both panes stay responsive. Sorting one pane does not re-sort the other. |
| 18 | With a split, drag a file from the left pane onto a folder row in the right pane. | `[optional]` The file copies into that folder. The job shows in the activity strip. |
| 19 | Split, make the window narrow (about 700 px wide), then wide again. | Both panes keep half of the width each. Nothing overlaps and no horizontal scrollbar appears for the pane area itself. |

Network steps: none. No step needs the SMB share. Do not use Y:, Z: or `\\192.168.1.10`.

## Look at (tests cannot judge these)

- Is the **accent frame** on the focused pane visible but not loud? Does it look right in both light and dark themes?
- Is the **6 px gap** between the panes enough to tell them apart?
- Does the focus change **at once** when you click in the other pane, with no flicker of the address bar?
- Do the **pane buttons** show the right one highlighted after every action (step 1, 7, 9, 10)?
- With different layouts per pane (step 6), does it feel clear which pane the Layout buttons control?
- Is anything **slow**: opening the split, switching focus, scrolling a big folder in one pane?

## Known edge cases

- The panes are always **half and half**. Dragging the gap to resize them is not built yet.
- The second pane has **no Sign in button**. A share that needs a sign-in shows "Sign-in required" with Try again; focus the first pane to sign in.
- The **offline banner** and the **drive card** sit above both panes and follow the focused one.
- **F5 and F6** do not copy or move to the other pane yet (F2); F5 still reloads the focused pane. There is also no swap-panes or same-folder command yet.
- The **preview pane** follows the focused pane's selection, but the Move and Copy buttons under it come with F3.
- Closing the app saves the split only when the session is saved normally; killing the process may lose the latest pane changes.
- Not verified on screen by the developer: everything in this playbook was written from the code and tests, so a step that does not behave as described is a bug to report.

## Result

Pass / fail / notes, and anything to tweak.

## Clean up

```powershell
Remove-Item -Recurse -Force "D:\Playbooks\data"
```
