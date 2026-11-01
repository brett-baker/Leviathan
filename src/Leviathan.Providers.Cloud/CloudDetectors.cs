// Detection logic adapted from Files (brett-baker/Files) src/Files.App/Utils/Cloud: CloudDrivesDetector.cs and
// Detector/*.cs, Copyright (c) Files Community, MIT License. Rewritten against IRegistryView and IFileSystem so it can be
// tested and so no detector touches the real machine in unit tests. See THIRD-PARTY-NOTICES.md.

using System.IO.Abstractions;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using System.Text.Json;
using Leviathan.Core.Paths;
using Leviathan.Core.Platform;
using Microsoft.Data.Sqlite;

namespace Leviathan.Providers.Cloud;

/// <summary>Finds the folders of one family of sync clients.</summary>
public interface ICloudDetector
{
    /// <summary>Detects providers. May throw; <see cref="CloudProviderCatalog"/> treats a failing detector as finding nothing.</summary>
    Task<IReadOnlyList<CloudProviderInfo>> DetectAsync(CancellationToken cancellationToken);
}

internal static class DetectorHelpers
{
    public static CloudProviderInfo? Make(CloudProviderKind kind, string name, string? folder)
    {
        if (string.IsNullOrWhiteSpace(folder))
        {
            return null;
        }

        var cleaned = folder.Trim();
        if (cleaned.StartsWith(@"\\?\", StringComparison.Ordinal))
        {
            cleaned = cleaned[@"\\?\".Length..];
        }

        // A bare drive letter ("G") stored by some clients is the root of that drive.
        if (cleaned.Length == 1 && char.IsLetter(cleaned[0]))
        {
            cleaned += @":\";
        }

        return LocationPath.TryParse(cleaned, out var location) ? new CloudProviderInfo(kind, name, location) : null;
    }

    public static string Text(object? value) => value as string ?? string.Empty;
}

/// <summary>OneDrive accounts (personal and business) from <c>HKCU\SOFTWARE\Microsoft\OneDrive\Accounts</c>.</summary>
public sealed class OneDriveDetector(IRegistryView registry) : ICloudDetector
{
    internal const string AccountsKey = @"SOFTWARE\Microsoft\OneDrive\Accounts";

    /// <inheritdoc />
    public Task<IReadOnlyList<CloudProviderInfo>> DetectAsync(CancellationToken cancellationToken)
    {
        var found = new List<CloudProviderInfo>();
        foreach (var account in registry.GetSubKeyNames(RegistryRoot.CurrentUser, AccountsKey))
        {
            var key = $@"{AccountsKey}\{account}";
            var displayName = DetectorHelpers.Text(registry.GetValue(RegistryRoot.CurrentUser, key, "DisplayName"));
            var userFolder = DetectorHelpers.Text(registry.GetValue(RegistryRoot.CurrentUser, key, "UserFolder"));
            var name = string.IsNullOrWhiteSpace(displayName) ? "OneDrive" : $"OneDrive - {displayName}";
            if (DetectorHelpers.Make(CloudProviderKind.OneDrive, name, userFolder) is { } provider && !found.Any(f => f.Name == name))
            {
                found.Add(provider);
            }
        }

        return Task.FromResult<IReadOnlyList<CloudProviderInfo>>(found);
    }
}

/// <summary>SharePoint libraries synced through OneDrive: the parent of each mount point other than the user folder.</summary>
public sealed class SharePointDetector(IRegistryView registry) : ICloudDetector
{
    /// <inheritdoc />
    public Task<IReadOnlyList<CloudProviderInfo>> DetectAsync(CancellationToken cancellationToken)
    {
        var found = new List<CloudProviderInfo>();
        foreach (var account in registry.GetSubKeyNames(RegistryRoot.CurrentUser, OneDriveDetector.AccountsKey))
        {
            var key = $@"{OneDriveDetector.AccountsKey}\{account}";
            var userFolder = DetectorHelpers.Text(registry.GetValue(RegistryRoot.CurrentUser, key, "UserFolder"));
            var cache = $@"{key}\ScopeIdToMountPointPathCache";
            var parents = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var valueName in registry.GetValueNames(RegistryRoot.CurrentUser, cache))
            {
                var directory = DetectorHelpers.Text(registry.GetValue(RegistryRoot.CurrentUser, cache, valueName));
                if (directory.Length > 0 && !directory.Equals(userFolder, StringComparison.OrdinalIgnoreCase)
                    && Path.GetDirectoryName(directory.TrimEnd('\\')) is { Length: > 0 } parent)
                {
                    parents.Add(parent);
                }
            }

            foreach (var parent in parents)
            {
                var name = $"SharePoint - {Path.GetFileName(parent)}";
                if (!found.Any(f => f.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                    && DetectorHelpers.Make(CloudProviderKind.OneDriveCommercial, name, parent) is { } provider)
                {
                    found.Add(provider);
                }
            }
        }

        return Task.FromResult<IReadOnlyList<CloudProviderInfo>>(found);
    }
}

/// <summary>
/// Clients that pin themselves into the Explorer navigation pane (iCloud, Proton Drive, MEGA, Nextcloud, ownCloud, kDrive and
/// others): found through the Desktop NameSpace key and the CLSID it points at.
/// </summary>
public sealed class NavigationPaneDetector(IRegistryView registry) : ICloudDetector
{
    private const string NamespaceKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\Desktop\NameSpace";
    private const string SyncRootKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\SyncRootManager";

    /// <inheritdoc />
    public Task<IReadOnlyList<CloudProviderInfo>> DetectAsync(CancellationToken cancellationToken)
    {
        var found = new List<CloudProviderInfo>();
        foreach (var id in registry.GetSubKeyNames(RegistryRoot.CurrentUser, NamespaceKey))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var clsid = $@"CLSID\{id}";
            if (registry.GetValue(RegistryRoot.ClassesRoot, clsid, "System.IsPinnedToNameSpaceTree") is not 1)
            {
                continue;
            }

            var identifier = DetectorHelpers.Text(registry.GetValue(RegistryRoot.CurrentUser, $@"{NamespaceKey}\{id}", null));
            if (identifier.Length == 0)
            {
                continue;
            }

            var folder = DetectorHelpers.Text(registry.GetValue(RegistryRoot.ClassesRoot, $@"{clsid}\Instance\InitPropertyBag", "TargetFolderPath"));
            var applicationName = DetectorHelpers.Text(registry.GetValue(RegistryRoot.CurrentUser, $@"{NamespaceKey}\{id}", "ApplicationName"));
            var clsidName = DetectorHelpers.Text(registry.GetValue(RegistryRoot.ClassesRoot, clsid, null));
            var syncRootName = DetectorHelpers.Text(registry.GetValue(RegistryRoot.LocalMachine, $@"{SyncRootKey}\{identifier}", null));

            var provider = Classify(identifier, applicationName, syncRootName, clsidName, folder);
            if (provider is not null)
            {
                found.Add(provider);
            }
        }

        return Task.FromResult<IReadOnlyList<CloudProviderInfo>>(found);
    }

    private static CloudProviderInfo? Classify(string identifier, string applicationName, string syncRootName, string clsidName, string folder)
    {
        string Or(string value, string fallback) => value.Length > 0 ? value : fallback;

        if (identifier.StartsWith("iCloudDrive", StringComparison.Ordinal))
        {
            return DetectorHelpers.Make(CloudProviderKind.ICloudDrive, "iCloud Drive", folder);
        }

        if (identifier.StartsWith("iCloudPhotos", StringComparison.Ordinal))
        {
            return DetectorHelpers.Make(CloudProviderKind.ICloudPhotos, "iCloud Photos", folder);
        }

        if (identifier.StartsWith("ownCloud", StringComparison.Ordinal))
        {
            return DetectorHelpers.Make(CloudProviderKind.OwnCloud, Or(clsidName, "ownCloud"), folder);
        }

        if (identifier.StartsWith("ProtonDrive", StringComparison.Ordinal))
        {
            return DetectorHelpers.Make(CloudProviderKind.ProtonDrive, "Proton Drive", folder);
        }

        if (identifier.StartsWith("SyncCom", StringComparison.Ordinal))
        {
            return DetectorHelpers.Make(CloudProviderKind.SyncCom, Or(clsidName, "Sync"), folder);
        }

        if (applicationName == "Nextcloud")
        {
            return DetectorHelpers.Make(CloudProviderKind.Nextcloud, Or(DetectorHelpers.Text(identifier), "Nextcloud"), folder);
        }

        if (applicationName == "kDrive" || syncRootName == "kDrive")
        {
            return DetectorHelpers.Make(CloudProviderKind.KDrive, Or(clsidName, "kDrive"), folder);
        }

        return identifier switch
        {
            "MEGA" => DetectorHelpers.Make(CloudProviderKind.Mega, $"MEGA ({Path.GetFileName(folder.TrimEnd('\\'))})", folder),
            "Jottacloud" => DetectorHelpers.Make(CloudProviderKind.Jottacloud, "Jottacloud", folder),
            "Creative Cloud Files" => DetectorHelpers.Make(CloudProviderKind.AdobeCreativeCloud, "Creative Cloud Files", folder),
            "Lucid" => DetectorHelpers.Make(CloudProviderKind.LucidLink, Or(clsidName, "LucidLink"), folder),
            "MagentaCLOUD" => DetectorHelpers.Make(CloudProviderKind.MagentaCloud, Or(clsidName, "MagentaCLOUD"), folder),
            _ => null,
        };
    }
}

/// <summary>Dropbox, from <c>info.json</c> in the website install or the newest Microsoft Store install.</summary>
public sealed class DropboxDetector(IFileSystem fileSystem, string localAppData) : ICloudDetector
{
    /// <inheritdoc />
    public async Task<IReadOnlyList<CloudProviderInfo>> DetectAsync(CancellationToken cancellationToken)
    {
        var infoPath = FindInfoJson();
        if (infoPath is null)
        {
            return [];
        }

        var text = await fileSystem.File.ReadAllTextAsync(infoPath, cancellationToken).ConfigureAwait(false);
        using var document = JsonDocument.Parse(text);
        var found = new List<CloudProviderInfo>();
        Add(document.RootElement, "personal", "Dropbox", found);
        Add(document.RootElement, "business", "Dropbox Business", found);
        return found;
    }

    private static void Add(JsonElement root, string property, string name, List<CloudProviderInfo> found)
    {
        if (root.TryGetProperty(property, out var account)
            && account.TryGetProperty("path", out var path)
            && DetectorHelpers.Make(CloudProviderKind.Dropbox, name, path.GetString()) is { } provider)
        {
            found.Add(provider);
        }
    }

    private string? FindInfoJson()
    {
        var website = fileSystem.Path.Combine(localAppData, "Dropbox", "info.json");
        if (fileSystem.File.Exists(website))
        {
            return website;
        }

        var packages = fileSystem.Path.Combine(localAppData, "Packages");
        if (!fileSystem.Directory.Exists(packages))
        {
            return null;
        }

        return fileSystem.Directory.GetDirectories(packages, "DropboxInc.Dropbox_*")
            .Select(dir => fileSystem.Path.Combine(dir, "LocalCache", "Local", "Dropbox", "info.json"))
            .Where(fileSystem.File.Exists)
            .OrderByDescending(path => fileSystem.File.GetLastWriteTimeUtc(path))
            .FirstOrDefault();
    }
}

/// <summary>Box Drive and the older Box Sync, from the sync-root file each writes.</summary>
public sealed class BoxDetector(IFileSystem fileSystem, string localAppData) : ICloudDetector
{
    /// <inheritdoc />
    public async Task<IReadOnlyList<CloudProviderInfo>> DetectAsync(CancellationToken cancellationToken)
    {
        foreach (var path in new[]
        {
            fileSystem.Path.Combine(localAppData, "Box", "Box", "data", "shell", "sync_root_folder.txt"),
            fileSystem.Path.Combine(localAppData, "Box Sync", "sync_root_folder.txt"),
        })
        {
            if (!fileSystem.File.Exists(path))
            {
                continue;
            }

            var text = await fileSystem.File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
            if (DetectorHelpers.Make(CloudProviderKind.Box, "Box", text.Trim()) is { } provider)
            {
                return [provider];
            }
        }

        return [];
    }
}

/// <summary>
/// Synology Drive sync tasks, from the client's <c>sys.sqlite</c>. The database is copied first because the running client
/// keeps it locked. Backup tasks (connection type 2) are ignored.
/// </summary>
public sealed class SynologyDriveDetector(IFileSystem fileSystem, string localAppData, string tempFolder) : ICloudDetector
{
    /// <inheritdoc />
    public Task<IReadOnlyList<CloudProviderInfo>> DetectAsync(CancellationToken cancellationToken)
    {
        var source = fileSystem.Path.Combine(localAppData, "SynologyDrive", "data", "db", "sys.sqlite");
        if (!fileSystem.File.Exists(source))
        {
            return Task.FromResult<IReadOnlyList<CloudProviderInfo>>([]);
        }

        fileSystem.Directory.CreateDirectory(tempFolder);
        var copy = fileSystem.Path.Combine(tempFolder, $"synology_drive_{Guid.NewGuid():N}.db");
        fileSystem.File.Copy(source, copy, overwrite: true);
        try
        {
            return Task.FromResult<IReadOnlyList<CloudProviderInfo>>(Read(copy));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            TryDelete(copy);
        }
    }

    private static List<CloudProviderInfo> Read(string databasePath)
    {
        var found = new List<CloudProviderInfo>();
        using var connection = new SqliteConnection($"Data Source={databasePath};Mode=ReadOnly;Pooling=False");
        connection.Open();

        var hosts = new Dictionary<string, (string Type, string Host)>();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT id, conn_type, host_name FROM connection_table";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                hosts[Convert.ToString(reader.GetValue(0), System.Globalization.CultureInfo.InvariantCulture)!] =
                    (Convert.ToString(reader.GetValue(1), System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty, reader.IsDBNull(2) ? string.Empty : reader.GetString(2));
            }
        }

        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT conn_id, sync_folder FROM session_table";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var connectionId = Convert.ToString(reader.GetValue(0), System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
                if (!hosts.TryGetValue(connectionId, out var connection1) || connection1.Type != "1" || reader.IsDBNull(1))
                {
                    continue;
                }

                var folder = reader.GetString(1);
                var name = $"Synology Drive - {connection1.Host} ({Path.GetFileName(folder.TrimEnd('\\'))})";
                if (DetectorHelpers.Make(CloudProviderKind.SynologyDrive, name, folder) is { } provider)
                {
                    found.Add(provider);
                }
            }
        }

