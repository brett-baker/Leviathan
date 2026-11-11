using System.IO.Abstractions;
using Leviathan.Engine.Duplicates;

namespace Leviathan.Integration.Tests;

/// <summary>The duplicate finder on a real disk: real files, real streams, odd names, a file open elsewhere (spec §15.2).</summary>
public sealed class DuplicateFinderDiskTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "leviathan-dups-" + Guid.NewGuid().ToString("N")[..8]);

    public DuplicateFinderDiskTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public async Task RealFiles_WithOddNames_AreGroupedByContents_AndAnOpenFileCanStillBeRead()
    {
        var content = new byte[3 * 1024 * 1024 + 11];
        new Random(3).NextBytes(content);
        var other = (byte[])content.Clone();
        other[1_500_000]++;
        Directory.CreateDirectory(Path.Combine(_root, "Ünï ✓ folder"));
        File.WriteAllBytes(Path.Combine(_root, "a.bin"), content);
        File.WriteAllBytes(Path.Combine(_root, "Ünï ✓ folder", "copy ✓.bin"), content);
        File.WriteAllBytes(Path.Combine(_root, "differs in the middle.bin"), other);
        File.WriteAllBytes(Path.Combine(_root, "open elsewhere.bin"), content);

        await using var held = new FileStream(Path.Combine(_root, "open elsewhere.bin"), FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var result = await new DuplicateFinder(new FileSystem()).FindAsync([_root], new DuplicateOptions(), null, CancellationToken.None);

        var group = Assert.Single(result.Groups);
        Assert.Equal(3, group.Paths.Count);
        Assert.DoesNotContain(group.Paths, p => p.EndsWith("differs in the middle.bin", StringComparison.Ordinal));
        Assert.Empty(result.Unreadable);
    }
}
