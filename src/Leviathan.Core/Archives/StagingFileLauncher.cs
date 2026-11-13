using Leviathan.Core.Paths;
using Leviathan.Core.Platform;

namespace Leviathan.Core.Archives;

/// <summary>What the archive catalog found at a location.</summary>
/// <param name="InArchive">True when the location is inside an archive file; false means "not mine, ask the file system".</param>
/// <param name="Kind">File or folder when it exists inside the archive; null when nothing is there.</param>
public readonly record struct ArchiveProbe(bool InArchive, Model.FileEntryKind? Kind)
{
    /// <summary>The location is not inside an archive.</summary>
    public static ArchiveProbe NotInArchive { get; } = new(false, null);
}

/// <summary>Answers "what is at this location?" for locations inside archives, so the address bar can go there (spec §5.2, §15.4).</summary>
public interface IArchiveCatalog
{
    /// <summary>Looks at <paramref name="location"/>. Throws <see cref="ArchiveException"/> when the archive cannot be read.</summary>
    Task<ArchiveProbe> ProbeAsync(LocationPath location, CancellationToken cancellationToken);
}

/// <summary>
/// Opens things inside archives with the programs that open them anywhere else: the file is unpacked to the scratch folder first
/// (spec §15.4, "browse ... with preview"), then handed to the launcher that does the real work. Everything outside an archive passes
/// straight through. Properties and terminals make no sense inside an archive and say so. The unpacked file is a copy: changes made
/// to it are not written back to the archive.
/// </summary>
public sealed class StagingFileLauncher(IFileLauncher inner, IArchiveStager stager) : IFileLauncher
{
    /// <inheritdoc />
    public Task<LaunchResult> OpenAsync(LocationPath path, CancellationToken cancellationToken) =>
        StagedAsync(path, (p, ct) => inner.OpenAsync(p, ct), cancellationToken);

    /// <inheritdoc />
    public Task<LaunchResult> RunAsAdministratorAsync(LocationPath path, CancellationToken cancellationToken) =>
        StagedAsync(path, (p, ct) => inner.RunAsAdministratorAsync(p, ct), cancellationToken);

    /// <inheritdoc />
    public Task<LaunchResult> OpenWithAsync(LocationPath path, string executable, CancellationToken cancellationToken) =>
        StagedAsync(path, (p, ct) => inner.OpenWithAsync(p, executable, ct), cancellationToken);

    /// <inheritdoc />
    public Task<LaunchResult> ChooseAppAsync(LocationPath path, CancellationToken cancellationToken) =>
        StagedAsync(path, (p, ct) => inner.ChooseAppAsync(p, ct), cancellationToken);

    /// <inheritdoc />
    public Task<LaunchResult> OpenTerminalAsync(LocationPath folder, TerminalKind kind, CancellationToken cancellationToken) =>
        ArchiveKinds.IsInside(folder)
            ? Task.FromResult(new LaunchResult(false, "A terminal cannot be opened inside an archive."))
            : inner.OpenTerminalAsync(folder, kind, cancellationToken);

    /// <inheritdoc />
    public Task<LaunchResult> ShowPropertiesAsync(LocationPath path, CancellationToken cancellationToken) =>
        ArchiveKinds.IsInside(path)
            ? Task.FromResult(new LaunchResult(false, "Properties are not available for items inside an archive."))
            : inner.ShowPropertiesAsync(path, cancellationToken);

    private async Task<LaunchResult> StagedAsync(LocationPath path, Func<LocationPath, CancellationToken, Task<LaunchResult>> launch, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (!ArchiveKinds.IsInside(path))
        {
            return await launch(path, cancellationToken).ConfigureAwait(false);
        }

        string staged;
        try
        {
            staged = await stager.StageAsync(path, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return new LaunchResult(false, Cancelled: true);
        }
        catch (Exception ex) when (ex is ArchiveException or IOException or UnauthorizedAccessException)
        {
            return new LaunchResult(false, $"{path.Name} could not be unpacked: {ex.Message}");
        }

        return LocationPath.TryParse(staged, out var location)
            ? await launch(location, cancellationToken).ConfigureAwait(false)
            : new LaunchResult(false, $"{path.Name} could not be unpacked to a usable path.");
    }
}