        return found;
    }

    private void TryDelete(string path)
    {
        try
        {
            fileSystem.File.Delete(path);
        }
        catch (IOException)
        {
            // A leftover temp copy is cleaned up with the temp folder.
        }
    }
}

/// <summary>
/// Google Drive for desktop: folders synced in mirror mode and File Stream mount points from <c>root_preference_sqlite.db</c>,
/// plus the base mount path from the registry. Only runs when the app is installed, because its config outlives an uninstall.
/// </summary>
public sealed class GoogleDriveDetector(IRegistryView registry, IFileSystem fileSystem, string localAppData, string tempFolder, IReadOnlyList<string> programFolders) : ICloudDetector
{
    /// <inheritdoc />
    public Task<IReadOnlyList<CloudProviderInfo>> DetectAsync(CancellationToken cancellationToken)
    {
        if (!IsInstalled())
        {
            return Task.FromResult<IReadOnlyList<CloudProviderInfo>>([]);
        }

        var found = new List<CloudProviderInfo>();
        var database = fileSystem.Path.Combine(localAppData, "Google", "DriveFS", "root_preference_sqlite.db");
        if (fileSystem.File.Exists(database))
        {
            fileSystem.Directory.CreateDirectory(tempFolder);
            var copy = fileSystem.Path.Combine(tempFolder, $"google_drive_{Guid.NewGuid():N}.db");
            fileSystem.File.Copy(database, copy, overwrite: true);
            var wal = database + "-wal";
            if (fileSystem.File.Exists(wal))
            {
                fileSystem.File.Copy(wal, copy + "-wal", overwrite: true);
            }

            try
            {
                ReadDatabase(copy, found);
            }
            finally
            {
                SqliteConnection.ClearAllPools();
                foreach (var file in new[] { copy, copy + "-wal" })
                {
                    if (fileSystem.File.Exists(file))
                    {
                        fileSystem.File.Delete(file);
                    }
                }
            }
        }

        if (RegistryBasePath() is { } basePath && DetectorHelpers.Make(CloudProviderKind.GoogleDrive, "Google Drive", fileSystem.Path.Combine(basePath, "My Drive")) is { } fallback)
        {
            found.Add(fallback);
        }

        return Task.FromResult<IReadOnlyList<CloudProviderInfo>>(found);
    }

