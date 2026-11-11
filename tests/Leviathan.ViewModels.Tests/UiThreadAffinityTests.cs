using System.Collections.Concurrent;
using System.Collections.Specialized;
using System.ComponentModel;
using System.IO.Abstractions.TestingHelpers;
using Leviathan.Core.Paths;
using Leviathan.Core.Platform;
using Leviathan.Core.Providers;
using Leviathan.Diagnostics;
using Leviathan.Testing;
using Leviathan.ViewModels.Commands;
using Leviathan.ViewModels.Folders;
using Leviathan.ViewModels.Navigation;
using Leviathan.ViewModels.Tabs;
using Leviathan.ViewModels.Views;
using Leviathan.ViewModels.Windows;

namespace Leviathan.ViewModels.Tests;

/// <summary>
/// A single-threaded message loop standing in for the WinUI dispatcher: continuations that capture it run on its thread, and
/// <see cref="IUiContext.Post"/> queues onto it. Anything that runs elsewhere is what WinUI calls "a different thread".
/// </summary>
internal sealed class UiLoop : SynchronizationContext, IUiContext, IDisposable
{
    private readonly BlockingCollection<(SendOrPostCallback Callback, object? State)> _queue = [];
    private readonly Thread _thread;

    public UiLoop()
    {
        var ready = new ManualResetEventSlim();
        var id = 0;
        _thread = new Thread(() =>
        {
            SetSynchronizationContext(this);
            id = Environment.CurrentManagedThreadId;
            ready.Set();
            foreach (var (callback, state) in _queue.GetConsumingEnumerable())
            {
                callback(state);
            }
        })
        { IsBackground = true, Name = "Test UI thread" };
        _thread.Start();
        ready.Wait();
        ThreadId = id;
    }

    public int ThreadId { get; }

    public override void Post(SendOrPostCallback d, object? state) => _queue.Add((d, state));

    void IUiContext.Post(Action action) => Post(_ => action(), null);

    /// <summary>Runs <paramref name="work"/> on the UI thread, like a button click handler, and waits for it to finish.</summary>
    public Task RunAsync(Func<Task> work)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Post(
            async _ =>
            {
                try
                {
                    await work();
                    done.SetResult();
                }
                catch (Exception ex)
                {
                    done.SetException(ex);
                }
            },
            null);
        return done.Task;
    }

    public void Dispose() => _queue.CompleteAdding();
}

/// <summary>
/// Crash signature 52a0ec04c34d (COMException 0x8001010E, RPC_E_WRONG_THREAD, in <c>SnapshotList.Replace</c>): clicking a folder in the
/// sidebar ran <c>nav.go</c>, whose continuation after <c>await resolver.ResolveAsync(...).ConfigureAwait(false)</c> updated the tab's
/// bound collections on a thread-pool thread. View models are UI-thread affine: every change a control can observe must be raised
/// on the thread that started the command.
/// </summary>
public sealed class UiThreadAffinityTests
{
    private sealed class Shell : IShellActions
    {
        public void FocusAddressBar()
        {
        }

        public void OpenDiagnostics()
        {
        }

        public void FocusFilterBox()
        {
        }

        public void FocusSearchBox()
        {
        }

        public bool ToggleSearchIndex(LocationPath folder) => false;

        public void OpenSpaceMap(LocationPath folder)
        {
        }

        public void ShowMessage(string message)
        {
        }

        public Task<bool> ConfirmAsync(string title, string message, string confirmText) => Task.FromResult(true);

        public void OpenContactSheet(Leviathan.Core.Model.FileEntry video)
        {
        }

        public void ExportContactSheets(IReadOnlyList<Leviathan.Core.Model.FileEntry> videos)
        {
        }

        public void ToggleQuickPreview()
        {
        }

        public void StartTriage()
        {
        }

        public void StartInboxTriage()
        {
        }

        public void FindDuplicates()
        {
        }
    }

    private sealed class NoLauncher : IFileLauncher
    {
        public Task<LaunchResult> OpenAsync(LocationPath path, CancellationToken cancellationToken) => Task.FromResult(LaunchResult.Ok);

