using System.ComponentModel;
using System.Diagnostics;
using Leviathan.Data;
using Leviathan.ViewModels.Folders;
using Leviathan.ViewModels.Settings;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.System;

namespace Leviathan.App;

/// <summary>Static helpers the settings templates bind to.</summary>
internal static class SettingsFormat
{
    /// <summary>"Files and folders", or "Network (arrives in Phase 2)" for a page that is not built yet.</summary>
    public static string Keys(string keys) => keys.Length == 0 ? "none" : keys;

    public static string Changed(bool custom) => custom ? "changed" : string.Empty;

    public static string Where(string pageId)
    {
        var page = SettingsCatalog.Page(pageId);
        return page.IsAvailable ? page.Title : $"{page.Title} (arrives in {page.Arrives})";
    }
}

/// <summary>
/// The settings sheet (spec §17.2, D36): the page list on the left, the page on the right, search across all settings on top.
/// Every control saves through the settings store the moment it changes, and the app applies it at once (<see cref="AppHost.ApplySettings"/>).
/// Pages that are not built yet say what they will hold. This control only draws the model and reports what the user did.
/// </summary>
internal sealed partial class SettingsSheet : UserControl
{
    private readonly SettingsSheetModel _model;
    private bool _ready;
    private bool _loading;

    public SettingsSheet(SettingsSheetModel model)
    {
        _model = model;
        InitializeComponent();
        _ready = true;
        PageList.ItemsSource = SettingsCatalog.Pages;
        Icons.Bind(ExpandButton, static (b, i) => b.Content = i, "layout-gallery");
        Icons.Bind(CloseButton, static (b, i) => b.Content = i, "close");
        _model.PropertyChanged += OnModelChanged;
        App.Host.Settings.Changed += OnSettingsChanged;
        Sync();
    }

    /// <summary>Gives the Keyboard page its rows (the window knows the commands).</summary>
    internal Func<IReadOnlyList<Leviathan.ViewModels.Commands.KeyboardRow>> KeyRows { get; set; } = () => [];

    /// <summary>The user asked for the diagnostics window.</summary>
    internal event EventHandler? DiagnosticsRequested;

    /// <summary>Called when the sheet is removed for good.</summary>
    internal void Detach()
    {
        _model.PropertyChanged -= OnModelChanged;
        App.Host.Settings.Changed -= OnSettingsChanged;
    }

    /// <summary>Puts the keyboard in the search box.</summary>
    internal void FocusSearch() => SearchBox.Focus(FocusState.Programmatic);

    private void OnModelChanged(object? sender, PropertyChangedEventArgs e) => Sync();

    private void OnSettingsChanged(object? sender, AppSettings settings) => DispatcherQueue.TryEnqueue(Load);


    /// <summary>How many pages <see cref="WarmPage"/> knows.</summary>
    public const int WarmPageCount = 7;

    /// <summary>
    /// Lays out one page once, while the sheet is invisible, so opening settings and visiting a page are not stalls (spec §3.2). The
    /// window calls it one page at a time while idle, with the sheet closed.
    /// </summary>
    public void WarmPage(int index)
    {
        if (_model.IsOpen || index is < 0 or >= WarmPageCount)
        {
            return;
        }

        UIElement[] pages = [GeneralPage, AppearancePage, FilesPage, TabsPage, AdvancedPage, KeyboardPage, PlannedPage];
        var page = pages[index];
        var before = page.Visibility;
        try
        {
            page.Visibility = Visibility.Visible;
            UpdateLayout();
        }
        finally
        {
            page.Visibility = before;
        }
    }

