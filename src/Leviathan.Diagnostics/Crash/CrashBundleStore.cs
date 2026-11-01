using System.IO.Abstractions;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Leviathan.Diagnostics.Crash;

/// <summary>A bundle found on disk.</summary>
/// <param name="Folder">Bundle folder.</param>
/// <param name="Report">Its report.</param>
public sealed record CrashBundle(string Folder, CrashReport Report);

/// <summary>Crashes grouped by signature, for the Diagnostics view and <c>leviathan-diag list</c>.</summary>
/// <param name="Signature">Signature.</param>
/// <param name="Count">Number of bundles.</param>
/// <param name="FirstSeen">Oldest bundle time.</param>
/// <param name="LastSeen">Newest bundle time.</param>
/// <param name="Latest">Most recent bundle.</param>
public sealed record CrashGroup(string Signature, int Count, DateTimeOffset FirstSeen, DateTimeOffset LastSeen, CrashBundle Latest);

/// <summary>
/// Writes and reads crash bundles: one folder per incident under
/// <c>%LOCALAPPDATA%\Leviathan\crashes</c> with report.md, report.json, events.jsonl, actions.json and environment.json
/// (spec §20.3).
/// </summary>
public sealed class CrashBundleStore
{
    /// <summary>File names inside a bundle.</summary>
    public static class Files
    {
        /// <summary>Summary written for Claude.</summary>
        public const string ReportMarkdown = "report.md";

        /// <summary>Machine-readable report.</summary>
        public const string ReportJson = "report.json";

        /// <summary>Flight-recorder events.</summary>
        public const string Events = "events.jsonl";

        /// <summary>Replayable action trail.</summary>
        public const string Actions = "actions.json";

        /// <summary>Environment facts.</summary>
        public const string Environment = "environment.json";

        /// <summary>Minidump, when captured.</summary>
        public const string Minidump = "minidump.dmp";
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly IFileSystem _fileSystem;

    /// <summary>Creates a store rooted at <paramref name="root"/>.</summary>
    public CrashBundleStore(IFileSystem fileSystem, string root)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        _fileSystem = fileSystem;
        Root = root;
    }

    /// <summary>Default location: %LOCALAPPDATA%\Leviathan\crashes.</summary>
    public static string DefaultRoot =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Leviathan", "crashes");

    /// <summary>Folder that holds the bundles.</summary>
    public string Root { get; }

    /// <summary>Writes a bundle and returns its folder. Never throws: a failure to write a crash report must not mask the crash.</summary>
    public string? TryWrite(CrashReport report)
    {
        try
        {
            return Write(report);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    /// <summary>Writes a bundle and returns its folder.</summary>
    public string Write(CrashReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        var folder = _fileSystem.Path.Combine(Root, report.Id);
        _fileSystem.Directory.CreateDirectory(folder);

        var previous = ReadAll().Where(b => b.Report.Signature == report.Signature).ToList();
        var firstSeen = previous.Count == 0 ? report.Timestamp : previous.Min(b => b.Report.Timestamp);

        _fileSystem.File.WriteAllText(_fileSystem.Path.Combine(folder, Files.ReportJson), JsonSerializer.Serialize(report, JsonOptions));
        _fileSystem.File.WriteAllText(_fileSystem.Path.Combine(folder, Files.ReportMarkdown), CrashReportMarkdown.Render(report, previous.Count + 1, firstSeen));
        _fileSystem.File.WriteAllText(
            _fileSystem.Path.Combine(folder, Files.Events),
            string.Concat(report.RecentEvents.Select(e => JsonSerializer.Serialize(e, CompactOptions) + "\n")));
        _fileSystem.File.WriteAllText(_fileSystem.Path.Combine(folder, Files.Actions), JsonSerializer.Serialize(report.Actions, JsonOptions));
        _fileSystem.File.WriteAllText(_fileSystem.Path.Combine(folder, Files.Environment), JsonSerializer.Serialize(report.Environment, JsonOptions));
        return folder;
    }

    /// <summary>Every readable bundle, newest first.</summary>
    public IReadOnlyList<CrashBundle> ReadAll()
    {
        if (!_fileSystem.Directory.Exists(Root))
        {
            return [];
        }

        var bundles = new List<CrashBundle>();
        foreach (var folder in _fileSystem.Directory.GetDirectories(Root))
        {
            var json = _fileSystem.Path.Combine(folder, Files.ReportJson);
            if (!_fileSystem.File.Exists(json))
            {
                continue;
            }

            try
            {
                if (JsonSerializer.Deserialize<CrashReport>(_fileSystem.File.ReadAllText(json), JsonOptions) is { } report)
                {
                    bundles.Add(new CrashBundle(folder, report));
                }
            }
            catch (JsonException)
            {
                // Skip a bundle that was being written when the process died.
            }
        }

        return [.. bundles.OrderByDescending(b => b.Report.Timestamp)];
    }

    /// <summary>The most recent bundle, or null.</summary>
    public CrashBundle? Latest()
    {
        var all = ReadAll();
        return all.Count > 0 ? all[0] : null;
    }

    /// <summary>Finds a bundle by ID (or unique ID prefix).</summary>
    public CrashBundle? Find(string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        var matches = ReadAll().Where(b => b.Report.Id.StartsWith(id, StringComparison.OrdinalIgnoreCase)).ToList();
        return matches.Count == 1 ? matches[0] : matches.FirstOrDefault(b => b.Report.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Bundles grouped by signature, most recent group first.</summary>
    public IReadOnlyList<CrashGroup> Groups() =>
        [.. ReadAll()
            .GroupBy(b => b.Report.Signature)
            .Select(g => new CrashGroup(g.Key, g.Count(), g.Min(b => b.Report.Timestamp), g.Max(b => b.Report.Timestamp), g.First()))
            .OrderByDescending(g => g.LastSeen)];

    /// <summary>Reads a bundle's report.md.</summary>
    public string ReadMarkdown(CrashBundle bundle)
    {
        ArgumentNullException.ThrowIfNull(bundle);
        return _fileSystem.File.ReadAllText(_fileSystem.Path.Combine(bundle.Folder, Files.ReportMarkdown));
    }

    private static readonly JsonSerializerOptions CompactOptions = new() { Converters = { new JsonStringEnumConverter() } };
}
