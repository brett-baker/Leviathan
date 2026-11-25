# Releasing Leviathan

How a release is made once the pipeline exists (release plan slice 5). A release is a deliberate act: you say "ship it" and name the
version, and the pipeline does the rest from the tag.

## Cut a release

1. Make sure `main` is what you want to ship, pushed, and green on the CI page.
2. Tag that commit and push the tag (Claude does this when you say "ship it"):
   ```powershell
   git tag v0.6.0-beta.1
   git push origin v0.6.0-beta.1
   ```
   Version rules: `vMAJOR.MINOR.PATCH` is a finished release; `vMAJOR.MINOR.PATCH-beta.N` is a beta (a GitHub pre-release).
3. Watch **Actions > Release**. It checks the tag, builds, runs every test and the budget gate, writes the notes from the commit titles
   since the last tag, fetches the previous release (for a small delta package), packs with Velopack and publishes the GitHub Release
   with `Setup.exe`, the full and delta packages and the feed files.
4. Install from the release page, or let an installed older copy find it (Settings > Advanced > Check now).

## Rehearse without publishing

```powershell
.\tools\New-ReleaseNotes.ps1 -Tag v0.6.0-beta.1 -OutFile notes.md
.\tools\Make-Release.ps1 -Version 0.6.0-beta.1 -ReleaseNotes notes.md
```

The installer and packages are in `artifacts\release\`. Nothing is uploaded.

## Rules the pipeline enforces

- The tag must be a version tag and its commit must be on `main`; otherwise the run stops before building.
- A failing test or a missed budget stops the run before anything is published.
- A release is never edited. A bad one is replaced by the next version; delete the GitHub Release and its tag only if nobody installed it.
- One feed: every release is packed into Velopack's default `win` channel. Stable or beta is the release page's pre-release flag. A copy on
  the stable channel sees finished releases only; a copy on beta sees both, so a beta user is offered the finished release too.

## Before anyone but you installs a build

- **Make the repository public and sign** (`docs/signing.md`): the SignPath Foundation signs open-source projects only. The steps are
  there; the pipeline signs by itself once the repository variable `SIGNPATH_ORGANIZATION_ID` is set, and stays unsigned (SmartScreen
  warns) until then.
- **The repository is private**, so the app cannot read its own release page without a token. Going public fixes that too. Go through
  `docs/repo-check.md` first (no secrets were found; a few things are yours to decide).
