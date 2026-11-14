# Phase 5, G1: budget sweep (spec §3.1)

Measured 2026-11-14 on Brett's PC (Windows 11, Release build, quiet but not idle), with
`dotnet run -c Release --project tests/Leviathan.Benchmarks -p:Platform=x64 -- --gate` and, for the real app,
`-- --cold-start-gate <published Leviathan.exe>` (new in G1: the cold start on its own).

| Budget (§3.1) | Measured | Result |
| --- | --- | --- |
| Local folder, 10,000 items, first rows < 100 ms | provider 2.7 to 3.5 ms; through the view model 2.5 ms; from the listing cache 0.0 ms (p95) | PASS |
| Local folder, 100,000 items, fully enumerated < 1.5 s | 150 ms (p95); **gate added in G1**, none existed | PASS |
| New tab < 150 ms | 3.0 ms with 1,000 items (p95) | PASS |
| Thumbnail from the cache < 200 ms; generated < 1 s | 0.4 ms; 94 ms for a 6 s clip (p95) | PASS |
| Media information, cold ffprobe < 300 ms | 66 ms (p95) | PASS |
| Preview requested after the 150 ms rest < 200 ms | 164 ms (p95) | PASS |
| Triage decisions < 50 ms (queue of 2,000) | 0.0 to 3.5 ms (p95) | PASS |
| Name search over 1,020,100 names, first results < 50 ms | 0.2 to 13.9 ms (p95); index built in 1.0 s, 101 MB | PASS |
| Space map of a 400,000-file / 1 TB share; layout < 1 s | scan 0.5 s; layout 135 ms (p95); resume correct | PASS |
| **Cold start to the first folder < 600 ms** | **published ReadyToRun: median 918 to 930 ms, worst 942 to 1,687 ms; plain Release build: median 1,224 ms** | **FAIL** |
| UI thread stall never > 50 ms | enforced at run time by the watchdog; not a benchmark | UI tests (not run) |
| **Idle memory, 10 tabs < 250 MB** (G2) | **UI process: private 262 to 274 MB, working set 345 to 364 MB** with 10 tabs restored and idle for 10 s; the same with 1 tab; the worker adds 20 MB private | **FAIL by about 12 to 24 MB** |
| Thumbnail memory capped (G2) | 64 MB LRU, now also at most 50,000 remembered answers (was unbounded for "no thumbnail" results) | PASS, tested |
| 1,000 tabs opened and closed, 200 windows (G2) | nothing left behind: the first closed tab is collected, growth under 8 MB | PASS, tested |
| NAS folder, 10,000 items, first rows < 400 ms; NAS copy >= 105 MB/s | needs a share Brett names | not measured: `--nas-rows <folder>`, `--nas-copy <folder>` |
| Local name search through Everything | needs Everything installed | skipped by the gate |

## Cold start: where the time goes

From the app's own trace (plain Release build, one run): 283 ms from process start to `OnLaunched`, 162 ms `AppHost.Create`,
587 ms constructing the main window (XAML 184 ms, title bar 61 ms, chrome icons 78 ms, tabs 96 ms), then the first folder at about
1,200 ms. ReadyToRun takes about 300 ms off that, which is why the published build shows about 920 ms.

Tried in G1 and **no effect** (median stayed 904 to 937 ms): `DOTNET_TieredPGO=0`, `DOTNET_TC_BackgroundWorkerTimeoutMs=0`,
a larger OSR limit. So the gap is the work done before the first folder is painted, not the runtime settings.

What would close it (about 320 ms to find): a lighter `MainWindow`. Build the status hub, settings sheet, preview pane and the
details panel on first use (`x:Load` or idle building) so `InitializeComponent` shrinks; read the icon packs and make the chrome icons
after the first folder is painted (about 140 ms: icon packs 59 ms plus chrome icons 78 ms); build the title bar after `Activate`.
This is UI work that has to be looked at on screen, so it is not done blind inside a benchmark slice. The same gap was recorded at the
end of Phase 1 (about 1,010 ms) and carried through Phases 2 to 4.

### Cold start varies with the load on the machine

The 920 ms above was measured while the PC was quiet. Later the same day, with a long ffmpeg job, Docker and Edge running (CPU about
80%), the same published build measured 1,200 to 1,290 ms, and so did the build from before E6 (commit 0ab57e1) measured side by side:
so the slices E6 to F3 added nothing to the start. Read the number as "about 0.9 to 1.2 s depending on load"; the budget is 0.6 s.

## Idle memory (G2): where it goes

Measured with `-- --idle-memory <published Leviathan.exe> [tabs]` (new in G2: writes a ten-tab session into a scratch data folder,
starts the app on it, waits 10 s after ready). The app's own trace line (`MEMORY:`, written only when `LEVIATHAN_UI_TRACE` is set)
says: **managed heap 26 MB**, GC committed 46 MB, private 274 MB, working set 364 MB. So about 230 MB of the process is native:
WinUI and the Windows App SDK, and on this PC the NVIDIA user-mode driver (`nvgpucomp64.dll` and `nvwgf2umx.dll`, about 180 MB of
mapped modules) that the first Direct3D device loads. One tab or ten makes no measurable difference (262 MB against 266 MB private):
tabs restore lazily and a listing is small. Garbage-collector switches moved it by at most 15 MB (`DOTNET_GCgen0size=16 MB`: 251 MB
private), so the managed side is not where the gap is.

What would close it: the budget was set for an app on the managed heap; on a machine with a discrete GPU driver the floor of any WinUI 3
window is about 250 MB. Options are to measure the budget as managed plus our own native (about 70 MB, passes easily), to run on a
machine with a lighter driver, or to cut WinUI's own use (no Mica, fewer composition surfaces), which is a visible change for Brett to
decide. Nothing in Leviathan's own code was found to grow.
