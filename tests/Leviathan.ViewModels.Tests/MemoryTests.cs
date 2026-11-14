using System.Runtime.CompilerServices;
using Leviathan.Core.Paths;
using Leviathan.Core.Platform;
using Leviathan.ViewModels.Folders;
using Leviathan.ViewModels.Tabs;
using Leviathan.ViewModels.Windows;

namespace Leviathan.ViewModels.Tests;

/// <summary>Memory that must stay bounded in a long session (spec §3.1: idle memory with 10 tabs, size-capped caches; build plan G2).</summary>
public sealed class MemoryTests
{
    private static LocationPath P(string path) => LocationPath.Parse(path);

    private static void ForceCollection()
    {
        for (var i = 0; i < 3; i++)
        {
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<WeakReference> OpenAndCloseAsync(TabStripViewModel strip)
    {
        var tab = strip.NewTab(P(@"C:\a\b"));
        await tab.ReloadAsync(CancellationToken.None);
        tab.Items.SortBy("size");
        tab.Selection.SelectAll();
        var weak = new WeakReference(tab);
        strip.Close(tab);
        return weak;
    }

    [Fact]
    public async Task OpeningAndClosingAThousandTabs_LeavesNothingBehind()
    {
        var window = new WindowManager(TabTestSupport.Services()).OpenWindow(P(@"C:\a"));
        var strip = window.Tabs;
        for (var i = 0; i < 50; i++)
        {
            await OpenAndCloseAsync(strip); // warm-up: JIT, caches and the closed-tab list reach their steady size
        }


        var closed = new List<WeakReference>();
        for (var i = 0; i < 1000; i++)
        {
            closed.Add(await OpenAndCloseAsync(strip));
        }

        // The last tab can still be held by a batch timer that has not fired yet (80 ms); that passes, a leak does not.
        await Task.Delay(500);
        ForceCollection();

        // Reachability is the exact test. Total memory is not used: tests run in parallel and allocate at the same time.
        var alive = closed.Count(w => w.IsAlive);
        Assert.True(alive == 0, $"{alive} of 1,000 closed tabs are still reachable (index {string.Join(',', closed.Select((w, i) => (w, i)).Where(x => x.w.IsAlive).Select(x => x.i))}): something keeps them (an event, the recent list, the closed-tab list)");
        Assert.Single(strip.Tabs);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference OpenAndCloseWindow(WindowManager manager)
    {
        var window = manager.OpenWindow(P(@"C:\a"));
        var weak = new WeakReference(window);
        manager.Close(window);
        return weak;
    }

    [Fact]
    public void OpeningAndClosingWindows_LeavesNothingBehind()
    {
        var manager = new WindowManager(TabTestSupport.Services());
        manager.OpenWindow(P(@"C:\a")); // one stays open so closing the others never ends the app
        for (var i = 0; i < 20; i++)
        {
            OpenAndCloseWindow(manager);
        }

        var closed = new List<WeakReference>();
        for (var i = 0; i < 200; i++)
        {
            closed.Add(OpenAndCloseWindow(manager));
        }

        ForceCollection();

        var alive = closed.Count(w => w.IsAlive);
        Assert.True(alive == 0, $"{alive} of 200 closed windows are still reachable");
    }

    private sealed class NoThumbnails : IThumbnailSource
    {
        public Task<ThumbnailImage?> CreateAsync(ThumbnailRequest request, CancellationToken cancellationToken) => Task.FromResult<ThumbnailImage?>(null);
    }

    private sealed class FailingThumbnails : IThumbnailSource
    {
        public Task<ThumbnailImage?> CreateAsync(ThumbnailRequest request, CancellationToken cancellationToken) => throw new InvalidOperationException("no thumbnailer");
    }

    private static ThumbnailRequest Request(int i) =>
        new(P($@"C:\library\file {i}.xyz"), new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero), 100, 96);

    [Fact]
    public async Task FilesWithoutAThumbnail_AreRemembered_ButOnlyUpToTheEntryLimit()
    {
        using var cache = new ThumbnailCache(new NoThumbnails(), capacityBytes: 64L * 1024 * 1024, maxConcurrent: 4, maxEntries: 100);

        for (var i = 0; i < 1000; i++)
        {
            await cache.GetAsync(Request(i), CancellationToken.None);
        }

        Assert.True(cache.Count <= 100, $"{cache.Count} remembered results for a limit of 100");
        Assert.True(cache.TryGetCached(Request(999), out _), "the newest result is kept");
        Assert.False(cache.TryGetCached(Request(0), out _), "the oldest result went");
    }

    [Fact]
    public async Task FilesWhoseThumbnailFailed_AreRemembered_ButOnlyUpToTheEntryLimit()
    {
        using var cache = new ThumbnailCache(new FailingThumbnails(), capacityBytes: 64L * 1024 * 1024, maxConcurrent: 4, maxEntries: 100);

        for (var i = 0; i < 1000; i++)
        {
            await cache.GetAsync(Request(i), CancellationToken.None);
        }

        Assert.True(cache.Stats.Failed <= 100, $"{cache.Stats.Failed} remembered failures for a limit of 100");
    }
}
