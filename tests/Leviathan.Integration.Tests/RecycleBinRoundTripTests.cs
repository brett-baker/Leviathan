using System.IO.Abstractions;
using Leviathan.Contracts;
using Leviathan.Engine.Execution;
using Leviathan.Engine.Journal;
using Leviathan.Engine.Scheduling;
using Leviathan.Engine.Transfers;
using Leviathan.Interop;

namespace Leviathan.Integration.Tests;

/// <summary>
/// The real Windows Recycle Bin: delete a file with <c>IFileOperation</c>, find it again through the <c>$I</c> record and restore it
/// (spec §12.5). The unit tests build the record by hand; this proves the layout read by <see cref="RecycleBinIndex"/> is the one
/// this version of Windows writes.
/// </summary>
public sealed class RecycleBinRoundTripTests : IAsyncDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "leviathan-bin-" + Guid.NewGuid().ToString("N"));

    public ValueTask DisposeAsync()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // Best effort.
        }

        return ValueTask.CompletedTask;
    }

    private static async Task<JobResult> RunAsync(JobHost host, JobRequest request)
    {
        var job = host.Submit(request, JobLane.Operations, null, "test");
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (!host.Scheduler.Find(job.Id)!.IsFinished)
        {
            Assert.True(DateTime.UtcNow < deadline, "The job did not finish.");
            await Task.Delay(20, TestContext.Current.CancellationToken);
        }

        return host.GetResult(job.Id)!;
    }

    [Fact]
    public async Task DeletedFile_IsFoundInTheRealBin_AndRestoredIntact()
    {
        Directory.CreateDirectory(_root);
        var file = Path.Combine(_root, "report 1.txt");
        await File.WriteAllTextAsync(file, "precious", TestContext.Current.CancellationToken);
        var fs = new FileSystem();
        await using var host = new JobHost(new JobScheduler(), [new DeleteExecutor(fs, new WindowsRecycleBin()), new RestoreExecutor(fs)], new InMemoryJobJournal());
        host.Start();

        var deleted = await RunAsync(host, new JobRequest(JobKind.Delete, [file], null));

        Assert.False(File.Exists(file));
        var where = deleted.Items[0].Result;
        if (where is null)
        {
            Assert.Skip("The Recycle Bin is turned off for the temp drive, so there is nothing to find.");
        }

        Assert.Contains("$Recycle.Bin", where, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("precious", await File.ReadAllTextAsync(where, TestContext.Current.CancellationToken));

        var restored = await RunAsync(host, new JobRequest(JobKind.Restore, [where], null) { Targets = [file] });

        Assert.Equal(ItemStatus.Done, restored.Items[0].Status);
        Assert.Equal("precious", await File.ReadAllTextAsync(file, TestContext.Current.CancellationToken));
        Assert.False(File.Exists(where));
    }

    [Fact]
    public async Task DeletedFolder_IsFoundInTheRealBin_AndRestoredWithItsContents()
    {
        var folder = Path.Combine(_root, "album");
        Directory.CreateDirectory(Path.Combine(folder, "cd1"));
        await File.WriteAllTextAsync(Path.Combine(folder, "cd1", "track.txt"), "la", TestContext.Current.CancellationToken);
        var fs = new FileSystem();
        await using var host = new JobHost(new JobScheduler(), [new DeleteExecutor(fs, new WindowsRecycleBin()), new RestoreExecutor(fs)], new InMemoryJobJournal());
        host.Start();

        var deleted = await RunAsync(host, new JobRequest(JobKind.Delete, [folder], null));

        var where = deleted.Items[0].Result;
        if (where is null)
        {
            Assert.Skip("The Recycle Bin is turned off for the temp drive, so there is nothing to find.");
        }

        await RunAsync(host, new JobRequest(JobKind.Restore, [where], null) { Targets = [folder] });

        Assert.Equal("la", await File.ReadAllTextAsync(Path.Combine(folder, "cd1", "track.txt"), TestContext.Current.CancellationToken));
    }
}
