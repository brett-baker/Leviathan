# Phase 4 — Search, space map and more locations

Phase 4 lets you find a file by what you remember about it, see where the space went, and open servers that are not Windows shares: a query language and
a search box whose results are an ordinary folder, three ways of answering it (Everything, Leviathan's own index, a live listing) merged into one answer, a
space map with a tree, a treemap and a compact version in the status hub, and SFTP, FTP, FTPS and NFS connections with their passwords in Windows Credential
Manager.

## What is in it

| Area | Delivered |
| --- | --- |
| Query and search (A) | `Leviathan.Search.Core`: a parser for the syntax of spec §13.1 (words, phrases, wildcards, `-exclude`, `regex:`, `ext:`, `size:`, `modified:`, `created:`, `tag:`, `type:`, `path:`, `duration:`, `res:`, `codec:`; `content:` is refused with a message) that never throws and points at the bad token; a matcher that tries the cheap conditions first and asks for tags and media information only for files that passed; a live search (8 folders at once, 2 per share, streaming, cancellable, links not followed, unreadable folders reported); results as a folder (`shell:search\…`) so they open in the same tab with every layout, column, tag and action; Ctrl+F and the navigation-row search box; status line with matches and folders searched; saved searches as Favorites (Ctrl+D or "Save this search to Favorites"), rerun on open |
| Sources (B, C) | Everything through its SDK (optional library, never shipped; the query it is sent is always a superset and every answer is judged again by the same matcher); an in-memory name index (one block of text, so a word in a million names is one scan) filled by a low-priority crawl, saved in SQLite, kept up to date every 6 hours and whenever a folder is opened, held back while paused or while a copy runs against the same server; a coordinator that picks the source per folder, merges and de-duplicates, falls back to a live listing if Everything fails mid-search, and says in the status line where the answers came from; "Index this folder for search" and a Search index line with Pause in the Background tab |
| Space map (D) | `Leviathan.SpaceMap.Core`: the squarified treemap with remainder tiles (a million files lay out within the pixels on screen), colour by kind or age, legend by kind or extension, a scanner that fills a tree while it runs, pauses, saves a checkpoint every 10 s and resumes by listing only what is left, and an SQLite copy per root; the Space map window (tree with sizes and shares, treemap, zoom, size filter, open / show in a tab / tag / delete); Ctrl+Shift+M and the command bar's existing button; the compact map and largest items in the status hub's Space tab |
| More locations (E) | `Leviathan.Providers.Remote`: SFTP (SSH.NET) and FTP/FTPS (FluentFTP) behind one provider with a session per server, trust on first use for host keys and certificates, classified errors; Add connection with five types; connections under Network, removable, with "Sign in" for a refused password; NFS through Windows' Client for NFS with a message when it is missing; the Internet section says whether each sync client is running |
| Verification (F) | Search and space map benchmarks (`--search-gate`, `--spacemap-gate`, both in `--gate`); real SFTP and FTP servers in Docker; `fixtures` folder `15-search`; four UAT playbooks; FlaUI tests for search and the space map (written, not run) |

## Gate

| Gate | Result |
| --- | --- |
| Local name search < 50 ms | **PASS through Leviathan's own index** (first results, p95, a million names, several runs): a word in one name 10.7 ms; a word plus an extension (9,860 hits) 1.9 to 28 ms; a word minus another (92,772 hits) 0.3 ms; a kind and a size 13.6 to 19.2 ms. All 92,772 results of the biggest query take about 100 ms. **Not measured through Everything**: it is not installed on the development machine, and the SDK calls have never run against a real one |
| A space map of a 1 TB share completes and can resume | **PASS on a synthetic share** (400,000 files, 1.00 TB, 20,000 folders, in memory): scanned in 0.7 s; stopped half way, the saved copy (1.3 MB compressed) resumed to the same totals and listed 9,997 of 20,001 folders; layout of the 400,000 files at 2560×1440 took 202 to 431 ms (p95; budget 1 s). **A run against the real NAS is a lab step** below. A scan of `C:\Tools` (252 GB, 216,672 files, 55,142 folders) through the real app took about 8 s |
| Earlier phases' budgets | **PASS** in the same run (listing 10,000 items 3.4 ms, new tab 8.1 ms, triage, preview, thumbnails). Cold start is still about 1 s against 600 ms (open since Phase 1) |

### Procedure for the real-share space map (Brett, at the machine)

Pick a share with a lot on it (not Y:, Z: or `\\192.168.1.10` unless you mean to). Start the app from Visual Studio (F5) with the Output window open.

1. Open a tab on the share, press Ctrl+Shift+M, and watch the map fill in. Note the time to the first picture and to "N in M files" without "Scanning…".
2. Press **Stop** about half way. Close the window.
3. Press Ctrl+Shift+M on the same share again. It must continue (the status says folders to go), not start over. Note how long the rest takes.
4. Close and open it once more: the finished map must appear at once.
5. Write the timings and the share's size in this file.

## How it was verified

All on Windows 11 on 2026-11-10 and 11. `Leviathan.App` was built into a scratch folder with zero warnings and zero errors (the normal output is locked while Leviathan
and Visual Studio have it open); each test project was run with `dotnet test`.

| Suite | Passed | Skipped |
| --- | --- | --- |
| Core | 209 | 0 |
| Engine | 325 | 0 |
| Data | 54 | 0 |
| Integration | 52 | 4 (the Samba lab, when it is not running) |
| ViewModels | 1,878 | 0 |
| Diagnostics | 20 | 0 |
| Interop | 97 | 0 |
| ShellHost | 57 | 0 |
| Media | 128 | 0 |
| Playback | 31 | 0 |
| Providers.Local | 36 | 1 (100,000 real files, `LEVIATHAN_STRESS=1`) |
| Providers.Cloud | 32 | 0 |
| Providers.Remote | 19 | 0 |
| Search.Core | 219 | 0 |
| SpaceMap.Core | 49 | 0 |
| Chaos | 3 | 1 (the cable-pull test, when the lab is not running) |
| **Total** | **3,209** | 6 |

- `Leviathan.UI.Tests` was not run (see below). Phase 3 ended at 2,791 tests in 13 projects; Phase 4 added three projects and 418 tests.
- `tests/Leviathan.Benchmarks -- --gate` passes every budget of Phases 0 to 4 in one run (10,000 items first rows 3.4 ms, new tab 8.1 ms, triage decisions, preview 164 ms, thumbnails, name search, space map); only the Everything line is skipped.

- Real servers: nine tests start `atmoz/sftp` and `delfer/alpine-ftp-server` containers (skipped, with the reason, without Docker or with `LEVIATHAN_NO_DOCKER=1`): a 3 MiB file written under a `.leviathan-part` name, renamed and read back byte for byte, unicode names and spaces, folders and deletes, a wrong password, a changed host key, a paused server (fails within 150 s), a closed port, and a tab loading the folder through the real `FolderViewModel`.
- Windows Credential Manager: seven tests write, read, replace and delete real entries under throwaway names.
- Real app: started with a remote connection configured (READY, no exception), and with `LEVIATHAN_OPEN_SPACEMAP` over `C:\Tools` (frames of 494 to 871 tiles, render 6 to 26 ms each).
- Bugs found while building and fixed: a test that crashed the test host (`Progress<T>` called back after the token source was disposed), a flaky wait in the rendezvous tests, `Directory.Packages.props` pulling SSH.NET 2025.1.0 (two known high-severity advisories; 2026.0.0 used), the command bar's `spacemap.open` id declared twice, the bar mapping the folder shown instead of the folder selected, and the fixture container commands passing the image before its arguments.

## Still unverified or open

- **The look of everything new**: the search box and status line, the Space map window and Space tab, the Add connection dialog and the trust question. They compile, their logic is tested and the app starts with them, but nobody has looked at them (no screen access in the session that built them). The playbooks in `docs/uat/` (A4–A5, B–C, D, E) are written for that.
- **Everything**: the SDK client has never run against a real Everything, so the 50 ms gate through it and the translation of queries are untested against the real thing. Dates are deliberately not pushed to it.
- **FTPS** was built but not run against a server; **NFS** was not run on a machine with Client for NFS.
- **The two FlaUI tests** (`SearchAndSpaceMapTests`) have never been run; they need `LEVIATHAN_UI_TESTS=1` and the desktop to themselves, and Brett's say-so first.
- **UI stalls** of 50 to 150 ms were traced while a big scan ran (and one of 1 s on the first run after a cold start); the cause is not isolated. The cold start is still about 1 s against 600 ms.
- One unexplained failure of the ViewModels suite in a run during a rebuild; more than 30 clean runs since.

## Deviations from the spec

See `docs/decisions.md` (entries marked Phase 4). The main ones:

- **No MFT/USN indexer service** (spec §13 "Leviathan.Indexer"): the local fallback and the NAS index are one crawl-built in-memory index. Reason: it needs an elevated process to read the MFT and the journal, nothing here could test it, and the crawl is what the NAS needs anyway. Slice B2b stays open.
- **The NAS index does not piggy-back on the folder-size crawl**: folder sizes are computed in the worker, the index lives in the app; the two lists are separate (a share is listed twice when both are on).
- **Search, scan and index crawl run in the app process, not the worker**: they write nothing, and the pipe contract is better changed once for all three.
- **The space map is a window, not a tab**; compress, "find duplicates here", hard links counted once and "rescan only changed folders" are not built.
- **SFTP and FTP copy, move and delete were wired after the phase** (see `docs/decisions.md`, 2026-11-11): the worker has a second executor for jobs that touch a server.
- **`content:` is refused** with a message until it is built.
- Property tests use a seeded `Random` rather than FsCheck (a dependency for three properties).
