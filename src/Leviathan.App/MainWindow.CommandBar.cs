using Leviathan.Core.Model;
using Leviathan.Core.Paths;
using Leviathan.Core.Platform;
using Leviathan.ViewModels.CommandBar;
using Leviathan.ViewModels.Commands;
using Leviathan.ViewModels.Folders;
using Leviathan.ViewModels.NewMenu;
using Leviathan.ViewModels.Views;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace Leviathan.App;

/// <summary>
/// The command bar (spec §6): the fixed buttons, the context zone that follows the selection, and the New, View, Sort and
/// Group menus. Everything here only decides what to show; clicking runs a command, so the bar, the keys and the action trail
/// share one path. A button whose command is not registered yet (the clipboard group before slice E5) stays disabled.
/// </summary>
public sealed partial class MainWindow
{
    private static readonly TimeSpan FilterDelay = TimeSpan.FromMilliseconds(150);

    private readonly List<AppBarButton> _contextButtons = [];
    private readonly Dictionary<AppBarButton, BarButton> _barButtons = [];
    private string _contextKey = string.Empty;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _filterTimer;
    private bool _syncingFilter;

    /// <inheritdoc />
    public void FocusFilterBox() => OnUi(() =>
    {
        FilterBox.Focus(FocusState.Keyboard);
        FilterBox.SelectAll();
    });

    private void InitializeCommandBar()
    {
        foreach (var (button, model) in new (AppBarButton, BarButton)[]
        {
            (CutButton, new BarButton(CommandIds.EditCut, "Cut", string.Empty, "Ctrl+X")),
            (CopyButton, new BarButton(CommandIds.EditCopy, "Copy", string.Empty, "Ctrl+C")),
            (RenameButton, new BarButton(CommandIds.EditRename, "Rename", string.Empty, "F2")),
            (DeleteButton, new BarButton(CommandIds.EditDelete, "Delete", string.Empty, "Del")),
            (UndoButton, new BarButton(CommandIds.EditUndo, "Undo", string.Empty, "Ctrl+Z")),
            (RedoButton, new BarButton(CommandIds.EditRedo, "Redo", string.Empty, "Ctrl+Y")),
        })
        {
            _barButtons[button] = model;
            ToolTipService.SetToolTip(button, model.Tooltip);
        }

        ToolTipService.SetToolTip(PasteButton, "Paste (Ctrl+V)");
        _viewModel.PropertyChanged += OnWindowPropertyChanged;
        ApplyPreviewPane();
        RefreshCommandBar();
    }

