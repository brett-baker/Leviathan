using System.IO.Abstractions.TestingHelpers;
using Leviathan.Contracts;
using Leviathan.Core.Paths;
using Leviathan.Engine.Execution;
using Leviathan.Engine.Journal;
using Leviathan.Engine.Scheduling;

namespace Leviathan.Providers.Remote.Tests;

/// <summary>Copy, move and delete between this PC and servers, and between servers, against an in-memory server with the rules of a real one (spec §12, §7.1).</summary>
public sealed class RemoteTransferExecutorTests
{
    private sealed class Node(bool isFolder)
    {
        public bool IsFolder { get; } = isFolder;

        public byte[] Data { get; set; } = [];

        public DateTimeOffset Modified { get; set; } = new(2026, 5, 6, 7, 8, 9, TimeSpan.Zero);
    }

    /// <summary>A server that keeps its files in memory and refuses what a real one refuses: renaming onto something, deleting a folder that is not empty, writing into a folder that is not there.</summary>
    private sealed class MemoryServer : IRemoteSessionFactory
    {
        public Dictionary<string, Node> Files { get; } = new(StringComparer.Ordinal) { ["/"] = new Node(true) };

        public int Renames { get; private set; }

        public HashSet<string> RefuseWriting { get; } = [];

        public bool TruncateWrites { get; set; }

        public void Put(string path, string text, DateTimeOffset? modified = null)
        {
            EnsureFolders(path);
            Files[path] = new Node(false) { Data = System.Text.Encoding.UTF8.GetBytes(text), Modified = modified ?? new DateTimeOffset(2026, 5, 6, 7, 8, 9, TimeSpan.Zero) };
        }

        public void PutFolder(string path)
        {
            EnsureFolders(path + "/x");
            Files.TryAdd(path, new Node(true));
        }

        public string Text(string path) => System.Text.Encoding.UTF8.GetString(Files[path].Data);

        public bool Has(string path) => Files.ContainsKey(path);

        private void EnsureFolders(string path)
        {
            var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
            var current = string.Empty;
            for (var i = 0; i < parts.Length - 1; i++)
            {
                current += "/" + parts[i];
                Files.TryAdd(current, new Node(true));
            }
        }

        public Task<IRemoteSession> ConnectAsync(RemoteEndpoint endpoint, CancellationToken cancellationToken) => Task.FromResult<IRemoteSession>(new Session(this));

        private sealed class Session(MemoryServer server) : IRemoteSession
        {
            public bool IsConnected => true;

            public Task<IReadOnlyList<RemoteItem>> ListAsync(string path, CancellationToken cancellationToken)
            {
                if (!server.Files.TryGetValue(path, out var folder) || !folder.IsFolder)
                {
                    throw new RemoteException(RemoteFailure.NotFound, "No such folder.");
                }

                var prefix = path == "/" ? "/" : path + "/";
                IReadOnlyList<RemoteItem> items =
                [
                    .. server.Files.Where(f => f.Key.StartsWith(prefix, StringComparison.Ordinal) && f.Key.Length > prefix.Length && !f.Key[prefix.Length..].Contains('/'))
                        .Select(f => new RemoteItem(f.Key[prefix.Length..], f.Key, f.Value.IsFolder, f.Value.IsFolder ? 0 : f.Value.Data.Length, f.Value.Modified)),
                ];
                return Task.FromResult(items);
            }

            public Task<RemoteItem?> StatAsync(string path, CancellationToken cancellationToken) =>
                Task.FromResult(server.Files.TryGetValue(path, out var n) ? new RemoteItem(path.Split('/').Last(), path, n.IsFolder, n.IsFolder ? 0 : n.Data.Length, n.Modified) : null);

            public Task<Stream> OpenReadAsync(string path, CancellationToken cancellationToken) =>
                server.Files.TryGetValue(path, out var n) && !n.IsFolder ? Task.FromResult<Stream>(new MemoryStream(n.Data, writable: false)) : throw new RemoteException(RemoteFailure.NotFound, "No such file.");

