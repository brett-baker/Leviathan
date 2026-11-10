namespace Leviathan.Testing;

/// <summary>The ways a media file can be damaged in the corrupt-file suite (spec §9.3, §19; Phase 3 gate: a corrupt-file test set causes no crash).</summary>
public enum CorruptKind
{
    /// <summary>No bytes at all.</summary>
    Empty,

    /// <summary>A single byte.</summary>
    OneByte,

    /// <summary>Only the first tenth of the file (a download that stopped early).</summary>
    TruncatedTenth,

    /// <summary>Only the first half.</summary>
    TruncatedHalf,

    /// <summary>Everything but the last 200 bytes (an MP4 loses its index, a FLAC its last frame).</summary>
    TruncatedTail,

    /// <summary>The first 4 KiB overwritten with zeros (the header and the first index).</summary>
    HeaderZeroed,

    /// <summary>64 bytes flipped at fixed places in the middle (bit rot).</summary>
    MiddleFlips,

    /// <summary>Same length, all random bytes.</summary>
    RandomGarbage,

    /// <summary>All zeros, same length.</summary>
    ZeroFilled,

    /// <summary>Plain text under a media name.</summary>
    TextInDisguise,

    /// <summary>The first four bytes set to 0xFFFFFFFF: a box or element that claims to be 4 GiB long.</summary>
    HugeFirstSize,
}

/// <summary>Makes damaged copies of a real media file, the same way every time, so a failure can be reproduced from its name alone.</summary>
public static class CorruptMedia
{
    /// <summary>Every kind of damage, in a fixed order.</summary>
    public static IReadOnlyList<CorruptKind> All { get; } = [.. Enum.GetValues<CorruptKind>()];

    /// <summary>The damaged version of <paramref name="source"/>. Never changes <paramref name="source"/>; the result for a kind is always the same bytes.</summary>
    public static byte[] Apply(byte[] source, CorruptKind kind)
    {
        ArgumentNullException.ThrowIfNull(source);
        switch (kind)
        {
            case CorruptKind.Empty:
                return [];
            case CorruptKind.OneByte:
                return source.Length == 0 ? [0x42] : [source[0]];
            case CorruptKind.TruncatedTenth:
                return source[..Math.Min(source.Length, Math.Max(1, source.Length / 10))];
            case CorruptKind.TruncatedHalf:
                return source[..Math.Min(source.Length, Math.Max(1, source.Length / 2))];
            case CorruptKind.TruncatedTail:
                return source[..Math.Min(source.Length, Math.Max(1, source.Length - 200))];
            case CorruptKind.HeaderZeroed:
            {
                var copy = (byte[])source.Clone();
                Array.Clear(copy, 0, Math.Min(4096, copy.Length));
                return copy;
            }

            case CorruptKind.MiddleFlips:
            {
                var copy = (byte[])source.Clone();
                var random = new Random(20261110);
                for (var i = 0; i < 64 && copy.Length > 8192; i++)
                {
                    var at = copy.Length / 4 + random.Next(copy.Length / 2);
                    copy[at] ^= 0xFF;
                }

                return copy;
            }

            case CorruptKind.RandomGarbage:
            {
                var copy = new byte[Math.Max(1, source.Length)];
                new Random(20261111).NextBytes(copy);
                return copy;
            }

            case CorruptKind.ZeroFilled:
                return new byte[Math.Max(1, source.Length)];
            case CorruptKind.TextInDisguise:
                return System.Text.Encoding.ASCII.GetBytes("This is not a video. It is a note someone renamed.\r\n");
            case CorruptKind.HugeFirstSize:
            {
                var copy = (byte[])source.Clone();
                for (var i = 0; i < Math.Min(4, copy.Length); i++)
                {
                    copy[i] = 0xFF;
                }

                return copy;
            }

            default:
                throw new ArgumentOutOfRangeException(nameof(kind));
        }
    }

    /// <summary>Writes one damaged copy per kind into <paramref name="folder"/> as <c>stem.kind.extension</c>, returning the paths in order.</summary>
    public static IReadOnlyList<string> WriteAll(byte[] source, string folder, string stem, string extension)
    {
        ArgumentNullException.ThrowIfNull(source);
        Directory.CreateDirectory(folder);
        var paths = new List<string>();
        foreach (var kind in All)
        {
            var path = Path.Combine(folder, $"{stem}.{kind}{extension}");
            File.WriteAllBytes(path, Apply(source, kind));
            paths.Add(path);
        }

        return paths;
    }
}
