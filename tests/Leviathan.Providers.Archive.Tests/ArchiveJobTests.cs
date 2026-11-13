using System.IO.Abstractions.TestingHelpers;
using Leviathan.Contracts;
using Leviathan.Core.Archives;
using Leviathan.Engine.Execution;
using Leviathan.Engine.Journal;
using Leviathan.Engine.Scheduling;
using Leviathan.Testing;

namespace Leviathan.Providers.Archive.Tests;

/// <summary>The extract and compress jobs run through the real job host with fakes for the archive engine (spec §15.4, §12.3).</summary>
public sealed class ArchiveJobTests
{
    private const string Zip = @"D:\In\photos.zip";

    private sealed class FakeWriter : IArchiveWriter
    {
        public List<ArchiveWriteEntry> Written { get; } = [];

        public string? Path { get; private set; }

        public string? Password { get; private set; }

        public ArchiveFormat Format { get; private set; }

        public ArchiveLevel Level { get; private set; }

        public Exception? Failure { get; set; }

        public Task? Gate { get; set; }

        public MockFileSystem? Disk { get; set; }

        public async Task WriteAsync(string archivePath, ArchiveFormat format, ArchiveLevel level, string? password, IReadOnlyList<ArchiveWriteEntry> entries, Action<long, string>? progress, CancellationToken cancellationToken)
        {
            Path = archivePath;
            Format = format;
            Level = level;
            Password = password;
            Written.AddRange(entries);
            Disk?.AddFile(archivePath, new MockFileData("archive bytes"));
            long done = 0;
            foreach (var entry in entries)
            {
                progress?.Invoke(done, entry.NameInArchive);
                done += entry.Size;
            }

            if (Gate is { } gate)
            {
                await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            }

            if (Failure is { } failure)
            {
                throw failure;
            }
        }
    }

    private static readonly Dictionary<string, string> Files = new()
    {
        ["readme.txt"] = "hello",
        ["2024/jan/a.jpg"] = "aaaa",
        ["2024/b.jpg"] = "bb",
        ["empty/"] = "",
    };

    private static async Task<(JobSnapshot Snapshot, JobResult Result, JobReport? Report)> RunAsync(IJobExecutor executor, JobRequest request)
    {
        await using var host = new JobHost(new JobScheduler(), [executor], new InMemoryJobJournal());
        host.Start();
        var job = host.Submit(request, JobLane.Operations, null, "test");
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!host.Scheduler.Find(job.Id)!.IsFinished)
        {
            Assert.True(DateTime.UtcNow < deadline, "the job did not finish");
            await Task.Delay(5);
        }

