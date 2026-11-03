using System.Collections.Concurrent;
using Leviathan.Contracts;
using Leviathan.Interop;

namespace Leviathan.ShellHost;

/// <summary>A shell context menu as the host uses it. All members run on the session thread that made it.</summary>
internal interface IShellMenuInstance : IDisposable
{
    IReadOnlyList<ShellMenuItem> Items { get; }

    IReadOnlyList<ShellMenuItem> LoadSubMenu(IReadOnlyList<int> path);

    /// <summary>Runs a command; returns the HRESULT (0 or positive is success).</summary>
    int Invoke(int itemId, string? verb, string? workingDirectory, nint ownerWindow);
}

/// <summary>Builds shell menus. Seam over the Win32 code so the manager's timeout, eviction and cleanup rules are tested without a shell.</summary>
internal interface IShellMenuSource
{
    /// <summary>Builds the menu; called on the thread that will own it.</summary>
    IShellMenuInstance Open(ShellMenuRequest request);
}

/// <summary><see cref="IShellMenuSource"/> over the real shell (<see cref="ShellContextMenu"/>).</summary>
internal sealed class WindowsShellMenuSource : IShellMenuSource
{
    public IShellMenuInstance Open(ShellMenuRequest request) => new Instance(ShellContextMenu.Create(request.Paths, request.Background, request.Extended));

    private sealed class Instance(ShellContextMenu menu) : IShellMenuInstance
    {
        public IReadOnlyList<ShellMenuItem> Items => menu.Items;

        public IReadOnlyList<ShellMenuItem> LoadSubMenu(IReadOnlyList<int> path) => menu.LoadSubMenu(path);

        public int Invoke(int itemId, string? verb, string? workingDirectory, nint ownerWindow) => menu.Invoke(itemId, verb, workingDirectory, ownerWindow);

        public void Dispose() => menu.Dispose();
    }
}

/// <summary>
/// Test switch (<c>--fault-crash-once &lt;marker file&gt;</c>): the first time a menu is opened the host ends the way a crashing
/// shell extension would end it, abruptly and with a failure code; the marker file stops the restarted host doing it again. It lets
/// the integration test prove that a handler's crash costs a restart of this process and nothing else (spec §2.2, §3.4).
/// </summary>
internal sealed class CrashOnceMenuSource(IShellMenuSource inner, string marker) : IShellMenuSource
{
    public IShellMenuInstance Open(ShellMenuRequest request)
    {
        if (!File.Exists(marker))
        {
            File.WriteAllText(marker, "crashed");
            Environment.Exit(99);
        }

        return inner.Open(request);
    }
}

/// <summary>
/// Keeps the open context menus of the host. Each menu lives on a thread of its own with a message loop, because the COM objects
/// the shell extensions made must be used where they were made, and is closed after <see cref="IdleTimeout"/> if nobody uses it
/// (a menu the user dismissed without telling us). A request that outlives its limit means a handler is stuck: the menu is
/// written off, the caller gets a <see cref="TimeoutException"/>, and enough of those raise <see cref="Unhealthy"/> so the host
/// restarts (spec §3.4). The most open menus are limited so a stream of right-clicks cannot pile up threads.
/// </summary>
internal sealed class ShellMenuManager : IDisposable
{
    private readonly IShellMenuSource _source;
    private readonly TimeProvider _time;
    private readonly int _maxOpen;
    private readonly int _maxAbandoned;
    private readonly ConcurrentDictionary<Guid, Entry> _open = new();
    private int _abandoned;

    public ShellMenuManager(IShellMenuSource source, TimeProvider? time = null, int maxOpen = 4, int maxAbandoned = 8)
    {
        ArgumentNullException.ThrowIfNull(source);
        _source = source;
        _time = time ?? TimeProvider.System;
        _maxOpen = maxOpen;
        _maxAbandoned = maxAbandoned;
    }

    /// <summary>How long a menu may sit unused before it is closed.</summary>
    public TimeSpan IdleTimeout { get; init; } = TimeSpan.FromMinutes(2);

