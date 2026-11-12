namespace Leviathan.Core.Duplicates;

/// <summary>The hard-link work of the duplicate clean-up (spec §15.2), behind a seam so it is tested with a fake and done by the file system in the app.</summary>
public interface IHardLinker
{
    /// <summary>
    /// Makes <paramref name="extraPath"/> a hard link to <paramref name="keepPath"/>, replacing the file there without ever leaving the
    /// path missing (link beside it, then swap). Returns null on success, or one sentence saying why not (another volume, a file system
    /// without links, the file in use) with everything as it was.
    /// </summary>
    Task<string?> ReplaceWithLinkAsync(string keepPath, string extraPath, CancellationToken cancellationToken);

    /// <summary>Gives <paramref name="path"/> its own copy of the contents again (the undo of a link). Returns null on success or one sentence.</summary>
    Task<string?> MakeIndependentAsync(string path, CancellationToken cancellationToken);
}