    private static void ReadDatabase(string path, List<CloudProviderInfo> found)
    {
        using var connection = new SqliteConnection($"Data Source={path};Pooling=False");
        connection.Open();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT title, last_seen_absolute_path FROM roots";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var folder = reader.IsDBNull(1) ? null : reader.GetString(1);
                var title = reader.IsDBNull(0) ? Path.GetFileName(folder ?? string.Empty) : reader.GetString(0);
                if (DetectorHelpers.Make(CloudProviderKind.GoogleDrive, $"Google Drive ({title})", folder) is { } provider)
                {
                    found.Add(provider);
                }
            }
        }

        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT name, last_mount_point FROM media WHERE fs_type = 10";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                if (reader.IsDBNull(1))
                {
                    continue;
                }

                var mount = reader.GetString(1);
                var name = reader.IsDBNull(0) ? "Google Drive" : reader.GetString(0);
                if (DetectorHelpers.Make(CloudProviderKind.GoogleDrive, name, Path.Combine(mount, "My Drive")) is { } provider)
                {
                    found.Add(provider);
                }
            }
        }
    }

    private bool IsInstalled() =>
        programFolders.Any(root =>
        {
            var install = fileSystem.Path.Combine(root, "Google", "Drive File Stream");
            return fileSystem.Directory.Exists(install)
                && fileSystem.Directory.EnumerateFiles(install, "GoogleDriveFS.exe", SearchOption.AllDirectories).Any();
        });

    private string? RegistryBasePath()
    {
        var value = registry.GetValue(RegistryRoot.CurrentUser, @"Software\Google\DriveFS", "PerAccountPreferences");
        if (value is not string json)
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            foreach (var account in document.RootElement.EnumerateObject())
            {
                foreach (var item in account.Value.EnumerateArray())
                {
                    if (item.TryGetProperty("value", out var preferences)
                        && preferences.TryGetProperty("mount_point_path", out var mount)
                        && mount.GetString() is { Length: > 0 } path)
                    {
                        return path.Length == 1 ? $@"{path}:\" : path;
                    }
                }
            }
        }
        catch (JsonException)
        {
            // An unreadable preference blob means no fallback location.
        }

        return null;
    }
}

