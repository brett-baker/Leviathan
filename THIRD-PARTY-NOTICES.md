# Third-party notices

Leviathan uses the following packages (see `Directory.Packages.props` for versions). Each is used under its own license.

| Package | License |
| --- | --- |
| Microsoft.WindowsAppSDK, Microsoft.Windows.SDK.BuildTools | MIT |
| Microsoft.Windows.CsWin32 (build-time source generator) | MIT |
| CommunityToolkit.Mvvm | MIT |
| Microsoft.Extensions.* (Hosting, Logging, DependencyInjection) | MIT |
| Microsoft.Data.Sqlite (SQLite: public domain) | MIT |
| StreamJsonRpc | MIT |
| System.CommandLine, System.IO.Hashing | MIT |
| OpenTelemetry .NET | Apache-2.0 |
| System.IO.Abstractions (TestableIO) | MIT |
| xunit.v3, xunit.runner.visualstudio | Apache-2.0 |
| Microsoft.NET.Test.Sdk, coverlet.collector | MIT |
| BenchmarkDotNet | MIT |

## Code ported from Files (brett-baker/Files)

No code has been ported yet. Phase 0 is new code. When code is ported from the Files fork (dual-licensed MIT / MPL-2.0),
each file is listed here with its original path and license. Files under MPL-2.0 keep that license for their contents.

| Leviathan file | Original file in Files | License |
| --- | --- | --- |
| — | — | — |
| `src/Leviathan.Providers.Cloud/CloudDetectors.cs` (logic adapted, rewritten against `IRegistryView` / `IFileSystem`) | `src/Files.App/Utils/Cloud/CloudDrivesDetector.cs`, `Detector/{Box,DropBox,GoogleDrive,SynologyDrive}CloudDetector.cs` | MIT |
| `src/Leviathan.Interop/ShellThumbnailSource.cs` (GetImage-then-copy-bitmap approach, rewritten to return BGRA) | `src/Files.App.Storage/Windows/Helpers/WindowsStorableHelpers.Icon.cs` | MPL-2.0 |
| `src/Leviathan.Interop/WindowsFileAssociations.cs` (AssocQueryString lookup, generalized) | `src/Files.App/Utils/Shell/FileAssociationHelpers.cs` | MPL-2.0 |
| `src/Leviathan.ViewModels/NewMenu/ShellNewCatalog.cs` (ShellNew scan, rewritten against `IRegistryView` / `IFileSystem`) | `src/Files.App/Utils/Shell/ShellNewMenuHelper.cs` | MIT |
| `src/Leviathan.Interop/ShellContextMenu.cs` (menu build and read, submenu loading, command-string guard, invoke; rewritten against pointer-style bindings and contract types) | `src/Files.App/Utils/Shell/ContextMenu.cs` | MIT |
| `src/Leviathan.Interop/StaMessageThread.cs` (idea of an STA thread with a message loop, new code) | `src/Files.App/Utils/Shell/ContextMenuWorkerPool.cs` / `ThreadWithMessageQueue` (design only, no code copied) | MPL-2.0 |

## Icon packs (`src/Leviathan.App/Assets/Icons`)

Downloaded at pinned versions by `tools/icons/Get-IconPacks.ps1`; each pack folder carries its licence text (`LICENSE.txt`) and
a `pack.json` naming the upstream icon behind every file. The user chooses the pack in Options > Icon set (setting
`Appearance.IconPack`).

| Pack | Version | Licence | Credit |
| --- | --- | --- | --- |
| Fluent UI System Icons (`fluent`) | commit `9bfae3d` | MIT | Copyright (c) Microsoft Corporation. https://github.com/microsoft/fluentui-system-icons |
| Font Awesome Free (`fontawesome`) | 6.7.2 | Icons CC BY 4.0 | Font Awesome Free 6.7.2 by @fontawesome (https://fontawesome.com), icons licensed CC BY 4.0, https://creativecommons.org/licenses/by/4.0/. Changes: single-colour icons are recoloured at run time. |
