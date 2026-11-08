using System.Text.RegularExpressions;
using Leviathan.Core.Model;

namespace Leviathan.Core.Duplicates;

/// <summary>Resolution read from a file name, best last.</summary>
public enum NameResolution
{
    /// <summary>Not in the name.</summary>
    Unknown,

    /// <summary>480p and below.</summary>
    SubHd,

    /// <summary>720p.</summary>
    Hd720,

    /// <summary>1080p.</summary>
    Hd1080,

    /// <summary>2160p, 4K.</summary>
    Uhd2160,
}

/// <summary>
/// Name-based duplicate logic ported from Brett's vlib (<c>NameSimilarity</c>, <c>QualityNameParser</c>, <c>QualityRanker</c>): a file name is
/// reduced to its significant words (no resolution, codec, container, release-group noise, brackets or numbers) and two names are compared
/// by Jaccard overlap, so the same release in 720p and 1080p, or with an extra group tag, counts as a duplicate (spec §12.7).
/// </summary>
public static partial class NameSimilarity
{
    private static readonly HashSet<string> Noise = new(StringComparer.OrdinalIgnoreCase)
    {
        "xxx", "ppv", "new", "web", "webdl", "hdrip", "bdrip", "dvdrip", "hdtv", "webrip",
        "x264", "x265", "h264", "h265", "hevc", "avc", "av1", "xvid", "divx", "aac", "mp3", "opus",
        "mp4", "mkv", "avi", "wmv", "mov", "m4v", "flv",
        "p2p", "wrb", "xleech", "nogroup", "prt", "xvx", "rbg", "rarbg", "xc", "ktr", "sdh",
        "internal", "real", "proper", "repack", "uncut",
        "fhd", "uhd", "hd", "sd", "hq",
        "scene", "video", "clip", "part", "pt",
    };

    [GeneratedRegex(@"[\[\(\{][^\]\)\}]*[\]\)\}]")]
    private static partial Regex Bracketed();

    [GeneratedRegex(@"[^a-z0-9]+")]
    private static partial Regex NonWord();

    [GeneratedRegex(@"^\d{3,4}p$")]
    private static partial Regex ResToken();

    [GeneratedRegex(@"^\d{3,4}x\d{3,4}$")]
    private static partial Regex DimToken();

    [GeneratedRegex(@"\.(?<n>\d+)$")]
    private static partial Regex ConflictSuffix();

    [GeneratedRegex(@"(?:^|[.\-_ \[\(])(2160p|4k|uhd)(?:$|[.\-_ \]\)])", RegexOptions.IgnoreCase)]
    private static partial Regex Res2160();

    [GeneratedRegex(@"(?:^|[.\-_ \[\(])1080p(?:$|[.\-_ \]\)])", RegexOptions.IgnoreCase)]
    private static partial Regex Res1080();

    [GeneratedRegex(@"(?:^|[.\-_ \[\(])720p(?:$|[.\-_ \]\)])", RegexOptions.IgnoreCase)]
    private static partial Regex Res720();

    [GeneratedRegex(@"(?:^|[.\-_ \[\(])(480p|480i|360p|360i|240p|sd)(?:$|[.\-_ \]\)])", RegexOptions.IgnoreCase)]
    private static partial Regex ResSubHd();

    [GeneratedRegex(@"(?:^|[.\-_ \[\(])(hevc|x265|h\.?265)(?:$|[.\-_ \]\)])", RegexOptions.IgnoreCase)]
    private static partial Regex CodecHevc();

    /// <summary>Reduces a file name (no extension) to its set of significant lowercase words.</summary>
    public static HashSet<string> Normalize(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (var token in NonWord().Split(Bracketed().Replace(name.ToLowerInvariant(), " ")))
        {
            if (token.Length < 2 || token.All(char.IsDigit) || ResToken().IsMatch(token) || DimToken().IsMatch(token) || token is "4k" || Noise.Contains(token))
            {
                continue;
            }

            set.Add(token);
        }

        return set;
    }

