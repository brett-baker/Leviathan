using System.IO.Abstractions.TestingHelpers;
using System.Runtime.CompilerServices;
using Leviathan.Diagnostics.Crash;
using Leviathan.Testing;
using Microsoft.Extensions.Logging;

namespace Leviathan.Diagnostics.Tests;

public class CrashReportTests
{
    private static readonly BuildInfo Build = new("0.1.0+abc1234def", "abc1234def", "https://github.com/brett-baker/Leviathan");

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowNetworkError() =>
        throw new IOException("The specified network name is no longer available.", unchecked((int)0x80070040));

    private static Exception Capture(Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            return ex;
        }

        throw new InvalidOperationException("Expected an exception.");
    }

    [Fact]
    public void Build_DescribesExceptionChain_WithWin32Names()
    {
        var inner = Capture(ThrowNetworkError);
        var outer = new InvalidOperationException("Copy failed", inner);
        var builder = new CrashReportBuilder("Leviathan.Worker", Build, time: new ManualTimeProvider());

        var report = builder.Build(IncidentKind.Crash, outer);

        Assert.Equal(2, report.Exceptions.Count);
        Assert.Equal("System.InvalidOperationException", report.Exceptions[0].Type);
        Assert.Equal("0x80070040", report.Exceptions[1].HResult);
        Assert.Equal("ERROR_NETNAME_DELETED", report.Exceptions[1].Win32Error);
        Assert.StartsWith("worker crashed: IOException (ERROR_NETNAME_DELETED, 0x80070040)", report.Summary);
        Assert.Contains(nameof(ThrowNetworkError), report.Summary);
    }

    [Fact]
    public void Signature_IsStable_ForTheSameBug_AndDiffersForAnother()
    {
        var a = CrashReportBuilder.ComputeSignature(Capture(ThrowNetworkError));
        var b = CrashReportBuilder.ComputeSignature(Capture(ThrowNetworkError));
        var c = CrashReportBuilder.ComputeSignature(Capture(() => throw new InvalidOperationException("x")));

        Assert.Equal(a, b);
        Assert.NotEqual(a, c);
        Assert.Equal(12, a.Length);
    }

    [Theory]
    [InlineData(@"D:\a\Leviathan\Leviathan\src\Leviathan.Worker\Copy\ChunkedCopier.cs", "src/Leviathan.Worker/Copy/ChunkedCopier.cs")]
    [InlineData("/home/runner/work/Leviathan/tools/Leviathan.Diag/Program.cs", "tools/Leviathan.Diag/Program.cs")]
    [InlineData(@"C:\elsewhere\file.cs", "C:/elsewhere/file.cs")]
    public void ToRepoRelative_FindsRepositoryRoot(string path, string expected)
    {
        Assert.Equal(expected, CrashReportBuilder.ToRepoRelative(path));
    }

    [Fact]
    public void Frames_GetPermalinks_WhenFileAndCommitKnown()
    {
        var builder = new CrashReportBuilder("Leviathan.Worker", Build);
        var report = builder.Build(IncidentKind.Crash, Capture(ThrowNetworkError));

        var frame = report.Frames.FirstOrDefault(f => f.File is not null);
        if (frame is null)
        {
            Assert.Skip("Line information is not available in this build (no PDBs).");
            return;
        }

        Assert.StartsWith("https://github.com/brett-baker/Leviathan/blob/abc1234def/", frame.Permalink);
        Assert.Contains("#L", frame.Permalink);
        Assert.Contains(frame.File!, report.StartingPoints);
    }

    [Fact]
    public void Build_IncludesFlightRecorderAndActions()
    {
        var recorder = new FlightRecorder("Leviathan.App", capacity: 3);
        var actions = new ActionTrail(new ManualTimeProvider());
        for (var i = 0; i < 5; i++)
        {
            recorder.Record(LogLevel.Information, "test", $"event {i}");
        }

        actions.Record("Navigate", @"\\nas\media");
        actions.Record("Copy", @"\\nas\media\a.mkv");
        var builder = new CrashReportBuilder("Leviathan.App", Build, recorder, actions);

        var report = builder.Build(IncidentKind.Report, null, new Dictionary<string, string> { ["tab"] = @"\\nas\media" }, "Preview froze");

        Assert.Equal(new[] { "event 2", "event 3", "event 4" }, report.RecentEvents.Select(e => e.Message));
        Assert.Equal(2, report.Actions.Count);
        Assert.Equal("Problem reported in app", report.Summary);
        Assert.Equal(@"\\nas\media", report.Context["tab"]);
        Assert.Equal("Preview froze", report.UserNote);
    }

    [Fact]
    public void Markdown_HasAllSectionsInOrder()
    {
        var builder = new CrashReportBuilder("Leviathan.Worker", Build);
        var report = builder.Build(IncidentKind.Crash, Capture(ThrowNetworkError));

        var markdown = CrashReportMarkdown.Render(report, occurrences: 3, firstSeen: report.Timestamp.AddDays(-2));

        var positions = CrashReportMarkdown.Sections.Select(s => markdown.IndexOf("## " + s, StringComparison.Ordinal)).ToList();
        Assert.All(positions, p => Assert.True(p >= 0));
        Assert.True(positions.SequenceEqual(positions.Order()), "Sections are out of order.");
        Assert.Contains("seen 3 times", markdown);
        Assert.Contains("ERROR_NETNAME_DELETED", markdown);
    }

    [Fact]
    public void BundleStore_WritesAllFiles_AndReadsThemBack()
    {
        var fs = new MockFileSystem();
        var root = fs.Path.Combine(fs.Path.GetTempPath(), "leviathan-tests", Guid.NewGuid().ToString("N"));
        var store = new CrashBundleStore(fs, root);
        var builder = new CrashReportBuilder("Leviathan.Worker", Build);

        var folder = store.Write(builder.Build(IncidentKind.Crash, Capture(ThrowNetworkError)));
        var second = store.Write(builder.Build(IncidentKind.Crash, Capture(ThrowNetworkError)));

        foreach (var file in new[] { CrashBundleStore.Files.ReportMarkdown, CrashBundleStore.Files.ReportJson, CrashBundleStore.Files.Events, CrashBundleStore.Files.Actions, CrashBundleStore.Files.Environment })
        {
            Assert.True(fs.File.Exists(fs.Path.Combine(folder, file)), $"{file} missing");
        }

        var group = Assert.Single(store.Groups());
        Assert.Equal(2, group.Count);
        Assert.Contains("seen 2 times", store.ReadMarkdown(store.Find(fs.Path.GetFileName(second)!)!));
        Assert.NotNull(store.Latest());
    }

    [Fact]
    public void BundleStore_EmptyOrMissingRoot_ReturnsNothing()
    {
        var fs = new MockFileSystem();
        var store = new CrashBundleStore(fs, fs.Path.Combine(fs.Path.GetTempPath(), "does-not-exist-" + Guid.NewGuid().ToString("N")));

        Assert.Empty(store.ReadAll());
        Assert.Null(store.Latest());
    }
}

