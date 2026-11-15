# UAT: P5-A Duplicate files: identical and similar, review and clean up

**Goal:** find identical files (and look-alike pictures and videos) across folders, pick which copy to keep with a rule, and clean up the extra copies with one undoable action.
**Built in:** Phase 5 slices A1 to A4 (`docs/phase-5.md`), spec §15.2. The name-based finder (Alt+Shift+D) is in `H4-find-duplicates`; this is the content-based window (Alt+Shift+F).
**Time:** about 35 minutes. Similar videos need ffmpeg.

## Setup

1. Fixtures: `.\tools\New-UatFixtures.ps1`. Real duplicates are made in a scratch folder below, so no fixture is modified.
2. Make a duplicate set (PowerShell):
   ```powershell
   $dup = 'D:\Playbooks\uat\20-dup-review'
   New-Item -ItemType Directory -Force "$dup\a","$dup\b","$dup\library" | Out-Null
   Copy-Item 'D:\Playbooks\uat\01-basic\movie-small.mkv' "$dup\a\movie.mkv"
   Copy-Item 'D:\Playbooks\uat\01-basic\movie-small.mkv' "$dup\b\movie copy.mkv"
   Copy-Item 'D:\Playbooks\uat\01-basic\movie-small.mkv' "$dup\library\movie.mkv"
   Copy-Item 'D:\Playbooks\uat\01-basic\photo-small.jpg' "$dup\a\photo.jpg"
   Copy-Item 'D:\Playbooks\uat\01-basic\photo-small.jpg' "$dup\b\photo.jpg"
   Set-Content "$dup\a\unique.txt" 'only one of me'
   (Get-Item "$dup\a\movie.mkv").LastWriteTime = (Get-Date).AddDays(-60)
   # Same size, different bytes: must NOT be reported as duplicates.
   $bytes = [IO.File]::ReadAllBytes("$dup\a\photo.jpg"); $bytes[100] = $bytes[100] -bxor 0xFF
   [IO.File]::WriteAllBytes("$dup\b\photo-almost.jpg", $bytes)
   ```
3. For similar pictures, make two copies at different sizes with the preview fixtures' picture (`11-preview\picture.png`), for example resave one with Paint at half size into `$dup\b\picture-small.png` and copy the original to `$dup\a\picture.png`.
4. Scratch settings and start:
   ```powershell
   $env:LEVIATHAN_DATA_DIR = "D:\Playbooks\data"
   & .\src\Leviathan.App\bin\x64\Debug\net10.0-windows10.0.26100.0\Leviathan.exe
   ```
5. Open `D:\Playbooks\uat\20-dup-review`.

## Steps

### Identical files

