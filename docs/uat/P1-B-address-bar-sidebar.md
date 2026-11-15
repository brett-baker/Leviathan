# UAT: P1-B Address bar, history, sidebar tree, favourites and the drive card

**Goal:** get anywhere by breadcrumb, typed path, history or the sidebar, pin folders as favourites, and read a drive's summary.
**Built in:** Phase 1 slices B1, B2, D3, D6, D11, I1, I2 (`docs/phase-1.md`), spec §5.2, §7, §8.7.
**Time:** about 25 minutes.

## Setup

1. Fixtures: `.\tools\New-UatFixtures.ps1` (folders `01-basic`, `02-odd-names`, `03-long-paths`).
2. Scratch settings (so your real favourites stay untouched):
   ```powershell
   $env:LEVIATHAN_DATA_DIR = "D:\Playbooks\data"
   & .\src\Leviathan.App\bin\x64\Debug\net10.0-windows10.0.26100.0\Leviathan.exe
   ```

## Steps

### Breadcrumb and typed addresses

| # | Do | Expect |
|---|----|--------|
| 1 | Click empty space at the right of the address bar, or press **Ctrl+L** (or **Alt+D**). | The bar turns into a text box with the path selected. |
| 2 | Type `D:\Playbooks\uat\01-b` and wait. | Suggestions appear from the typed folder, recent folders and favourites. **Tab** accepts the top one. |
| 3 | Press **Enter**. | The folder opens and the bar returns to breadcrumb form. **Esc** instead leaves edit mode without navigating. |
| 4 | Type `%USERPROFILE%` and Enter. | Environment variables expand in typed addresses and your profile folder opens. |
| 5 | Type a path that does not exist, then Enter. | A clear "not found" state with a **Try again** button, not an error dialog. The previous folder is kept in history. |
| 6 | Click a breadcrumb segment such as `uat`. | That folder opens. **Middle-click** or **Ctrl+click** a segment opens it in a new tab. |
| 7 | Click the **chevron** after a segment. | A list of that folder's subfolders (siblings of the next segment) opens; typing filters it; choosing one navigates. |
| 8 | Narrow the window until the path no longer fits. | The first segments collapse into a **"…"** menu; the last ones stay. |
| 9 | Right-click the bar. | Copy path, Copy as UNC, Copy as quoted path, Copy as file:// URI. Paste each into a text file to check. |
| 10 | Open `D:\Playbooks\uat\03-long-paths`, drill to the bottom (over 260 characters). | Every level opens; the bar shows the whole path (trimmed in the middle). |

### History

| # | Do | Expect |
|---|----|--------|
| 11 | Visit three folders, press **Alt+Left** twice, then **Alt+Right**. | Back, back, forward. The forward list is dropped when you navigate somewhere new. |
| 12 | Press **Alt+Up**. | The parent folder opens. |
| 13 | Press **F5** (reload). | The folder lists again; a changed file appears. |
| 14 | Use the mouse **back/forward side buttons** if you have them. | Same as Alt+Left / Alt+Right. |

### Favourites

| # | Do | Expect |
|---|----|--------|
| 15 | Go to `01-basic`. Click the **star** at the right end of the address bar (or press **Ctrl+D**). | The star fills, the folder appears under **Favorites** in the sidebar, and the star's tooltip says "Remove from Favorites". |
| 16 | Right-click the star. | Rename, Icon (12 icons), Colour (9 colours and default) and Remove. Pick an icon and colour: the star and the sidebar row both change. |
| 17 | Press **Ctrl+D** again. | The favourite is removed; the star outlines. |
| 18 | Drag a folder from the file list onto the **Favorites** header. | It is pinned. |
| 19 | Close and restart the app. | Favourites, icons and colours are still there (`favorites.json` in the data folder). |

### Sidebar

| # | Do | Expect |
|---|----|--------|
| 20 | Look at the sections. | Favorites, Common folders, Tags, Local drives (each with a usage bar, red when nearly full), Network, Internet. |
| 21 | Click the arrow of a drive, then of a folder. | A spinner row shows at once, then the subfolders (loaded 40 at a time with no stall). A folder with no subfolders loses its arrow once checked. |
| 22 | Navigate in the file list to a deep folder. | The tree expands along the way down and highlights the current folder. |
| 23 | With a tree row selected, press **Right** and **Left**. | Expand and collapse. |
| 24 | Middle-click a tree row, or right-click it and choose open in a new tab. | It opens in a background tab. |
| 25 | Drag the **splitter** between the tree and the file view. | The sidebar resizes 1:1 between 140 px and the smaller of 520 px and half the window. Dragging far left collapses it; dragging out restores it. |
| 26 | Focus the splitter (Tab to it). Press **Left**, **Right**, **Home**, **Enter**. | Moves by 16 px each, resets to 220 px, and toggles collapse. A highlight shows the focus. |
| 27 | Press **Ctrl+B**. | The sidebar hides and shows. Restart: the width is remembered. |

### Drive summary card

| # | Do | Expect |
|---|----|--------|
| 28 | Open the root of `D:\`. | A summary card above the list: name, type, file system, SSD or HDD, a usage bar with used, free and total (warning colour under 15% free), **Open terminal here** and **Properties**. |
| 29 | Click the card's hide button, then run **View > Drive summary** (or the palette command "Fold or unfold the drive and folder summary"). | The card hides for that drive only and comes back. |

## Look at (tests cannot judge these)

- Does the breadcrumb feel as quick as Explorer's? Do suggestions appear without delay?
- Are the **sidebar usage bars** and the red warning clear in both themes?
- Is the **star** easy to find and to understand? Do the 12 icons and 9 colours look good?
- Is the spinner row when expanding a big drive reassuring or flickery?

## Known edge cases

- Dropping files on a breadcrumb segment is covered in the drag and drop playbook (P1-D).
- Network paths are never listed just to follow the view; expanding a share is explicit.
- Reordering favourites by drag is not built.
- The Recycle Bin and Settings entries are not in the tree; the Soft-delete bin lives in the status hub.

## Result

Pass / fail / notes, and anything to tweak.

## Clean up

```powershell
Remove-Item -Recurse -Force "D:\Playbooks\data"
```