            public async Task WriteAsync(string path, Stream content, CancellationToken cancellationToken)
            {
                if (server.RefuseWriting.Any(r => path.Contains(r, StringComparison.Ordinal)))
                {
                    throw new RemoteException(RemoteFailure.AccessDenied, "Permission denied.");
                }

                var parent = path[..path.LastIndexOf('/')];
                if (parent.Length > 0 && !server.Files.ContainsKey(parent))
                {
                    throw new RemoteException(RemoteFailure.NotFound, "No such folder.");
                }

                using var copy = new MemoryStream();
                await content.CopyToAsync(copy, cancellationToken);
                var data = copy.ToArray();
                server.Files[path] = new Node(false) { Data = server.TruncateWrites && data.Length > 1 ? data[..(data.Length / 2)] : data, Modified = DateTimeOffset.UtcNow };
            }

            public Task RenameAsync(string from, string to, CancellationToken cancellationToken)
            {
                if (!server.Files.ContainsKey(from))
                {
                    throw new RemoteException(RemoteFailure.NotFound, "No such file.");
                }

                if (server.Files.ContainsKey(to))
                {
                    throw new RemoteException(RemoteFailure.Unknown, "Target exists.");
                }

                server.Renames++;
                foreach (var key in server.Files.Keys.Where(k => k == from || k.StartsWith(from + "/", StringComparison.Ordinal)).ToList())
                {
                    var node = server.Files[key];
                    server.Files.Remove(key);
                    server.Files[to + key[from.Length..]] = node;
                }

                return Task.CompletedTask;
            }

            public Task DeleteAsync(string path, bool isFolder, CancellationToken cancellationToken)
            {
                if (!server.Files.ContainsKey(path))
                {
                    throw new RemoteException(RemoteFailure.NotFound, "No such file.");
                }

                if (isFolder && server.Files.Keys.Any(k => k.StartsWith(path + "/", StringComparison.Ordinal)))
                {
                    throw new RemoteException(RemoteFailure.Unknown, "Folder is not empty.");
                }

                server.Files.Remove(path);
                return Task.CompletedTask;
            }

            public Task CreateFolderAsync(string path, CancellationToken cancellationToken)
            {
                if (server.Files.ContainsKey(path))
                {
                    throw new RemoteException(RemoteFailure.Unknown, "Already there.");
                }

                server.Files[path] = new Node(true);
                return Task.CompletedTask;
            }

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    private sealed class Rig
    {
        public Rig()
        {
            Nas = new MemoryServer();
            Other = new MemoryServer();
            Remote = new RemoteLocationProvider(new Dictionary<RemoteProtocol, IRemoteSessionFactory> { [RemoteProtocol.Sftp] = new Router(this) });
            Disk = new MockFileSystem();
            Disk.AddDrive("C:", new MockDriveData { TotalSize = 1L << 30, AvailableFreeSpace = 1L << 29 });
            Disk.AddDirectory(@"C:\local");
            Disk.AddDirectory(@"C:\down");
            Executor = new RemoteTransferExecutor(Remote, Disk);
        }

        public MemoryServer Nas { get; }

        public MemoryServer Other { get; }

        public RemoteLocationProvider Remote { get; }

        public MockFileSystem Disk { get; }

        public RemoteTransferExecutor Executor { get; }

        private sealed class Router(Rig rig) : IRemoteSessionFactory
        {
            public Task<IRemoteSession> ConnectAsync(RemoteEndpoint endpoint, CancellationToken cancellationToken) =>
                (endpoint.Host == "other" ? rig.Other : rig.Nas).ConnectAsync(endpoint, cancellationToken);
        }

        public async Task<(JobSnapshot Job, IReadOnlyList<ItemOutcome> Items)> RunAsync(JobRequest request)
        {
            await using var host = new JobHost(new JobScheduler(), [Executor], new InMemoryJobJournal());
            host.Start();
            var job = host.Submit(request, JobLane.Operations, null, "test");
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (!host.Scheduler.Find(job.Id)!.IsFinished)
            {
                Assert.True(DateTime.UtcNow < deadline, "the job did not finish");
                await Task.Delay(5);
            }

            return (host.Snapshot(host.Scheduler.Find(job.Id)!), host.GetResult(job.Id)!.Items);
        }
    }

