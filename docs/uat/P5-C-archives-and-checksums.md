# UAT: P5-C Archives (browse, extract, compress) and checksums

**Goal:** open zip, 7z, tar and other archives like folders, extract and create them through the queue, and compute and verify checksums for any files.
**Built in:** Phase 5 slices C1 to C3 (`docs/phase-5.md`), spec §15.4.
**Time:** about 40 minutes. 7z and rar need the 7-Zip library that ships with the app; `.7z` fixtures need `7z.exe` when you build them.

## Setup

1. Fixtures: `.\tools\New-UatFixtures.ps1` (folder `17-archives`: `to-compress` (four files, one with a Unicode name, one 300 KB binary, a nested folder), `ready.zip`, `ready.tar`, `ready.tar.gz`, `ready.7z` if 7-Zip is installed, an empty `extract-here`, and `sums` with `one.bin`, `two.bin`, `three.txt`, `good.sha256` and `bad.sha256`).
2. Scratch settings and start:
   ```powershell
   $env:LEVIATHAN_DATA_DIR = "D:\Playbooks\data"
   & .\src\Leviathan.App\bin\x64\Debug\net10.0-windows10.0.26100.0\Leviathan.exe
   ```
3. Open `D:\Playbooks\uat\17-archives`.

## Steps

### Browsing an archive as a folder

| # | Do | Expect |
|---|----|--------|
| 1 | Double-click `ready.zip`. | It opens **as a folder**: `readme.txt`, `data`, `ünïcödé.txt`. The address bar shows the archive in the path. The unicode name is correct. |
| 2 | Open `data` and `data\nested`. | Folders inside the archive open; sizes and dates show. Sorting, layouts and the filter work as in any folder. |
| 3 | Select `readme.txt` and open the **preview** (Alt+P or Space). | The text preview shows its content straight from the archive. |
| 4 | Repeat for `ready.tar`, `ready.tar.gz` and `ready.7z`. | Each opens as a folder with the same content. |
| 5 | Open the archive in a **new tab** (middle-click) and go **Up** (Alt+Up). | You return to `17-archives`. |
| 6 | Try to rename, delete or paste into an archive folder. | These are refused or hidden (archives are read-only to browse); nothing inside the archive changes. |
| 7 | Drag `readme.txt` from the open archive into `extract-here`. | The file is extracted there (a copy job). |

### Extract

| # | Do | Expect |
|---|----|--------|
| 8 | Select `ready.zip`, right-click or use the palette: **Extract here**. | A job runs through the queue (Operations tab, with progress). The contents appear **in the current folder**. Remove them afterwards. |
| 9 | Select `ready.zip`: **Extract to folder**. | A new folder named after the archive (`ready`) holds the contents. |
| 10 | Select it: **Extract to…** and pick `extract-here`. | The contents go there. |
| 11 | Extract the same archive into `extract-here` again. | The conflict dialog (P2-C) opens for the clashing names. |
| 12 | Select two archives and **Extract to folder**. | One folder per archive, in one job. |
| 13 | Compare the extracted files with `to-compress`: `Get-ChildItem ... -Recurse \| Get-FileHash`. | Identical bytes; the Unicode name is intact. |
| 14 | Cancel an extraction half way (use the bandwidth limit to slow it). | The job ends; no half-written files remain (`*.leviathan-part` are cleaned). |

### Compress

| # | Do | Expect |
|---|----|--------|
| 15 | Select the files in `to-compress` and choose **Compress**. | A zip with a suggested name is made next to them as a queued job; it appears in the list. |
| 16 | Choose **Compress with options…**. | A **Compress** dialog: **Name**, **Format** (Zip, 7z), **Compression** level, **Password (optional)** with the note "A password uses AES-256. With 7z the list of files is hidden too." |
| 17 | Make a **7z** with a password `uat123` and open it. | A password prompt appears (or the open fails with a clear password message); with the right password the files list. Without it the 7z shows no file names at all. |
| 18 | Make a zip at the highest and lowest levels. | The highest level is smaller (the 300 KB binary is a pattern, so the difference may be large); both extract fine. |
| 19 | Compress a folder containing a file name with a trailing space or a very long path (`02-odd-names`, `03-long-paths`). | Either they are included or the report lists what was skipped and why; the job does not hang. |
| 20 | Compress a very large selection (`06-ten-thousand`, 10,000 files). | A job with progress; the window stays responsive; the zip lists 10,000 entries. |

### Checksums

| # | Do | Expect |
|---|----|--------|
| 21 | In `17-archives\sums` select `one.bin`, `two.bin`, `three.txt` and run **Checksums…** (palette or right-click). | A window opens with check boxes for the kinds (MD5, SHA-1, SHA-256, SHA-512, xxHash3, CRC32). Untick all and press **Compute**: "Tick at least one kind of sum." |
| 22 | Tick **SHA-256** and press **Compute**. | One row per file with its hash. Progress shows; **Stop** halts and says "Stopped." |
| 23 | Compare with PowerShell: `Get-FileHash -Algorithm SHA256 one.bin`. | The same value. Tick several kinds and use the **Show** list to switch which one is displayed. |
| 24 | Paste a correct SHA-256 into **Compare with a sum** (copy from `good.sha256`); then change one character. | The row that matches is marked OK; after the change nothing matches and it says so. |
| 25 | Press **Verify against a checksum file…** and choose `good.sha256`. | Every file shows **OK** (a tick glyph, normal colour). |
| 26 | Verify against `bad.sha256`. | `two.bin` shows a **mismatch** (✕, error colour, readable in both themes) and `missing.bin` shows **missing** (?, warning colour); `one.bin` and `three.txt` are OK. |
| 27 | Press **Copy as checksum file**, paste into a text editor. | Lines like `<hash> *one.bin` for the chosen kind. |
| 28 | Press **Save checksum file…** and save as `mine.sha256`; verify against it. | "Saved mine.sha256." and verifying it shows all OK. |
| 29 | Compute for a 6 GiB file (`04-attributes\big-sparse-6GiB.bin`). | It streams (memory stays flat), shows progress, and finishes; **Stop** is quick. |
| 30 | Compute on a file you cannot read (open exclusively in another program). | That row shows a failure with the reason; other files still complete. |

## Look at (tests cannot judge these)

- Does opening an archive **feel like a folder**? Is it clear in the address bar that you are inside one?
- Is the **Compress** dialog clear about the password and 7z behaviour?
- Are the OK, mismatch and missing marks readable and distinct without relying on colour alone?
- Speed of extraction compared with 7-Zip or Explorer.

## Known edge cases

- Archives are read-only here: no adding to or deleting from an archive in place.
- Rar can be opened and extracted but not created.
- A wrong password on an encrypted archive fails the extract with a message; Leviathan does not store the password.
- Very large archives (many GB) open by reading the index only; extraction is real work and is queued.
- The `.7z` fixture is built only if `7z.exe` is installed; skip those steps otherwise.

## Result

Pass / fail / notes, and anything to tweak.

## Clean up

```powershell
Remove-Item -Recurse -Force "D:\Playbooks\data"
.\tools\New-UatFixtures.ps1
```