/// <summary>
/// The catalog of every detector. Detectors run in parallel; one that throws or takes longer than <see cref="Timeout"/> is
/// skipped, so a broken client never hides the others (spec §3.2: nothing here may stall the UI).
/// </summary>
public sealed class CloudProviderCatalog : ICloudProviderCatalog
{
    /// <summary>How long one detector may take.</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(10);

    private readonly IReadOnlyList<ICloudDetector> _detectors;
    private readonly ILogger? _logger;

    /// <summary>Creates a catalog over <paramref name="detectors"/>.</summary>
    public CloudProviderCatalog(IEnumerable<ICloudDetector> detectors, ILogger? logger = null, TimeSpan? timeout = null)
    {
        ArgumentNullException.ThrowIfNull(detectors);
        _detectors = [.. detectors];
        _logger = logger;
        Timeout = timeout ?? DefaultTimeout;
    }

    /// <summary>The per-detector timeout.</summary>
    public TimeSpan Timeout { get; }

    /// <summary>Builds the catalog for this PC: every detector, reading the real registry and file system.</summary>
    [SupportedOSPlatform("windows")]
    public static CloudProviderCatalog ForThisMachine(ILogger? logger = null)
    {
        var registry = new WindowsRegistryView();
        var fileSystem = new FileSystem();
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var temp = fileSystem.Path.Combine(fileSystem.Path.GetTempPath(), "Leviathan", "cloud");
        string[] programs = [Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86)];
        return new CloudProviderCatalog(
            [
                new OneDriveDetector(registry),
                new SharePointDetector(registry),
                new NavigationPaneDetector(registry),
                new DropboxDetector(fileSystem, localAppData),
                new BoxDetector(fileSystem, localAppData),
                new SynologyDriveDetector(fileSystem, localAppData, temp),
                new GoogleDriveDetector(registry, fileSystem, localAppData, temp, programs),
            ],
            logger);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<CloudProviderInfo>> DetectAsync(CancellationToken cancellationToken)
    {
        var tasks = _detectors.Select(d => RunAsync(d, cancellationToken)).ToList();
        var results = await Task.WhenAll(tasks).ConfigureAwait(false);
        return [.. results
            .SelectMany(r => r)
            .DistinctBy(p => (p.Kind, p.SyncFolder))
            .OrderBy(p => p.Kind)
            .ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase)];
    }

    private async Task<IReadOnlyList<CloudProviderInfo>> RunAsync(ICloudDetector detector, CancellationToken cancellationToken)
    {
        try
        {
            return await Task.Run(() => detector.DetectAsync(cancellationToken), cancellationToken).WaitAsync(Timeout, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Cloud detector {Detector} failed and was skipped.", detector.GetType().Name);
            return [];
        }
    }
}
