using Leviathan.Core.Paths;

namespace Leviathan.Core.Platform;

/// <summary>Whether a destination can hold the <c>:leviathan.tags</c> stream that colour tags live in (spec §11.2).</summary>
public interface IStreamSupport
{
    /// <summary>True when files in <paramref name="folder"/> can have named streams, false when they cannot, null when it cannot be told (an unreachable share).</summary>
    Task<bool?> SupportsStreamsAsync(LocationPath folder, CancellationToken cancellationToken);
}
