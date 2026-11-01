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
