using System.IO.Abstractions;
using Leviathan.Contracts;
using Leviathan.Engine.Execution;
using Leviathan.Engine.Journal;
using Leviathan.Engine.Scheduling;
using Leviathan.Engine.Transfers;
using Leviathan.Worker;

namespace Leviathan.Integration.Tests;

/// <summary>
/// The worker's side of the Soft-delete bin (spec §12.5) on a real temp folder: the bin listing, the automatic purge going through
/// validation without items of its own, and its lane. Never touches a share.
/// </summary>
public sealed class WorkerBinTests : IAsyncDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "leviathan-bin-" + Guid.NewGuid().ToString("N"));
    private readonly FileSystem _fs = new();
    private readonly BinRegistry _registry;
    private readonly JobHost _host;
    private readonly WorkerService _service;

    public WorkerBinTests()
    {
        Directory.CreateDirectory(_root);
        _registry = new BinRegistry(_fs, Path.Combine(_root, "bin-roots.json"));
        _host = new JobHost(new JobScheduler(), [new PurgeExecutor(_fs, _registry)], new InMemoryJobJournal());
        _service = new WorkerService(_host, bin: new BinScanner(_registry));
        _host.Start();
    }

    public async ValueTask DisposeAsync()
    {
        await _host.DisposeAsync();
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // Temp folder; the next run's cleaner gets it.
        }

        GC.SuppressFinalize(this);
    }

    // A local folder stands in for a share: the trash folder layout is the same.
    private string Trash(string name, string content, DateTimeOffset at)
    {
        var trashRoot = Path.Combine(_root, ".leviathan-trash");
        var batch = Path.Combine(trashRoot, "20261101-100000-aaaaaa");
        Directory.CreateDirectory(batch);
        var trashed = Path.Combine(batch, name);
        File.WriteAllText(trashed, content);
        var line = System.Text.Json.JsonSerializer.Serialize(new { original = @"C:\media\" + name, trashed, at }) + "\n";
        File.AppendAllText(Path.Combine(trashRoot, "manifest.jsonl"), line);
        _registry.Add(Core.Paths.LocationPath.Parse(trashRoot));
        return trashed;
    }

    [Fact]
    public async Task GetBin_ListsWhatWasSoftDeleted_WithItsOriginalPlaceAndSize()
    {
        var trashed = Trash("a.mkv", "12345", DateTimeOffset.UtcNow.AddDays(-2));

        var listing = await _service.GetBinAsync(measureFolders: false, CancellationToken.None);

        var item = Assert.Single(listing.Items);
        Assert.Equal(trashed, item.Trashed);
        Assert.Equal(@"C:\media\a.mkv", item.Original);
        Assert.Equal(5, item.SizeBytes);
        Assert.False(item.IsFolder);
        Assert.Empty(listing.Unreachable);
    }

    [Fact]
    public async Task GetBin_WithoutABinReader_IsEmpty_NotAnError()
    {
        var listing = await new WorkerService(_host).GetBinAsync(false, CancellationToken.None);

        Assert.Empty(listing.Items);
    }

    [Fact]
    public async Task TheAutomaticPurge_NeedsNoItems_RunsInTheBackgroundLane_AndDeletesWhatIsOld()
    {
        var old = Trash("old.mkv", "x", DateTimeOffset.UtcNow.AddDays(-40));
        var fresh = Trash("fresh.mkv", "y", DateTimeOffset.UtcNow.AddDays(-1));

        var snapshot = await _service.SubmitAsync(new JobRequest(JobKind.Purge, [], null) { PurgePolicy = new PurgePolicy(30, 0) }, CancellationToken.None);
        await FinishedAsync(snapshot.Id);

        Assert.Equal(JobLane.Background, snapshot.Lane);
        Assert.False(File.Exists(old));
        Assert.True(File.Exists(fresh));
    }

    [Fact]
    public async Task PurgingPickedItems_IsAUserOperation_AndAnEmptyPurgeWithoutARuleIsRefused()
    {
        var picked = Trash("a.mkv", "x", DateTimeOffset.UtcNow);

        var snapshot = await _service.SubmitAsync(new JobRequest(JobKind.Purge, [picked], null), CancellationToken.None);
        await FinishedAsync(snapshot.Id);

        Assert.Equal(JobLane.Operations, snapshot.Lane);
        Assert.False(File.Exists(picked));
        await Assert.ThrowsAsync<ArgumentException>(() => _service.SubmitAsync(new JobRequest(JobKind.Purge, [], null), CancellationToken.None));
    }

    private async Task FinishedAsync(Guid id)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!_host.Scheduler.Find(id)!.IsFinished)
        {
            Assert.True(DateTime.UtcNow < deadline, "the purge did not finish");
            await Task.Delay(10);
        }
    }
}
