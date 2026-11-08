using Leviathan.Contracts;

namespace Leviathan.ShellHost.Tests;

public sealed class PreviewHandlerLocatorTests
{
    private const string Handler = "{11111111-2222-3333-4444-555555555555}";
    private const string Other = "{99999999-2222-3333-4444-555555555555}";

    private sealed class FakeRegistry(Dictionary<string, string> values) : IClassesRegistry
    {
        public string? GetDefault(string subKey) => values.GetValueOrDefault(subKey);
    }

    private static string Key(string root) => $@"{root}\shellex\{PreviewHandlerLocator.HandlerKey}";

    [Fact]
    public void Find_UsesTheHandlerRegisteredOnTheExtension()
    {
        var registry = new FakeRegistry(new() { [Key(".docx")] = Handler });

        Assert.Equal(Handler, PreviewHandlerLocator.Find(".docx", registry));
    }

    [Fact]
    public void Find_FallsBackToTheProgId()
    {
        var registry = new FakeRegistry(new() { [".docx"] = "Word.Document.12", [Key("Word.Document.12")] = Handler });

        Assert.Equal(Handler, PreviewHandlerLocator.Find(".docx", registry));
    }

    [Fact]
    public void Find_FallsBackToSystemFileAssociations()
    {
        var registry = new FakeRegistry(new() { [Key(@"SystemFileAssociations\.docx")] = Handler });

        Assert.Equal(Handler, PreviewHandlerLocator.Find(".docx", registry));
    }

    [Fact]
    public void Find_PrefersTheExtensionOverTheProgId()
    {
        var registry = new FakeRegistry(new() { [Key(".docx")] = Handler, [".docx"] = "Word.Document.12", [Key("Word.Document.12")] = Other });

        Assert.Equal(Handler, PreviewHandlerLocator.Find(".docx", registry));
    }

    [Fact]
    public void Find_OfAnUnregisteredType_IsNull() => Assert.Null(PreviewHandlerLocator.Find(".docx", new FakeRegistry([])));

    [Fact]
    public void Find_IgnoresAValueThatIsNotAClassId()
    {
        var registry = new FakeRegistry(new() { [Key(".docx")] = "not a guid" });

        Assert.Null(PreviewHandlerLocator.Find(".docx", registry));
    }

    [Theory]
    [InlineData("")]
    [InlineData("docx")]
    [InlineData(@".a\b")]
    [InlineData(".a/b")]
    public void Find_OfAnOddExtension_IsNull(string extension) => Assert.Null(PreviewHandlerLocator.Find(extension, new FakeRegistry(new() { [Key(extension)] = Handler })));
}

public sealed class ShellPreviewManagerTests
{
    private static readonly ShellPreviewRequest Request = new(@"C:\docs\a.docx", 1234, new ShellRect(10, 20, 300, 200));

    private sealed class FakeInstance : IShellPreviewInstance
    {
        public int OwnerThread { get; } = Environment.CurrentManagedThreadId;

        public ApartmentState OwnerApartment { get; } = Thread.CurrentThread.GetApartmentState();

        public List<ShellRect> Moves { get; } = [];

        public bool Disposed { get; private set; }

        public int DisposedOnThread { get; private set; }

        public string Handler => "{fake}";

        public void Move(ShellRect bounds) => Moves.Add(bounds);

        public void Dispose()
        {
            Disposed = true;
            DisposedOnThread = Environment.CurrentManagedThreadId;
        }
    }

    private sealed class FakeSource : IShellPreviewSource
    {
        public List<FakeInstance> Created { get; } = [];

        public string? Refusal { get; set; }

        public Action? OnOpen { get; set; }

        public IShellPreviewInstance? Open(ShellPreviewRequest request, out string? error)
        {
            OnOpen?.Invoke();
            if (Refusal is not null)
            {
                error = Refusal;
                return null;
            }

            var instance = new FakeInstance();
            lock (Created)
            {
                Created.Add(instance);
            }

            error = null;
            return instance;
        }
    }

    [Fact]
    public async Task Open_MakesThePreviewOnAnStaThread_AndReportsTheHandler()
    {
        var source = new FakeSource();
        using var manager = new ShellPreviewManager(source);

        var result = await manager.OpenAsync(Request, TestContext.Current.CancellationToken);

        Assert.NotEqual(Guid.Empty, result.Id);
        Assert.Equal("{fake}", result.Handler);
        Assert.Null(result.Error);
        Assert.Equal(ApartmentState.STA, source.Created[0].OwnerApartment);
        Assert.NotEqual(Environment.CurrentManagedThreadId, source.Created[0].OwnerThread);
        Assert.Equal(1, manager.OpenCount);
    }

