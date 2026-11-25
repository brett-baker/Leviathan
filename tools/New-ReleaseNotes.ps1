<#
.SYNOPSIS
  Writes the release notes for a version tag from the commit titles since the previous release tag (release plan §5).

.DESCRIPTION
  The notes go into the update package, so the app can show "What's new", and onto the GitHub release page. The previous release is the
  newest v* tag before this one; the first release lists the most recent commits. Merge commits are left out.

.PARAMETER Tag
  The release tag, e.g. v0.6.0 or v0.6.0-beta.1. It must exist, or be HEAD's intended tag (the notes then run up to HEAD).

.PARAMETER OutFile
  Where to write the Markdown. The text is also written to the output.
#>
param(
  [Parameter(Mandatory)][ValidatePattern('^v\d+\.\d+\.\d+(-beta\.\d+)?$')][string]$Tag,
  [string]$OutFile = '',
  [int]$Max = 100
)

$ErrorActionPreference = 'Stop'

function Get-PreviousTag([string]$tag) {
  # The newest release tag that is not this one and is reachable from it (or from HEAD when the tag is not made yet).
  $from = if (git tag --list $tag) { "$tag^" } else { 'HEAD' }
  $found = git describe --tags --match 'v[0-9]*' --abbrev=0 $from 2>$null
  if ($LASTEXITCODE -eq 0 -and $found) { return $found.Trim() }
  return $null
}

function Get-ReleaseNoteLines([string[]]$subjects, [int]$max) {
  $lines = $subjects | Where-Object { $_ -and $_ -notmatch '^Merge ' } | ForEach-Object { "- $_" }
  if ($lines.Count -gt $max) {
    $lines = @($lines | Select-Object -First $max) + "- ...and $($lines.Count - $max) more changes."
  }
  return @($lines)
}

$previous = Get-PreviousTag $Tag
$to = if (git tag --list $Tag) { $Tag } else { 'HEAD' }
$range = if ($previous) { "$previous..$to" } else { $to }
$subjects = @(git log $range --pretty=format:%s)
$version = $Tag.Substring(1)
$body = @("## What's new in Leviathan $version", '')
if ($previous) { $body += "Changes since $($previous.Substring(1)):", '' } else { $body += 'The first release.', '' }
$entries = Get-ReleaseNoteLines $subjects $Max
$body += if ($entries.Count -gt 0) { $entries } else { '- No changes listed.' }
$text = ($body -join "`n") + "`n"

if ($OutFile) { [System.IO.File]::WriteAllText($OutFile, $text, [System.Text.UTF8Encoding]::new($false)) }
$text