    private const string Nas = "sftp://brett@nas/";
    private const string Other = "sftp://brett@other/";

    private static JobRequest Copy(string destination, ConflictPolicy conflict = ConflictPolicy.KeepBoth, ErrorMode onError = ErrorMode.SkipAndReport, params string[] sources) =>
        new(JobKind.Copy, sources, destination) { Conflict = conflict, OnError = onError };

    private static JobRequest Move(string destination, ConflictPolicy conflict = ConflictPolicy.KeepBoth, params string[] sources) =>
        new(JobKind.Move, sources, destination) { Conflict = conflict, OnError = ErrorMode.SkipAndReport };

    [Fact]
    public async Task Upload_AFile_ArrivesWhole_WithNoPartFile_AndTheOriginalStays()
    {
        var rig = new Rig();
        rig.Nas.PutFolder("/media");
        rig.Disk.AddFile(@"C:\local\a.txt", new MockFileData("hello world"));

        var (job, items) = await rig.RunAsync(Copy(Nas + "media", sources: @"C:\local\a.txt"));

        Assert.Equal(JobState.Completed, job.State);
        Assert.Equal("hello world", rig.Nas.Text("/media/a.txt"));
        Assert.DoesNotContain(rig.Nas.Files.Keys, k => k.EndsWith(RemoteTransferExecutor.PartSuffix, StringComparison.Ordinal));
        Assert.True(rig.Disk.File.Exists(@"C:\local\a.txt"));
        Assert.Equal(11, job.BytesDone);
        Assert.Equal(ItemStatus.Done, Assert.Single(items).Status);
    }

    [Fact]
    public async Task Upload_AFolderTree_KeepsTheStructureAndEmptyFolders()
    {
        var rig = new Rig();
        rig.Nas.PutFolder("/media");
        rig.Disk.AddFile(@"C:\local\proj\readme.txt", new MockFileData("r"));
        rig.Disk.AddFile(@"C:\local\proj\src\deep\code.cs", new MockFileData("class C {}"));
        rig.Disk.AddDirectory(@"C:\local\proj\empty");

        var (job, _) = await rig.RunAsync(Copy(Nas + "media", sources: @"C:\local\proj"));

        Assert.Equal(JobState.Completed, job.State);
        Assert.Equal("r", rig.Nas.Text("/media/proj/readme.txt"));
        Assert.Equal("class C {}", rig.Nas.Text("/media/proj/src/deep/code.cs"));
        Assert.True(rig.Nas.Files["/media/proj/empty"].IsFolder);
    }

    [Fact]
    public async Task Download_AFileAndAFolder_ComeDownWithTheirTimes()
    {
        var rig = new Rig();
        var when = new DateTimeOffset(2025, 1, 2, 3, 4, 5, TimeSpan.Zero);
        rig.Nas.Put("/media/clip.mkv", "video bytes", when);
        rig.Nas.Put("/media/set/one.txt", "1", when);
        rig.Nas.Put("/media/set/two.txt", "22", when);

        var (job, _) = await rig.RunAsync(Copy(@"C:\down", sources: [Nas + "media/clip.mkv", Nas + "media/set"]));

        Assert.Equal(JobState.Completed, job.State);
        Assert.Equal("video bytes", rig.Disk.File.ReadAllText(@"C:\down\clip.mkv"));
        Assert.Equal("22", rig.Disk.File.ReadAllText(@"C:\down\set\two.txt"));
        Assert.Equal(when.UtcDateTime, rig.Disk.File.GetLastWriteTimeUtc(@"C:\down\clip.mkv"));
        Assert.DoesNotContain(rig.Disk.AllFiles, f => f.EndsWith(RemoteTransferExecutor.PartSuffix, StringComparison.Ordinal));
        Assert.True(rig.Nas.Has("/media/clip.mkv"));
    }

