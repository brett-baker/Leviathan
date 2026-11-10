using System.Globalization;
using System.Text;
using Leviathan.Core.Paths;

namespace Leviathan.Search;

/// <summary>What to search: the query text as typed, and the folders to search under.</summary>
/// <param name="QueryText">The query as typed in the search box (spec §13.1).</param>
/// <param name="Roots">The folders searched, each with everything below it.</param>
/// <param name="IncludeHidden">Whether hidden items are searched.</param>
public sealed record SearchRequest(string QueryText, IReadOnlyList<LocationPath> Roots, bool IncludeHidden = false);

/// <summary>
/// The virtual folder that holds the results of a search, <c>shell:search\…</c> (spec §13: "results in a normal file view tab"). The request is
/// written into the path itself (hex, so it survives the case-insensitive comparison of shell locations), which means a search tab can go back
/// and forward, be restored with the last session, be pinned to Favorites (spec §13.2) and be reloaded, with no registry of searches to keep in step.
/// </summary>
public static class SearchLocations
{
    /// <summary>The root of all search result folders.</summary>
    public const string Root = "shell:search";

    private const int ChunkLength = 60;
    private const int MaxQueryLength = 500;
    private const int MaxRoots = 16;

    /// <summary>True for <c>shell:search</c> and anything below it.</summary>
    public static bool IsSearchLocation(LocationPath location)
    {
        ArgumentNullException.ThrowIfNull(location);
        return location.Kind == LocationKind.Shell && string.Equals(location.Root, Root, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The location that holds the results of <paramref name="request"/>.</summary>
    public static LocationPath For(SearchRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var query = string.Join(' ', request.QueryText.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)).Trim();
        if (query.Length > MaxQueryLength)
        {
            query = query[..MaxQueryLength];
        }

        var lines = new List<string> { "v1", request.IncludeHidden ? "h" : "-" };
        lines.AddRange(request.Roots.Take(MaxRoots).Select(r => r.ToString()));
        lines.Add(query);
        var hex = Convert.ToHexStringLower(Encoding.UTF8.GetBytes(string.Join('\n', lines)));
        var chunks = Enumerable.Range(0, (hex.Length + ChunkLength - 1) / ChunkLength).Select(i => hex.Substring(i * ChunkLength, Math.Min(ChunkLength, hex.Length - (i * ChunkLength))));
        return LocationPath.Parse(Root + "\\" + string.Join('\\', chunks));
    }

    /// <summary>The request a search location holds, or null when <paramref name="location"/> is not one or is damaged.</summary>
    public static SearchRequest? TryRead(LocationPath location)
    {
        if (!IsSearchLocation(location) || location.Segments.Count == 0)
        {
            return null;
        }

        try
        {
            var text = Encoding.UTF8.GetString(Convert.FromHexString(string.Concat(location.Segments)));
            var lines = text.Split('\n');
            if (lines.Length < 3 || lines[0] != "v1")
            {
                return null;
            }

            var roots = new List<LocationPath>();
            foreach (var line in lines.Skip(2).Take(lines.Length - 3))
            {
                if (!LocationPath.TryParse(line, out var root))
                {
                    return null;
                }

                roots.Add(root);
            }

            return new SearchRequest(lines[^1], roots, lines[1] == "h");
        }
        catch (FormatException)
        {
            return null;
        }
    }

    /// <summary>The short name for a tab or a Favorites entry, such as <c>Search: holiday</c>.</summary>
    public static string TitleOf(LocationPath location)
    {
        var request = TryRead(location);
        if (request is null)
        {
            return "Search";
        }

        var query = request.QueryText.Trim();
        return query.Length == 0 ? "Search" : string.Create(CultureInfo.InvariantCulture, $"Search: {(query.Length > 40 ? query[..40] + "…" : query)}");
    }

    /// <summary>The address-bar text: the query and where it looks, such as <c>holiday in C:\Videos</c>.</summary>
    public static string DescribeOf(LocationPath location)
    {
        var request = TryRead(location);
        if (request is null)
        {
            return "Search";
        }

        var where = request.Roots.Count switch
        {
            0 => "nowhere",
            1 => request.Roots[0].ToString(),
            _ => string.Create(CultureInfo.InvariantCulture, $"{request.Roots.Count} folders"),
        };
        return string.Create(CultureInfo.InvariantCulture, $"Search for \"{request.QueryText.Trim()}\" in {where}");
    }
}
