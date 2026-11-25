using System.ComponentModel;
using System.Diagnostics;
using Leviathan.Data;
using Leviathan.ViewModels.Commands;
using Leviathan.ViewModels.Triage;
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

    public static Visibility Show(bool value) => value ? Visibility.Visible : Visibility.Collapsed;

    public static Visibility Hide(bool value) => value ? Visibility.Collapsed : Visibility.Visible;

    public static string Where(string pageId)
    {
        var page = SettingsCatalog.Page(pageId);
        return page.IsAvailable ? page.Title : $"{page.Title} (arrives in {page.Arrives})";
    }
}

/// <summary>
/// A row of the Keyboard page: the command, its key caps, where it works, and the group name when it is the first of its group.
/// </summary>
internal sealed record KeyRowView(KeyboardRow Row, string? Header)
{
    public bool HasHeader => Header is not null;

    public string Scope => KeyScopes.Describe(Row.Category);

    public IReadOnlyList<KeyCap> Caps => KeyCaps.Split(Row.Keys);
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
    private bool _keysLoading;
    private KeyboardRow? _editing;
    private KeyGesture? _pending;
    private readonly TextBox[] _quickBoxes = new TextBox[QuickTargets.Count];

    public SettingsSheet(SettingsSheetModel model)
    {
        _model = model;
        InitializeComponent();
        BuildQuickRows();
        ShowAdvice();
        _ready = true;
        PageList.ItemsSource = SettingsCatalog.Pages;
        Icons.Bind(ExpandButton, static (b, i) => b.Content = i, "layout-gallery");
        Icons.Bind(CloseButton, static (b, i) => b.Content = i, "close");
        _model.PropertyChanged += OnModelChanged;
        App.Host.Settings.Changed += OnSettingsChanged;
        App.Host.SearchIndex.Changed += OnIndexChanged;
        Sync();
    }

    // What to suggest on the Operations page, worked out from this machine; nothing is changed for the user.
    private void ShowAdvice()
    {
        var cores = Environment.ProcessorCount;
        RunningCard.Advice = Suggest(OperationsAdvice.RunningJobs(cores));
        PerDeviceCard.Advice = Suggest(OperationsAdvice.PerDevice());
        BackgroundCard.Advice = Suggest(OperationsAdvice.Background(cores));

        static string Suggest(OperationsAdvice.Advice advice) => $"Suggested for this PC: {advice.Value}. {advice.Why}";
    }

    /// <summary>Gives the Keyboard page its rows (the window knows the commands).</summary>
    internal Func<IReadOnlyList<Leviathan.ViewModels.Commands.KeyboardRow>> KeyRows { get; set; } = () => [];

    /// <summary>The window handle the file pickers need (export and import of keys).</summary>
    internal nint WindowHandle { get; set; }

    /// <summary>The user asked for the diagnostics window.</summary>
    internal event EventHandler? DiagnosticsRequested;

    /// <summary>Called when the sheet is removed for good.</summary>
    internal void Detach()
    {
        _model.PropertyChanged -= OnModelChanged;
        App.Host.Settings.Changed -= OnSettingsChanged;
        App.Host.SearchIndex.Changed -= OnIndexChanged;
    }

    /// <summary>Puts the keyboard in the search box.</summary>
    internal void FocusSearch() => SearchBox.Focus(FocusState.Programmatic);

    private void OnModelChanged(object? sender, PropertyChangedEventArgs e) => Sync();

    private void OnSettingsChanged(object? sender, AppSettings settings) => DispatcherQueue.TryEnqueue(Load);


    /// <summary>How many pages <see cref="WarmPage"/> knows.</summary>
    public const int WarmPageCount = 14;

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

