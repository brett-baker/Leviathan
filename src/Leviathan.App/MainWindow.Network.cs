using Leviathan.Providers.Remote;
using Leviathan.ViewModels.Commands;
using Leviathan.ViewModels.Sidebar;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Leviathan.App;

/// <summary>The Network section's actions (spec §7.1): Add connection…, Remove, and opening a server.</summary>
public sealed partial class MainWindow
{
    // Add connection… (spec 7.1): a Windows share, an SFTP, FTP or FTPS server, or an NFS export. A share joins Network by name, the drive (if
    // asked for) is mapped and remembered by Windows, and the tab goes there; a share that wants a login shows the usual "Sign in…" button, and
    // no share password passes through Leviathan (spec 3.3). An SFTP/FTP/FTPS server takes its password here, which goes straight into Credential
    // Manager and nowhere else; NFS goes through Windows' Client for NFS.
    private async Task AddConnectionAsync()
    {
        var kind = new ComboBox { Header = "Type", MinWidth = 220 };
        foreach (var label in new[] { "Windows share (SMB)", "SFTP (SSH)", "FTP", "FTPS (FTP over TLS)", "NFS export" })
        {
            kind.Items.Add(label);
        }

        kind.SelectedIndex = 0;
        var remoteHost = new TextBox { Header = "Server", PlaceholderText = "nas.local   or   192.168.1.10" };
        var remotePort = new TextBox { Header = "Port (optional)", PlaceholderText = "22 for SFTP, 21 for FTP" };
        var remoteUser = new TextBox { Header = "User name" };
        var remotePassword = new PasswordBox { Header = "Password (kept in Windows Credential Manager)" };
        var remoteKey = new TextBox { Header = "Private key file (SFTP, optional)", PlaceholderText = "the id_ed25519 file in your .ssh folder" };
        var remotePath = new TextBox { Header = "Folder to open first (optional)", PlaceholderText = "/home/you" };
        var remoteName = new TextBox { Header = "Name in the tree (optional)" };
        var nfsExport = new TextBox { Header = "Export on the server", PlaceholderText = "/volume1/media" };
        var remotePanel = new StackPanel { Spacing = 12, Visibility = Visibility.Collapsed };
        foreach (var element in new UIElement[] { remoteHost, remotePort, remoteUser, remotePassword, remoteKey, remotePath, nfsExport, remoteName })
        {
            remotePanel.Children.Add(element);
        }

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

        var smbPanel = new StackPanel { Spacing = 12 };
        smbPanel.Children.Add(address);
        smbPanel.Children.Add(name);
        smbPanel.Children.Add(letters);
        kind.SelectionChanged += (_, _) =>
        {
            var smb = kind.SelectedIndex == 0;
            var nfs = kind.SelectedIndex == 4;
            smbPanel.Visibility = smb ? Visibility.Visible : Visibility.Collapsed;
            remotePanel.Visibility = smb ? Visibility.Collapsed : Visibility.Visible;
            remotePort.Visibility = remoteUser.Visibility = remotePassword.Visibility = remoteKey.Visibility = remotePath.Visibility = nfs ? Visibility.Collapsed : Visibility.Visible;
            remoteKey.Visibility = kind.SelectedIndex == 1 ? Visibility.Visible : Visibility.Collapsed;
            nfsExport.Visibility = nfs ? Visibility.Visible : Visibility.Collapsed;
            error.Visibility = Visibility.Collapsed;
        };
        kind.SelectedIndex = 0;
        nfsExport.Visibility = Visibility.Collapsed;
        var panel = new StackPanel { Spacing = 12 };
        panel.Children.Add(kind);
        panel.Children.Add(smbPanel);
        panel.Children.Add(remotePanel);
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
        RemoteConnection? acceptedRemote = null;
        string? acceptedPassword = null;
        Core.Paths.LocationPath? acceptedNfs = null;
        dialog.PrimaryButtonClick += async (_, args) =>
        {
            var deferral = args.GetDeferral();
            try
            {
                if (kind.SelectedIndex == 4)
                {
                    var nfsPath = NfsSupport.PathOf(remoteHost.Text, nfsExport.Text, out var nfsProblem);
                    if (nfsPath is null)
                    {
                        args.Cancel = true;
                        ShowProblem(nfsProblem);
                        return;
                    }

                    if (!NfsSupport.IsInstalled(new System.IO.Abstractions.FileSystem(), Environment.SystemDirectory))
                    {
                        args.Cancel = true;
                        ShowProblem(NfsSupport.NotInstalledMessage);
                        return;
                    }

                    acceptedNfs = nfsPath;
                    return;
                }

                if (kind.SelectedIndex > 0)
                {
                    var protocol = kind.SelectedIndex switch { 1 => RemoteProtocol.Sftp, 3 => RemoteProtocol.Ftps, _ => RemoteProtocol.Ftp };
                    var connection = new RemoteConnectionInput(protocol, remoteHost.Text, remotePort.Text, remoteUser.Text, remotePassword.Password, remoteKey.Text, remotePath.Text, remoteName.Text).ToConnection(out var remoteProblem);
                    if (connection is null)
                    {
                        args.Cancel = true;
                        ShowProblem(remoteProblem);
                        return;
                    }

                    acceptedRemote = connection;
                    acceptedPassword = remotePassword.Password;
                    return;
                }

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

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        if (acceptedRemote is not null)
        {
            // The password goes into Credential Manager and no further; the saved connection holds the server, the account and the folder.
            App.Host.RemoteSecrets.Set(acceptedRemote.Endpoint, string.IsNullOrEmpty(acceptedPassword) ? null : acceptedPassword);
            App.Host.RemoteConnections.AddOrReplace(acceptedRemote);
            App.Actions.Record("network.add", acceptedRemote.Location.Value);
            Run(CommandIds.NavGo, acceptedRemote.Location.Value);
            return;
        }

        if (acceptedNfs is not null)
        {
            App.Host.SavedServers.Add(acceptedNfs.Server ?? string.Empty, name.Text);
            App.Actions.Record("network.add", acceptedNfs.Value);
            Run(CommandIds.NavGo, acceptedNfs.Value);
            return;
        }

        if (accepted is null)
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
