using Leviathan.Core.Platform;
using Windows.Storage;

namespace Leviathan.Interop;

/// <summary>
/// Saves properties through the shell's property handler for the file type (<c>StorageFile.Properties</c>), so ID3, EXIF, MP4 and Office
/// properties are written in each format's own way (spec §9.2). The file's modified time is put back afterwards: editing a title is not
/// a change to the content, and folders sorted by date must not move.
/// </summary>
public sealed class ShellPropertyWriter : IPropertyWriter
{
    /// <inheritdoc />
    public async Task WriteAsync(string path, IReadOnlyDictionary<string, object> values, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(values);
        var before = File.GetLastWriteTimeUtc(path);
        var file = await StorageFile.GetFileFromPathAsync(path).AsTask(cancellationToken).ConfigureAwait(false);
        await file.Properties.SavePropertiesAsync(values.Select(v => new KeyValuePair<string, object>(v.Key, v.Value))).AsTask(cancellationToken).ConfigureAwait(false);
        try
        {
            File.SetLastWriteTimeUtc(path, before);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The edit is saved; only the date could not be put back.
        }
    }
}
