# UAT: D1 Preview pane shell

**Goal:** open, close, resize and arrange the preview/details pane, and open a floating quick preview with Space, with all of it remembered.
**Built in:** commit `c7ddd39`, spec §9.
**Time:** about 10 minutes.

## Setup

1. Build the fixtures (never test with real data):
   ```powershell
   pwsh .\tools\New-UatFixtures.ps1
   ```
   Fixture root: `D:\Playbooks\uat`. This playbook uses `09-media` and `11-preview`.
2. Start the app with scratch settings:
   ```powershell
   $env:LEVIATHAN_DATA_DIR = "D:\Playbooks\data"
   & .\src\Leviathan.App\bin\x64\Debug\net10.0-windows10.0.26100.0\Leviathan.exe
   ```
3. Open `D:\Playbooks\uat\11-preview`.

## Steps

| # | Do | Expect |
|---|----|--------|
| 1 | Press **Alt+P**. | A pane appears on the right with two parts, **Preview** above and **Details** below. Press **Alt+P** again: it disappears and the file list takes the space. |
| 2 | Open the pane again. Click `picture.png`. | The Preview part shows the picture; Details shows its facts. |
| 3 | Drag the thin splitter on the pane's left edge left and right. | The pane gets wider and narrower smoothly, with limits (it never swallows the file list, never gets thinner than a usable minimum). |
| 4 | Click the header of the **Preview** part, then the header of **Details**. | Each part collapses to its header and opens again; collapsing one gives the room to the other. |
| 5 | Click the pane's header button (collapse). | The pane collapses to a slim strip or hides, and comes back with the same button or **Alt+P**. |
| 6 | Open `09-media`, click `medium-45s.mkv`. In the **View** menu choose **Preview video: small**, then **medium**, then **large**. | The video area changes size each time. |
| 7 | Drag the handle under the video up and down. | The video area follows the mouse and stays between a minimum and a maximum. |
| 8 | Click a file in the list, then press **Space**. | A small floating preview window opens for that file. |
| 9 | With the floating preview open, press the arrow keys in the file list. | The floating preview follows the selection. **Space** or **Esc** closes it. |
| 10 | Press **Ctrl+\\** to split the tab into two panes. Click in the right pane. | The pane's header says "Preview · left pane" (or the pane it follows) and the preview follows the pane you click in. |
| 11 | Set the pane width, a video size and collapse Details. Close the app and start it again with the same data folder. | The window comes back with the same pane width, video size and collapsed parts. |
| 12 | Select nothing (click empty space in the list). | The pane shows an empty/hint state, not an error. |

## Look at (tests cannot judge these)

- Does the splitter feel natural to grab, and are the minimum and maximum widths sensible on your monitor?
- Are the collapsed headers readable? Do the icons of the buttons make sense?
- Does the floating preview open fast enough that Space feels instant?
- Layout at 150% display scaling, and on the second monitor if you have one.

## Known edge cases

- Pane state is saved in the session, so it is per window, not a global setting.
- The floating preview shows the same renderers as the pane; what each renderer draws is judged in the D2 playbook.
- Nothing here has been checked on a second monitor with different scaling.

## Result

Pass / fail / notes, and anything to tweak.

## Clean up

```powershell
Remove-Item -Recurse -Force "D:\Playbooks\data"
```