    private void Sync()
    {
        if (!_ready)
        {
            return;
        }

        _loading = true;
        try
        {
            if (SearchBox.Text != _model.SearchText)
            {
                SearchBox.Text = _model.SearchText;
            }

            var page = SettingsCatalog.Page(_model.PageId);
            PageList.SelectedItem = page;
            var searching = _model.IsSearching;
            var results = _model.Results;
            ResultList.ItemsSource = results;
            ResultList.Visibility = searching && results.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            NoResults.Visibility = searching && results.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            PageScroll.Visibility = searching ? Visibility.Collapsed : Visibility.Visible;
            GeneralPage.Visibility = Show("general");
            AppearancePage.Visibility = Show("appearance");
            FilesPage.Visibility = Show("files");
            TabsPage.Visibility = Show("tabs");
            AdvancedPage.Visibility = Show("advanced");
            KeyboardPage.Visibility = Show("keyboard");
            var planned = !page.IsAvailable;
            PlannedPage.Visibility = planned ? Visibility.Visible : Visibility.Collapsed;
            if (planned)
            {
                PlannedTitle.Text = page.Title;
                PlannedWhen.Text = $"This page arrives in {page.Arrives}. It will hold:";
                PlannedList.ItemsSource = page.Planned!.Select(item => new TextBlock { Text = "•  " + item, Margin = new Thickness(8, 0, 0, 2) }).ToList();
            }

            ExpandButton.SetValue(ToolTipService.ToolTipProperty, _model.IsExpanded ? "Back to the lower part of the window" : "Fill the window");
        }
        finally
        {
            _loading = false;
        }

        Load();

        Visibility Show(string id) => !_model.IsSearching && _model.PageId == id ? Visibility.Visible : Visibility.Collapsed;
    }

    // Reads the settings into the controls. Controls raise their change events while they are filled; _loading keeps those from being saved back.
    private void Load()
    {
        if (!_ready)
        {
            return;
        }

        var settings = App.Host.Settings.Current;
        _loading = true;
        try
        {
            StartupBox.SelectedIndex = (int)settings.General.Startup;
            ProfileBox.Visibility = settings.General.Startup == StartupMode.Profile ? Visibility.Visible : Visibility.Collapsed;
            ProfileBox.ItemsSource = App.Host.Profiles.Catalog.Profiles.Select(p => p.Name).ToList();
            ProfileBox.SelectedItem = settings.General.StartupProfile;
            if (!NewTabBox.FocusState.HasFlag(FocusState.Keyboard) && NewTabBox.Text != settings.General.NewTabLocation)
            {
                NewTabBox.Text = settings.General.NewTabLocation;
            }

            HiddenSwitch.IsOn = settings.Files.ShowHidden;
            SystemSwitch.IsOn = settings.Files.ShowSystem;
            SizesSwitch.IsOn = settings.Files.ShowFolderSizes;
            OwnTrashSwitch.IsOn = settings.Files.KeepOwnTrashOnShares;
            RecheckBox.Text = string.Join('\r', settings.Files.RecheckSizePaths);
            BinDaysBox.Value = settings.Files.BinKeepDays;
            BinSizeBox.Value = settings.Files.BinMaxGiB;
            LastTabSwitch.IsOn = settings.Tabs.LastTabResetsToHome;
            FillIconPacks(settings.Appearance.IconPack);
            ThemeButtons.SelectedIndex = (int)settings.Appearance.Theme;
            DensityButtons.SelectedIndex = settings.Appearance.Density == Density.Compact ? 1 : 0;
            MicaSwitch.IsOn = settings.Appearance.Mica;
            IconSizeButtons.SelectedIndex = Math.Max(0, Leviathan.ViewModels.Settings.AppearanceMetrics.IconSizes.ToList().IndexOf(settings.Appearance.IconSize));
            FillAccents(settings.Appearance.Accent);
            if (!AccentBox.FocusState.HasFlag(FocusState.Keyboard))
            {
                AccentBox.Text = settings.Appearance.Accent;
            }
            SettingsPathText.Text = App.Host.SettingsPath;
        }
        finally
        {
            _loading = false;
        }

        if (_model.IsOpen && _model.PageId == "advanced")
        {
            _ = ShowCacheSizesAsync();
        }

        if (_model.IsOpen && _model.PageId == "keyboard")
        {
            LoadKeyboard();
        }
    }

