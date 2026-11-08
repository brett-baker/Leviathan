namespace Leviathan.ShellHost;

/// <summary>Reads default values from HKEY_CLASSES_ROOT. Seam over the registry so the lookup order is tested without one.</summary>
internal interface IClassesRegistry
{
    /// <summary>The default value of <c>HKCR\&lt;subKey&gt;</c>, or null when the key or value is missing.</summary>
    string? GetDefault(string subKey);
}

/// <summary>
/// Finds the shell preview handler registered for a file type (spec §9.1). A handler is registered as the default value of
/// <c>shellex\{8895b1c6-b41f-4c1c-a562-0d564250836f}</c> under the extension, its ProgID or its perceived type, in the order Explorer
/// asks: the extension itself, then its ProgID, then <c>SystemFileAssociations</c>. Returns the handler's CLSID, or null when none is
/// registered, which is the normal case for a file kind whose program is not installed.
/// </summary>
internal static class PreviewHandlerLocator
{
    /// <summary>The interface id under <c>shellex</c> that names a preview handler.</summary>
    public const string HandlerKey = "{8895b1c6-b41f-4c1c-a562-0d564250836f}";

    /// <summary>The CLSID of the handler for <paramref name="extension"/> (with its dot), or null.</summary>
    public static string? Find(string extension, IClassesRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        if (string.IsNullOrEmpty(extension) || extension[0] != '.' || extension.Contains('\\') || extension.Contains('/'))
        {
            return null;
        }

        var progId = registry.GetDefault(extension);
        string?[] candidates =
        [
            registry.GetDefault($@"{extension}\shellex\{HandlerKey}"),
            progId is { Length: > 0 } ? registry.GetDefault($@"{progId}\shellex\{HandlerKey}") : null,
            registry.GetDefault($@"SystemFileAssociations\{extension}\shellex\{HandlerKey}"),
        ];
        return candidates.FirstOrDefault(c => Guid.TryParse(c, out _));
    }
}
