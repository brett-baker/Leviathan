# UAT: P5-D Compare and sync folders, and saved sync pairs

**Goal:** compare two folders, see exactly what a sync would do (always a dry run first), run it through the queue, and save a pair you can re-run with one click.
**Built in:** Phase 5 slices D1 to D3 (`docs/phase-5.md`), spec §15.5.
**Time:** about 40 minutes.

## Setup

1. Fixtures: `.\tools\New-UatFixtures.ps1` (folder `18-sync` with `left`, `right`, and an empty `saved-pair-target`). It holds every kind of difference:
   - `same.txt` identical; `only-left.txt`; `only-right.txt`;
   - `newer-left.txt` (left newer, longer), `newer-right.txt` (right newer, longer);
   - `looks-same.txt` (same size and time, **different bytes**: only a checksum compare sees it);
   - `sub\both.txt` identical, `sub\inner-left.txt` left only; `only-right-folder\x.txt`.
2. Scratch settings and start:
   ```powershell
   $env:LEVIATHAN_DATA_DIR = "D:\Playbooks\data"
   & .\src\Leviathan.App\bin\x64\Debug\net10.0-windows10.0.26100.0\Leviathan.exe
   ```
3. Open `D:\Playbooks\uat\18-sync` (split the tab with `left` and `right` if you like), then run **Compare and sync folders…** from the palette (**Ctrl+Shift+P**) or the right-click menu. The window opens as **Compare and sync folders** (with the folders pre-filled from a split tab, otherwise type them).

## Steps

### Compare

| # | Do | Expect |
|---|----|--------|
| 1 | Set **Left folder** `D:\Playbooks\uat\18-sync\left` and **Right folder** `D:\Playbooks\uat\18-sync\right`. Use **Browse…** for one of them. | The picker opens and fills the box. |
| 2 | Leave **Compare by** at *Name, size and date* and **Same time within (seconds)** at 2. Press **Compare**. | "Comparing…" then a summary such as `N items · M identical`. A table lists each path with a status: **Left only, Right only, Newer left, Newer right, Different, File vs folder, Identical** with a glyph. |
| 3 | Check the rows against the fixtures. | `only-left.txt` Left only; `only-right.txt` Right only; `newer-left.txt` Newer left; `newer-right.txt` Newer right; `same.txt` and `sub\both.txt` Identical; `sub` folder and `only-right-folder` appear; **`looks-same.txt` is Identical** (same size and time). |
| 4 | Change **Compare by** to *Contents (checksum; slower)* and press **Compare** again. | `looks-same.txt` is now **Different**. |
| 5 | Use the **Show** filter to show only some statuses (for example only differences). | Rows filter; the count stays visible. |
| 6 | Change the tolerance to 0, then 3600, and compare. | At 3600 s files within an hour count as the same time; at 0 only exact times. |
| 7 | Press **Compare** and then **Stop** during a big compare (compare `06-ten-thousand` with a copy). | "Stopped."; nothing is changed. |
| 8 | Compare a folder with a path that does not exist or with an SFTP folder. | A message: both must be folders on this PC or on a Windows share. |

### Sync modes (dry run first)

