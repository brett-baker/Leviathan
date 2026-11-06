using System.Text.Json;

namespace Leviathan.Media.Tests;

/// <summary>
/// Small generated clips in a temp folder, made with the installed ffmpeg (never committed, never real media). Tests that need them
/// skip, with the reason, on a machine without ffmpeg (spec §19.2).
/// </summary>
public sealed class MediaFixtures : IAsyncLifetime
{
    public string Folder { get; } = Path.Combine(Path.GetTempPath(), "leviathan-media-" + Guid.NewGuid().ToString("N"));

    public FfmpegTools Tools { get; private set; } = new(null, null, "not found");

    public string Clip => Path.Combine(Folder, "clip ✓ 日本語 (1).mp4");

    public string Truncated => Path.Combine(Folder, "truncated.mp4");

    public string Text => Path.Combine(Folder, "notes.txt");

    public string Empty => Path.Combine(Folder, "empty.mp4");

    public async ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(Folder);
        Tools = FfmpegLocator.Create().Locate();
        await File.WriteAllTextAsync(Text, "this is not a video");
        await File.WriteAllBytesAsync(Empty, []);
        if (Tools.FfmpegPath is not { } ffmpeg)
        {
            return;
        }

        var made = await new SystemProcessRunner().RunAsync(
            ffmpeg,
            ["-y", "-v", "error", "-f", "lavfi", "-i", "testsrc=duration=2:size=64x48:rate=10", "-f", "lavfi", "-i", "sine=frequency=440:duration=2", "-c:v", "mpeg4", "-c:a", "aac", "-shortest", Clip],
            TimeSpan.FromSeconds(60),
            1 << 20,
            CancellationToken.None);
        if (made.ExitCode != 0)
        {
            throw new InvalidOperationException("ffmpeg could not make the test clip: " + made.Error);
        }

        var bytes = await File.ReadAllBytesAsync(Clip);
        await File.WriteAllBytesAsync(Truncated, bytes.AsMemory(0, bytes.Length / 4).ToArray());
    }

    public ValueTask DisposeAsync()
    {
        try
        {
            Directory.Delete(Folder, recursive: true);
        }
        catch (IOException)
        {
            // Best effort.
        }

        return ValueTask.CompletedTask;
    }
}

public sealed class RealFfprobeTests(MediaFixtures fixtures) : IClassFixture<MediaFixtures>
{
    private FfprobeRunner Runner() => new(new SystemProcessRunner(), () => fixtures.Tools);

    private void RequireFfmpeg() => Assert.SkipUnless(fixtures.Tools.FfmpegPath is not null, "ffmpeg is not installed here (set LEVIATHAN_FFMPEG_DIR or add it to PATH).");

    [Fact]
    public async Task Probe_OfAGeneratedClip_WithAnOddName_ReturnsStreamsAndFormat()
    {
        RequireFfmpeg();

        var result = await Runner().ProbeAsync(fixtures.Clip, isNetwork: false, CancellationToken.None);

        Assert.Equal(ProbeStatus.Ok, result.Status);
        using var json = JsonDocument.Parse(result.Json!);
        Assert.Equal(2, json.RootElement.GetProperty("streams").GetArrayLength());
        Assert.Equal("mov,mp4,m4a,3gp,3g2,mj2", json.RootElement.GetProperty("format").GetProperty("format_name").GetString());
    }

    [Fact]
    public async Task Probe_ForANetworkFile_StillWorks_WithTheHeaderLimits()
    {
        RequireFfmpeg();

        var result = await Runner().ProbeAsync(fixtures.Clip, isNetwork: true, CancellationToken.None);

        Assert.Equal(ProbeStatus.Ok, result.Status);
    }

    [Theory]
    [InlineData("Truncated")]
    [InlineData("Text")]
    [InlineData("Empty")]
    public async Task Probe_OfADamagedOrNonMediaFile_IsUnreadable_AndDoesNotThrow(string which)
    {
        RequireFfmpeg();
        var path = which switch { "Truncated" => fixtures.Truncated, "Text" => fixtures.Text, _ => fixtures.Empty };

        var result = await Runner().ProbeAsync(path, isNetwork: false, CancellationToken.None);

        Assert.Equal(ProbeStatus.Failed, result.Status);
        Assert.True(result.IsUnreadable);
        Assert.False(string.IsNullOrWhiteSpace(result.Message));
    }

    [Fact]
    public async Task Probe_OfAMissingFile_Fails()
    {
        RequireFfmpeg();

        var result = await Runner().ProbeAsync(Path.Combine(fixtures.Folder, "nope.mp4"), isNetwork: false, CancellationToken.None);

        Assert.Equal(ProbeStatus.Failed, result.Status);
    }

    [Fact]
    public async Task Probe_OfANameThatLooksLikeAProtocol_IsReadAsAFile()
    {
        RequireFfmpeg();
        var tricky = Path.Combine(fixtures.Folder, "concat;a=b.mp4");
        File.Copy(fixtures.Clip, tricky, overwrite: true);

        var result = await Runner().ProbeAsync(tricky, isNetwork: false, CancellationToken.None);

        Assert.Equal(ProbeStatus.Ok, result.Status);
    }
}
