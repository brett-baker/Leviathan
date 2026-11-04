using System.Collections.Specialized;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using Leviathan.Core.Model;
using Leviathan.Core.Paths;
using Leviathan.Core.Providers;
using Leviathan.Testing;
using Leviathan.ViewModels.Folders;
using Leviathan.ViewModels.Tabs;
using Leviathan.ViewModels.Views;

namespace Leviathan.ViewModels.Tests;

/// <summary>A folder of any size, handed out in batches of a thousand like a real lister does.</summary>
internal sealed class BatchedProvider(IReadOnlyList<FileEntry> entries) : ILocationProvider
{
    public string Name => "batched";

    public bool CanHandle(LocationPath location) => true;

    public async IAsyncEnumerable<ListingBatch> ListAsync(LocationPath location, ListingOptions options, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        for (var at = 0; at < entries.Count; at += 1000)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return new ListingBatch([.. entries.Skip(at).Take(1000)], at + 1000 >= entries.Count);
            await Task.Yield();
        }
    }
}

/// <summary>
/// The inputs that are normal for a file manager and awkward for code (CLAUDE.md: long paths, odd names, huge folders are normal
/// inputs): a folder of 100,000 files through the whole view model, names in every script and at every length, and a share that
/// stopped answering while another tab keeps browsing (spec 3.2, 3.3).
/// </summary>
public sealed class StressTests
{
    private static readonly DateTimeOffset Stamp = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static LocationPath P(string path) => LocationPath.Parse(path);

    private static FileEntry File(string folder, string name, long size = 1, FileEntryKind kind = FileEntryKind.File) =>
        new(name, LocationPath.Parse(folder).Combine(name), kind, size, Stamp.AddSeconds(size % 100_000), Stamp, FileAttributes.Normal);

