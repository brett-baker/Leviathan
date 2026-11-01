using Leviathan.Core.Paths;

namespace Leviathan.Core.Platform;

/// <summary>How a thumbnail should be produced.</summary>
public enum ThumbnailMode
{
    /// <summary>The content thumbnail if there is one, else the file-type icon (the default for the file view).</summary>
    Thumbnail,

    /// <summary>Only the file-type icon; never opens the file. Used for network files while browsing.</summary>
    IconOnly,

    /// <summary>Only what Windows already has cached; never generates. Null when nothing is cached.</summary>
    CachedOnly,
}

/// <summary>Pixels of a thumbnail: top-down 32-bit BGRA with premultiplied alpha, ready for a WinUI <c>WriteableBitmap</c>.</summary>
/// <param name="Width">Width in pixels.</param>
/// <param name="Height">Height in pixels.</param>
/// <param name="Bgra">Pixel data, <c>Width * Height * 4</c> bytes.</param>
public sealed record ThumbnailImage(int Width, int Height, ReadOnlyMemory<byte> Bgra)
{
    /// <summary>Memory the image holds, for the cache's size cap.</summary>
    public long SizeBytes => Bgra.Length;
}

/// <summary>What to produce a thumbnail for. Size and modified time are part of the identity: an edited file gets a new thumbnail.</summary>
/// <param name="Location">The file or folder.</param>
/// <param name="Modified">Last write time, so a changed file is not served a stale image.</param>
/// <param name="Length">Size in bytes, same purpose.</param>
/// <param name="Size">Edge of the square thumbnail in pixels.</param>
/// <param name="Mode">How to produce it.</param>
public readonly record struct ThumbnailRequest(LocationPath Location, DateTimeOffset Modified, long Length, int Size, ThumbnailMode Mode = ThumbnailMode.Thumbnail);

/// <summary>Produces a thumbnail from the operating system. Implementations run shell code off the UI thread and honour cancellation before starting work.</summary>
public interface IThumbnailSource
{
    /// <summary>Returns the image, or null when the file has none. Throws for failures (the cache remembers them).</summary>
    Task<ThumbnailImage?> CreateAsync(ThumbnailRequest request, CancellationToken cancellationToken);
}
