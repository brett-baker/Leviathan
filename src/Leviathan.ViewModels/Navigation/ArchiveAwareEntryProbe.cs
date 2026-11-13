using Leviathan.Core.Archives;
using Leviathan.Core.Model;
using Leviathan.Core.Paths;
using Leviathan.Core.Providers;

namespace Leviathan.ViewModels.Navigation;

/// <summary>
/// An entry probe that also knows the insides of archives (spec §5.2, §15.4): a path below an archive file is asked of the archive,
/// anything else of the probe it wraps. A damaged or password-protected archive fails the way a listing of it would, so the address
/// bar shows the reason instead of "not found".
/// </summary>
public sealed class ArchiveAwareEntryProbe(IEntryProbe inner, IArchiveCatalog catalog) : IEntryProbe
{
    /// <inheritdoc />
    public async Task<FileEntryKind?> GetKindAsync(LocationPath location, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(location);
        if (!ArchiveKinds.IsInside(location))
        {
            return await inner.GetKindAsync(location, cancellationToken).ConfigureAwait(false);
        }

        try
        {
            var probe = await catalog.ProbeAsync(location, cancellationToken).ConfigureAwait(false);
            return probe.InArchive ? probe.Kind : await inner.GetKindAsync(location, cancellationToken).ConfigureAwait(false);
        }
        catch (ArchiveException ex)
        {
            throw new LocationException(ex.Error is ArchiveError.PasswordRequired or ArchiveError.WrongPassword ? LocationError.AuthenticationRequired : LocationError.Unknown, location, ex.Message, ex);
        }
    }
}