    [Fact]
    public async Task Move_ToAServer_DeletesTheSourceOnlyAfterItArrived_AndRemovesEmptiedFolders()
    {
        var rig = new Rig();
        rig.Nas.PutFolder("/media");
        rig.Disk.AddFile(@"C:\local\set\a.txt", new MockFileData("aaa"));
        rig.Disk.AddFile(@"C:\local\set\sub\b.txt", new MockFileData("bbb"));

        var (job, _) = await rig.RunAsync(Move(Nas + "media", sources: @"C:\local\set"));

        Assert.Equal(JobState.Completed, job.State);
        Assert.Equal("bbb", rig.Nas.Text("/media/set/sub/b.txt"));
        Assert.False(rig.Disk.Directory.Exists(@"C:\local\set"));
    }

    [Fact]
    public async Task Move_WhenTheServerKeepsLessThanWasSent_TheSourceIsKept_AndTheBadCopyRemoved()
    {
        var rig = new Rig();
        rig.Nas.PutFolder("/media");
        rig.Nas.TruncateWrites = true;
        rig.Disk.AddFile(@"C:\local\big.bin", new MockFileData(new byte[100]));

        var (job, items) = await rig.RunAsync(Move(Nas + "media", sources: @"C:\local\big.bin"));

        Assert.Equal(JobState.Failed, job.State);
        Assert.True(rig.Disk.File.Exists(@"C:\local\big.bin"), "the original must not be deleted when the copy is short");
        Assert.False(rig.Nas.Has("/media/big.bin"));
        Assert.Equal(ItemStatus.Failed, Assert.Single(items).Status);
    }

    [Fact]
    public async Task Move_OnTheSameServer_IsARename_NotACopy()
    {
        var rig = new Rig();
        rig.Nas.Put("/inbox/set/a.txt", "a");
        rig.Nas.Put("/inbox/set/deep/b.txt", "b");
        rig.Nas.PutFolder("/keep");

        var (job, _) = await rig.RunAsync(Move(Nas + "keep", sources: Nas + "inbox/set"));

        Assert.Equal(JobState.Completed, job.State);
        Assert.Equal(1, rig.Nas.Renames);
        Assert.Equal("b", rig.Nas.Text("/keep/set/deep/b.txt"));
        Assert.False(rig.Nas.Has("/inbox/set"));
        Assert.Equal(0, job.BytesDone);
    }

    [Fact]
    public async Task Copy_BetweenTwoServers_StreamsThroughThisPc()
    {
        var rig = new Rig();
        rig.Nas.Put("/a/doc.txt", "from nas");
        rig.Other.PutFolder("/b");

        var (job, _) = await rig.RunAsync(Copy(Other + "b", sources: Nas + "a/doc.txt"));

        Assert.Equal(JobState.Completed, job.State);
        Assert.Equal("from nas", rig.Other.Text("/b/doc.txt"));
        Assert.True(rig.Nas.Has("/a/doc.txt"));
    }

    [Fact]
    public async Task Move_BetweenTwoServers_CopiesThenDeletes()
    {
        var rig = new Rig();
        rig.Nas.Put("/a/doc.txt", "from nas");
        rig.Other.PutFolder("/b");

        var (job, _) = await rig.RunAsync(Move(Other + "b", sources: Nas + "a/doc.txt"));

        Assert.Equal(JobState.Completed, job.State);
        Assert.Equal("from nas", rig.Other.Text("/b/doc.txt"));
        Assert.False(rig.Nas.Has("/a/doc.txt"));
    }

    [Fact]
    public async Task AClash_KeepBoth_NumbersTheNewName_AndLeavesTheOldOne()
    {
        var rig = new Rig();
        rig.Nas.Put("/media/a.txt", "old");
        rig.Disk.AddFile(@"C:\local\a.txt", new MockFileData("new"));

        await rig.RunAsync(Copy(Nas + "media", ConflictPolicy.KeepBoth, sources: @"C:\local\a.txt"));

        Assert.Equal("old", rig.Nas.Text("/media/a.txt"));
        Assert.Equal("new", rig.Nas.Text("/media/a (2).txt"));
    }

