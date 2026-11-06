using System.Security.Cryptography;

namespace Leviathan.Chaos.Tests;

/// <summary>A folder of random files on a real disk, and the check that a copy of it is whole.</summary>
internal sealed class ChaosTree : IDisposable
{
    public ChaosTree(string name)
    {
        Root = Path.Combine(Path.GetTempPath(), $"leviathan-chaos-{name}-{Guid.NewGuid():N}");
        Source = Path.Combine(Root, "source", "tree");
        Destination = Path.Combine(Root, "dest");
        State = Path.Combine(Root, "state");
        Directory.CreateDirectory(Source);
        Directory.CreateDirectory(Destination);
    }

    public string Root { get; }

    public string Source { get; }

    public string Destination { get; }

    public string State { get; }

    /// <summary>Where the copy of <see cref="Source"/> lands.</summary>
    public string Copy => Path.Combine(Destination, "tree");

    /// <summary>Writes <paramref name="count"/> files of random content, sizes between the two bounds, spread over a few sub folders.</summary>
    public void Fill(Random random, int count, int minBytes, int maxBytes)
    {
        for (var i = 0; i < count; i++)
        {
            var folder = Path.Combine(Source, "d" + (i % 7), "sub " + (i % 3));
            Directory.CreateDirectory(folder);
            var data = new byte[random.Next(minBytes, maxBytes)];
            random.NextBytes(data);
            File.WriteAllBytes(Path.Combine(folder, $"file {i:D5} ✓.bin"), data);
        }
    }

    /// <summary>
    /// Every file under the copy with a finished name must equal its source, and no finished name may exist without a source.
    /// Returns the files still missing and the part files present, so a caller decides whether that is acceptable yet.
    /// </summary>
    public (List<string> Missing, List<string> Parts) Inspect()
    {
        var parts = new List<string>();
        var present = new HashSet<string>();
        if (Directory.Exists(Copy))
        {
            foreach (var file in Directory.EnumerateFiles(Copy, "*", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(Copy, file);
                if (file.EndsWith(".leviathan-part", StringComparison.Ordinal))
                {
                    parts.Add(relative);
                    continue;
                }

                var original = Path.Combine(Source, relative);
                Assert.True(File.Exists(original), $"'{relative}' exists at the destination but has no source");
                Assert.True(Hash(file) == Hash(original), $"'{relative}' has its final name but its content differs from the source");
                present.Add(relative);
            }
        }

        var missing = Directory.EnumerateFiles(Source, "*", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(Source, f))
            .Where(r => !present.Contains(r))
            .ToList();
        return (missing, parts);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
            // Best effort.
        }
    }

    private static string Hash(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }
}
