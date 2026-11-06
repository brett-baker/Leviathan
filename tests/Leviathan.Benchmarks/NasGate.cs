using System.Diagnostics;
using System.Globalization;
using Leviathan.Contracts;
using Leviathan.Core.Paths;
using Leviathan.Core.Providers;
using Leviathan.Engine.Execution;
using Leviathan.Engine.Journal;
using Leviathan.Engine.Scheduling;
using Leviathan.Engine.Transfers;
using Leviathan.Providers.Local;

/// <summary>
/// The two NAS budgets of spec §3.1, measured by hand against a share you name (never a default, never run by <c>--gate</c>):
/// <c>--nas-rows &lt;folder&gt;</c> lists a folder and reports the first rows (budget 400 ms for 10,000 items), and
/// <c>--nas-copy &lt;folder&gt; [MiB] [--verify]</c> copies one large file into a new sub folder of it, through the same job host, executor and
/// Windows copier the worker uses, and reports MB/s (budget 105 MB/s on 1 GbE). The copy is timed without the read-back check by default, because the budget is about moving bytes; <c>--verify</c> includes the check the job applies to network copies, which reads the file back and costs a lot on a NAS. Listing only reads. The copy writes one temporary file
/// of random data to the local temp folder and one sub folder it creates (and deletes afterwards) on the target; nothing else is touched.
/// </summary>
internal static class NasGate
{
    private const double RowsBudgetMs = 400;
    private const double CopyBudgetMBps = 105;

    /// <summary>First rows of a listing of <paramref name="folder"/>: the first (cold) run, then the median and p95 of repeated runs.</summary>
    public static int Rows(string folder)
    {
        if (!Directory.Exists(folder))
        {
            Console.Error.WriteLine($"'{folder}' is not a folder this machine can reach.");
            return 2;
        }

        var provider = new LocalLocationProvider();
        var location = LocationPath.Parse(folder);
        var first = new List<double>();
        var count = 0;
        for (var run = 0; run < 15; run++)
        {
            var clock = Stopwatch.StartNew();
            var firstRows = -1.0;
            count = 0;
            foreach (var batch in provider.ListAsync(location, ListingOptions.Default, CancellationToken.None).ToBlockingEnumerable())
            {
                if (firstRows < 0)
                {
                    firstRows = clock.Elapsed.TotalMilliseconds;
                }

                count += batch.Items.Count;
            }

            first.Add(firstRows);
        }

        var cold = first[0];
        var warm = first.Skip(1).Order().ToList();
        var p95 = warm[Math.Clamp((int)Math.Ceiling(0.95 * warm.Count) - 1, 0, warm.Count - 1)];
        var pass = p95 <= RowsBudgetMs;
        var note = count < 10_000 ? $" · only {count:N0} items here, the budget is for 10,000" : string.Empty;
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{folder}: {count:N0} items · first rows: cold {cold:0} ms, p95 of the next 14 runs {p95:0} ms (budget {RowsBudgetMs:0} ms){note} · {(pass ? "PASS" : "FAIL")}"));
        return pass ? 0 : 1;
    }

#if WINDOWS_TFM
    /// <summary>Copies one <paramref name="mebibytes"/> MiB file into a new sub folder of <paramref name="target"/> and reports the throughput.</summary>
    public static async Task<int> CopyAsync(string target, long mebibytes, bool verify)
    {
        if (!Directory.Exists(target))
        {
            Console.Error.WriteLine($"'{target}' is not a folder this machine can reach.");
            return 2;
        }

        var source = Path.Combine(Path.GetTempPath(), "leviathan-nas-bench-" + Guid.NewGuid().ToString("N") + ".bin");
        var bytes = mebibytes * 1024 * 1024;
        var temp = new DriveInfo(Path.GetPathRoot(source)!);
        if (temp.AvailableFreeSpace < bytes + (1L << 30))
        {
            Console.Error.WriteLine("Not enough free space in the temp folder for the test file.");
            return 2;
        }

        var made = Path.Combine(target, "leviathan-bench-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            Console.WriteLine($"Writing {mebibytes:N0} MiB of random data to {source} ...");
            WriteRandomFile(source, bytes);
            Directory.CreateDirectory(made);

            var fs = new System.IO.Abstractions.FileSystem();
            await using var host = new JobHost(new JobScheduler(), [new CopyMoveExecutor(fs, copier: new Leviathan.Interop.WindowsFileCopier())], new InMemoryJobJournal());
            host.Start();
            var clock = Stopwatch.StartNew();
            var job = host.Submit(new JobRequest(JobKind.Copy, [source], made) { Verify = verify ? VerifyMode.Default : VerifyMode.Off }, JobLane.Operations, null, "NAS throughput");
            while (!host.Scheduler.Find(job.Id)!.IsFinished)
            {
                await Task.Delay(100);
            }

            clock.Stop();
            var state = host.Scheduler.Find(job.Id)!.State;
            if (state != JobState.Completed)
            {
                Console.Error.WriteLine($"The copy ended as {state}: {host.Snapshot(host.Scheduler.Find(job.Id)!).Error}");
                return 1;
            }

            var rate = bytes / 1_000_000.0 / clock.Elapsed.TotalSeconds;
            var pass = rate >= CopyBudgetMBps;
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{mebibytes:N0} MiB to {target} in {clock.Elapsed.TotalSeconds:0.0} s ({(verify ? "with the read-back check" : "no read-back check")}) · {rate:0} MB/s (budget {CopyBudgetMBps:0} MB/s) · {(pass ? "PASS" : "FAIL")}"));
            return pass ? 0 : 1;
        }
        finally
        {
            DeleteQuietly(source, file: true);
            DeleteQuietly(made, file: false);
        }
    }

    private static void WriteRandomFile(string path, long bytes)
    {
        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20);
        var chunk = new byte[8 << 20];
        new Random(1).NextBytes(chunk);
        for (long written = 0; written < bytes; written += chunk.Length)
        {
            stream.Write(chunk, 0, (int)Math.Min(chunk.Length, bytes - written));
        }
    }

    private static void DeleteQuietly(string path, bool file)
    {
        try
        {
            if (file)
            {
                File.Delete(path);
            }
            else if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (IOException)
        {
            Console.Error.WriteLine($"Could not remove {path}; delete it by hand.");
        }
    }
#endif
}
