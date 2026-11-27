namespace Leviathan.ViewModels.Triage.Learning;

/// <summary>The kinds of server triage can learn from. Stash is the first; a kind for another media server is one more entry here.</summary>
public enum LearningSourceKind
{
    /// <summary>A Stash server (GraphQL API with an API key).</summary>
    Stash,
}

/// <summary>What the Settings page needs to know about a kind of server: how to name it, what to prefill and what to ask for.</summary>
/// <param name="Kind">The kind.</param>
/// <param name="Title">Shown in the list of presets.</param>
/// <param name="UrlExample">A placeholder address, so the user sees the shape.</param>
/// <param name="SecretLabel">What the secret is called on this kind of server ("API key").</param>
/// <param name="Help">One line on where to find the secret.</param>
public sealed record LearningSourceKindInfo(LearningSourceKind Kind, string Title, string UrlExample, string SecretLabel, string Help);

/// <summary>
/// One server the learning may ask for facts about a file (performers, tags, date): its address lives in the settings file, its secret in
/// Windows Credential Manager under <see cref="Id"/>, so the settings file holds none. A server that is switched off is kept but never asked.
/// </summary>
/// <param name="Id">Stable; the secret is stored under it, so a rename or a new address keeps the key.</param>
/// <param name="Kind">What kind of server it is.</param>
/// <param name="Name">The user's name for it.</param>
/// <param name="Url">The server's address, e.g. <c>http://192.168.1.33:9999</c>.</param>
/// <param name="Enabled">Whether the learning may ask it.</param>
public sealed record LearningSource(string Id, LearningSourceKind Kind, string Name, string Url, bool Enabled = true)
{
    /// <summary>The presets offered by "Add a server".</summary>
    public static IReadOnlyList<LearningSourceKindInfo> Kinds { get; } =
    [
        new(LearningSourceKind.Stash, "Stash", "http://192.168.1.33:9999", "API key", "In Stash: Settings, Security, API Key."),
    ];

    /// <summary>The preset for <paramref name="kind"/>.</summary>
    public static LearningSourceKindInfo InfoOf(LearningSourceKind kind) => Kinds.FirstOrDefault(k => k.Kind == kind) ?? Kinds[0];

    /// <summary>A new, enabled server of the given kind with a fresh id.</summary>
    public static LearningSource Create(LearningSourceKind kind, string name, string url) =>
        new(Guid.NewGuid().ToString("N"), kind, name.Trim(), NormalizeUrl(url));

    /// <summary>
    /// The address cleaned up: trimmed, <c>http://</c> added when no scheme was typed, a trailing slash and a trailing <c>/graphql</c>
    /// removed (the client adds the path itself). Returns an empty string when it is not an http or https address.
    /// </summary>
    public static string NormalizeUrl(string? url)
    {
        var text = (url ?? string.Empty).Trim();
        if (text.Length == 0)
        {
            return string.Empty;
        }

        if (!text.Contains("://", StringComparison.Ordinal))
        {
            text = "http://" + text;
        }

        text = text.TrimEnd('/');
        if (text.EndsWith("/graphql", StringComparison.OrdinalIgnoreCase))
        {
            text = text[..^"/graphql".Length].TrimEnd('/');
        }

        return Uri.TryCreate(text, UriKind.Absolute, out var uri) && (uri.Scheme is "http" or "https") && uri.Host.Length > 0 ? text : string.Empty;
    }
}

/// <summary>Learning settings (triage learning plan): whether triage learns at all, and the servers that can add facts to what it learns from.</summary>
public sealed record LearningSettings
{
    /// <summary>
    /// Off by default: a copy of Leviathan that is not used to sort a pile of videos keeps no decision history and shows no chips or
    /// hints. The learning still works with no server at all (file name and video facts); a server only adds to it.
    /// </summary>
    public bool Enabled { get; init; }

    /// <summary>The servers, in the order the user added them.</summary>
    public List<LearningSource> Sources { get; init; } = [];

    /// <summary>The servers the learning may ask now.</summary>
    public IEnumerable<LearningSource> Active => Enabled ? Sources.Where(s => s.Enabled && s.Url.Length > 0) : [];

    /// <summary>The settings with a hand-edited file made safe: no missing list, no blank or duplicate ids, no unusable addresses.</summary>
    public LearningSettings Cleaned()
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var kept = new List<LearningSource>();
        foreach (var source in Sources ?? [])
        {
            if (source is null || string.IsNullOrWhiteSpace(source.Id) || !Enum.IsDefined(source.Kind) || !seen.Add(source.Id))
            {
                continue;
            }

            var name = (source.Name ?? string.Empty).Trim();
            kept.Add(source with { Name = name.Length == 0 ? InfoOfName(source.Kind) : name, Url = LearningSource.NormalizeUrl(source.Url) });
        }

        return this with { Sources = kept };
    }

    private static string InfoOfName(LearningSourceKind kind) => LearningSource.InfoOf(kind).Title;
}
