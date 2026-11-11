using FlaUI.Core.AutomationElements;
using FlaUI.Core.Input;
using FlaUI.Core.WindowsAPI;

namespace Leviathan.UI.Tests;

/// <summary>
/// Search and the space map through the real window (build plan Phase 4, F3). Written, not run in the session that wrote it: like every test in this project it needs
/// LEVIATHAN_UI_TESTS=1 and the desktop to itself. The folders are made by the test in its own temp folder; nothing outside it is read, changed or deleted.
/// </summary>
public sealed class SearchAndSpaceMapTests : IAsyncLifetime
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "leviathan-ui-search-" + Guid.NewGuid().ToString("N"));
    private AppSession? _app;

    public ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(Path.Combine(_folder, "movies"));
        Directory.CreateDirectory(Path.Combine(_folder, "docs"));
        File.WriteAllBytes(Path.Combine(_folder, "movies", "holiday one.mkv"), new byte[4000]);
        File.WriteAllBytes(Path.Combine(_folder, "movies", "holiday two.mkv"), new byte[3000]);
        File.WriteAllBytes(Path.Combine(_folder, "docs", "report.pdf"), new byte[2000]);
        File.WriteAllBytes(Path.Combine(_folder, "notes.txt"), new byte[10]);
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        if (_app is not null)
        {
            await _app.DisposeAsync();
        }

        try
        {
            Directory.Delete(_folder, recursive: true);
        }
        catch (IOException)
        {
            // Best effort.
        }
    }

    private static string Json(string text) => text.Replace("\\", "\\\\", StringComparison.Ordinal);

    [Fact]
    public async Task SpaceMap_OpensInItsOwnWindow_AndFillsIn()
    {
        _app = await AppSession.StartAsync(
            $$"""{ "General": { "NewTabLocation": "{{Json(_folder)}}" } }""",
            new Dictionary<string, string> { ["LEVIATHAN_OPEN_SPACEMAP"] = _folder });

        Wait.TryUntil(() => _app.FindTopLevel("Space map") is not null, TimeSpan.FromSeconds(30));
        var map = _app.FindTopLevel("Space map");

        Assert.NotNull(map);
        Assert.Contains(Path.GetFileName(_folder), map.Title, StringComparison.Ordinal);
        Assert.True(_app.IsRunning);
    }

    [Fact]
    public async Task Search_CtrlF_TypeAWord_Enter_ShowsTheMatchesInTheSameTab()
    {
        _app = await AppSession.StartAsync($$"""{ "General": { "NewTabLocation": "{{Json(_folder)}}" } }""");
        var box = _app.Get("Search this folder and below");
        var before = _app.TabCount();
        Assert.True(Native.IsForeground(_app.Window), "the app must be the window in front before keys are sent");

        _app.FocusFileList();
        using (_app.Acting("focusing the search box", budgetMs: 150))
        {
            AppSession.Press(VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_F);
        }

        AppSession.TypeText("holiday");
        AppSession.Press(VirtualKeyShort.RETURN);

        Assert.True(Wait.TryUntil(() => _app.Window.FindFirstDescendant(cf => cf.ByName("holiday one.mkv")) is not null, TimeSpan.FromSeconds(20)));
        Assert.NotNull(_app.Window.FindFirstDescendant(cf => cf.ByName("holiday two.mkv")));
        Assert.Null(_app.Window.FindFirstDescendant(cf => cf.ByName("report.pdf")));
        Assert.Equal(before, _app.TabCount());
        Assert.NotNull(box);
    }
}
