using System.Collections.Concurrent;
using System.Numerics;

namespace Leviathan.Media;

/// <summary>Whether a signature is of a picture or of a video.</summary>
public enum SignatureKind
{
    /// <summary>One hash of the whole picture.</summary>
    Image = 0,

    /// <summary>Hashes of frames taken at fixed places in the video, and its duration.</summary>
    Video = 1,
}

/// <summary>
/// What is kept about a picture or a video to find its look-alikes (spec §15.2): a 64-bit difference hash of the picture, or of each of
/// several frames of a video with its duration. Small enough to keep for every file of a library.
/// </summary>
/// <param name="Kind">Picture or video.</param>
/// <param name="Hashes">One hash for a picture; for a video one per frame, in order.</param>
/// <param name="DurationSeconds">Length of a video; 0 for a picture.</param>
public sealed record MediaSignature(SignatureKind Kind, IReadOnlyList<ulong> Hashes, double DurationSeconds)
{
    /// <summary>Where in a video the frames are taken, as a fraction of its length (spec §15.2: "frames taken at fixed percentages").</summary>
    public static readonly IReadOnlyList<double> VideoFrameFractions = [0.10, 0.30, 0.50, 0.70, 0.90];
}

/// <summary>
/// The difference hash (dHash) of a picture: shrink it to 9×8 grey pixels and set a bit for every pixel that is darker than the one on
/// its right. The same picture resized, re-compressed or re-encoded gives hashes a few bits apart; a different picture is about half
/// of the 64 bits apart. No image library is needed: ffmpeg shrinks the picture, this reads the 72 bytes.
/// </summary>
public static class DifferenceHash
{
    /// <summary>Pixels per row of the shrunken picture.</summary>
    public const int Columns = 9;

    /// <summary>Rows of the shrunken picture.</summary>
    public const int Rows = 8;

    /// <summary>Bytes in the shrunken picture (<see cref="Columns"/> × <see cref="Rows"/>).</summary>
    public const int PixelCount = Columns * Rows;

    /// <summary>The hash of a 9×8 grey picture, row by row.</summary>
    public static ulong FromGrey(ReadOnlySpan<byte> pixels)
    {
        if (pixels.Length < PixelCount)
        {
            throw new ArgumentException($"A hash needs {PixelCount} grey pixels.", nameof(pixels));
        }

        ulong hash = 0;
        for (var row = 0; row < Rows; row++)
        {
            for (var column = 0; column < Columns - 1; column++)
            {
                hash <<= 1;
                if (pixels[row * Columns + column] < pixels[row * Columns + column + 1])
                {
                    hash |= 1;
                }
            }
        }

        return hash;
    }

    /// <summary>How many of the 64 bits differ.</summary>
    public static int Distance(ulong a, ulong b) => BitOperations.PopCount(a ^ b);
}

/// <summary>How alike two files must be to be grouped (spec §15.2: tolerance setting).</summary>
/// <param name="MaxBitDistance">Most bits (of 64) a hash may differ by: 0 is the same picture, about 6 catches resized and re-compressed copies, 12 starts to catch edits and crops. Videos use it for the mean over their frames.</param>
/// <param name="DurationTolerance">How much longer one video may be than the other, as a fraction (re-encodes and trims differ by a little); at least one second is always allowed.</param>
public sealed record SimilarityOptions(int MaxBitDistance = 6, double DurationTolerance = 0.03)
{
    /// <summary>The largest tolerance offered.</summary>
    public const int LargestDistance = 20;

    /// <summary>The tolerance kept to 0 to <see cref="LargestDistance"/>.</summary>
    public int Distance => Math.Clamp(MaxBitDistance, 0, LargestDistance);
}

/// <summary>A set of files that look alike.</summary>
/// <param name="Paths">Two or more files, in the order they were given.</param>
public sealed record SimilarGroup(IReadOnlyList<string> Paths);

