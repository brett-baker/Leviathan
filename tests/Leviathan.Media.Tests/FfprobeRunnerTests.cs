using System.ComponentModel;

namespace Leviathan.Media.Tests;

public sealed class FfprobeRunnerTests
{
    private sealed class FakeRunner(Func<ProcessOutcome> outcome) : IProcessRunner
    {
        public string? Program { get; private set; }

        public IReadOnlyList<string> Arguments { get; private set; } = [];

        public TimeSpan Timeout { get; private set; }

        public int MaxOutput { get; private set; }

        public Task<ProcessOutcome> RunAsync(string program, IReadOnlyList<string> arguments, TimeSpan timeout, int maxOutputBytes, CancellationToken cancellationToken)
        {
            (Program, Arguments, Timeout, MaxOutput) = (program, arguments, timeout, maxOutputBytes);
            return Task.FromResult(outcome());
        }
    }

    private static readonly FfmpegTools Found = new(@"C:\ffmpeg\ffprobe.exe", @"C:\ffmpeg\ffmpeg.exe", "setting");

    private static ProcessOutcome Ok(string output = "{\"format\":{}}") => new(0, output, string.Empty, false, false);

    private static FfprobeRunner Runner(FakeRunner fake, FfmpegTools? tools = null, ProbeOptions? options = null) => new(fake, () => tools ?? Found, options);

    [Fact]
    public void Arguments_AskForFormatStreamsAndChaptersAsJson_AndReadTheFileAsAFile()
    {
        var arguments = Runner(new FakeRunner(() => Ok())).Arguments(@"D:\media\clip.mkv", isNetwork: false);

        Assert.Contains("-show_format", arguments);
        Assert.Contains("-show_streams", arguments);
        Assert.Contains("-show_chapters", arguments);
        Assert.Equal(["-of", "json"], arguments.SkipWhile(a => a != "-of").Take(2));
        Assert.Equal("file:D:\\media\\clip.mkv", arguments[^1]);
        Assert.Equal("-i", arguments[^2]);
        Assert.DoesNotContain("-probesize", arguments);
    }

    [Fact]
    public void Arguments_ForANetworkFile_LimitHowMuchIsRead()
    {
        var arguments = Runner(new FakeRunner(() => Ok()), options: new ProbeOptions { NetworkProbeBytes = 1_000_000, NetworkAnalyzeMicroseconds = 2_000_000 }).Arguments(@"\\nas\video\a.mp4", isNetwork: true);

        var size = arguments.ToList().IndexOf("-probesize");
        Assert.Equal("1000000", arguments[size + 1]);
        Assert.Equal("2000000", arguments[arguments.ToList().IndexOf("-analyzeduration") + 1]);
    }

    [Fact]
    public async Task Probe_WhenFfprobeAnswers_ReturnsItsJson_WithTheSpecLimits()
    {
        var fake = new FakeRunner(() => Ok("{\"streams\":[]}"));

        var result = await Runner(fake).ProbeAsync(@"D:\a.mkv", false, CancellationToken.None);

        Assert.Equal(ProbeStatus.Ok, result.Status);
        Assert.Equal("{\"streams\":[]}", result.Json);
        Assert.Equal(@"C:\ffmpeg\ffprobe.exe", fake.Program);
        Assert.Equal(TimeSpan.FromSeconds(10), fake.Timeout);
        Assert.False(result.IsUnreadable);
    }

    [Fact]
    public async Task Probe_WhenItTimesOut_IsUnreadable()
    {
        var result = await Runner(new FakeRunner(() => new ProcessOutcome(-1, string.Empty, string.Empty, true, false))).ProbeAsync(@"D:\a.mkv", false, CancellationToken.None);

        Assert.Equal(ProbeStatus.TimedOut, result.Status);
        Assert.True(result.IsUnreadable);
    }

    [Fact]
    public async Task Probe_WhenFfprobeFails_IsUnreadable_WithTheFirstLineOfItsComplaint()
    {
        var failed = new ProcessOutcome(1, string.Empty, "D:\\a.mkv: Invalid data found when processing input\nmore\n", false, false);

        var result = await Runner(new FakeRunner(() => failed)).ProbeAsync(@"D:\a.mkv", false, CancellationToken.None);

        Assert.Equal(ProbeStatus.Failed, result.Status);
        Assert.Equal(@"D:\a.mkv: Invalid data found when processing input", result.Message);
        Assert.True(result.IsUnreadable);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[1,2]")]
    public async Task Probe_WhenTheOutputIsNotAJsonObject_Fails(string output)
    {
        var result = await Runner(new FakeRunner(() => Ok(output))).ProbeAsync(@"D:\a.mkv", false, CancellationToken.None);

        Assert.Equal(ProbeStatus.Failed, result.Status);
    }

    [Fact]
    public async Task Probe_WhenTheOutputIsOverTheLimit_IsTooLarge()
    {
        var result = await Runner(new FakeRunner(() => new ProcessOutcome(0, "{", string.Empty, false, true))).ProbeAsync(@"D:\a.mkv", false, CancellationToken.None);

        Assert.Equal(ProbeStatus.TooLarge, result.Status);
        Assert.True(result.IsUnreadable);
    }

    [Fact]
    public async Task Probe_WithoutFfprobe_IsNotTheFilesFault()
    {
        var fake = new FakeRunner(() => Ok());

        var result = await Runner(fake, new FfmpegTools(null, null, "not found")).ProbeAsync(@"D:\a.mkv", false, CancellationToken.None);

        Assert.Equal(ProbeStatus.ToolMissing, result.Status);
        Assert.False(result.IsUnreadable);
        Assert.Null(fake.Program);
    }

    [Fact]
    public async Task Probe_WhenTheProgramCannotBeStarted_IsToolMissing_NotUnreadable()
    {
        var runner = new FfprobeRunner(new ThrowingRunner(), () => Found);

        var result = await runner.ProbeAsync(@"D:\a.mkv", false, CancellationToken.None);

        Assert.Equal(ProbeStatus.ToolMissing, result.Status);
        Assert.False(result.IsUnreadable);
    }

    private sealed class ThrowingRunner : IProcessRunner
    {
        public Task<ProcessOutcome> RunAsync(string program, IReadOnlyList<string> arguments, TimeSpan timeout, int maxOutputBytes, CancellationToken cancellationToken) =>
            throw new Win32Exception("The system cannot find the file specified.");
    }
}