    /// <summary>How long building a menu may take. Cold handlers (a first right-click after boot) can need several seconds.</summary>
    public TimeSpan OpenTimeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>How long reading a submenu may take.</summary>
    public TimeSpan CallTimeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>How long an invoke is awaited. A command that opens a dialog does not return until it closes; after this the caller is told it started.</summary>
    public TimeSpan InvokeWait { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>Raised once too many menus have hung. The host should exit and be restarted.</summary>
    public event EventHandler? Unhealthy;

    public int OpenCount => _open.Count;

    public int Abandoned => Volatile.Read(ref _abandoned);

    public async Task<ShellMenu> OpenAsync(ShellMenuRequest request, CancellationToken cancellationToken)
    {
        Validate(request);
        while (_open.Count >= _maxOpen && _open.OrderBy(e => e.Value.LastUsed).FirstOrDefault() is { Value: { } oldest })
        {
            await CloseAsync(oldest.Id).ConfigureAwait(false);
        }

        var entry = new Entry(new StaMessageThread("Leviathan shell menu"), _time);
        try
        {
            entry.Instance = await RunAsync(entry, () => _source.Open(request), OpenTimeout, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            Release(entry);
            throw;
        }

        _open[entry.Id] = entry;
        return new ShellMenu(entry.Id, entry.Instance.Items);
    }

    public async Task<IReadOnlyList<ShellMenuItem>> LoadSubMenuAsync(Guid menuId, IReadOnlyList<int> path, CancellationToken cancellationToken)
    {
        var entry = Find(menuId);
        return await RunAsync(entry, () => entry.Instance!.LoadSubMenu(path), CallTimeout, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ShellInvokeResult> InvokeAsync(Guid menuId, ShellInvokeRequest request, CancellationToken cancellationToken)
    {
        var entry = Find(menuId);
        entry.Touch();
        entry.Busy = true;
        Task<int> work;
        try
        {
            work = entry.Thread.RunAsync(() => entry.Instance!.Invoke(request.ItemId, request.Verb, request.WorkingDirectory, (nint)request.OwnerWindow), CancellationToken.None);
            var finished = await Task.WhenAny(work, Task.Delay(InvokeWait, _time, cancellationToken)).ConfigureAwait(false);
            if (finished != work)
            {
                // Probably a dialog the user is looking at. The thread must keep its loop; the menu closes when it finishes.
                _ = work.ContinueWith(_ => entry.Busy = false, TaskScheduler.Default);
                cancellationToken.ThrowIfCancellationRequested();
                return new ShellInvokeResult(true, null);
            }

            var result = await work.ConfigureAwait(false);
            entry.Busy = false;
            return result >= 0 ? new ShellInvokeResult(true, null) : new ShellInvokeResult(false, $"The shell did not run the command (0x{result:X8}).");
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException or ArgumentException)
        {
            entry.Busy = false;
            return new ShellInvokeResult(false, ex.Message);
        }
        finally
        {
            entry.Touch();
        }
    }

    public async Task CloseAsync(Guid menuId)
    {
        if (!_open.TryRemove(menuId, out var entry))
        {
            return;
        }

        // Release the handlers where they were made; the thread then ends. Not awaited past a limit: a stuck handler must not hold the caller.
        if (entry.Instance is { } instance && !entry.Dead)
        {
            try
            {
                await entry.Thread.RunAsync(() =>
                {
                    instance.Dispose();
                    return 0;
                }).WaitAsync(CallTimeout, _time).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is TimeoutException or System.Runtime.InteropServices.COMException)
            {
                WriteOff(entry);
            }
        }

        entry.Thread.Dispose();
    }

    /// <summary>Closes the menus nobody has used for <see cref="IdleTimeout"/>.</summary>
    public void SweepIdle()
    {
        var now = _time.GetUtcNow();
        foreach (var (id, entry) in _open)
        {
            if (!entry.Busy && now - entry.LastUsed >= IdleTimeout)
            {
                _ = CloseAsync(id);
            }
        }
    }

    public void Dispose()
    {
        foreach (var entry in _open.Values)
        {
            entry.Thread.Dispose();
        }

        _open.Clear();
    }

    private static void Validate(ShellMenuRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Paths is not { Count: > 0 and <= 5000 })
        {
            throw new ArgumentException("A menu needs between 1 and 5000 items.", nameof(request));
        }

        if (request.Background && request.Paths.Count != 1)
        {
            throw new ArgumentException("A background menu belongs to one folder.", nameof(request));
        }

        foreach (var path in request.Paths)
        {
            if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
            {
                throw new ArgumentException($"'{path}' is not a full path.", nameof(request));
            }
        }
    }

    private Entry Find(Guid menuId) =>
        _open.TryGetValue(menuId, out var entry) && !entry.Dead
            ? entry
            : throw new KeyNotFoundException("That menu is no longer open. Right-click again.");

    private async Task<T> RunAsync<T>(Entry entry, Func<T> work, TimeSpan timeout, CancellationToken cancellationToken)
    {
        entry.Touch();
        try
        {
            return await entry.Thread.RunAsync(work, CancellationToken.None).WaitAsync(timeout, _time, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            WriteOff(entry);
            throw new TimeoutException($"A shell extension did not answer within {timeout.TotalSeconds:0.#} s.");
        }
        finally
        {
            entry.Touch();
        }
    }

    private void WriteOff(Entry entry)
    {
        if (entry.Dead)
        {
            return;
        }

        entry.Dead = true;
        _open.TryRemove(entry.Id, out _);
        entry.Thread.Dispose();
        if (Interlocked.Increment(ref _abandoned) >= _maxAbandoned)
        {
            Unhealthy?.Invoke(this, EventArgs.Empty);
        }
    }

    private void Release(Entry entry)
    {
        _open.TryRemove(entry.Id, out _);
        entry.Thread.Dispose();
    }

    private sealed class Entry(StaMessageThread thread, TimeProvider time)
    {
        public Guid Id { get; } = Guid.NewGuid();

        public StaMessageThread Thread { get; } = thread;

        public IShellMenuInstance? Instance { get; set; }

        public DateTimeOffset LastUsed { get; private set; } = time.GetUtcNow();

        public volatile bool Busy;

        public volatile bool Dead;

        public void Touch() => LastUsed = time.GetUtcNow();
    }
}
