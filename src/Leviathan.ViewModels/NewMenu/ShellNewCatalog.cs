// The ShellNew registry scan follows Files (brett-baker/Files) src/Files.App/Utils/Shell/ShellNewMenuHelper.cs, Copyright (c)
// Files Community, MIT License: look under every HKCR extension key for a ShellNew key and read NullFile, FileName, Data and
// Command. Rewritten against IRegistryView and IFileSystem, with the display name taken from the registered file type.
// See THIRD-PARTY-NOTICES.md.

using System.IO.Abstractions;
using System.Text;
using Leviathan.Core.Platform;

namespace Leviathan.ViewModels.NewMenu;

/// <summary>Where the catalog looks for templates.</summary>
/// <param name="UserTemplatesFolder">Leviathan's templates folder (spec §6.1 source 3); created on demand by the app, not here.</param>
/// <param name="TemplateSearchFolders">Folders searched for a relative <c>FileName</c> (the Windows ShellNew and Templates folders).</param>
/// <param name="ExpandEnvironment">Expands <c>%VARS%</c> in registry values.</param>
public sealed record NewMenuOptions(string UserTemplatesFolder, IReadOnlyList<string> TemplateSearchFolders, Func<string, string> ExpandEnvironment)
{
    /// <summary>Options for this PC: <c>%APPDATA%\Leviathan\Templates</c> and the Windows template folders.</summary>
    public static NewMenuOptions ForThisMachine()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        return new NewMenuOptions(
            Path.Combine(appData, "Leviathan", "Templates"),
            [Path.Combine(windows, "ShellNew"), Path.Combine(appData, "Microsoft", "Windows", "Templates")],
            Environment.ExpandEnvironmentVariables);
    }
}

/// <summary>The New menu's content: built-in items, apps registered through <c>ShellNew</c>, and the user's templates.</summary>
public sealed class ShellNewCatalog : INewItemCatalog
{
    /// <summary>Folders deeper than this under an extension key are not searched for ShellNew (Explorer uses at most one ProgID level).</summary>
    private const int MaxDepth = 2;

    private static readonly HashSet<string> ShortcutExtensions = new(StringComparer.OrdinalIgnoreCase) { ".lnk", ".url", ".library-ms" };

    private readonly IRegistryView _registry;
    private readonly IFileSystem _fileSystem;
    private readonly NewMenuOptions _options;

