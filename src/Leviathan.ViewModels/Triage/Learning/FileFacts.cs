using System.Globalization;
using System.Text.RegularExpressions;

namespace Leviathan.ViewModels.Triage.Learning;

/// <summary>What the file's own name and place say: the words in the name (performer, studio and title are in there), the folder, the year.</summary>
/// <param name="Tokens">The distinct lower-case words of the file name, without the extension.</param>
/// <param name="Folder">The name of the folder it sits in.</param>
/// <param name="Extension">The extension, lower case, with the dot.</param>
/// <param name="Year">A year (1950 to 2099) found in the name, or null.</param>
/// <param name="Resolution">A height such as 1080 from a "1080p" in the name, or null.</param>
public sealed record NameFacts(IReadOnlyList<string> Tokens, string Folder, string Extension, int? Year, int? Resolution);

/// <summary>What ffprobe says about the video. Any value it did not give is null.</summary>
public sealed record VideoFacts(double? DurationSeconds, int? Width, int? Height, string? Codec, long? BitRate);

/// <summary>What a server knows about the scene a file is: who is in it, its tags, studio and date.</summary>
/// <param name="SourceId">The <see cref="LearningSource.Id"/> that answered.</param>
/// <param name="Title">The scene's title.</param>
/// <param name="Studio">The studio.</param>
/// <param name="Date">The date of production as the server holds it (<c>yyyy-MM-dd</c>).</param>
/// <param name="Rating">The server's rating, 0 to 100, or null.</param>
/// <param name="Performers">The performers' names.</param>
/// <param name="Tags">The tags' names.</param>
public sealed record SceneFacts(string SourceId, string? Title, string? Studio, string? Date, int? Rating, IReadOnlyList<string> Performers, IReadOnlyList<string> Tags);

/// <summary>
/// Everything the learning knows about one decided file, kept for good because the file itself is usually gone afterwards (a discard
/// goes to the bin, a keep is moved). <see cref="Complete"/> is false while a server that should have been asked could not be reached,
/// so the file is tried again later and never learned from half-informed.
/// <see cref="Asked"/> lists the servers that answered, so a server added later is asked about files collected before it existed.
/// </summary>
public sealed record FileFacts(string Path, long Size, long ModifiedTicks, DateTimeOffset Collected, NameFacts Name, VideoFacts? Video, IReadOnlyList<SceneFacts> Scenes, IReadOnlyList<string> Asked, bool Complete);

/// <summary>Reads the facts that are in a file's name and place.</summary>
public static partial class NameParser
{
    private const int MaxTokens = 40;

    [GeneratedRegex(@"[^\p{L}\p{N}']+")]
    private static partial Regex Separators();

    [GeneratedRegex(@"^(\d{3,4})p$", RegexOptions.IgnoreCase)]
    private static partial Regex ResolutionToken();

    /// <summary>The facts in <paramref name="path"/> (a Windows or UNC path); never reads the file or the disk.</summary>
    public static NameFacts Parse(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        var cut = path.LastIndexOfAny(['\\', '/']);
        var file = cut >= 0 ? path[(cut + 1)..] : path;
        var folderPath = cut > 0 ? path[..cut].TrimEnd('\\', '/') : string.Empty;
        var folderCut = folderPath.LastIndexOfAny(['\\', '/']);
        var folder = folderCut >= 0 ? folderPath[(folderCut + 1)..] : folderPath;

        var dot = file.LastIndexOf('.');
        var extension = dot > 0 && file.Length - dot <= 6 ? file[dot..].ToLowerInvariant() : string.Empty;
        var stem = extension.Length > 0 ? file[..dot] : file;

        var tokens = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        int? year = null;
        int? resolution = null;
        foreach (var raw in Separators().Split(stem))
        {
            var word = raw.Trim('\'').ToLowerInvariant();
            if (word.Length < 2)
            {
                continue;
            }

            if (ResolutionToken().Match(word) is { Success: true } r)
            {
                resolution ??= int.Parse(r.Groups[1].Value, CultureInfo.InvariantCulture);
            }
            else if (word.Length == 4 && int.TryParse(word, NumberStyles.None, CultureInfo.InvariantCulture, out var y) && y is >= 1950 and <= 2099)
            {
                year ??= y;
            }

            if (tokens.Count < MaxTokens && seen.Add(word))
            {
                tokens.Add(word);
            }
        }

        return new NameFacts(tokens, folder, extension, year, resolution);
    }
}
