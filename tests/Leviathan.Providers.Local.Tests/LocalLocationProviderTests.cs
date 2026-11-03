using Leviathan.Core.Model;
using Leviathan.Core.Paths;
using Leviathan.Core.Providers;
using Leviathan.Providers.Local.Win32;

namespace Leviathan.Providers.Local.Tests;

/// <summary>
/// Runs against a real temporary folder. Locations are written as <c>C:\root\...</c> and mapped onto the temp folder,
/// so the same tests run on Windows and on Linux CI.
/// </summary>
public sealed class LocalLocationProviderTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "leviathan-local-" + Guid.NewGuid().ToString("N"));
    private readonly LocationPath _rootLocation = LocationPath.Parse(@"C:\root");

    public LocalLocationProviderTests()
    {
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // Best effort.
        }
    }

    private string ToOsPath(LocationPath location) =>
        Path.Combine([_root, .. location.Segments.Skip(_rootLocation.Segments.Count)]);

    private LocalLocationProvider CreateProvider(IDirectoryLister? network = null) =>
        new(new ManagedDirectoryLister(), network, ToOsPath, TimeProvider.System);

    private static async Task<List<FileEntry>> ListAllAsync(ILocationProvider provider, LocationPath location, ListingOptions? options = null, CancellationToken cancellationToken = default)
    {
        var items = new List<FileEntry>();
        await foreach (var batch in provider.ListAsync(location, options ?? ListingOptions.Default, cancellationToken))
        {
            items.AddRange(batch.Items);
        }

        return items;
    }

    [Fact]
    public async Task List_ReturnsFilesAndFolders_WithSizes()
    {
        Directory.CreateDirectory(Path.Combine(_root, "sub"));
        await File.WriteAllBytesAsync(Path.Combine(_root, "a.bin"), new byte[1234]);
        await File.WriteAllTextAsync(Path.Combine(_root, "b.txt"), "hello");

        var items = await ListAllAsync(CreateProvider(), _rootLocation);

        Assert.Equal(3, items.Count);
        var a = Assert.Single(items, i => i.Name == "a.bin");
        Assert.Equal(FileEntryKind.File, a.Kind);
        Assert.Equal(1234, a.Size);
        Assert.Equal(@"C:\root\a.bin", a.Location.Value);
        Assert.Equal(".bin", a.Extension);
        Assert.Equal(FileEntryKind.Folder, Assert.Single(items, i => i.Name == "sub").Kind);
    }

    [Fact]
    public async Task List_LargeFolder_StreamsInBatches()
    {
        for (var i = 0; i < 1500; i++)
        {
            await File.WriteAllBytesAsync(Path.Combine(_root, $"f{i:D4}.bin"), []);
        }

        var batches = new List<ListingBatch>();
        await foreach (var batch in CreateProvider().ListAsync(_rootLocation, ListingOptions.Default with { BatchSize = 256 }, CancellationToken.None))
        {
            batches.Add(batch);
        }

        Assert.Equal(1500, batches.Sum(b => b.Items.Count));
        Assert.True(batches.Count >= 6);
        Assert.True(batches[^1].IsFinal);
    }

    [Fact]
    public async Task List_MissingFolder_ThrowsNotFound()
    {
        var ex = await Assert.ThrowsAsync<LocationException>(() => ListAllAsync(CreateProvider(), _rootLocation.Combine("missing")));

        Assert.Equal(LocationError.NotFound, ex.Error);
    }

    [Fact]
    public async Task List_SymbolicLink_IsReportedAsLink()
    {
        var target = Path.Combine(_root, "target");
        Directory.CreateDirectory(target);
        try
        {
            Directory.CreateSymbolicLink(Path.Combine(_root, "link"), target);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            Assert.Skip("Creating symbolic links needs Developer Mode or elevation on this machine.");
        }

        var items = await ListAllAsync(CreateProvider(), _rootLocation);

        Assert.Equal(FileEntryKind.Link, Assert.Single(items, i => i.Name == "link").Kind);
    }

    [Fact]
    public async Task List_HungNetworkShare_FailsFastAsUnreachable()
    {
        var provider = new LocalLocationProvider(new ManagedDirectoryLister(), new HangingLister(), _ => "unused", TimeProvider.System)
        {
            NetworkFirstBatchTimeout = TimeSpan.FromMilliseconds(200),
        };

        var started = DateTime.UtcNow;
        var ex = await Assert.ThrowsAsync<LocationException>(() => ListAllAsync(provider, LocationPath.Parse(@"\\nas\media")));

        Assert.Equal(LocationError.Unreachable, ex.Error);
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(3), "Did not fail fast.");
    }

    [Fact]
    public async Task List_AMappedNetworkDrive_IsListedLikeAShare_AndFailsFastWhenItHangs()
    {
        var provider = new LocalLocationProvider(new ManagedDirectoryLister(), new HangingLister(), _ => "unused", TimeProvider.System)
        {
            NetworkFirstBatchTimeout = TimeSpan.FromMilliseconds(200),
            IsMappedDrive = location => location.Root.StartsWith("Y", StringComparison.OrdinalIgnoreCase),
        };

        var ex = await Assert.ThrowsAsync<LocationException>(() => ListAllAsync(provider, LocationPath.Parse(@"Y:\Misc\Renamed")));

        Assert.Equal(LocationError.Unreachable, ex.Error);
    }

    [Fact]
    public async Task List_AnOrdinaryDrive_StaysOnTheLocalListerEvenWithAHangingNetworkOne()
    {
        var provider = new LocalLocationProvider(new ManagedDirectoryLister(), new HangingLister(), _ => _root, TimeProvider.System)
        {
            NetworkFirstBatchTimeout = TimeSpan.FromMilliseconds(200),
            IsMappedDrive = _ => false,
        };

        var items = await ListAllAsync(provider, _rootLocation);

        Assert.NotNull(items);
    }

    [Fact]
    public void MappedDrives_AreNeverGuessedFromNonDrivePaths()
    {
        Assert.False(MappedDrives.IsMappedDrive(LocationPath.Parse(@"\\nas\media")));
        Assert.False(MappedDrives.IsMappedDrive(LocationPath.Parse(@"C:\")));
        Assert.False(MappedDrives.IsMappedDrive(LocationPath.Parse(@"Q:\does\not\exist")));
    }

    [Fact]
    public void MappedDrives_RecogniseARealMappedDriveOnThisMachine()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Drive types are a Windows notion.");
        var mapped = DriveInfo.GetDrives().FirstOrDefault(d => d.DriveType == DriveType.Network);
        Assert.SkipWhen(mapped is null, "No network drive is mapped on this machine.");
        MappedDrives.Forget();

        Assert.True(MappedDrives.IsMappedDrive(LocationPath.Parse(mapped!.Name + @"some\folder")));
        Assert.False(MappedDrives.IsMappedDrive(LocationPath.Parse(@"C:\Windows")));
    }

    [Fact]
    public async Task List_CallerCancellation_IsNotReportedAsUnreachable()
    {
        var provider = new LocalLocationProvider(new ManagedDirectoryLister(), new HangingLister(), _ => "unused", TimeProvider.System);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ListAllAsync(provider, LocationPath.Parse(@"\\nas\media"), cancellationToken: cts.Token));
    }

    [Fact]
    public void CanHandle_DrivesAndShares_NotServersOrUris()
    {
        var provider = CreateProvider();

        Assert.True(provider.CanHandle(LocationPath.Parse(@"C:\")));
        Assert.True(provider.CanHandle(LocationPath.Parse(@"\\nas\media")));
        Assert.False(provider.CanHandle(LocationPath.Parse(@"\\nas")));
        Assert.False(provider.CanHandle(LocationPath.Parse("sftp://nas/x")));
    }

    [Fact]
    public async Task Win32Lister_ListsSystemFolder_OnWindows()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Win32 lister runs on Windows only.");
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var location = LocationPath.Parse(windows);
        var win32 = new Win32DirectoryLister().List(location, windows, CancellationToken.None).Select(e => e.Name).Order(StringComparer.OrdinalIgnoreCase).ToList();
        var managed = new ManagedDirectoryLister().List(location, windows, CancellationToken.None).Select(e => e.Name).Order(StringComparer.OrdinalIgnoreCase).ToList();

        Assert.NotEmpty(win32);
        Assert.Equal(managed, win32);
        await Task.CompletedTask;
    }

    [Fact]
    public void Win32FindData_Layout_MatchesWin32FindDataW()
    {
        // WIN32_FIND_DATAW is 592 bytes and FILETIMEs are 4-byte aligned. Default 8-byte alignment of the long fields
        // shifted every field after the attributes and produced "Not a valid Win32 FileTime" on real listings.
        Assert.Equal(592, System.Runtime.InteropServices.Marshal.SizeOf<Win32FindData>());
        Assert.Equal(4, (int)System.Runtime.InteropServices.Marshal.OffsetOf<Win32FindData>(nameof(Win32FindData.CreationTime)));
        Assert.Equal(20, (int)System.Runtime.InteropServices.Marshal.OffsetOf<Win32FindData>(nameof(Win32FindData.LastWriteTime)));
        Assert.Equal(44, (int)System.Runtime.InteropServices.Marshal.OffsetOf<Win32FindData>(nameof(Win32FindData.FileName)));
    }

    /// <summary>Blocks like a directory call on a dead SMB server.</summary>
    private sealed class HangingLister : IDirectoryLister
    {
        public IEnumerable<FileEntry> List(LocationPath location, string osPath, CancellationToken cancellationToken)
        {
            cancellationToken.WaitHandle.WaitOne(TimeSpan.FromSeconds(30));
            cancellationToken.ThrowIfCancellationRequested();
            yield break;
        }
    }
}
