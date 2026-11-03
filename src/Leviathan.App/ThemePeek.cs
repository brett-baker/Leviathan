using System.Text.Json;
using Leviathan.ViewModels.Settings;

namespace Leviathan.App;

/// <summary>
/// Reads the chosen theme out of the settings file before the application exists. WinUI only lets the application's theme be set in
/// its constructor, and a window that starts in another theme than the application keeps parts of itself in the wrong one; so the
/// one value that decides it is read here, by hand, from a file of a few hundred bytes. Anything unreadable means "same as Windows".
/// </summary>
internal static class ThemePeek
{
    /// <summary>The theme in the settings file.</summary>
    public static ThemeMode Read(string settingsPath)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(settingsPath), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            foreach (var section in document.RootElement.EnumerateObject().Where(p => p.NameEquals("Appearance") || string.Equals(p.Name, "appearance", StringComparison.OrdinalIgnoreCase)))
            {
                foreach (var setting in section.Value.EnumerateObject().Where(p => string.Equals(p.Name, "Theme", StringComparison.OrdinalIgnoreCase)))
                {
                    if (setting.Value.ValueKind == JsonValueKind.String && Enum.TryParse<ThemeMode>(setting.Value.GetString(), ignoreCase: true, out var mode))
                    {
                        return mode;
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        {
            // No file yet, or one that cannot be read: the system theme.
        }

        return ThemeMode.System;
    }
}