    [Fact]
    public async Task AClash_Skip_LeavesBothAsTheyAre_AndSaysSkipped()
    {
        var rig = new Rig();
        rig.Nas.Put("/media/a.txt", "old");
        rig.Disk.AddFile(@"C:\local\a.txt", new MockFileData("new"));

        var (job, items) = await rig.RunAsync(Copy(Nas + "media", ConflictPolicy.Skip, sources: @"C:\local\a.txt"));

        Assert.Equal(JobState.Completed, job.State);
        Assert.Equal("old", rig.Nas.Text("/media/a.txt"));
        Assert.Equal(ItemStatus.Skipped, Assert.Single(items).Status);
    }

    [Fact]
    public async Task AClash_Replace_PutsTheNewContentThere_WithoutLosingTheFileIfTheCopyFails()
    {
        var rig = new Rig();
        rig.Nas.Put("/media/a.txt", "old");
        rig.Disk.AddFile(@"C:\local\a.txt", new MockFileData("new content"));

        await rig.RunAsync(Copy(Nas + "media", ConflictPolicy.Replace, sources: @"C:\local\a.txt"));

        Assert.Equal("new content", rig.Nas.Text("/media/a.txt"));
        Assert.DoesNotContain(rig.Nas.Files.Keys, k => k.EndsWith(RemoteTransferExecutor.PartSuffix, StringComparison.Ordinal));
    }