        public Task<LaunchResult> RunAsAdministratorAsync(LocationPath path, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<LaunchResult> OpenWithAsync(LocationPath path, string executable, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<LaunchResult> ChooseAppAsync(LocationPath path, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<LaunchResult> OpenTerminalAsync(LocationPath folder, TerminalKind kind, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<LaunchResult> ShowPropertiesAsync(LocationPath path, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    /// <summary>
    /// Answers like a real disk or share: always asynchronously, from a pool thread. (An in-memory answer can complete before the
    /// caller awaits it, which would hide a continuation that wrongly resumes off the UI thread.)
    /// </summary>
    private sealed class SlowProbe(MockFileSystem fs) : IEntryProbe
    {
        public async Task<Leviathan.Core.Model.FileEntryKind?> GetKindAsync(LocationPath location, CancellationToken cancellationToken)
        {
            return await Task.Run(
                () =>
                {
                    Thread.Sleep(5);
                    var path = location.ToWin32Path();
                    return fs.Directory.Exists(path) ? Leviathan.Core.Model.FileEntryKind.Folder : fs.File.Exists(path) ? Leviathan.Core.Model.FileEntryKind.File : (Leviathan.Core.Model.FileEntryKind?)null;
                },
                CancellationToken.None).ConfigureAwait(false);
        }
    }

    private static string Locations(IEnumerable<(string Source, int Thread)> events, int uiThread) =>
        string.Join(", ", events.Where(e => e.Thread != uiThread).Select(e => $"{e.Source} on thread {e.Thread}").Distinct());

    [Fact]
    public async Task Signature52a0ec04c34d_Commands_RaiseEveryObservableChangeOnTheUiThread()
    {
        using var ui = new UiLoop();
        var events = new ConcurrentQueue<(string Source, int Thread)>();
        void Record(string source) => events.Enqueue((source, Environment.CurrentManagedThreadId));

        var fs = new MockFileSystem();
        foreach (var dir in new[] { @"C:\Home", @"C:\a", @"C:\a\b", @"C:\v" })
        {
            fs.AddDirectory(dir);
        }

        var services = new TabServices(new LocationProviderRegistry([TabTestSupport.Tree()]), ui, new FolderViewRules(), TimeProvider.System, () => TabTestSupport.P(@"C:\Home"));
        var windows = new WindowManager(services);
        WindowViewModel? window = null;
        CommandRegistry? commands = null;

        void Watch(TabViewModel tab)
        {
            tab.PropertyChanged += (_, e) => Record("tab." + e.PropertyName);
            tab.Items.Rows.CollectionChanged += (_, _) => Record("rows");
            tab.Items.PropertyChanged += (_, e) => Record("items." + e.PropertyName);
            tab.Address.PropertyChanged += (_, e) => Record("address." + e.PropertyName);
            tab.Folder.PropertyChanged += (_, e) => Record("folder." + e.PropertyName);
            tab.Folder.Entries.CollectionChanged += (_, _) => Record("entries");
            tab.Selection.Changed += (_, _) => Record("selection");
        }

        // Building the window and its first tab is what the app does on its UI thread at startup.
        await ui.RunAsync(() =>
        {
            window = windows.OpenWindow(TabTestSupport.P(@"C:\a"));
            window.Tabs.Tabs.CollectionChanged += (_, _) => Record("tabs");
            window.Tabs.PropertyChanged += (_, e) => Record("strip." + e.PropertyName);
            Watch(window.Tabs.Selected!);
            commands = WindowCommands.Create(new ActionTrail(), window, windows, new AddressResolver(new SlowProbe(fs)), new NoLauncher(), new Shell(), () => TabTestSupport.P(@"C:\Home"));
            return Task.CompletedTask;
        });

        Task Execute(CommandId id, string? target = null, object? parameter = null) =>
            ui.RunAsync(async () =>
            {
                await commands!.ExecuteAsync(id, new CommandContext(target, parameter), CancellationToken.None);
                if (window!.Tabs.Selected is { } selected && !events.IsEmpty)
                {
                    // New tabs from commands must be watched too; ignore duplicates by watching only unseen ones.
                }
            });

        // Each of these used to continue on a pool thread after an await, then touch the tab.
        await Execute(CommandIds.NavReload);
        await Execute(CommandIds.NavGo, @"C:\a\b");
        await Execute(CommandIds.NavGo, @"C:\a\1.txt");
        await Execute(CommandIds.NavUp);
        await Execute(CommandIds.NavBack);
        await Execute(CommandIds.NavForward);
        await ui.RunAsync(async () =>
        {
            var folder = window!.Tabs.Selected!.Items.Rows.First(r => r.Name == "b");
            await commands!.ExecuteAsync(CommandIds.ItemOpen, new CommandContext(folder.Location.Value, folder), CancellationToken.None);
        });
        await Execute(CommandIds.NavBack, parameter: 1);

        var wrong = Locations(events, ui.ThreadId);
        Assert.True(string.IsNullOrEmpty(wrong), $"Observable changes raised off the UI thread: {wrong}");
        Assert.NotEmpty(events);
    }

    [Fact]
    public async Task Signature52a0ec04c34d_NewTabsReopenAndDuplicate_ListOnTheUiThread()
    {
        using var ui = new UiLoop();
        var offThread = new ConcurrentBag<string>();
        var uiThread = ui.ThreadId;

        var services = new TabServices(new LocationProviderRegistry([TabTestSupport.Tree()]), ui, new FolderViewRules(), TimeProvider.System, () => TabTestSupport.P(@"C:\Home"));
        var windows = new WindowManager(services);
        var fs = new MockFileSystem();
        fs.AddDirectory(@"C:\v");

        await ui.RunAsync(async () =>
        {
            var window = windows.OpenWindow(TabTestSupport.P(@"C:\a"));
            window.Tabs.Tabs.CollectionChanged += (_, _) => Check("tabs");
            window.Tabs.PropertyChanged += (_, e) => Check("strip." + e.PropertyName);
            var commands = WindowCommands.Create(new ActionTrail(), window, windows, new AddressResolver(new SlowProbe(fs)), new NoLauncher(), new Shell(), () => TabTestSupport.P(@"C:\Home"));

            // The new tab only exists once the command has created it, so it is watched from the strip's change event.
            window.Tabs.Tabs.CollectionChanged += (_, args) =>
            {
                foreach (var tab in args.NewItems?.OfType<TabViewModel>() ?? [])
                {
                    tab.Items.Rows.CollectionChanged += (_, _) => Check("rows");
                    tab.Folder.PropertyChanged += (_, e) => Check("folder." + e.PropertyName);
                    tab.Address.PropertyChanged += (_, e) => Check("address." + e.PropertyName);
                }
            };

            await commands.ExecuteAsync(CommandIds.TabNew, new CommandContext(@"C:\v"), CancellationToken.None);
            await commands.ExecuteAsync(CommandIds.TabDuplicate, null, CancellationToken.None);
            await commands.ExecuteAsync(CommandIds.TabClose, null, CancellationToken.None);
            await commands.ExecuteAsync(CommandIds.TabReopen, null, CancellationToken.None);
        });

        Assert.True(offThread.IsEmpty, $"Observable changes raised off the UI thread: {string.Join(", ", offThread.Distinct())}");

        void Check(string source)
        {
            if (Environment.CurrentManagedThreadId != uiThread)
            {
                offThread.Add(source);
            }
        }
    }
}