public class FlightRecorderTests
{
    [Fact]
    public void KeepsMostRecentEvents_InOrder()
    {
        var recorder = new FlightRecorder("p", capacity: 4);
        for (var i = 0; i < 10; i++)
        {
            recorder.Record(LogLevel.Debug, "c", i.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        Assert.Equal(new[] { "6", "7", "8", "9" }, recorder.Snapshot().Select(e => e.Message));
    }

    [Fact]
    public void LoggerProvider_RecordsMessagesAndExceptions()
    {
        var recorder = new FlightRecorder("p");
        using var provider = new FlightRecorderLoggerProvider(recorder);
        var logger = provider.CreateLogger("Cat");

        logger.LogWarning(new InvalidOperationException("bad"), "Something {What}", "broke");
        logger.LogTrace("too detailed");

        var evt = Assert.Single(recorder.Snapshot());
        Assert.Equal("Something broke", evt.Message);
        Assert.Equal("Cat", evt.Category);
        Assert.Contains("bad", evt.Exception);
    }

    [Fact]
    public void ActionTrail_KeepsLastFifty()
    {
        var trail = new ActionTrail(new ManualTimeProvider());
        for (var i = 0; i < 60; i++)
        {
            trail.Record("Navigate", $"C:\\{i}");
        }

        var actions = trail.Snapshot();
        Assert.Equal(ActionTrail.Capacity, actions.Count);
        Assert.Equal("C:\\10", actions[0].Target);
    }
}

public class UiStallWatchdogTests
{
    /// <summary>A UI thread the test controls: queued callbacks run only when the test pumps.</summary>
    private sealed class ManualDispatcher : IUiDispatcher
    {
        private readonly Queue<Action> _queue = new();

        public bool TryEnqueue(Action callback)
        {
            lock (_queue)
            {
                _queue.Enqueue(callback);
            }

            return true;
        }

        public void Pump()
        {
            while (true)
            {
                Action next;
                lock (_queue)
                {
                    if (_queue.Count == 0)
                    {
                        return;
                    }

                    next = _queue.Dequeue();
                }

                next();
            }
        }
    }

    [Fact]
    public async Task ResponsiveUi_ReportsNoStall()
    {
        var time = new ManualTimeProvider();
        var dispatcher = new ManualDispatcher();
        await using var watchdog = new UiStallWatchdog(dispatcher, time);
        var stalls = new List<UiStall>();
        watchdog.StallDetected += (_, s) => stalls.Add(s);

        var beat = watchdog.BeatAsync(CancellationToken.None);
        time.Advance(TimeSpan.FromMilliseconds(10));
        dispatcher.Pump();
        var duration = await beat;

        Assert.Equal(TimeSpan.FromMilliseconds(10), duration);
        Assert.Empty(stalls);
    }

    [Fact]
    public async Task SlowUi_ReportsStall()
    {
        var time = new ManualTimeProvider();
        var dispatcher = new ManualDispatcher();
        await using var watchdog = new UiStallWatchdog(dispatcher, time);
        var stalls = new List<UiStall>();
        watchdog.StallDetected += (_, s) => stalls.Add(s);

        var beat = watchdog.BeatAsync(CancellationToken.None);
        time.Advance(TimeSpan.FromMilliseconds(120));
        dispatcher.Pump();
        await beat;

        var stall = Assert.Single(stalls);
        Assert.False(stall.IsHang);
        Assert.Equal(TimeSpan.FromMilliseconds(120), stall.Duration);
    }

    [Fact]
    public async Task HungUi_ReportsHang_WhileStillStuck()
    {
        var time = new ManualTimeProvider();
        var dispatcher = new ManualDispatcher();
        await using var watchdog = new UiStallWatchdog(dispatcher, time);
        var hang = new TaskCompletionSource<UiStall>();
        watchdog.StallDetected += (_, s) => hang.TrySetResult(s);

        var beat = watchdog.BeatAsync(CancellationToken.None);
        await Task.Delay(20);
        time.Advance(TimeSpan.FromSeconds(6));

        var reported = await hang.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(reported.IsHang);
        Assert.False(beat.IsCompleted);

        dispatcher.Pump();
        await beat.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, watchdog.StallCount);
    }
}

public class RepositoryPathTests
{
    [Theory]
    [InlineData("src/Leviathan.Worker/X.cs", true)]
    [InlineData("tests/Leviathan.Core.Tests/Y.cs", true)]
    [InlineData("/home/user/elsewhere/Program.cs", false)]
    [InlineData("C:/elsewhere/file.cs", false)]
    public void IsInRepository_OnlyForRepoRoots(string path, bool expected)
    {
        Assert.Equal(expected, CrashReportBuilder.IsInRepository(path));
    }
}
