using Leviathan.Testing;

namespace Leviathan.Media.Tests;

/// <summary>The damage the corrupt-file suite does is the same every time and never touches the original.</summary>
public sealed class CorruptMediaTests
{
    private static readonly byte[] Source = [.. Enumerable.Range(0, 20000).Select(i => (byte)(i * 31 % 251))];

    [Fact]
    public void EveryKind_IsRepeatable_AndLeavesTheSourceAlone()
    {
        var before = (byte[])Source.Clone();

        foreach (var kind in CorruptMedia.All)
        {
            Assert.Equal(CorruptMedia.Apply(Source, kind), CorruptMedia.Apply(Source, kind));
        }

        Assert.Equal(before, Source);
    }

    [Fact]
    public void EveryKind_DiffersFromTheSource()
    {
        Assert.All(CorruptMedia.All, kind => Assert.NotEqual(Source, CorruptMedia.Apply(Source, kind)));
    }

    [Fact]
    public void Truncations_KeepTheStart_AndAreShorter()
    {
        var half = CorruptMedia.Apply(Source, CorruptKind.TruncatedHalf);
        var tail = CorruptMedia.Apply(Source, CorruptKind.TruncatedTail);

        Assert.Equal(10000, half.Length);
        Assert.Equal(Source[..10000], half);
        Assert.Equal(Source.Length - 200, tail.Length);
        Assert.Equal(Source[..tail.Length], tail);
    }

    [Fact]
    public void HeaderZeroed_ClearsOnlyTheFirst4096Bytes()
    {
        var damaged = CorruptMedia.Apply(Source, CorruptKind.HeaderZeroed);

        Assert.All(damaged[..4096], b => Assert.Equal(0, b));
        Assert.Equal(Source[4096..], damaged[4096..]);
    }

    [Fact]
    public void TheSmallKinds_AreWhatTheyClaim()
    {
        Assert.Empty(CorruptMedia.Apply(Source, CorruptKind.Empty));
        Assert.Single(CorruptMedia.Apply(Source, CorruptKind.OneByte));
        Assert.Equal(Source.Length, CorruptMedia.Apply(Source, CorruptKind.RandomGarbage).Length);
        Assert.Equal(Source.Length, CorruptMedia.Apply(Source, CorruptKind.ZeroFilled).Length);
        Assert.Equal(new byte[] { 0xFF, 0xFF, 0xFF, 0xFF }, CorruptMedia.Apply(Source, CorruptKind.HugeFirstSize)[..4]);
    }

    [Fact]
    public void ASourceThatIsTooSmall_StillGivesEveryKindWithoutThrowing()
    {
        foreach (var kind in CorruptMedia.All)
        {
            Assert.NotNull(CorruptMedia.Apply([], kind));
            Assert.NotNull(CorruptMedia.Apply([1], kind));
        }
    }

    [Fact]
    public void WriteAll_MakesOneNamedFilePerKind()
    {
        var folder = Path.Combine(Path.GetTempPath(), "leviathan-corrupt-" + Guid.NewGuid().ToString("N"));
        try
        {
            var paths = CorruptMedia.WriteAll(Source, folder, "clip", ".mp4");

            Assert.Equal(CorruptMedia.All.Count, paths.Count);
            Assert.All(paths, p => Assert.True(File.Exists(p)));
            Assert.Contains(paths, p => Path.GetFileName(p) == "clip.TruncatedHalf.mp4");
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }
}
