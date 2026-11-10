using Leviathan.Core.Model;

namespace Leviathan.Search;

/// <summary>What the media metadata cache knows about a file, as far as a search needs (<c>duration:</c>, <c>res:</c>, <c>codec:</c>).</summary>
/// <param name="Duration">Length, if known.</param>
/// <param name="Height">Height of the first video stream, if there is one.</param>
/// <param name="Codecs">Names of the video and audio codecs, in lower case.</param>
public sealed record MediaFacts(TimeSpan? Duration, int? Height, IReadOnlyList<string> Codecs);

/// <summary>
/// The facts about a file that cost more than reading its directory entry: tags, media information, text inside it. A matcher asks for them
/// only when every cheap condition has already matched, so a search over a share reads no tag streams for files it has already ruled out (spec §3, §13).
/// </summary>
public interface ISearchFacts
{
    /// <summary>The names of the colour tags on the item (spec §11); empty when none.</summary>
    ValueTask<IReadOnlyList<string>> GetTagsAsync(FileEntry entry, CancellationToken cancellationToken);

    /// <summary>The media information from the cache, or null when it is not known (the search does not probe files).</summary>
    ValueTask<MediaFacts?> GetMediaAsync(FileEntry entry, CancellationToken cancellationToken);

    /// <summary>True when the text occurs inside the file (spec §13.1 <c>content:</c>).</summary>
    ValueTask<bool> ContentContainsAsync(FileEntry entry, string text, CancellationToken cancellationToken);
}

/// <summary>Facts for a search that has none: no tags, no media information, no content.</summary>
public sealed class NoSearchFacts : ISearchFacts
{
    /// <summary>The shared instance.</summary>
    public static NoSearchFacts Instance { get; } = new();

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<string>> GetTagsAsync(FileEntry entry, CancellationToken cancellationToken) => ValueTask.FromResult<IReadOnlyList<string>>([]);

    /// <inheritdoc />
    public ValueTask<MediaFacts?> GetMediaAsync(FileEntry entry, CancellationToken cancellationToken) => ValueTask.FromResult<MediaFacts?>(null);

    /// <inheritdoc />
    public ValueTask<bool> ContentContainsAsync(FileEntry entry, string text, CancellationToken cancellationToken) => ValueTask.FromResult(false);
}
