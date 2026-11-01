using System.Collections.Specialized;
using System.ComponentModel;
using Leviathan.Core.Model;
using Leviathan.Core.Platform;
using Leviathan.Core.Providers;
using Leviathan.ViewModels.Commands;
using Leviathan.ViewModels.Folders;
using Leviathan.ViewModels.Tabs;
using Leviathan.ViewModels.Windows;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace Leviathan.App;

/// <summary>
/// One Leviathan window (spec §4): tab strip in the title bar, navigation row, sidebar, file view, preview pane and status bar.
/// It only shows what the view models hold and forwards every action to the window's command registry, so the buttons, the
/// shortcuts and the action trail all go through the same commands. Slices D2 to D11 replace the placeholder controls here with
/// the real tab strip, breadcrumb bar, column grid and sidebar tree.
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001:Types that own disposable fields should be disposable",
    Justification = "The cancellation source is cancelled and released when the window closes, which a Window cannot express as IDisposable.")]
public sealed partial class MainWindow : Window, IShellActions
{
    private static DiagnosticsWindow? s_diagnostics;

    private readonly WindowViewModel _viewModel;
    private readonly CommandRegistry _commands;
    private readonly CancellationTokenSource _closing = new();
    private readonly Dictionary<TabViewModel, TabViewItem> _tabItems = [];
    private TabViewModel? _bound;
    private bool _syncingTabs;

    /// <summary>Creates the window for <paramref name="viewModel"/>.</summary>
    internal MainWindow(WindowViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        _viewModel = viewModel;
        InitializeComponent();
        Title = "Leviathan";

        // The tab strip sits in the title bar; the empty footer of the strip is the part you can drag the window by.
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(DragRegion);

        var host = App.Host;
        _commands = WindowCommands.Create(App.Actions, viewModel, host.Windows, host.Resolver, host.Launcher, this, () => host.Home);

        viewModel.Tabs.Tabs.CollectionChanged += OnTabsChanged;
        viewModel.Tabs.PropertyChanged += OnStripPropertyChanged;
        App.StallsChanged += OnStallsChanged;
        Closed += OnClosed;

        SyncTabs();
        ShowStalls();
        _ = LoadSidebarAsync();
    }

    /// <inheritdoc />
    public void FocusAddressBar() => OnUi(() =>
    {
        AddressBox.Focus(FocusState.Programmatic);
        AddressBox.SelectAll();
    });

    /// <inheritdoc />
    public void OpenDiagnostics() => OnUi(() =>
    {
        if (s_diagnostics is null)
        {
            s_diagnostics = new DiagnosticsWindow();
            s_diagnostics.Closed += (_, _) => s_diagnostics = null;
        }

        s_diagnostics.Activate();
    });

    /// <inheritdoc />
    public void ShowMessage(string message) => OnUi(() => MessageText.Text = message);

    private void OnUi(Action action)
    {
        if (DispatcherQueue.HasThreadAccess)
        {
            action();
        }
        else
        {
            DispatcherQueue.TryEnqueue(() => action());
        }
    }

    // Commands ---------------------------------------------------------------------------------------------------------

    /// <summary>Runs a command. Cancellation and expected I/O failures end quietly in the status bar; anything else is a bug and reaches the crash handler.</summary>
    private async void Run(CommandId id, string? target = null, object? parameter = null)
    {
        try
        {
            MessageText.Text = string.Empty;
            await _commands.ExecuteAsync(id, new CommandContext(target, parameter), _closing.Token);
        }
        catch (OperationCanceledException)
        {
            // The window closed or the operation was replaced; nothing to report.
        }
        catch (LocationException ex)
        {
            MessageText.Text = ex.Message;
        }
        finally
        {
            RefreshNavigationButtons();
        }
    }

    private void RefreshNavigationButtons()
    {
        BackButton.IsEnabled = _commands.CanExecute(CommandIds.NavBack);
        ForwardButton.IsEnabled = _commands.CanExecute(CommandIds.NavForward);
        UpButton.IsEnabled = _commands.CanExecute(CommandIds.NavUp);
        ReloadButton.IsEnabled = _commands.CanExecute(CommandIds.NavReload);
    }

    private void OnBackClick(object sender, RoutedEventArgs e) => Run(CommandIds.NavBack);

    private void OnForwardClick(object sender, RoutedEventArgs e) => Run(CommandIds.NavForward);

    private void OnUpClick(object sender, RoutedEventArgs e) => Run(CommandIds.NavUp);

    private void OnReloadClick(object sender, RoutedEventArgs e) => Run(CommandIds.NavReload);

    private void OnNewWindowClick(object sender, RoutedEventArgs e) => Run(CommandIds.WindowNew);

    private void OnDiagnosticsClick(object sender, RoutedEventArgs e) => Run(CommandIds.AppDiagnostics);

    private void OnAddTabClick(TabView sender, object args) => Run(CommandIds.TabNew);

    private void OnTabCloseRequested(TabView sender, TabViewTabCloseRequestedEventArgs args)
    {
        if (args.Tab.Tag is TabViewModel tab)
        {
            App.Actions.Record("tab.close", tab.History.Current.Value);
            _viewModel.Tabs.Close(tab, force: true);
        }
    }

