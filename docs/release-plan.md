# Release and update plan

How Leviathan becomes an installable app that tells the user when a new version exists, and how a release is cut
independently of day-to-day development. A plan to review with Brett; nothing here is built yet.

## 1. The idea in one page

**A commit is not a release.** Commits land on `main` all day. A *release* is a deliberate act: someone decides "this
state of `main` is good enough", gives it a version number, and publishes it. The app only ever knows about releases.

```
 commits on main ──► (CI: build + test on every push)
                          │
        you decide: "this is 0.6.0"
                          │
                 git tag v0.6.0 ──► release pipeline
                                      1. build Release, all projects
                                      2. run the whole test suite + budget gate
                                      3. sign the executables
                                      4. package: Setup.exe + update packages
                                      5. publish to a GitHub Release (the "feed")
                          │
   installed app, on a schedule ──► reads the feed ──► "0.6.0 is available" ──► user clicks
                                                         Restart and update
```

So "the point where someone says that's enough" is **pushing a version tag**. Everything after the tag is automatic and
repeatable. Development keeps going on `main` while the release pipeline runs on the tagged commit.

## 2. Decisions to make (recommendations first)

| Question | Recommendation | Why |
| --- | --- | --- |
| Installer and updater | **Velopack** (open source, .NET, MIT) | Makes `Setup.exe` for unpackaged apps like ours (`WindowsPackageType=None`), installs per user with no admin prompt, updates in the background, delta packages so an update is small, rolls back if the new version will not start. Update feed can be GitHub Releases. |
| Alternative | MSIX + App Installer | Cleaner uninstall and Store-style updates, but needs package identity, which changes how shell extensions, the worker and the elevated runner start. A bigger change; revisit only if the Store matters. |
| Where releases live | **GitHub Releases** on `brett-baker/Leviathan` | Already the remote; free; the app reads it over HTTPS with no server to run. If the repo stays private, the update feed needs a token or a small public feed repo (decide before the first public build). |
| Version numbers | **SemVer, taken from the tag** (`v0.6.0`) | One source of truth. `Directory.Build.props` `VersionPrefix` is set by the pipeline from the tag, so a build can never say a different number than its tag. |
| Release cadence | A release per finished phase or notable fix batch; tag when `main` is green and the gate passes | Brett decides; the pipeline does the rest. |
| Channels | `stable` (default) and `beta` | Beta gets every tagged `-beta.N` build; stable only `vX.Y.Z`. A setting picks the channel. |
| Code signing | Needed before public release (**Azure Trusted Signing** or a code-signing certificate) | Unsigned installers trigger SmartScreen warnings. Not needed while only Brett installs it. |

## 3. What gets installed

