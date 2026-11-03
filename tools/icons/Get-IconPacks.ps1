<#
.SYNOPSIS
  Downloads the icon packs listed in packs.json at their pinned versions into src/Leviathan.App/Assets/Icons and writes each
  pack's pack.json (the manifest the app reads) plus its licence file.

.DESCRIPTION
  Re-running is safe: files are overwritten. Reports any icon it cannot find so the mapping in packs.json can be fixed.
  Only github.com raw URLs at a pinned commit or tag are used, so the result is reproducible.
#>
param(
    [string]$Only = ''
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$definition = Get-Content (Join-Path $PSScriptRoot 'packs.json') -Raw | ConvertFrom-Json
$target = Join-Path $root 'src/Leviathan.App/Assets/Icons'

function Get-Url($pack, $spec) {
    if ($pack.source -eq 'fluent') {
        $name, $style = $spec -split ':'
        if (-not $style) { $style = 'regular' }
        $snake = ($name.ToLowerInvariant() -replace ' ', '_')
        $folder = [uri]::EscapeDataString($name)
        $file = "ic_fluent_${snake}_20_${style}.svg"
        return @{ Url = "https://raw.githubusercontent.com/microsoft/fluentui-system-icons/$($pack.ref)/assets/$folder/SVG/$file"; Color = ($style -eq 'color') }
    }

    $style, $name = $spec -split '/'
    return @{ Url = "https://raw.githubusercontent.com/FortAwesome/Font-Awesome/$($pack.ref)/svgs/$style/$name.svg"; Color = $false }
}

foreach ($pack in $definition.packs) {
    if ($Only -and $pack.id -ne $Only) { continue }
    $folder = Join-Path $target $pack.id
    New-Item -ItemType Directory -Force $folder | Out-Null
    $icons = [ordered]@{}
    $missing = @()
    foreach ($property in $pack.icons.PSObject.Properties) {
        $key = $property.Name
        $info = Get-Url $pack $property.Value
        $file = "$key.svg"
        try {
            Invoke-WebRequest -Uri $info.Url -OutFile (Join-Path $folder $file) -UseBasicParsing
            $icons[$key] = [ordered]@{ file = $file; color = $info.Color; source = $property.Value }
        }
        catch {
            $missing += "$key ($($property.Value)): $($info.Url)"
        }
    }

    Invoke-WebRequest -Uri $pack.licenseUrl -OutFile (Join-Path $folder 'LICENSE.txt') -UseBasicParsing
    $manifest = [ordered]@{
        id = $pack.id
        name = $pack.name
        description = $pack.description
        license = $pack.license
        attribution = $pack.attribution
        version = $pack.ref
        icons = $icons
    }
    [System.IO.File]::WriteAllText((Join-Path $folder 'pack.json'), ($manifest | ConvertTo-Json -Depth 5), (New-Object System.Text.UTF8Encoding($false)))
    "{0}: {1} icons downloaded, {2} missing" -f $pack.id, $icons.Count, $missing.Count
    $missing | ForEach-Object { "  MISSING $_" }
}
