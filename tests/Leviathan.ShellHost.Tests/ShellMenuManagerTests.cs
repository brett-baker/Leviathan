using Leviathan.Contracts;
using Leviathan.Testing;

namespace Leviathan.ShellHost.Tests;

public sealed class ShellMenuManagerTests
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(10);

    private sealed class FakeInstance(FakeSource source) : IShellMenuInstance
    {
        public int OwnerThread { get; } = Environment.CurrentManagedThreadId;

        public ApartmentState OwnerApartment { get; } = Thread.CurrentThread.GetApartmentState();

        public bool Disposed { get; private set; }

        public int DisposedOnThread { get; private set; }

        public IReadOnlyList<ShellMenuItem> Items { get; } =
        [
            new(0, 0, ShellMenuItemKind.Item, "&Open", "open", true, false, true, null, null),
            new(1, -1, ShellMenuItemKind.Separator, string.Empty, null, true, false, false, null, null),
            new(2, 5, ShellMenuItemKind.SubMenu, "Send to", null, true, false, false, null, null),
        ];

        public IReadOnlyList<ShellMenuItem> LoadSubMenu(IReadOnlyList<int> path)
        {
            source.OnCall?.Invoke();
            return [new ShellMenuItem(0, 10, ShellMenuItemKind.Item, "Desktop", null, true, false, false, null, null)];
        }

        public int Invoke(int itemId, string? verb, string? workingDirectory, nint ownerWindow)
        {
            source.Invoked.Add((itemId, verb, workingDirectory, ownerWindow));
            source.OnCall?.Invoke();
            return source.InvokeResult;
        }

        public void Dispose()
        {
            Disposed = true;
            DisposedOnThread = Environment.CurrentManagedThreadId;
        }
    }

    private sealed class FakeSource : IShellMenuSource
    {
        public List<FakeInstance> Created { get; } = [];

        public List<(int Id, string? Verb, string? Directory, nint Owner)> Invoked { get; } = [];

        public Action? OnCall { get; set; }

        public Action? OnOpen { get; set; }

        public int InvokeResult { get; set; }

        public IShellMenuInstance Open(ShellMenuRequest request)
        {
            OnOpen?.Invoke();
            var instance = new FakeInstance(this);
            lock (Created)
            {
                Created.Add(instance);
            }

            return instance;
        }
    }

    private static ShellMenuRequest Request(params string[] paths) => new(paths.Length == 0 ? [@"C:\a\1.txt"] : paths, false, false);

    private static async Task UntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "The condition was not reached in time.");
            await Task.Delay(5, TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task Open_BuildsTheMenuOnItsOwnStaThread_AndReturnsItsItems()
    {
        var source = new FakeSource();
        using var manager = new ShellMenuManager(source);

        var menu = await manager.OpenAsync(Request(), TestContext.Current.CancellationToken);

        Assert.Equal(3, menu.Items.Count);
        Assert.Equal("open", menu.Items[0].Verb);
        var instance = Assert.Single(source.Created);
        Assert.Equal(ApartmentState.STA, instance.OwnerApartment);
        Assert.NotEqual(Environment.CurrentManagedThreadId, instance.OwnerThread);
        Assert.Equal(1, manager.OpenCount);
        await manager.CloseAsync(menu.Id);
    }

    [Fact]
    public async Task Close_ReleasesTheHandlersOnTheThreadThatMadeThem()
    {
        var source = new FakeSource();
        using var manager = new ShellMenuManager(source);
        var menu = await manager.OpenAsync(Request(), TestContext.Current.CancellationToken);

        await manager.CloseAsync(menu.Id);

        var instance = source.Created[0];
        Assert.True(instance.Disposed);
        Assert.Equal(instance.OwnerThread, instance.DisposedOnThread);
        Assert.Equal(0, manager.OpenCount);
    }

    [Fact]
    public async Task Close_OfAMenuThatIsGone_IsNotAnError()
    {
        using var manager = new ShellMenuManager(new FakeSource());

        await manager.CloseAsync(Guid.NewGuid());
    }

    [Fact]
    public async Task LoadSubMenu_AndInvoke_ReachTheSameInstance()
    {
        var source = new FakeSource();
        using var manager = new ShellMenuManager(source);
        var menu = await manager.OpenAsync(Request(), TestContext.Current.CancellationToken);

        var children = await manager.LoadSubMenuAsync(menu.Id, [2], TestContext.Current.CancellationToken);
        var result = await manager.InvokeAsync(menu.Id, new ShellInvokeRequest(0, null, @"C:\a", 99), TestContext.Current.CancellationToken);

        Assert.Equal("Desktop", Assert.Single(children).Label);
        Assert.True(result.Succeeded);
        Assert.Equal((0, (string?)null, (string?)@"C:\a", (nint)99), Assert.Single(source.Invoked));
        await manager.CloseAsync(menu.Id);
    }

    [Fact]
    public async Task AShellThatRefusesTheCommand_GivesAFailureWithTheCode()
    {
        var source = new FakeSource { InvokeResult = unchecked((int)0x80004005) };
        using var manager = new ShellMenuManager(source);
        var menu = await manager.OpenAsync(Request(), TestContext.Current.CancellationToken);

        var result = await manager.InvokeAsync(menu.Id, new ShellInvokeRequest(0, null, null, 0), TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.Contains("0x80004005", result.Error, StringComparison.Ordinal);
        await manager.CloseAsync(menu.Id);
    }

    [Fact]
    public async Task AnInvokeThatOpensADialog_IsReportedStarted_AfterTheWait_AndTheMenuIsNotWrittenOff()
    {
        var time = new ManualTimeProvider();
        using var release = new ManualResetEventSlim();
        using var entered = new ManualResetEventSlim();
        var source = new FakeSource();
        using var manager = new ShellMenuManager(source, time);
        var menu = await manager.OpenAsync(Request(), TestContext.Current.CancellationToken);
        source.OnCall = () =>
        {
            entered.Set();
            release.Wait(Limit);
        };

        var invoke = manager.InvokeAsync(menu.Id, new ShellInvokeRequest(0, null, null, 0), TestContext.Current.CancellationToken);
        entered.Wait(Limit, TestContext.Current.CancellationToken);
        time.Advance(manager.InvokeWait);
        var result = await invoke;

        Assert.True(result.Succeeded);
        Assert.Equal(0, manager.Abandoned);
        release.Set();
        await manager.CloseAsync(menu.Id);
    }

    [Fact]
    public async Task OpeningAHungMenu_TimesOut_LeavesNothingOpen_AndIsCounted()
    {
        var time = new ManualTimeProvider();
        using var release = new ManualResetEventSlim();
        using var entered = new ManualResetEventSlim();
        var source = new FakeSource
        {
            OnOpen = () =>
            {
                entered.Set();
                release.Wait(Limit);
            },
        };
        using var manager = new ShellMenuManager(source, time);

        var open = manager.OpenAsync(Request(), TestContext.Current.CancellationToken);
        entered.Wait(Limit, TestContext.Current.CancellationToken);
        time.Advance(manager.OpenTimeout);

        var ex = await Assert.ThrowsAsync<TimeoutException>(() => open);

        Assert.Contains("shell extension", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, manager.OpenCount);
        Assert.Equal(1, manager.Abandoned);
        release.Set();
    }

    [Fact]
    public async Task EnoughHungMenus_RaiseUnhealthy()
    {
        var time = new ManualTimeProvider();
        using var release = new ManualResetEventSlim();
        var entered = 0;
        var source = new FakeSource
        {
            OnOpen = () =>
            {
                Interlocked.Increment(ref entered);
                release.Wait(Limit);
            },
        };
        using var manager = new ShellMenuManager(source, time, maxAbandoned: 2);
        var raised = 0;
        manager.Unhealthy += (_, _) => Interlocked.Increment(ref raised);

        for (var i = 1; i <= 2; i++)
        {
            var open = manager.OpenAsync(Request(), TestContext.Current.CancellationToken);
            await UntilAsync(() => Volatile.Read(ref entered) == i);
            time.Advance(manager.OpenTimeout);
            await Assert.ThrowsAsync<TimeoutException>(() => open);
        }

        Assert.Equal(1, raised);
        release.Set();
    }

    [Fact]
    public async Task AHandlerThatThrows_FailsTheOpen_AndLeavesNothingBehind()
    {
        // A shell handler fails with a COMException; that is exactly the failure being simulated, so the rule against throwing it does not apply.
#pragma warning disable CA2201
        var source = new FakeSource { OnOpen = () => throw new System.Runtime.InteropServices.COMException("handler failed") };
#pragma warning restore CA2201
        using var manager = new ShellMenuManager(source);

        await Assert.ThrowsAsync<System.Runtime.InteropServices.COMException>(() => manager.OpenAsync(Request(), TestContext.Current.CancellationToken));

        Assert.Equal(0, manager.OpenCount);
        Assert.Equal(0, manager.Abandoned);
    }

    [Fact]
    public async Task TooManyOpenMenus_CloseTheOldestOne()
    {
        var source = new FakeSource();
        using var manager = new ShellMenuManager(source, maxOpen: 2);
        var first = await manager.OpenAsync(Request(), TestContext.Current.CancellationToken);
        await manager.OpenAsync(Request(), TestContext.Current.CancellationToken);

        await manager.OpenAsync(Request(), TestContext.Current.CancellationToken);

        Assert.Equal(2, manager.OpenCount);
        Assert.True(source.Created[0].Disposed);
        await Assert.ThrowsAsync<KeyNotFoundException>(() => manager.LoadSubMenuAsync(first.Id, [2], TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AMenuNobodyUses_IsClosedAfterTheIdleTimeout()
    {
        var time = new ManualTimeProvider();
        var source = new FakeSource();
        using var manager = new ShellMenuManager(source, time);
        await manager.OpenAsync(Request(), TestContext.Current.CancellationToken);

        time.Advance(manager.IdleTimeout - TimeSpan.FromSeconds(1));
        manager.SweepIdle();
        Assert.Equal(1, manager.OpenCount);

        time.Advance(TimeSpan.FromSeconds(1));
        manager.SweepIdle();

        await UntilAsync(() => manager.OpenCount == 0);
        await UntilAsync(() => source.Created[0].Disposed);
    }

    [Fact]
    public async Task AMenuUsedRecently_IsNotSwept()
    {
        var time = new ManualTimeProvider();
        using var manager = new ShellMenuManager(new FakeSource(), time);
        var menu = await manager.OpenAsync(Request(), TestContext.Current.CancellationToken);
        time.Advance(manager.IdleTimeout - TimeSpan.FromSeconds(1));
        await manager.LoadSubMenuAsync(menu.Id, [2], TestContext.Current.CancellationToken);

        time.Advance(TimeSpan.FromSeconds(30));
        manager.SweepIdle();

        Assert.Equal(1, manager.OpenCount);
        await manager.CloseAsync(menu.Id);
    }

    [Theory]
    [InlineData(new string[0], false)]
    [InlineData(new[] { "relative\\file.txt" }, false)]
    [InlineData(new[] { "" }, false)]
    [InlineData(new[] { @"C:\a", @"C:\b" }, true)]
    public async Task BadRequests_AreRefusedBeforeAnythingStarts(string[] paths, bool background)
    {
        var source = new FakeSource();
        using var manager = new ShellMenuManager(source);

        await Assert.ThrowsAsync<ArgumentException>(() => manager.OpenAsync(new ShellMenuRequest(paths, background, false), TestContext.Current.CancellationToken));

        Assert.Empty(source.Created);
    }

    [Fact]
    public async Task AnUnknownMenu_SaysToRightClickAgain()
    {
        using var manager = new ShellMenuManager(new FakeSource());

        var ex = await Assert.ThrowsAsync<KeyNotFoundException>(() => manager.InvokeAsync(Guid.NewGuid(), new ShellInvokeRequest(0, null, null, 0), TestContext.Current.CancellationToken));

        Assert.Contains("Right-click again", ex.Message, StringComparison.Ordinal);
    }
}