| # | Do | Expect |
|---|----|--------|
| 9 | Open the **Sync mode** list. | Mirror left → right; Mirror right → left; Two-way (newer wins; conflicts are flagged; nothing is deleted); Update right from left; Update left from right. Each says what it does and whether it deletes. |
| 10 | Choose **Update right from left** and press **Sync…**. | A confirmation **Run this sync?** that describes the plan: "Copy N items (size) to the right folder." It lists `only-left.txt`, `newer-left.txt`, `sub\inner-left.txt`, and **nothing is deleted or overwritten with an older file**. Press **Cancel**: nothing changes. |
| 11 | Press **Sync…** again and confirm **Sync**. | The copies are queued as jobs (Operations panel); the status says "Started …". After they finish, **Compare** again: the left-only items are now identical; `only-right.txt` is untouched. |
| 12 | Rebuild the fixtures (`New-UatFixtures.ps1`) and choose **Mirror left → right**; press **Sync…**. | The plan also says it will **delete** what only the right has (`only-right.txt`, `only-right-folder`) and warns "N newer files will be overwritten with older ones" if a right file is newer (`newer-right.txt`). The deletions go to the **Recycle Bin or the share's soft delete**, not permanently. |
| 13 | Confirm the mirror. | The right folder matches the left. Check the deleted items in the Recycle Bin and restore one with **Ctrl+Z** or from the bin. |
| 14 | Rebuild fixtures and choose **Two-way**. | The plan copies newer files each way (`newer-left.txt` to the right, `newer-right.txt` to the left) and the one-sided files to the other side; **nothing is deleted**. Equal-time different files (`looks-same.txt`, with checksum) are **conflicts**: "left alone (same time, different contents): choose a side for them above." |
| 15 | Select a conflict row; use **For the selected items**: **Skip**, **Left → right**, **Right → left**, **Use the mode**. | The row's action text shows `[your choice: …]` and the plan description updates. Choose Left → right for `looks-same.txt` and sync: the right file gets the left bytes. |
| 16 | Choose **Mirror right → left** and **Update left from right** and repeat the dry runs. | The mirror images of steps 10 and 12. |
| 17 | After a sync, **change a folder** outside the app and press **Sync…** without comparing again. | "The folders or the way of comparing changed: compare again before syncing." |
| 18 | Try a mirror where the plan has zero items. | "Nothing to do: the folders already match for this mode." |
| 19 | Sync across a share: left `D:\Playbooks\uat\18-sync\left`, right `\\localhost\LeviathanUat\18-sync\saved-pair-target` (share from P2-A). | It works the same; copies are verified. |

### Saved pairs

| # | Do | Expect |
|---|----|--------|
| 20 | With `left` and `saved-pair-target`, press **Save as sync pair…**. | A **Sync pair** dialog with **Name** (default `left ⇄ saved-pair-target`) and **Check for changes** (how often). **Sync on its own after a check** is enabled **only** for an update mode. |
| 21 | Choose a Mirror mode: look at the auto checkbox. | It is disabled with the explanation that only an update (new and newer, never deleting) can run without you. |
| 22 | Choose *Update right from left*, save with a short interval (the shortest offered). | "Saved "left ⇄ saved-pair-target"; it is in Favorites." The pair appears in the sidebar **Favorites**. |
| 23 | Click the pair in Favorites. | The window opens as **Sync pair: …** with the folders and mode set; **Compare** and **Sync…** are one click away. The button now says **Save changes to this pair…**, and **Remove this pair** shows. |
| 24 | Add a file to `left` and wait for the interval, with **Sync on its own** ticked. | After the check the file is copied without you pressing anything, as a background job, with a note in Activity. |
| 25 | Close and restart the app. | The pair and its schedule are still there. |
| 26 | Press **Remove this pair**. | It leaves Favorites. |

## Look at (tests cannot judge these)

- Is the **plan description** in the confirmation understandable for hundreds of items? Is it obvious what will be deleted?
- Are the **status glyphs** readable; do Newer left and Newer right read clearly?
- Is the dialog's wording for the five modes and the auto-sync checkbox clear?
- Speed of comparing 10,000 files by name, size and date and by checksum.

## Known edge cases

- A mirror makes the target match even when the target has newer files; the plan says how many newer files it will overwrite, so read it.
- Compare by checksum reads every file on both sides, which is slow for large files on a network.
- Both folders must be on this PC or a Windows share (no SFTP or FTP sync yet).
- A folder that exists only on one side is copied or removed as a whole.
- Scheduled checks run only while the app (and its worker) are running.

## Result

Pass / fail / notes, and anything to tweak.

## Clean up

```powershell
Remove-Item -Recurse -Force "D:\Playbooks\data"
.\tools\New-UatFixtures.ps1
```
