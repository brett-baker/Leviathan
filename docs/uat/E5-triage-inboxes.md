# UAT: E5 Triage inboxes and sessions

**Goal:** save a folder as a named inbox, triage it in batches over several sittings, and see new arrivals marked.
**Built in:** commit `fa16b70`, spec §10.1 ("working at scale").
**Time:** about 15 minutes.

## Setup

1. Build the fixtures (needs ffmpeg; use `pwsh`):
   ```powershell
   pwsh .\tools\New-UatFixtures.ps1
   ```
   Uses `D:\Playbooks\uat\10-triage\inbox` (six clips) and `keep`.
2. Point Keep at the scratch keep folder, as in the E1-E4 playbook: create `D:\Playbooks\data\settings.json` with
   ```json
   { "Media": { "TriageKeepFolder": "D:\\Playbooks\\uat\\10-triage\\keep", "TriageMoveWhen": "OnKeep" } }
   ```
3. Start the app with scratch settings (inboxes are saved in `inboxes.json` in that data folder):
   ```powershell
   $env:LEVIATHAN_DATA_DIR = "D:\Playbooks\data"
   & .\src\Leviathan.App\bin\x64\Debug\net10.0-windows10.0.26100.0\Leviathan.exe
   ```
4. Open `D:\Playbooks\uat\10-triage\inbox`.

## Steps

| # | Do | Expect |
|---|----|--------|
| 1 | Press **Alt+Shift+T**. | A dialog "Triage an inbox". The box lists "New inbox from this folder"; below it a name (prefilled `inbox`), "Include sub-folders", Order, "Files per session" (1000) and the folder path. |
| 2 | Set the name to `UAT inbox`, Order to **Largest first**, Files per session to **3**. Press **Start**. | "Reading the inbox…", then a triage window with 3 clips in the queue, the largest first. |
| 3 | Keep the first, discard the second, skip the third, then press **Esc**. | The window closes. |
| 4 | Press **Alt+Shift+T** again and pick `UAT inbox`. | The detail line reads like "2 triaged of 6 seen" (kept and discarded count; the skipped clip does not). No "new". |
| 5 | Press **Start**. | A session of up to 3 clips, the largest first, drawn only from the undecided ones: the skipped clip is among them, the kept and discarded clips do not come back. |
| 6 | Decide all of them, close. Open the dialog and press **Start** once more. | A message "Nothing left to triage in 'UAT inbox': all N videos are decided." No window opens. |
| 7 | In PowerShell run: `Copy-Item D:\Playbooks\uat\09-media\short-3s.mp4 D:\Playbooks\uat\10-triage\inbox\arrival.mp4` | A new file in the inbox folder. |
| 8 | Open the inbox dialog and pick `UAT inbox`; **Start**. | A message "1 new in 'UAT inbox'." The session contains `arrival.mp4`; it is marked **New** in the queue list. |
| 9 | Close the app, start it again (same data folder), open the inbox dialog. | `UAT inbox` is still listed with the same counts: it survives restarts. |
| 10 | Pick the inbox and press **Delete inbox**. | "Removed the inbox 'UAT inbox'. Its files were not touched." The clips are still on disk. |
| 11 | Create a new inbox with **Random** order, 1000 per session, then start it twice. | The order differs between sessions (random), and decided files stay decided. |
| 12 | Open a folder on a drive that has no videos and press **Alt+Shift+T**, create an inbox, Start. | "The inbox '…' has no videos." |

## Look at (tests cannot judge these)

- Is the inbox dialog clear about what "triaged of seen" means?
- Is the **New** marker easy to see in the queue list?
- Is a batch size of 1000 a sensible default for you?

## Known edge cases

- Only Keep and Discard count as decided; a skipped file comes back next session (deliberate).
- The first read of an inbox is the baseline: nothing in it is New. New arrivals are found when the inbox is opened; there is no scheduled background check yet.
- Inbox state lives in `inboxes.json`, not in the tag index. Files moved out of the folder by other programs drop out at the next read.
- 30,000-file inboxes are covered by unit tests, not by these fixtures.

## Result

Pass / fail / notes, and anything to tweak.

## Clean up

```powershell
Remove-Item -Recurse -Force "D:\Playbooks\data"
pwsh .\tools\New-UatFixtures.ps1   # rebuilds the triage folder
```
