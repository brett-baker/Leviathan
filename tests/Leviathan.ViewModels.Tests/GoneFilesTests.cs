using Leviathan.Core.Paths;
using Leviathan.ViewModels.Tabs;
using Leviathan.ViewModels.Triage;

namespace Leviathan.ViewModels.Tests;

/// <summary>A file another program moved away must leave the list, not linger as a row with an error behind it.</summary>
public sealed class GoneFilesTests
{
    private sealed class DiskWithoutOne(string missing) : ITagStore
    {
        public Task<IReadOnlySet<int>> GetAsync(string path, CancellationToken cancellationToken) =>
            Task.FromResult(string.Equals(path, missing, StringComparison.OrdinalIgnoreCase) ? TagFiles.Gone : (IReadOnlySet<int>)new HashSet<int>());

        public Task SetAsync(string path, IReadOnlySet<int> tags, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    [Fact]
    public async Task ARowWhoseFileIsGone_IsTakenOutOfTheList_AndTheRestStay()
    {
        var provider = TabTestSupport.Tree().AddFile(@"C:\mixed\here.txt", 1).AddFile(@"C:\mixed\moved.txt", 1);
        var services = TabTestSupport.Services(provider) with { TagStore = new DiskWithoutOne(@"C:\mixed\moved.txt") };
        var tab = new TabViewModel(services, LocationPath.Parse(@"C:\mixed"));
        await tab.ReloadAsync(CancellationToken.None);
        Assert.Equal(2, tab.Folder.Entries.Count);

        tab.FileTags.SetVisible([.. tab.Folder.Entries]);
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (tab.Folder.Entries.Count > 1)
        {
            Assert.True(DateTime.UtcNow < deadline, "the missing file was never taken out of the list");
            await Task.Delay(10);
        }

        Assert.Equal(["here.txt"], tab.Folder.Entries.Select(e => e.Name));
    }
}
