using System.Text;
using Leviathan.Contracts;
using Leviathan.Core.Paths;
using Leviathan.ViewModels.Folders;

namespace Leviathan.ViewModels.Hub;

/// <summary>One line of the report viewer: an item, what became of it, and why.</summary>
/// <param name="Source">The item as it was before the job.</param>
/// <param name="Name">Its name, for the line.</param>
/// <param name="Status">Done, skipped or failed.</param>
/// <param name="Glyph">A short mark for the status.</param>
/// <param name="Detail">Where it went, or why it failed.</param>
public sealed record ReportLine(string Source, string Name, ItemStatus Status, string Glyph, string Detail);

/// <summary>
/// A finished job's report as the viewer shows it (spec §12.3 "Reports"): the counts, the items (failures first, since those are
/// what the user opens it for), the notes, and what "Retry failed", "Show destination" and "Show source" would act on. The
/// worker keeps the report for seven days; this only arranges it.
/// </summary>
public sealed class OperationReportView(JobReport report)
{
    /// <summary>The report as the worker kept it.</summary>
    public JobReport Report { get; } = report ?? throw new ArgumentNullException(nameof(report));

    /// <summary>The job's title.</summary>
    public string Title => Report.Title;

    /// <summary>"3 done · 1 skipped · 2 failed · 1.4 GB", the counts that are not zero.</summary>
    public string Summary
    {
        get
        {
            var parts = new List<string>();
            if (Report.Done > 0)
            {
                parts.Add($"{Report.Done:N0} done");
            }

            if (Report.Skipped > 0)
            {
                parts.Add($"{Report.Skipped:N0} skipped");
            }

            if (Report.Failed > 0)
            {
                parts.Add($"{Report.Failed:N0} failed");
            }

            if (Report.BytesDone > 0)
            {
                parts.Add(DisplayFormat.Size(Report.BytesDone));
            }

            return parts.Count == 0 ? Report.State.ToString() : string.Join(" · ", parts);
        }
    }

    /// <summary>The items, failed ones first, then skipped, then done; the order inside each is the job's own.</summary>
    public IReadOnlyList<ReportLine> Lines =>
    [
        .. Report.Items
            .OrderBy(i => i.Status switch { ItemStatus.Failed => 0, ItemStatus.Skipped => 1, _ => 2 })
            .Select(i => new ReportLine(i.Source, NameOf(i.Source), i.Status, i.Status switch { ItemStatus.Failed => "✕", ItemStatus.Skipped => "–", _ => "✓" }, DetailOf(i))),
    ];

    /// <summary>Things worth knowing that are not failures, e.g. names changed to be valid on the destination.</summary>
    public IReadOnlyList<string> Notes => Report.Notes;

    /// <summary>The job's own error summary, when it failed as a whole.</summary>
    public string? Error => Report.Error;

    /// <summary>The sources that failed, for "Retry failed".</summary>
    public IReadOnlyList<string> FailedSources => [.. Report.Items.Where(i => i.Status == ItemStatus.Failed).Select(i => i.Source)];

    /// <summary>"Retry failed" can run: something failed, and the job is one that can run again from its sources (copy, move, delete).</summary>
    public bool CanRetryFailed => FailedSources.Count > 0 && (Report.Kind is JobKind.Delete || (Report.Kind is JobKind.Copy or JobKind.Move && Report.Destination is not null));

    /// <summary>Where the job put things (copy and move), or null.</summary>
    public string? Destination => Report.Destination;

    /// <summary>The folder holding the first source, for "Show source"; null when the report lists no items or the source has no parent.</summary>
    public string? SourceFolder
    {
        get
        {
            var first = Report.Items.Count > 0 ? Report.Items[0].Source : null;
            return first is null ? null : ParentOf(first);
        }
    }

    /// <summary>The report as plain text, for "Copy" and for pasting into a bug report.</summary>
    public string ToText()
    {
        var text = new StringBuilder();
        text.AppendLine(Report.Title);
        text.AppendLine($"{Report.State} · {Report.FinishedAt.ToLocalTime():g} · {Summary}");
        if (Report.Error is { Length: > 0 } error)
        {
            text.AppendLine(error);
        }

        foreach (var note in Report.Notes)
        {
            text.AppendLine($"Note: {note}");
        }

        text.AppendLine();
        foreach (var line in Lines)
        {
            text.AppendLine($"{line.Glyph} {line.Source}{(line.Detail.Length > 0 ? "  →  " + line.Detail : string.Empty)}");
        }

        return text.ToString();
    }

    private static string DetailOf(ItemOutcome item) => item.Status switch
    {
        ItemStatus.Failed => item.Error ?? "failed",
        ItemStatus.Skipped => item.Error ?? "skipped",
        _ => item.Result ?? string.Empty,
    };

    private static string NameOf(string location) => LocationPath.TryParse(location, out var path) ? path.Name : location;

    private static string? ParentOf(string location) => LocationPath.TryParse(location, out var path) ? path.Parent?.Value : null;
}
