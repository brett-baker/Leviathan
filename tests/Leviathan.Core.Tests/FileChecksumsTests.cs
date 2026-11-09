using System.Text;
using Leviathan.Core.Hashing;

namespace Leviathan.Core.Tests;

/// <summary>Checksums on demand (spec §9.2 Integrity).</summary>
public sealed class FileChecksumsTests
{
    private static MemoryStream Abc() => new(Encoding.ASCII.GetBytes("abc"));

    [Fact]
    public async Task Compute_GivesTheKnownSumsOfAbc()
    {
        var sums = await FileChecksums.ComputeAsync(Abc(), [ChecksumKind.Md5, ChecksumKind.Sha1, ChecksumKind.Sha256], null, CancellationToken.None);

        Assert.Equal("900150983cd24fb0d6963f7d28e17f72", sums[ChecksumKind.Md5]);
        Assert.Equal("a9993e364706816aba3e25717850c26c9cd0d89d", sums[ChecksumKind.Sha1]);
        Assert.Equal("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad", sums[ChecksumKind.Sha256]);
    }

    [Fact]
    public async Task Compute_OfNothing_IsTheSumOfTheEmptyString()
    {
        var sums = await FileChecksums.ComputeAsync(new MemoryStream(), [ChecksumKind.Md5], null, CancellationToken.None);

        Assert.Equal("d41d8cd98f00b204e9800998ecf8427e", sums[ChecksumKind.Md5]);
    }

    [Fact]
    public async Task Compute_ReportsBytesReadAcrossBlocks()
    {
        var reported = new List<long>();
        var data = new MemoryStream(new byte[(2 * 1024 * 1024) + 5]);

        await FileChecksums.ComputeAsync(data, [ChecksumKind.Sha1], new Sync(reported),CancellationToken.None);

        Assert.Equal([1024L * 1024, 2L * 1024 * 1024, (2L * 1024 * 1024) + 5], reported);
    }

    [Fact]
    public async Task Compute_WhenCancelled_Throws_AndGivesNoSum()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => FileChecksums.ComputeAsync(Abc(), [ChecksumKind.Md5], null, cts.Token));
    }

    private sealed class Sync(List<long> into) : IProgress<long>
    {
        public void Report(long value) => into.Add(value);
    }
}
