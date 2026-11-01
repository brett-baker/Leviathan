using System.Collections.Specialized;
using Leviathan.Core.Paths;
using Leviathan.Core.Providers;
using Leviathan.Testing;
using Leviathan.ViewModels.Folders;
using Leviathan.ViewModels.Views;

namespace Leviathan.ViewModels.Tests;

public sealed class FolderViewModelTests
{
    private sealed class InlineUi : IUiContext
    {
        public int Posts { get; private set; }

        public void Post(Action action)
        {
            Posts++;
            action();
        }
    }

    private static LocationPath P(string path) => LocationPath.Parse(path);

    private static FolderViewModel Make(ILocationProvider provider, out InlineUi ui)
    {
        ui = new InlineUi();
        return new FolderViewModel(new LocationProviderRegistry([provider]), ui);
    }

    [Fact]
    public async Task LoadAsync_FolderWithItems_FillsEntriesAndIsLoaded()
    {
        var fake = new FakeLocationProvider().AddFile(@"C:\x\a.txt", 5).AddFile(@"C:\x\b.txt", 6).AddFolder(@"C:\x\sub");
        using var vm = Make(fake, out _);

        await vm.LoadAsync(P(@"C:\x"), CancellationToken.None);

        Assert.Equal(FolderLoadState.Loaded, vm.State);
        Assert.Equal(3, vm.Entries.Count);
        Assert.Equal(P(@"C:\x"), vm.Location);
        Assert.NotNull(vm.FirstRowsElapsed);
        Assert.NotNull(vm.TotalElapsed);
    }

    [Fact]
    public async Task LoadAsync_EmptyFolder_IsEmptyNotLoaded()
    {
        using var vm = Make(new FakeLocationProvider().AddFolder(@"C:\empty"), out _);

        await vm.LoadAsync(P(@"C:\empty"), CancellationToken.None);

        Assert.Equal(FolderLoadState.Empty, vm.State);
        Assert.Empty(vm.Entries);
    }

    [Fact]
    public async Task LoadAsync_LargeFolder_RaisesOneChangePerBatchNotPerItem()
    {
        var fake = new FakeLocationProvider();
        for (var i = 0; i < 1000; i++)
        {
            fake.AddFile(@"C:\big\f" + i + ".txt", 1);
        }

        using var vm = Make(fake, out var ui);
        vm.Options = new ListingOptions { BatchSize = 250 };
        var changes = new List<NotifyCollectionChangedAction>();
        vm.Entries.CollectionChanged += (_, e) => changes.Add(e.Action);

        await vm.LoadAsync(P(@"C:\big"), CancellationToken.None);

        Assert.Equal(1000, vm.Entries.Count);
        Assert.Equal([NotifyCollectionChangedAction.Reset, .. Enumerable.Repeat(NotifyCollectionChangedAction.Add, 4)], changes);
        Assert.True(ui.Posts < 20, $"expected a handful of UI posts, got {ui.Posts}");
    }

    [Fact]
    public async Task LoadAsync_SecondFolder_ReplacesRows()
    {
        var fake = new FakeLocationProvider().AddFile(@"C:\a\1.txt", 1).AddFile(@"C:\b\2.txt", 1).AddFile(@"C:\b\3.txt", 1);
        using var vm = Make(fake, out _);

        await vm.LoadAsync(P(@"C:\a"), CancellationToken.None);
        await vm.LoadAsync(P(@"C:\b"), CancellationToken.None);

        Assert.Equal(["2.txt", "3.txt"], vm.Entries.Select(e => e.Name).Order());
        Assert.Equal(P(@"C:\b"), vm.Location);
    }

    [Fact]
    public async Task LoadAsync_MissingFolder_IsNotFound_WithMessage()
    {
        using var vm = Make(new FakeLocationProvider(), out _);

        await vm.LoadAsync(P(@"C:\nope"), CancellationToken.None);

        Assert.Equal(FolderLoadState.NotFound, vm.State);
        Assert.False(string.IsNullOrEmpty(vm.Message));
        Assert.False(vm.IsRetryable);
    }

    [Fact]
    public async Task LoadAsync_AccessDenied_IsAccessDenied()
    {
        var fault = new FaultInjectingLocationProvider(new FakeLocationProvider().AddFolder(@"C:\secret"));
        fault.Inject(@"C:\secret", new Fault.AccessDenied());
        using var vm = Make(fault, out _);

        await vm.LoadAsync(P(@"C:\secret"), CancellationToken.None);

        Assert.Equal(FolderLoadState.AccessDenied, vm.State);
        Assert.Empty(vm.Entries);
    }

    [Fact]
    public async Task LoadAsync_NetworkDropMidListing_KeepsPartialRowsAndIsOfflineAndRetryable()
    {
        var fake = new FakeLocationProvider();
        for (var i = 0; i < 600; i++)
        {
            fake.AddFile(@"\\nas\media\f" + i + ".mkv", 1);
        }

        var fault = new FaultInjectingLocationProvider(fake);
        fault.Inject(@"\\nas\media", new Fault.NetworkDrop(AfterBatches: 1));
        using var vm = Make(fault, out _);
        vm.Options = new ListingOptions { BatchSize = 256 };

        await vm.LoadAsync(P(@"\\nas\media"), CancellationToken.None);

        Assert.Equal(FolderLoadState.Offline, vm.State);
        Assert.True(vm.IsRetryable);
        Assert.Equal(256, vm.Entries.Count);
    }

