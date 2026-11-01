namespace Leviathan.Core.Platform;

/// <summary>Where a "New" menu item comes from (spec §6.1).</summary>
public enum NewItemSource
{
    /// <summary>Built into Leviathan; matches Windows' own New menu.</summary>
    BuiltIn,

    /// <summary>Registered by an installed app through a <c>ShellNew</c> registry entry, the source Explorer uses.</summary>
    Registry,

    /// <summary>A file in Leviathan's templates folder.</summary>
    UserTemplate,
}

/// <summary>What a New item creates.</summary>
public abstract record NewItemContent;

/// <summary>A new folder.</summary>
public sealed record FolderContent : NewItemContent;

/// <summary>An empty file (<c>NullFile</c>).</summary>
public sealed record EmptyFileContent : NewItemContent;

/// <summary>A file with fixed starting bytes (<c>Data</c>, or a built-in blank document).</summary>
public sealed record FileDataContent(byte[] Bytes) : NewItemContent;

/// <summary>A copy of an existing template file (<c>FileName</c>, or a user template).</summary>
public sealed record FileTemplateContent(string TemplatePath) : NewItemContent;

/// <summary>One entry of the New menu.</summary>
/// <param name="Id">Stable id, e.g. <c>builtin:folder</c> or <c>ext:.docx</c>.</param>
/// <param name="DisplayName">Menu text, e.g. "Text document" or "Microsoft Word Document".</param>
/// <param name="Extension">The extension including the dot, or empty for a folder.</param>
/// <param name="DefaultName">Name given to the new item before any numbering, e.g. "New Text Document.txt".</param>
/// <param name="Source">Where the entry came from.</param>
/// <param name="Content">What gets created.</param>
public sealed record NewItemTemplate(string Id, string DisplayName, string Extension, string DefaultName, NewItemSource Source, NewItemContent Content);

/// <summary>Lists the items of the New menu from the three sources of spec §6.1. Reads the registry and the templates folder; call off the UI thread.</summary>
public interface INewItemCatalog
{
    /// <summary>Built-in items first, then registered apps and user templates, each group sorted by name. Never throws for an unreadable source.</summary>
    Task<IReadOnlyList<NewItemTemplate>> GetTemplatesAsync(CancellationToken cancellationToken);
}
