using Leviathan.Testing;

namespace Leviathan.Media.Tests;

public sealed class MediaMetadataServiceTests
{
    private const string GoodJson = """{"streams":[{"index":0,"codec_type":"video","codec_name":"h264","width":1920,"height":1080}],"format":{"format_name":"matroska,webm","duration":"60.5"}}""";

    /// <summary>A stand-in for ffprobe: counts calls, can be held open, and answers per file.</summary>
    private sealed class FakeProbe : IProcessRunner
    {
        private int _calls;
        private int _running;
        private int _peak;

        public Func<string, bool, ProcessOutcome> Answer { get; set; } = (_, _) => new ProcessOutcome(0, GoodJson, string.Empty, false, false);

        public TaskCompletionSource? Gate { get; set; }

        public int Calls => Volatile.Read(ref _calls);

        public int Peak => Volatile.Read(ref _peak);

        public List<bool> NetworkFlags { get; } = [];

        public async Task<ProcessOutcome> RunAsync(string program, IReadOnlyList<string> arguments, TimeSpan timeout, int maxOutputBytes, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _calls);
            var now = Interlocked.Increment(ref _running);
            int peak;
            while (now > (peak = Volatile.Read(ref _peak)) && Interlocked.CompareExchange(ref _peak, now, peak) != peak)
            {
            }

