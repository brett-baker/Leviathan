using Leviathan.Core.Model;
using Leviathan.Core.Paths;
using Leviathan.Core.Providers;
using Leviathan.Core.Text;

namespace Leviathan.ViewModels.Navigation;

/// <summary>Where a suggestion came from.</summary>
public enum SuggestionSource
{
    /// <summary>A subfolder of the directory being typed.</summary>
    Folder,

    /// <summary>A recently visited location.</summary>
    History,

    /// <summary>A pinned favorite.</summary>
    Favorite,
}

/// <summary>One autocomplete entry.</summary>
/// <param name="Text">Full text to put in the address bar.</param>
/// <param name="Source">Where it came from.</param>
public sealed record AddressSuggestion(string Text, SuggestionSource Source);

/// <summary>Lists subfolders for the address bar. The real implementation streams from the location provider.</summary>
public interface IFolderLister
{
    /// <summary>Returns the folders directly below <paramref name="location"/>, or an empty list when it cannot be listed.</summary>
    Task<IReadOnlyList<LocationPath>> ListFoldersAsync(LocationPath location, CancellationToken cancellationToken);
}

/// <summary><see cref="IFolderLister"/> over the location providers. Failures give an empty list: autocomplete must never throw.</summary>
public sealed class ProviderFolderLister : IFolderLister
{
    private readonly LocationProviderRegistry _providers;

    /// <summary>Creates a lister over <paramref name="providers"/>.</summary>
    public ProviderFolderLister(LocationProviderRegistry providers)
    {
        ArgumentNullException.ThrowIfNull(providers);
        _providers = providers;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<LocationPath>> ListFoldersAsync(LocationPath location, CancellationToken cancellationToken)
    {
        var folders = new List<LocationPath>();
        try
        {
            var provider = _providers.Resolve(location);
            await foreach (var batch in provider.ListAsync(location, ListingOptions.Default, cancellationToken).ConfigureAwait(false))
            {
                folders.AddRange(batch.Items.Where(i => i.Kind == FileEntryKind.Folder).Select(i => i.Location));
            }
        }
        catch (LocationException)
        {
            return [];
        }

        return folders;
    }
}

/// <summary>
/// Autocomplete for the address bar (spec §5.2): subfolders of the directory part of the text, then history and favorites,
/// de-duplicated and capped. Tab accepts the longest prefix all suggestions share.
/// </summary>
public sealed class AddressCompleter
{
    /// <summary>Most suggestions returned.</summary>
    public const int MaxSuggestions = 12;

    private readonly IFolderLister _folders;

    /// <summary>Creates a completer.</summary>
    public AddressCompleter(IFolderLister folders)
    {
        ArgumentNullException.ThrowIfNull(folders);
        _folders = folders;
    }

    /// <summary>Suggests completions for <paramref name="text"/>.</summary>
    public async Task<IReadOnlyList<AddressSuggestion>> SuggestAsync(
        string text,
        LocationPath? current,
        IEnumerable<LocationPath> history,
        IEnumerable<LocationPath> favorites,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(history);
        ArgumentNullException.ThrowIfNull(favorites);
        var suggestions = new List<AddressSuggestion>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var split = Math.Max(text.LastIndexOf('\\'), text.LastIndexOf('/'));
        if (split >= 0)
        {
            var directoryText = text[..(split + 1)];
            var prefix = text[(split + 1)..];
            if (LocationPath.TryResolve(directoryText, current, out var directory, out _))
            {
                var children = await _folders.ListFoldersAsync(directory, cancellationToken).ConfigureAwait(false);
                foreach (var child in children.OrderBy(c => c.Name, NaturalStringComparer.Instance))
                {
                    if (child.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && seen.Add(child.Value))
                    {
                        suggestions.Add(new AddressSuggestion(child.Value, SuggestionSource.Folder));
                    }
                }
            }
        }

        AddMatching(suggestions, seen, text, favorites, SuggestionSource.Favorite);
        AddMatching(suggestions, seen, text, history, SuggestionSource.History);
        return suggestions.Count > MaxSuggestions ? suggestions[..MaxSuggestions] : suggestions;
    }

    /// <summary>The longest prefix shared by every suggestion (ignoring case), for Tab. Empty when there are none.</summary>
    public static string CommonPrefix(IReadOnlyList<AddressSuggestion> suggestions)
    {
        ArgumentNullException.ThrowIfNull(suggestions);
        if (suggestions.Count == 0)
        {
            return string.Empty;
        }

        var first = suggestions[0].Text;
        var length = first.Length;
        foreach (var suggestion in suggestions)
        {
            var i = 0;
            var max = Math.Min(length, suggestion.Text.Length);
            while (i < max && char.ToUpperInvariant(first[i]) == char.ToUpperInvariant(suggestion.Text[i]))
            {
                i++;
            }

            length = i;
        }

        return first[..length];
    }

    private static void AddMatching(List<AddressSuggestion> suggestions, HashSet<string> seen, string text, IEnumerable<LocationPath> source, SuggestionSource kind)
    {
        if (text.Length == 0)
        {
            return;
        }

        foreach (var location in source)
        {
            if (location.Value.StartsWith(text, StringComparison.OrdinalIgnoreCase) && !location.Value.Equals(text, StringComparison.OrdinalIgnoreCase) && seen.Add(location.Value))
            {
                suggestions.Add(new AddressSuggestion(location.Value, kind));
            }
        }
    }
}
