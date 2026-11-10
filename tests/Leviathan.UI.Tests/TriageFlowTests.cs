using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Input;
using FlaUI.Core.WindowsAPI;

namespace Leviathan.UI.Tests;

/// <summary>What the tests need from Windows that UI Automation does not give.</summary>
internal static partial class Native
{
    [LibraryImport("user32.dll")]
    private static partial nint GetForegroundWindow();

    /// <summary>True when <paramref name="window"/> is the window that receives the keyboard.</summary>
    public static bool IsForeground(Window window) => GetForegroundWindow() == window.Properties.NativeWindowHandle.ValueOrDefault;
}

/// <summary>
/// The triage flow of spec §10 driven through the real triage window with the real keyboard (build plan G2): open it from the file
/// list, Keep, Discard, Undo, Skip and Restore, and read the results from the disk, the tag stream and the Recycle Bin. The files are
/// made in a folder of the test's own and the app has its own data folder and worker. Like every test in this project it needs
/// LEVIATHAN_UI_TESTS=1 and the desktop to itself; keys are sent only after the triage window has been checked to be the one in front.
/// </summary>
public sealed class TriageFlowTests
{
    private static async Task<string?> FfmpegAsync()
    {
        var folder = Environment.GetEnvironmentVariable("LEVIATHAN_FFMPEG_DIR");
        var candidates = folder is { Length: > 0 }
            ? [Path.Combine(folder, "ffmpeg.exe")]
            : (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(';', StringSplitOptions.RemoveEmptyEntries).Select(p => Path.Combine(p.Trim(), "ffmpeg.exe")).ToArray();
        await Task.CompletedTask;
        return candidates.FirstOrDefault(File.Exists);
    }

    private static async Task<bool> MakeClipAsync(string ffmpeg, string path, int seconds)
    {
        var info = new ProcessStartInfo(ffmpeg) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
        foreach (var argument in new[] { "-y", "-v", "error", "-f", "lavfi", "-i", $"testsrc=duration={seconds}:size=160x90:rate=10", "-f", "lavfi", "-i", $"sine=duration={seconds}", "-c:v", "mpeg4", "-c:a", "aac", "-shortest", path })
        {
            info.ArgumentList.Add(argument);
        }

        using var process = Process.Start(info)!;
        await process.WaitForExitAsync();
        return process.ExitCode == 0 && File.Exists(path);
    }

    private static string Json(string text) => text.Replace("\\", "\\\\", StringComparison.Ordinal);

    private static int[] TagsOf(string path)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(path + ":leviathan.tags"));
        return [.. document.RootElement.GetProperty("tags").EnumerateArray().Select(t => t.GetInt32())];
    }

    // Names of the items in the Recycle Bin with the folder they came from (the shell's own view of it, as Explorer shows it).
    private static List<(string Name, string From)> RecycleBin()
    {
        var shell = Activator.CreateInstance(Type.GetTypeFromProgID("Shell.Application")!)!;
        dynamic bin = ((dynamic)shell).NameSpace(10);
        var found = new List<(string, string)>();
        foreach (var item in bin.Items())
        {
            found.Add(((string)item.Name, (string)bin.GetDetailsOf(item, 1)));
        }

        return found;
    }

    private static int Position(Window triage)
    {
        var text = triage.FindFirstDescendant(cf => cf.ByAutomationId("ProgressText"))?.Name ?? string.Empty;
        var match = Regex.Match(text, @"(\d[\d,.]*)\s*/\s*(\d[\d,.]*)");
        return match.Success ? int.Parse(match.Groups[1].Value.Replace(",", string.Empty, StringComparison.Ordinal).Replace(".", string.Empty, StringComparison.Ordinal), System.Globalization.CultureInfo.InvariantCulture) : -1;
    }

    private static void KeyInTriage(Window triage, params VirtualKeyShort[] keys)
    {
        // Real keys go to whatever has the keyboard: they are sent only while the triage window of this test is the one in front.
        triage.SetForeground();
        Assert.True(Wait.TryUntil(() => Native.IsForeground(triage), TimeSpan.FromSeconds(5)), "the triage window is in front before a key is sent");
        AppSession.Press(keys);
    }

    [Fact]
    public async Task Triage_KeepDiscardUndoSkipAndRestore_LeaveTheRightFilesAndTags()
    {
        var ffmpeg = await FfmpegAsync();
        Assert.SkipUnless(ffmpeg is not null, "ffmpeg is not installed here (set LEVIATHAN_FFMPEG_DIR or add it to PATH).");

        var root = Path.Combine(Path.GetTempPath(), "leviathan-ui-triage-" + Guid.NewGuid().ToString("N"));
        var inbox = Path.Combine(root, "inbox");
        var keep = Path.Combine(root, "keep");
        Directory.CreateDirectory(inbox);
        Directory.CreateDirectory(keep);
        try
        {
            string[] names = ["clip1.mp4", "clip2.mp4", "clip3.mp4", "clip4.mp4"];
            foreach (var name in names)
            {
                Assert.True(await MakeClipAsync(ffmpeg!, Path.Combine(inbox, name), 6), $"ffmpeg made {name}");
            }

            var settings = $$"""{ "General": { "NewTabLocation": "{{Json(inbox)}}" }, "Media": { "TriageKeepFolder": "{{Json(keep)}}", "TriageMoveWhen": "OnKeep" } }""";
            await using var app = await AppSession.StartAsync(settings);
            Wait.Until(() => app.Has("clip1.mp4"), TimeSpan.FromSeconds(20), "the clips of the test folder");

            // 1. Open triage on the first clip.
            var first = app.PointOf("clip1.mp4");
            using (app.Acting())
            {
                AppSession.Tap(first);
            }

            Assert.True(Wait.TryUntil(() => app.IsSelected("clip1.mp4"), TimeSpan.FromSeconds(5)), "clip1.mp4 is selected before triage is opened");
            AppSession.Press(VirtualKeyShort.ALT, VirtualKeyShort.KEY_T);
            Window? triage = null;
            Wait.Until(() => (triage = app.FindTopLevel("Triage")) is not null, TimeSpan.FromSeconds(20), "the triage window");
            Assert.True(Wait.TryUntil(() => Position(triage!) == 1, TimeSpan.FromSeconds(20)), "the triage window starts on the first clip");

            // 2. Keep (Enter): clip1 moves to the keep folder and carries the green tag (4).
            KeyInTriage(triage!, VirtualKeyShort.RETURN);
            Wait.Until(() => File.Exists(Path.Combine(keep, "clip1.mp4")), TimeSpan.FromSeconds(30), "clip1.mp4 to arrive in the keep folder");
            Assert.False(File.Exists(Path.Combine(inbox, "clip1.mp4")));
            Assert.Contains(4, TagsOf(Path.Combine(keep, "clip1.mp4")));
            Assert.True(Wait.TryUntil(() => Position(triage!) == 2, TimeSpan.FromSeconds(10)), "triage moved on to clip2");

            // 3. Discard (Delete): clip2 leaves the folder; 4. Undo (Ctrl+Z): it is back and triage is on it again.
            KeyInTriage(triage!, VirtualKeyShort.DELETE);
            Wait.Until(() => !File.Exists(Path.Combine(inbox, "clip2.mp4")), TimeSpan.FromSeconds(30), "clip2.mp4 to leave the inbox");
            KeyInTriage(triage!, VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_Z);
            Wait.Until(() => File.Exists(Path.Combine(inbox, "clip2.mp4")), TimeSpan.FromSeconds(30), "clip2.mp4 to come back");
            Assert.True(Wait.TryUntil(() => Position(triage!) == 2, TimeSpan.FromSeconds(10)), "after Undo triage is on clip2 again");

            // 5. Skip (Down): nothing changes on disk and triage moves to clip3.
            KeyInTriage(triage!, VirtualKeyShort.DOWN);
            Assert.True(Wait.TryUntil(() => Position(triage!) == 3, TimeSpan.FromSeconds(10)), "Skip moved on to clip3");
            Assert.True(File.Exists(Path.Combine(inbox, "clip2.mp4")), "a skipped clip stays where it is");

            // 6. Keep clip3; 7. Discard clip4: the queue is finished.
            KeyInTriage(triage!, VirtualKeyShort.RETURN);
            Wait.Until(() => File.Exists(Path.Combine(keep, "clip3.mp4")), TimeSpan.FromSeconds(30), "clip3.mp4 to arrive in the keep folder");
            Assert.Contains(4, TagsOf(Path.Combine(keep, "clip3.mp4")));
            KeyInTriage(triage!, VirtualKeyShort.DELETE);
            Wait.Until(() => !File.Exists(Path.Combine(inbox, "clip4.mp4")), TimeSpan.FromSeconds(30), "clip4.mp4 to leave the inbox");

            Wait.Until(() => triage!.FindFirstDescendant(cf => cf.ByAutomationId("SummaryText"))?.Name.Contains("kept", StringComparison.Ordinal) == true, TimeSpan.FromSeconds(10), "the summary");
            var summary = triage!.FindFirstDescendant(cf => cf.ByAutomationId("SummaryText"))!.Name;
            Assert.Contains("2 kept", summary, StringComparison.Ordinal);
            Assert.Contains("1 discarded", summary, StringComparison.Ordinal);
            Assert.Contains("1 skipped", summary, StringComparison.Ordinal);

            // 8. The discard was soft: the file is in the Recycle Bin, and Restore selected brings it back (which also leaves the bin clean).
            Assert.Contains(RecycleBin(), item => item.Name.StartsWith("clip4", StringComparison.OrdinalIgnoreCase) && item.From.Contains("leviathan-ui-triage-", StringComparison.Ordinal));
            var discarded = triage.FindFirstDescendant(cf => cf.ByAutomationId("DiscardedList"))!;
            var row = discarded.FindFirstDescendant(cf => cf.ByName("clip4.mp4")) ?? discarded.FindFirstDescendant(cf => cf.ByControlType(FlaUI.Core.Definitions.ControlType.ListItem))!;
            row.Patterns.SelectionItem.Pattern.Select();
            triage.FindFirstDescendant(cf => cf.ByAutomationId("RestoreButton"))!.AsButton().Invoke();
            Wait.Until(() => File.Exists(Path.Combine(inbox, "clip4.mp4")), TimeSpan.FromSeconds(30), "clip4.mp4 to be restored");
            Assert.DoesNotContain(RecycleBin(), item => item.Name.StartsWith("clip4", StringComparison.OrdinalIgnoreCase) && item.From.Contains("leviathan-ui-triage-", StringComparison.Ordinal));

            // 9. Close with Esc: the main window is still there and the skipped clip is untouched.
            KeyInTriage(triage, VirtualKeyShort.ESCAPE);
            Assert.True(Wait.TryUntil(() => app.FindTopLevel("Triage") is null, TimeSpan.FromSeconds(10)), "the triage window closed");
            Assert.True(app.IsRunning);
            Assert.True(File.Exists(Path.Combine(inbox, "clip2.mp4")));
            Assert.Equal(new[] { "clip1.mp4", "clip3.mp4" }, Directory.EnumerateFiles(keep).Select(f => Path.GetFileName(f)!).Order().ToArray());
        }
        finally
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch (IOException)
            {
                // Best effort.
            }
        }
    }
}
