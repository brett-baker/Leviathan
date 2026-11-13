using System.IO.Abstractions.TestingHelpers;
using Leviathan.Core.Archives;
using Leviathan.Core.Paths;
using Leviathan.Core.Providers;
using Leviathan.Testing;

namespace Leviathan.Providers.Archive.Tests;

/// <summary>Listing inside archives, the cache behind it, and unpacking one entry (spec §15.4).</summary>
public sealed class ArchiveProviderTests
{
    private const string Zip = @"D:\Stuff\photos.zip";

    private static readonly Dictionary<string, string> Files = new()
    {
        ["readme.txt"] = "hello",
        ["2024/jan/a.jpg"] = "aaaa",
        ["2024/b.jpg"] = "bb",
        ["empty/"] = "",
    };

    private sealed class Rig
    {
        public Rig(bool zipIsFile = true)
        {
            Reader = new FakeArchiveReader().Add(Zip, Files);
            Disk = new MockFileSystem();
            if (zipIsFile)
            {
                Disk.AddFile(Zip, new MockFileData("zip bytes") { LastWriteTime = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc) });
            }

            Index = new ArchiveIndex(Reader, Disk);
            Inner = new FakeLocationProvider().AddFolder(@"D:\Stuff\photos.zip\real");
            Provider = new ArchiveAwareLocationProvider(Inner, Index);
        }

        public FakeArchiveReader Reader { get; }

        public MockFileSystem Disk { get; }

        public ArchiveIndex Index { get; }

        public FakeLocationProvider Inner { get; }

        public ArchiveAwareLocationProvider Provider { get; }

