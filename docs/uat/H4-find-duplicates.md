# UAT: H4a and H4b Find duplicates

**Goal:** find look-alike files inside one folder, or files in one pane that already exist in the other pane, and select the extra copies without anything being changed.
**Built in:** commits `347c25a` (H4a), `cedc651` (H4b), spec §12.7.
**Time:** about 15 minutes.

## Setup

1. Build the fixtures (use `pwsh`):
   ```powershell
   pwsh .\tools\New-UatFixtures.ps1
   ```
   Uses `D:\Playbooks\uat\12-duplicates`: `one-folder` (three versions of one movie, an unrelated film, two zips with look-alike names), and `left` and `right` (the same episodes in different qualities, plus one file only on the left).
2. Start the app with scratch settings:
   ```powershell
   $env:LEVIATHAN_DATA_DIR = "D:\Playbooks\data"
   & .\src\Leviathan.App\bin\x64\Debug\net10.0-windows10.0.26100.0\Leviathan.exe
   ```

## Steps

| # | Do | Expect |
|---|----|--------|
| 1 | Open `12-duplicates\one-folder`. Press **Alt+Shift+D**. | A dialog "Find duplicates". "Names must be this alike (%)" shows 60. The summary names the groups, for example "2 groups, 3 extra copies, 3.3 MB that could be freed". The "Look for" box is hidden (there is no second pane). |
| 2 | Read the list. | Group 1: the three `Movie Name 2019` files, "Keep: …" first (the highest quality, expected `…2160p.HEVC-OTHER.mkv`), the other two indented with their sizes and "% alike". Group 2: the two Holiday photos zips. `Other Film 2020.mkv` and `unrelated-thing.dat` are in no group. |
| 3 | Set the percentage to 100 and press **Search again**. | Only names that are the same once the noise words (resolution, codec, group, brackets) are removed still group. Note which groups survive; the look-alike zips with `(1)` are the likely ones to separate. |
| 4 | Set it to 10, **Search again**. | More groups; unrelated files start to match each other. Set it back to 60. |
| 5 | Press **Select the extra copies**. | The dialog closes. The extra copies (not the "Keep" ones) are selected in the folder; the status bar says "Selected 3 extra copies: Delete moves them to the bin." Nothing was deleted or moved yet. |
| 6 | Press **Delete**. | The selected copies go to the Recycle Bin (after the usual confirmation). Undo (**Ctrl+Z**) brings them back. |
| 7 | Rebuild the fixtures. Open the dialog again, change the percentage to 75, press **Close** (not Select). Reopen the dialog. | The dialog now starts at 75: the choice is remembered. (Reset it to 60 afterwards.) |
| 8 | Rebuild the fixtures. Open `12-duplicates\left` and split the tab (**Ctrl+\\**) with `12-duplicates\right` in the other pane. Click in the left pane, press **Alt+Shift+D**. | The dialog now has "Look for": "Duplicates in this folder" and "Files here that are already in the other pane". |
| 9 | Choose **Files here that are already in the other pane**. | A list of source files: `Show S01E01 720p.mkv` (verdict "better copy already there": the right pane has a 1080p), `Show S01E02 1080p.mkv` ("same file already there", the sizes match), `Show S01E03 1080p.mkv` ("this one is better": the right copy is 480p). `Only on the left.mkv` is not listed. The summary counts the ones with a better or identical copy and their size. |
| 10 | Press **Select the ones already there**. | In the left pane, `S01E01 720p` and `S01E02 1080p` are selected; `S01E03` and `Only on the left` are not. The status says Delete moves them to the bin. |
| 11 | Close the split (**Ctrl+\\**) and open `left` on its own, press **Alt+Shift+D**. | Only the within-folder mode (no "Look for" box). The three episodes are not grouped with each other (different episodes). |
| 12 | Open a folder with 10,000 items (`06-ten-thousand`), press **Alt+Shift+D**. | The dialog opens and fills without freezing the window (the search runs off the UI thread). The files are all `item-NNNNN` names; expect a very large number of groups or none, but no hang. |

## Look at (tests cannot judge these)

- Is "Keep:" the copy you would keep? (The ranker is a port of the vlib one; note the cases you disagree with.) Note any group where you disagree.
- Is 60% a good default, and is the % wording understandable?
- Does the list stay readable with many groups?

## Known edge cases

- Matching is by **name only**; there is no hash or content check, so two different files with the same name count as duplicates and identical content under different names does not.
- The dialog never deletes: it only selects; Delete and Undo do the rest.
- The pane comparison uses the other pane's *shown* rows (a filter in that pane limits what it can find).
- Step 12 is a stress check, not a meaningful result.

## Result

Pass / fail / notes, and anything to tweak.

## Clean up

```powershell
Remove-Item -Recurse -Force "D:\Playbooks\data"
pwsh .\tools\New-UatFixtures.ps1   # restores anything you deleted
```
