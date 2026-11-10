<#
.SYNOPSIS
  Builds the test folders the UAT playbooks (docs/uat) use, so nobody tests with real data.

.DESCRIPTION
  Creates D:\Playbooks (or -Root) with the same content every time: ordinary files, odd names, long paths, hidden and
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
    [string]$Root = 'D:\Playbooks',
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

$uat = Join-Path $Root 'uat'
if (Test-Path $uat) {
    # Only the fixture folder is rebuilt; the scratch data and export folders next to it (data, sheets-out) are left alone.
    Get-ChildItem $uat -Force | ForEach-Object {
        # \\?\ lets Remove-Item delete the long paths made below.
        Remove-Item -LiteralPath ('\\?\' + $_.FullName) -Recurse -Force
    }
}

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

# 9. A few tiny clips for the media columns (needs ffmpeg on the PATH or in LEVIATHAN_FFMPEG_DIR; skipped when it is missing).
$media = Join-Path $uat '09-media'
New-Item -ItemType Directory -Path $media -Force | Out-Null
$ff = if ($env:LEVIATHAN_FFMPEG_DIR) { Join-Path $env:LEVIATHAN_FFMPEG_DIR 'ffmpeg.exe' } else { (Get-Command ffmpeg -ErrorAction SilentlyContinue).Source }
if ($ff) {
    function Pattern([int]$secs) { @('-f', 'lavfi', '-i', "testsrc=duration=${secs}:size=320x180:rate=30000/1001") }
    function Tone([int]$secs) { @('-f', 'lavfi', '-i', "sine=frequency=440:duration=${secs}") }
    & $ff -y -v error @(Pattern 3) @(Tone 3) -c:v libx264 -pix_fmt yuv420p -c:a aac -shortest (Join-Path $media 'short-3s.mp4')
    & $ff -y -v error @(Pattern 45) @(Tone 45) -c:v libx264 -pix_fmt yuv420p -c:a aac -shortest (Join-Path $media 'medium-45s.mkv')
    & $ff -y -v error @(Pattern 125) -c:v libx264 -pix_fmt yuv420p (Join-Path $media 'long-2m05.avi')
    & $ff -y -v error @(Pattern 5) -c:v libx265 -pix_fmt yuv420p10le -x265-params 'log-level=error:colorprim=bt2020:transfer=smpte2084:colormatrix=bt2020nc' (Join-Path $media 'hdr10-5s.mkv')
    & $ff -y -v error @(Tone 20) -c:a flac (Join-Path $media 'song-20s.flac')
} else {
    Write-Warning 'ffmpeg not found: 09-media has only the broken clip and the text file.'
}
[IO.File]::WriteAllBytes((Join-Path $media 'broken.mp4'), [byte[]](1..200 | ForEach-Object { Get-Random -Maximum 256 }))
[IO.File]::WriteAllText((Join-Path $media 'notes.txt'), 'not media')

# 10. A triage inbox: six short clips of different lengths and sizes (for the triage playbook), plus an empty keep folder.
$triage = Join-Path $uat '10-triage'
New-Item -ItemType Directory -Path (Join-Path $triage 'inbox') -Force | Out-Null
New-Item -ItemType Directory -Path (Join-Path $triage 'keep') -Force | Out-Null
if ($ff) {
    $sizes = @{ 'clip1-red' = 8; 'clip2-blue' = 12; 'clip3-long' = 30; 'clip4-short' = 4; 'clip5-odd name (1) [x].mp4' = 10; 'clip6-last' = 6 }
    foreach ($name in $sizes.Keys) {
        $file = if ($name.EndsWith('.mp4')) { $name } else { "$name.mp4" }
        & $ff -y -v error @(Pattern $sizes[$name]) @(Tone $sizes[$name]) -c:v libx264 -pix_fmt yuv420p -c:a aac -shortest (Join-Path $triage "inbox\$file")
    }
}

# 11. One of each kind the preview pane and the details pane draw: pictures, text in two encodings, code, Markdown, HTML with a
#     script (it must NOT run), a 3 MB log (the pane shows the first 2 MB), a PDF, a zip, a font, a Word document, a folder.
$preview = Join-Path $uat '11-preview'
New-Item -ItemType Directory -Path $preview -Force | Out-Null
try {
    Add-Type -AssemblyName System.Drawing
    $bmp = New-Object System.Drawing.Bitmap 640, 400
    $gfx = [System.Drawing.Graphics]::FromImage($bmp)
    $gfx.Clear([System.Drawing.Color]::SteelBlue)
    $gfx.FillEllipse([System.Drawing.Brushes]::Orange, 80, 60, 240, 240)
    $gfx.DrawString('Leviathan UAT', (New-Object System.Drawing.Font 'Segoe UI', 32), [System.Drawing.Brushes]::White, 40, 320)
    $gfx.Dispose()
    $bmp.Save((Join-Path $preview 'picture.png'), [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Save((Join-Path $preview 'photo.jpg'), [System.Drawing.Imaging.ImageFormat]::Jpeg)
    $bmp.Dispose()
} catch { Write-Warning "System.Drawing is not available: no picture fixtures ($_)" }
if ($ff) { & $ff -y -v error @(Pattern 2) -vf 'fps=10,scale=160:-1' (Join-Path $preview 'animated.gif') }
New-TextFile (Join-Path $preview 'notes.txt') "Plain text`r`nsecond line`r`n"
[IO.File]::WriteAllText((Join-Path $preview 'utf16.txt'), "UTF-16 text: caf$([char]0xE9) $([char]0x4E2D)$([char]0x6587)`r`n", [Text.Encoding]::Unicode)
New-TextFile (Join-Path $preview 'sample.cs') "using System;`nclass Hello { static void Main() { Console.WriteLine(`"hi`"); } }`n"
New-TextFile (Join-Path $preview 'readme.md') "# Heading`n`nSome **bold** text and a [link](https://example.com).`n`n- one`n- two`n"
New-TextFile (Join-Path $preview 'page.html') "<html><body><h1>HTML page</h1><script>document.body.insertAdjacentHTML('beforeend','<p>SCRIPT RAN</p>')</script></body></html>"
$logLine = ('log line ' * 10) + "`n"
[IO.File]::WriteAllText((Join-Path $preview 'big.log'), ($logLine * 33000))

# A one-page PDF with correct offsets, so every reader opens it without repairing it.
$pdfText = 'BT /F1 24 Tf 40 100 Td (Leviathan PDF) Tj ET'
$pdfBodies = @(
    '<< /Type /Catalog /Pages 2 0 R >>',
    '<< /Type /Pages /Kids [3 0 R] /Count 1 >>',
    '<< /Type /Page /Parent 2 0 R /MediaBox [0 0 300 200] /Contents 4 0 R /Resources << /Font << /F1 5 0 R >> >> >>',
    "<< /Length $($pdfText.Length) >>`nstream`n$pdfText`nendstream",
    '<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>')
$pdf = New-Object Text.StringBuilder
[void]$pdf.Append("%PDF-1.4`n")
$pdfOffsets = @()
for ($i = 0; $i -lt $pdfBodies.Count; $i++) {
    $pdfOffsets += $pdf.Length
    [void]$pdf.Append("$($i + 1) 0 obj`n$($pdfBodies[$i])`nendobj`n")
}
$xref = $pdf.Length
[void]$pdf.Append("xref`n0 6`n0000000000 65535 f `n")
foreach ($o in $pdfOffsets) { [void]$pdf.Append(("{0:D10} 00000 n `n" -f $o)) }
[void]$pdf.Append("trailer`n<< /Size 6 /Root 1 0 R >>`nstartxref`n$xref`n%%EOF`n")
[IO.File]::WriteAllText((Join-Path $preview 'sample.pdf'), $pdf.ToString(), [Text.Encoding]::ASCII)

# A zip with a folder inside, a font copied from Windows, and a minimal Word document built by hand.
$zipSource = Join-Path $preview 'zip-source'
New-TextFile (Join-Path $zipSource 'one.txt') 'first'
New-TextFile (Join-Path $zipSource 'inner\two.txt') 'second'
Compress-Archive -Path (Join-Path $zipSource '*') -DestinationPath (Join-Path $preview 'sample.zip') -Force
Remove-Item -LiteralPath $zipSource -Recurse -Force
$arial = Join-Path $env:WINDIR 'Fonts\arial.ttf'
if (Test-Path $arial) { Copy-Item $arial (Join-Path $preview 'arial-copy.ttf') }
Add-Type -AssemblyName System.IO.Compression.FileSystem
function Add-ZipText($zip, [string]$name, [string]$text) {
    $entry = $zip.CreateEntry($name)
    $writer = New-Object IO.StreamWriter($entry.Open())
    $writer.Write($text)
    $writer.Dispose()
}
$word = [IO.Compression.ZipFile]::Open((Join-Path $preview 'letter.docx'), 'Create')
Add-ZipText $word '[Content_Types].xml' '<?xml version="1.0" encoding="UTF-8"?><Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"><Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/><Default Extension="xml" ContentType="application/xml"/><Override PartName="/word/document.xml" ContentType="application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml"/></Types>'
Add-ZipText $word '_rels/.rels' '<?xml version="1.0" encoding="UTF-8"?><Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="word/document.xml"/></Relationships>'
Add-ZipText $word 'word/document.xml' '<?xml version="1.0" encoding="UTF-8"?><w:document xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main"><w:body><w:p><w:r><w:t>Leviathan UAT letter</w:t></w:r></w:p></w:body></w:document>'
$word.Dispose()
foreach ($n in 1..3) { New-TextFile (Join-Path $preview "a-folder\item-$n.txt") "item $n" }

# 12. Duplicates: one folder with look-alike names, and a left/right pair for "files here that are already in the other pane".
$dups = Join-Path $uat '12-duplicates'
$one = Join-Path $dups 'one-folder'
New-BinaryFile (Join-Path $one 'Movie.Name.2019.1080p.x264-GRP.mkv') 2MB
New-BinaryFile (Join-Path $one 'Movie Name (2019) 720p.mp4') 1MB
New-BinaryFile (Join-Path $one 'Movie.Name.2019.2160p.HEVC-OTHER.mkv') 3MB
New-BinaryFile (Join-Path $one 'Other Film 2020.mkv') 1MB
New-BinaryFile (Join-Path $one 'Holiday photos 2018.zip') 300KB
New-BinaryFile (Join-Path $one 'Holiday Photos 2018 (1).zip') 300KB
New-BinaryFile (Join-Path $one 'unrelated-thing.dat') 10KB
$left = Join-Path $dups 'left'
$right = Join-Path $dups 'right'
New-BinaryFile (Join-Path $left 'Show S01E01 720p.mkv') 1MB
New-BinaryFile (Join-Path $left 'Show S01E02 1080p.mkv') 2MB
New-BinaryFile (Join-Path $left 'Show S01E03 1080p.mkv') 2MB
New-BinaryFile (Join-Path $left 'Only on the left.mkv') 1MB
New-BinaryFile (Join-Path $right 'Show.S01E01.1080p.x265.mkv') 3MB
New-BinaryFile (Join-Path $right 'Show S01E02 1080p.mkv') 2MB
New-BinaryFile (Join-Path $right 'Show S01E03 480p.mkv') 500KB

# 13. Colour tags: six small files, one in a sub-folder. Tag them with the keys 1 to 9; the tags live on the files in this folder only.
$tags = Join-Path $uat '13-tags'
foreach ($n in 'alpha.txt', 'bravo.txt', 'charlie.txt', 'delta.txt', 'echo.txt', 'sub\foxtrot.txt') { New-TextFile (Join-Path $tags $n) "tag me: $n" }
Copy-Item (Join-Path $preview 'photo.jpg') (Join-Path $tags 'golf.jpg') -ErrorAction SilentlyContinue

# 14. Damaged media: eleven kinds of damage per clip (the same kinds, in the same order, as tests/Leviathan.Testing/CorruptMedia.cs), so
#     the app can be shown every way a video or a song goes wrong. Needs the clips of 09-media (so ffmpeg).
$corrupt = Join-Path $uat '14-corrupt'
New-Item -ItemType Directory -Path $corrupt -Force | Out-Null
function New-DamagedCopies([string]$Source, [string]$Stem) {
    if (-not (Test-Path $Source)) { return }
    $bytes = [IO.File]::ReadAllBytes($Source)
    $ext = [IO.Path]::GetExtension($Source)
    function Save([string]$Kind, [byte[]]$Data) { [IO.File]::WriteAllBytes((Join-Path $corrupt "$Stem.$Kind$ext"), $Data) }
    function Head([int]$n) { $n = [Math]::Min($bytes.Length, [Math]::Max(1, $n)); $bytes[0..($n - 1)] }
    Save 'Empty' ([byte[]]@())
    Save 'OneByte' ([byte[]]@($bytes[0]))
    Save 'TruncatedTenth' ([byte[]](Head ([int]($bytes.Length / 10))))
    Save 'TruncatedHalf' ([byte[]](Head ([int]($bytes.Length / 2))))
    Save 'TruncatedTail' ([byte[]](Head ($bytes.Length - 200)))
    $zeroed = [byte[]]$bytes.Clone(); [Array]::Clear($zeroed, 0, [Math]::Min(4096, $zeroed.Length)); Save 'HeaderZeroed' $zeroed
    $flipped = [byte[]]$bytes.Clone(); $rng = New-Object Random 20261110
    if ($flipped.Length -gt 8192) { 1..64 | ForEach-Object { $at = [int]($flipped.Length / 4) + $rng.Next([int]($flipped.Length / 2)); $flipped[$at] = $flipped[$at] -bxor 0xFF } }
    Save 'MiddleFlips' $flipped
    $garbage = New-Object byte[] $bytes.Length; (New-Object Random 20261111).NextBytes($garbage); Save 'RandomGarbage' $garbage
    Save 'ZeroFilled' (New-Object byte[] $bytes.Length)
    Save 'TextInDisguise' ([Text.Encoding]::ASCII.GetBytes("This is not a video. It is a note someone renamed.`r`n"))
    $huge = [byte[]]$bytes.Clone(); 0..3 | ForEach-Object { $huge[$_] = 0xFF }; Save 'HugeFirstSize' $huge
}
New-DamagedCopies (Join-Path $media 'medium-45s.mkv') 'video'
New-DamagedCopies (Join-Path $media 'short-3s.mp4') 'clip'
New-DamagedCopies (Join-Path $media 'song-20s.flac') 'song'
Copy-Item (Join-Path $media 'short-3s.mp4') (Join-Path $corrupt 'intact-control.mp4') -ErrorAction SilentlyContinue

$count = (Get-ChildItem -LiteralPath ('\\?\' + $uat) -Recurse -Force -File | Measure-Object).Count
Write-Host "Fixtures ready: $uat ($count files)"

if ($Share) {
    if (-not (Test-Elevated)) { throw 'Publishing the share needs an elevated PowerShell. Run again as administrator, or leave out -Share.' }
    if (Get-SmbShare -Name $shareName -ErrorAction SilentlyContinue) { Remove-SmbShare -Name $shareName -Force }
    New-SmbShare -Name $shareName -Path $uat -FullAccess ([Security.Principal.WindowsIdentity]::GetCurrent().Name) | Out-Null
    Write-Host "Shared as \\localhost\$shareName (remove with -RemoveShare)."
}
