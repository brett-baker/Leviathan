using Leviathan.Core.Paths;

namespace Leviathan.ViewModels.Views;

/// <summary>How far a saved view reaches (spec §8.1).</summary>
public enum ViewScope
{
    /// <summary>Only the folder itself.</summary>
    ThisFolder,

    /// <summary>The folder and everything below it.</summary>
    ThisFolderAndSubfolders,

    /// <summary>Every folder of one <see cref="FolderKind"/>.</summary>
    AllFoldersOfType,
}

/// <summary>One saved view. Exactly one of <see cref="Location"/> (folder scopes) or <see cref="Kind"/> (type scope) applies.</summary>
/// <param name="Scope">Reach of the rule.</param>
/// <param name="Location">The folder, for folder scopes.</param>
/// <param name="Kind">The folder type, for <see cref="ViewScope.AllFoldersOfType"/>.</param>
/// <param name="Settings">The view to apply.</param>
public sealed record FolderViewRule(ViewScope Scope, LocationPath? Location, FolderKind? Kind, FolderViewSettings Settings);

/// <summary>
/// Remembers the view of each folder with inheritance (spec §8.1). Most specific wins: a rule for exactly this folder, then the
/// nearest ancestor's "and subfolders" rule, then the "all folders of this type" rule, then the global default.
/// </summary>
public sealed class FolderViewRules
{
    private readonly Dictionary<LocationPath, FolderViewSettings> _thisFolder = [];
    private readonly Dictionary<LocationPath, FolderViewSettings> _subtree = [];
    private readonly Dictionary<FolderKind, FolderViewSettings> _byKind = [];

    /// <summary>Raised when a rule is added or removed so the view can be re-resolved.</summary>
    public event EventHandler? Changed;

    /// <summary>The view used when no rule applies; changed by the "all folders" setting.</summary>
    public FolderViewSettings GlobalDefault { get; private set; } = FolderViewSettings.Default;

    /// <summary>Replaces the global default.</summary>
    public void SetGlobalDefault(FolderViewSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        GlobalDefault = settings;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Saves a view for a folder scope (<paramref name="scope"/> is not <see cref="ViewScope.AllFoldersOfType"/>).</summary>
    public void Set(LocationPath folder, ViewScope scope, FolderViewSettings settings)
    {
        ArgumentNullException.ThrowIfNull(folder);
        ArgumentNullException.ThrowIfNull(settings);
        switch (scope)
        {
            case ViewScope.ThisFolder:
                _thisFolder[folder] = settings;
                break;
            case ViewScope.ThisFolderAndSubfolders:
                _subtree[folder] = settings;
                break;
            default:
                throw new ArgumentException("Use SetForKind for the all-folders-of-type scope.", nameof(scope));
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Saves a view for every folder of <paramref name="kind"/>.</summary>
    public void SetForKind(FolderKind kind, FolderViewSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _byKind[kind] = settings;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Forgets the rules that apply to exactly <paramref name="folder"/> (both scopes). Returns true when something was removed.</summary>
    public bool Clear(LocationPath folder)
    {
        ArgumentNullException.ThrowIfNull(folder);
        var removed = _thisFolder.Remove(folder) | _subtree.Remove(folder);
        if (removed)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }

        return removed;
    }

    /// <summary>Resolves the view for <paramref name="folder"/>, whose content was detected as <paramref name="kind"/>.</summary>
    public FolderViewSettings Resolve(LocationPath folder, FolderKind kind)
    {
        ArgumentNullException.ThrowIfNull(folder);
        if (_thisFolder.TryGetValue(folder, out var exact))
        {
            return exact;
        }

        // The nearest ancestor (or the folder itself) with an "and subfolders" rule: walk up from the folder.
        for (var current = folder; current is not null; current = current.Parent)
        {
            if (_subtree.TryGetValue(current, out var inherited))
            {
                return inherited;
            }
        }

        return _byKind.TryGetValue(kind, out var byKind) ? byKind : GlobalDefault;
    }

    /// <summary>Every saved rule, for persistence.</summary>
    public IReadOnlyList<FolderViewRule> Snapshot() =>
    [
        .. _thisFolder.Select(p => new FolderViewRule(ViewScope.ThisFolder, p.Key, null, p.Value)),
        .. _subtree.Select(p => new FolderViewRule(ViewScope.ThisFolderAndSubfolders, p.Key, null, p.Value)),
        .. _byKind.Select(p => new FolderViewRule(ViewScope.AllFoldersOfType, null, p.Key, p.Value)),
    ];

    /// <summary>Replaces all rules with <paramref name="rules"/> (loading from disk). Malformed rules are skipped.</summary>
    public void Load(IEnumerable<FolderViewRule> rules)
    {
        ArgumentNullException.ThrowIfNull(rules);
        _thisFolder.Clear();
        _subtree.Clear();
        _byKind.Clear();
        foreach (var rule in rules)
        {
            switch (rule.Scope)
            {
                case ViewScope.ThisFolder when rule.Location is not null:
                    _thisFolder[rule.Location] = rule.Settings;
                    break;
                case ViewScope.ThisFolderAndSubfolders when rule.Location is not null:
                    _subtree[rule.Location] = rule.Settings;
                    break;
                case ViewScope.AllFoldersOfType when rule.Kind is not null:
                    _byKind[rule.Kind.Value] = rule.Settings;
                    break;
            }
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }
}