    [Fact]
    public async Task AClash_ReplaceIfNewer_OnlyReplacesOlderFiles()
    {
        var rig = new Rig();
        rig.Nas.Put("/media/new.txt", "server newer", new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero));
        rig.Nas.Put("/media/old.txt", "server older", new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero));
        rig.Disk.AddFile(@"C:\local\new.txt", new MockFileData("local") { LastWriteTime = new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero) });
        rig.Disk.AddFile(@"C:\local\old.txt", new MockFileData("local") { LastWriteTime = new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero) });

        await rig.RunAsync(Copy(Nas + "media", ConflictPolicy.ReplaceIfNewer, sources: [@"C:\local\new.txt", @"C:\local\old.txt"]));

        Assert.Equal("server newer", rig.Nas.Text("/media/new.txt"));
        Assert.Equal("local", rig.Nas.Text("/media/old.txt"));
    }

    [Fact]
    public async Task AFolderThatIsAlreadyThere_IsMergedInto_FileByFile()
    {
        var rig = new Rig();
        rig.Nas.Put("/media/set/keep.txt", "keep");
        rig.Disk.AddFile(@"C:\local\set\add.txt", new MockFileData("add"));

        await rig.RunAsync(Copy(Nas + "media", sources: @"C:\local\set"));

        Assert.Equal("keep", rig.Nas.Text("/media/set/keep.txt"));
        Assert.Equal("add", rig.Nas.Text("/media/set/add.txt"));
    }

    [Fact]
    public async Task AFileTheServerRefuses_IsReported_AndTheRestOfTheFolderStillArrives()
    {
        var rig = new Rig();
        rig.Nas.PutFolder("/media");
        rig.Nas.RefuseWriting.Add("secret");
        rig.Disk.AddFile(@"C:\local\set\fine.txt", new MockFileData("fine"));
        rig.Disk.AddFile(@"C:\local\set\secret.txt", new MockFileData("nope"));

        var (job, items) = await rig.RunAsync(Copy(Nas + "media", sources: @"C:\local\set"));

        Assert.Equal(JobState.Failed, job.State);
        Assert.Equal("fine", rig.Nas.Text("/media/set/fine.txt"));
        Assert.False(rig.Nas.Has("/media/set/secret.txt"));
        var item = Assert.Single(items);
        Assert.Equal(ItemStatus.Failed, item.Status);
        Assert.Contains("secret.txt", item.Error, StringComparison.Ordinal);
        Assert.True(rig.Disk.File.Exists(@"C:\local\set\secret.txt"));
    }

    [Fact]
    public async Task ASourceThatIsGone_IsReportedNotAnException()
    {
        var rig = new Rig();
        rig.Nas.PutFolder("/media");

        var (job, items) = await rig.RunAsync(Copy(Nas + "media", sources: @"C:\local\nothing.txt"));

        Assert.Equal(JobState.Failed, job.State);
        Assert.Contains("not there", Assert.Single(items).Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ADestinationThatIsNotAFolder_FailsTheJobWithAMessage()
    {
        var rig = new Rig();
        rig.Disk.AddFile(@"C:\local\a.txt", new MockFileData("x"));

        var (job, _) = await rig.RunAsync(Copy(Nas + "nowhere", sources: @"C:\local\a.txt"));

        Assert.Equal(JobState.Failed, job.State);
        Assert.Contains("does not exist", job.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Delete_RemovesAFolderTreeAndAFile_OnTheServer()
    {
        var rig = new Rig();
        rig.Nas.Put("/old/a/1.txt", "1");
        rig.Nas.Put("/old/a/b/2.txt", "2");
        rig.Nas.Put("/old/keep.txt", "k");

        var (job, items) = await rig.RunAsync(new JobRequest(JobKind.Delete, [Nas + "old/a", Nas + "old/keep.txt"], null));

        Assert.Equal(JobState.Completed, job.State);
        Assert.Equal(2, items.Count);
        Assert.False(rig.Nas.Has("/old/a"));
        Assert.False(rig.Nas.Has("/old/a/b/2.txt"));
        Assert.False(rig.Nas.Has("/old/keep.txt"));
        Assert.True(rig.Nas.Has("/old"));
    }

    [Fact]
    public async Task ASpeedLimit_SlowsTheCopy()
    {
        var rig = new Rig();
        rig.Nas.PutFolder("/media");
        rig.Disk.AddFile(@"C:\local\big.bin", new MockFileData(new byte[200_000]));
        var started = System.Diagnostics.Stopwatch.StartNew();

        await rig.RunAsync(new JobRequest(JobKind.Copy, [@"C:\local\big.bin"], Nas + "media") { BytesPerSecondLimit = 100_000, OnError = ErrorMode.SkipAndReport });

        Assert.True(started.Elapsed > TimeSpan.FromSeconds(1), $"took {started.Elapsed}");
    }

    [Fact]
    public void Involves_IsTrueForAnyServerSourceOrDestination()
    {
        Assert.True(RemoteTransferExecutor.Involves(new JobRequest(JobKind.Copy, [@"C:\a"], "sftp://u@h/x")));
        Assert.True(RemoteTransferExecutor.Involves(new JobRequest(JobKind.Move, ["ftp://u@h/x/a"], @"C:\b")));
        Assert.True(RemoteTransferExecutor.Involves(new JobRequest(JobKind.Delete, ["ftps://h/x"], null)));
        Assert.False(RemoteTransferExecutor.Involves(new JobRequest(JobKind.Copy, [@"C:\a"], @"\\nas\media")));
    }

    private sealed class Spy(bool handles) : IJobExecutor
    {
        public int Runs { get; private set; }

        public bool CanExecute(JobKind kind) => handles;

        public Task<JobOutcome> ExecuteAsync(JobContext context, CancellationToken cancellationToken)
        {
            Runs++;
            return Task.FromResult(JobOutcome.Completed);
        }
    }

    [Fact]
    public async Task TheRouter_SendsServerJobsToTheRemoteExecutor_AndTheRestToTheLocalOne()
    {
        var rig = new Rig();
        rig.Nas.PutFolder("/media");
        rig.Disk.AddFile(@"C:\local\a.txt", new MockFileData("x"));
        var local = new Spy(handles: true);
        var routed = new RemoteAwareExecutor(rig.Executor, local);
        await using var host = new JobHost(new JobScheduler(), [routed], new InMemoryJobJournal());
        host.Start();

        var first = host.Submit(Copy(@"C:\down", sources: @"C:\local\a.txt"), JobLane.Operations, null, "local");
        var second = host.Submit(Copy(Nas + "media", sources: @"C:\local\a.txt"), JobLane.Operations, null, "remote");
        while (!host.Scheduler.Find(first.Id)!.IsFinished || !host.Scheduler.Find(second.Id)!.IsFinished)
        {
            await Task.Delay(5);
        }

        Assert.Equal(1, local.Runs);
        Assert.Equal("x", rig.Nas.Text("/media/a.txt"));
        Assert.True(routed.CanExecute(JobKind.Copy));
    }
}
