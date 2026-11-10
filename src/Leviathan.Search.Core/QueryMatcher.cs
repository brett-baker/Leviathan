using System.Text.RegularExpressions;
using Leviathan.Core.Model;

namespace Leviathan.Search;

/// <summary>
/// Decides whether a file matches a <see cref="Query"/> (spec §13.1). Conditions are tried cheapest first: name, extension, size, dates,
/// type and path come from the directory entry; tags, media information and file content are asked of <see cref="ISearchFacts"/> only for
/// files that passed everything cheaper. Time comes from the <see cref="TimeProvider"/>, so <c>lastweek</c> is testable and a saved search stays current.
/// </summary>
public sealed class QueryMatcher
{
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(100);

    private readonly List<(Func<FileEntry, bool> Test, bool Negated)> _cheap = [];
    private readonly List<(QueryTerm Term, bool Negated)> _costly = [];
    private readonly TimeProvider _time;
    private readonly TimeZoneInfo _zone;

    /// <summary>Prepares <paramref name="query"/> for matching; the clauses it understood are used, errors are ignored.</summary>
    public QueryMatcher(Query query, TimeProvider? time = null, TimeZoneInfo? zone = null)
    {
        ArgumentNullException.ThrowIfNull(query);
        _time = time ?? TimeProvider.System;
        _zone = zone ?? TimeZoneInfo.Local;
        foreach (var clause in query.Clauses)
        {
            if (clause.Term is TagFilter or DurationFilter or ResolutionFilter or CodecFilter or ContentFilter)
            {
                _costly.Add((clause.Term, clause.Negated));
            }
            else
            {
                _cheap.Add((Compile(clause.Term), clause.Negated));
            }
        }
    }

    /// <summary>True when matching may read tags, media information or file content, so the caller should supply real <see cref="ISearchFacts"/>.</summary>
    public bool NeedsFacts => _costly.Count > 0;

    /// <summary>True when the query has no conditions: everything matches.</summary>
    public bool MatchesEverything => _cheap.Count == 0 && _costly.Count == 0;

    /// <summary>Tests only the conditions that need nothing but the directory entry. A caller that can skip the costly part uses this as a first pass.</summary>
    public bool MatchesCheap(FileEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        foreach (var (test, negated) in _cheap)
        {
            if (test(entry) == negated)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Tests every condition, reading tags, media information or content only when the cheap ones passed.</summary>
    public async ValueTask<bool> MatchesAsync(FileEntry entry, ISearchFacts facts, CancellationToken cancellationToken)
    {
        if (!MatchesCheap(entry))
        {
            return false;
        }

        if (_costly.Count == 0)
        {
            return true;
        }

        IReadOnlyList<string>? tags = null;
        MediaFacts? media = null;
        var mediaRead = false;
        foreach (var (term, negated) in _costly)
        {
            cancellationToken.ThrowIfCancellationRequested();
            bool result;
            switch (term)
            {
                case TagFilter tag:
                    tags ??= await facts.GetTagsAsync(entry, cancellationToken).ConfigureAwait(false);
                    result = tags.Any(t => string.Equals(t, tag.Name, StringComparison.OrdinalIgnoreCase));
                    break;
                case ContentFilter content:
                    result = entry.Kind == FileEntryKind.File && await facts.ContentContainsAsync(entry, content.Text, cancellationToken).ConfigureAwait(false);
                    break;
                default:
                    if (!mediaRead)
                    {
                        media = await facts.GetMediaAsync(entry, cancellationToken).ConfigureAwait(false);
                        mediaRead = true;
                    }

                    result = MatchesMedia(term, media);
                    break;
            }

            if (result == negated)
            {
                return false;
            }
        }

        return true;
    }

    private static bool MatchesMedia(QueryTerm term, MediaFacts? media) => term switch
    {
        DurationFilter d => media?.Duration is { } duration && d.Duration.Test(duration),
        ResolutionFilter r => media?.Height is { } height && r.Height.Test(height),
        CodecFilter c => media is not null && media.Codecs.Any(x => string.Equals(x, c.Codec, StringComparison.OrdinalIgnoreCase)),
        _ => false,
    };

    private Func<FileEntry, bool> Compile(QueryTerm term)
    {
        switch (term)
        {
            case NameTerm name:
                return CompileName(name);
            case RegexTerm regex:
                var pattern = new Regex(regex.Pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeout);
                return e => SafeIsMatch(pattern, e.Name);
            case ExtensionFilter ext:
                var extensions = new HashSet<string>(ext.Extensions, StringComparer.OrdinalIgnoreCase);
                return e => e.Kind != FileEntryKind.Folder && extensions.Contains(e.Extension.TrimStart('.'));
            case SizeFilter size:
                return e => e.Kind == FileEntryKind.File && size.Size.Test(e.Size);
            case DateFilter date:
                return e => MatchesDate(date, e);
            case TypeFilter type:
                return e => type.Kind switch
                {
                    FileTypeKind.Folder => e.Kind == FileEntryKind.Folder,
                    FileTypeKind.File => e.Kind != FileEntryKind.Folder,
                    _ => e.Kind == FileEntryKind.File && FileTypes.IsKind(e.Extension.TrimStart('.'), type.Kind),
                };
            case PathFilter path:
                var wanted = path.Text.Replace('/', '\\');
                return e => e.Location.ToString().Replace('/', '\\').Contains(wanted, StringComparison.OrdinalIgnoreCase);
            default:
                return _ => true;
        }
    }

    private static Func<FileEntry, bool> CompileName(NameTerm term)
    {
        if (!term.HasWildcards)
        {
            return e => e.Name.Contains(term.Text, StringComparison.OrdinalIgnoreCase);
        }

        // A wildcard term describes the whole name: IMG_*.jpg must start with IMG_ and end with .jpg.
        var pattern = new Regex("^" + Regex.Escape(term.Text).Replace("\\*", ".*", StringComparison.Ordinal).Replace("\\?", ".", StringComparison.Ordinal) + "$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Singleline, RegexTimeout);
        return e => SafeIsMatch(pattern, e.Name);
    }

    private static bool SafeIsMatch(Regex pattern, string text)
    {
        try
        {
            return pattern.IsMatch(text);
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }
    }

    private bool MatchesDate(DateFilter filter, FileEntry entry)
    {
        var moment = filter.Field == DateField.Created ? entry.Created : entry.Modified;
        var day = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(moment, _zone).DateTime);
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(_time.GetUtcNow(), _zone).DateTime);
        var (start, end) = filter.Value.Span(today);
        return filter.Op switch
        {
            Comparison.Equal => day >= start && day < end,
            Comparison.Greater => day >= end,
            Comparison.GreaterOrEqual => day >= start,
            Comparison.Less => day < start,
            Comparison.LessOrEqual => day < end,
            Comparison.Between => day >= start && day < filter.Upper!.Span(today).End,
            _ => false,
        };
    }
}
