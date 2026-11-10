# UAT: F1 to F3g Colour tags

**Goal:** tag files with the keys 1 to 9, see the tags everywhere (column, badges, details pane, sidebar), filter and list by tag, edit the tag set, and be warned when a copy would lose tags.
**Built in:** commits `7606018` (F1 keys and set), `fbc5118` (F2 storage and index), `a4ba820` (F3a column and `tag:` filter), `01dabeb` (F3b badges), `3e144cd` (F3c sidebar), `3baebe3` (F3d tint), `3a1f085` (F3e details pane), `4f35b3f` (F3f warning), `afe5dbe` (F3g Tags page), spec §11.
**Time:** about 40 minutes (the FAT disk in part 6 adds 10).

## Setup

1. Build the fixtures (never test with real data; use `pwsh`):
   ```powershell
   pwsh .\tools\New-UatFixtures.ps1
   ```
   Uses `D:\Playbooks\uat\13-tags`: `alpha.txt`, `bravo.txt`, `charlie.txt`, `delta.txt`, `echo.txt`, `golf.jpg` and `sub\foxtrot.txt`.
2. Start the app with scratch settings (the tag set and the tag index live in this data folder):
   ```powershell
   $env:LEVIATHAN_DATA_DIR = "D:\Playbooks\data"
   & .\src\Leviathan.App\bin\x64\Debug\net10.0-windows10.0.26100.0\Leviathan.exe
   ```
3. Open `D:\Playbooks\uat\13-tags`. Show the Tags column (right-click a column header → Tags) if it is not there, and show the pane (**Alt+P**).
4. To look at a file's tag stream by hand:
   ```powershell
   Get-Content "D:\Playbooks\uat\13-tags\alpha.txt:leviathan.tags"
   ```

## Part 1: tagging with keys (F1)