    [Fact]
    public async Task AFolderOfOneHundredThousandFiles_LoadsSortsFiltersAndSelects_InReasonableTime_WithFewUpdates()
    {
        var entries = Enumerable.Range(0, 100_000)
            .Select(i => File(@"C:\big", $"clip {i:D6}{(i % 3 == 0 ? ".mkv" : i % 3 == 1 ? ".mp4" : ".txt")}", size: (i * 7919L) % 1_000_000, kind: i % 500 == 0 ? FileEntryKind.Folder : FileEntryKind.File))
            .ToList();
        // A serial UI thread like the real dispatcher: the inline test context runs chunks on whichever thread posts them, which a
        // dispatcher never does, so a timer-thread chunk could race the final one.
        using var ui = new UiLoop();
        var services = new TabServices(new LocationProviderRegistry([new BatchedProvider(entries)]), ui, new FolderViewRules(), TimeProvider.System, () => P(@"C:\Home"));
        var tab = new TabViewModel(services, P(@"C:\big"));
        var changes = 0;
        tab.Folder.Entries.CollectionChanged += (_, _) => changes++;
        var clock = Stopwatch.StartNew();

        await ui.RunAsync(() => tab.ReloadAsync(CancellationToken.None));
        var deadline = Stopwatch.GetTimestamp() + (30 * Stopwatch.Frequency);
        while (tab.Folder.State == FolderLoadState.Loading && Stopwatch.GetTimestamp() < deadline)
        {
            await Task.Delay(10);
        }

        Assert.Equal(FolderLoadState.Loaded, tab.Folder.State);
        Assert.Equal(100_000, tab.Folder.Entries.Count);
        Assert.Equal(100_000, tab.Items.ShownCount);
        // Rows reach the list in chunks, not one change per item (spec 3.2): a hundred thousand rows are about fifty chunks, never ten thousand updates.
        Assert.True(changes < 500, $"{changes} list updates for 100,000 rows");

        tab.Items.SortBy("size");
        var files = tab.Items.Rows.Where(r => r.Kind == FileEntryKind.File).Take(5000).Select(r => r.Size).ToList();
        Assert.Equal(files.Order().ToList(), files);
        tab.Items.SortBy("name");
        tab.Items.SetFilter("*.mkv");
        Assert.Equal(Enumerable.Range(0, 100_000).Count(i => i % 3 == 0), tab.Items.ShownCount);
        tab.Items.SetFilter(null);
        tab.Selection.SelectAll();
        Assert.Equal(100_000, tab.Selection.Count);
        tab.Selection.Clear();
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(30), $"load, sort, filter and select took {clock.Elapsed.TotalSeconds:0.0} s");
        tab.Dispose();
    }

    [Fact]
    public void NamesInEveryScriptAndAtEveryLength_SortAndFilterWithoutFailing()
    {
        var names = new[]
        {
            "plain.txt", "ünïcode ✓ 日本語.txt", "emoji 😀🎬 file.mkv", "עברית.txt", "العربية.txt", " leading space.txt", "trailing space .txt",
            "trailing dot.", ".hidden", "..double", "a#b%c&d.txt", "aux.txt", "CON", "nul.txt", "File (1).txt", "file (10).txt",
            "file (2).txt", "ＦＵＬＬＷＩＤＴＨ.txt", "é.txt", "é.txt", new string('x', 250) + ".txt", "İstanbul.txt", "ı.txt", "z.txt", "Z.txt",
        };
        var entries = names.Select((name, i) => File(@"C:\odd", name, i)).ToList();
        var source = new RangeObservableCollection<FileEntry>();
        source.AddRange(entries);
        var items = new FolderItemsView(source);

        foreach (var column in new[] { "name", "size", "modified", "type" })
        {
            items.SortBy(column);
            Assert.Equal(names.Length, items.ShownCount);
        }

        items.SetFilter("*.txt");
        Assert.True(items.ShownCount > 0);
        items.SetFilter("😀");
        Assert.Equal(1, items.ShownCount);
        items.SetFilter("עברית");
        Assert.Equal(1, items.ShownCount);
        items.SetFilter(string.Empty);
        Assert.Equal(names.Length, items.ShownCount);
    }

    [Fact]
    public async Task AShareThatStoppedAnswering_LeavesAnotherTabBrowsing_AndCanBeLeftBehind()
    {
        var fake = new FakeLocationProvider().AddFile(@"C:\a\1.txt", 1).AddFile(@"C:\a\2.txt", 2).AddFile(@"C:\b\3.txt", 3);
        var faults = new FaultInjectingLocationProvider(fake).Inject(@"\\nas\media", new Fault.Hang());
        var services = TabTestSupport.Services(faults);
        var dead = new TabViewModel(services, P(@"C:\a"));
        var alive = new TabViewModel(services, P(@"C:\a"));
        using var stop = new CancellationTokenSource();

        // The dead share never answers: its tab stays loading, and nothing else waits for it.
        var hung = dead.NavigateAsync(P(@"\\nas\media"), stop.Token);
        await alive.NavigateAsync(P(@"C:\a"), CancellationToken.None);
        await alive.NavigateAsync(P(@"C:\b"), CancellationToken.None);

        Assert.False(hung.IsCompleted, "the dead share is still being waited for");
        Assert.Equal(FolderLoadState.Loaded, alive.Folder.State);
        Assert.Equal(["3.txt"], alive.Folder.Entries.Select(e => e.Name));

        // Leaving the dead share (navigating away) frees the tab at once.
        await dead.NavigateAsync(P(@"C:\a"), CancellationToken.None);

        Assert.Equal(FolderLoadState.Loaded, dead.Folder.State);
        Assert.Equal(2, dead.Folder.Entries.Count);
        stop.Cancel();
        try
        {
            await hung.WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (OperationCanceledException)
        {
            // The old navigation ended when the tab left it.
        }

        dead.Dispose();
        alive.Dispose();
    }

    [Fact]
    public async Task TheDeadShare_ComesBackToLife_WhenItAnswersAgain()
    {
        var fake = new FakeLocationProvider().AddFile(@"\\nas\media\film.mkv", 1);
        var faults = new FaultInjectingLocationProvider(fake).Inject(@"\\nas\media", new Fault.NetworkDrop());
        var tab = new TabViewModel(TabTestSupport.Services(faults), P(@"C:\"));

        await tab.NavigateAsync(P(@"\\nas\media"), CancellationToken.None);
        Assert.Equal(FolderLoadState.Offline, tab.Folder.State);
        Assert.True(tab.Folder.IsRetryable);

        faults.Clear(@"\\nas\media");
        await tab.ReloadAsync(CancellationToken.None, forceFresh: true);

        Assert.Equal(FolderLoadState.Loaded, tab.Folder.State);
        Assert.Equal(["film.mkv"], tab.Folder.Entries.Select(e => e.Name));
        tab.Dispose();
    }
}

public sealed class SnapshotListTests
{
    [Fact]
    public void Replace_SwapsTheContents_WithOneResetAndTheCountAndIndexerNotified()
    {
        var list = new SnapshotList<string>();
        var resets = new List<NotifyCollectionChangedAction>();
        var properties = new List<string?>();
        list.CollectionChanged += (_, e) => resets.Add(e.Action);
        list.PropertyChanged += (_, e) => properties.Add(e.PropertyName);

        list.Replace(["a", "b", "c"]);

        Assert.Equal(3, list.Count);
        Assert.Equal("b", list[1]);
        Assert.Equal([NotifyCollectionChangedAction.Reset], resets);
        Assert.Contains("Count", properties);
        Assert.Contains("Item[]", properties);
        Assert.Equal(["a", "b", "c"], list);
    }

    [Fact]
    public void AsANonGenericList_ItReadsButNeverChanges()
    {
        var list = new SnapshotList<string>();
        list.Replace(["a", "b"]);
        var non = (System.Collections.IList)list;

        Assert.True(non.IsReadOnly);
        Assert.True(non.IsFixedSize);
        Assert.False(((System.Collections.ICollection)list).IsSynchronized);
        Assert.Same(list, ((System.Collections.ICollection)list).SyncRoot);
        Assert.Equal("b", non[1]);
        Assert.True(non.Contains("a"));
        Assert.False(non.Contains("z"));
        Assert.False(non.Contains(42));
        Assert.Equal(1, non.IndexOf("b"));
        Assert.Equal(-1, non.IndexOf("z"));
        Assert.Equal(-1, non.IndexOf(42));
        var copy = new string[3];
        ((System.Collections.ICollection)list).CopyTo(copy, 1);
        Assert.Equal(new string?[] { null, "a", "b" }, copy);
        Assert.Throws<NotSupportedException>(() => non.Add("x"));
        Assert.Throws<NotSupportedException>(() => non.Clear());
        Assert.Throws<NotSupportedException>(() => non.Insert(0, "x"));
        Assert.Throws<NotSupportedException>(() => non.Remove("a"));
        Assert.Throws<NotSupportedException>(() => non.RemoveAt(0));
        Assert.Throws<NotSupportedException>(() => non[0] = "x");
        Assert.Throws<ArgumentNullException>(() => list.Replace(null!));
    }
}
