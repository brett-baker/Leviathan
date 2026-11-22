<#
.SYNOPSIS
  Builds the installer and update packages for one version, on this machine, without publishing anything (release plan §5).

.DESCRIPTION
  The same steps the release pipeline will run from a tag: publish every project as Release with the version stamped in, then pack
  it with Velopack into artifacts\release\ (Setup.exe, the full package and the feed files). Rehearse a release here before tagging.

  The program is installed to %LOCALAPPDATA%\LeviathanApp\. That name is deliberately not "Leviathan": Velopack owns its install
  folder, and uninstalling removes it, which must never reach the user's data in %LOCALAPPDATA%\Leviathan\.

.PARAMETER Version
  SemVer, e.g. 0.2.0 or 0.2.0-beta.1. A label after the dash puts the build on the beta channel; none is stable.
#>
param(
  [Parameter(Mandatory)][ValidatePattern('^\d+\.\d+\.\d+(-beta\.\d+)?$')][string]$Version,
  [string]$Output = (Join-Path $PSScriptRoot '..\artifacts\release')
)

$ErrorActionPreference = 'Stop'
$root = Resolve-Path (Join-Path $PSScriptRoot '..')
$publish = Join-Path $root 'artifacts\release-publish'
$channel = if ($Version -like '*-beta.*') { 'beta' } else { 'stable' }

if (Test-Path $publish) { Remove-Item $publish -Recurse -Force }
& (Join-Path $PSScriptRoot 'Publish-App.ps1') -Output $publish -Version $Version

dotnet tool restore --tool-manifest (Join-Path $root '.config\dotnet-tools.json')
if ($LASTEXITCODE -ne 0) { throw 'tool restore failed' }

$icon = Join-Path $PSScriptRoot 'icons\Leviathan.ico'
$iconArgs = if (Test-Path $icon) { @('--icon', $icon) } else { @() }
dotnet vpk pack --packId LeviathanApp --packVersion $Version --packTitle Leviathan --packAuthors 'Brett Baker' `
  --packDir $publish --mainExe Leviathan.exe --outputDir $Output --channel $channel --runtime win-x64 @iconArgs
if ($LASTEXITCODE -ne 0) { throw 'vpk pack failed' }
Write-Host "Release $Version ($channel) is in $Output"
