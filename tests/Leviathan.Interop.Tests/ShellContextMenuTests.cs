using Leviathan.Contracts;

namespace Leviathan.Interop.Tests;

/// <summary>
/// The real shell's context menu on this machine, with whatever shell extensions are installed. The assertions are about what every
/// Windows has (Properties, Open with, a Send to submenu), not about particular third-party items.
/// </summary>
public sealed class ShellContextMenuTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "leviathan-menu-" + Guid.NewGuid().ToString("N"));
    private readonly StaMessageThread _thread = new("Leviathan test shell menu");

    public ShellContextMenuTests()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "note.txt"), "x");
        File.WriteAllText(Path.Combine(_root, "other.txt"), "y");
    }

    public void Dispose()
    {
        _thread.Dispose();
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // Best effort.
        }
    }

    private Task<T> OnThread<T>(Func<T> work) => _thread.RunAsync(work, TestContext.Current.CancellationToken);

    private static IEnumerable<ShellMenuItem> Flatten(IEnumerable<ShellMenuItem> items) =>
        items.SelectMany(i => new[] { i }.Concat(i.Children is null ? [] : Flatten(i.Children)));

    [Fact]
    public async Task AFilesMenu_HasTheStandardEntries_WithVerbsAndLabels()
    {
        var items = await OnThread(() =>
        {
            using var menu = ShellContextMenu.Create([Path.Combine(_root, "note.txt")], background: false, extended: false);
            return menu.Items;
        });

        Assert.True(items.Count >= 5, $"Only {items.Count} items.");
        Assert.Contains(items, i => i.Verb == "properties");
        Assert.Contains(items, i => i.Kind == ShellMenuItemKind.Separator);
        Assert.All(items.Where(i => i.Kind != ShellMenuItemKind.Separator), i => Assert.False(string.IsNullOrEmpty(i.Label)));
        Assert.Equal(items.Select(i => i.Position), Enumerable.Range(0, items.Count));
    }

    [Fact]
    public async Task AMenuForSeveralFiles_IsBuilt()
    {
        var count = await OnThread(() =>
        {
            using var menu = ShellContextMenu.Create([Path.Combine(_root, "note.txt"), Path.Combine(_root, "other.txt")], background: false, extended: false);
            return menu.Items.Count;
        });

        Assert.True(count >= 5);
    }

    [Fact]
    public async Task ThePositionsLeadToSubMenus_WhichLoadOnRequest()
    {
        var (before, loaded) = await OnThread(() =>
        {
            using var menu = ShellContextMenu.Create([Path.Combine(_root, "note.txt")], background: false, extended: false);
            var withSub = menu.Items.First(i => i.Kind == ShellMenuItemKind.SubMenu);
            return (withSub, menu.LoadSubMenu([withSub.Position]));
        });

        Assert.Null(before.Children);
        Assert.NotNull(loaded);
        Assert.All(loaded.Where(i => i.Kind != ShellMenuItemKind.Separator), i => Assert.False(string.IsNullOrEmpty(i.Label)));
    }

    [Fact]
    public async Task AFoldersBackgroundMenu_IsBuilt()
    {
        var items = await OnThread(() =>
        {
            using var menu = ShellContextMenu.Create([_root], background: true, extended: false);
            return menu.Items;
        });

        Assert.NotEmpty(items);
    }

    [Fact]
    public async Task TheExtendedMenu_IsAtLeastAsLongAsTheNormalOne()
    {
        var (normal, extended) = await OnThread(() =>
        {
            using var a = ShellContextMenu.Create([Path.Combine(_root, "note.txt")], background: false, extended: false);
            using var b = ShellContextMenu.Create([Path.Combine(_root, "note.txt")], background: false, extended: true);
            return (Flatten(a.Items).Count(), Flatten(b.Items).Count());
        });

        Assert.True(extended >= normal);
    }

    [Fact]
    public async Task AnUnknownVerb_IsRefusedWithAFailureCode_NotAnException()
    {
        var hr = await OnThread(() =>
        {
            using var menu = ShellContextMenu.Create([Path.Combine(_root, "note.txt")], background: false, extended: false);
            return menu.Invoke(-1, "no-such-verb-leviathan", null, 0);
        });

        Assert.True(hr < 0);
    }

    [Fact]
    public async Task AMissingFile_FailsToBuildAMenu()
    {
        await Assert.ThrowsAnyAsync<Exception>(() => OnThread(() => ShellContextMenu.Create([Path.Combine(_root, "gone.txt")], background: false, extended: false)));
    }

    [Fact]
    public async Task NoPaths_IsAnArgumentError()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => OnThread(() => ShellContextMenu.Create([], background: false, extended: false)));
    }

    [Fact]
    public async Task UsingADisposedMenu_Throws_AndDisposingTwiceIsFine()
    {
        await OnThread(() =>
        {
            var menu = ShellContextMenu.Create([Path.Combine(_root, "note.txt")], background: false, extended: false);
            menu.Dispose();
            menu.Dispose();
            Assert.Throws<ObjectDisposedException>(() => menu.LoadSubMenu([0]));
            return 0;
        });
    }
}

public sealed class StaMessageThreadTests
{
    [Fact]
    public async Task Work_RunsOnAnStaThread_InTheOrderItWasQueued()
    {
        using var thread = new StaMessageThread("Leviathan test thread");
        var order = new List<int>();

        var apartment = await thread.RunAsync(() => Thread.CurrentThread.GetApartmentState(), TestContext.Current.CancellationToken);
        var first = thread.RunAsync(() =>
        {
            order.Add(1);
            return 1;
        }, TestContext.Current.CancellationToken);
        var second = thread.RunAsync(() =>
        {
            order.Add(2);
            return 2;
        }, TestContext.Current.CancellationToken);
        await Task.WhenAll(first, second);

        Assert.Equal(ApartmentState.STA, apartment);
        Assert.Equal([1, 2], order);
    }

    [Fact]
    public async Task AnExceptionInTheWork_ReachesTheCaller_AndTheThreadCarriesOn()
    {
        using var thread = new StaMessageThread("Leviathan test thread");

        await Assert.ThrowsAsync<InvalidOperationException>(() => thread.RunAsync<int>(() => throw new InvalidOperationException("x"), TestContext.Current.CancellationToken));

        Assert.Equal(5, await thread.RunAsync(() => 5, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AfterDispose_NewWorkIsRefused()
    {
        var thread = new StaMessageThread("Leviathan test thread");
        thread.Dispose();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => thread.RunAsync(() => 1, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task WorkQueuedBeforeDispose_StillRuns()
    {
        var thread = new StaMessageThread("Leviathan test thread");
        var queued = thread.RunAsync(() => 7, TestContext.Current.CancellationToken);

        thread.Dispose();

        Assert.Equal(7, await queued);
    }
}
