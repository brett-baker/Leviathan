namespace Leviathan.ViewModels.Triage.Learning;

/// <summary>
/// Where the secret of a learning server (its API key) is kept. The real one is Windows Credential Manager, protected by the user's
/// sign-in, so neither the settings file nor a log ever holds a key. A key is never written back to the screen once saved.
/// </summary>
public interface ILearningSecrets
{
    /// <summary>The secret kept for the server, or null when none is.</summary>
    string? Get(string sourceId);

    /// <summary>Keeps the secret for the server; null or empty forgets it.</summary>
    void Set(string sourceId, string? secret);
}

/// <summary>What a test of a server found.</summary>
/// <param name="Ok">True when the server answered and accepted the key.</param>
/// <param name="Message">One sentence for the user: what the server is, or what to fix.</param>
public sealed record LearningTestResult(bool Ok, string Message);

/// <summary>Asks a server whether the address and key work, so a mistake shows up in Settings and not later as a silent gap in the learning.</summary>
public interface ILearningSourceTester
{
    /// <summary>Tries the server. Never throws for a network or server problem: that is a result with <see cref="LearningTestResult.Ok"/> false.</summary>
    Task<LearningTestResult> TestAsync(LearningSource source, string? secret, CancellationToken cancellationToken);
}
