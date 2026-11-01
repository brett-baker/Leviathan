using System.Globalization;
using System.Text;

namespace Leviathan.Diagnostics.Crash;

/// <summary>
/// Renders report.md: the summary written for Claude to start from (spec §20.3). Sections always appear in the same
/// order so a reader (or a model) knows where to look.
/// </summary>
public static class CrashReportMarkdown
{
    /// <summary>Section headings, in order.</summary>
    public static IReadOnlyList<string> Sections { get; } =
    [
        "Summary",
        "Crash signature",
        "Exception chain",
        "Stack trace",
        "What was happening",
        "Relevant log lines",
        "Environment",
        "Starting points",
        "How to reproduce",
    ];

    /// <summary>Renders the report.</summary>
    /// <param name="report">The report.</param>
    /// <param name="occurrences">How many bundles share this signature (including this one).</param>
    /// <param name="firstSeen">When the signature was first seen.</param>
    public static string Render(CrashReport report, int occurrences = 1, DateTimeOffset? firstSeen = null)
    {
        ArgumentNullException.ThrowIfNull(report);
        var md = new StringBuilder();
        var inv = CultureInfo.InvariantCulture;

        md.Append("# Leviathan ").Append(report.Kind.ToString().ToLowerInvariant()).Append(" report — ").AppendLine(report.Id);
        md.AppendLine();
        md.AppendLine("> Generated automatically. Read top to bottom; the stack links point at the exact commit that was running.");
        md.AppendLine();

        md.AppendLine("## Summary").AppendLine();
        md.AppendLine(report.Summary);
        md.Append(CultureInfo.InvariantCulture, $"- Process: `{report.Process}` · Time: {report.Timestamp:yyyy-MM-dd HH:mm:ss} UTC · Version: `{report.Build.Version}`").AppendLine();
        if (!string.IsNullOrWhiteSpace(report.UserNote))
        {
            md.Append("- User note: ").AppendLine(report.UserNote);
        }

        md.AppendLine();

        md.AppendLine("## Crash signature").AppendLine();
        md.Append(inv, $"`{report.Signature}` — seen {occurrences} time{(occurrences == 1 ? string.Empty : "s")}");
        if (firstSeen is { } first)
        {
            md.Append(inv, $", first on {first:yyyy-MM-dd HH:mm} UTC");
        }

        md.AppendLine(".").AppendLine();

        md.AppendLine("## Exception chain").AppendLine();
        if (report.Exceptions.Count == 0)
        {
            md.AppendLine("No exception (hang or manual report).");
        }
        else
        {
            for (var i = 0; i < report.Exceptions.Count; i++)
            {
                var ex = report.Exceptions[i];
                md.Append(inv, $"{i + 1}. `{ex.Type}`: {ex.Message} — HRESULT {ex.HResult}");
                if (ex.Win32Error is not null)
                {
                    md.Append(" (").Append(ex.Win32Error).Append(')');
                }

                md.AppendLine();
            }
        }

        md.AppendLine();

        md.AppendLine("## Stack trace").AppendLine();
        if (report.Frames.Count == 0)
        {
            md.AppendLine("Not available.");
        }
        else
        {
            foreach (var frame in report.Frames.Take(40))
            {
                md.Append("- `").Append(frame.Method).Append('`');
                if (frame.File is not null)
                {
                    md.Append(" — ").Append(frame.File);
                    if (frame.Line is { } line)
                    {
                        md.Append(':').Append(line.ToString(inv));
                    }

                    if (frame.Permalink is not null)
                    {
                        md.Append(" ([source](").Append(frame.Permalink).Append("))");
                    }
                }

                md.AppendLine();
            }
        }

        md.AppendLine();

        md.AppendLine("## What was happening").AppendLine();
        md.AppendLine("Last user actions (oldest first):").AppendLine();
        if (report.Actions.Count == 0)
        {
            md.AppendLine("- None recorded.");
        }
        else
        {
            foreach (var action in report.Actions.TakeLast(20))
            {
                md.Append(inv, $"- {action.Timestamp:HH:mm:ss} {action.Command}");
                if (action.Target is not null)
                {
                    md.Append(" `").Append(action.Target).Append('`');
                }

                md.AppendLine();
            }
        }

        if (report.Context.Count > 0)
        {
            md.AppendLine().AppendLine("Context:").AppendLine();
            foreach (var (key, value) in report.Context.OrderBy(kv => kv.Key, StringComparer.Ordinal))
            {
                md.Append("- **").Append(key).Append(":** ").AppendLine(value);
            }
        }

        md.AppendLine();

        md.AppendLine("## Relevant log lines").AppendLine();
        var events = report.RecentEvents.TakeLast(50).ToList();
        if (events.Count == 0)
        {
            md.AppendLine("None recorded.");
        }
        else
        {
            md.AppendLine("```");
            foreach (var evt in events)
            {
                md.Append(inv, $"{evt.Timestamp:HH:mm:ss.fff} {evt.Level,-11} [{evt.Process}] {evt.Category}: {evt.Message}");
                if (evt.TraceId is not null)
                {
                    md.Append(" trace=").Append(evt.TraceId);
                }

                md.AppendLine();
                if (evt.Exception is not null)
                {
                    md.AppendLine("    " + evt.Exception.Split('\n')[0].Trim());
                }
            }

            md.AppendLine("```");
        }

        md.AppendLine();

        md.AppendLine("## Environment").AppendLine();
        foreach (var (key, value) in report.Environment.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            md.Append("- ").Append(key).Append(": ").AppendLine(value);
        }

        md.AppendLine();

        md.AppendLine("## Starting points").AppendLine();
        if (report.StartingPoints.Count == 0)
        {
            md.AppendLine("No source files on the stack; start from the component named in the summary.");
        }
        else
        {
            foreach (var file in report.StartingPoints)
            {
                md.Append("- `").Append(file).AppendLine("`");
            }
        }

        md.AppendLine();

        md.AppendLine("## How to reproduce").AppendLine();
        if (report.Actions.Count == 0)
        {
            md.AppendLine("No action trail. Use the log lines above.");
        }
        else
        {
            md.Append(inv, $"Replay the action trail in a debug build: `leviathan-diag replay {report.Id}`").AppendLine();
        }

        return md.ToString();
    }
}
