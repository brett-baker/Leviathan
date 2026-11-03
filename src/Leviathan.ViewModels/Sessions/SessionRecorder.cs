using System.Collections.Specialized;
using System.ComponentModel;
using Leviathan.ViewModels.Folders;
using Leviathan.ViewModels.Tabs;
using Leviathan.ViewModels.Windows;

namespace Leviathan.ViewModels.Sessions;

/// <summary>
/// Keeps the built-in "Last session" profile current (spec §17.1): every change to the windows, their tabs, history, views or
/// positions schedules a save two seconds later (each change moves the deadline, so a burst is one write), and the last window
/// closing saves at once. Because it is written continuously it survives a crash or a power cut, not only a clean exit. The
/// capture runs on the UI thread, the file write on the thread pool, so saving never stalls the window (spec §3.2). Nothing
/// empty is ever written over a good session.
/// </summary>
public sealed class SessionRecorder : IDisposable
{
    /// <summary>Quiet time before a change is written.</summary>
    public static readonly TimeSpan Delay = TimeSpan.FromSeconds(2);

    private readonly WindowManager _windows;
    private readonly JsonFileStore<SessionFile> _store;
    private readonly IUiContext _ui;
    private readonly TimeProvider _time;
    private readonly ITimer _timer;
    private readonly Lock _gate = new();
    private readonly HashSet<WindowViewModel> _hookedWindows = [];
    private readonly HashSet<TabViewModel> _hookedTabs = [];
    private readonly HashSet<TabGroup> _hookedGroups = [];
    private Task _write = Task.CompletedTask;
    private bool _frozen;
    private bool _disposed;

    /// <summary>Creates the recorder. Call <see cref="Start"/> on the UI thread.</summary>
    public SessionRecorder(WindowManager windows, JsonFileStore<SessionFile> store, IUiContext ui, TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(windows);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(ui);
        _windows = windows;
        _store = store;
        _ui = ui;
        _time = time ?? TimeProvider.System;
        _timer = _time.CreateTimer(_ => _ui.Post(CaptureAndQueue), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    /// <summary>The last queued write; tests await it.</summary>
    public Task Saved
    {
        get
        {
            lock (_gate)
            {
                return _write;
            }
        }
    }

    /// <summary>Writes that failed (disk full, locked file). The session is simply not saved that time.</summary>
    public int Failures { get; private set; }

    /// <summary>Starts watching the windows that are open and every one that opens later.</summary>
    public void Start()
    {
        foreach (var window in _windows.Windows)
        {
            Hook(window);
        }

        _windows.WindowOpened += OnWindowOpened;
        _windows.WindowClosed += OnWindowClosed;
    }

    /// <summary>Something worth saving changed. Callable from any thread.</summary>
    public void MarkDirty()
    {
        if (!_disposed && !_frozen)
        {
            _timer.Change(Delay, Timeout.InfiniteTimeSpan);
        }
    }

    /// <summary>
    /// Writes the session now and stops further saves. Called when the last window begins to close: its tabs are still there, and
    /// the windows closing after it must not replace the session with an empty one.
    /// </summary>
    public Task SaveFinalAsync()
    {
        CaptureAndQueue();
        _frozen = true;
        _timer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        return Saved;
    }

    /// <summary>Saves normally again after <see cref="SaveFinalAsync"/> (a profile was opened into a running app).</summary>
    public void Resume() => _frozen = false;

    /// <inheritdoc />
    public void Dispose()
    {
        _disposed = true;
        _timer.Dispose();
        _windows.WindowOpened -= OnWindowOpened;
        _windows.WindowClosed -= OnWindowClosed;
        foreach (var window in _hookedWindows.ToList())
        {
            Unhook(window);
        }
    }

    private void CaptureAndQueue()
    {
        if (_disposed || _frozen)
        {
            return;
        }

        var state = SessionCapture.Capture(_windows);
        if (state.IsEmpty)
        {
            return;
        }

        var file = new SessionFile(SessionFile.CurrentSchemaVersion, _time.GetUtcNow(), state);
        lock (_gate)
        {
            _write = _write.ContinueWith(_ => WriteAsync(file), CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default).Unwrap();
        }
    }

    private async Task WriteAsync(SessionFile file)
    {
        try
        {
            await _store.SaveAsync(file, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Failures++;
        }
    }

    private void OnWindowOpened(object? sender, WindowViewModel window)
    {
        Hook(window);
        MarkDirty();
    }

    private void OnWindowClosed(object? sender, WindowViewModel window)
    {
        Unhook(window);
        MarkDirty();
    }

    private void Hook(WindowViewModel window)
    {
        if (!_hookedWindows.Add(window))
        {
            return;
        }

        window.PropertyChanged += OnChanged;
        window.Tabs.PropertyChanged += OnChanged;
        window.Tabs.Tabs.CollectionChanged += OnTabsChanged;
        window.Tabs.Groups.CollectionChanged += OnGroupsChanged;
        foreach (var tab in window.Tabs.Tabs)
        {
            Hook(tab);
        }

        foreach (var group in window.Tabs.Groups)
        {
            Hook(group);
        }
    }

    private void Unhook(WindowViewModel window)
    {
        if (!_hookedWindows.Remove(window))
        {
            return;
        }

        window.PropertyChanged -= OnChanged;
        window.Tabs.PropertyChanged -= OnChanged;
        window.Tabs.Tabs.CollectionChanged -= OnTabsChanged;
        window.Tabs.Groups.CollectionChanged -= OnGroupsChanged;
        foreach (var tab in window.Tabs.Tabs)
        {
            Unhook(tab);
        }

        foreach (var group in window.Tabs.Groups)
        {
            Unhook(group);
        }
    }

    private void Hook(TabGroup group)
    {
        if (_hookedGroups.Add(group))
        {
            group.PropertyChanged += OnChanged;
        }
    }

    private void Unhook(TabGroup group)
    {
        if (_hookedGroups.Remove(group))
        {
            group.PropertyChanged -= OnChanged;
        }
    }

    private void OnGroupsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        foreach (var group in e.NewItems?.OfType<TabGroup>() ?? [])
        {
            Hook(group);
        }

        foreach (var group in e.OldItems?.OfType<TabGroup>() ?? [])
        {
            Unhook(group);
        }

        MarkDirty();
    }

    private void Hook(TabViewModel tab)
    {
        if (_hookedTabs.Add(tab))
        {
            tab.PropertyChanged += OnChanged;
            tab.Selection.Changed += OnSelectionChanged;
        }
    }

    private void Unhook(TabViewModel tab)
    {
        if (_hookedTabs.Remove(tab))
        {
            tab.PropertyChanged -= OnChanged;
            tab.Selection.Changed -= OnSelectionChanged;
        }
    }

    private void OnTabsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        foreach (var tab in e.NewItems?.OfType<TabViewModel>() ?? [])
        {
            Hook(tab);
        }

        foreach (var tab in e.OldItems?.OfType<TabViewModel>() ?? [])
        {
            Unhook(tab);
        }

        MarkDirty();
    }

    private void OnChanged(object? sender, PropertyChangedEventArgs e) => MarkDirty();

    private void OnSelectionChanged(object? sender, EventArgs e) => MarkDirty();
}