    [Fact]
    public async Task LoadAsync_Cancelled_IsCancelledAndNeverEmpty()
    {
        var fault = new FaultInjectingLocationProvider(new FakeLocationProvider().AddFolder(@"\\nas\media"));
        fault.Inject(@"\\nas\media", new Fault.Hang());
        using var vm = Make(fault, out _);
        using var cts = new CancellationTokenSource();

        var load = vm.LoadAsync(P(@"\\nas\media"), cts.Token);
        Assert.Equal(FolderLoadState.Loading, vm.State);
        await cts.CancelAsync();
        await load;

        Assert.Equal(FolderLoadState.Cancelled, vm.State);
        Assert.NotEqual(FolderLoadState.Empty, vm.State);
    }

    [Fact]
    public async Task LoadAsync_NavigatingAwayFromAHungShare_CancelsItAndShowsTheNewFolder()
    {
        var fake = new FakeLocationProvider().AddFolder(@"\\nas\media").AddFile(@"C:\local\a.txt", 1);
        var fault = new FaultInjectingLocationProvider(fake);
        fault.Inject(@"\\nas\media", new Fault.Hang());
        using var vm = Make(fault, out _);

        var hung = vm.LoadAsync(P(@"\\nas\media"), CancellationToken.None);
        await vm.LoadAsync(P(@"C:\local"), CancellationToken.None);
        await hung;

        Assert.Equal(FolderLoadState.Loaded, vm.State);
        Assert.Equal(P(@"C:\local"), vm.Location);
        Assert.Single(vm.Entries);
    }

    [Fact]
    public async Task Cancel_DuringListing_StopsIt()
    {
        var fault = new FaultInjectingLocationProvider(new FakeLocationProvider().AddFolder(@"\\nas\media"));
        fault.Inject(@"\\nas\media", new Fault.Hang());
        using var vm = Make(fault, out _);

        var load = vm.LoadAsync(P(@"\\nas\media"), CancellationToken.None);
        vm.Cancel();
        await load;

        Assert.Equal(FolderLoadState.Cancelled, vm.State);
    }

    [Fact]
    public async Task ReloadAsync_ListsTheSameFolderAgain()
    {
        var fake = new FakeLocationProvider().AddFile(@"C:\x\a.txt", 1);
        using var vm = Make(fake, out _);

        await vm.LoadAsync(P(@"C:\x"), CancellationToken.None);
        await vm.ReloadAsync(CancellationToken.None);

        Assert.Equal(2, fake.ListCalls[P(@"C:\x")]);
        Assert.Single(vm.Entries);
    }

    [Fact]
    public async Task ReloadAsync_BeforeAnyLoad_DoesNothing()
    {
        using var vm = Make(new FakeLocationProvider(), out _);

        await vm.ReloadAsync(CancellationToken.None);

        Assert.Equal(FolderLoadState.Idle, vm.State);
    }

    [Fact]
    public async Task LoadAsync_MostlyVideos_DetectsTheFolderKind()
    {
        var fake = new FakeLocationProvider().AddFile(@"C:\v\a.mkv", 1).AddFile(@"C:\v\b.mp4", 1).AddFile(@"C:\v\c.mov", 1);
        using var vm = Make(fake, out _);

        await vm.LoadAsync(P(@"C:\v"), CancellationToken.None);

        Assert.Equal(FolderKind.Videos, vm.Kind);
    }

    [Fact]
    public async Task LoadAsync_HiddenItems_FollowTheOptions()
    {
        var fake = new FakeLocationProvider().AddFile(@"C:\h\a.txt", 1);
        using var vm = Make(fake, out _);
        vm.Options = new ListingOptions { IncludeHidden = true };

        await vm.LoadAsync(P(@"C:\h"), CancellationToken.None);

        Assert.Single(vm.Entries);
    }

    [Fact]
    public async Task LoadAsync_LateBatchesFromAnOldLoad_NeverReachTheNewFolder()
    {
        // A UI context that holds posts until released simulates the dispatcher being busy while a newer load starts.
        var held = new List<Action>();
        var ui = new HeldUi(held);
        var fake = new FakeLocationProvider().AddFile(@"C:\old\1.txt", 1).AddFile(@"C:\new\2.txt", 1);
        using var vm = new FolderViewModel(new LocationProviderRegistry([fake]), ui);

        await vm.LoadAsync(P(@"C:\old"), CancellationToken.None);
        await vm.LoadAsync(P(@"C:\new"), CancellationToken.None);
        foreach (var action in held.ToList())
        {
            action();
        }

        Assert.Equal(["2.txt"], vm.Entries.Select(e => e.Name));
        Assert.Equal(P(@"C:\new"), vm.Location);
        Assert.Equal(FolderLoadState.Loaded, vm.State);
    }

    private sealed class HeldUi(List<Action> held) : IUiContext
    {
        public void Post(Action action) => held.Add(action);
    }
}
