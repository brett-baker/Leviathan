using Leviathan.Core.Duplicates;
using Leviathan.ViewModels.Folders;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Leviathan.App;

/// <summary>Find duplicates (spec §12.7): name-based groups among the files shown in the folder view.</summary>
public sealed partial class MainWindow
{
    private bool _duplicatesOpen;

    /// <summary>
    /// Alt+Shift+D: groups the files of the folder view whose names are alike (resolution, codec and release-group tags ignored), shows
    /// each group with the copy to keep, and on request selects the extra copies in the folder so Delete or Move acts on them. Nothing
    /// is changed on disk by the dialog itself.
    /// </summary>
    public void FindDuplicates() => OnUi(async () =>
    {
        if (_duplicatesOpen || _bound is not { } tab)
        {
            return;
        }

        _duplicatesOpen = true;
        try
        {
            var files = tab.Items.Rows.ToList();
            var percent = App.Host.Settings.Current.Files.DuplicateSimilarityPercent;
            var box = new NumberBox { Header = "Names must be this alike (%)", Minimum = 10, Maximum = 100, Value = percent, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact };
            var summary = new TextBlock { TextWrapping = TextWrapping.Wrap };
            var list = new ListView { MaxHeight = 360, SelectionMode = ListViewSelectionMode.None };
            IReadOnlyList<DuplicateGroup> groups = [];
            IReadOnlyList<DuplicateMatch> matches = [];

            // The other pane of a split tab is the destination (spec §12.7); without one only the folder itself can be searched.
            var shown = _viewModel.Tabs.Selected;
            var other = shown?.Secondary is null ? null : ReferenceEquals(tab, shown) ? shown.Secondary : shown;
            var destination = other is not null && !ReferenceEquals(other, tab) ? other : null;
            var mode = new ComboBox { Header = "Look for", HorizontalAlignment = HorizontalAlignment.Stretch, Visibility = destination is null ? Visibility.Collapsed : Visibility.Visible };
            mode.Items.Add("Duplicates in this folder");
            mode.Items.Add("Files here that are already in the other pane");
            mode.SelectedIndex = 0;
            var againstOther = false;
            var okButtonEnabled = false;
            ContentDialog? open = null;

            async Task RunAsync()
            {
                var threshold = (double.IsNaN(box.Value) ? percent : box.Value) / 100.0;
                againstOther = destination is not null && mode.SelectedIndex == 1;
                list.Items.Clear();
                if (againstOther)
                {
                    var target = destination!.Items.Rows.ToList();
                    matches = await Task.Run(() => DuplicateFinder.FindAgainst(files, target, threshold));
                    groups = [];
                    foreach (var match in matches)
                    {
                        var text = new StackPanel { Margin = new Thickness(0, 4, 0, 8) };
                        var verdict = match.Identical ? "same file already there" : match.DestinationIsBetter ? "better copy already there" : "this one is better";
                        text.Children.Add(new TextBlock { Text = $"{match.Source.Name}  ({DisplayFormat.Size(match.Source.Size)}) · {verdict}", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
                        foreach (var copy in match.Copies.Take(3))
                        {
                            text.Children.Add(new TextBlock { Text = $"   there: {copy.Name}  ({DisplayFormat.Size(copy.Size)})", Opacity = 0.75, TextTrimming = TextTrimming.CharacterEllipsis });
                        }

                        list.Items.Add(text);
                    }

                    var skippable = matches.Where(m => m.DestinationIsBetter).ToList();
                    summary.Text = matches.Count == 0
                        ? $"None of the {files.Count:N0} items here are in the other pane."
                        : $"{matches.Count:N0} file{(matches.Count == 1 ? "" : "s")} here also in the other pane; {skippable.Count:N0} already have a better or identical copy there ({DisplayFormat.Size(skippable.Sum(m => m.Source.Size))}).";
                    okButtonEnabled = skippable.Count > 0;
                    if (open is not null)
                    {
                        open.PrimaryButtonText = "Select the ones already there";
                        open.IsPrimaryButtonEnabled = okButtonEnabled;
                    }

                    return;
                }

                matches = [];
                groups = await Task.Run(() => DuplicateFinder.Find(files, threshold));
                foreach (var group in groups)
                {
                    var text = new StackPanel { Margin = new Thickness(0, 4, 0, 8) };
                    text.Children.Add(new TextBlock { Text = $"Keep: {group.Keeper.Name}  ({DisplayFormat.Size(group.Keeper.Size)})", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
                    for (var i = 0; i < group.Others.Count; i++)
                    {
                        text.Children.Add(new TextBlock { Text = $"   {group.Others[i].Name}  ({DisplayFormat.Size(group.Others[i].Size)}, {group.Similarity[i]:P0} alike)", Opacity = 0.75, TextTrimming = TextTrimming.CharacterEllipsis });
                    }

                    list.Items.Add(text);
                }

                var extra = groups.Sum(g => g.Others.Count);
                summary.Text = groups.Count == 0
                    ? $"No duplicates among {files.Count:N0} items in this folder."
                    : $"{groups.Count:N0} group{(groups.Count == 1 ? "" : "s")}, {extra:N0} extra cop{(extra == 1 ? "y" : "ies")}, {DisplayFormat.Size(groups.Sum(g => g.ReclaimableBytes))} that could be freed.";
                okButtonEnabled = groups.Count > 0;
                if (open is not null)
                {
                    open.PrimaryButtonText = "Select the extra copies";
                    open.IsPrimaryButtonEnabled = okButtonEnabled;
                }
            }

            var rerun = new Button { Content = "Search again" };
            rerun.Click += async (_, _) => await RunAsync();
            mode.SelectionChanged += async (_, _) => await RunAsync();
            await RunAsync();
            var dialog = new ContentDialog
            {
                Title = "Find duplicates",
                Content = new StackPanel { Spacing = 10, MinWidth = 520, Children = { mode, box, rerun, summary, list } },
                PrimaryButtonText = againstOther ? "Select the ones already there" : "Select the extra copies",
                CloseButtonText = "Close",
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = Content.XamlRoot,
            };
            open = dialog;
            dialog.IsPrimaryButtonEnabled = okButtonEnabled;
            if (await dialog.ShowAsync() == ContentDialogResult.Primary && (groups.Count > 0 || matches.Count > 0))
            {
                var extras = againstOther
                    ? matches.Where(m => m.DestinationIsBetter).Select(m => m.Source.Location).ToHashSet()
                    : groups.SelectMany(g => g.Others).Select(f => f.Location).ToHashSet();
                tab.Selection.Clear();
                foreach (var row in tab.Items.Rows.Where(r => extras.Contains(r.Location)))
                {
                    tab.Selection.Toggle(row);
                }

                MessageText.Text = againstOther
                    ? $"Selected {extras.Count:N0} file{(extras.Count == 1 ? "" : "s")} already in the other pane: Delete moves them to the bin."
                    : $"Selected {extras.Count:N0} extra cop{(extras.Count == 1 ? "y" : "ies")}: Delete moves them to the bin.";
            }

            var chosen = (int)Math.Clamp(double.IsNaN(box.Value) ? percent : box.Value, 10, 100);
            if (chosen != percent)
            {
                await App.Host.Settings.UpdateAsync(s => s with { Files = s.Files with { DuplicateSimilarityPercent = chosen } }, CancellationToken.None);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MessageText.Text = $"Could not search for duplicates: {ex.Message}";
        }
        finally
        {
            _duplicatesOpen = false;
        }
    });
}
