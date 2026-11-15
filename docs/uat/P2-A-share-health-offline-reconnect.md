# UAT: P2-A Share health, offline cache, change detection and reconnecting

**Goal:** when a share drops, only that tab suffers: it says so quickly, shows the last listing greyed, and recovers when the share returns.
**Built in:** Phase 2 slices A1 to A5 (`docs/phase-2.md`), spec §3.3, §8.6.
**Time:** about 30 minutes. Needs an elevated PowerShell for the local test share.

## Setup

1. Build the fixtures **and the local test share** `\\localhost\LeviathanUat` (an SMB share on this PC, so no real NAS is touched):
   ```powershell
   # elevated PowerShell
   .\tools\New-UatFixtures.ps1 -Share
   ```
2. Scratch settings and start (normal PowerShell):
   ```powershell
   $env:LEVIATHAN_DATA_DIR = "D:\Playbooks\data"
   & .\src\Leviathan.App\bin\x64\Debug\net10.0-windows10.0.26100.0\Leviathan.exe
   ```
3. Never use `Y:`, `Z:` or `\\192.168.1.10` here. Steps marked **[lab]** need a dedicated test share you name; skip them otherwise.

## Steps

| # | Do | Expect |
|---|----|--------|
| 1 | Open `\\localhost\LeviathanUat\01-basic` in a tab. Keep a second tab open on `D:\Playbooks\uat\01-basic`. | The share lists normally. The status bar shows the location health (online) and the link speed. |
| 2 | Open `\\localhost\LeviathanUat\06-ten-thousand`. | First rows appear quickly; the status bar health shows online or slow with the latency. |
| 3 | Visit a few share folders, then **remove the share** (elevated): `Remove-SmbShare -Name LeviathanUat -Force`. In the share tab, press **F5** or open another folder of the share. | Within about 2 to 5 seconds the tab shows the **last listing greyed** with a banner such as "offline, as of 14:02" and **Retry** and **Work offline** buttons, and the tab gets a warning badge. Nothing else freezes: the local tab, the sidebar and the status bar stay responsive. |
| 4 | Click **Retry** while the share is still gone. | It fails fast (a couple of seconds) with the banner still showing. Retries back off rather than hammering. |
| 5 | Click **Work offline**. | The tab stops retrying and keeps the greyed cached listing; folders you never visited say they are not cached. |
| 6 | Re-create the share without rebuilding the fixtures (elevated): `New-SmbShare -Name LeviathanUat -Path D:\Playbooks\uat -FullAccess "$env:USERDOMAIN\$env:USERNAME"`. Press **Retry** (or refresh). | The banner disappears and the listing becomes live. The health returns to online. |
| 7 | Try to open `\\10.255.255.1\share` (an address with no server). | The tab says the location is unreachable after about 5 seconds at most, not after a Windows 30-second hang. Other tabs keep working. |
| 8 | With the share online, open `\\localhost\LeviathanUat\08-targets\target-a` in the app, then in another program create a file there (`New-Item` in PowerShell). | Within a few seconds the new file appears without pressing F5 (network folders are polled, with a longer interval when idle). Delete it: it disappears. |
| 9 | Open Quick settings in the status hub (**Ctrl+Shift+O**), change the **polling interval**, and repeat step 8. | The new file shows up at roughly the new interval. |
| 10 | Open the status hub **Background** tab. | A "Network folders" line shows the folders watched by polling and the listings kept for offline use. |
| 11 | **Sign in:** if a tab says authentication is required (a share needing credentials), click **Sign in…** on its state panel. | A Windows credential dialog opens; after signing in the tab reloads. Leviathan never stores the password itself (Windows Credential Manager does). **[lab]** needs a share that asks for credentials. |
| 12 | Disconnect a mapped drive (`net use X: /delete`) if you mapped one to the test share, then open it. | The tab offers to reconnect rather than showing an empty list. |
| 13 | Make two names for the same share (for example `\\localhost\LeviathanUat` and `\\127.0.0.1\LeviathanUat`). Copy a file between them. | The job treats both as one server (one identity; "same server" copies are handled as such). |

### Network section of the tree

| # | Do | Expect |
|---|----|--------|
| 14 | In the sidebar, open **Network**. | Mapped drives and saved servers, each with a health dot. **Nearby** may list servers found by discovery (WS-Discovery or mDNS) after a few seconds, in the background. |
| 15 | Choose **Add connection…**, add `\\localhost\LeviathanUat` as an SMB connection (optionally with a drive letter). | It appears under Network with a green dot; expanding lists the share's folders (never lists a whole server without you opening it). |
| 16 | Remove the share (as in step 3) and look at the tree dot. | The dot goes red or amber within a few seconds. |

## Look at (tests cannot judge these)

- Is the **offline banner** clear? Do the grey listing and the badge make the state obvious without being alarming?
- Is "fails in 2 to 5 seconds" what you see, or does anything wait for 30 seconds?
- Does anything outside the dead tab (sidebar, other tabs, typing in the address bar) ever stall?

## Known edge cases

- The credential dialog and reconnecting a disconnected mapped drive use Windows calls that were never exercised against a real disconnected drive or a share needing a login; steps 11 and 12 are the first real check.
- Reconnect is on the UI side; a copy running in the worker does not reconnect by itself (it waits and retries, see P2-B).
- Health dots are not shown for SFTP, FTP or NFS connections (see `E-remote-locations`).
- IPv6 discovery is not built.

## Result

Pass / fail / notes, and anything to tweak.

## Clean up

```powershell
# elevated
.\tools\New-UatFixtures.ps1 -RemoveShare
Remove-Item -Recurse -Force "D:\Playbooks\data"
```
