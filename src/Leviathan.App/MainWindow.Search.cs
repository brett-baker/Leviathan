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
