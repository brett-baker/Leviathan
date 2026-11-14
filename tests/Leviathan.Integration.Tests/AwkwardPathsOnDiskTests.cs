using System.IO.Abstractions;
using Leviathan.Contracts;
using Leviathan.Core.Paths;
using Leviathan.Core.Platform;
using Leviathan.Engine.Execution;
using Leviathan.Engine.Journal;
using Leviathan.Engine.Scheduling;
using Leviathan.Engine.Transfers;

namespace Leviathan.Integration.Tests;

/// <summary>
/// Long paths and awkward names are normal inputs (CLAUDE.md, build plan G3): copy, move and permanent delete of a real tree whose
/// paths pass 260 characters and whose names Windows normally refuses to create (a trailing dot or space, a device name). Everything
/// runs in a temporary folder; nothing leaves a part file behind.
/// </summary>
public sealed class AwkwardPathsOnDiskTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "leviathan-awkward-" + Guid.NewGuid().ToString("N"));

    public AwkwardPathsOnDiskTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            // The awkward names need the extended prefix to be removed too.
            Directory.Delete(Extended(_root), recursive: true);
        }
        catch (IOException)
        {
            // Best effort.
        }
    }

    private static string Extended(string path) => path.StartsWith(@"\\?\", StringComparison.Ordinal) ? path : @"\\?\" + path;

    /// <summary>A folder tree 14 levels deep, each level 24 characters, so the file inside is past 400 characters.</summary>
    private string DeepTree(string name, out string file)
    {
        var folder = Path.Combine(_root, name);
        var deep = folder;
        for (var level = 0; level < 14; level++)
        {
            deep = Path.Combine(deep, $"level {level:D2} with a long name");
        }

        Directory.CreateDirectory(deep);
        file = Path.Combine(deep, "payload ✓ 日本語.bin");
        File.WriteAllBytes(file, [.. Enumerable.Range(0, 5000).Select(i => (byte)(i % 251))]);
        return folder;
    }

    private async Task<ScheduledJob> RunAsync(JobRequest request, params IJobExecutor[] executors)
    {
        await using var host = new JobHost(new JobScheduler(), executors, new InMemoryJobJournal());
        host.Start();
        var job = host.Submit(request, JobLane.Operations, null, request.Kind.ToString());
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (!host.Scheduler.Find(job.Id)!.IsFinished && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10);
        }

        return host.Scheduler.Find(job.Id)!;
    }

    private static CopyMoveExecutor Copier() => new(new FileSystem());

    private static DeleteExecutor Deleter() => new(new FileSystem(), new NoBin());

    private sealed class NoBin : IRecycleBin
    {
        public Task RecycleAsync(LocationPath path, CancellationToken cancellationToken) => throw new InvalidOperationException("a permanent delete never uses the Recycle Bin");
    }

    [Fact]
    public async Task Copy_OfAPathPast400Characters_ArrivesWhole_AndLeavesNoPartFile()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows paths");
        var source = DeepTree("src", out var file);
        Assert.True(file.Length > 400, $"the test path is only {file.Length} characters");
        var destination = Path.Combine(_root, "dst");
        Directory.CreateDirectory(destination);

        var job = await RunAsync(new JobRequest(JobKind.Copy, [source], destination) { Verify = VerifyMode.Full }, Copier());

        Assert.True(job.State == JobState.Completed, job.Error ?? job.State.ToString());
        var copy = Path.Combine(destination, "src") + file[source.Length..];
        Assert.Equal(File.ReadAllBytes(file), File.ReadAllBytes(copy));
        Assert.Empty(Directory.EnumerateFiles(destination, "*" + CopyMoveExecutor.PartSuffix, SearchOption.AllDirectories));
    }

    [Fact]
    public async Task Move_OfAPathPast400Characters_ArrivesWhole_AndTheSourceIsGone()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows paths");
        var source = DeepTree("src", out var file);
        var bytes = File.ReadAllBytes(file);
        var destination = Path.Combine(_root, "dst");
        Directory.CreateDirectory(destination);

        var job = await RunAsync(new JobRequest(JobKind.Move, [source], destination), Copier());

        Assert.True(job.State == JobState.Completed, job.Error ?? job.State.ToString());
        Assert.Equal(bytes, File.ReadAllBytes(Path.Combine(destination, "src") + file[source.Length..]));
        Assert.False(Directory.Exists(source));
    }

    [Fact]
    public async Task PermanentDelete_OfAPathPast400Characters_RemovesTheWholeTree()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows paths");
        var source = DeepTree("src", out _);

        var job = await RunAsync(new JobRequest(JobKind.Delete, [source], null) { DeleteMode = DeleteMode.Permanent }, Deleter());

        Assert.True(job.State == JobState.Completed, job.Error ?? job.State.ToString());
        Assert.False(Directory.Exists(source));
    }

    // Names the Windows shell and most programs cannot touch, created through the extended path prefix. A file manager that opens a
    // drive for repair has to be able to copy and remove them (build plan G3: "names with every awkward character").
    private static readonly string[] AwkwardNames =
    [
        "trailing dot.", "trailing space ", " leading space.txt", "con", "aux.txt", "nul.log", "com1", "prn.", "a b  c.txt", "semi;colon,comma=eq.txt",
        "hash#percent%amp&.txt", "caf\u00e9 \u00fcber \u65e5\u672c\u8a9e \ud83d\udcc1.txt", "ends with two dots..", "(1) [2] {3}.txt", "'quote' `tick` ~tilde ^caret @at !bang $dollar.txt",
    ];

    private string AwkwardTree(string name)
    {
        var folder = Path.Combine(_root, name);
        Directory.CreateDirectory(folder);
        foreach (var awkward in AwkwardNames)
        {
            File.WriteAllText(Extended(Path.Combine(folder, awkward)), "content of " + awkward);
        }

        return folder;
    }

    public static TheoryData<string> Names() => [.. AwkwardNames];

    [Theory]
    [MemberData(nameof(Names))]
    public async Task Copy_OfAFileWithAnAwkwardName_KeepsTheNameAndEveryByte(string awkward)
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows paths");
        var source = Path.Combine(_root, "src");
        Directory.CreateDirectory(source);
        File.WriteAllText(Extended(Path.Combine(source, awkward)), "content of " + awkward);
        File.WriteAllText(Path.Combine(source, "ordinary.txt"), "ordinary");
        var destination = Path.Combine(_root, "dst");
        Directory.CreateDirectory(destination);

        var job = await RunAsync(new JobRequest(JobKind.Copy, [source], destination) { Verify = VerifyMode.Full }, Copier());

        Assert.True(job.State == JobState.Completed, $"'{awkward}': {job.Error ?? job.State.ToString()}");
        var copied = Directory.EnumerateFiles(Extended(Path.Combine(destination, "src"))).Select(p => Path.GetFileName(p)!).Order(StringComparer.Ordinal).ToList();
        Assert.Equal(new[] { awkward, "ordinary.txt" }.Order(StringComparer.Ordinal).ToList(), copied);
        Assert.Equal("content of " + awkward, File.ReadAllText(Extended(Path.Combine(destination, "src", awkward))));
        Assert.Empty(Directory.EnumerateFiles(Extended(destination), "*" + CopyMoveExecutor.PartSuffix, SearchOption.AllDirectories));
    }

    [Fact]
    public async Task CopyThenDelete_OfAFolderWhoseNameWindowsWouldChange_KeepsItsContents()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows paths");
        var source = Path.Combine(_root, "src");
        var odd = Path.Combine(source, "folder with a dot.");
        Directory.CreateDirectory(Extended(Path.Combine(odd, "inner con")));
        File.WriteAllText(Extended(Path.Combine(odd, "inside.txt")), "inside");
        File.WriteAllText(Extended(Path.Combine(odd, "inner con", "deep.txt")), "deep");
        var destination = Path.Combine(_root, "dst");
        Directory.CreateDirectory(destination);

        var copy = await RunAsync(new JobRequest(JobKind.Copy, [source], destination) { Verify = VerifyMode.Full }, Copier());

        Assert.True(copy.State == JobState.Completed, copy.Error ?? copy.State.ToString());
        var copied = Path.Combine(destination, "src", "folder with a dot.");
        Assert.Equal("inside", File.ReadAllText(Extended(Path.Combine(copied, "inside.txt"))));
        Assert.Equal("deep", File.ReadAllText(Extended(Path.Combine(copied, "inner con", "deep.txt"))));

        var delete = await RunAsync(new JobRequest(JobKind.Delete, [Path.Combine(destination, "src")], null) { DeleteMode = DeleteMode.Permanent }, Deleter());

        Assert.True(delete.State == JobState.Completed, delete.Error ?? delete.State.ToString());
        Assert.False(Directory.Exists(Extended(Path.Combine(destination, "src"))));
    }

    [Fact]
    public async Task PermanentDelete_OfFilesWithAwkwardNames_RemovesThemAll()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows paths");
        var source = AwkwardTree("src");

        var job = await RunAsync(new JobRequest(JobKind.Delete, [source], null) { DeleteMode = DeleteMode.Permanent }, Deleter());

        Assert.True(job.State == JobState.Completed, job.Error ?? job.State.ToString());
        Assert.False(Directory.Exists(Extended(source)));
    }
}
