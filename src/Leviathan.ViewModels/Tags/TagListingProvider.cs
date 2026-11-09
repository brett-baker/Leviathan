using System.Runtime.CompilerServices;
using Leviathan.Core.Model;
using Leviathan.Core.Paths;
using Leviathan.Core.Providers;

namespace Leviathan.ViewModels.Tags;

/// <summary>The virtual folder of one colour tag, <c>shell:tag\3</c> (spec §7, §11.2): where the Tags section of the sidebar goes.</summary>
public static class TagLocations
{
    /// <summary>The root of all tag folders.</summary>
    public const string Root = "shell:tag";

    /// <summary>The folder that lists every file carrying tag <paramref name="tagId"/>.</summary>
    public static LocationPath For(int tagId) => LocationPath.Parse(Root + "\\" + tagId.ToString(System.Globalization.CultureInfo.InvariantCulture));

    /// <summary>The tag a folder belongs to, or null when <paramref name="location"/> is not a tag folder.</summary>
    public static int? TagIdOf(LocationPath location)
    {
        ArgumentNullException.ThrowIfNull(location);
        return IsTagLocation(location) && location.Segments.Count == 1
            && int.TryParse(location.Segments[0], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var id)
            ? id
            : null;
    }

    /// <summary>True for <c>shell:tag</c> and anything below it.</summary>
    public static bool IsTagLocation(LocationPath location)
    {
        ArgumentNullException.ThrowIfNull(location);
        return location.Kind == LocationKind.Shell && string.Equals(location.Root, Root, StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>
/// Lists the files that carry a tag, wherever they are, from the tag index (spec §11.2). The files are the real ones: opening, copying
/// or tagging them works as in their own folder. Files that no longer exist are left out. The lookup and the stat calls run off the
/// caller's thread, in batches.
/// </summary>
/// <param name="index">The tag index (opened on first use).</param>
/// <param name="stat">Describes the file at a path, or returns null when it is gone or unreadable.</param>
public sealed class TagListingProvider(Lazy<ITagIndex> index, Func<string, FileEntry?> stat) : ILocationProvider
{
    /// <inheritdoc />
    public string Name => "tags";

    /// <inheritdoc />
    public bool CanHandle(LocationPath location) => TagLocations.IsTagLocation(location);

    /// <inheritdoc />
    public async IAsyncEnumerable<ListingBatch> ListAsync(LocationPath location, ListingOptions options, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(location);
        ArgumentNullException.ThrowIfNull(options);
        if (TagLocations.TagIdOf(location) is not { } id)
        {
            yield return new ListingBatch([], true);
            yield break;
        }

        var paths = await Task.Run(() => index.Value.PathsWithTag(id), cancellationToken).ConfigureAwait(false);
        var batch = new List<FileEntry>();
        foreach (var path in paths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var entry = await Task.Run(() => stat(path), cancellationToken).ConfigureAwait(false);
            if (entry is null || entry.Kind != FileEntryKind.File || !options.Includes(entry))
            {
                continue;
            }

            batch.Add(entry);
            if (batch.Count >= options.BatchSize)
            {
                yield return new ListingBatch(batch, false);
                batch = [];
            }
        }

        yield return new ListingBatch(batch, true);
    }
}
