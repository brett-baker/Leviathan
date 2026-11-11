# UAT: B–C Search sources and the index

**Goal:** keep a folder (a NAS share, say) in a search index so name searches there answer at once, and see what the index is doing.
**Built in:** Phase 4 slices B1–B3 and C1–C2 (commits `8bd5489`, `d5f1b50`, `5955410`, `884bf44`), spec §13.
**Time:** about 15 minutes.

## Setup

1. Build the fixtures, with `-Share` (elevated) for the network steps:
   ```powershell
   .\tools\New-UatFixtures.ps1 -Share
   ```
   The share is `\\localhost\LeviathanUat`; use `D:\Playbooks\uat\15-search` locally and `\\localhost\LeviathanUat\15-search` for the share.
2. Start the app from Visual Studio (F5) with `LEVIATHAN_DATA_DIR` = `D:\Playbooks\data`. Open the **status hub** (the bar at the bottom) and look at the **Background** tab.

## Steps

| # | Do | Expect |
|---|----|--------|
| 1 | Open `\\localhost\LeviathanUat\15-search`. Press Ctrl+Shift+P and run "Index this folder for search (or stop)". | The status bar says the folder is now kept in the search index. |
| 2 | Look at the Background tab, "Search index". | The folder is "listing · N folders, M items", then "listed <date time>". |
| 3 | Search `holiday` in that folder. | Results as before; the status bar ends with "from the index, last listed …". |
| 4 | Create `holiday extra.txt` in `\\localhost\LeviathanUat\15-search` from Explorer and search again. | It is **not** found yet (the index is as of its last listing) — this is expected. |
| 5 | Open the folder in a tab (it lists), then search `holiday` again. | Now it is found: a folder you open refreshes its entries in the index. |
| 6 | Click **Pause** next to "Search index". | The line says "Paused". Start a big copy to the same share; indexing stays quiet. Click **Resume**. |
| 7 | Search a local folder that is not indexed. | "folders listed now" or, if Everything runs, "from Everything" (see edge cases). |
| 8 | Run the index command again on the same folder. | The folder is no longer kept in the index; the Background line returns to "No folders are indexed". |
| 9 | Restart the app. | An indexed folder is searchable at once without listing again. |

## Look at (tests cannot judge these)

- Whether the Background line is clear about what is indexed, when, and why it waits.
- Whether the "last listed" age is the right thing to show beside results.

## Known edge cases

- **Everything:** the SDK library is not shipped. Put `Everything64.dll` next to the app in an `everything` folder, or install Everything; without it the status says searches list folders now. This path has **not been run against a real Everything**.
- The index is refilled every 6 hours and when you open a folder; items deleted elsewhere stay in the index until then.
- There is no MFT/USN indexer service yet (needs an elevated machine to test); local disks are indexed by the same crawl as shares.
- The index copy is kept in SQLite (`leviathan.db`) and opened only when a folder is chosen.

## Result

Pass / fail / notes, and anything to tweak.

## Clean up

```powershell
.\tools\New-UatFixtures.ps1 -RemoveShare   # elevated
Remove-Item -Recurse -Force "D:\Playbooks\data"
```