    /// <summary>|A ∩ B| / |A ∪ B|; 0 when either set is empty.</summary>
    public static double Jaccard(IReadOnlySet<string> a, IReadOnlySet<string> b)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);
        if (a.Count == 0 || b.Count == 0)
        {
            return 0;
        }

        var (small, large) = a.Count <= b.Count ? (a, b) : (b, a);
        var shared = small.Count(large.Contains);
        return (double)shared / (a.Count + b.Count - shared);
    }

    /// <summary>The name without extension and without a trailing ".N" conflict suffix (<c>x.1.mp4</c> is a copy of <c>x.mp4</c>).</summary>
    public static string BaseName(string fileName) => ConflictSuffix().Replace(Path.GetFileNameWithoutExtension(fileName), string.Empty);

    /// <summary>True when the name ends in a ".N" conflict suffix.</summary>
    public static bool HasConflictSuffix(string fileName) => ConflictSuffix().IsMatch(Path.GetFileNameWithoutExtension(fileName));

    /// <summary>The resolution the name states.</summary>
    public static NameResolution ResolutionOf(string fileName) =>
        Res2160().IsMatch(fileName) ? NameResolution.Uhd2160
        : Res1080().IsMatch(fileName) ? NameResolution.Hd1080
        : Res720().IsMatch(fileName) ? NameResolution.Hd720
        : ResSubHd().IsMatch(fileName) ? NameResolution.SubHd
        : NameResolution.Unknown;

    /// <summary>True when the name says HEVC / x265.</summary>
    public static bool IsHevc(string fileName) => CodecHevc().IsMatch(fileName);
}

/// <summary>One set of files that look like copies of the same thing.</summary>
/// <param name="Keeper">The copy to keep (vlib's order: HEVC 1080p, HEVC 720p, largest 1080p/720p, smallest 2160p; ties prefer .mkv then the longest name).</param>
/// <param name="Others">The other copies, most similar to the keeper first.</param>
/// <param name="Similarity">Name similarity of each other copy to the keeper (1 for an exact conflict copy), same order as <paramref name="Others"/>.</param>
public sealed record DuplicateGroup(FileEntry Keeper, IReadOnlyList<FileEntry> Others, IReadOnlyList<double> Similarity)
{
    /// <summary>Bytes freed by removing <see cref="Others"/>.</summary>
    public long ReclaimableBytes => Others.Sum(f => f.Size);
}

/// <summary>
/// Finds duplicate groups among the files of one folder by name (spec §12.7): "x.1.mp4" next to "x.mp4" with the same size is an exact
/// conflict copy; otherwise names whose significant words overlap at least the threshold are grouped (single-link, so a chain of near
/// names forms one group). Candidate pairs come from a word index, so a folder of thousands of files is not compared pairwise.
/// </summary>
public static class DuplicateFinder
{
    /// <summary>The default similarity (0 to 1) at which two names count as the same thing.</summary>
    public const double DefaultThreshold = 0.6;

