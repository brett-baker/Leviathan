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