    private void FillIconPacks(string current)
    {
        var chosen = App.Host.Icons.CurrentId;
        IconPackList.Children.Clear();
        foreach (var pack in App.Host.Icons.Packs)
        {
            var id = pack.Id;
            var button = new RadioButton
            {
                GroupName = "iconpack",
                IsChecked = string.Equals(id, chosen, StringComparison.OrdinalIgnoreCase),
                Content = new StackPanel
                {
                    Children =
                    {
                        new TextBlock { Text = pack.Name },
                        new TextBlock { Text = $"{pack.Manifest.Description} Licence: {pack.Manifest.License}.", TextWrapping = TextWrapping.Wrap, Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"], Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"] },
                    },
                },
            };
            button.Click += async (_, _) => await SaveAsync(s => s with { Appearance = s.Appearance with { IconPack = id } });
            IconPackList.Children.Add(button);
        }

        _ = current;
    }

    // The accent swatches: "Windows" first (no colour of its own), then the presets; the one in use is ringed.
    private void FillAccents(string current)
    {
        AccentList.Children.Clear();
        AccentList.Children.Add(Swatch("Windows accent", string.Empty, current.Length == 0));
        foreach (var (name, hex) in AccentPalette.Presets)
        {
            AccentList.Children.Add(Swatch(name, hex, string.Equals(hex, current, StringComparison.OrdinalIgnoreCase)));
        }
    }

    private static Button Swatch(string name, string hex, bool chosen)
    {
        var fill = AccentPalette.From(hex) is { } palette
            ? Windows.UI.Color.FromArgb(255, palette.Accent.R, palette.Accent.G, palette.Accent.B)
            : (Windows.UI.Color)Application.Current.Resources["SystemAccentColor"];
        var button = new Button
        {
            Width = 32,
            Height = 32,
            Padding = new Thickness(0),
            Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(fill),
            BorderBrush = new Microsoft.UI.Xaml.Media.SolidColorBrush(chosen ? Windows.UI.Color.FromArgb(255, 128, 128, 128) : Microsoft.UI.Colors.Transparent),
            BorderThickness = new Thickness(chosen ? 3 : 1),
            CornerRadius = new CornerRadius(16),
            Content = chosen ? new FontIcon { Glyph = "\uE73E", FontSize = 14, Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.White) } : null,
        };
        ToolTipService.SetToolTip(button, name);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, name);
        button.Click += async (_, _) => await SaveAsync(s => s with { Appearance = s.Appearance with { Accent = hex } });
        return button;
    }

    private async void OnLookChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready || _loading || ThemeButtons.SelectedIndex < 0 || DensityButtons.SelectedIndex < 0 || IconSizeButtons.SelectedIndex < 0)
        {
            return;
        }

        var theme = (ThemeMode)ThemeButtons.SelectedIndex;
        var density = DensityButtons.SelectedIndex == 1 ? Density.Compact : Density.Comfortable;
        var size = AppearanceMetrics.IconSizes[IconSizeButtons.SelectedIndex];
        await SaveAsync(s => s with { Appearance = s.Appearance with { Theme = theme, Density = density, IconSize = size } });
    }

    private async void OnLookToggled(object sender, RoutedEventArgs e)
    {
        if (!_ready || _loading)
        {
            return;
        }

        var mica = MicaSwitch.IsOn;
        await SaveAsync(s => s with { Appearance = s.Appearance with { Mica = mica } });
    }

    private void OnAccentKeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter)
        {
            e.Handled = true;
            OnAccentCommit(sender, e);
        }
    }

    private async void OnAccentCommit(object sender, RoutedEventArgs e)
    {
        if (!_ready || _loading)
        {
            return;
        }

        var text = AccentBox.Text.Trim();
        if (text.Length > 0 && AccentPalette.From(text) is null)
        {
            AccentNote.Text = $"'{text}' is not a colour. Write it like #0F6CBD.";
            return;
        }

        AccentNote.Text = "Pick one, or type a colour. The first swatch follows Windows. Windows opened from now on use it; this one has it from the next start.";
        await SaveAsync(s => s with { Appearance = s.Appearance with { Accent = text.Length == 0 ? string.Empty : AccentPalette.From(text)!.Accent.Hex } });
    }

    private static async Task SaveAsync(Func<AppSettings, AppSettings> change)
    {
        try
        {
            await App.Host.Settings.UpdateAsync(change, CancellationToken.None);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The change still applies for this run; the file could not be written.
        }
    }

    // ---- General

    private async void OnStartupChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready || _loading || StartupBox.SelectedIndex < 0)
        {
            return;
        }

        var mode = (StartupMode)StartupBox.SelectedIndex;
        var profile = App.Host.Settings.Current.General.StartupProfile ?? (App.Host.Profiles.Catalog.Profiles.Count > 0 ? App.Host.Profiles.Catalog.Profiles[0].Name : null);
        if (mode == StartupMode.Profile && profile is null)
        {
            // Nothing to open yet: stay on Last session and say how to make a profile.
            DispatcherQueue.TryEnqueue(() =>
            {
                _loading = true;
                StartupBox.SelectedIndex = (int)StartupMode.LastSession;
                _loading = false;
            });
            return;
        }

        await SaveAsync(s => s with { General = s.General with { Startup = mode, StartupProfile = profile } });
    }

    private async void OnProfileChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready || _loading || ProfileBox.SelectedItem is not string name)
        {
            return;
        }

        await SaveAsync(s => s with { General = s.General with { Startup = StartupMode.Profile, StartupProfile = name } });
    }

    private void OnNewTabKeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter)
        {
            e.Handled = true;
            OnNewTabCommit(sender, e);
        }
    }

    private async void OnNewTabCommit(object sender, RoutedEventArgs e)
    {
        if (!_ready || _loading)
        {
            return;
        }

        var text = NewTabBox.Text.Trim();
        if (text.Length > 0 && !Leviathan.Core.Paths.LocationPath.TryParse(text, out _))
        {
            NewTabNote.Text = $"'{text}' is not a folder address. Use something like C:\\Projects or \\\\nas\\media.";
            return;
        }

        NewTabNote.Text = "A folder such as C:\\Projects or \\\\nas\\media. Empty means your user folder.";
        await SaveAsync(s => s with { General = s.General with { NewTabLocation = text } });
    }

    // ---- Files and folders, tabs

    private async void OnFilesToggled(object sender, RoutedEventArgs e)
    {
        if (!_ready || _loading)
        {
            return;
        }

        var hidden = HiddenSwitch.IsOn;
        var system = SystemSwitch.IsOn;
        var sizes = SizesSwitch.IsOn;
        var ownTrash = OwnTrashSwitch.IsOn;
        await SaveAsync(s => s with { Files = s.Files with { ShowHidden = hidden, ShowSystem = system, ShowFolderSizes = sizes, KeepOwnTrashOnShares = ownTrash } });
    }

    // Saved when the box is left; lines that are not drive letter paths are kept as typed but never counted.
    private async void OnRecheckLostFocus(object sender, RoutedEventArgs e)
    {
        if (!_ready || _loading)
        {
            return;
        }

        var lines = RecheckBox.Text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        await SaveAsync(s => s with { Files = s.Files with { RecheckSizePaths = lines } });
    }

    // The bin's limits apply at the next daily purge or "Delete old now"; a box cleared to nothing reads as NaN and is left as it was.
    private async void OnBinLimitChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (!_ready || _loading || double.IsNaN(sender.Value))
        {
            return;
        }

        var days = double.IsNaN(BinDaysBox.Value) ? 30 : (int)BinDaysBox.Value;
        var gib = double.IsNaN(BinSizeBox.Value) ? 100 : (int)BinSizeBox.Value;
        await SaveAsync(s => s with { Files = s.Files with { BinKeepDays = days, BinMaxGiB = gib } });
    }

    private async void OnTabsToggled(object sender, RoutedEventArgs e)
    {
        if (!_ready || _loading)
        {
            return;
        }

        var on = LastTabSwitch.IsOn;
        await SaveAsync(s => s with { Tabs = s.Tabs with { LastTabResetsToHome = on } });
    }

    // ---- Advanced

    private void OnShowSettingsFileClick(object sender, RoutedEventArgs e)
    {
        try
        {
            using var explorer = Process.Start(new ProcessStartInfo("explorer.exe") { ArgumentList = { "/select,", App.Host.SettingsPath }, UseShellExecute = false });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            // No Explorer to show it in; the path is on the page.
        }
    }

    private void OnDiagnosticsClick(object sender, RoutedEventArgs e) => DiagnosticsRequested?.Invoke(this, EventArgs.Empty);

    private async void OnClearListingsClick(object sender, RoutedEventArgs e)
    {
        App.Host.Listings.ClearMemory();
        if (App.Host.Listings.Store is SqliteListingStore store)
        {
            try
            {
                await store.ClearAsync();
            }
            catch (IOException)
            {
                // The saved copy stays; it is checked on every visit anyway.
            }
        }

        await ShowCacheSizesAsync();
    }

    private async void OnClearThumbnailsClick(object sender, RoutedEventArgs e)
    {
        App.Host.Thumbnails.Clear();
        await ShowCacheSizesAsync();
    }

    private async void OnClearActivityClick(object sender, RoutedEventArgs e)
    {
        await App.Host.Activity.ClearAsync();
        await ShowCacheSizesAsync();
    }

    private async Task ShowCacheSizesAsync()
    {
        var text = $"Remembered folders: {App.Host.Listings.FolderCount:N0} in memory";
        if (App.Host.Listings.Store is SqliteListingStore store)
        {
            try
            {
                var (folders, bytes) = await store.UsageAsync();
                text += $", {folders:N0} saved ({DisplayFormat.Size(bytes)})";
            }
            catch (IOException)
            {
                text += ", the saved copy could not be read";
            }
        }

        if (App.Host.Thumbnails is ThumbnailCache thumbnails)
        {
            text += $". Thumbnails: {thumbnails.Stats.Cached:N0} ({DisplayFormat.Size(thumbnails.Stats.Bytes)}).";
        }

        CacheText.Text = text;
    }

    // ---- Keyboard

    private void LoadKeyboard()
    {
        var filter = KeyFilterBox.Text.Trim();
        var rows = KeyRows()
            .Where(r => filter.Length == 0
                || r.Title.Contains(filter, StringComparison.OrdinalIgnoreCase)
                || r.Category.Contains(filter, StringComparison.OrdinalIgnoreCase)
                || r.Keys.Contains(filter, StringComparison.OrdinalIgnoreCase)
                || r.Command.Value.Contains(filter, StringComparison.OrdinalIgnoreCase))
            .ToList();
        KeyList.ItemsSource = rows;
        KeyProblemsText.Text = string.Join(Environment.NewLine, App.Host.KeyProblems);
        KeyProblemsText.Visibility = App.Host.KeyProblems.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        KeyNotesText.Text = string.Join(Environment.NewLine, App.Host.KeyNotes);
        KeyNotesText.Visibility = App.Host.KeyNotes.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private void OnKeyFilterChanged(object sender, TextChangedEventArgs e)
    {
        if (_ready)
        {
            LoadKeyboard();
        }
    }

    private async void OnReloadFileClick(object sender, RoutedEventArgs e)
    {
        try
        {
            await App.Host.Settings.ReloadAsync(CancellationToken.None);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            KeyProblemsText.Text = "The settings file could not be read: " + ex.Message;
            KeyProblemsText.Visibility = Visibility.Visible;
        }
    }

    // ---- sheet chrome

    private void OnSearchChanged(object sender, TextChangedEventArgs e)
    {
        if (_ready && !_loading && _model.SearchText != SearchBox.Text)
        {
            _model.SearchText = SearchBox.Text;
        }
    }

    private void OnPageSelected(object sender, SelectionChangedEventArgs e)
    {
        if (_ready && !_loading && PageList.SelectedItem is SettingsPageInfo page && page.Id != _model.PageId)
        {
            _model.ShowPage(page.Id);
        }
    }

    private void OnResultClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is SettingEntry entry)
        {
            _model.ShowPage(entry.PageId);
        }
    }

    private void OnExpandClick(object sender, RoutedEventArgs e) => _model.IsExpanded = !_model.IsExpanded;

    private void OnCloseClick(object sender, RoutedEventArgs e) => _model.Close();
}
