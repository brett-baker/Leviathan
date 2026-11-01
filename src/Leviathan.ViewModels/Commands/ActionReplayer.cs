using Leviathan.Diagnostics;

namespace Leviathan.ViewModels.Commands;

/// <summary>
/// Re-runs an action trail from a crash bundle through the command registry, in order, so a crash can be reproduced in a
/// debug build (spec §20.1, <c>leviathan-diag replay</c>). Entries that are not registered commands are skipped and reported.
/// </summary>
public sealed class ActionReplayer
{
    private readonly CommandRegistry _registry;

    /// <summary>Creates a replayer over <paramref name="registry"/>.</summary>
    public ActionReplayer(CommandRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        _registry = registry;
    }

    /// <summary>Replays <paramref name="actions"/> oldest first and returns the outcome of each.</summary>
    public async Task<IReadOnlyList<(UserAction Action, CommandResult Result)>> ReplayAsync(IEnumerable<UserAction> actions, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actions);
        var results = new List<(UserAction, CommandResult)>();
        foreach (var action in actions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = await _registry.ExecuteAsync(new CommandId(action.Command), new CommandContext(action.Target), cancellationToken).ConfigureAwait(false);
            results.Add((action, result));
        }

        return results;
    }
}
