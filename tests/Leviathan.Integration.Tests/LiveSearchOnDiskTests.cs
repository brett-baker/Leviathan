using Leviathan.Core.Paths;
using Leviathan.Core.Providers;
using Leviathan.Providers.Local;
using Leviathan.Search;
using Leviathan.Testing;

namespace Leviathan.Integration.Tests;

/// <summary>The live search over a real temp folder with the real local provider: odd names, a long path, hidden files.</summary>
public sealed class LiveSearchOnDiskTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "leviathan-search-" + Guid.NewGuid().ToString("N"));

    public LiveSearchOnDiskTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // Best effort.
        }
    }

    private void Make(string relative, string content = "x")
    {
        var path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private async Task<List<string>> SearchAsync(string query, bool hidden = false)
    {
        var search = new LiveSearch(new LocationProviderRegistry([new LocalLocationProvider()]), new LiveSearchOptions { IncludeHidden = hidden });
        var matcher = new QueryMatcher(QueryParser.Parse(query), new ManualTimeProvider(), TimeZoneInfo.Utc);
        var names = new List<string>();
        await foreach (var batch in search.RunAsync([LocationPath.Parse(_root)], matcher, NoSearchFacts.Instance, CancellationToken.None))
        {
            names.AddRange(batch.Matches.Select(m => Path.GetRelativePath(_root, m.Location.ToString())));
        }

        names.Sort(StringComparer.Ordinal);
        return names;
    }

    [Fact]
    public async Task Search_FindsFilesWithOddNamesInNestedFolders()
    {
        Make(Path.Combine("a", "clip one.mkv"));
        Make(Path.Combine("a", "b", "Ünïcödé ✓.mkv"));
        Make(Path.Combine("a", "b", "c", "notes.txt"));
        Make("top.mkv");

        var found = await SearchAsync("ext:mkv");

        Assert.Equal([Path.Combine("a", "b", "Ünïcödé ✓.mkv"), Path.Combine("a", "clip one.mkv"), "top.mkv"], found);
    }

    [Fact]
    public async Task Search_ReachesAPathLongerThan260Characters()
    {
        var deep = string.Join(Path.DirectorySeparatorChar, Enumerable.Range(0, 12).Select(i => new string((char)('a' + i), 25)));
        var folder = Path.Combine(_root, deep);
        Directory.CreateDirectory(@"\\?\" + folder);
        File.WriteAllText(@"\\?\" + Path.Combine(folder, "needle.mkv"), "x");

        var found = await SearchAsync("needle");

        Assert.Single(found);
    }

    [Fact]
    public async Task Search_SkipsHiddenFilesUnlessAsked()
    {
        Make("shown.txt");
        Make("secret.txt");
        File.SetAttributes(Path.Combine(_root, "secret.txt"), FileAttributes.Hidden);

        Assert.Equal(["shown.txt"], await SearchAsync("txt"));
        Assert.Equal(["secret.txt", "shown.txt"], await SearchAsync("txt", hidden: true));
    }

    [Fact]
    public async Task Search_SizeAndTypeFiltersUseTheRealEntries()
    {
        Make("small.mkv", "x");
        Make("big.mkv", new string('x', 5000));

        Assert.Equal(["big.mkv"], await SearchAsync("type:video size:>1KB"));
    }
}