    private void OnWindowPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ViewModels.Windows.WindowViewModel.ShowPreview))
        {
            OnUi(ApplyPreviewPane);
        }
    }

    private void ApplyPreviewPane()
    {
        PreviewPane.Visibility = _viewModel.ShowPreview ? Visibility.Visible : Visibility.Collapsed;
        PreviewColumn.Width = new GridLength(_viewModel.ShowPreview ? 280 : 0);
        PreviewToggle.IsChecked = _viewModel.ShowPreview;
    }

    private void OnPreviewToggleClick(object sender, RoutedEventArgs e) => Run(CommandIds.PreviewPane);

    // Splitting arrives in Phase 2; the one-pane button stays down.
    private void OnPaneSingleClick(object sender, RoutedEventArgs e) => PaneSingleButton.IsChecked = true;

    // Enabled state ----------------------------------------------------------------------------------------------------

    /// <summary>Brings the enabled state of every bar control, and the context zone, in line with the tab and its selection.</summary>
    private void RefreshCommandBar()
    {
        foreach (var (button, model) in _barButtons)
        {
            button.IsEnabled = _commands.CanExecute(model.Command);
        }

        PasteButton.IsEnabled = _commands.CanExecute(CommandIds.EditPaste);
        NewButton.IsEnabled = _commands.CanExecute(CommandIds.NewFolder);
        var hasTab = _bound is not null;
        ViewButton.IsEnabled = SortButton.IsEnabled = GroupButton.IsEnabled = FilterBox.IsEnabled = hasTab;
        RefreshContextZone();
    }

    private void RefreshContextZone()
    {
        if (_bound is not { } tab)
        {
            ReplaceContextButtons([]);
            _contextKey = string.Empty;
            return;
        }

        var selected = tab.Selection.SelectedEntries;
        var selection = ContextActions.Classify(selected);
        var wanted = ContextActions.For(selection, _commands);
        var key = $"{selection}:{string.Join(',', wanted.Select(w => w.Command.Value))}";
        if (key != _contextKey)
        {
            _contextKey = key;
            ReplaceContextButtons(wanted);
        }

        foreach (var button in _contextButtons)
        {
            var model = _barButtons[button];
            button.IsEnabled = _commands.CanExecute(model.Command, ContextActions.ContextOf(model, selected, tab.History.Current));
        }
    }

    private void ReplaceContextButtons(IReadOnlyList<BarButton> wanted)
    {
        foreach (var old in _contextButtons)
        {
            Bar.PrimaryCommands.Remove(old);
            _barButtons.Remove(old);
        }

        _contextButtons.Clear();
        foreach (var model in wanted)
        {
            var button = new AppBarButton { Label = model.Title, Icon = new FontIcon { Glyph = model.Glyph } };
            ToolTipService.SetToolTip(button, model.Tooltip);
            AutomationProperties.SetName(button, model.Title);
            button.Click += OnBarButtonClick;
            _barButtons[button] = model;
            _contextButtons.Add(button);
            Bar.PrimaryCommands.Add(button);
        }
    }

    private void OnBarButtonClick(object sender, RoutedEventArgs e)
    {
        if (sender is not AppBarButton button || !_barButtons.TryGetValue(button, out var model) || _bound is not { } tab)
        {
            return;
        }

        if (model.Command == CommandIds.SelectByPattern)
        {
            AskForPattern(button);
            return;
        }

        if (_contextButtons.Contains(button))
        {
            var context = ContextActions.ContextOf(model, tab.Selection.SelectedEntries, tab.History.Current);
            Run(model.Command, context.Target, context.Parameter);
        }
        else
        {
            Run(model.Command);
        }
    }

    private void OnPasteClick(SplitButton sender, SplitButtonClickEventArgs args) => Run(CommandIds.EditPaste);

    /// <summary>"Select by pattern": asks for a wildcard such as <c>*.mkv</c> in a small flyout and selects what matches.</summary>
    private void AskForPattern(FrameworkElement anchor)
    {
        var box = new TextBox { PlaceholderText = "*.mkv", Width = 200, AcceptsReturn = false };
        var flyout = new Flyout { Content = new StackPanel { Spacing = 8, Children = { new TextBlock { Text = "Select items matching" }, box } } };
        box.KeyDown += (_, e) =>
        {
            if (e.Key == VirtualKey.Enter)
            {
                e.Handled = true;
                flyout.Hide();
                Run(CommandIds.SelectByPattern, box.Text);
            }
        };
        flyout.Opened += (_, _) => box.Focus(FocusState.Programmatic);
        flyout.ShowAt(anchor);
    }

    // New menu ---------------------------------------------------------------------------------------------------------

    private async void OnNewMenuOpening(object? sender, object e)
    {
        NewMenu.Items.Clear();
        var pending = App.Host.NewItems.TemplatesAsync();
        if (!pending.IsCompleted)
        {
            // The registry scan is still running (a cold start). The Folder item works without it.
            NewMenu.Items.Add(NewItemFor(NewItemService.FolderTemplate));
            NewMenu.Items.Add(new MenuFlyoutItem { Text = "Loading more…", IsEnabled = false });
        }

        IReadOnlyList<NewItemTemplate> templates;
        try
        {
            templates = await pending;
        }
        catch (OperationCanceledException)
        {
            return;
        }

        NewMenu.Items.Clear();
        NewItemSource? previous = null;
        foreach (var template in templates)
        {
            if (previous is { } source && source != template.Source)
            {
                NewMenu.Items.Add(new MenuFlyoutSeparator());
            }

            previous = template.Source;
            NewMenu.Items.Add(NewItemFor(template));
        }

        NewMenu.Items.Add(new MenuFlyoutSeparator());
        var manage = new MenuFlyoutItem { Text = "Manage templates…", Icon = new FontIcon { Glyph = "" } };
        manage.Click += OnManageTemplatesClick;
        NewMenu.Items.Add(manage);
    }

    private MenuFlyoutItem NewItemFor(NewItemTemplate template)
    {
        var item = new MenuFlyoutItem
        {
            Text = template.DisplayName,
            Icon = new FontIcon { Glyph = template.Content is FolderContent ? "" : "" },
            IsEnabled = _commands.CanExecute(CommandIds.NewItem, new CommandContext(template.Id)),
        };
        if (template.Content is FolderContent)
        {
            item.KeyboardAcceleratorTextOverride = "Ctrl+Shift+N";
        }

        item.Click += (_, _) => Run(CommandIds.NewItem, template.Id);
        return item;
    }

    private async void OnManageTemplatesClick(object sender, RoutedEventArgs e)
    {
        // Open the templates folder (created on first use) so the user can drop files in; the next menu reads it again.
        var folder = NewMenuOptions.ForThisMachine().UserTemplatesFolder;
        try
        {
            await Task.Run(() => Directory.CreateDirectory(folder));
            App.Host.NewItems.Refresh();
            Run(CommandIds.TabOpenInNewTab, folder, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MessageText.Text = $"The templates folder could not be opened: {ex.Message}";
        }
    }

    // View, Sort and Group menus ---------------------------------------------------------------------------------------

    private void OnViewMenuOpening(object? sender, object e)
    {
        ViewMenu.Items.Clear();
        if (_bound is not { } tab)
        {
            return;
        }

        foreach (var (layout, title, glyph, available) in new (ViewLayout, string, string, bool)[]
        {
            (ViewLayout.Details, "Details", "", true),
            (ViewLayout.List, "List", "", true),
            (ViewLayout.Tiles, "Tiles (Phase 3)", "", false),
            (ViewLayout.Thumbnails, "Thumbnails", "", true),
            (ViewLayout.Gallery, "Gallery (Phase 3)", "", false),
        })
        {
            var item = new RadioMenuFlyoutItem { Text = title, GroupName = "layout", IsChecked = tab.Layout == layout, IsEnabled = available, Icon = new FontIcon { Glyph = glyph } };
            item.Click += (_, _) => Run(CommandIds.ViewLayout, parameter: layout);
            ViewMenu.Items.Add(item);
        }

        var bigger = new MenuFlyoutItem { Text = "Larger thumbnails", Icon = new FontIcon { Glyph = "" }, IsEnabled = _commands.CanExecute(CommandIds.ViewZoom, new CommandContext(null, 1)) };
        bigger.Click += (_, _) => Run(CommandIds.ViewZoom, parameter: 1);
        var smaller = new MenuFlyoutItem { Text = "Smaller thumbnails", Icon = new FontIcon { Glyph = "" }, IsEnabled = _commands.CanExecute(CommandIds.ViewZoom, new CommandContext(null, -1)) };
        smaller.Click += (_, _) => Run(CommandIds.ViewZoom, parameter: -1);
        ViewMenu.Items.Add(new MenuFlyoutSeparator());
        ViewMenu.Items.Add(bigger);
        ViewMenu.Items.Add(smaller);
        ViewMenu.Items.Add(new MenuFlyoutSeparator());

        var hidden = new ToggleMenuFlyoutItem { Text = "Show hidden items", IsChecked = App.Host.ViewOptions.ShowHidden };
        hidden.Click += (_, _) => Run(CommandIds.ViewHidden);
        var preview = new ToggleMenuFlyoutItem { Text = "Preview pane", IsChecked = _viewModel.ShowPreview, KeyboardAcceleratorTextOverride = "Alt+P" };
        preview.Click += (_, _) => Run(CommandIds.PreviewPane);
        ViewMenu.Items.Add(hidden);
        ViewMenu.Items.Add(preview);
        ViewMenu.Items.Add(new MenuFlyoutItem { Text = "Split pane (Phase 2)", IsEnabled = false });
    }

    private void OnSortMenuOpening(object? sender, object e)
    {
        SortMenu.Items.Clear();
        if (_bound is not { } tab)
        {
            return;
        }

        var primary = tab.Items.Sort.Count > 0 ? tab.Items.Sort[0] : new SortKey("name");
        foreach (var column in FileColumns.All.Where(c => c.Id != FileColumns.Tags.Id))
        {
            var item = new RadioMenuFlyoutItem { Text = column.Title, GroupName = "sort", IsChecked = primary.Column.Equals(column.Id, StringComparison.OrdinalIgnoreCase) };
            item.Click += (_, _) => Run(CommandIds.SortBy, column.Id);
            SortMenu.Items.Add(item);
        }

        SortMenu.Items.Add(new MenuFlyoutSeparator());
        AddRadio(SortMenu, "Ascending", "direction", primary.Direction == SortDirection.Ascending, CommandIds.SortDirection, "asc");
        AddRadio(SortMenu, "Descending", "direction", primary.Direction == SortDirection.Descending, CommandIds.SortDirection, "desc");
        SortMenu.Items.Add(new MenuFlyoutSeparator());
        AddRadio(SortMenu, "Folders first", "folders", tab.Items.Folders == FolderPlacement.First, CommandIds.FolderPlacement, nameof(FolderPlacement.First));
        AddRadio(SortMenu, "Folders mixed with files", "folders", tab.Items.Folders == FolderPlacement.Mixed, CommandIds.FolderPlacement, nameof(FolderPlacement.Mixed));
        AddRadio(SortMenu, "Folders last", "folders", tab.Items.Folders == FolderPlacement.Last, CommandIds.FolderPlacement, nameof(FolderPlacement.Last));
    }

    private void OnGroupMenuOpening(object? sender, object e)
    {
        GroupMenu.Items.Clear();
        if (_bound is not { } tab)
        {
            return;
        }

        AddRadio(GroupMenu, "None", "group", tab.Items.GroupBy is null, CommandIds.GroupBy, string.Empty);
        foreach (var column in FileColumns.All.Where(c => c.Group is not null))
        {
            AddRadio(GroupMenu, column.Title, "group", string.Equals(tab.Items.GroupBy, column.Id, StringComparison.OrdinalIgnoreCase), CommandIds.GroupBy, column.Id);
        }
    }

    private void AddRadio(MenuFlyout menu, string text, string group, bool isChecked, CommandId command, string target)
    {
        var item = new RadioMenuFlyoutItem { Text = text, GroupName = group, IsChecked = isChecked };
        item.Click += (_, _) => Run(command, target);
        menu.Items.Add(item);
    }

    // Filter box -------------------------------------------------------------------------------------------------------

    private void OnFilterTextChanged(object sender, TextChangedEventArgs e)
    {
        if (_syncingFilter)
        {
            return;
        }

        // Filtering re-sorts the folder on the UI thread, so wait until typing pauses (spec §3.2).
        _filterTimer ??= CreateFilterTimer();
        _filterTimer.Stop();
        _filterTimer.Start();
    }

    private Microsoft.UI.Dispatching.DispatcherQueueTimer CreateFilterTimer()
    {
        var timer = DispatcherQueue.CreateTimer();
        timer.Interval = FilterDelay;
        timer.IsRepeating = false;
        timer.Tick += (_, _) => Run(CommandIds.FilterSet, FilterBox.Text);
        return timer;
    }

    private void OnFilterKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Escape)
        {
            e.Handled = true;
            FilterBox.Text = string.Empty;
            _filterTimer?.Stop();
            Run(CommandIds.FilterSet, string.Empty);
            FocusFiles();
        }
        else if (e.Key == VirtualKey.Enter || e.Key == VirtualKey.Down)
        {
            e.Handled = true;
            FocusFiles();
        }
    }

    /// <summary>Shows the bound tab's filter in the box (a tab keeps its own; navigating clears it).</summary>
    private void SyncFilterBox()
    {
        var text = _bound?.FilterText ?? string.Empty;
        if (FilterBox.Text == text)
        {
            return;
        }

        _syncingFilter = true;
        try
        {
            FilterBox.Text = text;
        }
        finally
        {
            _syncingFilter = false;
        }
    }

    /// <summary>True while a text box has the keyboard, so Ctrl+A and the clipboard keys edit text instead of selecting rows.</summary>
    private bool IsEditingText() => FocusManager.GetFocusedElement(Content.XamlRoot) is TextBox or PasswordBox or RichEditBox;
}
