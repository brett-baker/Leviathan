# UAT: A7 Link speed in the status bar

**Goal:** see at a glance what speed the connection to the NAS can reach, so a slow copy can be judged against it.
**Built in:** commit f04c83d, spec §3.5.
**Time:** 5 minutes locally, 10 more in the lab.

## Setup

Fixtures built (with `-Share`, elevated, for step 2); app started with scratch settings (see `TEMPLATE.md`).

## Steps

| # | Do | Expect |
|---|----|--------|
| 1 | Open `D:\Playbooks\uat\01-basic` (a local folder). | The status bar shows items and free space as before; **no** network text. |
| 2 | Open `\\localhost\LeviathanUat\01-basic`. | The status bar shows `\\localhost\LeviathanUat` and **no speed** (a share on this PC never leaves the PC, so there is no link to measure). |
| 3 | `[lab]` Open a folder on a test share on your NAS. | `\\nas\share · 1 Gb/s` (or `2.5 Gb/s`, `10 Gb/s`) appears within about a second of the folder opening; the folder itself did not wait for it. |
| 4 | `[lab]` Open another folder on the same NAS, then switch between tabs. | The speed is there at once (remembered for a minute). |
| 5 | `[lab]` Unplug the cable (or disable the adapter), wait a minute, open the share again. | The folder shows the "not responding" state and the status bar says `· not responding`, not a stale speed. |
| 6 | `[lab]` Set the NIC to 100 Mb/s if you can. | After a minute the bar says `100 Mb/s`. |
| 7 | `[lab]` Copy a few GB to the NAS and compare the job's speed with the number in the bar. | Copy speed is below the link rate (1 Gb/s is about 112 MB/s at best; the spec's target is at least 105 MB/s). |

## Look at (tests cannot judge these)

- Is the wording (`· 1 Gb/s`) readable next to the share name, and does it fit when the window is narrow?
- Does the status bar flicker when the speed arrives?

## Known edge cases

- The number is what the adapter **negotiated**, not what the NAS or the path in between can deliver. A 2.5 GbE card into a 1 Gb/s switch port shows `1 Gb/s`; a slow disk in the NAS is still slow.
- Wi-Fi reports its current link rate, which moves.
- A VPN or a name that does not resolve shows no speed. IPv6-only servers are not measured.
- Not done yet: remembering measured speed between runs, and showing the measured speed of each share.
- Not checked on the real 2.5 GbE to 1 Gb/s link when this was built.

## Clean up

```powershell
.\tools\New-UatFixtures.ps1 -RemoveShare    # elevated
Remove-Item -Recurse -Force "D:\Playbooks\data"
```