- One folder per user, **`%LOCALAPPDATA%\LeviathanApp\`** (Velopack's layout: `current\`, `packages\`, `Update.exe`). The name differs
  from the data folder on purpose: Velopack deletes its folder on uninstall, so it must never be the one that holds user data. It holds `Leviathan.exe`,
  `Leviathan.Worker.exe`, `Leviathan.Media.exe`, `Leviathan.ShellHost.exe`, `Leviathan.Elevated.exe`, `RestartAgent.exe` and
  libraries (LibVLC, ffprobe, 7-Zip).
- **User data stays where it is now**, `%LOCALAPPDATA%\Leviathan\` (settings, journal, databases, logs, crashes), apart from the
  program folder. An update or uninstall never touches it, which is what "nothing is ever lost" needs.
- Start Menu shortcut, optional desktop shortcut, entry in Apps and features.

## 4. How the app finds out about an update

1. At start-up (after the first folder is on screen, spec §3.1) and then every few hours, a background task asks the feed
   "what is the newest version for my channel?". Cancellable, with a timeout; failing silently when offline.
2. If newer: download the update package in the background (deltas make it small), verify its hash and signature.
3. Tell the user, quietly: a notice in the status hub and a small badge, **"Leviathan 0.6.0 is ready. What's new · Restart
   and update · Later"**. Never interrupt a running copy or move; **the update only applies when the user chooses, or
   at the next start**.
4. "Restart and update": the app asks the worker whether jobs are running. Running jobs are journaled and resume after the restart
   (spec §3.4), but the dialog says so, and offers "Wait for jobs to finish". Then all helper processes are stopped (the
   `ProcessSupervisor` already knows them), Velopack swaps the program folder and starts the new version.
5. Settings: *Check for updates* (on by default), *Channel*, *Check now*, *What's new* (the release notes). Added to the
   Settings sheet as an **About and updates** page.

## 5. Building a release independent of development

- **Pipeline** `.github/workflows/release.yml`, triggered by a tag `v*`. It checks out *that tag's commit*, so work landing on
  `main` meanwhile is not in the release.
- Steps: set the version from the tag, `dotnet publish` the app and the helpers as Release, run the full test suite and the
  budget gate (`--gate`), sign, `vpk pack`, upload `Setup.exe`, the full and delta packages and the `releases.*.json` feed
  files to the GitHub Release, and generate release notes from the commit titles since the last tag.
- **Hotfixes**: if a bug appears in 0.6.0 while `main` already has half-finished 0.7 work, branch `release/0.6` from the tag,
  fix there (or cherry-pick the fix from `main`), tag `v0.6.1`. The pipeline is the same. Most of the time the fix is on `main`
  and a new tag on `main` is enough.
- **A release is immutable.** A bad release is never edited: it is superseded by the next version (and the feed can mark one
  version as skipped).
- Locally, `tools/Make-Release.ps1` runs the same steps without publishing, so a release can be rehearsed before tagging.

## 6. Things in this app that need care

- **Several processes.** The worker, media and shell-host processes must be stopped before files are replaced, and the new
  version must start them again. The update step belongs to the UI process plus the existing `RestartAgent`.
- **Version handshake.** The UI and worker already check a contract version; after an update, a mismatched old worker must be
  replaced, never reused. Keep `ContractVersion.Major` honest.
- **Data compatibility.** Settings (`SchemaVersion`), journal and databases must open with the next version, and a new version
  must be able to read what the previous one wrote. Add a test that opens files written by the last release.
- **Rolling back.** Velopack keeps the previous version; a new version that crashes at start-up goes back to it and the crash
  bundle explains why (spec §20).
- **The dev copy.** Running from Visual Studio keeps using the build folder and never checks for updates (guarded by "not
  installed"), so development and an installed copy do not fight. They share user data, so Brett's installed copy should use
  `LEVIATHAN_DATA_DIR` for experiments, as today.

## 7. Slices

1. **Version from the tag**: pipeline-set version, `About` shows version, commit and channel. Test: the version reader.
2. **Installable build, local only**: `tools/Make-Release.ps1` produces `Setup.exe` with Velopack; install, run, uninstall on a
   clean machine or VM; check that data survives.
3. **Update check and notice**: feed reader behind an `IUpdateSource` interface with a fake; the notice in the status hub; the
   Settings page. Tests: newer, same, older, offline, bad hash, channel rules.
4. **Apply the update**: stop helpers, swap, restart, resume jobs, roll back. Chaos test: kill during the swap.
5. **Release pipeline on GitHub**: `release.yml`, release notes, first beta tag (`v0.6.0-beta.1`) installed by Brett and updated
   to `-beta.2` to watch the whole loop.
6. **Signing and public stable channel**: only when the app is shared beyond Brett.

## 8. Answers (Brett, 2026-11-22)

1. **Repo goes public, but not yet** (Brett, same day: it stays private for now). Design for public; until then builds are
   rehearsed locally (`tools/Make-Release.ps1`) and the GitHub pipeline waits. The feed reader sits behind `IUpdateSource`, so a
   private-repo token or a different feed can be swapped in without touching the app.
2. **A few people will install it soon**: signing moves up. Slice 6 must land before the first build is shared; until then
   only Brett installs. Before going public, check the repo for secrets and personal data, and keep `THIRD-PARTY-NOTICES.md` current.
3. **Release when Brett says "ship it"**: no automatic cadence. He names the version; Claude tags it and the pipeline runs.
4. **Per-user install, no admin prompt**: confirmed.
