using Leviathan.ViewModels.Sessions;

namespace Leviathan.ViewModels.Views;

/// <summary>The saved folder views as stored on disk (<c>viewrules.json</c>).</summary>
/// <param name="SchemaVersion">Version of the layout.</param>
/// <param name="Rules">Every rule, for folders and for folder types.</param>
public sealed record ViewRulesFile(int SchemaVersion, IReadOnlyList<FolderViewRule> Rules)
{
    /// <summary>The newest layout this build writes.</summary>
    public const int CurrentSchemaVersion = 1;
}

/// <summary>
/// Keeps <see cref="FolderViewRules"/> between runs (spec §8.1: "the chosen set is saved with the folder's view settings"): reads
/// them at startup and writes them after every change. Saving never blocks the caller and a failed write only loses that save.
/// </summary>
public sealed class ViewRulesPersistence : IDisposable
{
    private readonly FolderViewRules _rules;
    private readonly JsonFileStore<ViewRulesFile> _store;
    private readonly Lock _gate = new();
    private Task _write = Task.CompletedTask;
    private bool _loading;

    /// <summary>Creates the persistence for <paramref name="rules"/>.</summary>
    public ViewRulesPersistence(FolderViewRules rules, JsonFileStore<ViewRulesFile> store)
    {
        ArgumentNullException.ThrowIfNull(rules);
        ArgumentNullException.ThrowIfNull(store);
        _rules = rules;
        _store = store;
    }

    /// <summary>The last queued write; tests await it.</summary>
    public Task Saved
    {
        get
        {
            lock (_gate)
            {
                return _write;
            }
        }
    }

    /// <summary>Reads the saved rules into the live set. A missing or unreadable file means no saved rules.</summary>
    public async Task LoadAsync(CancellationToken cancellationToken)
    {
        var file = await _store.LoadAsync(cancellationToken).ConfigureAwait(false);
        if (file?.Rules is null)
        {
            return;
        }

        _loading = true;
        try
        {
            _rules.Load(file.Rules);
        }
        finally
        {
            _loading = false;
        }
    }

    /// <summary>Starts writing after every change to the rules.</summary>
    public void Start() => _rules.Changed += OnChanged;

    /// <inheritdoc />
    public void Dispose() => _rules.Changed -= OnChanged;

    private void OnChanged(object? sender, EventArgs e)
    {
        if (_loading)
        {
            return;
        }

        var file = new ViewRulesFile(ViewRulesFile.CurrentSchemaVersion, _rules.Snapshot());
        lock (_gate)
        {
            _write = _write.ContinueWith(_ => WriteAsync(file), CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default).Unwrap();
        }
    }

    private async Task WriteAsync(ViewRulesFile file)
    {
        try
        {
            await _store.SaveAsync(file, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The view still applies for this run; it is just not remembered.
        }
    }
}
