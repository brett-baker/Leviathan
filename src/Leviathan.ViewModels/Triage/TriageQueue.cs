using Leviathan.Core.Model;
using Leviathan.Core.Paths;
using Leviathan.Core.Providers;
using Leviathan.ViewModels.Preview;

namespace Leviathan.ViewModels.Triage;

/// <summary>
/// Builds the list of files a triage session goes through (spec §10.1): the selected videos, or every video in a folder (and its
/// sub-folders when asked), in the order given or the one chosen, optionally only those with no Keep or Discard decision yet. Folder
/// listing streams from the provider and is cancellable, so a 30,000-file inbox never blocks the window.
/// </summary>
public static class TriageQueue
{
    /// <summary>The most files a queue holds; a bigger inbox is worked in sessions of N (spec §10.1, slice E5).</summary>
    public const int MaxItems = 200_000;

    /// <summary>The videos among <paramref name="selection"/>, in the order given, minus the triaged ones when <paramref name="options"/> says so.</summary>
    /// <param name="selection">The selected entries.</param>
    /// <param name="options">How to filter.</param>
    /// <param name="isTriaged">True when a file already has a Keep or Discard decision (the tag index answers this).</param>
    public static IReadOnlyList<FileEntry> FromSelection(IReadOnlyList<FileEntry> selection, TriageOptions options, Func<FileEntry, bool>? isTriaged = null)
    {
        ArgumentNullException.ThrowIfNull(selection);
        ArgumentNullException.ThrowIfNull(options);
        return [.. selection.Where(e => IsVideo(e) && !(options.UntriagedOnly && isTriaged?.Invoke(e) == true)).Take(MaxItems)];
    }

    /// <summary>
    /// Every video in <paramref name="folder"/> (and below it when <see cref="TriageOptions.Recursive"/>), in <paramref name="order"/> (name,
    /// ignoring case, by default). Links are not followed, so a loop of junctions cannot make the walk endless. A sub-folder that cannot be
    /// read is skipped and reported through <paramref name="skipped"/>; the walk goes on.
    /// </summary>
    public static async Task<IReadOnlyList<FileEntry>> FromFolderAsync(
        ILocationProvider provider,
        LocationPath folder,
        TriageOptions options,
        Func<FileEntry, bool>? isTriaged,
        IComparer<FileEntry>? order,
        Action<LocationPath, Exception>? skipped,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(options);
        var found = new List<FileEntry>();
        var pending = new Queue<LocationPath>();
        pending.Enqueue(folder);
        var first = true;
        while (pending.Count > 0 && found.Count < MaxItems)
        {
            var current = pending.Dequeue();
            try
            {
                await foreach (var batch in provider.ListAsync(current, ListingOptions.Default, cancellationToken).ConfigureAwait(false))
                {
                    foreach (var entry in batch.Items)
                    {
                        if (entry.Kind == FileEntryKind.Folder && options.Recursive)
                        {
                            pending.Enqueue(entry.Location);
                        }
                        else if (IsVideo(entry) && !(options.UntriagedOnly && isTriaged?.Invoke(entry) == true) && found.Count < MaxItems)
                        {
                            found.Add(entry);
                        }
                    }
                }
            }
            catch (LocationException ex) when (!first)
            {
                // One unreadable sub-folder must not end the walk; the folder the user chose failing is the caller's error.
                skipped?.Invoke(current, ex);
            }

            first = false;
        }

        found.Sort(order ?? ByName);
        return found;
    }

    /// <summary>Name, ignoring case, then path: a stable default order.</summary>
    public static IComparer<FileEntry> ByName { get; } = Comparer<FileEntry>.Create((a, b) =>
    {
        var byName = StringComparer.OrdinalIgnoreCase.Compare(a.Name, b.Name);
        return byName != 0 ? byName : StringComparer.OrdinalIgnoreCase.Compare(a.Location.Value, b.Location.Value);
    });

    private static bool IsVideo(FileEntry entry) => entry.Kind == FileEntryKind.File && PreviewClassifier.Classify(entry) == PreviewKind.Video;
}
