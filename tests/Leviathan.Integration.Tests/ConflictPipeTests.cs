using System.IO.Abstractions;
using Leviathan.Contracts;
using Leviathan.Engine.Execution;
using Leviathan.Engine.Journal;
using Leviathan.Engine.Scheduling;
using Leviathan.Engine.Transfers;
using Leviathan.Ipc;
using Leviathan.Worker;
using Microsoft.Extensions.Logging.Abstractions;

namespace Leviathan.Integration.Tests;

/// <summary>
/// The conflict question of a copy, end to end over a real named pipe and real files (spec §12.4): the worker lists the clashes in the
/// job's snapshot, the client answers with a rule and a per-file override, and the files end up as answered.
/// </summary>
public sealed class ConflictPipeTests : IAsyncDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "leviathan-conflict-" + Guid.NewGuid().ToString("N"));
    private readonly string _pipe = "Leviathan.Tests." + Guid.NewGuid().ToString("N");
    private readonly JobHost _host;
    private readonly PipeServer _server;

    public ConflictPipeTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "src"));
        Directory.CreateDirectory(Path.Combine(_root, "dst"));
        foreach (var name in new[] { "a.txt", "b.txt" })
        {
            File.WriteAllText(Path.Combine(_root, "src", name), "new-" + name);
            File.WriteAllText(Path.Combine(_root, "dst", name), "old-" + name);
        }

        _host = new JobHost(new JobScheduler(), [new CopyMoveExecutor(new FileSystem())], new InMemoryJobJournal());
        var hub = new WorkerHub(_host, NullLogger<WorkerHub>.Instance);
        var service = new WorkerService(_host);
        _server = new PipeServer(_pipe, connection =>
        {
            connection.Rpc.AddLocalRpcTarget<IWorkerService>(service, null);
            hub.Add(connection.Id, connection.Rpc.Attach<IWorkerCallbacks>());
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

    private sealed class NoCallbacks : IWorkerCallbacks
    {
        public Task OnJobChangedAsync(JobSnapshot snapshot) => Task.CompletedTask;

        public Task OnJobRemovedAsync(Guid jobId) => Task.CompletedTask;
    }

    [Fact]
    public async Task ACopyThatClashes_AsksOverThePipe_AndFollowsTheAnswerWithItsOverride()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Locations are Windows paths; runs on Windows CI.");
        await using var client = new WorkerClient(new NoCallbacks(), _pipe);
        await client.ConnectAsync(TimeSpan.FromSeconds(5), CancellationToken.None);
        var src = Path.Combine(_root, "src");
        var dst = Path.Combine(_root, "dst");

        var submitted = await client.Service.SubmitAsync(
            new JobRequest(JobKind.Copy, [Path.Combine(src, "a.txt"), Path.Combine(src, "b.txt")], dst) { Conflict = ConflictPolicy.Ask },
            CancellationToken.None);

        JobConflicts? question = null;
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline && question is null)
        {
            question = (await client.Service.GetJobsAsync(CancellationToken.None)).Single(j => j.Id == submitted.Id).Conflicts;
            await Task.Delay(20);
        }

        Assert.NotNull(question);
        Assert.Equal(2, question!.TotalCount);
        Assert.All(question.Items, i => Assert.Equal(9, i.SourceSize));
        var overrideTarget = question.Items.Single(i => i.Target.EndsWith("b.txt", StringComparison.Ordinal)).Target;

        await client.Service.AnswerConflictsAsync(
            submitted.Id,
            question.Id,
            new ConflictAnswer(ConflictPolicy.Replace, new Dictionary<string, ConflictPolicy> { [overrideTarget] = ConflictPolicy.KeepBoth }),
            CancellationToken.None);

        JobSnapshot? final = null;
        while (DateTime.UtcNow < deadline)
        {
            final = (await client.Service.GetJobsAsync(CancellationToken.None)).Single(j => j.Id == submitted.Id);
            if (final.State is JobState.Completed or JobState.Failed)
            {
                break;
            }

            await Task.Delay(20);
        }

        Assert.Equal(JobState.Completed, final!.State);
        Assert.Equal("new-a.txt", File.ReadAllText(Path.Combine(dst, "a.txt")));
        Assert.Equal("old-b.txt", File.ReadAllText(Path.Combine(dst, "b.txt")));
        Assert.Equal("new-b.txt", File.ReadAllText(Path.Combine(dst, "b (2).txt")));
    }
}
