using System.Formats.Tar;
using System.IO.Abstractions;
using System.IO.Compression;
using System.Text;
using Leviathan.Core.Archives;
using Leviathan.Core.Paths;
using Leviathan.Core.Providers;
using SevenZip;

namespace Leviathan.Providers.Archive.Tests;

/// <summary>
/// The readers against real archives in a real temporary folder: zip made by the framework, 7z and encrypted zip made by 7-Zip,
/// tar and tar.gz made by the framework, damaged files, and a cancelled extraction (spec §15.4, §19.2 "integration tests use real
/// files"). Needs <c>7z64.dll</c>, which is copied beside the tests.
/// </summary>
public sealed class RealArchiveTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "leviathan-archive-" + Guid.NewGuid().ToString("N"));

    public RealArchiveTests()
    {
        Directory.CreateDirectory(_folder);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_folder, recursive: true);
        }
        catch (IOException)
        {
            // Left in the temp folder.
        }
    }

    private string Make(string name) => Path.Combine(_folder, name);

    private string MakeZip(string name = "sample.zip")
    {
        var path = Make(name);
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        Write(zip, "readme.txt", "hello zip");
        Write(zip, "photos/2024/a.jpg", "AAAA");
        Write(zip, "photos/b.jpg", "BB");
        zip.CreateEntry("empty/");
        return path;
    }

    private static void Write(ZipArchive zip, string name, string text)
    {
        using var writer = new StreamWriter(zip.CreateEntry(name).Open(), new UTF8Encoding(false));
        writer.Write(text);
    }

    private string MakeTar(string name, bool gzip)
    {
        var path = Make(name);
        using var file = File.Create(path);
        using var outer = gzip ? new GZipStream(file, System.IO.Compression.CompressionLevel.Fastest) : (Stream)file;
        using var tar = new TarWriter(outer, TarEntryFormat.Pax, leaveOpen: true);
        tar.WriteEntry(new PaxTarEntry(TarEntryType.Directory, "./docs/"));
        foreach (var (entryName, text) in new[] { ("./top.txt", "top"), ("./docs/a.txt", "inside a"), ("docs/b.txt", "inside b") })
        {
            tar.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, entryName) { DataStream = new MemoryStream(Encoding.UTF8.GetBytes(text)) });
        }

        return path;
    }

    private static async Task<string> TextAsync(IArchiveReader reader, string archive, string entry, string? password = null)
    {
        using var buffer = new MemoryStream();
        await reader.ExtractEntryAsync(archive, entry, buffer, password, CancellationToken.None);
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    [Fact]
    public async Task SevenZip_Zip_ListsEntriesWithForwardSlashPaths()
    {
        var entries = await new SevenZipArchiveReader().ReadEntriesAsync(MakeZip(), null, CancellationToken.None);

        var files = entries.Where(e => !e.IsDirectory).Select(e => e.Path).Order().ToArray();
        Assert.Equal(["photos/2024/a.jpg", "photos/b.jpg", "readme.txt"], files);
        Assert.Equal(9, entries.Single(e => e.Path == "readme.txt").Size);
        Assert.NotNull(entries.Single(e => e.Path == "readme.txt").Modified);
    }

    [Fact]
    public async Task SevenZip_Zip_ExtractsOneEntry()
    {
        Assert.Equal("AAAA", await TextAsync(new SevenZipArchiveReader(), MakeZip(), "photos/2024/a.jpg"));
        Assert.Equal("hello zip", await TextAsync(new SevenZipArchiveReader(), MakeZip("again.zip"), @"README.TXT"));
    }

    [Fact]
    public async Task SevenZip_7z_RoundTrips()
    {
        var source = Make("src.txt");
        await File.WriteAllTextAsync(source, "seven zip content");
        var archive = Make("sample.7z");
        new SevenZipCompressor { ArchiveFormat = OutArchiveFormat.SevenZip, DirectoryStructure = false }.CompressFiles(archive, source);
        var reader = new SevenZipArchiveReader();

        var entries = await reader.ReadEntriesAsync(archive, null, CancellationToken.None);

        Assert.Equal("src.txt", Assert.Single(entries).Path);
        Assert.Equal("seven zip content", await TextAsync(reader, archive, "src.txt"));
    }

    [Fact]
    public async Task SevenZip_AnEntryThatIsNotThere_IsEntryNotFound()
    {
        var ex = await Assert.ThrowsAsync<ArchiveException>(() => TextAsync(new SevenZipArchiveReader(), MakeZip(), "nope.txt"));

        Assert.Equal(ArchiveError.EntryNotFound, ex.Error);
    }

    [Fact]
    public async Task SevenZip_NotAnArchive_IsUnreadable()
    {
        var path = Make("junk.zip");
        await File.WriteAllTextAsync(path, "this is not a zip file at all");

        var ex = await Assert.ThrowsAsync<ArchiveException>(() => new SevenZipArchiveReader().ReadEntriesAsync(path, null, CancellationToken.None));

        Assert.Equal(ArchiveError.Unreadable, ex.Error);
    }

    [Fact]
    public async Task SevenZip_APasswordProtectedZip_NeedsAndAcceptsThePassword()
    {
        var source = Make("secret.txt");
        await File.WriteAllTextAsync(source, "classified");
        var archive = Make("locked.zip");
        new SevenZipCompressor { ArchiveFormat = OutArchiveFormat.Zip, ZipEncryptionMethod = ZipEncryptionMethod.Aes256, DirectoryStructure = false }
            .CompressFilesEncrypted(archive, "pa55", source);
        var reader = new SevenZipArchiveReader();

        var entries = await reader.ReadEntriesAsync(archive, null, CancellationToken.None);
        var without = await Assert.ThrowsAsync<ArchiveException>(() => TextAsync(reader, archive, "secret.txt"));
        var wrong = await Assert.ThrowsAsync<ArchiveException>(() => TextAsync(reader, archive, "secret.txt", "nope"));

        Assert.True(entries.Single().Encrypted);
        Assert.Equal(ArchiveError.PasswordRequired, without.Error);
        Assert.Equal(ArchiveError.WrongPassword, wrong.Error);
        Assert.Equal("classified", await TextAsync(reader, archive, "secret.txt", "pa55"));
    }

    [Fact]
    public async Task SevenZip_CancellingMidExtraction_StopsItAndThrowsCancelled()
    {
        var source = Make("big.bin");
        var random = new Random(1);
        var bytes = new byte[8 << 20];
        random.NextBytes(bytes);
        await File.WriteAllBytesAsync(source, bytes);
        var archive = Make("big.zip");
        new SevenZipCompressor { ArchiveFormat = OutArchiveFormat.Zip, CompressionLevel = SevenZip.CompressionLevel.None, DirectoryStructure = false }.CompressFiles(archive, source);
        using var cancel = new CancellationTokenSource();
        var sink = new CancelAfterFirstWrite(cancel);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new SevenZipArchiveReader().ExtractEntryAsync(archive, "big.bin", sink, null, cancel.Token));

        Assert.True(sink.Length < bytes.Length);
    }

    [Fact]
    public async Task SevenZip_AGzipWithoutAName_IsNamedAfterTheArchive()
    {
        var path = Make("notes.txt.gz");
        await using (var file = File.Create(path))
        await using (var gzip = new GZipStream(file, System.IO.Compression.CompressionLevel.Fastest))
        {
            await gzip.WriteAsync(Encoding.UTF8.GetBytes("zipped text"));
        }

        var reader = new SevenZipArchiveReader();
        var entry = Assert.Single(await reader.ReadEntriesAsync(path, null, CancellationToken.None));

        Assert.Equal("notes.txt", entry.Path);
        Assert.Equal("zipped text", await TextAsync(reader, path, "notes.txt"));
    }

    [Theory]
    [InlineData("sample.tar", false)]
    [InlineData("sample.tar.gz", true)]
    [InlineData("sample.tgz", true)]
    public async Task Tar_ListsTheFilesNotTheWrapper(string name, bool gzip)
    {
        var entries = await new TarArchiveReader().ReadEntriesAsync(MakeTar(name, gzip), null, CancellationToken.None);

        Assert.Equal(["docs/a.txt", "docs/b.txt", "top.txt"], entries.Where(e => !e.IsDirectory).Select(e => e.Path).Order().ToArray());
        Assert.Contains(entries, e => e.IsDirectory && e.Path == "docs");
    }

    [Fact]
    public async Task Tar_ExtractsOneEntryFromTheMiddle()
    {
        var archive = MakeTar("sample.tar.gz", gzip: true);

        Assert.Equal("inside a", await TextAsync(new TarArchiveReader(), archive, "docs/a.txt"));
        Assert.Equal("inside b", await TextAsync(new TarArchiveReader(), archive, "DOCS/B.TXT"));
    }

    [Fact]
    public async Task Tar_AnEntryThatIsNotThere_IsEntryNotFound()
    {
        var ex = await Assert.ThrowsAsync<ArchiveException>(() => TextAsync(new TarArchiveReader(), MakeTar("a.tar", false), "ghost.txt"));

        Assert.Equal(ArchiveError.EntryNotFound, ex.Error);
    }

    [Fact]
    public async Task Tar_ATruncatedFile_IsUnreadableNotACrash()
    {
        var archive = MakeTar("cut.tar.gz", gzip: true);
        var bytes = await File.ReadAllBytesAsync(archive);
        await File.WriteAllBytesAsync(archive, bytes[..(bytes.Length / 2)]);

        await Assert.ThrowsAsync<ArchiveException>(() => new TarArchiveReader().ReadEntriesAsync(archive, null, CancellationToken.None));
    }

    [Fact]
    public void Composite_SendsTarToTheFrameworkAndTheRestToSevenZip()
    {
        Assert.True(TarArchiveReader.Handles(@"C:\x\a.TAR.GZ"));
        Assert.True(TarArchiveReader.Handles(@"C:\x\a.tgz"));
        Assert.False(TarArchiveReader.Handles(@"C:\x\a.gz"));
        Assert.False(TarArchiveReader.Handles(@"C:\x\a.zip"));
    }

    [Fact]
    public async Task EndToEnd_BrowseAndStageAZipOnTheRealDisk()
    {
        var zip = MakeZip();
        var disk = new FileSystem();
        var index = new ArchiveIndex(CompositeArchiveReader.Default(), disk);
        var inner = new Leviathan.Providers.Local.LocalLocationProvider();
        var provider = new ArchiveAwareLocationProvider(inner, index);
        var archiveLocation = LocationPath.Parse(zip);

        var top = new List<string>();
        await foreach (var batch in provider.ListAsync(archiveLocation, ListingOptions.Default, CancellationToken.None))
        {
            top.AddRange(batch.Items.Select(i => i.Name));
        }

        var photos = new List<string>();
        await foreach (var batch in provider.ListAsync(archiveLocation.Combine("photos"), ListingOptions.Default, CancellationToken.None))
        {
            photos.AddRange(batch.Items.Select(i => i.Name));
        }

        var stager = new ArchiveStager(index, disk, Make("stage"));
        var staged = await stager.StageAsync(archiveLocation.Combine("photos").Combine("b.jpg"), CancellationToken.None);

        Assert.Equal(["empty", "photos", "readme.txt"], top.Order(StringComparer.Ordinal).ToArray());
        Assert.Equal(["2024", "b.jpg"], photos.Order(StringComparer.Ordinal).ToArray());
        Assert.Equal("BB", await File.ReadAllTextAsync(staged));
    }

    private sealed class CancelAfterFirstWrite(CancellationTokenSource cancel) : Stream
    {
        private long _written;

        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => _written;

        public override long Position
        {
            get => _written;
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count)
        {
            _written += count;
            cancel.Cancel();
        }
    }
}
