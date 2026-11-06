<#
.SYNOPSIS
  Makes the ffprobe JSON samples that the parser's snapshot tests read (tests/Leviathan.Media.Tests/Samples).

.DESCRIPTION
  Generates a few tiny clips with ffmpeg (test patterns only, never real media), runs ffprobe on each exactly as Leviathan does, removes
  the file name and size (they differ per run), and writes the JSON. Run it again only when a new sample is wanted; the committed
  samples are what the tests read, so the tests do not need ffmpeg. The expected results are the .verified.json files next to them.

.PARAMETER FfmpegDir
  Folder with ffmpeg.exe and ffprobe.exe. Defaults to LEVIATHAN_FFMPEG_DIR, then the PATH.
#>
param([string] $FfmpegDir = $env:LEVIATHAN_FFMPEG_DIR)

$ErrorActionPreference = 'Stop'
$ffmpeg = if ($FfmpegDir) { Join-Path $FfmpegDir 'ffmpeg.exe' } else { (Get-Command ffmpeg).Source }
$ffprobe = if ($FfmpegDir) { Join-Path $FfmpegDir 'ffprobe.exe' } else { (Get-Command ffprobe).Source }
$out = Join-Path $PSScriptRoot '..\tests\Leviathan.Media.Tests\Samples'
New-Item -ItemType Directory -Force $out | Out-Null
$work = Join-Path ([IO.Path]::GetTempPath()) ('leviathan-samples-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory $work | Out-Null

function Make([string] $name, [string[]] $arguments) {
    $file = Join-Path $work $name
    & $ffmpeg -y -v error @arguments $file
    if ($LASTEXITCODE -ne 0) { throw "ffmpeg failed for $name" }
    $json = & $ffprobe -v error -hide_banner -show_format -show_streams -show_chapters -of json "file:$file" | Out-String
    $object = $json | ConvertFrom-Json
    # The name and size differ on every run; nothing else is touched.
    $object.format.PSObject.Properties.Remove('filename')
    $object.format.PSObject.Properties.Remove('size')
    $object | ConvertTo-Json -Depth 20 | Set-Content (Join-Path $out ($name + '.json')) -Encoding utf8
    Write-Host "made $name.json"
}

$video = @('-f', 'lavfi', '-i', 'testsrc=duration=2:size=128x72:rate=24')
$tone = @('-f', 'lavfi', '-i', 'sine=frequency=440:duration=2')

# A plain MP4: one video, one audio.
Make 'plain.mp4' ($video + $tone + @('-c:v', 'libx264', '-pix_fmt', 'yuv420p', '-c:a', 'aac', '-shortest'))

# A Matroska file with two audio languages, two subtitle tracks (one forced), chapters and titles.
$srt = Join-Path $work 'sub.srt'
"1`n00:00:00,000 --> 00:00:01,000`nHello`n" | Set-Content $srt -Encoding utf8
$meta = Join-Path $work 'chapters.txt'
@(';FFMETADATA1', 'title=Sample movie', '[CHAPTER]', 'TIMEBASE=1/1000', 'START=0', 'END=1000', 'title=Opening', '[CHAPTER]', 'TIMEBASE=1/1000', 'START=1000', 'END=2000', 'title=Ending') | Set-Content $meta -Encoding utf8
Make 'multi.mkv' ($video + $tone + @('-f', 'lavfi', '-i', 'sine=frequency=880:duration=2', '-i', $srt, '-i', $srt, '-i', $meta,
        '-map', '0', '-map', '1', '-map', '2', '-map', '3', '-map', '4', '-map_metadata', '5',
        '-c:v', 'libx264', '-pix_fmt', 'yuv420p', '-c:a', 'aac', '-c:s', 'srt',
        '-metadata:s:a:0', 'language=eng', '-metadata:s:a:0', 'title=Stereo', '-metadata:s:a:1', 'language=jpn',
        '-metadata:s:s:0', 'language=eng', '-metadata:s:s:1', 'language=jpn', '-disposition:s:1', 'forced', '-disposition:a:0', 'default', '-shortest'))

# 10-bit HEVC, once with PQ (HDR10) and once with HLG.
Make 'hdr10.mkv' ($video + @('-c:v', 'libx265', '-pix_fmt', 'yuv420p10le', '-x265-params', 'log-level=error:colorprim=bt2020:transfer=smpte2084:colormatrix=bt2020nc'))
Make 'hlg.mkv' ($video + @('-c:v', 'libx265', '-pix_fmt', 'yuv420p10le', '-x265-params', 'log-level=error:colorprim=bt2020:transfer=arib-std-b67:colormatrix=bt2020nc'))

# An MP3 with cover art (an attached picture is a video stream that is not video).
$cover = Join-Path $work 'cover.png'
& $ffmpeg -y -v error -f lavfi -i 'testsrc=duration=1:size=64x64:rate=1' -frames:v 1 $cover
Make 'cover.mp3' (@('-f', 'lavfi', '-i', 'sine=frequency=330:duration=2', '-i', $cover, '-map', '0', '-map', '1', '-c:a', 'libmp3lame', '-c:v', 'copy', '-id3v2_version', '3',
        '-metadata', 'title=Tone', '-metadata', 'artist=Test', '-disposition:v', 'attached_pic'))

# A tiny FLAC with no tags at all.
Make 'tone.flac' ($tone + @('-c:a', 'flac'))

Remove-Item $work -Recurse -Force
Write-Host "Samples are in $out"