| # | Do | Expect |
|---|----|--------|
| 1 | Press **Alt+Shift+F** (or run **Find duplicate files by contents…** from the palette). | A window **Duplicate files** opens and begins **Searching…** the folder shown and everything under it, in the worker. The window is usable while it searches; **Stop searching** stops it. |
| 2 | Wait for the result. | Groups: one of three `movie` files (**3 identical files · size each · bytes to free**) and one of two `photo.jpg` files. `unique.txt` and `photo-almost.jpg` are not listed. |
| 3 | Look at one group. | Each file shows name, folder, size, date (and resolution for pictures) with a thumbnail; one is marked **Keep** by a radio button. |
| 4 | Change the **Keep** rule: *Oldest copy*, *Newest copy*, *Largest file*, *Highest resolution*, *Copy in the preferred folder*. | The kept file in every group updates at once. The oldest rule keeps the file dated 60 days ago. A tie goes to the file found first, the same every time. |
| 5 | Choose *Copy in the preferred folder* and type `D:\Playbooks\uat\20-dup-review\library`. | The `library\movie.mkv` copy is kept. Groups with no copy there fall back to the other rules. |
| 6 | Click a different radio button in one group. | Only that group changes; the rest follow the rule. |
| 7 | Change the **Extra copies** action: *Move to the Recycle Bin*, *Move to a folder*, *Replace with a hard link*, *Put a tag on them*. | *Move to a folder* shows a **Move to** box; *Put a tag* shows a **Tag** picker (the colour tags); the others need nothing more. |
| 8 | Choose *Move to the Recycle Bin* and press **Apply**. | A confirmation: **Apply to the extra copies?** with the count. Confirm. |
| 9 | Look at the result. | The extra copies are in the Recycle Bin; one of each group stays where it was; the window says how many files were done. |
| 10 | Press **Undo last**. | The files come back to their folders. The message says **Undone. Search again to see the groups as they are now.** |
| 11 | Press **Search again**. | The groups are found again. |
| 12 | Choose *Move to a folder* with `D:\Playbooks\uat\20-dup-review\moved`, **Apply**. | The extras move there (name clashes get numbered names). **Undo last** puts them back. |
| 13 | Choose *Replace with a hard link* and **Apply** (same drive). | Each extra becomes a hard link to the kept file: same content, one copy on disk (`fsutil hardlink list` shows both names). **Undo last** restores independent files. On another drive it refuses with a reason. |
| 14 | Choose *Put a tag on them* and **Apply**. | The extras get the chosen colour tag and nothing is moved. With no tags defined it says **There is no tag to put on the copies.** |

### Similar pictures and videos

| # | Do | Expect |
|---|----|--------|
| 15 | Change the first drop-down from **Identical files** to **Similar pictures**. | A **Tolerance (0 to 20)** box appears (default 6). Press **Find** / **Search again**. |
| 16 | Read the result. | `picture.png` and `picture-small.png` are one group of **similar files** (no "identical"); `photo.jpg` and `photo-almost.jpg` may also group because they look alike. Groups say "N similar files · bytes in the extra copies". |
| 17 | Set the tolerance to 0, search again, then to 20. | 0 finds only near-exact looks; 20 groups more loosely (and more wrongly). |
| 18 | Choose **Similar pictures and videos** (needs ffmpeg) with two clips of the same video at different quality (`ffmpeg -i short-3s.mp4 -vf scale=160:-1 small.mp4`). | The two clips form a group; video frames show as thumbnails. |
| 19 | Try **Replace with a hard link** on a similar group. | It is not offered or is refused: look-alikes are different files by definition. |

### Scope and safety

| # | Do | Expect |
|---|----|--------|
| 20 | Open the window from a **split tab** or with several folders chosen (if the window offers a scope). | Only the folders shown are searched; the list names them. Folders that are not on this PC or a Windows share are refused with a message. |
| 21 | Run it on `D:\Playbooks\uat\06-ten-thousand` (10,000 files of 1 byte, all identical `x`). | 10,000 files in one big group; the window stays responsive; **Apply** asks for confirmation naming 9,999 extras. **Do not apply**: cancel. |
| 22 | Stop the search part way (**Stop searching**). | It says **Search stopped.**; results found so far remain; nothing is changed. |

## Look at (tests cannot judge these)

- Is each group easy to read: can you see **which file is kept and why**? Are thumbnails useful?
- Is the confirmation wording clear about what happens to which files?
- Does the window stay smooth with a very large group?
- Are 6 as the default tolerance and the 0 to 20 range sensible for your photos?

## Known edge cases

- Identical means identical bytes, found by size, then a hash of the first and last megabyte, then a full hash only for the rest. Different files with the same size and edges are still told apart by the full hash.
- Hard links work only on one NTFS volume; they do not apply to network shares that do not support them.
- Undo covers the **last** apply only.
- The Recycle Bin action puts files in the Windows bin; on a share it follows the soft-delete rules (P2-D).
- Similar-video matching uses several frames and the duration; short or very dark clips may group wrongly.

## Result

Pass / fail / notes, and anything to tweak.

## Clean up

```powershell
Remove-Item -Recurse -Force "D:\Playbooks\uat\20-dup-review","D:\Playbooks\data"
```
