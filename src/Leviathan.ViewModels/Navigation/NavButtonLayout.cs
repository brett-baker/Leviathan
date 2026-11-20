namespace Leviathan.ViewModels.Navigation;

/// <summary>The navigation buttons of a pane, as flags.</summary>
[Flags]
public enum NavButtons
{
    /// <summary>None.</summary>
    None = 0,

    /// <summary>Back.</summary>
    Back = 1,

    /// <summary>Forward.</summary>
    Forward = 2,

    /// <summary>Up.</summary>
    Up = 4,

    /// <summary>Reload.</summary>
    Reload = 8,

    /// <summary>All four.</summary>
    All = Back | Forward | Up | Reload,
}

/// <summary>
/// Which navigation buttons a pane has room for (spec §15.1). The address bar keeps its minimum width and the buttons give way first, in
/// the order Reload, Up, Forward; Back stays last. The keys (F5, Alt+Up, Alt+Right) still work for a hidden button. Pure, so the order and
/// the thresholds are tested without a window.
/// </summary>
public static class NavButtonLayout
{
    /// <summary>The width the address bar needs before a button is allowed to take room from it.</summary>
    public const double MinimumAddressWidth = 220;

    /// <summary>A button and the space after it.</summary>
    public const double ButtonWidth = 40;

    // In the order they are hidden, last first.
    private static readonly NavButtons[] ByPriority = [NavButtons.Back, NavButtons.Forward, NavButtons.Up, NavButtons.Reload];

    /// <summary>The buttons to show in a bar <paramref name="width"/> wide (before its padding): as many as leave the address bar its minimum, but at least Back.</summary>
    public static NavButtons Visible(double width)
    {
        var room = width - MinimumAddressWidth;
        var count = double.IsNaN(room) ? ByPriority.Length : (int)Math.Floor(room / ButtonWidth);
        count = Math.Clamp(count, 1, ByPriority.Length);
        var shown = NavButtons.None;
        for (var i = 0; i < count; i++)
        {
            shown |= ByPriority[i];
        }

        return shown;
    }
}
