# UAT: R2 Local installer

**Goal:** build an installer on this machine, install Leviathan from it, see which version it reports, and uninstall it without touching your data.
**Built in:** commits `e087c1c` and `b814a95`, release plan slices 1 and 2.
**Time:** about 15 minutes (the build takes about 3).

## Setup

1. No fixtures are needed; the installer is the thing under test.
2. Close Leviathan if it is running (the build and install replace files).
3. Build the installer (nothing is published anywhere):
   ```powershell
   .\tools\Make-Release.ps1 -Version 0.2.0-beta.1
   ```
4. Put a marker in your real data folder, to prove uninstalling leaves it alone:
   ```powershell
   Set-Content "$env:LOCALAPPDATA\Leviathan\uat-marker.txt" "keep me"
   ```
5. Scratch settings so the installed copy does not touch your real ones. Set this in the same PowerShell window you use for every step:
   ```powershell
   $env:LEVIATHAN_DATA_DIR = "D:\Playbooks\data"
   ```

## Steps

| # | Do | Expect |
|---|----|--------|
| 1 | Look in `artifacts\release`. | `LeviathanApp-win-Setup.exe` (about 143 MB), a `.nupkg`, `Portable.zip`, `RELEASES` and two `.json` feed files. |
| 2 | Run `.\artifacts\release\LeviathanApp-win-Setup.exe` from your PowerShell window. | Windows SmartScreen may warn (the file is unsigned): choose More info, then Run anyway. No administrator prompt. A short progress window, then Leviathan starts by itself. |
| 3 | Open Settings (Ctrl+,), then Advanced. | The About card says **Leviathan 0.2.0-beta.1**, with the commit it was built from, and the channel **Beta**. |
| 4 | Look for `$env:LOCALAPPDATA\LeviathanApp\`. | It holds `current\`, `packages\` and `Update.exe`. Your real data folder `$env:LOCALAPPDATA\Leviathan\` is unchanged. |
| 5 | Open the Start menu and search for Leviathan. | A shortcut exists. A desktop shortcut was also created. |
| 6 | Open Apps and Features (Settings, Apps, Installed apps). | Leviathan is listed with its version. |
| 7 | Close Leviathan. From your PowerShell window, start it again: `& "$env:LOCALAPPDATA\LeviathanApp\current\Leviathan.exe"` | It opens as before, on the folder you left. |
| 8 | Close it and uninstall it from Apps and Features. | It disappears with no prompt. `$env:LOCALAPPDATA\LeviathanApp\` is gone, the shortcuts are gone. |
| 9 | Check `Get-Content "$env:LOCALAPPDATA\Leviathan\uat-marker.txt"` and `D:\Playbooks\data`. | The marker still says "keep me". Your data folders are still there. |

## Look at (tests cannot judge these)

- How long the installer takes and whether its window looks acceptable (it is Velopack's default, with a default icon).
- How the installed app feels to start compared with the build you run from Visual Studio (cold start was budgeted under 600 ms).
- The wording of the About card.

## Known edge cases

- **Unsigned:** the SmartScreen warning is expected until signing is set up (release plan slice 6).
- **No custom icon:** the repository has no `.ico` yet, so the installer and shortcuts use the default one.
- **Always set `LEVIATHAN_DATA_DIR` first.** The installed app uses your real settings, favourites and saved servers otherwise. Shortcuts from the Start menu do not carry the variable; use step 7's command.
- **Do not run copy, move or delete jobs in the installed copy.** A scratch launch has been seen reading your real job journal despite `LEVIATHAN_DATA_DIR`; this is not fixed yet.
- **A dev build and an installed copy share worker pipes and data**, so close one before running the other.
- **Not built yet:** the update notice, applying an update, and rolling back (slices 3 and 4). Installing a second version over the first is not part of this playbook.
- **Unverified:** ARM64 builds, and installing on a clean machine or VM (your machine already has the Windows App SDK pieces).

## Result

Pass / fail / notes, and anything to tweak.

## Clean up

```powershell
Remove-Item "$env:LOCALAPPDATA\Leviathan\uat-marker.txt"
Remove-Item -Recurse -Force "D:\Playbooks\data"
Remove-Item -Recurse -Force .\artifacts\release, .\artifacts\release-publish
```
