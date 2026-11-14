using System.IO.Abstractions;
using Leviathan.Contracts;
using Leviathan.Core.Hashing;
using Leviathan.Core.Paths;
using Leviathan.Engine.Execution;

namespace Leviathan.Engine.Checksums;

/// <summary>
/// Runs <see cref="JobKind.Checksum"/> (spec §15.4) in the worker, in the background lane, so hashing a folder of 100 GB files on a
/// share never touches the window's thread and survives it closing. Every file is read once, whatever number of sums is asked for.
/// Two modes: <em>compute</em> (the sources are files and folders; each file comes back with its sums as <c>kind=hex;kind=hex</c>) and
/// <em>verify</em> (the sources are checksum files; every file they name is read and compared, and a file that does not match or is
/// missing comes back failed with a reason starting <see cref="Mismatch"/> or <see cref="Missing"/>). It reads files and changes nothing.
/// </summary>
public sealed class ChecksumExecutor(IFileSystem fileSystem) : IJobExecutor
{
    /// <summary>See <see cref="ChecksumResults.Mismatch"/>.</summary>
    public const string Mismatch = ChecksumResults.Mismatch;

    /// <summary>See <see cref="ChecksumResults.Missing"/>.</summary>
    public const string Missing = ChecksumResults.Missing;

    /// <inheritdoc />
    public bool CanExecute(JobKind kind) => kind == JobKind.Checksum;

    /// <summary>See <see cref="ChecksumResults.Encode"/>.</summary>
    public static string Encode(IReadOnlyDictionary<ChecksumKind, string> sums) => ChecksumResults.Encode(sums);

    /// <summary>See <see cref="ChecksumResults.Decode"/>.</summary>
    public static IReadOnlyDictionary<ChecksumKind, string> Decode(string? result) => ChecksumResults.Decode(result);

    /// <inheritdoc />
    public async Task<JobOutcome> ExecuteAsync(JobContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var request = context.Job.Request;
        var work = request.ChecksumVerify ? ReadChecksumFiles(context, request) : ListFiles(context, request, cancellationToken);
        var total = work.Sum(w => w.Size);
        var done = 0L;
        var count = 0;
        var matched = 0;
        var wrong = 0;
        var absent = 0;
        context.Report(0, total, 0, work.Count);
        foreach (var item in work)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!fileSystem.File.Exists(item.Path))
            {
                absent++;
                context.AddResult(new ItemOutcome(item.Path, null, ItemStatus.Failed, $"{Missing}: the file is not there."));
                context.Report(done, total, ++count, work.Count, item.Path);
                continue;
            }

            try
            {
                var sums = await HashAsync(item.Path, item.Kinds, done, p => context.Report(p, total, count, work.Count, item.Path), cancellationToken).ConfigureAwait(false);
                done += item.Size;
                var encoded = Encode(sums);
                if (item.Expected is { } expected)
                {
                    var actual = sums[expected.Kind];
                    if (string.Equals(actual, expected.Hash, StringComparison.OrdinalIgnoreCase))
                    {
                        matched++;
                        context.AddResult(new ItemOutcome(item.Path, encoded, ItemStatus.Done));
                    }
                    else
                    {
                        wrong++;
                        context.AddResult(new ItemOutcome(item.Path, encoded, ItemStatus.Failed, $"{Mismatch}: expected {expected.Hash}, got {actual}."));
                    }
                }
                else
                {
                    context.AddResult(new ItemOutcome(item.Path, encoded, ItemStatus.Done));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                done += item.Size;
                context.AddResult(new ItemOutcome(item.Path, null, ItemStatus.Failed, ex.Message));
            }

            context.Report(done, total, ++count, work.Count, item.Path);
        }

        if (request.ChecksumVerify)
        {
            context.AddNote($"{matched:N0} files match, {wrong:N0} do not, {absent:N0} are missing.");
        }

        return JobOutcome.Completed;
    }

    private sealed record Item(string Path, long Size, IReadOnlyCollection<ChecksumKind> Kinds, ChecksumEntry? Expected);

    private async Task<IReadOnlyDictionary<ChecksumKind, string>> HashAsync(string path, IReadOnlyCollection<ChecksumKind> kinds, long before, Action<long> report, CancellationToken cancellationToken)
    {
        using var stream = fileSystem.FileStream.New(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 20, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return await FileChecksums.ComputeAsync(stream, kinds, new Inline(read => report(before + read)), cancellationToken).ConfigureAwait(false);
    }

    private List<Item> ListFiles(JobContext context, JobRequest request, CancellationToken cancellationToken)
    {
        var kinds = (request.ChecksumKinds ?? []).Select(k => Enum.Parse<ChecksumKind>(k)).Distinct().ToList();
        var items = new List<Item>();
        foreach (var source in request.Sources)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var location = LocationPath.Parse(source);
            if (location.Kind is not (LocationKind.Drive or LocationKind.Unc))
            {
                context.AddResult(new ItemOutcome(source, null, ItemStatus.Skipped, "Only files on this PC and on Windows shares can be checked."));
                continue;
            }

            var path = location.ToWin32Path();
            if (fileSystem.File.Exists(path))
            {
                items.Add(new Item(path, fileSystem.FileInfo.New(path).Length, kinds, null));
            }
            else if (fileSystem.Directory.Exists(path))
            {
                try
                {
                    foreach (var file in fileSystem.Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories).Order(StringComparer.OrdinalIgnoreCase))
                    {
                        items.Add(new Item(file, fileSystem.FileInfo.New(file).Length, kinds, null));
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    context.AddResult(new ItemOutcome(source, null, ItemStatus.Failed, ex.Message));
                }
            }
            else
            {
                context.AddResult(new ItemOutcome(source, null, ItemStatus.Failed, "It is not there any more."));
            }
        }

        return items;
    }

    // The files a checksum file names, each with its expected sum, found beside the checksum file.
    private List<Item> ReadChecksumFiles(JobContext context, JobRequest request)
    {
        var items = new List<Item>();
        foreach (var source in request.Sources)
        {
            var location = LocationPath.Parse(source);
            if (location.Kind is not (LocationKind.Drive or LocationKind.Unc) || !fileSystem.File.Exists(location.ToWin32Path()))
            {
                context.AddResult(new ItemOutcome(source, null, ItemStatus.Failed, "The checksum file is not there."));
                continue;
            }

            var manifestPath = location.ToWin32Path();
            string text;
            try
            {
                text = fileSystem.File.ReadAllText(manifestPath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                context.AddResult(new ItemOutcome(source, null, ItemStatus.Failed, ex.Message));
                continue;
            }

            var contents = ChecksumManifest.Parse(text, fileSystem.Path.GetFileName(manifestPath));
            foreach (var line in contents.Skipped)
            {
                context.AddNote($"{location.Name}: not a checksum line: {line}");
            }

            var folder = fileSystem.Path.GetDirectoryName(manifestPath) ?? string.Empty;
            foreach (var entry in contents.Entries)
            {
                var path = fileSystem.Path.GetFullPath(fileSystem.Path.Combine(folder, entry.Name.Replace('/', fileSystem.Path.DirectorySeparatorChar)));
                var size = fileSystem.File.Exists(path) ? fileSystem.FileInfo.New(path).Length : 0;
                items.Add(new Item(path, size, [entry.Kind], entry));
            }

            if (contents.Entries.Count == 0)
            {
                context.AddResult(new ItemOutcome(source, null, ItemStatus.Failed, "The checksum file holds no sums that could be read."));
            }
        }

        return items;
    }

    private sealed class Inline(Action<long> report) : IProgress<long>
    {
        public void Report(long value) => report(value);
    }
}