        public async Task<string[]> NamesAsync(string location, ListingOptions? options = null)
        {
            var names = new List<string>();
            await foreach (var batch in Provider.ListAsync(LocationPath.Parse(location), options ?? ListingOptions.Default, CancellationToken.None))
            {
                names.AddRange(batch.Items.Select(i => i.Name));
            }

            names.Sort(StringComparer.Ordinal);
            return [.. names];
        }
    }

    [Fact]
    public async Task List_TheArchiveItself_ShowsItsTopLevel()
    {
        var rig = new Rig();

        Assert.Equal(["2024", "empty", "readme.txt"], await rig.NamesAsync(Zip));
    }

    [Fact]
    public async Task List_AFolderInside_ShowsOnlyThatFolder()
    {
        var rig = new Rig();

        Assert.Equal(["b.jpg", "jan"], await rig.NamesAsync(Zip + @"\2024"));
    }

    [Fact]
    public async Task List_AnEmptyFolder_IsEmptyButFinal()
    {
        var rig = new Rig();
        var batches = new List<ListingBatch>();

        await foreach (var batch in rig.Provider.ListAsync(LocationPath.Parse(Zip + @"\empty"), ListingOptions.Default, CancellationToken.None))
        {
            batches.Add(batch);
        }

        var only = Assert.Single(batches);
        Assert.True(only.IsFinal);
        Assert.Empty(only.Items);
    }

    [Fact]
    public async Task List_AFolderNotInTheArchive_IsNotFound()
    {
        var rig = new Rig();

        var ex = await Assert.ThrowsAsync<LocationException>(() => rig.NamesAsync(Zip + @"\nope"));

        Assert.Equal(LocationError.NotFound, ex.Error);
    }

    [Fact]
    public async Task List_SplitsIntoBatches_TheLastOneFinal()
    {
        var rig = new Rig();
        var options = ListingOptions.Default with { BatchSize = 2 };
        var batches = new List<ListingBatch>();

        await foreach (var batch in rig.Provider.ListAsync(LocationPath.Parse(Zip), options, CancellationToken.None))
        {
            batches.Add(batch);
        }

        Assert.Equal([2, 1], batches.Select(b => b.Items.Count));
        Assert.Equal([false, true], batches.Select(b => b.IsFinal));
    }

    [Fact]
    public async Task List_AFolderNamedLikeAnArchive_IsStillAFolder()
    {
        var rig = new Rig(zipIsFile: false);
        rig.Disk.AddDirectory(Zip);

        Assert.Equal(["real"], await rig.NamesAsync(Zip));
        Assert.Equal(0, rig.Reader.Reads);
    }

    [Fact]
    public async Task List_AnOrdinaryFolder_GoesStraightToTheWrappedProvider()
    {
        var rig = new Rig();
        rig.Inner.AddFile(@"D:\Stuff\plain\x.txt", 1);

        Assert.Equal(["x.txt"], await rig.NamesAsync(@"D:\Stuff\plain"));
        Assert.Equal(0, rig.Reader.Reads);
    }

    [Fact]
    public async Task List_AnUnreadableArchive_IsAClearLocationError()
    {
        var rig = new Rig();
        rig.Reader.Failure = new ArchiveException(ArchiveError.Unreadable, "photos.zip could not be read as an archive.");

        var ex = await Assert.ThrowsAsync<LocationException>(() => rig.NamesAsync(Zip));

        Assert.Contains("could not be read", ex.Message);
    }

    [Fact]
    public async Task List_APasswordProtectedArchive_AsksForCredentials()
    {
        var rig = new Rig();
        rig.Reader.Failure = new ArchiveException(ArchiveError.PasswordRequired, "photos.zip is password protected.");

        var ex = await Assert.ThrowsAsync<LocationException>(() => rig.NamesAsync(Zip));

        Assert.Equal(LocationError.AuthenticationRequired, ex.Error);
    }

    [Fact]
    public async Task List_TheArchiveVanishedMeanwhile_FallsBackToTheWrappedProvider()
    {
        var rig = new Rig();
        await rig.NamesAsync(Zip);
        rig.Disk.RemoveFile(Zip);

        // The folder now looks like nothing: the wrapped provider answers.
        Assert.Equal(["real"], await rig.NamesAsync(Zip));
    }

    [Fact]
    public async Task Index_ReadsAnArchiveOnce_ForManyListings()
    {
        var rig = new Rig();

        await rig.NamesAsync(Zip);
        await rig.NamesAsync(Zip + @"\2024");
        await rig.NamesAsync(Zip + @"\2024\jan");

        Assert.Equal(1, rig.Reader.Reads);
    }

    [Fact]
    public async Task Index_AChangedArchive_IsReadAgain()
    {
        var rig = new Rig();
        await rig.NamesAsync(Zip);

        rig.Disk.File.WriteAllText(Zip, "more bytes than before");
        await rig.NamesAsync(Zip);

        Assert.Equal(2, rig.Reader.Reads);
    }

    [Fact]
    public async Task Index_TwoTabsOpeningTheSameArchive_ShareOneRead()
    {
        var rig = new Rig();
        var gate = new TaskCompletionSource();
        rig.Reader.ReadGate = gate.Task;

        var first = rig.NamesAsync(Zip);
        var second = rig.NamesAsync(Zip + @"\2024");
        gate.SetResult();
        await Task.WhenAll(first, second);

        Assert.Equal(1, rig.Reader.Reads);
    }

    [Fact]
    public async Task Index_ACallerThatGivesUp_DoesNotPoisonTheNext()
    {
        var rig = new Rig();
        var gate = new TaskCompletionSource();
        rig.Reader.ReadGate = gate.Task;
        using var cancel = new CancellationTokenSource();

        var impatient = rig.Index.GetAsync(Zip, cancel.Token);
        await cancel.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => impatient);
        var patient = rig.Index.GetAsync(Zip, CancellationToken.None);
        gate.SetResult();

        Assert.Equal(4, (await patient).Entries.Count);
        Assert.Equal(1, rig.Reader.Reads);
    }

    [Fact]
    public async Task Index_AFailedRead_IsNotRemembered()
    {
        var rig = new Rig();
        rig.Reader.Failure = new ArchiveException(ArchiveError.Unreadable, "bad");
        await Assert.ThrowsAsync<ArchiveException>(() => rig.Index.GetAsync(Zip, CancellationToken.None));

        rig.Reader.Failure = null;

        Assert.Equal(4, (await rig.Index.GetAsync(Zip, CancellationToken.None)).Entries.Count);
    }

    [Fact]
    public async Task Index_KeepsOnlyTheLastFewArchives()
    {
        var rig = new Rig();
        var small = new ArchiveIndex(rig.Reader, rig.Disk, capacity: 2);
        foreach (var name in new[] { "a", "b", "c" })
        {
            var path = $@"D:\Stuff\{name}.zip";
            rig.Reader.Add(path, Files);
            rig.Disk.AddFile(path, new MockFileData(name));
            await small.GetAsync(path, CancellationToken.None);
        }

        await small.GetAsync(@"D:\Stuff\a.zip", CancellationToken.None);

        Assert.Equal(4, rig.Reader.Reads);
    }

    [Fact]
    public async Task Stage_UnpacksTheEntryToAScratchFile()
    {
        var rig = new Rig();
        var stager = new ArchiveStager(rig.Index, rig.Disk, @"C:\Scratch");

        var path = await stager.StageAsync(LocationPath.Parse(Zip + @"\2024\b.jpg"), CancellationToken.None);

        Assert.StartsWith(@"C:\Scratch\", path);
        Assert.EndsWith(@"\2024\b.jpg", path);
        Assert.Equal("bb", rig.Disk.File.ReadAllText(path));
        Assert.DoesNotContain(rig.Disk.AllFiles, f => f.EndsWith(".leviathan-part", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Stage_TheSameEntryTwice_UnpacksOnce()
    {
        var rig = new Rig();
        var stager = new ArchiveStager(rig.Index, rig.Disk, @"C:\Scratch");
        var entry = LocationPath.Parse(Zip + @"\readme.txt");

        var first = await stager.StageAsync(entry, CancellationToken.None);
        var second = await stager.StageAsync(entry, CancellationToken.None);

        Assert.Equal(first, second);
        Assert.Equal(1, rig.Reader.Extractions);
    }

    [Fact]
    public async Task Stage_AFailedUnpack_LeavesNoPartFile()
    {
        var rig = new Rig();
        var stager = new ArchiveStager(rig.Index, rig.Disk, @"C:\Scratch");
        await rig.Index.GetAsync(Zip, CancellationToken.None);
        rig.Reader.Failure = new ArchiveException(ArchiveError.Unreadable, "bad data");

        await Assert.ThrowsAsync<ArchiveException>(() => stager.StageAsync(LocationPath.Parse(Zip + @"\readme.txt"), CancellationToken.None));

        Assert.DoesNotContain(rig.Disk.AllFiles, f => f.StartsWith(@"C:\Scratch", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Stage_AnEntryNotInTheArchive_IsEntryNotFound()
    {
        var rig = new Rig();
        var stager = new ArchiveStager(rig.Index, rig.Disk, @"C:\Scratch");

        var ex = await Assert.ThrowsAsync<ArchiveException>(() => stager.StageAsync(LocationPath.Parse(Zip + @"\ghost.txt"), CancellationToken.None));

        Assert.Equal(ArchiveError.EntryNotFound, ex.Error);
    }

    [Fact]
    public async Task Stage_APlainFile_IsReturnedAsItIs()
    {
        var rig = new Rig();
        var stager = new ArchiveStager(rig.Index, rig.Disk, @"C:\Scratch");

        Assert.Equal(@"D:\Stuff\notes.txt", await stager.StageAsync(LocationPath.Parse(@"D:\Stuff\notes.txt"), CancellationToken.None));
        Assert.Equal(Zip, await stager.StageAsync(LocationPath.Parse(Zip), CancellationToken.None));
    }

    [Fact]
    public async Task Clean_RemovesOldScratchFolders_KeepsRecentOnes()
    {
        var rig = new Rig();
        var clock = new ManualTimeProvider();
        var stager = new ArchiveStager(rig.Index, rig.Disk, @"C:\Scratch", time: clock);
        var old = await stager.StageAsync(LocationPath.Parse(Zip + @"\readme.txt"), CancellationToken.None);
        rig.Disk.File.SetLastAccessTimeUtc(old, clock.GetUtcNow().UtcDateTime);

        clock.Advance(TimeSpan.FromDays(3));

        Assert.Equal(1, stager.Clean(TimeSpan.FromDays(1)));
        Assert.False(rig.Disk.File.Exists(old));
        Assert.Equal(0, stager.Clean(TimeSpan.FromDays(1)));
    }
}
