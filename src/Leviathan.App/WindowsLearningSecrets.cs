using Leviathan.Interop;
using Leviathan.ViewModels.Triage.Learning;

namespace Leviathan.App;

/// <summary>Keeps each learning server's API key in Windows Credential Manager under <c>Leviathan:learning:&lt;id&gt;</c>, never in the settings file.</summary>
internal sealed class WindowsLearningSecrets : ILearningSecrets
{
    private const string Prefix = "Leviathan:learning:";

    public string? Get(string sourceId) => WindowsCredentialStore.Get(Prefix + sourceId);

    public void Set(string sourceId, string? secret) => WindowsCredentialStore.Set(Prefix + sourceId, "api-key", secret);
}
