using System.IO.Abstractions;
using System.IO.Abstractions.TestingHelpers;
using Leviathan.Core.Platform;
using Leviathan.Testing;
using Microsoft.Data.Sqlite;

namespace Leviathan.Providers.Cloud.Tests;

public sealed class CloudDetectorTests
{
    private const string Accounts = @"SOFTWARE\Microsoft\OneDrive\Accounts";
    private const string LocalAppData = @"C:\Users\b\AppData\Local";

    [Fact]
    public async Task OneDrive_ListsEachAccount_WithItsDisplayName()
    {
        var registry = new FakeRegistry()
            .Set(RegistryRoot.CurrentUser, Accounts + @"\Personal", "UserFolder", @"C:\Users\b\OneDrive")
            .Set(RegistryRoot.CurrentUser, Accounts + @"\Business1", "UserFolder", @"C:\Users\b\OneDrive - Contoso")
            .Set(RegistryRoot.CurrentUser, Accounts + @"\Business1", "DisplayName", "Contoso");

        var found = await new OneDriveDetector(registry).DetectAsync(CancellationToken.None);

        Assert.Equal(["OneDrive", "OneDrive - Contoso"], found.Select(f => f.Name).Order());
        Assert.All(found, f => Assert.Equal(CloudProviderKind.OneDrive, f.Kind));
    }

    [Fact]
    public async Task OneDrive_AccountWithoutFolder_OrNoOneDrive_FindsNothing()
    {
        var registry = new FakeRegistry().Set(RegistryRoot.CurrentUser, Accounts + @"\Personal", "DisplayName", "x");

        Assert.Empty(await new OneDriveDetector(registry).DetectAsync(CancellationToken.None));
        Assert.Empty(await new OneDriveDetector(new FakeRegistry()).DetectAsync(CancellationToken.None));
    }

    [Fact]
    public async Task SharePoint_UsesTheParentOfEachMountPoint_ExcludingTheUserFolder()
    {
        var cache = Accounts + @"\Business1\ScopeIdToMountPointPathCache";
        var registry = new FakeRegistry()
            .Set(RegistryRoot.CurrentUser, Accounts + @"\Business1", "UserFolder", @"C:\Users\b\OneDrive - Contoso")
            .Set(RegistryRoot.CurrentUser, cache, "scope1", @"C:\Users\b\Contoso\Team Site - Docs")
            .Set(RegistryRoot.CurrentUser, cache, "scope2", @"C:\Users\b\Contoso\Team Site - Reports")
            .Set(RegistryRoot.CurrentUser, cache, "scope3", @"C:\Users\b\OneDrive - Contoso");

        var found = await new SharePointDetector(registry).DetectAsync(CancellationToken.None);

        var site = Assert.Single(found);
        Assert.Equal("SharePoint - Contoso", site.Name);
        Assert.Equal(@"C:\Users\b\Contoso", site.SyncFolder.Value);
        Assert.Equal(CloudProviderKind.OneDriveCommercial, site.Kind);
    }

    private static FakeRegistry Pinned(FakeRegistry registry, string id, string identifier, string folder, string? clsidName = null, string? applicationName = null, bool pinned = true)
    {
        const string Namespace = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\Desktop\NameSpace";
        registry.Set(RegistryRoot.CurrentUser, $@"{Namespace}\{id}", null, identifier);
        if (applicationName is not null)
        {
            registry.Set(RegistryRoot.CurrentUser, $@"{Namespace}\{id}", "ApplicationName", applicationName);
        }

        registry.Set(RegistryRoot.ClassesRoot, $@"CLSID\{id}", "System.IsPinnedToNameSpaceTree", pinned ? 1 : 0);
        registry.Set(RegistryRoot.ClassesRoot, $@"CLSID\{id}\Instance\InitPropertyBag", "TargetFolderPath", folder);
        if (clsidName is not null)
        {
            registry.Set(RegistryRoot.ClassesRoot, $@"CLSID\{id}", null, clsidName);
        }

        return registry;
    }

