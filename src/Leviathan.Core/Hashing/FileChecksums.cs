using System.Security.Cryptography;

namespace Leviathan.Core.Hashing;

/// <summary>The checksums the details pane can compute on demand (spec §9.2 "Integrity").</summary>
public enum ChecksumKind
{
    /// <summary>MD5 (32 hex digits); for comparing against published sums, not for security.</summary>
    Md5,

    /// <summary>SHA-1.</summary>
    Sha1,

    /// <summary>SHA-256.</summary>
    Sha256,
}

/// <summary>
/// Computes several checksums of one stream in a single pass (spec §9.2): reading a 6 GB file from a share once costs the same however
/// many sums are wanted. Reads in 1 MiB blocks off the calling thread's context, reports bytes read, and stops at the next block when
/// cancelled, so a huge file never holds the pane.
/// </summary>
public static class FileChecksums
{
    private const int Block = 1024 * 1024;

    /// <summary>Reads <paramref name="stream"/> to the end and returns each requested checksum as lowercase hex.</summary>
    /// <param name="stream">The data; read from its current position.</param>
    /// <param name="kinds">Which sums to compute.</param>
    /// <param name="progress">Called with the total bytes read so far after each block; may be null.</param>
    /// <param name="cancellationToken">Stops the read.</param>
    public static async Task<IReadOnlyDictionary<ChecksumKind, string>> ComputeAsync(Stream stream, IReadOnlyCollection<ChecksumKind> kinds, IProgress<long>? progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(kinds);
        var hashes = kinds.Distinct().ToDictionary(k => k, k => IncrementalHash.CreateHash(k switch
        {
            ChecksumKind.Md5 => HashAlgorithmName.MD5,
            ChecksumKind.Sha1 => HashAlgorithmName.SHA1,
            _ => HashAlgorithmName.SHA256,
        }));
        try
        {
            var buffer = new byte[Block];
            long total = 0;
            int read;
            while ((read = await stream.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false)) > 0)
            {
                foreach (var hash in hashes.Values)
                {
                    hash.AppendData(buffer, 0, read);
                }

                total += read;
                progress?.Report(total);
            }

            return hashes.ToDictionary(p => p.Key, p => Convert.ToHexStringLower(p.Value.GetHashAndReset()));
        }
        finally
        {
            foreach (var hash in hashes.Values)
            {
                hash.Dispose();
            }
        }
    }
}