    [Fact]
    public async Task Open_WhenThereIsNoHandler_ReturnsTheReason_AndKeepsNothingOpen()
    {
        var source = new FakeSource { Refusal = "No preview handler is registered for .docx files." };
        using var manager = new ShellPreviewManager(source);

        var result = await manager.OpenAsync(Request, TestContext.Current.CancellationToken);

        Assert.Equal(Guid.Empty, result.Id);
        Assert.Equal("No preview handler is registered for .docx files.", result.Error);
        Assert.Equal(0, manager.OpenCount);
    }

    [Fact]
    public async Task Move_ReachesThePreview_OnItsOwnThread()
    {
        var source = new FakeSource();
        using var manager = new ShellPreviewManager(source);
        var id = (await manager.OpenAsync(Request, TestContext.Current.CancellationToken)).Id;

        await manager.MoveAsync(id, new ShellRect(1, 2, 3, 4), TestContext.Current.CancellationToken);

        Assert.Equal([new ShellRect(1, 2, 3, 4)], source.Created[0].Moves);
    }

    [Fact]
    public async Task Move_OfAnUnknownPreview_IsIgnored()
    {
        using var manager = new ShellPreviewManager(new FakeSource());

        await manager.MoveAsync(Guid.NewGuid(), new ShellRect(1, 2, 3, 4), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Close_ReleasesThePreview_OnTheThreadThatMadeIt()
    {
        var source = new FakeSource();
        using var manager = new ShellPreviewManager(source);
        var id = (await manager.OpenAsync(Request, TestContext.Current.CancellationToken)).Id;

        await manager.CloseAsync(id);

        Assert.True(source.Created[0].Disposed);
        Assert.Equal(source.Created[0].OwnerThread, source.Created[0].DisposedOnThread);
        Assert.Equal(0, manager.OpenCount);
    }

    [Fact]
    public async Task Close_OfAPreviewThatIsGone_IsNotAnError()
    {
        using var manager = new ShellPreviewManager(new FakeSource());

        await manager.CloseAsync(Guid.NewGuid());
    }

    [Fact]
    public async Task OpeningMoreThanTheLimit_ClosesTheOldest()
    {
        var source = new FakeSource();
        using var manager = new ShellPreviewManager(source, maxOpen: 2);

        await manager.OpenAsync(Request, TestContext.Current.CancellationToken);
        await manager.OpenAsync(Request, TestContext.Current.CancellationToken);
        await manager.OpenAsync(Request, TestContext.Current.CancellationToken);

        Assert.Equal(2, manager.OpenCount);
        Assert.True(source.Created[0].Disposed);
        Assert.False(source.Created[2].Disposed);
    }

    [Fact]
    public async Task AHandlerThatNeverReturns_TimesOut_AndIsWrittenOff()
    {
        var gate = new ManualResetEventSlim();
        var source = new FakeSource { OnOpen = () => gate.Wait(TimeSpan.FromSeconds(30)) };
        using var manager = new ShellPreviewManager(source) { OpenTimeout = TimeSpan.FromMilliseconds(200) };

        await Assert.ThrowsAsync<TimeoutException>(() => manager.OpenAsync(Request, TestContext.Current.CancellationToken));

        Assert.Equal(1, manager.Abandoned);
        Assert.Equal(0, manager.OpenCount);
        gate.Set();
    }

    [Fact]
    public async Task EnoughHungHandlers_RaiseUnhealthy()
    {
        var gate = new ManualResetEventSlim();
        var source = new FakeSource { OnOpen = () => gate.Wait(TimeSpan.FromSeconds(30)) };
        using var manager = new ShellPreviewManager(source, maxAbandoned: 2) { OpenTimeout = TimeSpan.FromMilliseconds(100) };
        var raised = 0;
        manager.Unhealthy += (_, _) => raised++;

        for (var i = 0; i < 2; i++)
        {
            await Assert.ThrowsAsync<TimeoutException>(() => manager.OpenAsync(Request, TestContext.Current.CancellationToken));
        }

        Assert.Equal(1, raised);
        gate.Set();
    }

    [Theory]
    [InlineData("")]
    [InlineData(@"relative\a.docx")]
    public async Task Open_OfABadPath_Throws(string path)
    {
        using var manager = new ShellPreviewManager(new FakeSource());

        await Assert.ThrowsAsync<ArgumentException>(() => manager.OpenAsync(Request with { Path = path }, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Open_OfAnEmptyRectangle_Throws()
    {
        using var manager = new ShellPreviewManager(new FakeSource());

        await Assert.ThrowsAsync<ArgumentException>(() => manager.OpenAsync(Request with { Bounds = new ShellRect(0, 0, 0, 10) }, TestContext.Current.CancellationToken));
    }
}