    [Fact]
    public async Task NavigationPane_RecognizesPinnedClients_ByIdentifier()
    {
        var registry = new FakeRegistry();
        Pinned(registry, "{1}", "iCloudDrive_x", @"C:\Users\b\iCloudDrive");
        Pinned(registry, "{2}", "ProtonDrive_y", @"C:\Users\b\Proton Drive");
        Pinned(registry, "{3}", "MEGA", @"C:\Users\b\MEGA");
        Pinned(registry, "{4}", "NextcloudAccount", @"C:\Users\b\Nextcloud", applicationName: "Nextcloud");
        Pinned(registry, "{5}", "Mystery", @"C:\Users\b\Mystery");

        var found = await new NavigationPaneDetector(registry).DetectAsync(CancellationToken.None);

        Assert.Equal(
            [CloudProviderKind.ICloudDrive, CloudProviderKind.ProtonDrive, CloudProviderKind.Mega, CloudProviderKind.Nextcloud],
            found.Select(f => f.Kind).Order());
        Assert.Contains(found, f => f.Name == "MEGA (MEGA)");
        Assert.Contains(found, f => f.Name == "Proton Drive");
    }

    [Fact]
    public async Task NavigationPane_IgnoresUnpinnedEntries_AndEntriesWithoutAFolder()
    {
        var registry = new FakeRegistry();
        Pinned(registry, "{1}", "iCloudDrive_x", @"C:\Users\b\iCloudDrive", pinned: false);
        Pinned(registry, "{2}", "ProtonDrive_y", "");

        Assert.Empty(await new NavigationPaneDetector(registry).DetectAsync(CancellationToken.None));
    }

