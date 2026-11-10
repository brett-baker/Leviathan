# UAT: E6 Skim playback

**Goal:** press S on a video and watch short bursts from along its length, so a long clip can be judged in seconds.
**Built in:** commit `64fcbe0`, spec §10.2.
**Time:** about 10 minutes.

## Setup

1. Build the fixtures (needs ffmpeg; use `pwsh`):
   ```powershell
   pwsh .\tools\New-UatFixtures.ps1
   ```
   Uses `09-media` (`long-2m05.avi` is the best one: 125 s, a numbered test pattern whose clock shows where you are) and `10-triage\inbox`.
2. Start the app with scratch settings:
   ```powershell
   $env:LEVIATHAN_DATA_DIR = "D:\Playbooks\data"
   & .\src\Leviathan.App\bin\x64\Debug\net10.0-windows10.0.26100.0\Leviathan.exe
   ```

## Steps

A burst is 3 seconds; bursts start every 5% of the length, so a 125 s clip has about 20 bursts, a little over a minute of viewing.

| # | Do | Expect |
|---|----|--------|
| 1 | Open `09-media`, click `long-2m05.avi`, show the pane (**Alt+P**), press play, then press **S** with the focus in the pane's player (click the video first). | Playback jumps to the next 5% point and plays 3 seconds, then jumps to the next point, and so on. The seek bar moves in steps. |
| 2 | Watch the test pattern's clock across several bursts. | Each burst starts about 6 seconds later in the clip than the previous one (5% of 125 s). |
| 3 | Let it run to the end. | The last burst fits inside the clip and the video pauses at the end. It does not loop or run past the end. |
| 4 | Press **S** again while skimming. | Skim stops; the video stays where it is. |
| 5 | Start skimming, then press **Space**. | Skimming stops and the video pauses. |
| 6 | Start skimming, then press **Right** (jump 30 s). | Skimming stops and the jump happens. |
| 7 | Start skimming, then click another file. | Skimming stops; the new file loads normally and does not start skimming on its own. |
| 8 | Open `10-triage\inbox`, click a clip, press **Alt+T**. In the triage window press **S**. | The same skimming in the triage window. |
| 9 | While skimming in triage, press **Enter** (Keep). | The next clip starts normally and is not skimming. |
| 10 | Press **S** on `short-3s.mp4` (3 s). | One short burst or a quick play to the end; no error, no stuck state. |
| 11 | Press **S** on `broken.mp4`. | Nothing happens or a calm message; the app is not affected. |

## Look at (tests cannot judge these)

- Is 3 seconds every 5% a good rhythm for judging a clip? Too choppy, too slow?
- How noticeable is the small pause at each jump (see edge cases)?
- Is S easy to remember, and does it clash with anything you type in the pane?

## Known edge cases

- There is no decoded-ahead join: every burst ends with one seek, so a short hitch is expected between bursts (the engines hold a single stream). Judge whether it is acceptable.
- Burst length and interval are settings in `Media` (`SkimBurstSeconds`, `SkimIntervalPercent`) with no settings page yet.
- On a network share, each seek can take longer; this is not measured in the fixtures.

## Result

Pass / fail / notes, and anything to tweak.

## Clean up

```powershell
Remove-Item -Recurse -Force "D:\Playbooks\data"
```
