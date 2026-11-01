using System.IO.Abstractions.TestingHelpers;
using Leviathan.Contracts;
using Leviathan.Engine.Journal;

namespace Leviathan.Engine.Tests;

public class JournalTests
{
    private static string TempJournal(MockFileSystem fs) =>
        fs.Path.Combine(fs.Path.GetTempPath(), "leviathan-tests", Guid.NewGuid().ToString("N"), "jobs.journal");

    private static JournalRecord Enqueued(Guid id, JobKind kind = JobKind.Copy) => new()
    {
        Type = JournalRecordType.Enqueued,
        JobId = id,
        At = DateTimeOffset.UtcNow,
        Request = new JobRequest(kind, [@"D:\a", @"D:\b"], @"\\nas\media") { Priority = JobPriority.High },
        Lane = JobLane.Operations,
        DeviceKey = @"\\nas",
        Title = "Copy 2 items",
    };

    [Fact]
    public void FileJournal_RoundTripsRecords()
    {
        var fs = new MockFileSystem();
        var journal = new FileJobJournal(fs, TempJournal(fs));
        var id = Guid.NewGuid();

        journal.Append(Enqueued(id));
        journal.Append(new JournalRecord { Type = JournalRecordType.StateChanged, JobId = id, At = DateTimeOffset.UtcNow, State = JobState.Running });

        var records = journal.ReadAll();
        Assert.Equal(2, records.Count);
        Assert.Equal(new[] { @"D:\a", @"D:\b" }, records[0].Request!.Sources);
        Assert.Equal(JobPriority.High, records[0].Request!.Priority);
        Assert.Equal(JobState.Running, records[1].State);
    }

    [Fact]
    public void FileJournal_SkipsTornLastLine()
    {
        var fs = new MockFileSystem();
        var path = TempJournal(fs);
        var journal = new FileJobJournal(fs, path);
        journal.Append(Enqueued(Guid.NewGuid()));

        // Simulate a crash in the middle of writing the next line.
        fs.File.AppendAllText(path, "{\"Type\":\"StateCha");

        Assert.Single(journal.ReadAll());
    }

    [Fact]
    public void FileJournal_Compact_ReplacesContent()
    {
        var fs = new MockFileSystem();
        var journal = new FileJobJournal(fs, TempJournal(fs));
        var keep = Guid.NewGuid();
        journal.Append(Enqueued(Guid.NewGuid()));
        journal.Append(Enqueued(keep));

        journal.Compact([Enqueued(keep)]);

        Assert.Equal(keep, Assert.Single(journal.ReadAll()).JobId);
    }

    [Fact]
    public void Replay_ReturnsOnlyUnfinished_AndRequeuesInterrupted()
    {
        var running = Guid.NewGuid();
        var waiting = Guid.NewGuid();
        var paused = Guid.NewGuid();
        var completed = Guid.NewGuid();
        var removed = Guid.NewGuid();
        var at = DateTimeOffset.UtcNow;
        JournalRecord State(Guid id, JobState s) => new() { Type = JournalRecordType.StateChanged, JobId = id, At = at, State = s };

        var recovered = JournalReplay.Recover(
        [
            Enqueued(running), State(running, JobState.Running),
            Enqueued(waiting), State(waiting, JobState.Waiting),
            Enqueued(paused), State(paused, JobState.Paused),
            Enqueued(completed), State(completed, JobState.Running), State(completed, JobState.Completed),
            Enqueued(removed), new JournalRecord { Type = JournalRecordType.Removed, JobId = removed, At = at },
            State(Guid.NewGuid(), JobState.Running), // unknown job: ignored
        ]);

        Assert.Equal(new[] { running, waiting, paused }, recovered.Select(r => r.JobId));
        Assert.Equal(JobState.Queued, recovered[0].LastState);
        Assert.Equal(JobState.Queued, recovered[1].LastState);
        Assert.Equal(JobState.Paused, recovered[2].LastState);
    }
}
