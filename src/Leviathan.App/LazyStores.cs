namespace Leviathan.App;

/// <summary>Opens the SQLite copy of the search index only when it is first used, so starting the app never opens the database for an index nobody asked for.</summary>
internal sealed class LazyNameIndexStore(Func<Leviathan.Search.INameIndexStore> create) : Leviathan.Search.INameIndexStore
{
    private readonly Lazy<Leviathan.Search.INameIndexStore> _store = new(create);

    public void Save(Leviathan.Search.IndexSnapshot snapshot) => _store.Value.Save(snapshot);

    public Leviathan.Search.IndexSnapshot? Load() => _store.Value.Load();
}

/// <summary>Opens the SQLite copy of the space map scans only when a scan is first saved or loaded.</summary>
internal sealed class LazySpaceScanStore(Func<Leviathan.SpaceMap.ISpaceScanStore> create) : Leviathan.SpaceMap.ISpaceScanStore
{
    private readonly Lazy<Leviathan.SpaceMap.ISpaceScanStore> _store = new(create);

    public void Save(Leviathan.SpaceMap.SpaceScanSnapshot snapshot) => _store.Value.Save(snapshot);

    public Leviathan.SpaceMap.SpaceScanSnapshot? Load(string root) => _store.Value.Load(root);

    public void Delete(string root) => _store.Value.Delete(root);
}
