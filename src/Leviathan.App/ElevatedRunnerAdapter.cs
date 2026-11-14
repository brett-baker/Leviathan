using Leviathan.Contracts;
using Leviathan.Interop;
using Leviathan.ViewModels.Elevation;

namespace Leviathan.App;

/// <summary>Gives the view models the administrator helper's answer in words (spec §15.6).</summary>
internal sealed class ElevatedRunnerAdapter(ElevatedHelper helper) : IElevatedRunner
{
    public async Task<ElevatedOutcome> RunAsync(JobRequest request, CancellationToken cancellationToken)
    {
        // A copy of a big file can take hours; the wait ends with the app or after six hours, not before.
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(TimeSpan.FromHours(6));
        var result = await helper.RunAsync(request, limit.Token).ConfigureAwait(false);
        return new ElevatedOutcome(result.Ran, result.Succeeded, result.Summary);
    }
}
