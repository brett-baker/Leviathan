using Leviathan.Core.Model;
using Leviathan.Core.Paths;
using Leviathan.ViewModels.Triage;

namespace Leviathan.ViewModels.Tags;

/// <summary>What a tag change did to a selection.</summary>
/// <param name="Changed">How many files were changed.</param>
/// <param name="Message">One line for the status bar.</param>
/// <param name="Problems">Files that could not be changed, with the reason.</param>
public sealed record TagChange(int Changed, string Message, IReadOnlyList<string> Problems);

/// <summary>
/// Puts colour tags on the selection in the file view (spec §11.1: keys 1 to 9 toggle the first nine tags, Ctrl+0 clears them). A key
/// toggles as a group, the way Explorer-style checkboxes do: when every selected file already has the tag it comes off all of them,
/// otherwise it goes on all of them. Only files carry tags; folders in the selection are left alone. The tags are written through
/// <see cref="ITagStore"/>, which restores the modified time (rule 1) and fails loudly where a location cannot hold a tag, so a
/// failure is reported for that file and the rest still change. The caller runs this off the UI thread.
/// </summary>
public sealed class FileTagging(ITagStore store, Func<TagCatalog> catalog)
{
    /// <summary>Toggles the tag that key <paramref name="number"/> means on every file in <paramref name="entries"/>.</summary>
    public async Task<TagChange> ToggleAsync(IReadOnlyList<FileEntry> entries, int number, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var tag = catalog().ForKey(number);
        if (tag is null)
        {
            return new TagChange(0, $"There is no tag {number}.", []);
        }

        var files = entries.Where(e => e.Kind == FileEntryKind.File).ToList();
        if (files.Count == 0)
        {
            return new TagChange(0, "Tags go on files, not folders.", []);
        }

        var problems = new List<string>();
        var current = new Dictionary<FileEntry, IReadOnlySet<int>>();
        foreach (var file in files)
        {
            try
            {
                current[file] = await store.GetAsync(file.Location.ToWin32Path(), cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                problems.Add($"{file.Name}: {ex.Message}");
            }
        }

        var turnOn = current.Values.Any(tags => !tags.Contains(tag.Id));
        var changed = 0;
        foreach (var (file, tags) in current)
        {
            var next = new HashSet<int>(tags);
            if (turnOn ? !next.Add(tag.Id) : !next.Remove(tag.Id))
            {
                continue;
            }

            try
            {
                await store.SetAsync(file.Location.ToWin32Path(), next, cancellationToken).ConfigureAwait(false);
                changed++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                problems.Add($"{file.Name}: {ex.Message}");
            }
        }

        return new TagChange(changed, Describe(turnOn ? $"{tag.Name} on" : $"{tag.Name} off", changed, files, problems), problems);
    }

    /// <summary>Takes every tag off every file in <paramref name="entries"/> (Ctrl+0).</summary>
    public async Task<TagChange> ClearAsync(IReadOnlyList<FileEntry> entries, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var files = entries.Where(e => e.Kind == FileEntryKind.File).ToList();
        if (files.Count == 0)
        {
            return new TagChange(0, "Tags go on files, not folders.", []);
        }

        var problems = new List<string>();
        var changed = 0;
        foreach (var file in files)
        {
            try
            {
                var path = file.Location.ToWin32Path();
                if ((await store.GetAsync(path, cancellationToken).ConfigureAwait(false)).Count == 0)
                {
                    continue;
                }

                await store.SetAsync(path, new HashSet<int>(), cancellationToken).ConfigureAwait(false);
                changed++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                problems.Add($"{file.Name}: {ex.Message}");
            }
        }

        return new TagChange(changed, Describe("Tags cleared", changed, files, problems), problems);
    }

    private static string Describe(string what, int changed, List<FileEntry> files, List<string> problems)
    {
        var where = files.Count == 1 ? files[0].Name : $"{changed} of {files.Count} files";
        var text = $"{what}: {where}.";
        return problems.Count == 0 ? text : $"{text} {problems[0]}" + (problems.Count > 1 ? $" (+{problems.Count - 1} more)" : string.Empty);
    }
}