        UIElement[] pages = [GeneralPage, AppearancePage, FilesPage, TabsPage, AdvancedPage, KeyboardPage, PlannedPage, TagsPage, NetworkPage, OperationsPage, DeletePage, MediaPage, SearchPage, TriagePage];
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
            TagsPage.Visibility = Show("tags");
            NetworkPage.Visibility = Show("network");
            OperationsPage.Visibility = Show("operations");
            DeletePage.Visibility = Show("delete");
            MediaPage.Visibility = Show("media");
            TriagePage.Visibility = Show("triage");
            SearchPage.Visibility = Show("search");
            var planned = !page.IsAvailable;
            PlannedPage.Visibility = planned ? Visibility.Visible : Visibility.Collapsed;
            if (planned)
            {
                PlannedTitle.Text = page.Title;
                PlannedWhen.Text = $"This page arrives in {page.Arrives}. It will hold:";
                PlannedList.ItemsSource = page.Planned!.Select(item => new TextBlock { Text = "•  " + item, Margin = new Thickness(8, 0, 0, 2) }).ToList();
            }

            PageTitle.Text = page.Title;
            PageSummary.Text = page.Summary;
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
            EagerBox.SelectedIndex = settings.Files.UiUpdates == UiUpdateMode.Immediate ? 0 : 1;
            TriageWhenBox.SelectedIndex = settings.Media.TriageMoveWhen == TriageMoveMode.AtEnd ? 1 : 0;
            TriageAskSwitch.IsOn = settings.Media.TriageAskBeforeMove;
            OwnTrashSwitch.IsOn = settings.Files.KeepOwnTrashOnShares;
            RecheckBox.Text = string.Join('\r', settings.Files.RecheckSizePaths);
            BinDaysBox.Value = settings.Files.BinKeepDays;
            BinSizeBox.Value = settings.Files.BinMaxGiB;
            LastTabBox.SelectedIndex = settings.Tabs.LastTabResetsToHome ? 1 : 0;
            PinnedNamesSwitch.IsOn = settings.Tabs.PinnedTabsShowNames;
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
            UpdatesSwitch.IsOn = settings.Updates.CheckForUpdates;
            UpdateChannelBox.SelectedIndex = (int)settings.Updates.Channel;
            ShowAbout();
            ShowUpdateState();
            TerminalBox.SelectedIndex = (int)settings.Files.Terminal;
            GitSwitch.IsOn = settings.Files.GitStatus;
            var quick = QuickTargets.Normalize(settings.Files.QuickTargets);
            for (var i = 0; i < _quickBoxes.Length; i++)
            {
                if (!_quickBoxes[i].FocusState.HasFlag(FocusState.Keyboard) && _quickBoxes[i].Text != quick[i])
                {
                    _quickBoxes[i].Text = quick[i];
                }
            }

            var perf = settings.Performance;
            NetSizesSwitch.IsOn = perf.FolderSizesOnNetwork;
            NetThumbsSwitch.IsOn = perf.ThumbnailsOnNetwork;
            RunningBox.Value = perf.MaxRunningJobs;
            PerDeviceBox.Value = perf.MaxPerDevice;
            BackgroundBox.Value = perf.MaxBackgroundJobs;
            VerifyBox.SelectedIndex = (int)perf.Verify;
            BandwidthBox.Value = perf.BandwidthMBps;
            ScheduleSwitch.IsOn = perf.ScheduleEnabled;
            ScheduleLimitBox.Value = perf.ScheduleMBps;
            if (!ScheduleFromBox.FocusState.HasFlag(FocusState.Keyboard))
            {
                ScheduleFromBox.Text = SettingsInput.FormatTimeOfDay(perf.ScheduleFrom);
            }

            if (!ScheduleToBox.FocusState.HasFlag(FocusState.Keyboard))
            {
                ScheduleToBox.Text = SettingsInput.FormatTimeOfDay(perf.ScheduleTo);
            }

            TriageOffsetBox.Value = settings.Media.TriageStartOffsetPercent;
            TriageAdvanceSwitch.IsOn = settings.Media.TriageAutoAdvance;
            TriageDelayBox.Value = settings.Media.TriageAdvanceDelayMs;
            if (!TriageFolderBox.FocusState.HasFlag(FocusState.Keyboard) && TriageFolderBox.Text != settings.Media.TriageKeepFolder)
            {
                TriageFolderBox.Text = settings.Media.TriageKeepFolder;
            }

