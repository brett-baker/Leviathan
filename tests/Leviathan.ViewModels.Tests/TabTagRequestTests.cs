using Leviathan.Core.Model;
using Leviathan.Core.Providers;
using Leviathan.ViewModels.Folders;
using Leviathan.ViewModels.Tabs;
using Leviathan.ViewModels.Tags;
using Leviathan.ViewModels.Triage;
using Leviathan.ViewModels.Views;

namespace Leviathan.ViewModels.Tests;

/// <summary>Which tabs read colour tags: the Tags column, a tag: filter, or the picture layouts' badges (spec §11.1).</summary>
public sealed class TabTagRequestTests
{
    private sealed class CountingStore : ITagStore
    {
        private int _reads;

        public int Reads => Volatile.Read(ref _reads);

        public Task<IReadOnlySet<int>> GetAsync(string path, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _reads);
            return Task.FromResult<IReadOnlySet<int>>(new HashSet<int> { 1 });
        }

        public Task SetAsync(string path, IReadOnlySet<int> tags, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private static async Task<(TabViewModel Tab, CountingStore Store)> OpenAsync()
    {
        var store = new CountingStore();
        var services = new TabServices(new LocationProviderRegistry([TabTestSupport.Tree()]), new InlineUiContext(), new FolderViewRules(), TimeProvider.System,
            () => TabTestSupport.P(@"C:\Home"), TagStore: store);
        var tab = new TabViewModel(services, TabTestSupport.P(@"C:\v"));
        await tab.ReloadAsync(CancellationToken.None);
        return (tab, store);
    }

    [Fact]
    public async Task RequestTags_WithoutTheColumnOrBadges_ReadsNothing()
    {
        var (tab, store) = await OpenAsync();
        tab.Columns.SetVisible(FileColumns.Tags.Id, false);
        Assert.False(tab.ShowsTagsColumn);

        tab.RequestTags([.. tab.Items.Rows]);
        await Task.Delay(100);

        Assert.Equal(0, store.Reads);
    }

    [Fact]
    public async Task RequestTags_ForBadges_ReadsTheFilesOnScreen()
    {
        var (tab, store) = await OpenAsync();
        tab.Columns.SetVisible(FileColumns.Tags.Id, false);
        var files = tab.Items.Rows.Where(e => e.Kind == FileEntryKind.File).ToList();
        Assert.NotEmpty(files);

        tab.RequestTags(files, showsBadges: true);
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (store.Reads < files.Count && DateTime.UtcNow < deadline)
        {
            await Task.Delay(5);
        }

        Assert.Equal(files.Count, store.Reads);
    }
}
