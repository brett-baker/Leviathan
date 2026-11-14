# Phase 5, G3: stress (build plan §G, spec §3, CLAUDE.md "long paths, odd names, huge folders, 100 GB files are normal inputs")

Run 2026-11-14 on Brett's PC. Everything below used temporary folders; no real share, drive or user data was touched.

## What was run and what it found

| Case | How | Result |
| --- | --- | --- |
| Browse a 500,000-file folder | view model test `ALargeFolder_LoadsSortsFiltersAndSelects...(500_000)`: load in batches, sort by size and name, filter `*.mkv`, select all | pass in about 11 s together with the 100,000 case; list updates stay in chunks (under 1,000 for 500,000 rows) |
| Search a million names | G1 gate: 1,020,100 names, first results 0.2 to 14 ms | pass (see `docs/phase-5-budgets.md`) |
| Path past 400 characters: copy, move, permanent delete | `AwkwardPathsOnDiskTests` on a real temp folder, 14 levels deep | pass, no part files left, source gone after a move |
| Names Windows changes or refuses: trailing dot, trailing or leading space, `con`, `aux.txt`, `nul.log`, `com1`, `prn.`, `..` endings, emoji, `#%&;,=` and more (15 names) | the same tests, one case per name; also a folder named `folder with a dot.` holding a file and a folder named `inner con` | **found 4 bugs, all fixed (below)**; all 15 names now copy with every byte, and delete |
| Very large file | `-- --big-copy 24`: a 24 GiB file through the real copy engine, default verify (sampled above 4 GiB), markers checked at every GiB | pass: 44 s, **584 MB/s, memory peak 46 MB** (flat: it streams); 2 GiB gave the same 45 MB |
| Share drops during an operation | engine tests: copy and move (`FailingCopier`), any executor (`JobHostTests`), **delete (new)** | copy, move and delete now all go to Waiting and finish when the share is back; give up after the wait limit |

## Bugs found and fixed

1. **A file or folder whose name ends in a dot or a space, or is a device name (`con`, `aux.txt`), could not be copied, moved or deleted.** Windows rewrites such names unless the path has the `\\?\` prefix; the engine used plain paths. Such files do exist on NAS shares and come from Linux and macOS. `LocationPath.ToWin32Path` now adds the prefix for exactly those names (all other paths are unchanged, so ordinary files behave as before), and the places that walk a tree by strings use `LocationPath.ForIo`.
2. **A path with a leading or trailing space in a name was trimmed when the engine read its own plan back** (`LocationPath.Parse` trims, for typed text). New `LocationPath.ParseExact` keeps names exactly; the engine, worker and result handling use it for paths that came from a listing or a job. Typed text still goes through `TryResolve` and `Parse`.
3. **That bug made the job hang in "Running" with no error** (it retried a file it could not find); with the fix it completes. A file that is genuinely missing still ends the job normally.
4. **A delete on a share that dropped failed the item** instead of waiting and retrying as copy and move do (spec §3.3). Deletes are safe to repeat (items already gone count as "was already gone"), so the host now waits and runs the job again. Regression test: `AShareThatDropsDuringADelete_PutsTheJobInWaiting...`.
5. Permanent delete walked a folder with one recursive call; it now goes folder by folder (junctions are removed, never followed) so odd names are reachable.

Also fixed on the way: the in-memory file system of the unit tests cannot hold `\\?\` paths, so `TransferPlanner` and `CopyMoveExecutor` take an optional path mapper (default: the real one); three share-renaming tests pass the plain path. A stale triage integration test (it assumed the old default for when kept files move) now sets the setting it depends on.

## What was not run, and why

- **100 GB.** A 24 GiB copy ran; at 584 MB/s a 100 GB file would take about 3 minutes and the memory does not depend on size, but the 100 GB figure itself is an extrapolation. Move and delete of a very large file were not repeated at that size.
- **Dead shares with real SMB.** The NAS drop tests (`NetworkDropTests`, needs the lab share and a proxy) were skipped here; the engine-level behaviour above is tested with injected failures only. A manual run against a share you name is still open.
- **Odd names in the folder view.** The engine now handles them; the file list, rename, open and the context menu still build paths the ordinary way, so on a name with a trailing dot or space they may fail. This was not exercised on screen.
- **500,000 files on screen.** The view model was tested; scrolling a 500,000-row list was not looked at.
