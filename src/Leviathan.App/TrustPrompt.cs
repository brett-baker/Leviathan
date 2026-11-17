namespace Leviathan.App;

/// <summary>
/// The question "do you trust this server?" for an SSH host key or a TLS certificate seen for the first time (spec 7.1). The sessions ask from a pool thread; the
/// window that registered itself shows the dialog on its own thread and the answer comes back. With no window to ask, the server is not trusted.
/// </summary>
internal sealed class TrustPrompt
{
    /// <summary>Shows the question and returns the answer; set by a window.</summary>
    public Func<Leviathan.Providers.Remote.TrustRequest, Task<bool>>? Ask { get; set; }

    public Task<bool> AskAsync(Leviathan.Providers.Remote.TrustRequest request) => Ask is { } ask ? ask(request) : Task.FromResult(false);
}