    /// <summary>Creates the catalog.</summary>
    public ShellNewCatalog(IRegistryView registry, IFileSystem fileSystem, NewMenuOptions options)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(fileSystem);
        ArgumentNullException.ThrowIfNull(options);
        _registry = registry;
        _fileSystem = fileSystem;
        _options = options;
    }

    /// <summary>The items that are always offered (spec §6.1 source 1). The Shortcut item arrives with the target picker in the command bar slice.</summary>
    public static IReadOnlyList<NewItemTemplate> BuiltIn { get; } =
    [
        new("builtin:folder", "Folder", string.Empty, "New folder", NewItemSource.BuiltIn, new FolderContent()),
        new("builtin:txt", "Text document", ".txt", "New Text Document.txt", NewItemSource.BuiltIn, new EmptyFileContent()),
        new("builtin:rtf", "Rich text document", ".rtf", "New Rich Text Document.rtf", NewItemSource.BuiltIn, new FileDataContent(Encoding.ASCII.GetBytes(@"{\rtf1\ansi\deff0 }"))),
        new("builtin:bmp", "Bitmap image", ".bmp", "New Bitmap Image.bmp", NewItemSource.BuiltIn, new FileDataContent(OnePixelBitmap())),
        new("builtin:zip", "Compressed (zipped) folder", ".zip", "New Compressed (zipped) Folder.zip", NewItemSource.BuiltIn, new FileDataContent(EmptyZip())),
    ];

    /// <inheritdoc />
    public Task<IReadOnlyList<NewItemTemplate>> GetTemplatesAsync(CancellationToken cancellationToken)
    {
        var items = new List<NewItemTemplate>(BuiltIn);
        var taken = new HashSet<string>(BuiltIn.Select(b => b.Extension), StringComparer.OrdinalIgnoreCase) { string.Empty };

        foreach (var template in ReadRegistered(cancellationToken).OrderBy(t => t.DisplayName, StringComparer.OrdinalIgnoreCase))
        {
            if (taken.Add(template.Extension))
            {
                items.Add(template);
            }
        }

        items.AddRange(ReadUserTemplates());
        return Task.FromResult<IReadOnlyList<NewItemTemplate>>(items);
    }

    private IEnumerable<NewItemTemplate> ReadRegistered(CancellationToken cancellationToken)
    {
        foreach (var extension in _registry.GetSubKeyNames(RegistryRoot.ClassesRoot, string.Empty).Where(k => k.StartsWith('.') && !ShortcutExtensions.Contains(k)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var shellNew = FindShellNewKey(extension, 1);
            if (shellNew is null)
            {
                continue;
            }

            var template = Parse(extension, shellNew);
            if (template is not null)
            {
                yield return template;
            }
        }
    }

    private string? FindShellNewKey(string path, int depth)
    {
        foreach (var child in _registry.GetSubKeyNames(RegistryRoot.ClassesRoot, path))
        {
            if (child.Equals("ShellNew", StringComparison.OrdinalIgnoreCase))
            {
                return $@"{path}\{child}";
            }
        }

        if (depth >= MaxDepth)
        {
            return null;
        }

        foreach (var child in _registry.GetSubKeyNames(RegistryRoot.ClassesRoot, path))
        {
            if (FindShellNewKey($@"{path}\{child}", depth + 1) is { } found)
            {
                return found;
            }
        }

        return null;
    }

    private NewItemTemplate? Parse(string extension, string key)
    {
        var names = _registry.GetValueNames(RegistryRoot.ClassesRoot, key);
        bool Has(string name) => names.Contains(name, StringComparer.OrdinalIgnoreCase);

        // A Command entry starts a program (a wizard); creating those needs the app, so they are not offered as plain files.
        if (Has("Command") && !Has("NullFile") && !Has("FileName") && !Has("Data"))
        {
            return null;
        }

        NewItemContent? content = null;
        if (Has("FileName") && Resolve(_registry.GetValue(RegistryRoot.ClassesRoot, key, "FileName") as string) is { } template)
        {
            content = new FileTemplateContent(template);
        }
        else if (Has("Data"))
        {
            content = _registry.GetValue(RegistryRoot.ClassesRoot, key, "Data") switch
            {
                byte[] bytes => new FileDataContent(bytes),
                string text => new FileDataContent(Encoding.UTF8.GetBytes(text)),
                _ => null,
            };
        }
        else if (Has("NullFile"))
        {
            content = new EmptyFileContent();
        }

        if (content is null)
        {
            return null;
        }

        var display = FriendlyTypeName(extension);
        var itemName = _registry.GetValue(RegistryRoot.ClassesRoot, key, "ItemName") as string;
        var defaultName = itemName is { Length: > 0 } && !itemName.StartsWith('@') && !itemName.Contains("%s", StringComparison.Ordinal)
            ? EnsureExtension(itemName, extension)
            : $"New {display}{extension}";
        return new NewItemTemplate("ext:" + extension.ToLowerInvariant(), display, extension, defaultName, NewItemSource.Registry, content);
    }

    private string FriendlyTypeName(string extension)
    {
        var progId = _registry.GetValue(RegistryRoot.ClassesRoot, extension, null) as string;
        if (!string.IsNullOrEmpty(progId) && _registry.GetValue(RegistryRoot.ClassesRoot, progId, null) is string description && description.Length > 0 && !description.StartsWith('@'))
        {
            return description;
        }

        return $"{extension.TrimStart('.').ToUpperInvariant()} file";
    }

    private string? Resolve(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return null;
        }

        var expanded = _options.ExpandEnvironment(fileName);
        if (_fileSystem.Path.IsPathRooted(expanded))
        {
            return _fileSystem.File.Exists(expanded) ? expanded : null;
        }

        return _options.TemplateSearchFolders
            .Select(folder => _fileSystem.Path.Combine(folder, expanded))
            .FirstOrDefault(_fileSystem.File.Exists);
    }

    private IEnumerable<NewItemTemplate> ReadUserTemplates()
    {
        if (!_fileSystem.Directory.Exists(_options.UserTemplatesFolder))
        {
            yield break;
        }

        IEnumerable<string> files;
        try
        {
            files = _fileSystem.Directory.GetFiles(_options.UserTemplatesFolder).Order(StringComparer.OrdinalIgnoreCase).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            yield break;
        }

        foreach (var file in files)
        {
            var name = _fileSystem.Path.GetFileName(file);
            if (name.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase) || name.Equals("Thumbs.db", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // A dotfile such as ".gitignore" is all extension; it is labelled by its full name.
            var extension = _fileSystem.Path.GetExtension(name);
            var label = extension.Length > 0 && name.Length > extension.Length ? _fileSystem.Path.GetFileNameWithoutExtension(name) : name;
            yield return new NewItemTemplate("template:" + name.ToLowerInvariant(), label, extension, name, NewItemSource.UserTemplate, new FileTemplateContent(file));
        }
    }

    private static string EnsureExtension(string name, string extension) =>
        name.EndsWith(extension, StringComparison.OrdinalIgnoreCase) ? name : name + extension;

    private static byte[] EmptyZip() => [0x50, 0x4B, 0x05, 0x06, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0];

    private static byte[] OnePixelBitmap()
    {
        // 24-bit, 1x1, white. Rows are padded to four bytes.
        byte[] bytes = new byte[58];
        bytes[0] = (byte)'B';
        bytes[1] = (byte)'M';
        BitConverter.GetBytes(58).CopyTo(bytes, 2);
        BitConverter.GetBytes(54).CopyTo(bytes, 10);
        BitConverter.GetBytes(40).CopyTo(bytes, 14);
        BitConverter.GetBytes(1).CopyTo(bytes, 18);
        BitConverter.GetBytes(1).CopyTo(bytes, 22);
        BitConverter.GetBytes((short)1).CopyTo(bytes, 26);
        BitConverter.GetBytes((short)24).CopyTo(bytes, 28);
        BitConverter.GetBytes(4).CopyTo(bytes, 34);
        bytes[54] = bytes[55] = bytes[56] = 255;
        return bytes;
    }
}
