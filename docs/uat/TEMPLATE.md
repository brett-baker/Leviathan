# UAT: <slice id> <name>

**Goal:** one sentence: what a user should be able to do after this slice.
**Built in:** commit `<hash>`, spec §<n>.
**Time:** about <n> minutes.

## Setup

1. Build the fixtures (never test with real data):
   ```powershell
   .\tools\New-UatFixtures.ps1          # add -Share (elevated) for the network steps
   ```
   Fixture root: `D:\Playbooks\uat`.
2. Start the app with scratch settings, so your real settings, favourites and saved servers are untouched:
   ```powershell
   $env:LEVIATHAN_DATA_DIR = "D:\Playbooks\data"
   & .\src\Leviathan.App\bin\x64\Debug\net10.0-windows10.0.26100.0\Leviathan.exe
   ```

## Steps

| # | Do | Expect |
|---|----|--------|
| 1 | … | … |

Mark `[lab]` on any step that needs a real NAS or a container; use a dedicated test share, never Y:, Z: or `\\192.168.1.10`.

## Look at (tests cannot judge these)

- Layout, wording, colour, feel, speed.

## Known edge cases

- What is deliberately not handled, what looks odd but is right, what is unverified.

## Result

Pass / fail / notes, and anything to tweak.

## Clean up

```powershell
.\tools\New-UatFixtures.ps1 -RemoveShare   # if you used -Share (elevated)
Remove-Item -Recurse -Force "D:\Playbooks\data"
```