/// <summary>
/// Groups files whose signatures are close (spec §15.2). Candidates come from a BK-tree over one hash per file, so a library of
/// 100,000 pictures is not compared pair by pair; each candidate pair is then checked in full, and files joined by a chain of matches
/// end in one group. Pictures only meet pictures and videos only meet videos.
/// </summary>
public static class SimilarMediaMatcher
{
    /// <summary>True when the two signatures describe media that looks alike under <paramref name="options"/>.</summary>
    public static bool AreSimilar(MediaSignature a, MediaSignature b, SimilarityOptions options)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);
        ArgumentNullException.ThrowIfNull(options);
        if (a.Kind != b.Kind || a.Hashes.Count == 0 || a.Hashes.Count != b.Hashes.Count)
        {
            return false;
        }

        var limit = options.Distance;
        if (a.Kind == SignatureKind.Image)
        {
            return DifferenceHash.Distance(a.Hashes[0], b.Hashes[0]) <= limit;
        }

        var longer = Math.Max(a.DurationSeconds, b.DurationSeconds);
        if (Math.Abs(a.DurationSeconds - b.DurationSeconds) > Math.Max(1.0, longer * Math.Max(0, options.DurationTolerance)))
        {
            return false;
        }

        // The mean says the frames look alike; the largest says no single frame is a different scene.
        var total = 0;
        var worst = 0;
        for (var i = 0; i < a.Hashes.Count; i++)
        {
            var distance = DifferenceHash.Distance(a.Hashes[i], b.Hashes[i]);
            total += distance;
            worst = Math.Max(worst, distance);
        }

        return total <= limit * a.Hashes.Count && worst <= limit * 2;
    }

    /// <summary>The groups of look-alikes among <paramref name="files"/>, the largest first; files with no look-alike are left out.</summary>
    public static IReadOnlyList<SimilarGroup> Group(IReadOnlyList<(string Path, MediaSignature Signature)> files, SimilarityOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(options);
        var parent = Enumerable.Range(0, files.Count).ToArray();

        int Find(int i)
        {
            while (parent[i] != i)
            {
                parent[i] = parent[parent[i]];
                i = parent[i];
            }

            return i;
        }

        foreach (var kind in new[] { SignatureKind.Image, SignatureKind.Video })
        {
            var tree = new BkTree();
            var indexes = Enumerable.Range(0, files.Count).Where(i => files[i].Signature.Kind == kind && files[i].Signature.Hashes.Count > 0).ToList();
            foreach (var i in indexes)
            {
                tree.Add(Anchor(files[i].Signature), i);
            }

            // A video can differ at its anchor frame by more than a picture would, so the search is a little wider than the final check.
            var radius = kind == SignatureKind.Image ? options.Distance : options.Distance * 2;
            foreach (var i in indexes)
            {
                cancellationToken.ThrowIfCancellationRequested();
                foreach (var j in tree.Within(Anchor(files[i].Signature), radius))
                {
                    if (j > i && Find(i) != Find(j) && AreSimilar(files[i].Signature, files[j].Signature, options))
                    {
                        parent[Find(j)] = Find(i);
                    }
                }
            }
        }

        return [.. Enumerable.Range(0, files.Count)
            .GroupBy(Find)
            .Where(g => g.Count() > 1)
            .Select(g => new SimilarGroup([.. g.OrderBy(i => i).Select(i => files[i].Path)]))
            .OrderByDescending(g => g.Paths.Count)
            .ThenBy(g => g.Paths[0], StringComparer.OrdinalIgnoreCase)];
    }

    // The hash that stands for a file in the tree: the only one of a picture, the middle frame of a video.
    private static ulong Anchor(MediaSignature signature) => signature.Hashes[signature.Hashes.Count / 2];

    /// <summary>A BK-tree over the Hamming distance of 64-bit hashes: finds every hash within a radius without looking at them all.</summary>
    private sealed class BkTree
    {
        private sealed class Node(ulong hash)
        {
            public ulong Hash { get; } = hash;

            public List<int> Items { get; } = [];

            public Dictionary<int, Node> Children { get; } = [];
        }

        private Node? _root;

        public void Add(ulong hash, int item)
        {
            if (_root is null)
            {
                _root = new Node(hash);
                _root.Items.Add(item);
                return;
            }

            var node = _root;
            while (true)
            {
                var distance = DifferenceHash.Distance(hash, node.Hash);
                if (distance == 0)
                {
                    node.Items.Add(item);
                    return;
                }

                if (!node.Children.TryGetValue(distance, out var child))
                {
                    node.Children[distance] = child = new Node(hash);
                    child.Items.Add(item);
                    return;
                }

                node = child;
            }
        }

        public IEnumerable<int> Within(ulong hash, int radius)
        {
            if (_root is null)
            {
                yield break;
            }

            var pending = new Stack<Node>();
            pending.Push(_root);
            while (pending.Count > 0)
            {
                var node = pending.Pop();
                var distance = DifferenceHash.Distance(hash, node.Hash);
                if (distance <= radius)
                {
                    foreach (var item in node.Items)
                    {
                        yield return item;
                    }
                }

                foreach (var (edge, child) in node.Children)
                {
                    if (edge >= distance - radius && edge <= distance + radius)
                    {
                        pending.Push(child);
                    }
                }
            }
        }
    }
}

/// <summary>Where signatures are kept between runs (SQLite in the app, memory in tests); they are cached by file version like the metadata (spec §9.3, §15.2).</summary>
public interface IMediaSignatureStore
{
    /// <summary>The signature of exactly this version of the file, or null (including when the file has changed since).</summary>
    MediaSignature? Get(MediaFileKey key);

    /// <summary>Keeps a signature, replacing any earlier one for the path.</summary>
    void Put(MediaFileKey key, MediaSignature signature);
}

/// <summary>A store in memory: for tests, and for a session where the database is not available.</summary>
public sealed class InMemoryMediaSignatureStore : IMediaSignatureStore
{
    private readonly ConcurrentDictionary<string, (MediaFileKey Key, MediaSignature Signature)> _entries = new(StringComparer.OrdinalIgnoreCase);

    /// <inheritdoc />
    public MediaSignature? Get(MediaFileKey key) =>
        _entries.TryGetValue(key.Path, out var entry) && entry.Key.Matches(key) ? entry.Signature : null;

    /// <inheritdoc />
    public void Put(MediaFileKey key, MediaSignature signature) => _entries[key.Path] = (key, signature);
}
