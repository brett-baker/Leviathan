using System.ComponentModel;
using Leviathan.Core.Paths;
using Leviathan.Core.Platform;
using Leviathan.ViewModels.Folders;
using Leviathan.ViewModels.Navigation;
using Leviathan.ViewModels.Tabs;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using Windows.System;

namespace Leviathan.App;

/// <summary>What the address bar needs from the app: suggestions, the subfolder lists, the recent and favorite folders, and the cloud folders.</summary>
/// <param name="Completer">Autocomplete.</param>
/// <param name="Subfolders">The lists behind the chevrons.</param>
/// <param name="Recent">Recently visited folders, newest first.</param>
/// <param name="Favorites">Pinned folders.</param>
/// <param name="IsCloudFolder">True when a location is inside a cloud sync folder.</param>
internal sealed record AddressBarServices(
    AddressCompleter Completer,
    SubfolderMenu Subfolders,
    Func<IReadOnlyList<LocationPath>> Recent,
    Func<IReadOnlyList<LocationPath>> Favorites,
    Func<LocationPath, bool> IsCloudFolder);

/// <summary>A request to go somewhere from the breadcrumb row.</summary>
/// <param name="Location">The destination.</param>
/// <param name="NewTab">True for a middle-click or Ctrl+click: open it in a new tab instead.</param>
internal sealed record AddressNavigation(LocationPath Location, bool NewTab);

/// <summary>Reads which share a mapped drive letter points at (for "Copy as UNC path").</summary>
internal sealed class DriveMappings : IDriveMappings
{
    public string? TryGetUncRoot(string driveRoot) => Interop.WindowsDriveMappings.QueryUnc(driveRoot);
}

/// <summary>
/// The address bar (spec §5.2). Breadcrumb mode is the default: every segment is a button, and a chevron after it opens the
/// list of its subfolders with type-to-filter. Clicking empty space, Ctrl+L or Alt+D switches to an editor with
/// autocomplete from the folder being typed, recent folders and favorites; Enter goes there, Esc leaves. Segments that do not
/// fit collapse into a "…" menu, the right-click menu copies the path in four forms, and an icon shows a lock (access
/// denied), a warning (share not responding) or a cloud (inside a sync folder).
/// All state lives in <see cref="AddressBarViewModel"/>; this control only draws it.
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001:Types that own disposable fields should be disposable",
    Justification = "The cancellation sources are cancelled and replaced as the user types or the menus close; a UserControl cannot be IDisposable.")]
public sealed partial class AddressBar : UserControl
{
    private const double OverflowButtonWidth = 34;

    private AddressBarServices? _services;
    private TabViewModel? _tab;
    private AddressBarViewModel? _viewModel;
    private IReadOnlyList<AddressSuggestion> _suggestions = [];
    private CancellationTokenSource? _suggestionsCts;
    private bool _editing;

    /// <summary>Creates the control.</summary>
    public AddressBar() => InitializeComponent();

    /// <summary>Raised with the text the user typed when they press Enter.</summary>
    internal event EventHandler<string>? Submitted;

    /// <summary>Raised when a breadcrumb or subfolder is chosen.</summary>
    internal event EventHandler<AddressNavigation>? NavigationRequested;

    /// <summary>Raised when the editor closes (Enter, Esc or focus lost), so the host can move focus to the file list.</summary>
    internal event EventHandler? EditEnded;

    internal void Attach(AddressBarServices services)
    {
        _services = services;
        Rebuild();
    }

    /// <summary>Shows the address of <paramref name="tab"/>.</summary>
    internal void Bind(TabViewModel? tab)
    {
        if (_tab is not null)
        {
            _tab.Address.PropertyChanged -= OnAddressChanged;
            _tab.Folder.PropertyChanged -= OnStatusChanged;
            _tab.PropertyChanged -= OnStatusChanged;
        }

        _tab = tab;
        _viewModel = tab?.Address;
        if (tab is not null)
        {
            tab.Address.PropertyChanged += OnAddressChanged;
            tab.Folder.PropertyChanged += OnStatusChanged;
            tab.PropertyChanged += OnStatusChanged;
        }

        if (_editing)
        {
            EndEdit();
        }

        Rebuild();
    }

    /// <summary>Re-evaluates the status icon, e.g. after the cloud folders have been detected.</summary>
    internal void RefreshStatus() => DispatcherQueue.TryEnqueue(Rebuild);

    /// <summary>Switches to the editor (Ctrl+L, Alt+D, a click on empty space) with the path selected, or with <paramref name="typed"/> as the start of a new address.</summary>
    internal void BeginEdit(string? typed = null)
    {
        if (_viewModel is null)
        {
            return;
        }

        _viewModel.BeginEdit(typed);
        _editing = true;
        Editor.Text = _viewModel.EditText;
        CrumbPanel.Visibility = Visibility.Collapsed;
        Editor.Visibility = Visibility.Visible;

        // Focus is refused for an element that has not been laid out yet, so lay it out first and retry once if it still fails.
        Editor.UpdateLayout();
        if (!FocusEditor(typed))
        {
            DispatcherQueue.TryEnqueue(() => FocusEditor(typed));
        }
    }

