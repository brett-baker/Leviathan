# UAT: P1-C File views, sorting, grouping, filtering, selection and the column chooser

**Goal:** read any folder in Details, List or Thumbnails, sort, group and filter it, select and rename with the keyboard and mouse, and choose columns.
**Built in:** Phase 1 slices B3 to B5, D4, D5, D8, D10, I3 (`docs/phase-1.md`), spec §8.
**Time:** about 35 minutes.

## Setup

1. Fixtures: `.\tools\New-UatFixtures.ps1` (folders `01-basic`, `02-odd-names`, `04-attributes`, `06-ten-thousand`, `11-preview`).
2. Scratch settings and start:
   ```powershell
   $env:LEVIATHAN_DATA_DIR = "D:\Playbooks\data"
   & .\src\Leviathan.App\bin\x64\Debug\net10.0-windows10.0.26100.0\Leviathan.exe
   ```

## Steps

### Listing and layouts

| # | Do | Expect |
|---|----|--------|
| 1 | Open `06-ten-thousand` (10,000 files). | The first rows appear at once and the rest stream in; the window never freezes. The status bar counts up to `10,000 files`. Scroll fast with the wheel and by dragging the scrollbar: smooth. |
| 2 | Open `01-basic`. Press **Ctrl+Shift+6**, **Ctrl+Shift+5**, **Ctrl+Shift+2**. | Details, List (names in columns, scrolls sideways), Thumbnails (wrapping grid, scrolls down). The selection survives the switch. |
| 3 | In Thumbnails, hold **Ctrl** and use the mouse wheel. | Thumbnails resize between 64 and 512 px. The status-bar thumbnail slider moves with it. |
| 4 | Open `11-preview`. In Thumbnails look at the pictures. | `picture.png` and `photo.jpg` show real thumbnails the right way up (not upside down). Other files show icons. |
| 5 | Use the **layout switcher** in the status bar. | Same as the keys. The layout is remembered per tab and per folder. |
| 6 | Open `04-attributes`. | `hidden.txt` and `system-hidden.txt` are not shown. Press **Ctrl+H**: they appear (greyed). Press it again to hide. The empty files and the 6 GiB sparse file show correct sizes. |
| 7 | Open a folder that is empty (`01-basic\Empty folder`). | An empty state, not a blank grid. |
| 8 | Open a folder you cannot read (for example `C:\System Volume Information`). | A clear "Access denied" state with Try again. |

### Details grid

| # | Do | Expect |
|---|----|--------|
| 9 | Click the **Name** header, again, then **Size**. | Sorts ascending, descending (the arrow flips), then by size. **Shift+click** another header adds a secondary sort. |
| 10 | Drag a header's right edge. Double-click it. | The column resizes, then auto-fits its content. |
| 11 | Drag a header to a new place. | Columns reorder. Restart or open a new tab on the same folder: widths and order stay. |
| 12 | Right-click a header. | A list of columns with ticks. **Name** cannot be hidden. |
| 13 | Click **More columns…** at the bottom of that list (or View > **Choose columns**). | The chooser opens with groups (File system, Leviathan, Media, Image, Audio, Document), a search box, tick boxes, order controls, **Reset to default** and **Apply to all folders of this type**. Columns whose data is not available yet are greyed. |
| 14 | Tick *Date created*, drag it to the second place, **Apply**. | The column appears there. |
| 15 | Open the chooser again and use **Reset to default**. | The default columns return. |

### Sort, group, filter

| # | Do | Expect |
|---|----|--------|
| 16 | View > **Group by** (Type, Date…) and **Sort direction**. | Group headers appear and the rows regroup. |
| 17 | Press **Ctrl+Shift+F** and type `note` in the filter box. | Only matching rows remain; the status bar shows the filter state. Click that part of the status bar to clear the filter. |
| 18 | Press **Ctrl+V** with text on the clipboard while the filter box has focus. | The text is pasted into the box (not files into the folder). |

### Selection and keyboard

| # | Do | Expect |
|---|----|--------|
| 19 | Click a row, **Ctrl+click** another, **Shift+click** a third. | Standard multi-select. The status bar shows the count and total size; click that part to select none. |
| 20 | **Ctrl+A**, **Ctrl+Shift+A**, **Ctrl+I**. | Select all, select none, invert. |
| 21 | Run **Select by pattern** from the palette (Ctrl+Shift+P) with `*.txt`. | Only matching items are selected. |
| 22 | Press **Down**, **Up**, **Home**, **End**, **PageDown** in Details, then in List and Thumbnails. | Movement follows each layout's direction; moving across a short last line lands on the last item. |
| 23 | Type `rep` quickly. | The selection jumps to the first name starting with `rep` (type-ahead). |
| 24 | Drag on blank space. | A rubber band selects what it crosses. |
| 25 | Press **Enter** on a folder and on a `.txt`. | The folder opens; the file opens in its default program. |

### Rename and new items

| # | Do | Expect |
|---|----|--------|
| 26 | Select `notes.txt`, press **F2**. | An edit box opens with the name selected **without** the extension. |
| 27 | Type a new name, press **Enter**. | Renamed. **Esc** instead cancels. **Tab** renames and moves on to the next item. |
| 28 | Rename to a name Windows refuses (`a:b`). | The box stays open with the reason; nothing changes on disk. |
| 29 | Click a selected file once, wait, click it again slowly. | Slow-click starts a rename. |
| 30 | Press **Ctrl+Shift+N**. | A new folder is created, selected and its name box opens. |
| 31 | Use the command bar **New** menu. | Built-in, template and user templates (Word, text…) plus **Manage templates…**. A new text file opens with its name box focused. |

### Status bar

| # | Do | Expect |
|---|----|--------|
| 32 | Read the status bar in `01-basic`, then select two files. | Item count (folders, files), then selection count and size. Click the item count: hidden items toggle. |
| 33 | Look at the free-space part. | A bar and the free amount of the current drive. |

## Look at (tests cannot judge these)

- Scrolling the 10,000-row folder: any stutter, blank rows or flicker?
- Row height and spacing in Compact and Comfortable density, in both themes.
- Do thumbnails load quietly without the grid jumping?
- Is the rename box well placed and the extension handling natural?

## Known edge cases

- Folder sizes appear later (the worker counts them); stale values are italic. Sorting by size counts folders by their real numbers once counted.
- Network folders show no folder sizes unless you opt in.
- A cached listing shows first and is then checked against the disk; a file that changed shows after a moment.
- Paste as shortcut, hard link or symbolic link is greyed with a tooltip.

## Result

Pass / fail / notes, and anything to tweak.

## Clean up

```powershell
Remove-Item -Recurse -Force "D:\Playbooks\data"
```
