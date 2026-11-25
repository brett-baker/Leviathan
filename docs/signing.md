# Code signing

How Leviathan's Windows releases are signed (release plan slice 6), and what has to be done once, by a person, before the first signed
release. The pipeline is ready; it stays unsigned until the repository variable `SIGNPATH_ORGANIZATION_ID` is set.

## What is signed

| What | When | Why |
| --- | --- | --- |
| The program's own binaries (`Leviathan*.exe`, `Leviathan*.dll`) | Before Velopack packs them | Antivirus and SmartScreen judge each file, and the files inside the update package are the ones that run. |
| `LeviathanApp-win-Setup.exe` | After Velopack packs it | This is the file people download. SmartScreen judges it first. |

Not signed: third-party libraries (LibVLC, 7-Zip, WinUI and the rest keep their own signatures or none), and Velopack's own `Update.exe`
inside the package (signing it after packing would break the package's hash). Only the program's own files carry the SignPath Foundation
certificate, which is what its policy allows.

## How it works

SignPath signs on its own servers: the workflow uploads the files as a GitHub artifact, SignPath fetches that artifact itself (so it knows
it came from this repository's workflow, not from someone's laptop), signs it and hands it back. The key never leaves SignPath. The steps
are in `.github/workflows/release.yml`; the two artifact configurations (what to sign in each upload) are in `.signpath/`.

## One-time setup (needs you)

1. **Make the repository public.** SignPath Foundation signs open-source projects only. First go through `docs/repo-check.md`: no
   secrets were found, but decide about the commit email address and the NAS address in `CLAUDE.md`. The licence is GPL-3.0 (`LICENSE`).
2. **Apply** at <https://signpath.org/apply> as the maintainer of `brett-baker/Leviathan`. They check the project is open source, that it
   is not malware, and that the README states a code signing policy (it does: see "Code signing policy" in `README.md`). Approval takes
   days to weeks; there is no cost.
3. **In SignPath**, once approved: create the project, note its **project slug**, its **organization id**, and the **signing policy slug**
   (usually `release-signing`). Add the two artifact configurations from `.signpath/binaries.xml` and `.signpath/installer.xml` with the
   slugs `binaries` and `installer`. SignPath checks the XML when you save it; if it rejects a line, fix it there and copy the fix back to
   `.signpath/` so the repository stays the record.
4. **Connect GitHub**: install the SignPath GitHub App on the repository (SignPath's setup page shows how) and create an API token for a
   CI user with permission to submit to the signing policy.
5. **In the repository settings** (Settings > Secrets and variables > Actions):
   - Secret `SIGNPATH_API_TOKEN` = the API token.
   - Variables `SIGNPATH_ORGANIZATION_ID`, `SIGNPATH_PROJECT_SLUG`, `SIGNPATH_SIGNING_POLICY_SLUG`.
6. Tag a beta. The workflow signs, then fails the run if `Leviathan.exe`, `Leviathan.Worker.exe` or the installer does not check as validly
   signed, so a release can never go out half signed by accident.

## Checking a signed release

```powershell
Get-AuthenticodeSignature .\LeviathanApp-win-Setup.exe | Format-List Status, SignerCertificate
```

`Status` should be `Valid`, and the signer should be the SignPath Foundation. A brand new certificate has no download reputation yet, so
SmartScreen may still warn for the first downloads even though the file is signed; that fades as more people install it.

## What was not verified

- The two artifact configurations and the workflow steps have not run against SignPath. They follow SignPath's published GitHub Actions
  integration, but the first signing run is the real test.
- The update path (an installed copy downloading a signed package and swapping the folder) is checked by the first beta-to-beta update, not
  before.