    private bool FocusEditor(string? typed)
    {
        var focused = Editor.Focus(FocusState.Programmatic);
        if (FindDescendant<TextBox>(Editor) is { } box)
        {
            if (typed is null)
            {
                box.SelectAll();
            }
            else
            {
                box.Select(box.Text.Length, 0);
            }
        }

        return focused;
    }

    private void EndEdit(bool restoreFocus = false)
    {
        if (!_editing)
        {
            return;
        }

        _editing = false;
        _suggestionsCts?.Cancel();
        _viewModel?.CancelEdit();
        Editor.Visibility = Visibility.Collapsed;
        CrumbPanel.Visibility = Visibility.Visible;
        if (restoreFocus)
        {
            EditEnded?.Invoke(this, EventArgs.Empty);
        }
    }

    // Breadcrumb row ---------------------------------------------------------------------------------------------------

    private void OnAddressChanged(object? sender, PropertyChangedEventArgs e) => DispatcherQueue.TryEnqueue(Rebuild);

    private void OnStatusChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(FolderViewModel.State) or nameof(TabViewModel.IsOffline))
        {
            DispatcherQueue.TryEnqueue(Rebuild);
        }
    }

    private void OnFrameSizeChanged(object sender, SizeChangedEventArgs e) => Rebuild();

    private void Rebuild()
    {
        CrumbPanel.Children.Clear();
        if (_viewModel is null)
        {
            return;
        }

        var crumbs = _viewModel.Breadcrumbs;
        var status = BuildStatusIcon();
        var statusWidth = status is null ? 0 : 26;

        // One button for the segment and one for the chevron after it.
        var pairs = crumbs.Select(c => (Crumb: c, Label: CrumbButton(c), Chevron: ChevronButton(c))).ToList();
        double Width(Button button)
        {
            button.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            return button.DesiredSize.Width;
        }

        var available = Frame.ActualWidth <= 0 ? double.PositiveInfinity : Frame.ActualWidth - 12 - statusWidth;
        var first = 0;
        var used = Width(pairs[^1].Label) + Width(pairs[^1].Chevron);
        for (var i = pairs.Count - 2; i >= 0; i--)
        {
            var next = used + Width(pairs[i].Label) + Width(pairs[i].Chevron);
            var needsOverflow = i > 0;
            if (next + (needsOverflow ? OverflowButtonWidth : 0) > available)
            {
                first = i + 1;
                break;
            }

            used = next;
        }

        if (status is not null)
        {
            CrumbPanel.Children.Add(status);
        }

        if (first > 0)
        {
            CrumbPanel.Children.Add(OverflowButton(crumbs.Take(first).ToList()));
        }

        for (var i = first; i < pairs.Count; i++)
        {
            if (i == pairs.Count - 1)
            {
                // The current folder always shows, trimmed with an ellipsis if even it does not fit.
                pairs[i].Label.MaxWidth = Math.Max(60, available - OverflowButtonWidth - 34);
            }

            CrumbPanel.Children.Add(pairs[i].Label);
            CrumbPanel.Children.Add(pairs[i].Chevron);
        }
    }

    private FontIcon? BuildStatusIcon()
    {
        if (_tab is null)
        {
            return null;
        }

        (string Glyph, string Tip)? icon = _tab.Folder.State switch
        {
            FolderLoadState.AccessDenied => ("", "Access denied: this folder needs other permissions"),
            _ when _tab.IsOffline => ("", "This location is not responding"),
            _ when _services?.IsCloudFolder(_tab.History.Current) == true => ("", "Cloud folder (kept in sync by its sync client)"),
            _ => null,
        };

        if (icon is not { } shown)
        {
            return null;
        }

        var glyph = new FontIcon { Glyph = shown.Glyph, FontSize = 14, Margin = new Thickness(6, 0, 4, 0), VerticalAlignment = VerticalAlignment.Center };
        ToolTipService.SetToolTip(glyph, shown.Tip);
        return glyph;
    }

    private Button CrumbButton(Breadcrumb crumb)
    {
        var button = SubtleButton(new TextBlock { Text = crumb.Label.TrimEnd('\\'), TextTrimming = TextTrimming.CharacterEllipsis }, new Thickness(8, 0, 8, 0));
        ToolTipService.SetToolTip(button, crumb.Location.Value);
        button.Click += (_, _) => NavigationRequested?.Invoke(this, new AddressNavigation(crumb.Location, KeyTranslator.IsDown(VirtualKey.Control)));
        button.PointerPressed += (_, e) =>
        {
            if (e.GetCurrentPoint(button).Properties.IsMiddleButtonPressed)
            {
                e.Handled = true;
                NavigationRequested?.Invoke(this, new AddressNavigation(crumb.Location, NewTab: true));
            }
        };
        return button;
    }

    private Button ChevronButton(Breadcrumb crumb)
    {
        var button = SubtleButton(new FontIcon { Glyph = "", FontSize = 9 }, new Thickness(5, 0, 5, 0));
        ToolTipService.SetToolTip(button, "Folders in " + crumb.Label.TrimEnd('\\'));
        AutomationProperties.SetName(button, "Folders in " + crumb.Label.TrimEnd('\\'));
        button.Click += (_, _) => ShowSubfolders(crumb.Location, button);
        return button;
    }

    private static Button SubtleButton(object content, Thickness padding) => new()
    {
        Content = content,
        Padding = padding,
        MinWidth = 0,
        MinHeight = 26,
        Height = 26,
        BorderThickness = new Thickness(0),
        Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
        VerticalAlignment = VerticalAlignment.Center,
    };

    private Button OverflowButton(IReadOnlyList<Breadcrumb> hidden)
    {
        var button = SubtleButton(new FontIcon { Glyph = "", FontSize = 12 }, new Thickness(8, 0, 8, 0));
        ToolTipService.SetToolTip(button, "Earlier folders");
        AutomationProperties.SetName(button, "Earlier folders");
        var menu = new MenuFlyout { Placement = Microsoft.UI.Xaml.Controls.Primitives.FlyoutPlacementMode.BottomEdgeAlignedLeft };
        foreach (var crumb in hidden)
        {
            var item = new MenuFlyoutItem { Text = crumb.Label.TrimEnd('\\'), Icon = new FontIcon { Glyph = "" } };
            item.Click += (_, _) => NavigationRequested?.Invoke(this, new AddressNavigation(crumb.Location, NewTab: false));
            menu.Items.Add(item);
        }

        button.Flyout = menu;
        return button;
    }

    // Subfolder list behind a chevron ---------------------------------------------------------------------------------

    private void ShowSubfolders(LocationPath location, FrameworkElement anchor)
    {
        if (_services is null)
        {
            return;
        }

        var cts = new CancellationTokenSource();
        var filter = new TextBox { PlaceholderText = "Filter folders", MinWidth = 260 };
        AutomationProperties.SetName(filter, "Filter folders");
        var status = new TextBlock { Text = "Loading…", Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"], Margin = new Thickness(4) };
        var list = new ListView
        {
            ItemTemplate = (DataTemplate)Resources["FolderItemTemplate"],
            IsItemClickEnabled = true,
            SelectionMode = ListViewSelectionMode.None,
            MaxHeight = 380,
            MinWidth = 260,
        };
        var panel = new StackPanel { Spacing = 6, Children = { filter, status, list } };
        var flyout = new Flyout { Content = panel, Placement = Microsoft.UI.Xaml.Controls.Primitives.FlyoutPlacementMode.BottomEdgeAlignedLeft };
        IReadOnlyList<LocationPath> all = [];

        void Show()
        {
            var shown = SubfolderMenu.Filter(all, filter.Text);
            list.ItemsSource = shown;
            status.Visibility = shown.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            status.Text = all.Count == 0 ? "No folders here" : "No folders match";
        }

        void Choose(LocationPath chosen)
        {
            flyout.Hide();
            NavigationRequested?.Invoke(this, new AddressNavigation(chosen, KeyTranslator.IsDown(VirtualKey.Control)));
        }

        filter.TextChanged += (_, _) => Show();
        filter.KeyDown += (_, e) =>
        {
            if (e.Key == VirtualKey.Enter && list.ItemsSource is IReadOnlyList<LocationPath> { Count: > 0 } shown)
            {
                e.Handled = true;
                Choose(shown[0]);
            }
        };
        list.ItemClick += (_, e) =>
        {
            if (e.ClickedItem is LocationPath chosen)
            {
                Choose(chosen);
            }
        };
        flyout.Opened += (_, _) => filter.Focus(FocusState.Programmatic);
        flyout.Closed += (_, _) => cts.Cancel();
        flyout.ShowAt(anchor);

        _ = LoadAsync();

        async Task LoadAsync()
        {
            try
            {
                all = await _services.Subfolders.LoadAsync(location, cts.Token);
                Show();
            }
            catch (OperationCanceledException)
            {
                // The list was closed before it finished loading.
            }
        }
    }

    // Editor -----------------------------------------------------------------------------------------------------------

    private void OnEditorTextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (args.Reason == AutoSuggestionBoxTextChangeReason.UserInput)
        {
            _ = RefreshSuggestionsAsync(sender.Text);
        }
    }

    private async Task RefreshSuggestionsAsync(string text)
    {
        if (_services is null)
        {
            return;
        }

        _suggestionsCts?.Cancel();
        var cts = _suggestionsCts = new CancellationTokenSource();
        try
        {
            // A short pause so typing fast does not list a folder per keystroke.
            await Task.Delay(60, cts.Token);
            var suggestions = await _services.Completer.SuggestAsync(text, _tab?.History.Current, _services.Recent(), _services.Favorites(), cts.Token);
            if (!cts.IsCancellationRequested)
            {
                _suggestions = suggestions;
                Editor.ItemsSource = suggestions.Select(x => x.Text).ToList();
            }
        }
        catch (OperationCanceledException)
        {
            // Superseded by newer typing.
        }
    }

    private void OnEditorQuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
    {
        var text = args.ChosenSuggestion as string ?? args.QueryText;
        EndEdit(restoreFocus: true);
        Submitted?.Invoke(this, text);
    }

    private void OnEditorPreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Escape)
        {
            e.Handled = true;
            EndEdit(restoreFocus: true);
        }
        else if (e.Key == VirtualKey.Tab && !KeyTranslator.IsDown(VirtualKey.Control) && _suggestions.Count > 0)
        {
            // Tab accepts: the longest common start of the suggestions, or the first one when that adds nothing.
            e.Handled = true;
            var common = AddressCompleter.CommonPrefix(_suggestions);
            var text = common.Length > Editor.Text.Length ? common : _suggestions[0].Text;
            Editor.Text = text;
            if (FindDescendant<TextBox>(Editor) is { } box)
            {
                box.Select(text.Length, 0);
            }

            // Typing a folder name and pressing Tab continues into it: list what is below.
            _ = RefreshSuggestionsAsync(text);
        }
    }

    private void OnEditorLostFocus(object sender, RoutedEventArgs e)
    {
        // Choosing a suggestion moves focus into the suggestion list, which is still part of the editor.
        DispatcherQueue.TryEnqueue(() =>
        {
            if (_editing && !IsInsideEditor(FocusManager.GetFocusedElement(XamlRoot) as DependencyObject))
            {
                EndEdit();
            }
        });
    }

    private bool IsInsideEditor(DependencyObject? element)
    {
        for (var current = element; current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (ReferenceEquals(current, Editor))
            {
                return true;
            }
        }

        // The suggestion list is a popup outside the editor's own tree.
        return element is not null && element.GetType().Name is "ListViewItem" or "ListView" or "PopupRoot";
    }

    // Clicks on the bar itself -------------------------------------------------------------------------------------------

    private void OnFrameTapped(object sender, TappedRoutedEventArgs e)
    {
        if (_editing || IsOnButton(e.OriginalSource as DependencyObject))
        {
            return;
        }

        BeginEdit();
    }

    private bool IsOnButton(DependencyObject? element)
    {
        for (var current = element; current is not null && !ReferenceEquals(current, Frame); current = VisualTreeHelper.GetParent(current))
        {
            if (current is Button)
            {
                return true;
            }
        }

        return false;
    }

    private void OnFrameRightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (_viewModel is null || _editing)
        {
            return;
        }

        e.Handled = true;
        var menu = new MenuFlyout();
        void Copy(string text, CopyAsFormat format)
        {
            var item = new MenuFlyoutItem { Text = text };
            item.Click += async (_, _) => await CopyAsync(format);
            menu.Items.Add(item);
        }

        Copy("Copy path", CopyAsFormat.Path);
        Copy("Copy as UNC path", CopyAsFormat.Unc);
        Copy("Copy path in quotes", CopyAsFormat.Quoted);
        Copy("Copy as file:// URI", CopyAsFormat.FileUri);
        menu.Items.Add(new MenuFlyoutSeparator());
        var edit = new MenuFlyoutItem { Text = "Edit address" };
        edit.Click += (_, _) => BeginEdit();
        menu.Items.Add(edit);
        menu.ShowAt(Frame, e.GetPosition(Frame));
    }

    /// <summary>Copies the current location in the chosen form. The UNC form asks Windows which share a drive letter maps to, which can wait on a dead server, so it runs off the UI thread.</summary>
    private async Task CopyAsync(CopyAsFormat format)
    {
        if (_viewModel is null)
        {
            return;
        }

        var model = _viewModel;
        var text = await Task.Run(() => model.CopyAs(format, new DriveMappings()));
        var package = new DataPackage();
        package.SetText(text);
        Clipboard.SetContent(package);
    }

    private static T? FindDescendant<T>(DependencyObject parent)
        where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T match)
            {
                return match;
            }

            if (FindDescendant<T>(child) is { } found)
            {
                return found;
            }
        }

        return null;
    }
}
