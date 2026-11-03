using Leviathan.Core.Paths;
using Leviathan.Testing;

namespace Leviathan.Providers.Local.Tests;

public sealed class ChangeDebouncerTests
{
    private static readonly TimeSpan Delay = TimeSpan.FromMilliseconds(300);
    private static readonly TimeSpan Gap = TimeSpan.FromSeconds(1);

    [Fact]
    public void ABurst_IsOneCall_AfterTheDelay()
    {
        var time = new ManualTimeProvider();
        var calls = 0;
        using var debouncer = new ChangeDebouncer(time, Delay, Gap, () => calls++);

        debouncer.Signal();
        debouncer.Signal();
        time.Advance(TimeSpan.FromMilliseconds(299));
        Assert.Equal(0, calls);
        debouncer.Signal();
        time.Advance(TimeSpan.FromMilliseconds(2));

        Assert.Equal(1, calls);
    }

    [Fact]
    public void ASteadyStreamOfEvents_CallsNoMoreOftenThanTheGap()
    {
        var time = new ManualTimeProvider();
        var calls = 0;
        using var debouncer = new ChangeDebouncer(time, Delay, Gap, () => calls++);

        debouncer.Signal();
        time.Advance(Delay);
        Assert.Equal(1, calls);

        // Events keep coming: the next call waits until a second has passed since the last one.
        debouncer.Signal();
        time.Advance(Delay);
        Assert.Equal(1, calls);
        time.Advance(Gap - Delay);
        Assert.Equal(2, calls);
    }

    [Fact]
    public void AnEventAfterALongQuietSpell_IsCalledAfterJustTheDelay()
    {
        var time = new ManualTimeProvider();
        var calls = 0;
        using var debouncer = new ChangeDebouncer(time, Delay, Gap, () => calls++);
        debouncer.Signal();
        time.Advance(Delay);

        time.Advance(TimeSpan.FromMinutes(1));
        debouncer.Signal();
        time.Advance(Delay);

        Assert.Equal(2, calls);
    }

    [Fact]
    public void Dispose_CancelsAPendingCall()
    {
        var time = new ManualTimeProvider();
        var calls = 0;
        var debouncer = new ChangeDebouncer(time, Delay, Gap, () => calls++);
        debouncer.Signal();

        debouncer.Dispose();
        time.Advance(TimeSpan.FromSeconds(5));
        debouncer.Signal();
        time.Advance(TimeSpan.FromSeconds(5));

        Assert.Equal(0, calls);
    }
}

/// <summary>Runs against a real temporary folder with real file-system events.</summary>
public sealed class LocalFolderWatcherTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "leviathan-watch-" + Guid.NewGuid().ToString("N"));

    public LocalFolderWatcherTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // Best effort.
        }
    }

    private static LocalFolderWatcher Fast() => new() { Delay = TimeSpan.FromMilliseconds(50), MinGap = TimeSpan.FromMilliseconds(50), IsMappedDrive = _ => false };

    [Fact]
    public async Task ANewFile_IsReported()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Locations are Windows paths.");
        var changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var watch = Fast().Watch(LocationPath.Parse(_root), () => changed.TrySetResult());

        await File.WriteAllTextAsync(Path.Combine(_root, "new.txt"), "x");

        await changed.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task ARemovedFile_IsReported()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Locations are Windows paths.");
        var path = Path.Combine(_root, "old.txt");
        await File.WriteAllTextAsync(path, "x");
        var changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var watch = Fast().Watch(LocationPath.Parse(_root), () => changed.TrySetResult());

        File.Delete(path);

        await changed.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task AfterDispose_NothingIsReported()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Locations are Windows paths.");
        var calls = 0;
        var watch = Fast().Watch(LocationPath.Parse(_root), () => Interlocked.Increment(ref calls));
        watch.Dispose();

        await File.WriteAllTextAsync(Path.Combine(_root, "late.txt"), "x");
        await Task.Delay(300);

        Assert.Equal(0, calls);
    }

    [Fact]
    public void ANetworkPath_IsNotWatched_AndNoErrorIsRaised()
    {
        var calls = 0;
        using var watch = Fast().Watch(LocationPath.Parse(@"\\nas\media\films"), () => calls++);

        Assert.NotNull(watch);
        Assert.Equal(0, calls);
    }

    [Fact]
    public void AMappedDrive_IsNotWatched()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Locations are Windows paths.");
        var asked = false;
        var watcher = new LocalFolderWatcher { IsMappedDrive = _ => asked = true };

        using var watch = watcher.Watch(LocationPath.Parse(_root), () => { });

        Assert.True(asked);
    }

    [Fact]
    public void AFolderThatDoesNotExist_GivesAnInertSubscription()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Locations are Windows paths.");
        using var watch = Fast().Watch(LocationPath.Parse(Path.Combine(_root, "missing")), () => { });

        Assert.NotNull(watch);
    }
}
