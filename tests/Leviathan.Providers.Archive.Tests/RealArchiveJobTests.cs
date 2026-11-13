using System.Formats.Tar;
using System.IO.Abstractions;
using System.IO.Compression;
using System.Text;
using Leviathan.Contracts;
using Leviathan.Core.Archives;
using Leviathan.Engine.Execution;
using Leviathan.Engine.Journal;
using Leviathan.Engine.Scheduling;

namespace Leviathan.Providers.Archive.Tests;

/// <summary>Extract and compress jobs against real archives and a real folder (spec §15.4, §19.2).</summary>
public sealed class RealArchiveJobTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "leviathan-archive-jobs-" + Guid.NewGuid().ToString("N"));

    public RealArchiveJobTests() => Directory.CreateDirectory(_folder);

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

    private string P(params string[] parts) => Path.Combine([_folder, .. parts]);

    private static async Task<JobSnapshot> RunAsync(IJobExecutor executor, JobRequest request)
    {
        await using var host = new JobHost(new JobScheduler(), [executor], new InMemoryJobJournal());
        host.Start();
        var job = host.Submit(request, JobLane.Operations, null, "test");
        var deadline = DateTime.UtcNow.AddSeconds(60);
        while (!host.Scheduler.Find(job.Id)!.IsFinished)
        {
            Assert.True(DateTime.UtcNow < deadline, "the job did not finish");
            await Task.Delay(10);
        }

        return host.Snapshot(host.Scheduler.Find(job.Id)!);
    }

    private static ArchiveExtractExecutor Extractor() => new(CompositeArchiveReader.Default(), new FileSystem());

    private static ArchiveCompressExecutor Compressor() => new(new SevenZipArchiveWriter(), new FileSystem());

    private void MakeTree()
    {
        Directory.CreateDirectory(P("src", "docs", "deep"));
        File.WriteAllText(P("src", "top.txt"), "top file");
        File.WriteAllText(P("src", "docs", "a.txt"), "inside a");
        File.WriteAllBytes(P("src", "docs", "deep", "blob.bin"), Enumerable.Range(0, 70_000).Select(i => (byte)(i * 7)).ToArray());
        Directory.CreateDirectory(P("src", "emptydir"));
    }

    [Fact]
    public async Task Compress_ThenExtract_Zip_RoundTripsTheTree()
    {
        MakeTree();

        var made = await RunAsync(Compressor(), new JobRequest(JobKind.Compress, [P("src")], P("out", "tree.zip")) { ArchiveLevel = ArchiveLevel.Fast });
        var back = await RunAsync(Extractor(), new JobRequest(JobKind.Extract, [P("out", "tree.zip")], P("back")));

        Assert.True(made.State == JobState.Completed, "made: " + made.Error);
        Assert.Equal(JobState.Completed, back.State);
        Assert.False(File.Exists(P("out", "tree.zip.leviathan-part")));
        Assert.Equal("top file", File.ReadAllText(P("back", "src", "top.txt")));
        Assert.Equal("inside a", File.ReadAllText(P("back", "src", "docs", "a.txt")));
        Assert.Equal(File.ReadAllBytes(P("src", "docs", "deep", "blob.bin")), File.ReadAllBytes(P("back", "src", "docs", "deep", "blob.bin")));
    }

    [Fact]
    public async Task Compress_SevenZip_WithAPassword_NeedsItToExtract()
    {
        MakeTree();

        var made = await RunAsync(Compressor(), new JobRequest(JobKind.Compress, [P("src", "top.txt"), P("src", "docs")], P("out", "locked.7z")) { ArchiveFormat = ArchiveFormat.SevenZip, ArchiveLevel = ArchiveLevel.Maximum, ArchivePassword = "pa55" });
        var without = await RunAsync(Extractor(), new JobRequest(JobKind.Extract, [P("out", "locked.7z")], P("nope")));
        var wrong = await RunAsync(Extractor(), new JobRequest(JobKind.Extract, [P("out", "locked.7z")], P("nope")) { ArchivePassword = "bad" });
        var right = await RunAsync(Extractor(), new JobRequest(JobKind.Extract, [P("out", "locked.7z")], P("yes")) { ArchivePassword = "pa55" });

        Assert.True(made.State == JobState.Completed, "made: " + made.Error);
        Assert.Equal(JobState.Failed, without.State);
        Assert.Equal(JobState.Failed, wrong.State);
        Assert.True(right.State == JobState.Completed, "right: " + right.Error);
        Assert.Equal("top file", File.ReadAllText(P("yes", "top.txt")));
        Assert.Equal("inside a", File.ReadAllText(P("yes", "docs", "a.txt")));
        Assert.False(Directory.Exists(P("nope")) && Directory.EnumerateFiles(P("nope"), "*", SearchOption.AllDirectories).Any());
    }

    [Fact]
    public async Task Extract_TarGz_IntoASubfolder_ReportsProgressAndKeepsTimes()
    {
        var tar = P("data.tar.gz");
        var modified = new DateTimeOffset(2023, 4, 5, 6, 7, 8, TimeSpan.Zero);
        await using (var file = File.Create(tar))
        await using (var gzip = new GZipStream(file, System.IO.Compression.CompressionLevel.Fastest))
        await using (var writer = new TarWriter(gzip, TarEntryFormat.Pax))
        {
            await writer.WriteEntryAsync(new PaxTarEntry(TarEntryType.RegularFile, "./one.txt") { DataStream = new MemoryStream(Encoding.UTF8.GetBytes("one")), ModificationTime = modified });
            await writer.WriteEntryAsync(new PaxTarEntry(TarEntryType.RegularFile, "sub/two.txt") { DataStream = new MemoryStream(Encoding.UTF8.GetBytes("two two")) });
        }

        var snapshot = await RunAsync(Extractor(), new JobRequest(JobKind.Extract, [tar], P("dest")) { ExtractIntoSubfolder = true });

        Assert.Equal(JobState.Completed, snapshot.State);
        Assert.Equal("one", File.ReadAllText(P("dest", "data", "one.txt")));
        Assert.Equal("two two", File.ReadAllText(P("dest", "data", "sub", "two.txt")));
        Assert.Equal(modified.UtcDateTime, File.GetLastWriteTimeUtc(P("dest", "data", "one.txt")));
        Assert.Equal(10, snapshot.BytesDone);
        Assert.Equal(2, snapshot.ItemsDone);
    }

    [Fact]
    public async Task Extract_Zip_FromTheFramework_KeepsBothOnAClash()
    {
        var zip = P("a.zip");
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
        {
            await using var entry = archive.CreateEntry("note.txt").Open();
            await entry.WriteAsync(Encoding.UTF8.GetBytes("from zip"));
        }

        Directory.CreateDirectory(P("dest"));
        File.WriteAllText(P("dest", "note.txt"), "mine");

        var snapshot = await RunAsync(Extractor(), new JobRequest(JobKind.Extract, [zip], P("dest")));

        Assert.Equal(JobState.Completed, snapshot.State);
        Assert.Equal("mine", File.ReadAllText(P("dest", "note.txt")));
        Assert.Equal("from zip", File.ReadAllText(P("dest", "note (2).txt")));
    }
}
