using Leviathan.Core.Paths;

namespace Leviathan.App;

/// <summary>Checksums (spec §15.4): the window for the items chosen, computed or verified in the worker.</summary>
public sealed partial class MainWindow
{
    /// <inheritdoc />
    public void OpenChecksums(IReadOnlyList<LocationPath> items, bool verify) => OnUi(() =>
        new ChecksumWindow(items, verify, new WorkerChecksums(() => App.Host.WorkerConnection)).Activate());
}
