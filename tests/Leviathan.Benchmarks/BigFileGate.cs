using System.Diagnostics;
using System.Globalization;
using System.IO.Abstractions;
using Leviathan.Contracts;
using Leviathan.Engine.Execution;
using Leviathan.Engine.Journal;
using Leviathan.Engine.Scheduling;
using Leviathan.Engine.Transfers;

/// <summary>
/// A very large file through the real copy engine (spec 3.1, build plan G3: "100 GB file operations"). Writes a file of the given size
/// in a temporary folder (its end of file is set, so the empty stretches cost no disk reads), puts recognisable bytes every GiB and at
/// both ends, copies it into a second temporary folder with the default verify policy (a file over 4 GiB is checked by sampling), and
/// reports the time, the speed, the largest memory the process held (a copy must stream: memory stays flat whatever the size) and
/// whether the copy has the same length and the same bytes at every marker. Both folders are removed. Exit code 0 = correct.
/// </summary>
internal static class BigFileGate
{
    public static async Task<int> RunAsync(int gibibytes)
    {
        var root = Path.Combine(Path.GetTempPath(), "leviathan-bigfile-" + Guid.NewGuid().ToString("N"));
        var source = Path.Combine(root, "src");
        var destination = Path.Combine(root, "dst");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(destination);
        var length = gibibytes * 1024L * 1024 * 1024;
        var file = Path.Combine(source, "huge.bin");
        try
        {
            var offsets = Enumerable.Range(0, gibibytes).Select(i => i * 1024L * 1024 * 1024 + 12345).Append(length - 16).ToList();
            await using (var stream = new FileStream(file, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                stream.SetLength(length);
                foreach (var offset in offsets)
                {
                    stream.Position = offset;
                    await stream.WriteAsync(Marker(offset));
                }
            }

            var peak = 0L;
            using var sampler = new CancellationTokenSource();
            var sampling = Task.Run(async () =>
            {
                using var self = Process.GetCurrentProcess();
                while (!sampler.IsCancellationRequested)
                {
                    self.Refresh();
                    peak = Math.Max(peak, self.WorkingSet64);
                    await Task.Delay(200).ConfigureAwait(false);
                }
            });

            await using var host = new JobHost(new JobScheduler(), [new CopyMoveExecutor(new FileSystem())], new InMemoryJobJournal());
            host.Start();
            var clock = Stopwatch.StartNew();
            var job = host.Submit(new JobRequest(JobKind.Copy, [source], destination), JobLane.Operations, null, "copy");
            while (!host.Scheduler.Find(job.Id)!.IsFinished)
            {
                await Task.Delay(100);
            }

            clock.Stop();
            await sampler.CancelAsync();
            await sampling.ConfigureAwait(false);
            var done = host.Scheduler.Find(job.Id)!;
            var copy = Path.Combine(destination, "src", "huge.bin");
            var ok = done.State == JobState.Completed && File.Exists(copy) && new FileInfo(copy).Length == length;
            if (ok)
            {
                await using var read = new FileStream(copy, FileMode.Open, FileAccess.Read, FileShare.Read);
                var buffer = new byte[16];
                foreach (var offset in offsets)
                {
                    read.Position = offset;
                    await read.ReadExactlyAsync(buffer);
                    ok &= buffer.AsSpan().SequenceEqual(Marker(offset));
                }
            }

            var megabytes = length / 1_000_000.0 / clock.Elapsed.TotalSeconds;
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"{gibibytes} GiB file copied in {clock.Elapsed.TotalSeconds:0.0} s · {megabytes:0} MB/s · memory peak {peak / 1048576.0:0} MB · state {done.State} · {(ok ? "CORRECT" : "WRONG: " + done.Error)}"));
            return ok ? 0 : 1;
        }
        finally
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch (IOException)
            {
                // Best effort; it is in the temporary folder.
            }
        }
    }

    private static byte[] Marker(long offset) => BitConverter.GetBytes(offset).Concat(BitConverter.GetBytes(~offset)).ToArray();
}
