using Leviathan.Diagnostics.Crash;

namespace Leviathan.ViewModels.Diagnostics;

/// <summary>One line of the crash list in the Diagnostics view: a bug (a signature) and how often it happened.</summary>
/// <param name="Signature">The crash signature that groups repeats (spec §20.3).</param>
/// <param name="Title">The summary of the latest occurrence.</param>
/// <param name="Detail">"3 times · last 2 h ago · first 4 d ago · Worker · Crash".</param>
/// <param name="Count">How many bundles share the signature.</param>
public sealed record CrashRow(string Signature, string Title, string Detail, int Count);

/// <summary>
/// The crash list of the Diagnostics view (spec §20.4): crashes grouped by signature so a repeat is counted and not reported again,
/// <b>Copy for Claude</b> (the latest report of a group) and <b>Open folder</b> (its bundle). Reads through
/// <see cref="CrashBundleStore"/> so it is tested on an in-memory file system; the window calls it off the UI thread.
/// </summary>
public sealed class CrashList(CrashBundleStore store, TimeProvider? time = null)
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    /// <summary>The folder that holds every bundle.</summary>
    public string Root => store.Root;

    /// <summary>The groups, the one seen most recently first.</summary>
    public IReadOnlyList<CrashRow> Rows()
    {
        var now = _time.GetUtcNow();
        return
        [
            .. store.Groups().Select(g =>
            {
                var report = g.Latest.Report;
                var parts = new List<string> { g.Count == 1 ? "once" : $"{g.Count} times", "last " + Ago(now - g.LastSeen) };
                if (g.Count > 1)
                {
                    parts.Add("first " + Ago(now - g.FirstSeen));
                }

                parts.Add(report.Process);
                parts.Add(report.Kind.ToString());
                return new CrashRow(g.Signature, report.Summary, string.Join(" · ", parts), g.Count);
            }),
        ];
    }

    /// <summary>The report of the latest occurrence of <paramref name="signature"/>, as the text to paste to Claude; null when the group is gone.</summary>
    public string? ReportFor(string signature)
    {
        if (Group(signature) is not { } group)
        {
            return null;
        }

        try
        {
            return store.ReadMarkdown(group.Latest);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The markdown file is missing or locked: the same text is rebuilt from report.json.
            return CrashReportMarkdown.Render(group.Latest.Report, group.Count, group.FirstSeen);
        }
    }

    /// <summary>The bundle folder of the latest occurrence of <paramref name="signature"/>, or null.</summary>
    public string? FolderFor(string signature) => Group(signature)?.Latest.Folder;

    private CrashGroup? Group(string signature) =>
        store.Groups().FirstOrDefault(g => string.Equals(g.Signature, signature, StringComparison.Ordinal));

    private static string Ago(TimeSpan age) =>
        age < TimeSpan.FromMinutes(1) ? "just now"
        : age.TotalHours < 1 ? $"{(int)age.TotalMinutes} min ago"
        : age.TotalDays < 1 ? $"{(int)age.TotalHours} h ago"
        : $"{(int)age.TotalDays} d ago";
}
