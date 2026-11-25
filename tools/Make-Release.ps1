<#
.SYNOPSIS
  Builds the installer and update packages for one version, on this machine, without publishing anything (release plan §5).

.DESCRIPTION
  The same steps the release pipeline will run from a tag: publish every project as Release with the version stamped in, then pack
  it with Velopack into artifacts\release\ (Setup.exe, the full package and the feed files). Rehearse a release here before tagging.

  The program is installed to %LOCALAPPDATA%\LeviathanApp\. That name is deliberately not "Leviathan": Velopack owns its install
  folder, and uninstalling removes it, which must never reach the user's data in %LOCALAPPDATA%\Leviathan\.

.PARAMETER Version
  SemVer, e.g. 0.2.0 or 0.2.0-beta.1. A label after the dash is a beta, none is stable. Both go into the one Velopack feed (win): stable or beta is the release page's pre-release flag, which the app's update source reads, so a beta user is also offered the finished release.
#>
param(
  [Parameter(Mandatory)][ValidatePattern('^\d+\.\d+\.\d+(-beta\.\d+)?$')][string]$Version,
  [string]$Output = (Join-Path $PSScriptRoot '..\artifacts\release'),
  # A Markdown file of release notes to put in the update package (tools\New-ReleaseNotes.ps1 writes one).
  [string]$ReleaseNotes = '',
  # Publish and stop, so the binaries can be signed before they are packed (docs/signing.md).
  [switch]$PublishOnly,
  # Pack what -PublishOnly left in artifacts\release-publish (signed by then) instead of publishing again.
  [switch]$UsePublished
)

$ErrorActionPreference = 'Stop'
$root = Resolve-Path (Join-Path $PSScriptRoot '..')
$publish = Join-Path $root 'artifacts\release-publish'

if ($UsePublished) {
  if (-not (Test-Path (Join-Path $publish 'Leviathan.exe'))) { throw "Nothing to pack in $publish. Run with -PublishOnly first." }
}
else {
  if (Test-Path $publish) { Remove-Item $publish -Recurse -Force }
  & (Join-Path $PSScriptRoot 'Publish-App.ps1') -Output $publish -Version $Version
}

if ($PublishOnly) { Write-Host "Published to $publish; pack it with -UsePublished."; return }

dotnet tool restore --tool-manifest (Join-Path $root '.config\dotnet-tools.json')
if ($LASTEXITCODE -ne 0) { throw 'tool restore failed' }

$icon = Join-Path $PSScriptRoot 'icons\Leviathan.ico'
$iconArgs = if (Test-Path $icon) { @('--icon', $icon) } else { @() }
$notesArgs = if ($ReleaseNotes) { @('--releaseNotes', (Resolve-Path $ReleaseNotes)) } else { @() }
dotnet vpk pack --packId LeviathanApp --packVersion $Version --packTitle Leviathan --packAuthors 'Brett Baker' `
  --packDir $publish --mainExe Leviathan.exe --outputDir $Output --runtime win-x64 @iconArgs @notesArgs
if ($LASTEXITCODE -ne 0) { throw 'vpk pack failed' }
Write-Host "Release $Version is in $Output"
