using System.Collections.Concurrent;
using Leviathan.Contracts;
using Leviathan.Core.Providers;
using Leviathan.Engine.Execution;
using Leviathan.Engine.FolderSizes;
using Leviathan.Engine.Journal;
using Leviathan.Engine.Scheduling;
using Leviathan.Ipc;
using Leviathan.Providers.Local;
using Leviathan.Worker;
using Microsoft.Extensions.Logging.Abstractions;

namespace Leviathan.Integration.Tests;

/// <summary>
/// End to end over a real named pipe: client → JSON-RPC → WorkerService → JobHost → folder-size executor → local
/// provider on a temp folder, with progress pushed back to the client.
/// </summary>
public sealed class WorkerPipeTests : IAsyncDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "leviathan-int-" + Guid.NewGuid().ToString("N"));
    private readonly string _pipe = "Leviathan.Tests." + Guid.NewGuid().ToString("N");
    private readonly JobHost _host;
    private readonly PipeServer _server;
    private readonly WorkerHub _hub;

    public WorkerPipeTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "sub"));
        File.WriteAllBytes(Path.Combine(_root, "a.bin"), new byte[1000]);
        File.WriteAllBytes(Path.Combine(_root, "sub", "b.bin"), new byte[2000]);

        var registry = new LocationProviderRegistry([new LocalLocationProvider()]);
        var sizes = new FolderSizeService(registry, new InMemoryFolderSizeStore());
        _host = new JobHost(new JobScheduler(), [new ComputeFolderSizeExecutor(sizes)], new InMemoryJobJournal());
        _hub = new WorkerHub(_host, NullLogger<WorkerHub>.Instance);
        var service = new WorkerService(_host);
        _server = new PipeServer(_pipe, connection =>
        {
            connection.Rpc.AddLocalRpcTarget<IWorkerService>(service, null);
            _hub.Add(connection.Id, connection.Rpc.Attach<IWorkerCallbacks>());
        });
        _host.Start();
        _server.Start();
    }

    public async ValueTask DisposeAsync()
    {
        await _server.DisposeAsync();
        await _host.DisposeAsync();
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // Best effort.
        }
    }

    private sealed class RecordingCallbacks : IWorkerCallbacks
    {
        public ConcurrentQueue<JobSnapshot> Changes { get; } = new();

        public Task OnJobChangedAsync(JobSnapshot snapshot)
        {
            Changes.Enqueue(snapshot);
            return Task.CompletedTask;
        }

        public Task OnJobRemovedAsync(Guid jobId) => Task.CompletedTask;
    }

    [Fact]
    public async Task Client_ComputesFolderSize_ThroughWorker_AndReceivesProgress()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Locations are Windows paths; runs on Windows CI.");
        var callbacks = new RecordingCallbacks();
        await using var client = new WorkerClient(callbacks, _pipe);

        await client.ConnectAsync(TimeSpan.FromSeconds(5), CancellationToken.None);
        Assert.Equal(ContractVersion.Major, client.Info!.ContractMajor);

        var submitted = await client.Service.SubmitAsync(new JobRequest(JobKind.ComputeFolderSize, [_root], null), CancellationToken.None);
        Assert.Equal(JobLane.Background, submitted.Lane);

        var deadline = DateTime.UtcNow.AddSeconds(10);
        JobSnapshot? final = null;
        while (DateTime.UtcNow < deadline)
        {
            final = (await client.Service.GetJobsAsync(CancellationToken.None)).Single(j => j.Id == submitted.Id);
            if (final.State is JobState.Completed or JobState.Failed)
            {
                break;
            }

            await Task.Delay(50);
        }

        Assert.NotNull(final);
        Assert.Equal(JobState.Completed, final!.State);
        Assert.Equal(3000, final.BytesDone);
        Assert.Equal(2, final.ItemsDone);
        Assert.Contains(callbacks.Changes, c => c.Id == submitted.Id);
    }

    [Fact]
    public async Task Client_SubmitsACopy_WorkerRefusesBadRequests_AndKnowsNoExecutorYet()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Locations are Windows paths; runs on Windows CI.");
        await using var client = new WorkerClient(new RecordingCallbacks(), _pipe);
        await client.ConnectAsync(TimeSpan.FromSeconds(5), CancellationToken.None);

        // A copy with no destination never reaches the queue.
        await Assert.ThrowsAnyAsync<Exception>(() => client.Service.SubmitAsync(new JobRequest(JobKind.Copy, [_root], null), CancellationToken.None));

        // A valid one is queued with the new fields intact; this worker has no copy executor yet (slice E2), so it fails with a message.
        var submitted = await client.Service.SubmitAsync(
            new JobRequest(JobKind.Copy, [Path.Combine(_root, "a.bin")], Path.Combine(_root, "sub")) { Conflict = ConflictPolicy.Skip },
            CancellationToken.None);
        Assert.Equal(JobLane.Operations, submitted.Lane);

        var deadline = DateTime.UtcNow.AddSeconds(5);
        JobSnapshot? job = null;
        while (DateTime.UtcNow < deadline)
        {
            job = (await client.Service.GetJobsAsync(CancellationToken.None)).Single(j => j.Id == submitted.Id);
            if (job.State == JobState.Failed)
            {
                break;
            }

            await Task.Delay(20);
        }

        Assert.Equal(JobState.Failed, job!.State);
        var result = await client.Service.GetResultAsync(submitted.Id, CancellationToken.None);
        Assert.NotNull(result);
        Assert.Empty(result!.Items);
        Assert.Null(await client.Service.GetResultAsync(Guid.NewGuid(), CancellationToken.None));
    }

    [Fact]
    public async Task Client_ConnectTimesOut_WhenNoWorker()
    {
        await using var client = new WorkerClient(new RecordingCallbacks(), "Leviathan.Tests.NoSuchPipe." + Guid.NewGuid().ToString("N"));

        await Assert.ThrowsAsync<TimeoutException>(() => client.ConnectAsync(TimeSpan.FromMilliseconds(300), CancellationToken.None));
    }

    [Fact]
    public async Task SeveralClients_AreServedIndependently()
    {
        await using var first = new WorkerClient(new RecordingCallbacks(), _pipe);
        await using var second = new WorkerClient(new RecordingCallbacks(), _pipe);

        await first.ConnectAsync(TimeSpan.FromSeconds(5), CancellationToken.None);
        await second.ConnectAsync(TimeSpan.FromSeconds(5), CancellationToken.None);

        await first.Service.SetLimitsAsync(new ConcurrencyLimits(5, 2, 1), CancellationToken.None);
        var limits = await second.Service.GetLimitsAsync(CancellationToken.None);

        Assert.Equal(5, limits.MaxRunningJobs);
        Assert.Equal(2, _server.ConnectionCount);
    }
}
