using System.IO.Abstractions;
using Leviathan.Contracts;
using Leviathan.Engine.Execution;
using Leviathan.Engine.Journal;
using Leviathan.Engine.Scheduling;
using Leviathan.Engine.Transfers;

namespace Leviathan.Integration.Tests;

/// <summary>The planned copy on a real disk with a real plan file: odd names, nested folders, and nothing left behind.</summary>
public sealed class RealDiskCopyTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "leviathan-copy-" + Guid.NewGuid().ToString("N"));

    public RealDiskCopyTests() => Directory.CreateDirectory(_root);

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

    [Fact]
    public async Task Copy_OfANestedTreeWithOddNames_ArrivesWhole_AndLeavesNoPartOrPlanFile()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows paths");
        var source = Path.Combine(_root, "src", "Tree ✓ 日本語");
        Directory.CreateDirectory(Path.Combine(source, "sub dir"));
        await File.WriteAllBytesAsync(Path.Combine(source, "a#b.bin"), new byte[3_000_000]);
        await File.WriteAllTextAsync(Path.Combine(source, "sub dir", "ünï.txt"), "hello");
        var destination = Path.Combine(_root, "dst");
        Directory.CreateDirectory(destination);
        var plans = Path.Combine(_root, "plans");
        await using var host = new JobHost(new JobScheduler(), [new CopyMoveExecutor(new FileSystem(), planStore: new FileTransferPlanStore(new FileSystem(), plans))], new InMemoryJobJournal());
        host.Start();

        var job = host.Submit(new JobRequest(JobKind.Copy, [source], destination) { Verify = VerifyMode.Full }, JobLane.Operations, null, "copy");
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (!host.Scheduler.Find(job.Id)!.IsFinished && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10);
        }

        Assert.Equal(JobState.Completed, host.Scheduler.Find(job.Id)!.State);
        var copied = Path.Combine(destination, "Tree ✓ 日本語");
        Assert.Equal(3_000_000, new FileInfo(Path.Combine(copied, "a#b.bin")).Length);
        Assert.Equal("hello", await File.ReadAllTextAsync(Path.Combine(copied, "sub dir", "ünï.txt")));
        Assert.Empty(Directory.EnumerateFiles(destination, "*" + CopyMoveExecutor.PartSuffix, SearchOption.AllDirectories));
        Assert.Empty(Directory.Exists(plans) ? Directory.EnumerateFiles(plans) : []);
        Assert.True(File.Exists(Path.Combine(source, "a#b.bin")));
    }
}

/// <summary>The progress journal on a real disk: appended, flushed, replayed after a "crash".</summary>
public sealed class RealDiskJournalTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "leviathan-journal-" + Guid.NewGuid().ToString("N"));

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

    [Fact]
    public void Journal_WrittenByOneProcess_IsReplayedByTheNext_EvenWithATornLastLine()
    {
        var id = Guid.NewGuid();
        var journal = new FileTransferJournal(new FileSystem(), _root);
        journal.StepDone(id, 0, 0);
        journal.FileConfirmed(id, 0, 1, 33_554_432);
        journal.ItemFinished(id, 2, Leviathan.Contracts.ItemStatus.Done, null);
        // The process dies here: its handle goes away with it, and the last write is cut short.
        journal.Dispose();
        File.AppendAllText(Path.Combine(_root, id.ToString("N") + ".progress"), "{\"T\":\"c\",\"I\":0,\"S\":1,\"B\":6710");

        var replay = new FileTransferJournal(new FileSystem(), _root).Load(id);

        Assert.Contains((0, 0), replay.DoneSteps);
        Assert.Equal(33_554_432, replay.Confirmed[(0, 1)]);
        Assert.Equal(Leviathan.Contracts.ItemStatus.Done, replay.Items[2].Status);
    }
}
