namespace Leviathan.Core.Platform;

/// <summary>Writes Windows properties of a file through the shell's property handler (the seam to <c>StorageFile.Properties</c>).</summary>
public interface IPropertyWriter
{
    /// <summary>Saves <paramref name="values"/> (property name to value) into the file at <paramref name="path"/>.</summary>
    Task WriteAsync(string path, IReadOnlyDictionary<string, object> values, CancellationToken cancellationToken);
}
