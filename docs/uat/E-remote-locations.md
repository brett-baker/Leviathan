# UAT: E SFTP, FTP, FTPS, NFS and sync status

**Goal:** add a server of another kind, browse it like any folder, and see which cloud clients are running.
**Built in:** Phase 4 slices E1–E6 (commit `bb88820`), spec §7, §7.1.
**Time:** about 20 minutes.

## Setup

1. Two throwaway servers in Docker (public test images; nothing of yours is on them). In PowerShell:
   ```powershell
   docker run -d --name uat-sftp -p 2222:22 atmoz/sftp uat:uatpass:::upload
   docker run -d --name uat-ftp -p 2121:21 -p 31000-31010:31000-31010 -e ADDRESS=127.0.0.1 -e MIN_PORT=31000 -e MAX_PORT=31010 -e "USERS=uat|uatpass|/ftp/uat" delfer/alpine-ftp-server
   ```
2. Start the app from Visual Studio (F5) with `LEVIATHAN_DATA_DIR` = `D:\Playbooks\data`.

## Steps

| # | Do | Expect |
|---|----|--------|
| 1 | Sidebar → Network → **Add connection…**. Choose **SFTP**, server `127.0.0.1`, port `2222`, user `uat`, password `uatpass`, folder `/upload`, name "UAT SFTP". Connect. | A dialog asks whether to trust the server and shows a fingerprint. Choose **Trust**. The tab opens `/upload` (empty). |
| 2 | Look at the sidebar. | "UAT SFTP" under Network with "SFTP · 127.0.0.1:2222" as its tooltip. |
| 3 | Open Windows' **Credential Manager** → Windows Credentials. | An entry named `Leviathan:sftp://uat@127.0.0.1:2222` holds the password. Check `D:\Playbooks\data\remote-connections.json`: **no password** in it. |
| 4 | Close the tab, click "UAT SFTP". | Opens at once with no new trust question. |
| 5 | Stop the container (`docker stop uat-sftp`), then click the entry. | The tab says the server could not be reached (not a hang), within about 10 seconds. Other tabs are unaffected. `docker start uat-sftp`, press F5 in the tab: it works again. |
| 6 | Remove the connection (right-click → Remove connection), add it again with a **wrong** password. | "refused the sign-in"; the tab offers **Sign in**, which asks for the password; the right one makes the folder open. |
| 7 | Add an **FTP** connection (port `2121`, user `uat`, password `uatpass`, folder `/ftp/uat`). | Opens an empty folder, no trust question (plain FTP). |
| 8 | Add an **NFS export** (any host). | If Windows' Client for NFS is missing, the dialog says how to turn it on and does not add anything. If it is installed, the share opens as `\\host\export`. |
| 9 | Look at the sidebar's **Internet** section. | Each cloud drive says "Sync client running", "Sync client not running" or "Sync folder missing". Quit OneDrive (or another client) and wait a minute: the line changes. |
| 10 | Stop the test servers. | `docker rm -f uat-sftp uat-ftp` |

## Look at (tests cannot judge these)

- The Add connection dialog: whether the fields for each type are the right ones and clearly labelled.
- The trust question's wording and fingerprint display.
- Error messages for a dead server, a refused sign-in and a changed key.

## Known edge cases

- **Copy, move, delete, rename and new folder** work between the file view and these servers, and between two servers, but have had **no hand testing**: only unit tests and the Docker servers. Delete on a server is permanent and asks first. A copy stopped by a crash starts that file again; copies are checked by size only; modified times are not kept on a server. Opening a file on a server is not wired.
- FTPS is built (with certificate trust on first use) but was not run against a server. NFS was not run on a machine with Client for NFS.
- A changed host key is refused with a message to remove the connection and add it again.
- Sidebar expansion of a server lists its folders through the same provider; health dots are not shown for these servers.

## Result

Pass / fail / notes, and anything to tweak.

## Clean up

```powershell
docker rm -f uat-sftp uat-ftp
Remove-Item -Recurse -Force "D:\Playbooks\data"
# Credential Manager: remove entries named Leviathan:… if you left connections in place.
```