            SkimBurstBox.Value = settings.Media.SkimBurstSeconds;
            SkimIntervalBox.Value = settings.Media.SkimIntervalPercent;
            HoverScrubSwitch.IsOn = perf.HoverScrub;
        }
        finally
        {
            _loading = false;
        }

        if (_model.IsOpen && _model.PageId == "search")
        {
            FillIndex();
        }

        if (_model.IsOpen && _model.PageId == "advanced")
        {
            _ = ShowCacheSizesAsync();
        }

        FillTags(settings.Tags);

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
        var updates = EagerBox.SelectedIndex == 0 ? UiUpdateMode.Immediate : UiUpdateMode.WhenFinished;
        var git = GitSwitch.IsOn;
        await SaveAsync(s => s with { Files = s.Files with { ShowHidden = hidden, ShowSystem = system, ShowFolderSizes = sizes, KeepOwnTrashOnShares = ownTrash, UiUpdates = updates, GitStatus = git } });
    }

    private void OnFilesSelected(object sender, SelectionChangedEventArgs e) => OnFilesToggled(sender, new RoutedEventArgs());

    private async void OnTriageToggled(object sender, RoutedEventArgs e) => await SaveMediaAsync();

    private async void OnTriageSelected(object sender, SelectionChangedEventArgs e) => await SaveMediaAsync();

    // ---- General, network, operations, media and preview (spec §17.2)

    private async void OnTerminalChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready || _loading || TerminalBox.SelectedIndex < 0)
        {
            return;
        }

        var kind = (Leviathan.Core.Platform.TerminalKind)TerminalBox.SelectedIndex;
        await SaveAsync(s => s with { Files = s.Files with { Terminal = kind } });
    }

    private async void OnNetworkToggled(object sender, RoutedEventArgs e)
    {
        if (!_ready || _loading)
        {
            return;
        }

        var sizes = NetSizesSwitch.IsOn;
        var thumbs = NetThumbsSwitch.IsOn;
        await SaveAsync(s => s with { Performance = s.Performance with { FolderSizesOnNetwork = sizes, ThumbnailsOnNetwork = thumbs } });
    }

    private async void OnOperationsChanged(NumberBox sender, NumberBoxValueChangedEventArgs args) => await SaveOperationsAsync();

    private async void OnOperationsSelectionChanged(object sender, SelectionChangedEventArgs e) => await SaveOperationsAsync();

    private async void OnOperationsToggled(object sender, RoutedEventArgs e) => await SaveOperationsAsync();

    private async void OnOperationsCommit(object sender, RoutedEventArgs e) => await SaveOperationsAsync();

    private async void OnOperationsKeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter)
        {
            e.Handled = true;
            await SaveOperationsAsync();
        }
    }

    private static int Whole(NumberBox box, int fallback) => double.IsNaN(box.Value) ? fallback : (int)box.Value;

    // One save for the whole page; the numbers are clamped to their limits, a time that is not hh:mm is refused with a note.
    private async Task SaveOperationsAsync()
    {
        if (!_ready || _loading)
        {
            return;
        }

        if (!SettingsInput.TryParseTimeOfDay(ScheduleFromBox.Text, out var from) || !SettingsInput.TryParseTimeOfDay(ScheduleToBox.Text, out var to))
        {
            OperationsNote.Text = "Write the hours like 22:00 or 6:30.";
            return;
        }

        OperationsNote.Text = "A window that ends earlier than it starts runs past midnight. What to do on a name conflict is asked in the conflict dialog; a standing default is not a setting yet.";
        var running = Whole(RunningBox, 3);
        var perDevice = Whole(PerDeviceBox, 1);
        var background = Whole(BackgroundBox, 2);
        var verify = VerifyBox.SelectedIndex < 0 ? Leviathan.Contracts.VerifyMode.Default : (Leviathan.Contracts.VerifyMode)VerifyBox.SelectedIndex;
        var limit = Whole(BandwidthBox, 0);
        var scheduled = ScheduleSwitch.IsOn;
        var scheduleLimit = Whole(ScheduleLimitBox, 0);
        await SaveAsync(s => s with
        {
            Performance = (s.Performance with
            {
                MaxRunningJobs = running,
                MaxPerDevice = perDevice,
                MaxBackgroundJobs = background,
                Verify = verify,
                BandwidthMBps = limit,
                ScheduleEnabled = scheduled,
                ScheduleFrom = from,
                ScheduleTo = to,
                ScheduleMBps = scheduleLimit,
            }).Clamped(),
        });
    }

    private async void OnMediaChanged(NumberBox sender, NumberBoxValueChangedEventArgs args) => await SaveMediaAsync();

    private async void OnMediaToggled(object sender, RoutedEventArgs e) => await SaveMediaAsync();

    private async void OnMediaCommit(object sender, RoutedEventArgs e) => await SaveMediaAsync();

    private async void OnMediaKeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter)
        {
            e.Handled = true;
            await SaveMediaAsync();
        }
    }

    private async Task SaveMediaAsync()
    {
        if (!_ready || _loading)
        {
            return;
        }

        var folder = TriageFolderBox.Text.Trim();
        if (folder.Length > 0 && !Leviathan.Core.Paths.LocationPath.TryParse(folder, out _))
        {
            TriageFolderBox.Description = $"'{folder}' is not a folder address.";
            return;
        }

        TriageFolderBox.Description = null;
        var when = TriageWhenBox.SelectedIndex == 1 ? TriageMoveMode.AtEnd : TriageMoveMode.OnKeep;
        var ask = TriageAskSwitch.IsOn;
        var offset = Whole(TriageOffsetBox, 10);
        var advance = TriageAdvanceSwitch.IsOn;
        var delay = Whole(TriageDelayBox, 0);
        var burst = Whole(SkimBurstBox, 3);
        var interval = Whole(SkimIntervalBox, 5);
        var hover = HoverScrubSwitch.IsOn;
        await SaveAsync(s => s with
        {
            Media = (s.Media with
            {
                TriageMoveWhen = when,
                TriageAskBeforeMove = ask,
                TriageStartOffsetPercent = offset,
                TriageAutoAdvance = advance,
                TriageAdvanceDelayMs = delay,
                TriageKeepFolder = folder,
                SkimBurstSeconds = burst,
                SkimIntervalPercent = interval,
            }).Clamped(),
            Performance = s.Performance with { HoverScrub = hover },
        });
    }

    // The nine quick targets (spec §15.6): one box per slot, saved when the box is left; an address that is not a folder keeps the old value.
    private void BuildQuickRows()
    {
        for (var i = 0; i < _quickBoxes.Length; i++)
        {
            var box = new TextBox { Header = $"Slot {i + 1}", PlaceholderText = "not set", Width = 420, HorizontalAlignment = HorizontalAlignment.Left };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(box, $"Quick target {i + 1}");
            box.LostFocus += OnQuickLostFocus;
            box.KeyDown += (_, e) =>
            {
                if (e.Key == VirtualKey.Enter)
                {
                    e.Handled = true;
                    OnQuickLostFocus(box, e);
                }
            };
            _quickBoxes[i] = box;
            QuickTargetRows.Children.Add(box);
        }
    }

    private async void OnQuickLostFocus(object sender, RoutedEventArgs e)
    {
        if (!_ready || _loading)
        {
            return;
        }

        var saved = QuickTargets.Normalize(App.Host.Settings.Current.Files.QuickTargets);
        var slots = new string[QuickTargets.Count];
        for (var i = 0; i < slots.Length; i++)
        {
            var text = _quickBoxes[i].Text.Trim();
            slots[i] = text.Length == 0 || Leviathan.Core.Paths.LocationPath.TryParse(text, out _) ? text : saved[i];
            _quickBoxes[i].Description = text.Length > 0 && slots[i] != text ? $"'{text}' is not a folder address." : null;
        }

        await SaveAsync(s => s with { Files = s.Files with { QuickTargets = QuickTargets.Normalize(slots) } });
    }

    // ---- Search and index (spec §13)

    private void OnIndexChanged(object? sender, EventArgs e) => DispatcherQueue.TryEnqueue(() =>
    {
        if (_model.IsOpen && _model.PageId == "search")
        {
            FillIndex();
        }
    });

    private void FillIndex()
    {
        var index = App.Host.SearchIndex;
        IndexRows.Children.Clear();
        var now = DateTimeOffset.UtcNow;
        foreach (var status in index.Status())
        {
            var root = status.Root;
            var row = new Grid { ColumnSpacing = 8 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var text = new StackPanel();
            text.Children.Add(new TextBlock { Text = root.ToWin32Path(), TextTrimming = TextTrimming.CharacterEllipsis });
            text.Children.Add(new TextBlock { Text = SettingsInput.IndexStatus(status, now), Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"], Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"], TextWrapping = TextWrapping.Wrap });
            var listNow = new Button { Content = "List now" };
            listNow.Click += (_, _) => _ = Task.Run(() => index.CrawlNowAsync(root, CancellationToken.None));
            var remove = new Button { Content = "Remove" };
            remove.Click += (_, _) => _ = Task.Run(() => index.Remove(root));
            Grid.SetColumn(listNow, 1);
            Grid.SetColumn(remove, 2);
            row.Children.Add(text);
            row.Children.Add(listNow);
            row.Children.Add(remove);
            IndexRows.Children.Add(row);
        }

        if (IndexRows.Children.Count == 0)
        {
            IndexRows.Children.Add(new TextBlock { Text = "No folder is indexed yet. Add the ones you search most, such as a share.", Opacity = 0.65, TextWrapping = TextWrapping.Wrap });
        }

        IndexPauseButton.Content = index.IsPaused ? "Resume" : "Pause";
        IndexNote.Text = index.IsPaused ? "Indexing is paused; searches use what is already listed." : string.Empty;
    }

    private void OnIndexAddKeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter)
        {
            e.Handled = true;
            OnIndexAddClick(sender, e);
        }
    }

    private void OnIndexAddClick(object sender, RoutedEventArgs e)
    {
        var text = IndexAddBox.Text.Trim();
        if (text.Length == 0)
        {
            return;
        }

        if (!Leviathan.Core.Paths.LocationPath.TryParse(text, out var root))
        {
            IndexNote.Text = $"'{text}' is not a folder address. Use something like D:\\Photos or \\\\nas\\media.";
            return;
        }

        IndexAddBox.Text = string.Empty;
        _ = Task.Run(() => App.Host.SearchIndex.Add(root));
    }

    private void OnIndexPauseClick(object sender, RoutedEventArgs e)
    {
        App.Host.SearchIndex.SetPaused(!App.Host.SearchIndex.IsPaused);
        FillIndex();
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

        var home = LastTabBox.SelectedIndex == 1;
        var names = PinnedNamesSwitch.IsOn;
        await SaveAsync(s => s with { Tabs = s.Tabs with { LastTabResetsToHome = home, PinnedTabsShowNames = names } });
    }

    private void OnTabsSelected(object sender, SelectionChangedEventArgs e) => OnTabsToggled(sender, new RoutedEventArgs());

    // ---- Advanced

    // The version comes from the release tag through the build (release plan §1); a local build says it is one.
    private void ShowAbout()
    {
        var version = Leviathan.ViewModels.Settings.AppVersion.Parse(Leviathan.Diagnostics.Crash.CrashReportBuilder.ReadBuildInfo(typeof(App).Assembly));
        AboutCard.Title = $"Leviathan {version.Display}";
        AboutCard.Description = version.ShortCommit.Length > 0 ? $"Built from commit {version.ShortCommit}." : "The commit this build was made from is not known.";
        AboutChannelText.Text = version.Channel.ToString();
    }

    // ---- Updates (release plan §4): the checker owns the state; this page only shows it and asks for a check.

    private void ShowUpdateState()
    {
        var updates = App.Host.Updates;
        var disabled = updates.State == Leviathan.ViewModels.Updates.UpdateState.Disabled;
        var line = updates.Message.Length == 0 ? "Not checked yet." : updates.Message;
        if (updates.LastChecked is { } at && updates.State != Leviathan.ViewModels.Updates.UpdateState.Checking)
        {
            line += $" Last checked {at.ToLocalTime():g}.";
        }

        UpdateCard.Description = line;
        UpdateCheckButton.IsEnabled = !disabled && updates.State != Leviathan.ViewModels.Updates.UpdateState.Checking;
    }

    private async void OnCheckUpdatesClick(object sender, RoutedEventArgs e)
    {
        var updates = App.Host.Updates;
        updates.Changed += OnUpdateStateChanged;
        try
        {
            await updates.CheckAsync(CancellationToken.None);
        }
        finally
        {
            updates.Changed -= OnUpdateStateChanged;
            ShowUpdateState();
        }
    }

    private void OnUpdateStateChanged(object? sender, EventArgs e) => DispatcherQueue.TryEnqueue(ShowUpdateState);

    private async void OnUpdatesChanged(object sender, RoutedEventArgs e)
    {
        if (!_ready || _loading)
        {
            return;
        }

        var on = UpdatesSwitch.IsOn;
        await SaveAsync(s => s with { Updates = s.Updates with { CheckForUpdates = on } });
    }

    private async void OnUpdateChannelSelected(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready || _loading || UpdateChannelBox.SelectedIndex < 0)
        {
            return;
        }

        var choice = (Leviathan.ViewModels.Settings.UpdateChannelChoice)UpdateChannelBox.SelectedIndex;
        await SaveAsync(s => s with { Updates = s.Updates with { Channel = choice } });
    }

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
        _keysLoading = true;
        // The rows are in group order; the first row of each group carries the group's name.
        var views = rows.Select((r, i) => new KeyRowView(r, i == 0 || rows[i - 1].Category != r.Category ? r.Category : null)).ToList();
        KeyList.ItemsSource = views;
        if (_editing is { } editing)
        {
            var again = rows.FirstOrDefault(r => r.Command == editing.Command);
            KeyList.SelectedItem = views.FirstOrDefault(v => v.Row == again);
            _editing = again;
            if (again is null)
            {
                KeyEditor.Visibility = Visibility.Collapsed;
            }
            else
            {
                ShowEditor(again);
            }
        }

        _keysLoading = false;
        KeyProblemsText.Text = string.Join(Environment.NewLine, App.Host.KeyProblems);
        KeyProblemsText.Visibility = App.Host.KeyProblems.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        KeyNotesText.Text = string.Join(Environment.NewLine, App.Host.KeyNotes);
        KeyNotesText.Visibility = App.Host.KeyNotes.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    // ---- Keyboard editing (spec §17.2)

    private static bool IsTriageKey(CommandId id) => TriageCommands.IsTriage(id.Value);

    private static KeyMap CurrentMap(CommandId id) => IsTriageKey(id) ? App.Host.TriageKeys : App.Host.Keys;

    private static KeyMap DefaultMap(CommandId id) => IsTriageKey(id) ? TriageCommands.DefaultKeyMap() : WindowCommands.DefaultKeyMap();

    private static bool IsKnown(CommandId id) => IsTriageKey(id) ? TriageCommands.IsKnown(id) : CommandIds.All.Contains(id);

    private void OnKeyRowSelected(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready || _keysLoading || KeyList.SelectedItem is not KeyRowView { Row: var row })
        {
            return;
        }

        _editing = row;
        _pending = null;
        KeyCaptureBox.Text = string.Empty;
        KeyApplyButton.IsEnabled = false;
        ShowEditor(row);
    }

    private void ShowEditor(KeyboardRow row)
    {
        KeyEditor.Visibility = Visibility.Visible;
        KeyEditorTitle.Text = $"{row.Title}: {SettingsFormat.Keys(row.Keys)}";
        KeyEditorNote.Text = row.IsCustom ? "Changed from the default." : "The default.";
        KeyResetButton.IsEnabled = row.IsCustom;
    }

    // The box is read-only; it only listens. Modifiers alone give no gesture, so nothing is taken until a real key comes.
    private void OnKeyCapture(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        e.Handled = true;
        if (_editing is not { } row || KeyTranslator.ToGesture(e.Key) is not { } gesture)
        {
            return;
        }

        _pending = gesture;
        KeyCaptureBox.Text = gesture.ToString();
        KeyApplyButton.IsEnabled = true;
        var holder = KeyBindingEditor.HeldBy(CurrentMap(row.Command), row.Command, gesture);
        KeyEditorNote.Text = holder is { } other
            ? $"{gesture} runs {KeyRows().FirstOrDefault(r => r.Command == other)?.Title ?? other.Value} now. Using it here takes it from there."
            : $"{gesture} is free.";
    }

    private async void OnKeyApplyClick(object sender, RoutedEventArgs e)
    {
        if (_editing is not { } row || _pending is not { } gesture)
        {
            return;
        }

        _pending = null;
        KeyApplyButton.IsEnabled = false;
        await SaveAsync(s => s with { KeyBindings = KeyBindingEditor.Set(s.KeyBindings, row.Command, gesture, DefaultMap(row.Command)) });
    }

    private async void OnKeyRemoveClick(object sender, RoutedEventArgs e)
    {
        if (_editing is not { } row)
        {
            return;
        }

        await SaveAsync(s => s with { KeyBindings = KeyBindingEditor.Unbind(s.KeyBindings, row.Command, DefaultMap(row.Command)) });
    }

    private async void OnKeyResetClick(object sender, RoutedEventArgs e)
    {
        if (_editing is not { } row)
        {
            return;
        }

        await SaveAsync(s => s with { KeyBindings = KeyBindingEditor.Reset(s.KeyBindings, row.Command) });
    }

    private async Task<bool> ConfirmAsync(string title, string text, string yes)
    {
        var dialog = new ContentDialog { Title = title, Content = text, PrimaryButtonText = yes, CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Close, XamlRoot = XamlRoot };
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    private async void OnKeysResetAllClick(object sender, RoutedEventArgs e)
    {
        var count = App.Host.Settings.Current.KeyBindings.Count;
        if (count == 0)
        {
            KeyFileNote.Text = "Every command already has its default key.";
            return;
        }

        if (await ConfirmAsync("Reset all keys?", $"{count:N0} changed key{(count == 1 ? string.Empty : "s")} go back to the defaults. Export them first if you want to keep them.", "Reset all"))
        {
            await SaveAsync(s => s with { KeyBindings = [] });
            KeyFileNote.Text = "All keys are the defaults again.";
        }
    }

    private async void OnKeysExportClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new Windows.Storage.Pickers.FileSavePicker { SuggestedFileName = "leviathan-keys" };
            picker.FileTypeChoices.Add("Leviathan keys", [".json"]);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WindowHandle);
            if (await picker.PickSaveFileAsync() is not { } file)
            {
                return;
            }

            var text = KeyBindingEditor.Export(App.Host.Settings.Current.KeyBindings);
            await Task.Run(() => File.WriteAllText(file.Path, text, new System.Text.UTF8Encoding(false)));
            KeyFileNote.Text = $"Saved {file.Name} with {App.Host.Settings.Current.KeyBindings.Count:N0} changed key{(App.Host.Settings.Current.KeyBindings.Count == 1 ? string.Empty : "s")}.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
        {
            KeyFileNote.Text = $"Could not save the key file: {ex.Message}";
        }
    }

    private async void OnKeysImportClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new Windows.Storage.Pickers.FileOpenPicker();
            picker.FileTypeFilter.Add(".json");
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WindowHandle);
            if (await picker.PickSingleFileAsync() is not { } file)
            {
                return;
            }

            var text = await Task.Run(() => File.ReadAllText(file.Path));
            var imported = KeyBindingEditor.Import(text, IsKnown);
            if (!imported.IsKeyFile)
            {
                KeyFileNote.Text = imported.Problems[0];
                return;
            }

            var had = App.Host.Settings.Current.KeyBindings.Count;
            if (had > 0 && !await ConfirmAsync("Import keys?", $"The {imported.Bindings.Count:N0} changed key{(imported.Bindings.Count == 1 ? string.Empty : "s")} in {file.Name} replace your {had:N0}.", "Import"))
            {
                return;
            }

            await SaveAsync(s => s with { KeyBindings = new Dictionary<string, string>(imported.Bindings) });
            KeyFileNote.Text = $"Imported {imported.Bindings.Count:N0} changed key{(imported.Bindings.Count == 1 ? string.Empty : "s")} from {file.Name}."
                + (imported.Problems.Count > 0 ? " " + string.Join(" ", imported.Problems) : string.Empty);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
        {
            KeyFileNote.Text = $"Could not read the key file: {ex.Message}";
        }
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
