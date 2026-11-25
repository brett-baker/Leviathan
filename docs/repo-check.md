# Repository check before going public

Done 2026-11-25 on `main` (1,219 tracked files, 336 commits). Repeat before the repository is made public.

## Found nothing

- **Secrets:** no private keys, certificates, tokens, API keys or connection strings in the files or in any commit (searched all history for
  key and token patterns and for key, certificate, database, dump and environment file names). The only passwords are test values: the
  throw-away Samba container in `docker/` (`lab-only-test-password`) and test constants in unit tests.
- **Large or binary files:** none beyond `third-party/7zip` (7-Zip's `7z64.dll` and `7zArm64.dll`, LGPL, listed in
  `THIRD-PARTY-NOTICES.md`) and one icon. No media, databases or logs. `bin/`, `obj/`, `artifacts/`, `TestResults/` and `.vs/` are ignored.
- **Assistant or editor settings:** no `.claude/`, no `settings.local.json`, no `.vscode/`.

## Decide before going public

| Item | Where | Note |
| --- | --- | --- |
| Commit email address | all 330 of Brett's commits carry `brett.baker@gmail.com` | Public once the repository is. Rewriting history is not worth it. For new commits GitHub's "keep my email private" gives a `noreply` address (Settings > Emails), then `git config user.email` that address. |
| Home network details | `CLAUDE.md` names the NAS address and drive letters (`\\192.168.1.10`, `Y:`, `Z:`) as places never to test against | A private (LAN-only) address, so low risk, but it describes your setup. Generalise it if you prefer. |
| Working notes | `CLAUDE.md`, `docs/decisions.md`, `docs/phase-*.md`, `docs/uat/` | Candid build notes and test steps with your folder layout (`D:\Playbooks`). Nothing sensitive; they are also a record of how the app was built. |
| The name in the code | `Copyright (c) Brett Baker` in `Directory.Build.props`, the `Brett Baker` package author | Intended. |

## Fixed in this check

- `docs/decisions.md` quoted a real user-profile path (`C:\Users\<name>\AppData`); it now says `<user>`.
- Added the missing `LICENSE` (GPL-3.0) and a licence and code-signing section to `README.md`.

## Licence fit (GPL-3.0)

- LGPL libraries (LibVLC, SevenZipSharp, 7-Zip) can be used by a GPL-3.0 program. 7-Zip's unRAR code carries an extra restriction (it may
  not be used to rebuild RAR compression); `THIRD-PARTY-NOTICES.md` already says so. The program only decompresses.
- Code ported from the Files project is MIT / MPL-2.0; MPL-2.0 files keep their own licence for their contents, which GPL-3.0 allows.
- ffprobe and ffmpeg are not in the repository or the installer; the app finds them on the machine. If they are ever bundled, the GPL
  build's source offer must accompany the release.
