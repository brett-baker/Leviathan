# UAT: C5b Activity links to reports, Background network line

**Goal:** from the Activity list, get from "Copied 3 files" to what happened to each file; from the Background tab, see what Leviathan is doing for network folders.
**Built in:** commit 662d98d, spec §16.1, §3.3.
**Time:** about 10 minutes.

## Setup

Fixtures built (with `-Share`, elevated, for steps 8–9); app started with scratch settings (see `TEMPLATE.md`). Open the hub with **Ctrl+Shift+O**.

## Steps

| # | Do | Expect |
|---|----|--------|
| 1 | Open `uat\05-conflicts\source`, select all, copy; open `uat\05-conflicts\destination`, paste. At the first conflict choose **Replace** and tick apply to all. | A copy job runs and finishes. |
| 2 | Hub → **Activity**. | A line such as "Copied 4 items to …destination" at the top. |
| 3 | Click that line. | The job's **report** opens: a summary, then each item with its reason. **Show destination** and **Show source folder** work. |
| 4 | Copy `uat\07-bulk-copy` into `uat\08-targets\target-a` and cancel part way. Click its Activity line. | The report says cancelled and lists what was and was not copied. No `*.leviathan-part` files remain in the target. |
| 5 | Delete the empty folder `uat\08-targets\target-b`, then click its Activity line. | A report opens, or a clear "no report" message if the worker no longer has one. |
| 6 | Click an Activity line for something that is not a job, such as "Went to …". | The tab goes to that folder; no report dialog. |
| 7 | Tick **Earlier sessions**, close and reopen the app, reopen Hub → Activity. | Earlier lines are listed. A copy line from before the restart opens its report if the worker still has it, otherwise says reports are kept for seven days. |
| 8 | Open `\\localhost\LeviathanUat\01-basic`, then Hub → **Background**, **Network folders**. | `Watching 1 network folder for changes · 1 folder listing kept for offline use (N of 200,000 items)`. |
| 9 | Open a second network folder in another tab, check the line, then close both tabs. | The watched count rises to 2, then falls to 0 shortly after the tabs close. |
| 10 | Start fresh with only local folders open. | `No network folder watched · no listings kept for offline use`. |

## Look at (tests cannot judge these)

- Does a click on an Activity line do what you expect, and is it obvious that it will (cursor, hover)?
- Is the report dialog readable for a job with hundreds of items?
- Does the Network folders line fit in the Background tab at the fly-up's default size?

## Known edge cases

- Reports are kept for **seven days** by the worker; older Activity lines say so instead of opening.
- Only Leviathan's own jobs are logged; copies made by other programs do not appear.
- The Background tab has no share crawl or offline-cache refresh task yet; those come with the NAS index. The line shows only what exists today.
- An `activity.db` from before this slice is upgraded in place; its older lines have no report link.
- Edits to the *contents* of existing files on a share do not show until you refresh (F5); polling watches the folder's timestamp.
- Not checked by eye when this was built: the row click and the new line.

## Clean up

```powershell
.\tools\New-UatFixtures.ps1 -RemoveShare    # elevated
Remove-Item -Recurse -Force "D:\Playbooks\data"
```
