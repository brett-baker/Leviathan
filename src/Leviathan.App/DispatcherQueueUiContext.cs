using Leviathan.ViewModels.Folders;
using Microsoft.UI.Dispatching;

namespace Leviathan.App;

/// <summary>Runs view-model work on the WinUI dispatcher (spec §3.2: results reach the UI thread in batches).</summary>
internal sealed class DispatcherQueueUiContext(DispatcherQueue queue) : IUiContext
{
    public void Post(Action action) => queue.TryEnqueue(() =>
    {
        using var scope = UiTrace.Begin($"UI post {action.Target?.GetType().Name}.{action.Method.Name}", thresholdMs: 5);
        action();
    });
}