    [Fact]
    public async Task NavigationPane_KDriveIsFoundThroughTheSyncRootRegistration()
    {
        var registry = new FakeRegistry();
        Pinned(registry, "{1}", "kDrive123", @"C:\Users\b\kDrive", clsidName: "kDrive - Work");
        registry.Set(RegistryRoot.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\SyncRootManager\kDrive123", null, "kDrive");

        var found = Assert.Single(await new NavigationPaneDetector(registry).DetectAsync(CancellationToken.None));

        Assert.Equal(CloudProviderKind.KDrive, found.Kind);
        Assert.Equal("kDrive - Work", found.Name);
    }

    private static MockFileSystem Files(params (string Path, string Content)[] files)
    {
        var fs = new MockFileSystem();
        foreach (var (path, content) in files)
        {
            fs.AddFile(path, new MockFileData(content));
        }

        return fs;
    }

    [Fact]
    public async Task Dropbox_ReadsPersonalAndBusinessFolders_FromTheWebsiteInstall()
    {
        var fs = Files((LocalAppData + @"\Dropbox\info.json", """{"personal":{"path":"C:\\Users\\b\\Dropbox"},"business":{"path":"C:\\Users\\b\\Dropbox (Work)"}}"""));

        var found = await new DropboxDetector(fs, LocalAppData).DetectAsync(CancellationToken.None);

        Assert.Equal(["Dropbox", "Dropbox Business"], found.Select(f => f.Name));
        Assert.Equal(@"C:\Users\b\Dropbox (Work)", found[1].SyncFolder.Value);
    }

    [Fact]
    public async Task Dropbox_FallsBackToTheNewestStoreInstall()
    {
        var oldPath = LocalAppData + @"\Packages\DropboxInc.Dropbox_old\LocalCache\Local\Dropbox\info.json";
        var newPath = LocalAppData + @"\Packages\DropboxInc.Dropbox_new\LocalCache\Local\Dropbox\info.json";
        var fs = new MockFileSystem();
        fs.AddFile(oldPath, new MockFileData("""{"personal":{"path":"C:\\old"}}""") { LastWriteTime = new DateTime(2025, 1, 1) });
        fs.AddFile(newPath, new MockFileData("""{"personal":{"path":"C:\\new"}}""") { LastWriteTime = new DateTime(2026, 1, 1) });

        var found = await new DropboxDetector(fs, LocalAppData).DetectAsync(CancellationToken.None);

        Assert.Equal(@"C:\new", Assert.Single(found).SyncFolder.Value);
    }

    [Fact]
    public async Task Dropbox_NotInstalled_FindsNothing()
    {
        Assert.Empty(await new DropboxDetector(new MockFileSystem(), LocalAppData).DetectAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Box_ReadsTheSyncRoot_PreferringBoxDriveOverBoxSync()
    {
        var fs = Files(
            (LocalAppData + @"\Box\Box\data\shell\sync_root_folder.txt", "C:\\Users\\b\\Box\r\n"),
            (LocalAppData + @"\Box Sync\sync_root_folder.txt", @"C:\Users\b\Box Sync"));

        var found = Assert.Single(await new BoxDetector(fs, LocalAppData).DetectAsync(CancellationToken.None));

        Assert.Equal(@"C:\Users\b\Box", found.SyncFolder.Value);
    }

    [Fact]
    public async Task Box_UsesBoxSync_WhenBoxDriveIsAbsent_AndNothingWhenEmpty()
    {
        var fs = Files((LocalAppData + @"\Box Sync\sync_root_folder.txt", @"C:\Users\b\Box Sync"));
        Assert.Equal(@"C:\Users\b\Box Sync", Assert.Single(await new BoxDetector(fs, LocalAppData).DetectAsync(CancellationToken.None)).SyncFolder.Value);

        var empty = Files((LocalAppData + @"\Box Sync\sync_root_folder.txt", "  "));
        Assert.Empty(await new BoxDetector(empty, LocalAppData).DetectAsync(CancellationToken.None));
    }

    private static string NewTempFolder()
    {
        var folder = Path.Combine(Path.GetTempPath(), "leviathan-cloud-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        return folder;
    }

    private static void Execute(string database, params string[] statements)
    {
        using var connection = new SqliteConnection($"Data Source={database};Pooling=False");
        connection.Open();
        foreach (var sql in statements)
        {
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            command.ExecuteNonQuery();
        }
    }

    [Fact]
    public async Task SynologyDrive_ListsSyncTasks_NotBackupTasks_AndCleansUpItsCopy()
    {
        var root = NewTempFolder();
        try
        {
            var db = Path.Combine(root, "SynologyDrive", "data", "db", "sys.sqlite");
            Directory.CreateDirectory(Path.GetDirectoryName(db)!);
            Execute(
                db,
                "CREATE TABLE connection_table (id INTEGER, conn_type INTEGER, host_name TEXT)",
                "CREATE TABLE session_table (conn_id INTEGER, sync_folder TEXT, remote_path TEXT)",
                "INSERT INTO connection_table VALUES (1, 1, 'Diskstation')",
                "INSERT INTO connection_table VALUES (2, 2, 'Diskstation')",
                @"INSERT INTO session_table VALUES (1, 'D:\Sync\Projects', '/projects')",
                @"INSERT INTO session_table VALUES (2, 'D:\Backup', '/backup')",
                "INSERT INTO session_table VALUES (1, NULL, '/none')");
            var temp = Path.Combine(root, "temp");

            var found = await new SynologyDriveDetector(new FileSystem(), root, temp).DetectAsync(CancellationToken.None);

            var task = Assert.Single(found);
            Assert.Equal("Synology Drive - Diskstation (Projects)", task.Name);
            Assert.Equal(@"D:\Sync\Projects", task.SyncFolder.Value);
            Assert.Empty(Directory.GetFiles(temp));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task SynologyDrive_NotInstalled_FindsNothing()
    {
        Assert.Empty(await new SynologyDriveDetector(new MockFileSystem(), LocalAppData, @"C:\temp").DetectAsync(CancellationToken.None));
    }

    [Fact]
    public async Task GoogleDrive_Installed_ReadsMirroredFoldersStreamMountsAndTheRegistryBase()
    {
        var root = NewTempFolder();
        try
        {
            var programs = Path.Combine(root, "Program Files");
            var exe = Path.Combine(programs, "Google", "Drive File Stream", "99.0", "GoogleDriveFS.exe");
            Directory.CreateDirectory(Path.GetDirectoryName(exe)!);
            File.WriteAllText(exe, "x");
            var db = Path.Combine(root, "Google", "DriveFS", "root_preference_sqlite.db");
            Directory.CreateDirectory(Path.GetDirectoryName(db)!);
            Execute(
                db,
                "CREATE TABLE roots (title TEXT, last_seen_absolute_path TEXT)",
                "CREATE TABLE media (name TEXT, last_mount_point TEXT, fs_type INTEGER)",
                @"INSERT INTO roots VALUES ('Photos', '\\?\D:\Sync\Photos')",
                @"INSERT INTO media VALUES ('Google Drive (G:)', 'G:\', 10)",
                @"INSERT INTO media VALUES ('Other', 'H:\', 3)");
            var registry = new FakeRegistry().Set(
                RegistryRoot.CurrentUser,
                @"Software\Google\DriveFS",
                "PerAccountPreferences",
                """{"accounts":[{"value":{"mount_point_path":"G"}}]}""");

            var found = await new GoogleDriveDetector(registry, new FileSystem(), root, Path.Combine(root, "temp"), [programs]).DetectAsync(CancellationToken.None);

            Assert.Contains(found, f => f.Name == "Google Drive (Photos)" && f.SyncFolder.Value == @"D:\Sync\Photos");
            Assert.Contains(found, f => f.Name == "Google Drive (G:)" && f.SyncFolder.Value == @"G:\My Drive");
            Assert.DoesNotContain(found, f => f.SyncFolder.Value.StartsWith("H:", StringComparison.Ordinal));
            Assert.Equal(3, found.Count);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task GoogleDrive_ConfigLeftBehindAfterUninstall_IsIgnored()
    {
        var registry = new FakeRegistry().Set(RegistryRoot.CurrentUser, @"Software\Google\DriveFS", "PerAccountPreferences", """{"a":[{"value":{"mount_point_path":"G"}}]}""");

        var found = await new GoogleDriveDetector(registry, new MockFileSystem(), LocalAppData, @"C:\temp", [@"C:\Program Files"]).DetectAsync(CancellationToken.None);

        Assert.Empty(found);
    }

    private sealed class StubDetector(Func<CancellationToken, Task<IReadOnlyList<CloudProviderInfo>>> run) : ICloudDetector
    {
        public Task<IReadOnlyList<CloudProviderInfo>> DetectAsync(CancellationToken cancellationToken) => run(cancellationToken);
    }

    private static CloudProviderInfo Info(CloudProviderKind kind, string name, string folder) =>
        new(kind, name, Leviathan.Core.Paths.LocationPath.Parse(folder));

    [Fact]
    public async Task Catalog_MergesDetectors_DropsDuplicates_AndOrdersByKindThenName()
    {
        var catalog = new CloudProviderCatalog(
        [
            new StubDetector(_ => Task.FromResult<IReadOnlyList<CloudProviderInfo>>([Info(CloudProviderKind.Dropbox, "Dropbox", @"C:\Dropbox"), Info(CloudProviderKind.OneDrive, "OneDrive - B", @"C:\B")])),
            new StubDetector(_ => Task.FromResult<IReadOnlyList<CloudProviderInfo>>([Info(CloudProviderKind.OneDrive, "OneDrive - A", @"C:\A"), Info(CloudProviderKind.OneDrive, "Again", @"c:\b")])),
        ]);

        var found = await catalog.DetectAsync(CancellationToken.None);

        Assert.Equal(["OneDrive - A", "OneDrive - B", "Dropbox"], found.Select(f => f.Name));
    }

    [Fact]
    public async Task Catalog_AFailingDetector_DoesNotHideTheOthers()
    {
        var catalog = new CloudProviderCatalog(
        [
            new StubDetector(_ => throw new InvalidOperationException("broken client config")),
            new StubDetector(_ => Task.FromResult<IReadOnlyList<CloudProviderInfo>>([Info(CloudProviderKind.Box, "Box", @"C:\Box")])),
        ]);

        var found = await catalog.DetectAsync(CancellationToken.None);

        Assert.Equal(["Box"], found.Select(f => f.Name));
    }

    [Fact]
    public async Task Catalog_ASlowDetector_IsSkippedAfterTheTimeout()
    {
        var catalog = new CloudProviderCatalog(
            [
                new StubDetector(async ct => { await Task.Delay(TimeSpan.FromSeconds(30), ct); return [Info(CloudProviderKind.Box, "Box", @"C:\Box")]; }),
                new StubDetector(_ => Task.FromResult<IReadOnlyList<CloudProviderInfo>>([Info(CloudProviderKind.Dropbox, "Dropbox", @"C:\Dropbox")])),
            ],
            timeout: TimeSpan.FromMilliseconds(100));

        var found = await catalog.DetectAsync(CancellationToken.None);

        Assert.Equal(["Dropbox"], found.Select(f => f.Name));
    }

    [Fact]
    public async Task Catalog_Cancelled_Throws()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var catalog = new CloudProviderCatalog([new StubDetector(_ => Task.FromResult<IReadOnlyList<CloudProviderInfo>>([]))]);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => catalog.DetectAsync(cts.Token));
    }

    [Fact]
    public async Task ThisMachine_Detection_CompletesWithoutThrowing()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Reads the Windows registry.");
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var found = await CloudProviderCatalog.ForThisMachine().DetectAsync(CancellationToken.None);

        Assert.NotNull(found);
        Assert.All(found, f => Assert.False(string.IsNullOrWhiteSpace(f.Name)));
    }
}
