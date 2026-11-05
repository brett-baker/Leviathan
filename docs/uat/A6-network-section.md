# UAT: A6 Network section, saved servers, Add connection, Nearby

**Goal:** find a NAS or PC, add it, see whether it is up, and browse its shares from the tree, without typing UNC paths.
**Built in:** commits 2218435 and 9dc9740, spec §7.1, §3.3.
**Time:** about 15 minutes.

## Setup

1. Build the fixtures **with the local share**, from an elevated PowerShell (this publishes only the fixture folder as `\\localhost\LeviathanUat`):
   ```powershell
   .\tools\New-UatFixtures.ps1 -Share
   ```
2. Start the app with scratch settings (see `TEMPLATE.md`, step 2). The scratch folder means Network starts with no saved servers.

## Steps

| # | Do | Expect |
|---|----|--------|
| 1 | Look at the tree's **Network** section. | Your mapped drives (if any), a **Nearby** group and an **Add connection…** row. No saved servers yet. |
| 2 | Click **Add connection…**. Type `localhost`, leave the name empty, **Connect**. | The dialog closes, a tab opens on `\\localhost` listing the shares, **LeviathanUat** among them. No `C$`, `ADMIN$` or other `$` shares. |
| 3 | Look at the tree again. | `localhost` is now under Network, with a **green** dot. |
| 4 | Expand `localhost` in the tree, open **LeviathanUat**, then `01-basic`. | The shares appear under the server; the folder lists the fixture files. |
| 5 | Add connection → `\\localhost\LeviathanUat`, name it `Fixtures`, choose a free drive letter (for example `U:`), **Connect**. | The tab opens on the share, `Fixtures` shows in the tree, and `U:` exists in the tree and in Windows Explorer. |
| 6 | Add connection → `smb://localhost/LeviathanUat`, then `//localhost/LeviathanUat`, then `ftp://nas`. | The first two are accepted. `ftp://nas` is refused with a clear message (FTP/SFTP arrive in Phase 4). |
| 7 | Add connection → type `bad host!name`. | A message under the box, the dialog stays open, nothing saved. |
| 8 | Add connection → `localhost` again. | No duplicate in the tree; the name you gave earlier is kept or updated, not doubled. |
| 9 | Right-click `localhost` in the tree → **Remove from Network**. | The row goes. Typing `\\localhost\LeviathanUat` in the address bar adds it back on its own (a server you browse is remembered). |
| 10 | Open the **Nearby** group and wait up to 10 s. | "Searching…" while it runs, then other computers or NAS boxes on your network, if any answer. Your own PC may appear. Servers already saved do not repeat. |
| 11 | Elevated: `.\tools\New-UatFixtures.ps1 -RemoveShare`, then click the saved share in the tree. | The tab shows a clear "not reachable / not found" message, not a hang; the dot turns **red** or **amber** within a minute; other tabs keep working. |
| 12 | Close and reopen the app (same scratch folder). | Saved servers are still there. |

## Look at (tests cannot judge these)

- Do the health dots read at a glance (grey unknown, green up, amber slow, red unreachable, blue sign-in needed)?
- Is the Add connection dialog clear without instructions? Are the error messages helpful?
- Does the tree stay calm while Nearby fills in (no jumping rows, no flicker)?
- `[lab]` Real NAS: add it by name and by IP, check the dot and the share list, and that Nearby finds it (Synology answers mDNS and WS-Discovery; some boxes answer only one). Use a test share, not Y: or Z:.

## Known edge cases

- Leviathan stores **no passwords**. A share that wants a login shows "Sign in…" and uses Windows' own prompt.
- Only disk shares are listed; printer and `$` shares are hidden on purpose.
- Discovery is IPv4 only, and some networks block multicast (guest Wi-Fi, VLANs): Nearby then stays empty. That is not a bug.
- Mapped-drive letters are not health-tracked yet (the dot is on servers).
- A drive letter is offered only when the address names a share; a bare server cannot be mapped.
- SFTP, FTP and NFS entries are Phase 4.
- Not checked by eye when this was built: the new tree rows, the dialog layout, the dot colours.

## Clean up

```powershell
.\tools\New-UatFixtures.ps1 -RemoveShare    # elevated
net use U: /delete                          # only if you mapped U: in step 5
Remove-Item -Recurse -Force "$env:LOCALAPPDATA\Temp\leviathan-uat-data"
```
