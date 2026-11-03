using Leviathan.ViewModels.Commands;
using Leviathan.ViewModels.Sessions;
using Leviathan.ViewModels.Settings;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Leviathan.App;

/// <summary>
/// Startup profiles in the window (spec §17.1): the Profiles and "At startup" entries of the Options menu, the name prompt of
/// "Save current windows as profile…", and the picker shown at startup when the user chose to be asked. Everything that changes
/// a profile runs a command, so the action trail sees it; the dialogs only collect a name or a choice.
/// </summary>
public sealed partial class MainWindow
{
    private void OnOptionsMenuOpening(object? sender, object e)
    {
        var catalog = App.Host.Profiles.Catalog;
        ProfilesMenu.Items.Clear();

        var save = new MenuFlyoutItem { Text = "Save current windows as profile…" };
        save.Click += async (_, _) => await SaveProfileAsync();
        ProfilesMenu.Items.Add(save);
        ProfilesMenu.Items.Add(new MenuFlyoutSeparator());

        if (catalog.Profiles.Count == 0)
        {
            ProfilesMenu.Items.Add(new MenuFlyoutItem { Text = "No profiles yet", IsEnabled = false });
        }

        foreach (var profile in catalog.Profiles)
        {
            var name = profile.Name;
            var sub = new MenuFlyoutSubItem { Text = $"{name}  ({profile.State.TabCount} tab{(profile.State.TabCount == 1 ? string.Empty : "s")})" };
            var replace = new MenuFlyoutItem { Text = "Open, replacing the current windows" };
            replace.Click += (_, _) => Run(CommandIds.ProfileOpen, name, true);
            var add = new MenuFlyoutItem { Text = "Open in addition to the current windows" };
            add.Click += (_, _) => Run(CommandIds.ProfileOpen, name, false);
            var delete = new MenuFlyoutItem { Text = "Delete" };
            delete.Click += (_, _) => Run(CommandIds.ProfileDelete, name);
            sub.Items.Add(replace);
            sub.Items.Add(add);
            sub.Items.Add(new MenuFlyoutSeparator());
            sub.Items.Add(delete);
            ProfilesMenu.Items.Add(sub);
        }

        BuildStartupMenu();
    }

    private void BuildStartupMenu()
    {
        StartupMenu.Items.Clear();
        var general = App.Host.Settings.Current.General;
        AddStartupChoice("Restore the last session", general.Startup == StartupMode.LastSession, StartupMode.LastSession, null);
        AddStartupChoice("Ask me each time", general.Startup == StartupMode.Ask, StartupMode.Ask, null);
        var profiles = App.Host.Profiles.Catalog.Profiles;
        if (profiles.Count > 0)
        {
            StartupMenu.Items.Add(new MenuFlyoutSeparator());
        }

        foreach (var profile in profiles)
        {
            AddStartupChoice($"Open the profile \"{profile.Name}\"", general.Startup == StartupMode.Profile && string.Equals(general.StartupProfile, profile.Name, StringComparison.OrdinalIgnoreCase), StartupMode.Profile, profile.Name);
        }
    }

    private void AddStartupChoice(string text, bool isChecked, StartupMode mode, string? profile)
    {
        var item = new RadioMenuFlyoutItem { Text = text, GroupName = "startup", IsChecked = isChecked };
        item.Click += async (_, _) => await SetStartupAsync(mode, profile);
        StartupMenu.Items.Add(item);
    }

    private async Task SetStartupAsync(StartupMode mode, string? profile)
    {
        try
        {
            await App.Host.Settings.UpdateAsync(s => s with { General = s.General with { Startup = mode, StartupProfile = profile } }, CancellationToken.None);
            ShowMessage(mode switch
            {
                StartupMode.LastSession => "Leviathan will restore the last session at startup.",
                StartupMode.Ask => "Leviathan will ask what to open at startup.",
                _ => $"Leviathan will open the profile \"{profile}\" at startup.",
            });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ShowMessage($"The startup choice could not be saved: {ex.Message}");
        }
    }

    private async Task SaveProfileAsync()
    {
        var box = new TextBox { PlaceholderText = "NAS triage", MaxLength = ProfileCatalog.MaxNameLength, MinWidth = 280 };
        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            Title = "Save current windows as profile",
            Content = new StackPanel
            {
                Spacing = 8,
                Children =
                {
                    new TextBlock { Text = "Every open window with its tabs, views and position is saved under this name. A profile with the same name is replaced.", TextWrapping = TextWrapping.Wrap },
                    box,
                },
            },
            PrimaryButtonText = "Save",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
        };
        box.KeyDown += (_, e) =>
        {
            if (e.Key == Windows.System.VirtualKey.Enter && !string.IsNullOrWhiteSpace(box.Text))
            {
                dialog.Hide();
                Run(CommandIds.ProfileSave, box.Text);
            }
        };
        dialog.Opened += (_, _) => box.Focus(FocusState.Programmatic);
        if (await dialog.ShowAsync() == ContentDialogResult.Primary && !string.IsNullOrWhiteSpace(box.Text))
        {
            Run(CommandIds.ProfileSave, box.Text);
        }
    }

    /// <summary>
    /// The startup question (the user chose "Ask me each time"): Last session, a profile or a fresh window, with a box that
    /// remembers the choice. The window behind it is a fresh one and closes if something else is picked.
    /// </summary>
    internal async Task AskStartupAsync(StartupPlan plan)
    {
        var host = App.Host;
        var choices = new ListView { SelectionMode = ListViewSelectionMode.Single, MaxHeight = 320 };
        var entries = new List<(string Text, StartupAction Action, string? Profile, SessionState? State)>();
        if (plan.State is { } last)
        {
            entries.Add(($"Last session ({last.Windows.Count} window{(last.Windows.Count == 1 ? string.Empty : "s")}, {last.TabCount} tab{(last.TabCount == 1 ? string.Empty : "s")})", StartupAction.LastSession, null, last));
        }

        foreach (var profile in host.Profiles.Catalog.Profiles)
        {
            entries.Add(($"{profile.Name}  ({profile.State.TabCount} tab{(profile.State.TabCount == 1 ? string.Empty : "s")})", StartupAction.Profile, profile.Name, profile.State));
        }

        entries.Add(("A fresh window", StartupAction.Fresh, null, null));
        foreach (var entry in entries)
        {
            choices.Items.Add(entry.Text);
        }

        choices.SelectedIndex = 0;
        var remember = new CheckBox { Content = "Remember my choice" };
        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            Title = "What should Leviathan open?",
            Content = new StackPanel { Spacing = 12, Children = { choices, remember } },
            PrimaryButtonText = "Open",
            DefaultButton = ContentDialogButton.Primary,
        };
        await dialog.ShowAsync();

        var chosen = entries[Math.Max(0, choices.SelectedIndex)];
        if (remember.IsChecked == true)
        {
            var mode = chosen.Action switch
            {
                StartupAction.Profile => StartupMode.Profile,
                StartupAction.LastSession => StartupMode.LastSession,
                _ => StartupMode.Ask,
            };
            await SetStartupAsync(mode, chosen.Profile);
        }

        if (chosen.State is { } state)
        {
            var temporary = host.Windows.Windows.ToList();
            SessionCapture.Restore(host.Windows, state);
            foreach (var window in temporary)
            {
                host.Windows.Close(window);
            }
        }
    }
}