        var snapshot = host.Snapshot(host.Scheduler.Find(job.Id)!);
        return (snapshot, host.GetResult(job.Id)!, host.GetReport(job.Id));
    }

    private static (ArchiveExtractExecutor Executor, FakeArchiveReader Reader, MockFileSystem Disk) Extractor()
    {
        var reader = new FakeArchiveReader().Add(Zip, Files);
        var disk = new MockFileSystem();
        disk.AddFile(Zip, new MockFileData("zip"));
        disk.AddDirectory(@"D:\Out");
        return (new ArchiveExtractExecutor(reader, disk), reader, disk);
    }

    private static JobRequest Extract(string? destination = @"D:\Out", bool subfolder = false, ConflictPolicy conflict = ConflictPolicy.KeepBoth, string? password = null) =>
        new(JobKind.Extract, [Zip], destination) { ExtractIntoSubfolder = subfolder, Conflict = conflict, ArchivePassword = password };

    [Fact]
    public async Task Extract_WritesEveryFile_AndFolders_WithTheirTimes()
    {
        var (executor, _, disk) = Extractor();

        var (snapshot, _, _) = await RunAsync(executor, Extract());

        Assert.Equal(JobState.Completed, snapshot.State);
        Assert.Equal("hello", disk.File.ReadAllText(@"D:\Out\readme.txt"));
        Assert.Equal("aaaa", disk.File.ReadAllText(@"D:\Out\2024\jan\a.jpg"));
        Assert.Equal("bb", disk.File.ReadAllText(@"D:\Out\2024\b.jpg"));
        Assert.True(disk.Directory.Exists(@"D:\Out\empty"));
        Assert.Equal(new DateTime(2025, 6, 1, 12, 0, 0, DateTimeKind.Utc), disk.File.GetLastWriteTimeUtc(@"D:\Out\readme.txt"));
        Assert.DoesNotContain(disk.AllFiles, f => f.EndsWith(".leviathan-part", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Extract_IntoSubfolder_UsesTheArchiveName_AndAFreeNameWhenTaken()
    {
        var (executor, _, disk) = Extractor();

        await RunAsync(executor, Extract(subfolder: true));
        await RunAsync(executor, Extract(subfolder: true));

        Assert.True(disk.File.Exists(@"D:\Out\photos\readme.txt"));
        Assert.True(disk.File.Exists(@"D:\Out\photos (2)\readme.txt"));
    }

    [Fact]
    public async Task Extract_KeepBoth_NeverOverwrites()
    {
        var (executor, _, disk) = Extractor();
        disk.AddFile(@"D:\Out\readme.txt", new MockFileData("mine"));

        await RunAsync(executor, Extract());

        Assert.Equal("mine", disk.File.ReadAllText(@"D:\Out\readme.txt"));
        Assert.Equal("hello", disk.File.ReadAllText(@"D:\Out\readme (2).txt"));
    }

    [Fact]
    public async Task Extract_Replace_OverwritesOnlyWhenTheNewFileIsComplete()
    {
        var (executor, _, disk) = Extractor();
        disk.AddFile(@"D:\Out\readme.txt", new MockFileData("mine"));

        await RunAsync(executor, Extract(conflict: ConflictPolicy.Replace));

        Assert.Equal("hello", disk.File.ReadAllText(@"D:\Out\readme.txt"));
        Assert.False(disk.File.Exists(@"D:\Out\readme (2).txt"));
    }

    [Fact]
    public async Task Extract_Skip_LeavesTheExistingFile_AndReportsTheSkip()
    {
        var (executor, _, disk) = Extractor();
        disk.AddFile(@"D:\Out\readme.txt", new MockFileData("mine"));

        var (_, result, _) = await RunAsync(executor, Extract(conflict: ConflictPolicy.Skip));

        Assert.Equal("mine", disk.File.ReadAllText(@"D:\Out\readme.txt"));
        Assert.Contains(result.Items, i => i.Status == ItemStatus.Skipped && i.Source.EndsWith(@"\readme.txt", StringComparison.Ordinal));
        Assert.True(disk.File.Exists(@"D:\Out\2024\b.jpg"));
    }

    [Fact]
    public async Task Extract_AFileThatFails_IsReportedAndLeavesNoPartFile_TheRestStillExtract()
    {
        var (executor, reader, disk) = Extractor();
        reader.FailingEntries.Add("2024/b.jpg");

        var (snapshot, result, _) = await RunAsync(executor, Extract());

        Assert.Equal(JobState.Completed, snapshot.State);
        Assert.False(disk.File.Exists(@"D:\Out\2024\b.jpg"));
        Assert.DoesNotContain(disk.AllFiles, f => f.EndsWith(".leviathan-part", StringComparison.Ordinal));
        Assert.Contains(result.Items, i => i.Status == ItemStatus.Failed && i.Source.EndsWith(@"2024\b.jpg", StringComparison.Ordinal) && i.Error!.Contains("damaged"));
        Assert.True(disk.File.Exists(@"D:\Out\readme.txt"));
    }

    [Fact]
    public async Task Extract_NamesThatClimbOutOfTheFolder_AreRefusedOneByOne()
    {
        var reader = new FakeArchiveReader().Add(Zip, new Dictionary<string, string> { ["../evil.txt"] = "x", ["ok/fine.txt"] = "y", ["a:b.txt"] = "z", ["CON.txt"] = "w" });
        var disk = new MockFileSystem();
        disk.AddFile(Zip, new MockFileData("zip"));
        disk.AddDirectory(@"D:\Out");

        var (_, result, _) = await RunAsync(new ArchiveExtractExecutor(reader, disk), Extract());

        Assert.False(disk.File.Exists(@"D:\evil.txt"));
        Assert.Equal("y", disk.File.ReadAllText(@"D:\Out\ok\fine.txt"));
        Assert.Equal(3, result.Items.Count(i => i.Status == ItemStatus.Failed && i.Error!.StartsWith("Not extracted", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task Extract_AnArchiveThatCannotBeRead_FailsTheJobWithTheReason()
    {
        var (executor, reader, _) = Extractor();
        reader.Failure = new ArchiveException(ArchiveError.PasswordRequired, "photos.zip is password protected.");

        var (snapshot, result, _) = await RunAsync(executor, Extract());

        Assert.Equal(JobState.Failed, snapshot.State);
        Assert.Equal("photos.zip is password protected.", Assert.Single(result.Items).Error);
    }

    [Fact]
    public async Task Extract_ReportsBytesAndFiles()
    {
        var (executor, _, _) = Extractor();

        var (snapshot, _, _) = await RunAsync(executor, Extract());

        Assert.Equal(11, snapshot.BytesDone);
        Assert.Equal(11, snapshot.BytesTotal);
        Assert.Equal(3, snapshot.ItemsDone);
        Assert.Equal(3, snapshot.ItemsTotal);
    }

    [Fact]
    public async Task Extract_ASourceThatIsNotAnArchiveOnThisPc_IsSkippedWithAReason()
    {
        var (executor, _, _) = Extractor();

        var (_, result, _) = await RunAsync(executor, new JobRequest(JobKind.Extract, ["sftp://host/x/a.zip"], @"D:\Out"));

        Assert.Equal(ItemStatus.Skipped, Assert.Single(result.Items).Status);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Validator_ExtractNeedsADestination(string? destination)
    {
        Assert.NotNull(JobRequestValidator.Validate(new JobRequest(JobKind.Extract, [Zip], destination)));
        Assert.Null(JobRequestValidator.Validate(new JobRequest(JobKind.Extract, [Zip], @"D:\Out")));
    }

    [Fact]
    public void Validator_ExtractCannotAsk_AndCompressNeedsAnArchiveName()
    {
        Assert.NotNull(JobRequestValidator.Validate(Extract(conflict: ConflictPolicy.Ask)));
        Assert.NotNull(JobRequestValidator.Validate(new JobRequest(JobKind.Compress, [@"D:\a.txt"], @"D:\Out\a.rar")));
        Assert.NotNull(JobRequestValidator.Validate(new JobRequest(JobKind.Compress, [@"D:\a.txt"], null)));
        Assert.Null(JobRequestValidator.Validate(new JobRequest(JobKind.Compress, [@"D:\a.txt"], @"D:\Out\a.7z")));
        Assert.NotNull(JobRequestValidator.Validate(new JobRequest(JobKind.Compress, [@"D:\Work"], @"D:\Work\all.zip")));
    }

    private static (ArchiveCompressExecutor Executor, FakeWriter Writer, MockFileSystem Disk) Compressor()
    {
        var disk = new MockFileSystem();
        disk.AddFile(@"D:\Work\a.txt", new MockFileData("12345"));
        disk.AddFile(@"D:\Work\sub\b.txt", new MockFileData("123"));
        disk.AddFile(@"D:\Work\sub\deep\c.txt", new MockFileData("1"));
        disk.AddDirectory(@"D:\Work\emptydir");
        disk.AddFile(@"D:\Other\a.txt", new MockFileData("other"));
        var writer = new FakeWriter { Disk = disk };
        return (new ArchiveCompressExecutor(writer, disk), writer, disk);
    }

    [Fact]
    public async Task Compress_PacksFilesAndFolders_WithRelativeNames()
    {
        var (executor, writer, disk) = Compressor();

        var (snapshot, result, report) = await RunAsync(executor, new JobRequest(JobKind.Compress, [@"D:\Work\a.txt", @"D:\Work\sub"], @"D:\Out\pack.zip") { ArchiveLevel = ArchiveLevel.Maximum });

        Assert.Equal(JobState.Completed, snapshot.State);
        Assert.Equal(["a.txt", "sub/b.txt", "sub/deep/c.txt"], writer.Written.Select(e => e.NameInArchive).Order(StringComparer.Ordinal).ToArray());
        Assert.Equal(ArchiveLevel.Maximum, writer.Level);
        Assert.Equal(ArchiveFormat.Zip, writer.Format);
        Assert.True(disk.File.Exists(@"D:\Out\pack.zip"));
        Assert.DoesNotContain(disk.AllFiles, f => f.EndsWith(".leviathan-part", StringComparison.Ordinal));
        Assert.All(result.Items, i => Assert.Equal(ItemStatus.Done, i.Status));
        Assert.Equal(9, snapshot.BytesTotal);
        Assert.Null(report);
    }

    [Fact]
    public async Task Compress_WritesToAPartFileFirst_ThenRenames()
    {
        var (executor, writer, _) = Compressor();

        await RunAsync(executor, new JobRequest(JobKind.Compress, [@"D:\Work\a.txt"], @"D:\Out\pack.7z") { ArchiveFormat = ArchiveFormat.SevenZip });

        Assert.Equal(@"D:\Out\pack.7z.leviathan-part", writer.Path);
        Assert.Equal(ArchiveFormat.SevenZip, writer.Format);
    }

    [Fact]
    public async Task Compress_KeepsTheExistingArchive_AndUsesAFreeName()
    {
        var (executor, _, disk) = Compressor();
        disk.AddFile(@"D:\Out\pack.zip", new MockFileData("older"));

        await RunAsync(executor, new JobRequest(JobKind.Compress, [@"D:\Work\a.txt"], @"D:\Out\pack.zip"));

        Assert.Equal("older", disk.File.ReadAllText(@"D:\Out\pack.zip"));
        Assert.True(disk.File.Exists(@"D:\Out\pack (2).zip"));
    }

    [Fact]
    public async Task Compress_Replace_OverwritesTheArchive()
    {
        var (executor, _, disk) = Compressor();
        disk.AddFile(@"D:\Out\pack.zip", new MockFileData("older"));

        await RunAsync(executor, new JobRequest(JobKind.Compress, [@"D:\Work\a.txt"], @"D:\Out\pack.zip") { Conflict = ConflictPolicy.Replace });

        Assert.Equal("archive bytes", disk.File.ReadAllText(@"D:\Out\pack.zip"));
    }

    [Fact]
    public async Task Compress_Skip_WritesNothingWhenTheArchiveExists()
    {
        var (executor, writer, disk) = Compressor();
        disk.AddFile(@"D:\Out\pack.zip", new MockFileData("older"));

        var (snapshot, _, _) = await RunAsync(executor, new JobRequest(JobKind.Compress, [@"D:\Work\a.txt"], @"D:\Out\pack.zip") { Conflict = ConflictPolicy.Skip });

        Assert.Equal(JobState.Completed, snapshot.State);
        Assert.Empty(writer.Written);
    }

    [Fact]
    public async Task Compress_TwoItemsWithTheSameName_GetDistinctNamesInTheArchive()
    {
        var (executor, writer, _) = Compressor();

        await RunAsync(executor, new JobRequest(JobKind.Compress, [@"D:\Work\a.txt", @"D:\Other\a.txt"], @"D:\Out\pack.zip"));

        Assert.Equal(["a (2).txt", "a.txt"], writer.Written.Select(e => e.NameInArchive).Order(StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public async Task Compress_AFailedWrite_FailsTheJob_AndLeavesNoPartOrArchive()
    {
        var (executor, writer, disk) = Compressor();
        writer.Failure = new ArchiveException(ArchiveError.Unreadable, "pack.zip could not be created: disk full");

        var (snapshot, _, _) = await RunAsync(executor, new JobRequest(JobKind.Compress, [@"D:\Work\a.txt"], @"D:\Out\pack.zip"));

        Assert.Equal(JobState.Failed, snapshot.State);
        Assert.Contains("disk full", snapshot.Error);
        Assert.False(disk.File.Exists(@"D:\Out\pack.zip"));
        Assert.DoesNotContain(disk.AllFiles, f => f.EndsWith(".leviathan-part", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Compress_NothingToPack_FailsClearly()
    {
        var (executor, _, _) = Compressor();

        var (snapshot, _, _) = await RunAsync(executor, new JobRequest(JobKind.Compress, [@"D:\Work\emptydir"], @"D:\Out\pack.zip"));

        Assert.Equal(JobState.Failed, snapshot.State);
        Assert.Equal("There was nothing to compress.", snapshot.Error);
    }

    [Fact]
    public async Task Compress_PassesThePasswordToTheWriter_ButNeverToTheJournal()
    {
        var (executor, writer, _) = Compressor();
        var journal = new InMemoryJobJournal();
        await using var host = new JobHost(new JobScheduler(), [executor], journal);
        host.Start();

        var job = host.Submit(new JobRequest(JobKind.Compress, [@"D:\Work\a.txt"], @"D:\Out\pack.zip") { ArchivePassword = "s3cret" }, JobLane.Operations, null, "test");
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!host.Scheduler.Find(job.Id)!.IsFinished)
        {
            Assert.True(DateTime.UtcNow < deadline, "the job did not finish");
            await Task.Delay(5);
        }

        Assert.Equal("s3cret", writer.Password);
        Assert.All(journal.ReadAll(), r => Assert.Null(r.Request?.ArchivePassword));
    }

    [Fact]
    public async Task Compress_Cancelled_RemovesThePartFile()
    {
        var (executor, writer, disk) = Compressor();
        var gate = new TaskCompletionSource();
        writer.Gate = gate.Task;
        await using var host = new JobHost(new JobScheduler(), [executor], new InMemoryJobJournal());
        host.Start();
        var job = host.Submit(new JobRequest(JobKind.Compress, [@"D:\Work\a.txt"], @"D:\Out\pack.zip"), JobLane.Operations, null, "test");
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (writer.Path is null)
        {
            Assert.True(DateTime.UtcNow < deadline, "the writer did not start");
            await Task.Delay(5);
        }

        host.Cancel(job.Id);
        while (!host.Scheduler.Find(job.Id)!.IsFinished)
        {
            Assert.True(DateTime.UtcNow < deadline, "the job did not stop");
            await Task.Delay(5);
        }

        Assert.Equal(JobState.Cancelled, host.Snapshot(host.Scheduler.Find(job.Id)!).State);
        Assert.False(disk.File.Exists(@"D:\Out\pack.zip"));
        Assert.DoesNotContain(disk.AllFiles, f => f.EndsWith(".leviathan-part", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("a/b.txt", true)]
    [InlineData(@"a\b.txt", true)]
    [InlineData("../x.txt", false)]
    [InlineData("a/../../x.txt", false)]
    [InlineData("/abs.txt", false)]
    [InlineData("C:evil.txt", false)]
    [InlineData("what?.txt", false)]
    [InlineData("trailing./x.txt", false)]
    [InlineData("nul", false)]
    [InlineData("dir/COM1.log", false)]
    [InlineData("", false)]
    public void ExtractPaths_OnlyAllowsNamesThatStayInsideAndWindowsCanUse(string entry, bool allowed)
    {
        var path = ExtractPaths.Resolve(@"D:\Out", entry, out var problem);

        Assert.Equal(allowed, path is not null);
        Assert.Equal(!allowed, problem is not null);
        if (allowed)
        {
            Assert.StartsWith(@"D:\Out\", path, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Theory]
    [InlineData("photos.zip", "photos")]
    [InlineData("backup.tar.gz", "backup")]
    [InlineData("a.b.7z", "a.b")]
    [InlineData(".zip", "Extracted")]
    public void ExtractPaths_FolderName_DropsTheArchiveExtension(string archive, string folder)
    {
        Assert.Equal(folder, ExtractPaths.FolderNameFor(archive));
    }
}
