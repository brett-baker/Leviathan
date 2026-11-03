using Leviathan.Testing;

namespace Leviathan.ShellHost.Tests;

public sealed class StaPoolTests
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(10);

    /// <summary>Waits until <paramref name="condition"/> holds, for at most five seconds (no fixed sleeps, CLAUDE.md testing rules).</summary>
    private static async Task UntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "The condition was not reached in time.");
            await Task.Delay(5, TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task Work_RunsOnAnStaThread()
    {
        using var pool = new StaPool(2);

        var apartment = await pool.RunAsync(() => Thread.CurrentThread.GetApartmentState(), Limit, TestContext.Current.CancellationToken);

        Assert.Equal(ApartmentState.STA, apartment);
    }

    [Fact]
    public async Task Work_NeverRunsOnTheCallersThread()
    {
        using var pool = new StaPool(1);
        var caller = Environment.CurrentManagedThreadId;

        var worker = await pool.RunAsync(() => Environment.CurrentManagedThreadId, Limit, TestContext.Current.CancellationToken);

        Assert.NotEqual(caller, worker);
    }

    [Fact]
    public async Task AnExceptionInTheWork_ReachesTheCaller_AndTheThreadStaysUsable()
    {
        using var pool = new StaPool(1);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => pool.RunAsync<int>(() => throw new InvalidOperationException("handler broke"), Limit, TestContext.Current.CancellationToken));
        var next = await pool.RunAsync(() => 42, Limit, TestContext.Current.CancellationToken);

        Assert.Equal("handler broke", ex.Message);
        Assert.Equal(42, next);
        Assert.Equal(1, pool.Status.Failed);
        Assert.Equal(1, pool.Status.Completed);
    }

    [Fact]
    public async Task AHungRequest_TimesOutForTheCaller_AndTheNextRequestStillRuns()
    {
        var time = new ManualTimeProvider();
        using var pool = new StaPool(1, time);
        using var started = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var hung = pool.RunAsync(() =>
        {
            started.Set();
            release.Wait(Limit);
            return 1;
        }, TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
        started.Wait(Limit, TestContext.Current.CancellationToken);

        time.Advance(TimeSpan.FromSeconds(3));

        await Assert.ThrowsAsync<TimeoutException>(() => hung);
        Assert.Equal(1, pool.Status.TimedOut);
        Assert.Equal(1, pool.Status.Abandoned);

        // The only thread is stuck, yet the pool still answers: a replacement took its place.
        Assert.Equal(7, await pool.RunAsync(() => 7, Limit, TestContext.Current.CancellationToken));

        release.Set();
        await UntilAsync(() => pool.Status.Abandoned == 0);
    }

    [Fact]
    public async Task TheLimit_CountsFromWhenTheWorkStarts_NotFromWhenItWasQueued()
    {
        var time = new ManualTimeProvider();
        using var pool = new StaPool(1, time);
        using var firstStarted = new ManualResetEventSlim();
        using var releaseFirst = new ManualResetEventSlim();
        using var secondStarted = new ManualResetEventSlim();
        using var releaseSecond = new ManualResetEventSlim();
        var first = pool.RunAsync(() =>
        {
            firstStarted.Set();
            releaseFirst.Wait(Limit);
            return 1;
        }, TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
        var second = pool.RunAsync(() =>
        {
            secondStarted.Set();
            releaseSecond.Wait(Limit);
            return 2;
        }, TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
        firstStarted.Wait(Limit, TestContext.Current.CancellationToken);

        // Two seconds pass, then the first one is written off and the second starts on the replacement thread.
        time.Advance(TimeSpan.FromSeconds(3));
        await Assert.ThrowsAsync<TimeoutException>(() => first);
        secondStarted.Wait(Limit, TestContext.Current.CancellationToken);

        // The second waited three seconds in the queue, but it has only just started: two more seconds do not time it out.
        time.Advance(TimeSpan.FromSeconds(2));
        releaseSecond.Set();

        Assert.Equal(2, await second);
        releaseFirst.Set();
    }

    [Fact]
    public async Task ACancelledQueuedRequest_NeverRuns()
    {
        using var pool = new StaPool(1);
        using var started = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var blocker = pool.RunAsync(() =>
        {
            started.Set();
            release.Wait(Limit);
            return 0;
        }, Limit, TestContext.Current.CancellationToken);
        started.Wait(Limit, TestContext.Current.CancellationToken);
        using var cancel = new CancellationTokenSource();
        var ran = false;
        var queued = pool.RunAsync(() =>
        {
            ran = true;
            return 1;
        }, Limit, cancel.Token);

        await cancel.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queued);
        release.Set();
        await blocker;
        await pool.RunAsync(() => 0, Limit, TestContext.Current.CancellationToken);

        Assert.False(ran);
    }

    [Fact]
    public async Task AlreadyCancelled_ReturnsCancelledAtOnce()
    {
        using var pool = new StaPool(1);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pool.RunAsync(() => 1, Limit, new CancellationToken(canceled: true)));
    }

    [Fact]
    public async Task TooManyStuckThreads_RaiseUnhealthy()
    {
        var time = new ManualTimeProvider();
        using var pool = new StaPool(1, time, maxAbandoned: 2);
        using var release = new ManualResetEventSlim();
        var unhealthy = 0;
        pool.Unhealthy += (_, _) => Interlocked.Increment(ref unhealthy);

        for (var i = 0; i < 2; i++)
        {
            using var started = new ManualResetEventSlim();
            var hung = pool.RunAsync(() =>
            {
                started.Set();
                release.Wait(Limit);
                return 0;
            }, TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken);
            started.Wait(Limit, TestContext.Current.CancellationToken);
            time.Advance(TimeSpan.FromSeconds(1));
            await Assert.ThrowsAsync<TimeoutException>(() => hung);
        }

        Assert.Equal(1, unhealthy);
        release.Set();
    }

    [Fact]
    public async Task ManyRequestsAtOnce_AllComplete()
    {
        using var pool = new StaPool(2);

        var results = await Task.WhenAll(Enumerable.Range(0, 200).Select(i => pool.RunAsync(() => i * 2, Limit, TestContext.Current.CancellationToken)));

        Assert.Equal(Enumerable.Range(0, 200).Select(i => i * 2), results);
        Assert.Equal(200, pool.Status.Completed);
    }

    [Fact]
    public void AfterDispose_NewRequestsAreRefused()
    {
        var pool = new StaPool(1);
        pool.Dispose();

        Assert.Throws<ObjectDisposedException>(() =>
        {
            _ = pool.RunAsync(() => 1, Limit, CancellationToken.None);
        });
    }
}
