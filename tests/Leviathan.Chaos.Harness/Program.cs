using System.IO.Abstractions;
using Leviathan.Contracts;
using Leviathan.Engine.Execution;
using Leviathan.Engine.Journal;
using Leviathan.Engine.Scheduling;
using Leviathan.Engine.Transfers;

// Usage: harness <stateFolder> [--submit <source> <destination> <bytesPerSecond>]
// Starts the job host over the state folder (which recovers unfinished jobs from the journal), optionally queues one copy, and exits
// 0 when every job has completed. The chaos tests kill this process at random and start it again.
if (args.Length < 1)
{
    return 64;
}

var state = args[0];
Directory.CreateDirectory(state);
var fs = new FileSystem();
var executor = new CopyMoveExecutor(
    fs,
    planStore: new FileTransferPlanStore(fs, Path.Combine(state, "plans")),
    journal: new FileTransferJournal(fs, Path.Combine(state, "progress")),
    bandwidth: new BandwidthController());
var host = new JobHost(new JobScheduler(), [executor], new FileJobJournal(fs, Path.Combine(state, "jobs.journal")));
host.Start();

if (args.Length >= 5 && args[1] == "--submit")
{
    host.Submit(
        new JobRequest(JobKind.Copy, [args[2]], args[3]) { Verify = VerifyMode.Full, BytesPerSecondLimit = long.Parse(args[4], System.Globalization.CultureInfo.InvariantCulture) },
        JobLane.Operations,
        null,
        "chaos copy");

    // The journal line is written before Submit returns; from here a crash must not lose the job.
    Console.WriteLine("SUBMITTED");
    Console.Out.Flush();
}

while (true)
{
    var jobs = host.Scheduler.Snapshot();

    // Nothing recovered and nothing submitted: the earlier run finished everything.
    if (jobs.Count == 0 || jobs.All(j => j.IsFinished))
    {
        Console.WriteLine(string.Join(",", jobs.Select(j => j.State)));
        var ok = jobs.All(j => j.State == JobState.Completed);
        await host.DisposeAsync().ConfigureAwait(false);
        return ok ? 0 : 2;
    }

    await Task.Delay(50).ConfigureAwait(false);
}
