namespace Leviathan.ViewModels.Hub;

/// <summary>
/// The 8 colours jobs take in turn (spec §12.6): the strip under the address bar, the Operations rows and the status bar entry of a
/// job all use the same one. Chosen to stay apart in light and dark themes and for common colour-vision deficiencies; red is left out
/// because it is reserved for failures, and a neutral grey is kept for background tasks.
/// </summary>
public static class JobPalette
{
    /// <summary>How many job colours there are.</summary>
    public const int Count = 8;

    private static readonly uint[] Colors =
    [
        0xFF0067C0, // blue
        0xFFC25A00, // orange
        0xFF6B4FBB, // violet
        0xFF2A8A8A, // teal
        0xFFA0457A, // magenta
        0xFF5C7A1F, // olive
        0xFF8A6D00, // ochre
        0xFF3B5B8C, // slate blue
    ];

    /// <summary>The grey of background tasks and of jobs without a colour.</summary>
    public const uint Neutral = 0xFF8A8A8A;

    /// <summary>Red, for failures only (the tip of a line with failed items).</summary>
    public const uint Failure = 0xFFD13438;

    /// <summary>The colour as 0xAARRGGBB; the neutral grey for an index outside 0 to 7.</summary>
    public static uint Argb(int index) => index is >= 0 and < Count ? Colors[index] : Neutral;
}
