using Leviathan.Contracts;
using Leviathan.Engine.Supervision;
using Leviathan.Media;
using Leviathan.Testing;
using Microsoft.Data.Sqlite;

namespace Leviathan.Integration.Tests;

/// <summary>
/// The corrupt-file suite (Phase 3 gate: "a corrupt-file test set causes no crash"; spec §3.4, §9.3, §19). Real clips are made with
/// ffmpeg, damaged eleven ways each (<see cref="CorruptMedia"/>), and every damaged file is sent through the real media process for a
/// probe, a thumbnail, a contact sheet and a scrub strip. Each answer must be a clean "unreadable" or "failed" (or a good result when the
/// damage happens to leave something playable), nothing may hang, the process must be the same one at the end, and the intact clips must
/// still work afterwards.
/// </summary>
public sealed class CorruptMediaSuiteTests : IAsyncDisposable
{
    private static readonly TimeSpan PerCall = TimeSpan.FromSeconds(90);

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "leviathan-corrupt-suite-" + Guid.NewGuid().ToString("N"));
    private readonly string _pipe = "Leviathan.Corrupt.Tests." + Guid.NewGuid().ToString("N");
    private readonly List<IAsyncDisposable> _toDispose = [];

    public CorruptMediaSuiteTests() => Directory.CreateDirectory(_folder);

    public async ValueTask DisposeAsync()
    {
        foreach (var item in _toDispose)
        {
            await item.DisposeAsync();
        }

        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_folder, recursive: true);
        }
        catch (IOException)
        {
            // Best effort; the media process may still be closing its database.
        }
    }

    private static async Task<bool> MakeAsync(string path, params string[] arguments)
    {
        var tools = FfmpegLocator.Create().Locate();
        if (tools.FfmpegPath is not { } ffmpeg)
        {
            return false;
        }

        var made = await new SystemProcessRunner().RunAsync(ffmpeg, ["-y", "-v", "error", .. arguments, path], TimeSpan.FromSeconds(60), 1 << 20, CancellationToken.None);
        return made.ExitCode == 0;
    }

    private static MediaProbeRequest Probe(string path) => new(path, new FileInfo(path).Length, File.GetLastWriteTimeUtc(path).Ticks, IsNetwork: false);

    private async Task<IMediaService> StartAsync(string name)
    {
        var dll = Path.Combine(AppContext.BaseDirectory, "Leviathan.Media.dll");
        Assert.True(File.Exists(dll), "Leviathan.Media.dll was not built next to the tests.");
        var launcher = new ExecutableLauncher("dotnet", $"\"{dll}\" --pipe {_pipe} --db \"{Path.Combine(_folder, name + ".db")}\" --thumbs \"{Path.Combine(_folder, name + "-thumbs")}\"");
        var connection = new MediaHostConnection(launcher, _pipe);
        _toDispose.Add(connection);
        Connection = connection;
        return await connection.GetServiceAsync(TimeSpan.FromSeconds(30), CancellationToken.None);
    }

    private MediaHostConnection? Connection { get; set; }

    [Fact]
    public async Task EveryDamagedFile_GivesACleanAnswer_AndTheMediaProcessNeverDies()
    {
        var video = ("video", ".mp4", new[] { "-f", "lavfi", "-i", "testsrc=duration=2:size=128x72:rate=10", "-f", "lavfi", "-i", "sine=duration=2", "-c:v", "mpeg4", "-c:a", "aac", "-shortest" });
        var matroska = ("matroska", ".mkv", new[] { "-f", "lavfi", "-i", "testsrc=duration=2:size=128x72:rate=10", "-f", "lavfi", "-i", "sine=duration=2", "-c:v", "mpeg4", "-c:a", "aac", "-shortest" });
        var sound = ("sound", ".flac", new[] { "-f", "lavfi", "-i", "sine=frequency=440:duration=2", "-c:a", "flac" });
        var made = new List<(string Stem, string Extension, string Original, bool IsVideo)>();
        foreach (var (stem, extension, arguments) in new[] { video, matroska, sound })
        {
            var original = Path.Combine(_folder, "intact", stem + extension);
            Directory.CreateDirectory(Path.GetDirectoryName(original)!);
            Assert.SkipUnless(await MakeAsync(original, arguments), "ffmpeg is not installed here (set LEVIATHAN_FFMPEG_DIR or add it to PATH).");
            made.Add((stem, extension, original, extension != ".flac"));
        }

        var service = await StartAsync("suite");
        var processBefore = Connection!.SupervisedProcessId;
        var damaged = new List<(string Path, CorruptKind Kind, bool IsVideo)>();
        foreach (var (stem, extension, original, isVideo) in made)
        {
            var bytes = await File.ReadAllBytesAsync(original);
            foreach (var kind in CorruptMedia.All)
            {
                var path = Path.Combine(_folder, "damaged", $"{stem}.{kind}{extension}");
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                await File.WriteAllBytesAsync(path, CorruptMedia.Apply(bytes, kind));
                damaged.Add((path, kind, isVideo));
            }
        }

        var started = DateTime.UtcNow;
        var unreadable = new List<string>();
        foreach (var (path, kind, isVideo) in damaged)
        {
            var size = new FileInfo(path).Length;
            var ticks = File.GetLastWriteTimeUtc(path).Ticks;

            var probe = await service.ProbeAsync(Probe(path), CancellationToken.None).WaitAsync(PerCall);
            Assert.True(probe.Status is MediaProbeStatus.Ok or MediaProbeStatus.Unreadable, $"{Path.GetFileName(path)}: probe said {probe.Status}");
            if (probe.Status == MediaProbeStatus.Unreadable)
            {
                unreadable.Add(path);
                Assert.False(string.IsNullOrWhiteSpace(probe.Message), $"{Path.GetFileName(path)}: no reason given");
            }

            if (kind is CorruptKind.Empty or CorruptKind.OneByte or CorruptKind.ZeroFilled or CorruptKind.TextInDisguise or CorruptKind.RandomGarbage)
            {
                Assert.True(probe.Status == MediaProbeStatus.Unreadable, $"{Path.GetFileName(path)} ({size} bytes) was reported as {probe.Status}");
            }

            if (!isVideo)
            {
                continue;
            }

            var thumbnail = await service.GetThumbnailAsync(new MediaThumbnailRequest(path, size, ticks, false, 96), CancellationToken.None).WaitAsync(PerCall);
            var sheet = await service.GetContactSheetAsync(new MediaContactSheetRequest(path, size, ticks, false, 3, 3, 96), CancellationToken.None).WaitAsync(PerCall);
            var strip = await service.GetScrubStripAsync(new MediaScrubRequest(path, size, ticks, false, 20, 64), CancellationToken.None).WaitAsync(PerCall);
            foreach (var (what, status, jpeg) in new[] { ("thumbnail", thumbnail.Status, thumbnail.Jpeg), ("contact sheet", sheet.Status, sheet.Jpeg), ("scrub strip", strip.Status, strip.Jpeg) })
            {
                Assert.True(status is MediaThumbnailStatus.Ok or MediaThumbnailStatus.Failed, $"{Path.GetFileName(path)}: {what} said {status}");
                if (status == MediaThumbnailStatus.Ok)
                {
                    Assert.Equal(new byte[] { 0xFF, 0xD8 }, jpeg!.Take(2).ToArray());
                }
                else
                {
                    Assert.Null(jpeg);
                }
            }
        }

        // The process lived through all of it, and still does its job on the intact clips.
        var info = await service.PingAsync(CancellationToken.None);
        Assert.Equal(processBefore, Connection.SupervisedProcessId);
        Assert.Equal(processBefore, info.ProcessId);
        foreach (var (_, _, original, isVideo) in made)
        {
            Assert.Equal(MediaProbeStatus.Ok, (await service.ProbeAsync(Probe(original), CancellationToken.None).WaitAsync(PerCall)).Status);
            if (isVideo)
            {
                var good = await service.GetThumbnailAsync(new MediaThumbnailRequest(original, new FileInfo(original).Length, File.GetLastWriteTimeUtc(original).Ticks, false, 96), CancellationToken.None).WaitAsync(PerCall);
                Assert.Equal(MediaThumbnailStatus.Ok, good.Status);
            }
        }

        // What it could not read is on the list the Background tab shows, and nothing else is.
        var listed = (await service.GetUnreadableAsync(CancellationToken.None)).Select(a => a.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert.All(unreadable, path => Assert.Contains(path, listed));
        Assert.All(listed, path => Assert.Contains(path, damaged.Select(d => d.Path), StringComparer.OrdinalIgnoreCase));
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromMinutes(5), "the damaged set took longer than five minutes");
    }
}