    /// <summary>Finds the groups among <paramref name="files"/> (folders are ignored), largest reclaim first.</summary>
    public static IReadOnlyList<DuplicateGroup> Find(IReadOnlyList<FileEntry> files, double threshold = DefaultThreshold)
    {
        ArgumentNullException.ThrowIfNull(files);
        if (threshold <= 0 || threshold > 1)
        {
            threshold = DefaultThreshold;
        }

        var items = files.Where(f => f.Kind == FileEntryKind.File).ToArray();
        var words = items.Select(f => NameSimilarity.Normalize(Path.GetFileNameWithoutExtension(f.Name))).ToArray();
        var parent = Enumerable.Range(0, items.Length).ToArray();
        int Root(int x)
        {
            while (parent[x] != x)
            {
                x = parent[x] = parent[parent[x]];
            }

            return x;
        }

        // Exact conflict copies: same base name and extension, same size.
        var exact = items.Select((f, i) => (File: f, Index: i))
            .GroupBy(t => (NameSimilarity.BaseName(t.File.Name).ToUpperInvariant(), Path.GetExtension(t.File.Name).ToUpperInvariant(), t.File.Size))
            .Where(g => g.Count() > 1);
        foreach (var same in exact)
        {
            var first = same.First().Index;
            foreach (var t in same.Skip(1))
            {
                parent[Root(t.Index)] = Root(first);
            }
        }

        // Similar names: only files sharing a word are compared.
        var index = new Dictionary<string, List<int>>(StringComparer.Ordinal);
        for (var i = 0; i < items.Length; i++)
        {
            foreach (var word in words[i])
            {
                if (!index.TryGetValue(word, out var list))
                {
                    index[word] = list = [];
                }

                list.Add(i);
            }
        }

        var seen = new HashSet<long>();
        foreach (var list in index.Values.Where(l => l.Count > 1 && l.Count <= 2000))
        {
            for (var a = 0; a < list.Count; a++)
            {
                for (var b = a + 1; b < list.Count; b++)
                {
                    int i = list[a], j = list[b];
                    if (Root(i) != Root(j) && seen.Add(((long)Math.Min(i, j) << 32) | (uint)Math.Max(i, j)) && NameSimilarity.Jaccard(words[i], words[j]) >= threshold)
                    {
                        parent[Root(i)] = Root(j);
                    }
                }
            }
        }

        var groups = new List<DuplicateGroup>();
        foreach (var cluster in Enumerable.Range(0, items.Length).GroupBy(Root).Where(g => g.Count() > 1))
        {
            var members = cluster.ToList();
            var keeper = Keeper([.. members.Select(i => items[i])]);
            var k = members.First(i => ReferenceEquals(items[i], keeper));
            var others = members.Where(i => i != k)
                .Select(i => (File: items[i], Score: Score(items[k], items[i], words[k], words[i])))
                .OrderByDescending(t => t.Score).ThenBy(t => t.File.Name, StringComparer.OrdinalIgnoreCase).ToList();
            groups.Add(new DuplicateGroup(keeper, [.. others.Select(t => t.File)], [.. others.Select(t => t.Score)]));
        }

        return [.. groups.OrderByDescending(g => g.ReclaimableBytes).ThenBy(g => g.Keeper.Name, StringComparer.OrdinalIgnoreCase)];
    }

    private static double Score(FileEntry keeper, FileEntry other, IReadOnlySet<string> a, IReadOnlySet<string> b) =>
        NameSimilarity.BaseName(keeper.Name).Equals(NameSimilarity.BaseName(other.Name), StringComparison.OrdinalIgnoreCase) && keeper.Size == other.Size
            ? 1
            : NameSimilarity.Jaccard(a, b);

    /// <summary>The copy to keep, by vlib's order (see <see cref="DuplicateGroup"/>).</summary>
    public static FileEntry Keeper(IReadOnlyList<FileEntry> group)
    {
        ArgumentNullException.ThrowIfNull(group);
        var info = group.Select(f => (File: f, Res: NameSimilarity.ResolutionOf(f.Name), Hevc: NameSimilarity.IsHevc(f.Name), Clean: !NameSimilarity.HasConflictSuffix(f.Name))).ToList();

        var pool = (List<(FileEntry File, NameResolution Res, bool Hevc, bool Clean)>?)null;
        foreach (var res in new[] { NameResolution.Hd1080, NameResolution.Hd720 })
        {
            var hevc = info.Where(i => i.Hevc && i.Res == res).ToList();
            if (hevc.Count > 0)
            {
                pool = hevc;
                break;
            }
        }

        if (pool is null && !info.Any(i => i.Hevc))
        {
            foreach (var res in new[] { NameResolution.Hd1080, NameResolution.Hd720 })
            {
                var at = info.Where(i => i.Res == res).ToList();
                if (at.Count > 0)
                {
                    var max = at.Max(i => i.File.Size);
                    pool = [.. at.Where(i => i.File.Size == max)];
                    break;
                }
            }
        }

        if (pool is null && info.Where(i => i.Res == NameResolution.Uhd2160).ToList() is { Count: > 0 } uhd)
        {
            var min = uhd.Min(i => i.File.Size);
            pool = [.. uhd.Where(i => i.File.Size == min)];
        }

        pool ??= info;
        return pool
            .OrderByDescending(i => i.Clean)
            .ThenByDescending(i => i.File.Name.EndsWith(".mkv", StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(i => i.File.Name.Length)
            .ThenByDescending(i => i.File.Size)
            .ThenBy(i => i.File.Name, StringComparer.OrdinalIgnoreCase)
            .First().File;
    }
}
