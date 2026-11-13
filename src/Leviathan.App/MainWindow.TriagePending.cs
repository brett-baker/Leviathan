using Leviathan.ViewModels.Triage;
using Microsoft.UI.Xaml.Controls;

namespace Leviathan.App;

/// <summary>Kept files that triage was going to move when it ended, offered again after a restart (spec §10.1, §3.4).</summary>
public sealed partial class MainWindow
{
    private static int s_pendingOffered;

    /// <summary>
    /// A while after start (never inside the cold-start budget), asks about kept files whose move never happened, typically because the
    /// app stopped before the triage window closed. Only the first window asks, once per run. "Move" runs the same move as at the end of
    /// triage; "Not now" leaves them for the next start; "Forget them" stops offering. Nothing moves without the person saying so.
    /// </summary>
    private async Task OfferWaitingKeepMovesAsync()
    {
        if (Interlocked.Exchange(ref s_pendingOffered, 1) != 0)
        {
            return;
        }

        await Task.Delay(TimeSpan.FromSeconds(6));
        var host = App.Host;
        PendingKeepMoves waiting;
        try
        {
            waiting = await Task.Run(() =>
            {
                var found = PendingKeepMoves.From(host.TriageJournal.PendingMoves(), File.Exists);
                if (found.Stale.Count > 0)
                {
                    host.TriageJournal.AppendMoved(found.Stale);
                }

                return found;
            });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return;
        }

        foreach (var group in waiting.Groups)
        {
            OnUi(async () =>
            {
                var count = group.Files.Count;
                var dialog = new ContentDialog
                {
                    Title = count == 1 ? "A kept file is waiting to be moved" : $"{count:N0} kept files are waiting to be moved",
                    Content = $"Triage kept {(count == 1 ? "this file" : "these files")} but stopped before moving {(count == 1 ? "it" : "them")} to {group.Folder}.",
                    PrimaryButtonText = "Move",
                    SecondaryButtonText = "Forget",
                    CloseButtonText = "Not now",
                    DefaultButton = ContentDialogButton.Primary,
                    XamlRoot = Content.XamlRoot,
                };

                var answer = await dialog.ShowAsync();
                if (answer == ContentDialogResult.Primary)
                {
                    var actions = host.TriageActionsFor(() => group.Folder);
                    var result = await Task.Run(() => actions.MoveKeptAsync(group.Files, group.Folder, CancellationToken.None));
                    MessageText.Text = result.Describe(group.Folder);
                    var handled = TriageHandOff.Handled(new TriageHandOffPlan(group.Files, group.Folder, false), result, File.Exists);
                    await Task.Run(() => host.TriageJournal.AppendMoved(handled));
                }
                else if (answer == ContentDialogResult.Secondary)
                {
                    await Task.Run(() => host.TriageJournal.AppendMoved(group.Files.Select(f => f.Location.Value)));
                }
            });
        }
    }
}
