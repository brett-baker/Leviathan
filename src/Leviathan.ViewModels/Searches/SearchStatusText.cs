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

        if (!running && UsesMediaFields(report.Request))
        {
            text += " · duration, res and codec match only files whose media information is already known";
        }

        return text;
    }

    private static bool UsesMediaFields(SearchRequest request) =>
        QueryParser.Parse(request.QueryText).Clauses.Any(c => c.Term is DurationFilter or ResolutionFilter or CodecFilter);

    private static string Count(long n, string one, string many) =>
        string.Create(CultureInfo.InvariantCulture, $"{n:N0} {(n == 1 ? one : many)}");
}
