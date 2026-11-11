using Leviathan.Core.Paths;
using Leviathan.Search;
using Leviathan.ViewModels.Commands;
using Leviathan.ViewModels.Searches;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace Leviathan.App;

/// <summary>
/// The search box in the navigation row (spec §13, Ctrl+F): Enter searches the folder shown and everything below it, and the results open as a
/// folder in the same tab (so Back returns). Anything wrong with the text is said in the status bar and nothing runs. While the results list, the
/// status bar counts what was found and searched.
/// </summary>
public sealed partial class MainWindow
{
    /// <inheritdoc />
    public void FocusSearchBox() => OnUi(() =>
    {
        SearchBox.Focus(Microsoft.UI.Xaml.FocusState.Keyboard);
        if (FocusManager.GetFocusedElement(Content.XamlRoot) is TextBox box)
        {
            box.SelectAll();
        }
    });

    /// <summary>A hook for trying the space map without a click: LEVIATHAN_OPEN_SPACEMAP=&lt;folder&gt; opens its map once the window is up (docs/decisions.md).</summary>
    private void OpenSpaceMapFromEnvironment()
    {
        if (Environment.GetEnvironmentVariable("LEVIATHAN_OPEN_SPACEMAP") is { Length: > 0 } path && LocationPath.TryParse(path, out var folder))
        {
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => OpenSpaceMap(folder));
        }
    }

    // Asked from a pool thread by a session that meets a server it has not seen (spec 7.1): shown here, on the window's own thread.
    private Task<bool> AskToTrustAsync(Leviathan.Providers.Remote.TrustRequest request)
    {
        var answer = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        OnUi(async () =>
        {
            try
            {
                var dialog = new Microsoft.UI.Xaml.Controls.ContentDialog
                {
                    Title = $"Trust {request.Endpoint.Host}?",
                    Content = $"This is the first time Leviathan connects to {request.Endpoint.Host}. Its {request.Kind} is:\n\n{request.Fingerprint}\n\nOnly trust it if you expect this server. Leviathan will remember it and warn you if it ever changes.",
                    PrimaryButtonText = "Trust",
                    CloseButtonText = "Cancel",
                    DefaultButton = Microsoft.UI.Xaml.Controls.ContentDialogButton.Close,
                    XamlRoot = Content.XamlRoot,
                };
                answer.TrySetResult(await dialog.ShowAsync() == Microsoft.UI.Xaml.Controls.ContentDialogResult.Primary);
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException)
            {
                // Another dialog is open or the window is closing: the server is not trusted this time.
                answer.TrySetResult(false);
            }
        });
        return answer.Task;
    }

    // The server refused the sign-in (spec 7.1): ask for the password again, keep it in Credential Manager, and try the folder once more.
    private async Task AskForRemotePasswordAsync(LocationPath location)
    {
        var endpoint = Leviathan.Providers.Remote.RemoteLocationProvider.EndpointOf(location);
        var box = new Microsoft.UI.Xaml.Controls.PasswordBox { Header = $"Password for {endpoint.User ?? "anonymous"}@{endpoint.Host}" };
        var dialog = new Microsoft.UI.Xaml.Controls.ContentDialog
        {
            Title = "Sign in",
            Content = box,
            PrimaryButtonText = "Sign in",
            CloseButtonText = "Cancel",
            DefaultButton = Microsoft.UI.Xaml.Controls.ContentDialogButton.Primary,
            XamlRoot = Content.XamlRoot,
        };
        if (await dialog.ShowAsync() == Microsoft.UI.Xaml.Controls.ContentDialogResult.Primary)
        {
            App.Host.RemoteSecrets.Set(endpoint, box.Password);
            Run(Leviathan.ViewModels.Commands.CommandIds.NavReload);
        }
    }

    private static void RemoveRemoteConnection(LocationPath where)
    {
        if (App.Host.RemoteConnections.Remove(where) is { } removed)
        {
            App.Host.RemoteSecrets.Set(removed.Endpoint, null);
            App.Actions.Record("network.remove", removed.Location.Value);
        }
    }

    /// <inheritdoc />
    public bool ToggleSearchIndex(LocationPath folder) => App.Host.ToggleSearchIndex(folder);

    /// <inheritdoc />
    public void OpenSpaceMap(LocationPath folder) => OnUi(() =>
    {
        var host = App.Host;
        void Reveal(string path)
        {
            var location = LocationPath.TryParse(path, out var parsed) ? parsed : folder;
            var where = location.Parent ?? location;
            OnUi(() => Run(Leviathan.ViewModels.Commands.CommandIds.TabOpenInNewTab, where.Value, true));
        }

        var actions = new SpaceMapActions(
            async path =>
            {
                var location = LocationPath.Parse(path);
                if (Directory.Exists(location.ToWin32Path()))
                {
                    OnUi(() => Run(Leviathan.ViewModels.Commands.CommandIds.TabOpenInNewTab, location.Value, true));
                    return;
                }

                var result = await host.Launcher.OpenAsync(location, CancellationToken.None);
                if (!result.Started && !result.Cancelled)
                {
                    ShowMessage(result.Error ?? "That could not be opened.");
                }
            },
            Reveal,
            async paths =>
            {
                var outcome = await host.Transfers.DeleteAsync([.. paths.Select(LocationPath.Parse)], permanent: false, CancellationToken.None);
                ShowMessage(outcome.Message);
                return outcome.Started;
            },
            async (entry, number) => ShowMessage((await host.Tagging.ToggleAsync([entry], number, CancellationToken.None)).Message),
            () => [.. host.Settings.Current.Tags.Catalog().Tags.Select(t => t.Name)]);
        new SpaceMapWindow(host.SpaceScanner, folder, actions).Activate();
    });

    private void OnSearchQuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
    {
        Run(CommandIds.SearchRun, args.QueryText);
        FocusFiles();
    }

    private void OnSearchKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Escape)
        {
            e.Handled = true;
            SyncSearchBox();
            FocusFiles();
        }
    }

    /// <summary>Shows the query of the bound tab when it shows search results, and an empty box otherwise.</summary>
    private void SyncSearchBox()
    {
        var text = _bound is { } tab && SearchLocations.TryRead(tab.History.Current) is { } request ? request.QueryText : string.Empty;
        if (SearchBox.Text == text)
        {
            return;
        }

        SearchBox.Text = text;
    }

    // Raised on a pool thread after every batch of results.
    private void OnSearchReported(SearchReport report) => OnUi(() =>
    {
        if (_closed || _bound is not { } tab || SearchLocations.TryRead(tab.History.Current) is not { } shown || shown.QueryText != report.Request.QueryText)
        {
            return;
        }

        MessageText.Text = SearchStatusText.For(report);
    });
}
