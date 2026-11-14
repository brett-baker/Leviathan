using System.Globalization;
using System.IO.Hashing;
using System.Security.Cryptography;

namespace Leviathan.Core.Hashing;

/// <summary>The checksums Leviathan can compute (spec §9.2 "Integrity", §15.4 "Checksums").</summary>
public enum ChecksumKind
{
    /// <summary>MD5 (32 hex digits); for comparing against published sums, not for security.</summary>
    Md5,

    /// <summary>SHA-1 (40 hex digits).</summary>
    Sha1,

    /// <summary>SHA-256 (64 hex digits).</summary>
    Sha256,

    /// <summary>SHA-512 (128 hex digits).</summary>
    Sha512,

    /// <summary>xxHash3, 64 bit (16 hex digits): very fast, the sum Leviathan itself uses to verify copies (spec §3.5).</summary>
    XxHash3,

    /// <summary>CRC-32 (8 hex digits), as zip files and .sfv files use.</summary>
    Crc32,
}

/// <summary>What is known about each <see cref="ChecksumKind"/>: its name, the file extension of its sidecar files, and its length.</summary>
public static class ChecksumKinds
{
    /// <summary>Every kind, in the order the user interface lists them.</summary>
    public static IReadOnlyList<ChecksumKind> All { get; } = [ChecksumKind.Md5, ChecksumKind.Sha1, ChecksumKind.Sha256, ChecksumKind.Sha512, ChecksumKind.XxHash3, ChecksumKind.Crc32];

    /// <summary>The name shown to people: "SHA-256".</summary>
    public static string Title(ChecksumKind kind) => kind switch
    {
        ChecksumKind.Md5 => "MD5",
        ChecksumKind.Sha1 => "SHA-1",
        ChecksumKind.Sha256 => "SHA-256",
        ChecksumKind.Sha512 => "SHA-512",
        ChecksumKind.XxHash3 => "xxHash3",
        _ => "CRC32",
    };

    /// <summary>The extension of a checksum file for this kind, with its dot: ".sha256".</summary>
    public static string Extension(ChecksumKind kind) => kind switch
    {
        ChecksumKind.Md5 => ".md5",
        ChecksumKind.Sha1 => ".sha1",
        ChecksumKind.Sha256 => ".sha256",
        ChecksumKind.Sha512 => ".sha512",
        ChecksumKind.XxHash3 => ".xxh3",
        _ => ".crc32",
    };

    /// <summary>How many hex digits a sum of this kind has.</summary>
    public static int HexLength(ChecksumKind kind) => kind switch
    {
        ChecksumKind.Md5 => 32,
        ChecksumKind.Sha1 => 40,
        ChecksumKind.Sha256 => 64,
        ChecksumKind.Sha512 => 128,
        ChecksumKind.XxHash3 => 16,
        _ => 8,
    };

    /// <summary>The kind a checksum file's extension names (".sha256", ".md5", ".sfv" for CRC32), or null when it is none.</summary>
    public static ChecksumKind? FromExtension(string extension)
    {
        var e = extension.ToLowerInvariant();
        return e switch
        {
            ".md5" or ".md5sum" => ChecksumKind.Md5,
            ".sha1" or ".sha1sum" => ChecksumKind.Sha1,
            ".sha256" or ".sha256sum" => ChecksumKind.Sha256,
            ".sha512" or ".sha512sum" => ChecksumKind.Sha512,
            ".xxh3" or ".xxh" => ChecksumKind.XxHash3,
            ".crc32" or ".crc" or ".sfv" => ChecksumKind.Crc32,
            _ => null,
        };
    }

    /// <summary>The kind a hex string of this length can only be (32 → MD5), or null when the length matches none or is ambiguous.</summary>
    public static ChecksumKind? FromHexLength(int length) => All.Where(k => HexLength(k) == length).Select(k => (ChecksumKind?)k).FirstOrDefault();
}

/// <summary>
/// Computes several checksums of one stream in a single pass (spec §9.2, §15.4): reading a 6 GB file from a share once costs the same
/// however many sums are wanted. Reads in 1 MiB blocks, reports bytes read, and stops at the next block when cancelled, so a huge file
/// never holds the caller.
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
        var hashes = kinds.Distinct().ToDictionary(k => k, Create);
        try
        {
            var buffer = new byte[Block];
            long total = 0;
            int read;
            while ((read = await stream.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false)) > 0)
            {
                foreach (var hash in hashes.Values)
                {
                    hash.Append(buffer.AsSpan(0, read));
                }

                total += read;
                progress?.Report(total);
            }

            return hashes.ToDictionary(p => p.Key, p => p.Value.Finish());
        }
        finally
        {
            foreach (var hash in hashes.Values)
            {
                hash.Dispose();
            }
        }
    }

    /// <summary>The checksum of bytes already in memory.</summary>
    public static string Compute(ReadOnlySpan<byte> data, ChecksumKind kind)
    {
        using var hasher = Create(kind);
        hasher.Append(data);
        return hasher.Finish();
    }

    private static Hasher Create(ChecksumKind kind) => kind switch
    {
        ChecksumKind.Md5 => new Hasher(IncrementalHash.CreateHash(HashAlgorithmName.MD5)),
        ChecksumKind.Sha1 => new Hasher(IncrementalHash.CreateHash(HashAlgorithmName.SHA1)),
        ChecksumKind.Sha256 => new Hasher(IncrementalHash.CreateHash(HashAlgorithmName.SHA256)),
        ChecksumKind.Sha512 => new Hasher(IncrementalHash.CreateHash(HashAlgorithmName.SHA512)),
        ChecksumKind.XxHash3 => new Hasher(new XxHash3(), static a => ((XxHash3)a).GetCurrentHashAsUInt64().ToString("x16", CultureInfo.InvariantCulture)),
        _ => new Hasher(new Crc32(), static a => ((Crc32)a).GetCurrentHashAsUInt32().ToString("x8", CultureInfo.InvariantCulture)),
    };

    // One sum being computed: a cryptographic hash or one of the framework's non-cryptographic ones, behind the same three calls. The
    // numeric forms are written as the well-known tools write them (xxhsum's canonical form, the plain CRC value), not as raw bytes.
    private sealed class Hasher : IDisposable
    {
        private readonly IncrementalHash? _crypto;
        private readonly NonCryptographicHashAlgorithm? _fast;
        private readonly Func<NonCryptographicHashAlgorithm, string>? _format;

        public Hasher(IncrementalHash crypto) => _crypto = crypto;

        public Hasher(NonCryptographicHashAlgorithm fast, Func<NonCryptographicHashAlgorithm, string> format)
        {
            _fast = fast;
            _format = format;
        }

        public void Append(ReadOnlySpan<byte> data)
        {
            if (_crypto is not null)
            {
                _crypto.AppendData(data);
            }
            else
            {
                _fast!.Append(data);
            }
        }

        public string Finish() => _crypto is not null ? Convert.ToHexStringLower(_crypto.GetHashAndReset()) : _format!(_fast!);

        public void Dispose() => _crypto?.Dispose();
    }
}