| # | Do | Expect |
|---|----|--------|
| 1 | Click `alpha.txt`, press **1**. | The status bar says it tagged the file (for example "Tagged alpha.txt Red"). The Tags column shows a red dot. |
| 2 | Run the `Get-Content …alpha.txt:leviathan.tags` command. | JSON such as `{"v":1,"tags":[1],…}`. The file's **Modified** date in the list did not change. |
| 3 | Press **4** on the same file. | A red and a green dot, in the order of the Tags page (red first). |
| 4 | Press **1** again. | Red goes away; green stays. |
| 5 | Select `bravo.txt`, `charlie.txt`, `delta.txt` (Ctrl+click) and press **5**. | All three get blue. Press **5** again: blue is removed from all three. |
| 6 | Tag only `bravo.txt` with **5**, then select all three and press **5**. | The missing files get blue; none lose it (a group toggle turns off only when every selected file has the tag). |
| 7 | Select `alpha.txt` and press **Ctrl+0**. | Every tag is cleared from it. |
| 8 | Click the folder `sub`, press **2**. | Nothing is tagged and the status bar says folders are skipped. |
| 9 | Click in the list and type the letters `ch` quickly, then a digit `1`. | The first two keys jump to `charlie.txt`; the `1` is part of the type-ahead word (it does not tag) while you are still typing. After a pause, **1** tags again. |
| 10 | Press **8** or **9** while only 7 tags exist. | Nothing happens (no such tag) and no error. |
| 11 | Open `10-triage\inbox` (needs ffmpeg), start triage on a clip, press **Enter** (Keep), close. | The kept clip has the green tag (key 4's tag, "Green"). |

## Part 2: the Tags column, the filter and the badges (F3a, F3b, F3d)

| # | Do | Expect |
|---|----|--------|
| 12 | Tag `alpha.txt` Red, `bravo.txt` Green and Blue, `charlie.txt` Red. | Dots appear within a fraction of a second in the Tags column, one per tag, in the order of the Tags page. Hover a cell: the tip lists the names. |
| 13 | Click the filter box above the list and type `tag:red`. | Only `alpha.txt` and `charlie.txt` remain (all files are read, so rows may take a moment to appear). |
| 14 | Change it to `-tag:red`. | Everything except those two. |
| 15 | Type `tag:RED` and then `tag:gr`. | Case does not matter; the first matches the red files again. Try `tag:gr` too and note what it does (whole-name match is the intent). Clear the filter. |
| 16 | Hide the Tags column, then switch to **Thumbnails** (layout switcher). | Each tagged file's picture has small coloured dots in its bottom-right corner, even though the column is hidden. Hover the dots: the names. |
| 17 | Switch to **Tiles**, **Gallery**, **List**. | Dots on Tiles and Gallery; **List** shows no dots. Switch back to Details. |
| 18 | Open the Tags page (see part 5) and turn on **Tint the rows of tagged files**. | Tagged rows get a faint wash of their first tag's colour in Details and in Thumbnails; untagged rows stay plain. A selected or hovered row shows the selection colour, not the wash. Turn it off again. |
| 19 | Open a folder with many files (`06-ten-thousand`) with the Tags column shown; scroll quickly. | Scrolling stays smooth; no tags are read for rows that scrolled past; the column stays empty there. |

## Part 3: the details pane and the sidebar (F3c, F3e)

| # | Do | Expect |
|---|----|--------|
| 20 | Click `bravo.txt` with the pane open. | Under **General** a line with a green dot and a blue dot and their names ("Green  Blue"). A file with no tags shows no such line. |
| 21 | With `bravo.txt` selected press **Ctrl+0**. | The line in the pane disappears at once. Press **4**: it comes back. |
| 22 | Scroll the sidebar to the **Tags** section. | One row per tag in the Tags-page order, each with a coloured dot and no expander arrow. |
| 23 | Click **Red**. | The tab opens a virtual folder (address `shell:tag\1`) listing `alpha.txt` and `charlie.txt` from wherever they are (try tagging a file in `01-basic` too: it appears here as well). |
| 24 | Double-click a file in that list. | It opens in its own program, as in its folder. |
| 25 | Tag a file Red, close the app, rename the file in Explorer, start the app, open the Red tag folder, then open the file's own folder once. | In the tag folder the old name is gone at first (the file is not there any more). After the folder was shown once, the renamed file is listed in the Red tag folder (its tag was found again by file identity). |
| 26 | Delete a tagged file in Explorer (not the Recycle Bin: **Shift+Delete**) and reopen the Red tag folder. | The vanished file is simply not listed. |
| 27 | Click the **Up** button in the tag folder. | An empty folder, no error. |

## Part 4: storage and where tags live (F2)

| # | Do | Expect |
|---|----|--------|
| 28 | Close the app, start it again with the same data folder, open `13-tags`. | All the tags are still there. |
| 29 | Delete the data folder's database (`Remove-Item "D:\Playbooks\data\leviathan.db*"` while the app is closed), start the app, open `13-tags`. | The tags still show, because the stream on each file is the source of truth; the Red tag folder is empty until the files have been shown once (it refills as tags are read). |
| 30 | Copy a tagged file within the same NTFS drive with Leviathan (**Ctrl+C**, **Ctrl+V**). | The copy keeps its tags (the stream is copied). |
| 31 | `Get-Content` of the copy's stream, as in the setup. | The same JSON. |

## Part 5: the Tags page (F3g)

Open Settings (**Ctrl+,**) → **Tags**.

| # | Do | Expect |
|---|----|--------|
| 32 | Read the page. | A row per tag: the key it has (1 to 9), a colour dot, an editable name, and up and down buttons. Below: **Add a tag**, **Back to the standard colours and names**, the tint switch. |
| 33 | Rename **Red** to `Urgent` and press **Enter**. | The sidebar's Tags section, the Tags-column tips and the details pane use `Urgent` at once. |
| 34 | Clear a name box and press **Tab**. | The old name comes back; a tag always has a name. |
| 35 | Click a colour dot, pick **Teal**. | The dot changes there, in the sidebar, in the Tags column and on badges. |
| 36 | Move **Blue** to the top with the up buttons. | Blue is now key **1**. Files already tagged blue are still blue; pressing **1** now toggles blue. Move it back. |
| 37 | Press **Add a tag**. | A new "New tag" at the bottom with no key (beyond 9) or the next key; it can be renamed and used. |
| 38 | Keep adding until there are 32 tags. | **Add a tag** is disabled at 32. |
| 39 | Press **Back to the standard colours and names**. | The first seven are the standard names, colours and order; tags you added stay below them with their names. |
| 40 | Look for a delete control. | There is none (deliberate: ids are never reused). |
| 41 | Search the settings for `recolour`. | The search finds the Tags page. |

## Part 6: tags may be lost (F3f) and drives without streams (F2) [lab]

Needs a drive that cannot hold streams. Use a small FAT disk file (elevated PowerShell; pick a drive letter that is free):

```powershell
@"
create vdisk file="D:\Playbooks\fat.vhd" maximum=64 type=expandable
attach vdisk
create partition primary
format fs=fat32 quick label=LEVFAT
assign letter=T
"@ | Set-Content D:\Playbooks\fat.txt; diskpart /s D:\Playbooks\fat.txt
```

| # | Do | Expect |
|---|----|--------|
| 42 | Tag `alpha.txt` Red. Select it and press **Ctrl+C**, open `T:\` and press **Ctrl+V**. | The copy starts, and the status bar says "Tags may be lost: T:\ cannot keep colour tags." (the folder's name). |
| 43 | Look at the copy on `T:\`. | It has no stream: `Get-Content T:\alpha.txt:leviathan.tags` fails. |
| 44 | Copy an **untagged** file there. | No warning. |
| 45 | Copy a tagged file to another NTFS folder. | No warning. |
| 46 | **Shift** the paste of a tagged file to `T:\` (move). | The same warning (a move to a drive without streams also loses them). |
| 47 | Paste a tagged file as a shortcut (Alt-drag). | No warning. |
| 48 | On `T:\`, click `alpha.txt` and press **1**. | The tag is accepted, shown, and survives restarting the app (it is kept in the app's index, because the disk has no streams). |
| 49 | Close the app, rename the file in Explorer, start the app and look at `T:\`. | The renamed file's tag follows only if the disk has file IDs; on FAT it does not (it matches by path, size and time to the second). Note what you see. |

Remove the disk afterwards:

```powershell
@"
select vdisk file="D:\Playbooks\fat.vhd"
detach vdisk
"@ | Set-Content D:\Playbooks\fat.txt; diskpart /s D:\Playbooks\fat.txt; Remove-Item D:\Playbooks\fat.vhd, D:\Playbooks\fat.txt
```

## Look at (tests cannot judge these)

- Do the seven colours read clearly as dots, on light and dark theme, and in the small Tags column?
- Are the badges too big or too small on thumbnails of 64, 128 and 256 px?
- Is the tint pleasant, or too strong over the selection colour?
- Is the speed of the Tags column and the `tag:` filter acceptable on a folder of 10,000 files?
- Wording of the "Tags may be lost" note, and whether the status bar is the right place for it.
- Does the Tags page feel finished (alignment, the colour flyout, the way a rename saves)?

## Known edge cases

- **Not built:** import of the Files app's tag stream, and writing the Windows "Tags" property.
- Folders cannot be tagged.
- The Tags sidebar section lists only files whose tags Leviathan has read or written; a file tagged by another program shows up after it has been shown once.
- The warning checks only the dragged or pasted files themselves (the first 200), not what is inside dropped folders.
- A destination that does not answer within 2 seconds counts as "unknown" and gets no warning.
- Tags and the tint setting can still be edited in `settings.json`; the Tags page and the file agree.
- Deleting a tag is deliberately not possible.
- Nothing here was seen on a real NAS share: use `\\localhost\LeviathanUat` (`-Share`, elevated) for the SMB behaviour; whether a NAS supports streams depends on its file system.

## Result

Pass / fail / notes, and anything to tweak.

## Clean up

```powershell
Remove-Item -Recurse -Force "D:\Playbooks\data"
pwsh .\tools\New-UatFixtures.ps1   # rebuilds the fixtures with untagged files
```