            try
            {
                if (Gate is { } gate)
                {
                    await gate.Task;
                }

                var path = arguments[^1]["file:".Length..];
                var network = arguments.Contains("-probesize");
                lock (NetworkFlags)
                {
                    NetworkFlags.Add(network);
                }

                return Answer(path, network);
            }
            finally
            {
                Interlocked.Decrement(ref _running);
            }
        }
    }

    private static readonly FfmpegTools Tools = new(@"C:\ff\ffprobe.exe", null, "setting");

    private static MediaFileKey Key(string path = @"D:\v\a.mkv", long size = 1000, int day = 1) => new(path, size, new DateTime(2026, 5, day, 0, 0, 0, DateTimeKind.Utc));

    // Bounded polling instead of a fixed sleep, so a slow machine cannot make a test race (a probe is started by Task.Run).
    private static async Task WaitUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "The condition was not met within 10 s.");
            await Task.Delay(5);
        }
    }

    private static (MediaMetadataService Service, FakeProbe Probe, InMemoryMediaMetadataStore Store) Create(int maxParallel = 2, FfmpegTools? tools = null)
    {
        var probe = new FakeProbe();
        var store = new InMemoryMediaMetadataStore();
        return (new MediaMetadataService(new FfprobeRunner(probe, () => tools ?? Tools), store, new ManualTimeProvider(), maxParallel), probe, store);
    }

    [Fact]
    public async Task Get_ProbesOnce_ThenAnswersFromTheCache()
    {
        var (service, probe, _) = Create();

        var first = await service.GetAsync(Key(), false, CancellationToken.None);
        var second = await service.GetAsync(Key(), false, CancellationToken.None);

        Assert.Equal(MetadataState.Ok, first.State);
        Assert.Equal(1920, first.Info!.PrimaryVideo!.Width);
        Assert.Equal(TimeSpan.FromSeconds(60.5), first.Info.Duration);
        Assert.Equal(1, probe.Calls);
        Assert.Equal(first, second);
    }

    [Fact]
    public async Task Get_AfterTheFileChanges_ProbesAgain()
    {
        var (service, probe, _) = Create();
        await service.GetAsync(Key(size: 1000), false, CancellationToken.None);

        await service.GetAsync(Key(size: 2000), false, CancellationToken.None);
        await service.GetAsync(Key(size: 2000, day: 9), false, CancellationToken.None);

        Assert.Equal(3, probe.Calls);
    }

    [Fact]
    public async Task Get_RemembersAnUnreadableFile_UntilItChanges()
    {
        var (service, probe, _) = Create();
        probe.Answer = (_, _) => new ProcessOutcome(1, string.Empty, "Invalid data found when processing input", false, false);

        var first = await service.GetAsync(Key(), false, CancellationToken.None);
        var again = await service.GetAsync(Key(), false, CancellationToken.None);

        Assert.Equal(MetadataState.Unreadable, first.State);
        Assert.Equal("Invalid data found when processing input", first.Message);
        Assert.Equal(1, probe.Calls);
        Assert.Equal(MetadataState.Unreadable, again.State);

        probe.Answer = (_, _) => new ProcessOutcome(0, GoodJson, string.Empty, false, false);
        var replaced = await service.GetAsync(Key(size: 5000), false, CancellationToken.None);
        Assert.Equal(MetadataState.Ok, replaced.State);
    }

    [Fact]
    public async Task Get_ForATimeout_IsUnreadable_AndNotRetriedEvenOnANetwork()
    {
        var (service, probe, _) = Create();
        probe.Answer = (_, _) => new ProcessOutcome(-1, string.Empty, string.Empty, true, false);

        var result = await service.GetAsync(Key(@"\\nas\v\a.mkv"), true, CancellationToken.None);

        Assert.Equal(MetadataState.Unreadable, result.State);
        Assert.Equal(1, probe.Calls);
    }

    [Fact]
    public async Task Get_ForANetworkFileThatFailsUnderTheHeaderLimits_IsTriedOnceWithoutThem()
    {
        var (service, probe, _) = Create();
        probe.Answer = (_, network) => network
            ? new ProcessOutcome(1, string.Empty, "moov atom not found", false, false)
            : new ProcessOutcome(0, GoodJson, string.Empty, false, false);

        var result = await service.GetAsync(Key(@"\\nas\v\a.mp4"), true, CancellationToken.None);

        Assert.Equal(MetadataState.Ok, result.State);
        Assert.Equal([true, false], probe.NetworkFlags);
    }

    [Fact]
    public async Task Get_ForANetworkFileThatFailsBothWays_IsUnreadable_AfterExactlyTwoTries()
    {
        var (service, probe, _) = Create();
        probe.Answer = (_, _) => new ProcessOutcome(1, string.Empty, "Invalid data", false, false);

        var result = await service.GetAsync(Key(@"\\nas\v\a.mp4"), true, CancellationToken.None);

        Assert.Equal(MetadataState.Unreadable, result.State);
        Assert.Equal(2, probe.Calls);
    }

    [Fact]
    public async Task Get_WithoutFfprobe_IsToolMissing_AndNothingIsRemembered()
    {
        var (service, _, store) = Create(tools: new FfmpegTools(null, null, "not found"));

        var result = await service.GetAsync(Key(), false, CancellationToken.None);

        Assert.Equal(MetadataState.ToolMissing, result.State);
        Assert.Null(store.Get(Key()));
        Assert.Empty(store.Unreadable());
    }

    [Fact]
    public async Task Get_ManyCallersForOneFile_ShareOneProbe()
    {
        var (service, probe, _) = Create();
        probe.Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var callers = Enumerable.Range(0, 8).Select(_ => service.GetAsync(Key(), false, CancellationToken.None)).ToList();
        await WaitUntil(() => probe.Calls == 1);
        await Task.Delay(50);
        probe.Gate.SetResult();
        var results = await Task.WhenAll(callers);

        Assert.Equal(1, probe.Calls);
        Assert.All(results, r => Assert.Equal(MetadataState.Ok, r.State));
    }

    [Fact]
    public async Task Get_RunsOnlyAsManyProbesAtOnceAsAllowed()
    {
        var (service, probe, _) = Create(maxParallel: 2);
        probe.Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var all = Enumerable.Range(0, 6).Select(i => service.GetAsync(Key($@"D:\v\{i}.mkv"), false, CancellationToken.None)).ToList();
        await WaitUntil(() => probe.Peak == 2);
        await Task.Delay(50);
        Assert.Equal(2, probe.Peak);
        probe.Gate.SetResult();
        await Task.WhenAll(all);

        Assert.Equal(6, probe.Calls);
        Assert.Equal(2, probe.Peak);
    }

    [Fact]
    public async Task Get_WhenEveryCallerLeavesBeforeItStarts_NoProbeRuns()
    {
        var (service, probe, store) = Create(maxParallel: 1);
        probe.Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var running = service.GetAsync(Key(@"D:\v\busy.mkv"), false, CancellationToken.None);
        await WaitUntil(() => probe.Calls == 1);
        using var cts = new CancellationTokenSource();
        var waiting = service.GetAsync(Key(@"D:\v\later.mkv"), false, cts.Token);
        await WaitUntil(() => service.Waiting == 1);

        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        probe.Gate.SetResult();
        await running;
        await Task.Delay(100);

        Assert.Equal(1, probe.Calls);
        Assert.Null(store.Get(Key(@"D:\v\later.mkv")));
    }

    [Fact]
    public async Task Get_WhenOneCallerLeavesButAnotherStaysWaiting_TheProbeStillRuns()
    {
        var (service, probe, _) = Create();
        probe.Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cts = new CancellationTokenSource();
        var leaving = service.GetAsync(Key(), false, cts.Token);
        var staying = service.GetAsync(Key(), false, CancellationToken.None);
        await Task.Delay(50);

        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => leaving);
        probe.Gate.SetResult();

        Assert.Equal(MetadataState.Ok, (await staying).State);
    }

    [Fact]
    public async Task RecoverFromCrash_HoldsTheInterruptedFileAgainstIt_SoNoRestartLoopStarts()
    {
        var (service, probe, store) = Create();
        store.MarkProbing(Key(), new DateTimeOffset(2026, 5, 1, 0, 0, 0, TimeSpan.Zero));

        var recovered = service.RecoverFromCrash();
        var result = await service.GetAsync(Key(), false, CancellationToken.None);

        Assert.Equal(1, recovered);
        Assert.Equal(MetadataState.Unreadable, result.State);
        Assert.Equal(MediaMetadataService.InterruptedReason, result.Message);
        Assert.Equal(0, probe.Calls);
    }

    [Fact]
    public async Task Retry_MakesTheNextRequestProbeAgain()
    {
        var (service, probe, _) = Create();
        probe.Answer = (_, _) => new ProcessOutcome(1, string.Empty, "bad", false, false);
        await service.GetAsync(Key(), false, CancellationToken.None);
        probe.Answer = (_, _) => new ProcessOutcome(0, GoodJson, string.Empty, false, false);

        Assert.True(service.Retry(Key().Path));
        var result = await service.GetAsync(Key(), false, CancellationToken.None);

        Assert.Equal(MetadataState.Ok, result.State);
        Assert.Equal(2, probe.Calls);
    }

    [Fact]
    public async Task Probed_IsRaisedForAProbe_NotForACacheHit_AndAThrowingListenerChangesNothing()
    {
        var (service, _, store) = Create();
        var raised = 0;
        service.Probed += (_, _) =>
        {
            raised++;
            throw new InvalidOperationException("a bad listener");
        };

        await service.GetAsync(Key(), false, CancellationToken.None);
        await service.GetAsync(Key(), false, CancellationToken.None);

        Assert.Equal(1, raised);
        Assert.Equal(MetadataState.Ok, store.Get(Key())!.State);
    }

    [Fact]
    public async Task Peek_NeverStartsAProbe()
    {
        var (service, probe, _) = Create();

        Assert.Null(service.Peek(Key()));
        await service.GetAsync(Key(), false, CancellationToken.None);

        Assert.NotNull(service.Peek(Key()));
        Assert.Equal(1, probe.Calls);
    }

    [Fact]
    public async Task UnreadableFiles_ListsWhatFailed_NewestFirst()
    {
        var (service, probe, _) = Create();
        probe.Answer = (_, _) => new ProcessOutcome(1, string.Empty, "bad", false, false);
        await service.GetAsync(Key(@"D:\v\one.mkv"), false, CancellationToken.None);

        Assert.Equal([@"D:\v\one.mkv"], service.UnreadableFiles().Select(e => e.Key.Path));
    }
}
