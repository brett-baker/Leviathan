using Leviathan.Core.Platform;
using Leviathan.Providers.Local;

namespace Leviathan.App;

/// <summary>
/// Thumbnails of files on network drives only when the user wants them (Quick settings): each one means reading the file over the
/// network. A file that is skipped gets no thumbnail, which the view draws as its icon, exactly as for a file without one.
/// </summary>
internal sealed class NetworkThumbnailGate(IThumbnailSource inner) : IThumbnailSource
{
    /// <summary>False to skip files on network drives.</summary>
    public bool AllowNetwork { get; set; } = true;

    /// <inheritdoc />
    public Task<ThumbnailImage?> CreateAsync(ThumbnailRequest request, CancellationToken cancellationToken) =>
        !AllowNetwork && (request.Location.IsNetwork || MappedDrives.IsMappedDrive(request.Location))
            ? Task.FromResult<ThumbnailImage?>(null)
            : inner.CreateAsync(request, cancellationToken);
}