    // Keyboard ---------------------------------------------------------------------------------------------------------

    private void OnPreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (KeyTranslator.IsDown(VirtualKey.Control) && !KeyTranslator.IsDown(VirtualKey.Menu) && KeyTranslator.DigitOf(e.Key) is > 0 and var digit)
        {
            e.Handled = true;
            Run(CommandIds.TabSelect, parameter: digit);
            return;
        }

        if (KeyTranslator.ToGesture(e.Key) is { } gesture && App.Host.Keys.Resolve(gesture, _viewModel.Tabs.Selected?.IsSplit == true) is { } command)
        {
            if (_commands.CanExecute(command))
            {
                e.Handled = true;
                Run(command);
            }
        }
    }

    // Address bar ------------------------------------------------------------------------------------------------------

    private void OnAddressGotFocus(object sender, RoutedEventArgs e) => AddressBox.SelectAll();

    private void OnAddressKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter)
        {
            e.Handled = true;
            Run(CommandIds.NavGo, AddressBox.Text);
            FileList.Focus(FocusState.Programmatic);
        }
        else if (e.Key == VirtualKey.Escape)
        {
            e.Handled = true;
            ShowAddress();
            FileList.Focus(FocusState.Programmatic);
        }
    }

    private void ShowAddress()
    {
        if (_bound is not null)
        {
            AddressBox.Text = _bound.Address.Location.Value;
        }
    }

    // Tabs -------------------------------------------------------------------------------------------------------------

    private void OnTabsChanged(object? sender, NotifyCollectionChangedEventArgs e) => OnUi(SyncTabs);

    private void OnStripPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(TabStripViewModel.Selected))
        {
            OnUi(SyncSelectedTab);
        }
    }

    /// <summary>Makes the tab strip show exactly the view model's tabs, in order.</summary>
    private void SyncTabs()
    {
        _syncingTabs = true;
        try
        {
            foreach (var removed in _tabItems.Keys.Except(_viewModel.Tabs.Tabs).ToList())
            {
                removed.PropertyChanged -= OnTabPropertyChanged;
                _tabItems.Remove(removed);
            }

            TabsView.TabItems.Clear();
            foreach (var tab in _viewModel.Tabs.Tabs)
            {
                if (!_tabItems.TryGetValue(tab, out var item))
                {
                    item = new TabViewItem { Tag = tab, IconSource = new SymbolIconSource { Symbol = Symbol.Folder } };
                    tab.PropertyChanged += OnTabPropertyChanged;
                    _tabItems[tab] = item;
                }

                item.Header = tab.Title;
                item.IsClosable = !tab.IsPinned;
                TabsView.TabItems.Add(item);
            }
        }
        finally
        {
            _syncingTabs = false;
        }

        SyncSelectedTab();
    }

    private void SyncSelectedTab()
    {
        var selected = _viewModel.Tabs.Selected;
        _syncingTabs = true;
        try
        {
            TabsView.SelectedItem = selected is not null && _tabItems.TryGetValue(selected, out var item) ? item : null;
        }
        finally
        {
            _syncingTabs = false;
        }

        BindTab(selected);
    }

    private void OnTabSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_syncingTabs && TabsView.SelectedItem is TabViewItem { Tag: TabViewModel tab })
        {
            _viewModel.Tabs.Select(tab);
        }
    }

    private void OnTabPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is TabViewModel tab && _tabItems.TryGetValue(tab, out var item) && e.PropertyName is nameof(TabViewModel.Title) or nameof(TabViewModel.CustomTitle))
        {
            OnUi(() => item.Header = tab.Title);
        }
    }

    /// <summary>Shows <paramref name="tab"/> in the body: its rows, address, state and status. Lists the folder if the tab was restored without one.</summary>
    private void BindTab(TabViewModel? tab)
    {
        if (ReferenceEquals(_bound, tab))
        {
            return;
        }

        if (_bound is not null)
        {
            _bound.Folder.PropertyChanged -= OnFolderPropertyChanged;
            _bound.Items.PropertyChanged -= OnItemsPropertyChanged;
            _bound.Address.PropertyChanged -= OnAddressPropertyChanged;
        }

        _bound = tab;
        if (tab is null)
        {
            FileList.ItemsSource = null;
            return;
        }

        FileList.ItemsSource = tab.Items.Rows;
        tab.Folder.PropertyChanged += OnFolderPropertyChanged;
        tab.Items.PropertyChanged += OnItemsPropertyChanged;
        tab.Address.PropertyChanged += OnAddressPropertyChanged;
        ShowAddress();
        UpdateState();
        UpdateStatus();
        RefreshNavigationButtons();
        if (tab.Folder.State == FolderLoadState.Idle)
        {
            Run(CommandIds.NavReload);
        }
    }

    private void OnFolderPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        OnUi(() =>
        {
            UpdateState();
            UpdateStatus();
            RefreshNavigationButtons();
        });
    }

    private void OnItemsPropertyChanged(object? sender, PropertyChangedEventArgs e) => OnUi(() =>
    {
        UpdateStatus();
        UpdateState();
    });

    private void OnAddressPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ViewModels.Navigation.AddressBarViewModel.Location))
        {
            OnUi(() =>
            {
                ShowAddress();
                RefreshNavigationButtons();
            });
        }
    }

    // File view --------------------------------------------------------------------------------------------------------

    private void OnFileDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if ((e.OriginalSource as FrameworkElement)?.DataContext is FileEntry entry)
        {
            Run(CommandIds.ItemOpen, entry.Location.Value, entry);
        }
    }

    private void OnFileKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter && FileList.SelectedItem is FileEntry entry)
        {
            e.Handled = true;
            Run(CommandIds.ItemOpen, entry.Location.Value, entry);
        }
        else if (e.Key == VirtualKey.Back && !KeyTranslator.IsDown(VirtualKey.Control))
        {
            e.Handled = true;
            Run(CommandIds.NavBack);
        }
    }

    // The ListView keeps its own selection until the column grid (slice D4) is driven by the SelectionModel.
    private void OnFileSelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateStatus();

    private void UpdateState()
    {
        var tab = _bound;
        var state = tab?.Folder.State ?? FolderLoadState.Idle;
        var hasRows = tab?.Items.ShownCount > 0;
        var (title, detail, busy) = state switch
        {
            FolderLoadState.Loading when !hasRows => ("Loading…", string.Empty, true),
            FolderLoadState.Empty => ("This folder is empty", string.Empty, false),
            FolderLoadState.NotFound => ("Folder not found", tab?.Folder.Message ?? string.Empty, false),
            FolderLoadState.AccessDenied => ("Access denied", tab?.Folder.Message ?? string.Empty, false),
            FolderLoadState.Offline => ("Not responding", $"{tab?.Folder.Message} Press F5 to try again.", false),
            FolderLoadState.Error => ("This folder could not be opened", tab?.Folder.Message ?? string.Empty, false),
            FolderLoadState.Cancelled when !hasRows => ("Listing cancelled", "Press F5 to list this folder again.", false),
            _ => (string.Empty, string.Empty, false),
        };

        StatePanel.Visibility = title.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        StateTitle.Text = title;
        StateDetail.Text = detail;
        StateRing.IsActive = busy;
        StateRing.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateStatus()
    {
        if (_bound is null)
        {
            StatusText.Text = string.Empty;
            return;
        }

        var selected = FileList.SelectedItems.OfType<FileEntry>().ToList();
        var bytes = selected.Where(e => e.Kind == FileEntryKind.File).Sum(e => e.Size);
        StatusText.Text = DisplayFormat.StatusText(_bound.Items.TotalCount, _bound.Items.ShownCount, selected.Count, bytes);
    }

    // Sidebar (placeholder) --------------------------------------------------------------------------------------------

    private async Task LoadSidebarAsync()
    {
        var host = App.Host;
        var items = new List<SidebarItem>();
        try
        {
            // Known folders and drive details read the registry and the disks: never on the UI thread.
            var known = await Task.Run(() => new[]
            {
                (KnownFolder.Home, "Home", ""),
                (KnownFolder.Desktop, "Desktop", ""),
                (KnownFolder.Documents, "Documents", ""),
                (KnownFolder.Downloads, "Downloads", ""),
                (KnownFolder.Pictures, "Pictures", ""),
                (KnownFolder.Videos, "Videos", ""),
                (KnownFolder.Music, "Music", ""),
            }.Select(k => (Location: host.KnownFolders.Get(k.Item1), k.Item2, k.Item3)).ToList(), _closing.Token);
            items.AddRange(known.Where(k => k.Location is not null).Select(k => new SidebarItem(k.Item2, string.Empty, k.Item3, k.Location!.Value)));

            var drives = await host.Volumes.GetDrivesAsync(_closing.Token);
            items.AddRange(drives.Select(d => new SidebarItem(
                d.DisplayName,
                !d.IsResponsive ? "Not responding" : d.NetworkPath ?? (d.HasSizes ? $"{DisplayFormat.Size(d.FreeBytes)} free of {DisplayFormat.Size(d.TotalBytes)}" : string.Empty),
                d.Kind == DriveKind.Network ? "" : "",
                d.Root.Value)));
        }
        catch (OperationCanceledException)
        {
            return;
        }

        Sidebar.ItemsSource = items;
    }

    private void OnSidebarItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is SidebarItem item)
        {
            Run(CommandIds.NavGo, item.Path);
        }
    }

    // Status -----------------------------------------------------------------------------------------------------------

    private void OnStallsChanged(object? sender, EventArgs e) => OnUi(ShowStalls);

    private void ShowStalls() => StallText.Text = App.StallCount > 0 ? $"UI stalls: {App.StallCount}" : string.Empty;

    private void OnClosed(object sender, WindowEventArgs args)
    {
        App.StallsChanged -= OnStallsChanged;
        _viewModel.Tabs.Tabs.CollectionChanged -= OnTabsChanged;
        _viewModel.Tabs.PropertyChanged -= OnStripPropertyChanged;
        _closing.Cancel();
        _closing.Dispose();
    }
}
