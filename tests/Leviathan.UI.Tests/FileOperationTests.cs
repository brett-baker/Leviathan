using FlaUI.Core.Input;
using FlaUI.Core.WindowsAPI;

namespace Leviathan.UI.Tests;

/// <summary>
/// Renaming, deleting and undoing real files in a folder of the test's own, with real keys (build plan H1). The effect is checked on
/// the disk, not on the screen: the worker does the work, and what counts is what it left behind.
/// </summary>
public sealed class FileOperationTests : IAsyncLifetime
{
    private AppSession _app = null!;
    private string _folder = null!;

    public async ValueTask InitializeAsync()
    {
        _folder = Path.Combine(Path.GetTempPath(), "leviathan-ui-files-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_folder);
        await File.WriteAllTextAsync(Path.Combine(_folder, "alpha.txt"), "a");
        await File.WriteAllTextAsync(Path.Combine(_folder, "bravo.txt"), "b");
        var json = $$"""{ "General": { "NewTabLocation": "{{_folder.Replace("\\", "\\\\", StringComparison.Ordinal)}}" } }""";
        _app = await AppSession.StartAsync(json);
        Wait.Until(() => _app.Has("alpha.txt"), TimeSpan.FromSeconds(20), "the files of the test folder");
    }

    public async ValueTask DisposeAsync()
    {
        await _app.DisposeAsync();
        try
        {
            Directory.Delete(_folder, recursive: true);
        }
        catch (IOException)
        {
            // Best effort.
        }
    }

    [Fact]
    public void Rename_F2_GivesTheSelectedFileANewName()
    {
        var row = _app.PointOf("alpha.txt");

        using (_app.Acting())
        {
            AppSession.Tap(row);
            AppSession.Press(VirtualKeyShort.F2);
            AppSession.TypeText("renamed");
            AppSession.Press(VirtualKeyShort.RETURN);
        }

        Wait.Until(() => File.Exists(Path.Combine(_folder, "renamed.txt")), TimeSpan.FromSeconds(10), "renamed.txt");
        Assert.False(File.Exists(Path.Combine(_folder, "alpha.txt")));
        Assert.True(File.Exists(Path.Combine(_folder, "bravo.txt")));
    }

    [Fact]
    public void Delete_SendsTheFileToTheRecycleBin_AndCtrlZ_BringsItBack()
    {
        var row = _app.PointOf("bravo.txt");
        var path = Path.Combine(_folder, "bravo.txt");

        using (_app.Acting())
        {
            AppSession.Tap(row);
            AppSession.Press(VirtualKeyShort.DELETE);
        }

        Wait.Until(() => !File.Exists(path), TimeSpan.FromSeconds(20), "bravo.txt to leave the folder");
        Assert.True(File.Exists(Path.Combine(_folder, "alpha.txt")));

        using (_app.Acting())
        {
            AppSession.Press(VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_Z);
        }

        Wait.Until(() => File.Exists(path), TimeSpan.FromSeconds(20), "bravo.txt to come back");
        Assert.Equal("b", File.ReadAllText(path));
    }

    [Fact]
    public void Tabs_DraggingATabIntoTheWindow_TearsItOffToAWindowOfItsOwn()
    {
        _app.FocusFileList();
        using (_app.Acting("opening a tab", budgetMs: 150))
        {
            AppSession.Press(VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_T);
        }

        var tabs = _app.Window.FindAllDescendants(cf => cf.ByControlType(FlaUI.Core.Definitions.ControlType.TabItem));
        var from = tabs[^1].GetClickablePoint();
        var list = _app.Get("FileList").BoundingRectangle;
        var to = new System.Drawing.Point(list.Left + 600, list.Top + 300);
        var before = _app.WindowCount();

        // Press on the tab, move down in steps (the strip only starts a drag after the pointer has moved a little), release in the file list.
        // A new window has to be made: up to 400 ms is the cost of that (spec 3.1 budgets a new tab at 150 ms, a window is more).
        using (_app.Acting("tearing a tab off", budgetMs: 400))
        {
            HandMouse.Drag(from, to, steps: 20, pauseMs: 25);
        }

        Wait.Until(() => _app.WindowCount() == before + 1, TimeSpan.FromSeconds(10), "a second window");
    }
}
