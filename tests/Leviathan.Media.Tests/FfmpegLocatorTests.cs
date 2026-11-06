namespace Leviathan.Media.Tests;

public sealed class FfmpegLocatorTests
{
    private static readonly string Suffix = OperatingSystem.IsWindows() ? ".exe" : string.Empty;

    private static FfmpegLocator Locator(IEnumerable<string> existing, Dictionary<string, string>? environment = null, string? setting = null)
    {
        var files = new HashSet<string>(existing, StringComparer.OrdinalIgnoreCase);
        return new FfmpegLocator(files.Contains, name => environment is not null && environment.TryGetValue(name, out var v) ? v : null, Path.Combine("app", "bin"), setting);
    }

    private static string Probe(string folder) => Path.Combine(folder, "ffprobe" + Suffix);

    private static string Ffmpeg(string folder) => Path.Combine(folder, "ffmpeg" + Suffix);

    [Fact]
    public void Locate_WhenNothingIsThere_SaysNotFound()
    {
        var tools = Locator([]).Locate();

        Assert.False(tools.HasFfprobe);
        Assert.Equal("not found", tools.Source);
    }

    [Fact]
    public void Locate_PrefersTheSetting_OverTheEnvironment_TheAppFolderAndPath()
    {
        var setting = Path.Combine("mine");
        var env = Path.Combine("env");
        var app = Path.Combine("app", "bin", "ffmpeg");
        var path = Path.Combine("onpath");
        var locator = Locator(
            [Probe(setting), Ffmpeg(setting), Probe(env), Probe(app), Probe(path)],
            new() { ["LEVIATHAN_FFMPEG_DIR"] = env, ["PATH"] = path },
            setting);

        var tools = locator.Locate();

        Assert.Equal(Probe(setting), tools.FfprobePath);
        Assert.Equal(Ffmpeg(setting), tools.FfmpegPath);
        Assert.Equal("setting", tools.Source);
    }

    [Fact]
    public void Locate_WithoutASetting_UsesTheEnvironmentVariable_ThenTheAppFolder_ThenPath()
    {
        var env = Path.Combine("env");
        var app = Path.Combine("app", "bin", "ffmpeg");
        var path = Path.Combine("onpath");

        Assert.Equal("LEVIATHAN_FFMPEG_DIR", Locator([Probe(env), Probe(app), Probe(path)], new() { ["LEVIATHAN_FFMPEG_DIR"] = env, ["PATH"] = path }).Locate().Source);
        Assert.Equal("app folder", Locator([Probe(app), Probe(path)], new() { ["PATH"] = path }).Locate().Source);
        Assert.Equal("PATH", Locator([Probe(path)], new() { ["PATH"] = path }).Locate().Source);
    }

    [Fact]
    public void Locate_SkipsAFolderThatHasNoFfprobe_AndTakesTheNextOne()
    {
        var setting = Path.Combine("empty");
        var path = Path.Combine("onpath");

        var tools = Locator([Probe(path)], new() { ["PATH"] = path }, setting).Locate();

        Assert.Equal(Probe(path), tools.FfprobePath);
    }

    [Fact]
    public void Locate_FindsFfprobeEvenWithoutFfmpeg()
    {
        var path = Path.Combine("onpath");

        var tools = Locator([Probe(path)], new() { ["PATH"] = path }).Locate();

        Assert.True(tools.HasFfprobe);
        Assert.Null(tools.FfmpegPath);
    }

    [Fact]
    public void Locate_ReadsQuotedPathEntries()
    {
        var folder = Path.Combine("with space");

        var tools = Locator([Probe(folder)], new() { ["PATH"] = "\"" + folder + "\"" }).Locate();

        Assert.Equal(Probe(folder), tools.FfprobePath);
    }
}
