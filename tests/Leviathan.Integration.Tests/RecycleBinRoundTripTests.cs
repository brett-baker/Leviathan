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
    public async Task ManyFilesAndAFolder_GoToTheRealBinInBatches_EachWithItsPlace_AndAllComeBack()
    {
        // 250 files take two shell batches. Each one's place in the bin comes from one reading of the bin's records.
        var files = Enumerable.Range(0, 250).Select(i => Path.Combine(_root, $"many {i:D3}.txt")).ToList();
        Directory.CreateDirectory(_root);
        foreach (var file in files)
        {
            await File.WriteAllTextAsync(file, "content of " + Path.GetFileName(file), TestContext.Current.CancellationToken);
        }

        var folder = Path.Combine(_root, "many album");
        Directory.CreateDirectory(Path.Combine(folder, "cd1"));
        await File.WriteAllTextAsync(Path.Combine(folder, "cd1", "track.txt"), "la", TestContext.Current.CancellationToken);
        var everything = files.Append(folder).ToList();
        var fs = new FileSystem();
        await using var host = new JobHost(new JobScheduler(), [new DeleteExecutor(fs, new WindowsRecycleBin()), new RestoreExecutor(fs)], new InMemoryJobJournal());
        host.Start();

        var deleted = await RunAsync(host, new JobRequest(JobKind.Delete, everything, null));

        Assert.All(everything, path => Assert.False(File.Exists(path) || Directory.Exists(path), path));
        Assert.Equal(everything, deleted.Items.Select(i => i.Source));
        Assert.All(deleted.Items, i => Assert.Equal(ItemStatus.Done, i.Status));
        if (deleted.Items.Any(i => i.Result is null))
        {
            Assert.Skip("The Recycle Bin is turned off for the temp drive, so there is nothing to find.");
        }

        var wheres = deleted.Items.Select(i => i.Result!).ToList();
        Assert.Equal(wheres.Count, wheres.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.All(wheres, where => Assert.True(File.Exists(where) || Directory.Exists(where), where));

        // Put everything back, so the real Recycle Bin of whoever runs the test is left as it was.
        var restored = await RunAsync(host, new JobRequest(JobKind.Restore, wheres, null) { Targets = everything });

        Assert.All(restored.Items, i => Assert.Equal(ItemStatus.Done, i.Status));
        Assert.Equal("content of many 007.txt", await File.ReadAllTextAsync(files[7], TestContext.Current.CancellationToken));
        Assert.Equal("la", await File.ReadAllTextAsync(Path.Combine(folder, "cd1", "track.txt"), TestContext.Current.CancellationToken));
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
