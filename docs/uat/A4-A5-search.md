# UAT: A4–A5 Search box, results tab and saved searches

**Goal:** type what you are looking for, get the matches as an ordinary folder in the same tab, refine it, and keep a search in Favorites.
**Built in:** Phase 4 slices A4 and A5 (commit `38e8460`, `229c791`), spec §13.
**Time:** about 10 minutes.

## Setup

1. Build the fixtures (never test with real data):
   ```powershell
   .\tools\New-UatFixtures.ps1          # add -Share (elevated) for the network steps
   ```
   Fixture root: `D:\Playbooks\uat`. This playbook uses `D:\Playbooks\uat\15-search`.
2. Start the app from Visual Studio (F5) with `LEVIATHAN_DATA_DIR` set to `D:\Playbooks\data` in the project's debug environment variables, so your real settings and favorites are untouched. Watch the Output window ("Debug") for the `Leviathan:` trace lines.

## Steps

| # | Do | Expect |
|---|----|--------|
| 1 | Open `D:\Playbooks\uat\15-search` in a tab. Press **Ctrl+F**. | The search box in the top row takes the keyboard. |
| 2 | Type `holiday` and press Enter. | The tab shows a folder named "Search: holiday" with `Summer holiday 2025.mkv`, `holiday-draft.mkv`, `Winter holiday 2022.mp4` and `holiday plans.pdf`. The address bar says what was searched and where. The status bar counts matches and folders searched. |
| 3 | Press **Back** (Alt+Left). | You are back in `15-search`, and the search box is empty. |
| 4 | Search `type:video size:>4MB`. | Only `Summer holiday 2025.mkv` and `Winter holiday 2022.mp4`. |
| 5 | Search `modified:lastyear` then `modified:2026`. | Files changed in those periods only (the old ones, dated three years back, never show). |
| 6 | Search `holiday -draft`. | The draft video is gone from the result. |
| 7 | Search `IMG_*.jpg`. | `IMG_0001.jpg` and `IMG_0002.jpg`. |
| 8 | Search `size:abc`. | Nothing runs; the status bar says what is wrong and quotes `size:abc`. The tab does not change. |
| 9 | Search `content:invoice`. | Refused with a message that `content:` is not available yet. |
| 10 | With results showing, search `holiday type:video`. | The same folders are searched again (refined), not a search of the results. |
| 11 | Change the layout to Tiles or Gallery, sort by size, select a result and press Space. | Everything works as in a normal folder: layouts, sorting, preview. |
| 12 | In a results tab press **Ctrl+D**. | "Added to Favorites". In the sidebar a magnifier entry "Search: holiday" appears; hovering it shows what it looks for. |
| 13 | Click that Favorite from another folder. | The search runs again and shows the current matches. |
| 14 | Create `holiday new.txt` in `15-search`, then open the Favorite again. | The new file is in the results. |
| 15 | Right-click the Favorite, rename it to "Holiday stuff". | The name changes; it still runs the same search. |
| 16 | Search `tag:green` after tagging a file green (key 1). | The tagged file is found. |

## Look at (tests cannot judge these)

- The wording of the status bar line while a search runs and when it ends.
- Whether the search box placeholder and tooltip are enough to learn the syntax.
- How quickly the first results appear on a large folder (try a big drive, then press Back at once).

## Known edge cases

- Search is **live** (folders are listed now) unless a folder is indexed (see the index playbook). On a large share it takes as long as a listing.
- `duration:`, `res:` and `codec:` only match files whose media information is already known (the search never probes files); the status line says so.
- `content:` is refused until it is built. Regular expressions use `regex:`.
- The search runs inside the app for now, not in the worker. Closing the tab stops it.
- The results of ad hoc searches are also kept by the folder listing cache (bounded); nothing else is stored.

## Result

Pass / fail / notes, and anything to tweak.

## Clean up

```powershell
Remove-Item -Recurse -Force "D:\Playbooks\data"
```
