using Leviathan.Core.Model;
using Leviathan.Search;
using Leviathan.ViewModels.Folders;
using Leviathan.ViewModels.Tags;
using Leviathan.ViewModels.Triage;

namespace Leviathan.ViewModels.Searches;

/// <summary>
/// The tags and media information a search can use (spec §13.1), taken from what the app already keeps: the tag store and the media cache.
/// Media is read from the cache only: a search never probes files, so <c>duration:</c>, <c>res:</c> and <c>codec:</c> match the files that
/// have been looked at (spec §9.3), and the status line says so. Content search is not offered by this source yet.
/// </summary>
/// <param name="tags">Reads the tags of one file.</param>
/// <param name="catalog">Gives tag ids their names; read on each use because tags can be renamed.</param>
/// <param name="media">The media cache; null when the media process is not available.</param>
public sealed class AppSearchFacts(ITagStore tags, Func<TagCatalog> catalog, IMediaInfoSource? media) : ISearchFacts
{
    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<string>> GetTagsAsync(FileEntry entry, CancellationToken cancellationToken)
    {
        var ids = await tags.GetAsync(entry.Location.ToString(), cancellationToken).ConfigureAwait(false);
        if (ids.Count == 0)
        {
            return [];
        }

        var names = catalog().Tags;
        return [.. names.Where(t => ids.Contains(t.Id)).Select(t => t.Name)];
    }

    /// <inheritdoc />
    public async ValueTask<MediaFacts?> GetMediaAsync(FileEntry entry, CancellationToken cancellationToken)
    {
        if (media is null || entry.Kind != FileEntryKind.File)
        {
            return null;
        }

        var file = new MediaFileRef(entry.Location.ToString(), entry.Size, entry.Modified.UtcTicks, entry.Location.IsNetwork);
        var found = await media.PeekAsync([file], cancellationToken).ConfigureAwait(false);
        if (!found.TryGetValue(file.Path, out var result) || result.State != MediaInfoState.Ok || result.Info is not { } info)
        {
            return null;
        }

        var codecs = info.Video.Select(v => v.Codec).Concat(info.Audio.Select(a => a.Codec)).OfType<string>().Select(c => c.ToLowerInvariant()).Distinct(StringComparer.Ordinal).ToList();
        return new MediaFacts(info.Duration, info.PrimaryVideo?.Height, codecs);
    }

    /// <inheritdoc />
    public ValueTask<bool> ContentContainsAsync(FileEntry entry, string text, CancellationToken cancellationToken) => ValueTask.FromResult(false);
}
