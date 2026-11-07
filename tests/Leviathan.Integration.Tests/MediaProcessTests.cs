using Leviathan.Contracts;
using Leviathan.Data;
using Leviathan.Engine.Supervision;
using Leviathan.Ipc;
using Leviathan.Media;
using Microsoft.Data.Sqlite;

namespace Leviathan.Integration.Tests;

/// <summary>
/// The real media process (<c>Leviathan.Media.dll</c>) on a pipe and a database of its own, driven through the real client and the
/// supervised connection (spec §2.2, §9.3, §3.4): it answers, it caches, a probe that was cut short by a crash is held against the file,
/// and a killed process is started again without losing what it had learned.
/// </summary>
public sealed class MediaProcessTests : IAsyncDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "leviathan-media-proc-" + Guid.NewGuid().ToString("N"));
    private readonly string _pipe = "Leviathan.Media.Tests." + Guid.NewGuid().ToString("N");
    private readonly List<IAsyncDisposable> _toDispose = [];

    public MediaProcessTests() => Directory.CreateDirectory(_folder);

    private string DatabasePath => Path.Combine(_folder, "media.db");

    private string ClipPath => Path.Combine(_folder, "clip ✓.mp4");

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
            // Best effort; a process that is still shutting down may hold the database for a moment.
        }
    }

    private MediaHostConnection Connection()
    {
        var dll = Path.Combine(AppContext.BaseDirectory, "Leviathan.Media.dll");
        Assert.True(File.Exists(dll), "Leviathan.Media.dll was not built next to the tests.");
        var launcher = new ExecutableLauncher("dotnet", $"\"{dll}\" --pipe {_pipe} --db \"{DatabasePath}\" --thumbs \"{Path.Combine(_folder, "thumbs")}\"");
        var connection = new MediaHostConnection(launcher, _pipe);
        _toDispose.Add(connection);
        return connection;
    }

    private static async Task<bool> MakeClipAsync(string path)
    {
        var tools = FfmpegLocator.Create().Locate();
        if (tools.FfmpegPath is not { } ffmpeg)
        {
            return false;
        }

        var made = await new SystemProcessRunner().RunAsync(
            ffmpeg,
            ["-y", "-v", "error", "-f", "lavfi", "-i", "testsrc=duration=1:size=64x48:rate=10", "-f", "lavfi", "-i", "sine=duration=1", "-c:v", "mpeg4", "-c:a", "aac", "-shortest", path],
            TimeSpan.FromSeconds(60),
            1 << 20,
            CancellationToken.None);
        return made.ExitCode == 0;
    }

    private static MediaProbeRequest Request(string path) =>
        new(path, new FileInfo(path).Length, File.GetLastWriteTimeUtc(path).Ticks, IsNetwork: false);

    [Fact]
    public async Task Process_DrawsAThumbnail_AsAJpeg_AndKeepsItInItsDiskCache()
    {
        Assert.SkipUnless(await MakeClipAsync(ClipPath), "ffmpeg is not installed here (set LEVIATHAN_FFMPEG_DIR or add it to PATH).");
        var service = await Connection().GetServiceAsync(TimeSpan.FromSeconds(30), CancellationToken.None);
        var request = new MediaThumbnailRequest(ClipPath, new FileInfo(ClipPath).Length, File.GetLastWriteTimeUtc(ClipPath).Ticks, false, 96);

        var first = await service.GetThumbnailAsync(request, CancellationToken.None);
        var second = await service.GetThumbnailAsync(request, CancellationToken.None);

        Assert.Equal(MediaThumbnailStatus.Ok, first.Status);
        Assert.Equal([(byte)0xFF, (byte)0xD8], first.Jpeg!.Take(2));
        Assert.Equal(first.Jpeg, second.Jpeg);
        Assert.Single(Directory.EnumerateFiles(Path.Combine(_folder, "thumbs"), "*.jpg", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task Process_AThumbnailOfABrokenFile_IsFailed_AndTheProcessKeepsAnswering()
    {
        Assert.SkipUnless(await MakeClipAsync(ClipPath), "ffmpeg is not installed here (set LEVIATHAN_FFMPEG_DIR or add it to PATH).");
        var broken = Path.Combine(_folder, "broken.mp4");
        await File.WriteAllBytesAsync(broken, [1, 2, 3, 4, 5, 6, 7, 8]);
        var service = await Connection().GetServiceAsync(TimeSpan.FromSeconds(30), CancellationToken.None);

        var answer = await service.GetThumbnailAsync(new MediaThumbnailRequest(broken, 8, File.GetLastWriteTimeUtc(broken).Ticks, false, 96), CancellationToken.None);
        var good = await service.GetThumbnailAsync(new MediaThumbnailRequest(ClipPath, new FileInfo(ClipPath).Length, File.GetLastWriteTimeUtc(ClipPath).Ticks, false, 96), CancellationToken.None);

        Assert.Equal(MediaThumbnailStatus.Failed, answer.Status);
        Assert.Null(answer.Jpeg);
        Assert.Equal(MediaThumbnailStatus.Ok, good.Status);
    }

    [Fact]
    public async Task Process_DrawsAContactSheet_WithTheFrameTimes_AndKeepsItApartFromTheThumbnails()
    {
        Assert.SkipUnless(await MakeClipAsync(ClipPath), "ffmpeg is not installed here (set LEVIATHAN_FFMPEG_DIR or add it to PATH).");
        var service = await Connection().GetServiceAsync(TimeSpan.FromSeconds(30), CancellationToken.None);
        var request = new MediaContactSheetRequest(ClipPath, new FileInfo(ClipPath).Length, File.GetLastWriteTimeUtc(ClipPath).Ticks, false, 3, 3, 96);

        var first = await service.GetContactSheetAsync(request, CancellationToken.None);
        var second = await service.GetContactSheetAsync(request, CancellationToken.None);

        Assert.True(first.Status == MediaThumbnailStatus.Ok, first.Message);
        Assert.Equal([(byte)0xFF, (byte)0xD8], first.Jpeg!.Take(2));
        Assert.Equal((3, 3, 96), (first.Columns, first.Rows, first.CellWidth));
        Assert.Equal(9, first.FrameSeconds.Length);
        Assert.Equal(first.Jpeg, second.Jpeg);
        Assert.Single(Directory.EnumerateFiles(Path.Combine(_folder, "sheets"), "*.jpg", SearchOption.AllDirectories));
        Assert.False(Directory.Exists(Path.Combine(_folder, "thumbs")) && Directory.EnumerateFiles(Path.Combine(_folder, "thumbs"), "*.jpg", SearchOption.AllDirectories).Any());
    }

    [Fact]
    public async Task Process_AContactSheetOfABrokenFile_IsFailed()
    {
        Assert.SkipUnless(await MakeClipAsync(ClipPath), "ffmpeg is not installed here (set LEVIATHAN_FFMPEG_DIR or add it to PATH).");
        var broken = Path.Combine(_folder, "broken2.mp4");
        await File.WriteAllBytesAsync(broken, [1, 2, 3, 4, 5, 6, 7, 8]);
        var service = await Connection().GetServiceAsync(TimeSpan.FromSeconds(30), CancellationToken.None);

        var answer = await service.GetContactSheetAsync(new MediaContactSheetRequest(broken, 8, File.GetLastWriteTimeUtc(broken).Ticks, false, 3, 3, 96), CancellationToken.None);

        Assert.Equal(MediaThumbnailStatus.Failed, answer.Status);
        Assert.Null(answer.Jpeg);
    }

    [Fact]
    public async Task Process_Starts_AnswersAProbe_AndAnswersTheSecondFromItsCache()
    {
        Assert.SkipUnless(await MakeClipAsync(ClipPath), "ffmpeg is not installed here (set LEVIATHAN_FFMPEG_DIR or add it to PATH).");
        var service = await Connection().GetServiceAsync(TimeSpan.FromSeconds(30), CancellationToken.None);

        var first = await service.ProbeAsync(Request(ClipPath), CancellationToken.None);
        var second = await service.ProbeAsync(Request(ClipPath), CancellationToken.None);
        var status = await service.GetStatusAsync(CancellationToken.None);

        Assert.Equal(MediaProbeStatus.Ok, first.Status);
        Assert.Equal(2, first.Info!.Video.Count + first.Info.Audio.Count);
        Assert.Equal(MediaProbeStatus.Ok, second.Status);
        Assert.Equal(first.Info!.Duration, second.Info!.Duration);
        Assert.Equal(first.Info.PrimaryVideo!.Codec, second.Info.PrimaryVideo!.Codec);
        Assert.Equal(1, status.Probed);
        Assert.Equal(0, status.Running);
    }

    [Fact]
    public async Task Ping_ReportsTheContractAndWhereFfprobeIs()
    {
        var connection = Connection();
        var service = await connection.GetServiceAsync(TimeSpan.FromSeconds(30), CancellationToken.None);

        var info = await service.PingAsync(CancellationToken.None);

        Assert.Equal(ContractVersion.Major, info.ContractMajor);
        Assert.Equal(ContractVersion.Minor, info.ContractMinor);
        Assert.Equal(0, info.QuarantinedAtStart);
        Assert.Equal(connection.SupervisedProcessId, info.ProcessId);
    }

    [Fact]
    public async Task Peek_ReturnsOnlyWhatIsCached_AndNeverStartsAProbe()
    {
        Assert.SkipUnless(await MakeClipAsync(ClipPath), "ffmpeg is not installed here (set LEVIATHAN_FFMPEG_DIR or add it to PATH).");
        var other = Path.Combine(_folder, "other.mp4");
        File.Copy(ClipPath, other);
        var service = await Connection().GetServiceAsync(TimeSpan.FromSeconds(30), CancellationToken.None);
        await service.ProbeAsync(Request(ClipPath), CancellationToken.None);

        var answers = await service.PeekAsync([Request(ClipPath), Request(other)], CancellationToken.None);
        var status = await service.GetStatusAsync(CancellationToken.None);

        Assert.Equal([ClipPath], answers.Select(a => a.Path));
        Assert.Equal(1, status.Probed);
    }

    [Fact]
    public async Task AFileThatWasBeingReadWhenTheLastProcessDied_IsQuarantined_NotReadAgain()
    {
        // Simulate the crash: the database says a probe of this file started and never finished.
        File.WriteAllText(Path.Combine(_folder, "crashy.mkv"), "not really a video");
        var request = Request(Path.Combine(_folder, "crashy.mkv"));
        var store = new SqliteMediaMetadataStore(new LeviathanDatabase(DatabasePath));
        store.MarkProbing(new MediaFileKey(request.Path, request.Size, new DateTime(request.ModifiedUtcTicks, DateTimeKind.Utc)), DateTimeOffset.UtcNow);
        SqliteConnection.ClearAllPools();

        var service = await Connection().GetServiceAsync(TimeSpan.FromSeconds(30), CancellationToken.None);
        var info = await service.PingAsync(CancellationToken.None);
        var answer = await service.ProbeAsync(request, CancellationToken.None);
        var unreadable = await service.GetUnreadableAsync(CancellationToken.None);
        var status = await service.GetStatusAsync(CancellationToken.None);

        Assert.Equal(1, info.QuarantinedAtStart);
        Assert.Equal(MediaProbeStatus.Unreadable, answer.Status);
        Assert.Equal(MediaMetadataService.InterruptedReason, answer.Message);
        Assert.Equal([request.Path], unreadable.Select(a => a.Path));
        Assert.Equal(0, status.Probed);
    }

    [Fact]
    public async Task Retry_MakesAQuarantinedFileProbe_AgainNextTime()
    {
        File.WriteAllText(Path.Combine(_folder, "plain.txt"), "text");
        var request = Request(Path.Combine(_folder, "plain.txt"));
        var service = await Connection().GetServiceAsync(TimeSpan.FromSeconds(30), CancellationToken.None);
        var first = await service.ProbeAsync(request, CancellationToken.None);

        var retried = await service.RetryAsync(request.Path, CancellationToken.None);
        var unreadable = await service.GetUnreadableAsync(CancellationToken.None);

        Assert.True(first.Status is MediaProbeStatus.Unreadable or MediaProbeStatus.ToolMissing);
        Assert.Equal(first.Status == MediaProbeStatus.Unreadable, retried);
        Assert.Empty(unreadable);
    }

    [Fact]
    public async Task AKilledProcess_IsStartedAgain_AndKeepsWhatItLearned()
    {
        Assert.SkipUnless(await MakeClipAsync(ClipPath), "ffmpeg is not installed here (set LEVIATHAN_FFMPEG_DIR or add it to PATH).");
        var connection = Connection();
        var service = await connection.GetServiceAsync(TimeSpan.FromSeconds(30), CancellationToken.None);
        await service.ProbeAsync(Request(ClipPath), CancellationToken.None);
        var firstProcess = connection.SupervisedProcessId!.Value;

        using (var process = System.Diagnostics.Process.GetProcessById(firstProcess))
        {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
        }

        // The next call finds the old connection dead, starts a new process, and the answer is still there (it is in the database).
        IMediaService again = null!;
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                again = await connection.GetServiceAsync(TimeSpan.FromSeconds(10), CancellationToken.None);
                var info = await again.PingAsync(CancellationToken.None);
                if (info.ProcessId != firstProcess)
                {
                    break;
                }
            }
            catch (Exception ex) when (ex is StreamJsonRpc.ConnectionLostException or IOException or ObjectDisposedException or MediaHostUnavailableException)
            {
                // Still the dead connection; try again.
            }

            await Task.Delay(200);
        }

        var peeked = await again.PeekAsync([Request(ClipPath)], CancellationToken.None);

        Assert.NotEqual(firstProcess, connection.SupervisedProcessId);
        Assert.Equal(1, connection.RestartCount);
        Assert.Single(peeked);
        Assert.Equal(MediaProbeStatus.Ok, peeked[0].Status);
    }
}
