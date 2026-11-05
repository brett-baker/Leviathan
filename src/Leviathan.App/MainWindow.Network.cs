using Leviathan.ViewModels.Commands;
using Leviathan.ViewModels.Sidebar;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Leviathan.App;

/// <summary>The Network section's actions (spec §7.1): Add connection…, Remove, and opening a server.</summary>
public sealed partial class MainWindow
{
    // Add connection… (spec 7.1): a server or share, an optional name for the tree and, for a share, an optional drive letter. SMB
    // only until Phase 4. The server joins Network, the drive (if asked for) is mapped and remembered by Windows, and the tab goes
    // there; a share that wants a login shows the usual "Sign in…" button. Nothing is typed into Windows here, and no password
    // passes through Leviathan (spec 3.3).
    private async Task AddConnectionAsync()
    {
        var address = new TextBox { Header = "Server or share", PlaceholderText = @"diskstation   or   \\192.168.1.10\media", MinWidth = 380 };
        var name = new TextBox { Header = "Name in the tree (optional)", PlaceholderText = "Same as the server" };
        var letters = new ComboBox { Header = "Map as drive letter (needs a share)", MinWidth = 160, IsEnabled = false };
        letters.Items.Add("None");
        foreach (var letter in FreeDriveLetters())
        {
            letters.Items.Add($"{letter}:");
        }

        letters.SelectedIndex = 0;
        var error = new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["SystemFillColorCriticalBrush"], Visibility = Visibility.Collapsed };
        address.TextChanged += (_, _) =>
        {
            var input = AddConnectionInput.Parse(address.Text, out _);
            letters.IsEnabled = input?.HasShare == true;
            if (!letters.IsEnabled)
            {
                letters.SelectedIndex = 0;
            }

            error.Visibility = Visibility.Collapsed;
        };

        var panel = new StackPanel { Spacing = 12 };
        panel.Children.Add(address);
        panel.Children.Add(name);
        panel.Children.Add(letters);
        panel.Children.Add(error);
        var dialog = new ContentDialog
        {
            Title = "Add connection",
            Content = panel,
            PrimaryButtonText = "Connect",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = Content.XamlRoot,
        };

        AddConnectionInput? accepted = null;
        dialog.PrimaryButtonClick += async (_, args) =>
        {
            var deferral = args.GetDeferral();
            try
            {
                var input = AddConnectionInput.Parse(address.Text, out var problem);
                if (input is null)
                {
                    args.Cancel = true;
                    ShowProblem(problem);
                    return;
                }

                if (letters.SelectedIndex > 0 && input.ShareRoot is { } share)
                {
                    var map = await App.Host.Connector.MapDriveAsync(((string)letters.SelectedItem)[0], share, _closing.Token);
                    if (!map.Succeeded)
                    {
                        args.Cancel = true;
                        ShowProblem(map.Message ?? "Windows could not map the drive. If the share needs a login, open it first and sign in, then map it.");
                        return;
                    }
                }

                accepted = input;
            }
            catch (OperationCanceledException)
            {
                args.Cancel = true;
            }
            finally
            {
                deferral.Complete();
            }

            void ShowProblem(string text)
            {
                error.Text = text;
                error.Visibility = Visibility.Visible;
            }
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary || accepted is null)
        {
            return;
        }

        var servers = App.Host.SavedServers;
        if (!servers.Add(accepted.Host, name.Text))
        {
            servers.Rename(accepted.Host, string.IsNullOrWhiteSpace(name.Text) ? null : name.Text);
        }

        App.Actions.Record("network.add", accepted.Location.Value);
        Run(CommandIds.NavGo, accepted.Location.Value);
    }

    // Drive letters Windows has not given to a disk (from the drives read at startup, so no disk is touched here), D: onwards.
    private IEnumerable<char> FreeDriveLetters()
    {
        var used = _drives.Select(d => char.ToUpperInvariant(d.Root.Value[0])).ToHashSet();
        return Enumerable.Range('D', 'Z' - 'D' + 1).Select(c => (char)c).Where(c => !used.Contains(c));
    }

    // The negotiated link speed to the server of a network folder for the status bar (spec 3.5). The first ask answers nothing and
    // measures in the background; the status bar is redrawn when the answer is in, so nothing waits on name resolution.
    private long? LinkSpeedOf(Core.Paths.LocationPath location)
    {
        if (location is not { Kind: Core.Paths.LocationKind.Unc, Server: { } server })
        {
            return null;
        }

        var probe = App.Host.LinkSpeed;
        if (probe.Cached(server) is { Known: true } known)
        {
            return known.BitsPerSecond;
        }

        _ = MeasureLinkAsync(server);
        return null;
    }

    private async Task MeasureLinkAsync(string server)
    {
        try
        {
            await App.Host.LinkSpeed.SpeedToAsync(server, _closing.Token);
            OnUi(UpdateStatus);
        }
        catch (OperationCanceledException)
        {
            // Window closing.
        }
    }

    private async Task StartNearbyAsync()
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(3), _closing.Token);
            await App.Host.Nearby.RefreshAsync(_closing.Token);
        }
        catch (OperationCanceledException)
        {
            // Window closing.
        }
    }

    // Devices turn up one by one while a search runs; only the Nearby rows change, so the rest of the tree stays where it is.
    private void OnNearbyChanged(object? sender, EventArgs e) => OnUi(() =>
    {
        var nearby = App.Host.Nearby;
        var group = SidebarSections.NearbyGroup(nearby.Devices, App.Host.SavedServers.Items, nearby.IsSearching);
        _ = UpdateNearbyAsync(group);
    });

    private async Task UpdateNearbyAsync(SidebarGroup group)
    {
        try
        {
            await _sidebar.UpdateGroupAsync(SidebarSections.NearbyTitle, group, HealthOfServer);
        }
        catch (OperationCanceledException)
        {
            // Window closing.
        }
    }

    private static void RemoveServerFromTree(string host) => App.Host.SavedServers.Remove(host);
}
