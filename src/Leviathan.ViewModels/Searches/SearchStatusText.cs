using System.Globalization;
using Leviathan.Search;

namespace Leviathan.ViewModels.Searches;

/// <summary>The words for the status bar while a search runs and when it ends (spec §13: "searched N folders", errors shown, never silent).</summary>
public static class SearchStatusText
{
    /// <summary>One line for a report: progress while it runs, the outcome when it is over.</summary>
    /// <param name="report">The latest report.</param>
    public static string For(SearchReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        var running = report.IsRunning;
        var p = report.Progress;
        var found = Count(p.Matches, "match", "matches");
        var folders = Count(p.FoldersSearched, "folder", "folders");
        string text;
        if (running)
        {
            text = string.Create(CultureInfo.InvariantCulture, $"Searching… {found} so far, {folders} searched");
        }
        else if (report.Cancelled)
        {
            text = string.Create(CultureInfo.InvariantCulture, $"Search stopped: {found} in {folders} (not everything was searched)");
        }
        else
        {
            text = string.Create(CultureInfo.InvariantCulture, $"{found} in {folders} searched");
        }

        if (p.ErrorCount > 0)
        {
            text += string.Create(CultureInfo.InvariantCulture, $" · {Count(p.ErrorCount, "folder", "folders")} could not be read");
        }

        if (SourcesNote(report) is { } sources)
        {
            text += " · " + sources;
        }

        if (!running && UsesMediaFields(report.Request))
        {
            text += " · duration, res and codec match only files whose media information is already known";
        }

        return text;
    }

    // Say where the answers came from whenever it was not a plain live listing: an index can be out of date, and a missing Everything is worth knowing.
    private static string? SourcesNote(SearchReport report)
    {
        if (report.Sources is not { Count: > 0 } sources || sources.All(s => s.Kind == SearchSourceKind.Live && s.Note is null))
        {
            return null;
        }

        var parts = new List<string>();
        foreach (var source in sources)
        {
            parts.Add(source.Kind switch
            {
                SearchSourceKind.Everything => "from Everything",
                SearchSourceKind.Index => string.Create(CultureInfo.InvariantCulture, $"from the index, last listed {source.IndexedAt?.ToLocalTime():yyyy-MM-dd HH:mm}"),
                _ => source.Note is null ? "folders listed now" : $"folders listed now ({source.Note.ToLowerInvariant()})",
            });
        }

        return string.Join(", ", parts);
    }

    private static bool UsesMediaFields(SearchRequest request) =>
        QueryParser.Parse(request.QueryText).Clauses.Any(c => c.Term is DurationFilter or ResolutionFilter or CodecFilter);

    private static string Count(long n, string one, string many) =>
        string.Create(CultureInfo.InvariantCulture, $"{n:N0} {(n == 1 ? one : many)}");
}
