using FlaUI.Core.Input;

namespace Leviathan.UI.Tests;

/// <summary>
/// Changing between Details, List and Thumbnails in a folder of 40,000 files must not freeze the window (spec 3.2, 8.1).
/// The budget here is looser than the 50 ms of a small folder (one layout pass over a huge list is allowed a little more) but a
/// freeze of seconds fails.
/// </summary>
public sealed class LargeFolderViewTests : IAsyncLifetime
{
    // A real 1x1 PNG, so image files go through the shell's thumbnailer like photos do.
    private static readonly byte[] Png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==");

    private string _folder = null!;
    private AppSession _app = null!;

    public async ValueTask InitializeAsync()
    {
        _folder = Path.Combine(Path.GetTempPath(), "leviathan-ui-big-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_folder);
        string[] extensions = [".txt", ".jpg", ".pdf", ".docx", ".png"];
        for (var i = 0; i < 40_000; i++)
        {
            var extension = extensions[i % extensions.Length];
            await File.WriteAllBytesAsync(Path.Combine(_folder, $"file-{i:D5}{extension}"), extension is ".png" or ".jpg" ? Png : [1, 2, 3]);
        }

        var json = $$"""{ "General": { "NewTabLocation": "{{_folder.Replace("\\", "\\\\", StringComparison.Ordinal)}}" } }""";
        _app = await AppSession.StartAsync(json);
        await Task.Delay(TimeSpan.FromSeconds(8));
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
        }
    }

    [Fact]
    public void SwitchingViews_In40000Files_DoesNotFreezeTheWindow()
    {
        var thumbs = _app.PointOf("ThumbsSwitch");
        var list = _app.PointOf("ListSwitch");
        var details = _app.PointOf("DetailsSwitch");
        Mouse.Click(_app.PointOf("FileList"));
        Keyboard.TypeSimultaneously(FlaUI.Core.WindowsAPI.VirtualKeyShort.CONTROL, FlaUI.Core.WindowsAPI.VirtualKeyShort.KEY_A);
        Thread.Sleep(1000);
        var steps = new (string Name, System.Drawing.Point Where)[] { ("thumbnails", thumbs), ("list", list), ("details", details), ("list", list), ("thumbnails", thumbs), ("details", details) };
        var worst = new List<(string Name, long Took)>();
        foreach (var (name, where) in steps)
        {
            var started = Environment.TickCount64;
            Mouse.Click(where);

            // How long until the window answers again: a call into the app waits behind a frozen UI thread.
            _ = _app.Find("StallText");
            var took = Environment.TickCount64 - started;
            worst.Add((name, took));
            Thread.Sleep(1500);
        }

        Assert.True(worst.All(w => w.Took <= 1500), string.Join(" | ", worst.Select(w => $"{w.Name}: {w.Took} ms")));
    }
}
