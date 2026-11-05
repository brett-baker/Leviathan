<#
.SYNOPSIS
  Builds the test folders the UAT playbooks (docs/uat) use, so nobody tests with real data.

.DESCRIPTION
  Creates %TEMP%\leviathan-uat (or -Root) with the same content every time: ordinary files, odd names, long paths, hidden and
  read-only files, empty files, a big sparse file, duplicates for conflict prompts, a 10,000-item folder and small media files.
  Running it again deletes and rebuilds the folder, so every playbook starts from a known state.

  -Share also publishes the "uat" folder as an SMB share called LeviathanUat on this machine (needs an elevated PowerShell),
  so the network steps can use \\localhost\LeviathanUat without touching a real NAS. -RemoveShare takes it away again.

.EXAMPLE
  .\tools\New-UatFixtures.ps1
  .\tools\New-UatFixtures.ps1 -Share        # elevated
  .\tools\New-UatFixtures.ps1 -RemoveShare  # elevated
#>
[CmdletBinding()]
param(
    [string]$Root = (Join-Path $env:LOCALAPPDATA 'Temp\leviathan-uat'),
    [switch]$Share,
    [switch]$RemoveShare
)

$ErrorActionPreference = 'Stop'
$shareName = 'LeviathanUat'

function Test-Elevated {
    $id = [Security.Principal.WindowsIdentity]::GetCurrent()
    return ([Security.Principal.WindowsPrincipal]$id).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

if ($RemoveShare) {
    if (-not (Test-Elevated)) { throw 'Removing the share needs an elevated PowerShell.' }
    if (Get-SmbShare -Name $shareName -ErrorAction SilentlyContinue) { Remove-SmbShare -Name $shareName -Force }
    Write-Host "Share $shareName removed."
    return
}

# Never wipe something that is not ours: the folder must be empty, absent, or carry our marker.
$marker = Join-Path $Root '.leviathan-uat'
if ((Test-Path $Root) -and -not (Test-Path $marker) -and (Get-ChildItem $Root -Force | Select-Object -First 1)) {
    throw "$Root exists and was not made by this script. Pick another -Root or empty it yourself."
}

if (Test-Path $Root) {
    Get-ChildItem $Root -Force | ForEach-Object {
        # \\?\ lets Remove-Item delete the long paths made below.
        Remove-Item -LiteralPath ('\\?\' + $_.FullName) -Recurse -Force
    }
}

$uat = Join-Path $Root 'uat'
New-Item -ItemType Directory -Path $uat -Force | Out-Null
Set-Content -LiteralPath $marker -Value 'Made by tools/New-UatFixtures.ps1; safe to delete.'

function New-TextFile([string]$Path, [string]$Text = 'leviathan uat') {
    $parent = Split-Path $Path -Parent
    [IO.Directory]::CreateDirectory($parent) | Out-Null
    [IO.File]::WriteAllText($Path, $Text)
}

# Deterministic bytes, so checksums and verify steps give the same answer every run.
function New-BinaryFile([string]$Path, [long]$Bytes) {
    [IO.Directory]::CreateDirectory((Split-Path $Path -Parent)) | Out-Null
    $block = New-Object byte[] 65536
    for ($i = 0; $i -lt $block.Length; $i++) { $block[$i] = [byte](($i * 31 + 7) % 251) }
    $stream = [IO.File]::Create($Path)
    try {
        $left = $Bytes
        while ($left -gt 0) {
            $n = [int][Math]::Min($left, $block.Length)
            $stream.Write($block, 0, $n)
            $left -= $n
        }
    } finally { $stream.Dispose() }
}

# 1. Everyday: a few folders and files of different kinds.
$basic = Join-Path $uat '01-basic'
foreach ($name in 'notes.txt', 'todo.md', 'report.docx', 'budget.xlsx', 'readme.pdf', 'script.ps1') {
    New-TextFile (Join-Path $basic $name) "content of $name"
}
New-BinaryFile (Join-Path $basic 'photo-small.jpg') 48KB
New-BinaryFile (Join-Path $basic 'archive.zip') 200KB
New-BinaryFile (Join-Path $basic 'movie-small.mkv') 5MB
foreach ($d in 'Documents', 'Pictures', 'Music', 'Empty folder') {
    New-Item -ItemType Directory -Path (Join-Path $basic $d) -Force | Out-Null
}
New-TextFile (Join-Path $basic 'Documents\deep\deeper\deepest\note.txt')

# 2. Odd names: spaces, Unicode, emoji, trailing dots and spaces (via \\?\), leading dots, reserved-looking names, very long names.
$odd = Join-Path $uat '02-odd-names'
foreach ($name in 'file with   spaces.txt', 'ünïcödé-ファイル-文件.txt', 'emoji 🎬 clip.txt', '.leading-dot.txt', 'UPPER and lower.TXT',
                  'a&b#c%d.txt', "it's.txt", 'two..dots.txt', ('x' * 200 + '.txt')) {
    New-TextFile (Join-Path $odd $name)
}
# Windows Explorer cannot make these; \\?\ can. They exist on NAS boxes and break careless code.
New-TextFile ('\\?\' + (Join-Path $odd 'trailing-dot.')) 'name ends with a dot'
New-TextFile ('\\?\' + (Join-Path $odd 'trailing-space ')) 'name ends with a space'
New-TextFile ('\\?\' + (Join-Path $odd 'CON.txt')) 'reserved device name'

# 3. Long paths: a path over 260 characters, and one near the 32,767 limit.
$long = Join-Path $uat '03-long-paths'
$segment = 'segment-of-a-long-folder-name-0123456789'
$deep = $long
for ($i = 0; $i -lt 7; $i++) { $deep = Join-Path $deep $segment }
New-TextFile ('\\?\' + (Join-Path $deep 'file-at-the-bottom.txt')) 'over 260 characters'

# 4. Attributes and sizes.
$attr = Join-Path $uat '04-attributes'
New-TextFile (Join-Path $attr 'visible.txt')
New-TextFile (Join-Path $attr 'hidden.txt')
(Get-Item -LiteralPath (Join-Path $attr 'hidden.txt')).Attributes = 'Hidden'
New-TextFile (Join-Path $attr 'read-only.txt')
(Get-Item -LiteralPath (Join-Path $attr 'read-only.txt')).IsReadOnly = $true
New-TextFile (Join-Path $attr 'system-hidden.txt')
(Get-Item -LiteralPath (Join-Path $attr 'system-hidden.txt')).Attributes = 'Hidden, System'
[IO.File]::WriteAllBytes((Join-Path $attr 'empty.bin'), [byte[]]@())
New-BinaryFile (Join-Path $attr 'one-byte.bin') 1
# A 6 GiB sparse file: occupies almost no disk, but copy, verify and progress behave as for a big file (sampled verify above 4 GB).
$big = Join-Path $attr 'big-sparse-6GiB.bin'
[IO.File]::WriteAllBytes($big, [byte[]]@())
& fsutil sparse setflag $big | Out-Null
$fs = [IO.File]::Open($big, 'Open', 'Write')
try { $fs.SetLength(6GB); $fs.Position = 1GB; $fs.Write([byte[]](1..64), 0, 64) } finally { $fs.Dispose() }

# 5. Conflicts: the same names in two folders with different content, sizes and dates.
$src = Join-Path $uat '05-conflicts\source'
$dst = Join-Path $uat '05-conflicts\destination'
New-TextFile (Join-Path $src 'same-name-newer.txt') 'source copy, newer and longer than the destination copy'
New-TextFile (Join-Path $dst 'same-name-newer.txt') 'old'
New-TextFile (Join-Path $src 'same-name-older.txt') 'source copy, older'
New-TextFile (Join-Path $dst 'same-name-older.txt') 'destination copy, newer'
New-TextFile (Join-Path $src 'identical.txt') 'identical content'
New-TextFile (Join-Path $dst 'identical.txt') 'identical content'
New-TextFile (Join-Path $src 'only-in-source.txt') 'only here'
(Get-Item -LiteralPath (Join-Path $src 'same-name-newer.txt')).LastWriteTime = (Get-Date).AddDays(-1)
(Get-Item -LiteralPath (Join-Path $dst 'same-name-newer.txt')).LastWriteTime = (Get-Date).AddDays(-30)
(Get-Item -LiteralPath (Join-Path $src 'same-name-older.txt')).LastWriteTime = (Get-Date).AddDays(-30)
(Get-Item -LiteralPath (Join-Path $dst 'same-name-older.txt')).LastWriteTime = (Get-Date).AddDays(-1)

# 6. A big folder: 10,000 small files, the spec's listing budget.
$many = Join-Path $uat '06-ten-thousand'
[IO.Directory]::CreateDirectory($many) | Out-Null
1..10000 | ForEach-Object { [IO.File]::WriteAllText((Join-Path $many ('item-{0:D5}.txt' -f $_)), 'x') }

# 7. Bulk to copy: a few hundred MB across many files, for progress, pause/resume and cancel.
$bulk = Join-Path $uat '07-bulk-copy'
1..5 | ForEach-Object { New-BinaryFile (Join-Path $bulk ('large-{0}.bin' -f $_)) 64MB }
1..200 | ForEach-Object { New-BinaryFile (Join-Path $bulk ('small\file-{0:D3}.bin' -f $_)) 64KB }

# 8. Empty targets to copy into.
foreach ($name in 'target-a', 'target-b') { New-Item -ItemType Directory -Path (Join-Path $uat "08-targets\$name") -Force | Out-Null }

$count = (Get-ChildItem -LiteralPath ('\\?\' + $uat) -Recurse -Force -File | Measure-Object).Count
Write-Host "Fixtures ready: $uat ($count files)"

if ($Share) {
    if (-not (Test-Elevated)) { throw 'Publishing the share needs an elevated PowerShell. Run again as administrator, or leave out -Share.' }
    if (Get-SmbShare -Name $shareName -ErrorAction SilentlyContinue) { Remove-SmbShare -Name $shareName -Force }
    New-SmbShare -Name $shareName -Path $uat -FullAccess ([Security.Principal.WindowsIdentity]::GetCurrent().Name) | Out-Null
    Write-Host "Shared as \\localhost\$shareName (remove with -RemoveShare)."
}
