using Leviathan.Core.Paths;
using Leviathan.ViewModels.Sync;

namespace Leviathan.App;

/// <summary>Compare and sync (spec §15.5): the window for two folders, normally the two panes of a split tab.</summary>
public sealed partial class MainWindow
{
    /// <inheritdoc />
    public void OpenSync(LocationPath? left, LocationPath? right) => OnUi(() =>
    {
        var host = App.Host;
        new SyncWindow(left, right, new WorkerCompare(() => host.WorkerConnection), new SyncRunner(host.Transfers, host.Transfers)).Activate();
    });
}
