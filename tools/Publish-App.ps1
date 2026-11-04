<#
.SYNOPSIS
  Publishes Leviathan ready-to-run (precompiled) so it starts faster (spec 3.1, cold start under 600 ms).

.DESCRIPTION
  A plain build runs on the JIT, which costs about a quarter of a second at start. ReadyToRun precompiles the app's own assemblies.
  WinUI's publish step leaves out the compiled XAML (.xbf) and resource index (.pri), which this script copies from the build output;
  without them the app crashes as soon as the first window is created.

  Measure the result with:
    dotnet run -c Release --project tests/Leviathan.Benchmarks -p:Platform=x64 -- --gate --cold-start <output>\Leviathan.exe
#>
param([string]$Output = (Join-Path $PSScriptRoot '..\artifacts\publish'))

$ErrorActionPreference = 'Stop'
$root = Resolve-Path (Join-Path $PSScriptRoot '..')
dotnet publish "$root\src\Leviathan.App" -c Release -p:Platform=x64 -r win-x64 -p:PublishReadyToRun=true -o $Output
if ($LASTEXITCODE -ne 0) { throw 'publish failed' }
$build = Join-Path $root 'src\Leviathan.App\bin\x64\Release\net10.0-windows10.0.26100.0\win-x64'
Copy-Item "$build\*.xbf", "$build\Leviathan.pri" $Output -Force
Write-Host "Published to $Output"
