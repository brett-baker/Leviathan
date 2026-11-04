using System.Diagnostics;
using Leviathan.Core.Model;
using Leviathan.Core.Paths;
using Leviathan.Core.Providers;
using Leviathan.Providers.Local.Win32;

namespace Leviathan.Providers.Local.Tests;

/// <summary>
/// Real-disk versions of the inputs CLAUDE.md calls normal: names in every script, paths past the 260-character limit and a folder
/// of 100,000 files. The first two always run; the big folder takes a while to create and is opt-in (LEVIATHAN_STRESS=1).
/// </summary>
public sealed class StressTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "leviathan-stress-" + Guid.NewGuid().ToString("N"));

    public StressTests()
    {
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(@"\\?\" + _root, recursive: true);
        }
        catch (IOException)
        {
            // Best effort.
        }
    }

    private static LocalLocationProvider Provider() => new(new ManagedDirectoryLister(), null, location => location.Value, TimeProvider.System);

    private static async Task<List<FileEntry>> ListAllAsync(LocalLocationProvider provider, LocationPath location)
    {
        var items = new List<FileEntry>();
        await foreach (var batch in provider.ListAsync(location, ListingOptions.Default, CancellationToken.None))
        {
            items.AddRange(batch.Items);
        }

        return items;
    }

    [Fact]
    public async Task List_NamesInEveryScript_ComeBackExactly()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows file names");
        var names = new[]
        {
            "ünïcode ✓ 日本語.txt", "emoji 😀🎬 file.mkv", "עברית.txt", "العربية.txt", " leading space.txt", "a#b%c&d.txt", "ＦＵＬＬＷＩＤＴＨ.txt",
            "File (1).txt", new string('x', 200) + ".txt", ".hidden", "two  spaces.txt",
        };
        foreach (var name in names)
        {
            await File.WriteAllTextAsync(Path.Combine(_root, name), "x");
        }

        var listed = await ListAllAsync(Provider(), LocationPath.Parse(_root));

        Assert.Equal(names.Order(StringComparer.Ordinal).ToList(), listed.Select(e => e.Name).Order(StringComparer.Ordinal).ToList());
    }

    [Fact]
    public async Task List_AFolderBeyondTwoHundredSixtyCharacters_IsListed()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows long paths");
        var deep = _root;
        while (deep.Length < 400)
        {
            deep = Path.Combine(deep, new string('d', 60));
        }

        var extended = @"\\?\" + deep;
        Directory.CreateDirectory(extended);
        await File.WriteAllTextAsync(Path.Combine(extended, "deep file.txt"), "x");

        var listed = await ListAllAsync(Provider(), LocationPath.Parse(extended));

        Assert.Equal(["deep file.txt"], listed.Select(e => e.Name));
    }

    [Fact]
    public async Task List_AHundredThousandFilesOnDisk_StreamsInBatches_AndIsFast()
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable("LEVIATHAN_STRESS") == "1", "set LEVIATHAN_STRESS=1 to create 100,000 files");
        var big = Path.Combine(_root, "big");
        Directory.CreateDirectory(big);
        Parallel.For(0, 100_000, i => File.WriteAllBytes(Path.Combine(big, $"file {i:D6}.dat"), []));
        var batches = 0;
        var total = 0;
        var clock = Stopwatch.StartNew();
        TimeSpan? first = null;

        await foreach (var batch in Provider().ListAsync(LocationPath.Parse(big), ListingOptions.Default, CancellationToken.None))
        {
            first ??= clock.Elapsed;
            batches++;
            total += batch.Items.Count;
        }

        Assert.Equal(100_000, total);
        Assert.True(batches > 10, $"{batches} batches");
        Assert.True(first < TimeSpan.FromMilliseconds(500), $"first rows after {first?.TotalMilliseconds:0} ms");
    }
}
