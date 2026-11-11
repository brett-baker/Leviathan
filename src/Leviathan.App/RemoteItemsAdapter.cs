using Leviathan.Core.Paths;
using Leviathan.Providers.Remote;
using Leviathan.ViewModels.Folders;

namespace Leviathan.App;

/// <summary>Renames and makes folders on SFTP, FTP and FTPS servers for the window, through the shared connection (spec §7.1).</summary>
internal sealed class RemoteItemsAdapter(RemoteLocationProvider provider) : IRemoteItems
{
    public bool Handles(LocationPath location) => location.Kind is LocationKind.Sftp or LocationKind.Ftp or LocationKind.Ftps;

    public async Task<bool> ExistsAsync(LocationPath location, CancellationToken cancellationToken) =>
        await provider.StatAsync(location, cancellationToken).ConfigureAwait(false) is not null;

    public Task RenameAsync(LocationPath from, LocationPath to, CancellationToken cancellationToken) => provider.RenameAsync(from, to, cancellationToken);

    public Task CreateFolderAsync(LocationPath location, CancellationToken cancellationToken) => provider.CreateFolderAsync(location, cancellationToken);
}
