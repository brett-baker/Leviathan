namespace Leviathan.ViewModels.Settings;

/// <summary>One key of a shortcut as the Keyboard page draws it: a key cap, or the word between two alternative shortcuts.</summary>
/// <param name="Text">What is written on the cap, or "or".</param>
/// <param name="IsCap">True for a key cap; false for the plain word between alternatives.</param>
public sealed record KeyCap(string Text, bool IsCap = true);

/// <summary>Turns the text of a shortcut ("Ctrl+Shift+O, F2") into the key caps the Keyboard page draws (spec §17.2).</summary>
public static class KeyCaps
{
    /// <summary>One cap per key; "or" between shortcuts that run the same command. Empty text gives no caps.</summary>
    public static IReadOnlyList<KeyCap> Split(string keys)
    {
        var caps = new List<KeyCap>();
        foreach (var gesture in (keys ?? string.Empty).Split(", ", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (caps.Count > 0)
            {
                caps.Add(new KeyCap("or", IsCap: false));
            }

            // A "+" key is written "Ctrl++": the last plus belongs to the key, not to the separator.
            var plusKey = gesture.EndsWith("++", StringComparison.Ordinal) || gesture == "+";
            var parts = (plusKey ? gesture[..^1] : gesture).Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
            if (plusKey)
            {
                parts.Add("+");
            }

            caps.AddRange(parts.Where(p => p.Length > 0).Select(p => new KeyCap(Name(p))));
        }

        return caps;
    }

    private static string Name(string key) => key switch
    {
        "Left" => "←",
        "Right" => "→",
        "Up" => "↑",
        "Down" => "↓",
        "Back" => "Backspace",
        "Return" => "Enter",
        _ => key,
    };
}

/// <summary>Says where a shortcut works, for the (i) beside a command and its group on the Keyboard page (spec §17.2).</summary>
public static class KeyScopes
{
    /// <summary>Where the shortcuts of <paramref name="category"/> are active, in a sentence.</summary>
    public static string Describe(string category)
    {
        ArgumentNullException.ThrowIfNull(category);
        if (category.StartsWith("Triage", StringComparison.Ordinal))
        {
            return "Only in the triage window, while you sort videos. Other windows ignore these keys, so they may be the same as a key elsewhere.";
        }

        return category switch
        {
            "View" or "Navigation" => "In a file window, whichever pane has the focus. Not while you type in a box.",
            _ => "In a file window, on the pane that has the focus (and from the command palette). Not while you type in a box or in the triage window.",
        };
    }
}

/// <summary>What to suggest for the Operations page, worked out from the machine (spec §12, §17.2). Suggestions only: nothing is changed for the user.</summary>
public static class OperationsAdvice
{
    /// <summary>A suggested value and the reason, in words a person can check.</summary>
    public sealed record Advice(int Value, string Why);

    /// <summary>Jobs at once: one per two cores, between 2 and 6; more would only make the drives fight each other.</summary>
    public static Advice RunningJobs(int cores)
    {
        var value = Math.Clamp(cores / 2, 2, 6);
        return new(value, $"{cores} processor cores here; more jobs than this mostly make the disks take turns.");
    }

    /// <summary>At once on one drive or share: one, because a spinning disk or a NAS copies fastest with a single stream.</summary>
    public static Advice PerDevice() =>
        new(1, "One stream at a time is fastest for a hard disk or a NAS; a fast SSD can take 2.");

    /// <summary>Background jobs (folder sizes, thumbnails): a quarter of the cores, between 1 and 4, so the file list stays quick.</summary>
    public static Advice Background(int cores)
    {
        var value = Math.Clamp(cores / 4, 1, 4);
        return new(value, $"{cores} processor cores here; background work should leave most of them to you.");
    }
}
