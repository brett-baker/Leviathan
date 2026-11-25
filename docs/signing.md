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

## SignPath Foundation's conditions (read from signpath.org/terms, 2026-11-25)

- An OSI-approved open-source licence (GPL-3.0 qualifies) and no proprietary code in the project.
- **The project must already be released in the form that is to be signed**, and be maintained. An unsigned public beta comes first.
- **A verifiable reputation for programs people download.** A brand-new project nobody knows may be turned down; they decide, and do not
  discuss it. A public repository with a README, a release page and some history is the best case you can make.
- A code signing policy on the home page (done: README), and a privacy statement (done; the update check is the one network call).
- **Two-factor authentication** on your GitHub and SignPath accounts.
- **Every release needs a person to approve the signing request** in SignPath. The workflow waits up to 50 minutes for each of its two
  requests (binaries, then installer), so be at hand to approve when you tag.
- Signed files must carry fixed metadata: product name `Leviathan` and the same product version in every file of a build. The build already
  stamps both from the tag; add the metadata restrictions in SignPath's artifact configuration editor.
- Only your own binaries are signed (that is what `.signpath/binaries.xml` selects).

## One-time setup (needs you), in this order

1. **Turn on two-factor authentication** on GitHub (Settings > Password and authentication) if it is not on.
2. **Decide the items in `docs/repo-check.md`** (commit email, the NAS address in `CLAUDE.md`), then **make the repository public**:
   GitHub > the repository > Settings > General > Danger Zone > Change visibility > Make public.
3. **Publish an unsigned beta** so there is a released build to point at: tell Claude "ship v0.2.0-beta.1". Check the release page and
   install it once.
4. **Apply** at <https://signpath.org/apply> as maintainer of `brett-baker/Leviathan`, with the release page as the download page.
   Approval takes days to weeks, or a no.
5. **After approval, in SignPath:** create the project and note its **organization id**, **project slug** and **signing policy slug**
   (usually `release-signing`). Add the artifact configurations `.signpath/binaries.xml` (slug `binaries`) and `.signpath/installer.xml`
   (slug `installer`), with the metadata restrictions. SignPath checks the XML when you save it; if it rejects a line, fix it there and
   copy the fix back to `.signpath/`, so the repository stays the record. Set yourself as the approver, with two-factor on.
6. **Connect GitHub:** install the SignPath GitHub App on the repository (SignPath's setup page shows how) and create an API token for a CI
   user allowed to submit to the signing policy.
7. **In the repository** (Settings > Secrets and variables > Actions): secret `SIGNPATH_API_TOKEN`, and variables
   `SIGNPATH_ORGANIZATION_ID`, `SIGNPATH_PROJECT_SLUG` and `SIGNPATH_SIGNING_POLICY_SLUG`.
8. Tag the next beta and approve both signing requests in SignPath when they arrive. The workflow then fails the run if `Leviathan.exe`,
   `Leviathan.Worker.exe` or the installer does not check as validly signed, so a release cannot go out half signed.

## If SignPath says no

The signing steps in the workflow are not tied to SignPath's certificate, only to its action. The other routes: Azure Trusted Signing
(about $10 a month; needs an Azure subscription, which your login does not have, and identity validation) or a bought certificate (the key
must now sit on a hardware token or cloud HSM, which makes automatic signing harder). Either replaces the two SignPath steps.

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
