namespace Leviathan.Core.Model;

/// <summary>Reads the stable identity of a file (spec §11.2); null where the file system has none or the file cannot be opened.</summary>
public interface IFileIdentitySource
{
    /// <summary>The identity of the file at <paramref name="path"/>, or null.</summary>
    FileIdentity? Get(string path);
}
