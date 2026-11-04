using System.IO.Abstractions;
using System.IO.Hashing;
using Leviathan.Contracts;

namespace Leviathan.Engine.Transfers;

/// <summary>How one finished copy is checked.</summary>
public enum VerifyMethod
{
    /// <summary>Not checked.</summary>
    None,

    /// <summary>Hash of the whole file on both sides.</summary>
    Full,

    /// <summary>Hash of the first and last blocks and some random blocks: cheap proof for very large files.</summary>
    Sampled,
}

/// <summary>The numbers of the verify policy (spec §3.5 item 4).</summary>
/// <param name="FullLimit">Files up to this size are hashed in full by default.</param>
/// <param name="EdgeBytes">Bytes checked at the start and at the end of a sampled file.</param>
/// <param name="RandomBlocks">Random blocks checked in the middle of a sampled file.</param>
/// <param name="BlockBytes">Size of each random block.</param>
public sealed record VerifyPolicy(long FullLimit = 4L * 1024 * 1024 * 1024, long EdgeBytes = 64L * 1024 * 1024, int RandomBlocks = 16, int BlockBytes = 4 * 1024 * 1024)
{
    /// <summary>
    /// Decides how to check one file. A move to another volume deletes its source afterwards, so it is always checked, whatever the job
    /// says (nothing is ever lost); a copy is checked when it touches a network location, or when the job asks for full checks.
    /// </summary>
    public VerifyMethod Decide(VerifyMode mode, bool move, bool networkInvolved, long length)
    {
        if (mode == VerifyMode.Full)
        {
            return VerifyMethod.Full;
        }

        if (mode == VerifyMode.Off && !move)
        {
            return VerifyMethod.None;
        }

        if (!move && !networkInvolved)
        {
            return VerifyMethod.None;
        }

        return length <= FullLimit ? VerifyMethod.Full : VerifyMethod.Sampled;
    }
}

/// <summary>Compares a finished copy with its source (spec §12.1 step 6).</summary>
public interface IFileVerifier
{
    /// <summary>
    /// True when <paramref name="target"/> matches <paramref name="source"/> by <paramref name="method"/>. With a <paramref name="sourceHash"/>
    /// from the copy itself, a full check reads only the target. A size difference always fails.
    /// </summary>
    Task<bool> VerifyAsync(string source, string target, VerifyMethod method, byte[]? sourceHash, CancellationToken cancellationToken);
}

/// <summary>xxHash3 verification over <see cref="IFileSystem"/> (fast enough that the link, not the CPU, sets the time).</summary>
public sealed class HashFileVerifier : IFileVerifier
{
    private const int Chunk = 1024 * 1024;

    private readonly IFileSystem _fs;
    private readonly VerifyPolicy _policy;

    /// <summary>Creates the verifier.</summary>
    public HashFileVerifier(IFileSystem fileSystem, VerifyPolicy? policy = null)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        _fs = fileSystem;
        _policy = policy ?? new VerifyPolicy();
    }

    /// <inheritdoc />
    public async Task<bool> VerifyAsync(string source, string target, VerifyMethod method, byte[]? sourceHash, CancellationToken cancellationToken)
    {
        var length = _fs.FileInfo.New(source).Length;
        if (_fs.FileInfo.New(target).Length != length)
        {
            return false;
        }

        switch (method)
        {
            case VerifyMethod.None:
                return true;
            case VerifyMethod.Full:
                var expected = sourceHash ?? await HashAsync(source, 0, length, cancellationToken).ConfigureAwait(false);
                var actual = await HashAsync(target, 0, length, cancellationToken).ConfigureAwait(false);
                return expected.AsSpan().SequenceEqual(actual);
            default:
                foreach (var (offset, count) in SampleRegions(length, target))
                {
                    var a = await HashAsync(source, offset, count, cancellationToken).ConfigureAwait(false);
                    var b = await HashAsync(target, offset, count, cancellationToken).ConfigureAwait(false);
                    if (!a.AsSpan().SequenceEqual(b))
                    {
                        return false;
                    }
                }

                return true;
        }
    }

    /// <summary>The regions a sampled check reads: both ends, and random blocks chosen the same way every time for the same file.</summary>
    public IReadOnlyList<(long Offset, long Count)> SampleRegions(long length, string seedText)
    {
        var regions = new List<(long, long)>();
        if (length <= 0)
        {
            return regions;
        }

        var edge = Math.Min(_policy.EdgeBytes, length);
        regions.Add((0, edge));
        if (length > edge)
        {
            regions.Add((length - edge, edge));
        }

        var block = Math.Min(_policy.BlockBytes, length);
        var random = new Random(unchecked((int)length ^ StableHash(seedText)));
        for (var i = 0; i < _policy.RandomBlocks; i++)
        {
            regions.Add((random.NextInt64(0, length - block + 1), block));
        }

        return regions;
    }

    private static int StableHash(string text)
    {
        var hash = 17;
        foreach (var c in text)
        {
            hash = unchecked((hash * 31) + c);
        }

        return hash;
    }

    private async Task<byte[]> HashAsync(string path, long offset, long count, CancellationToken ct)
    {
        using var stream = _fs.FileStream.New(path, FileMode.Open, FileAccess.Read, FileShare.Read, 0, FileOptions.SequentialScan);
        stream.Seek(offset, SeekOrigin.Begin);
        var hash = new XxHash3();
        var buffer = new byte[(int)Math.Min(Chunk, Math.Max(count, 1))];
        for (var left = count; left > 0;)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, left)), ct).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            hash.Append(buffer.AsSpan(0, read));
            left -= read;
        }

        return hash.GetCurrentHash();
    }
}
