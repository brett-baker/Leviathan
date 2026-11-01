using System.IO.Abstractions;
using System.Text.RegularExpressions;
using Leviathan.Core.Paths;
using Leviathan.Core.Platform;

namespace Leviathan.ViewModels.NewMenu;

/// <summary>
/// Creates the item a New menu entry describes (spec §6.1). The name is made unique the way Explorer does it
/// ("New Text Document.txt", then "New Text Document (2).txt"); the caller then opens the inline rename box on the result.
/// Creation never overwrites: a name that appears between the check and the write is skipped.
/// </summary>
public sealed partial class NewItemCreator
{
    private const int MaxAttempts = 1000;

    private readonly IFileSystem _fileSystem;

    /// <summary>Creates the creator over <paramref name="fileSystem"/>.</summary>
    public NewItemCreator(IFileSystem fileSystem)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        _fileSystem = fileSystem;
    }

    /// <summary>
    /// Creates the item in <paramref name="folder"/>, named <paramref name="name"/> or the template's default name, numbered
    /// if taken. Returns its location.
    /// </summary>
    /// <exception cref="ArgumentException">The name is empty or contains characters Windows does not allow.</exception>
    /// <exception cref="IOException">The item could not be created (access denied, disk full, ...).</exception>
    public LocationPath Create(NewItemTemplate template, LocationPath folder, string? name = null)
    {
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(folder);
        var wanted = string.IsNullOrWhiteSpace(name) ? template.DefaultName : name.Trim();
        ValidateName(wanted);

        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            var candidate = _fileSystem.Path.Combine(folder.Value, Numbered(wanted, attempt, template.Content is FolderContent));
            try
            {
                if (TryCreate(template.Content, candidate))
                {
                    return LocationPath.Parse(candidate);
                }
            }
            catch (IOException) when (_fileSystem.File.Exists(candidate) || _fileSystem.Directory.Exists(candidate))
            {
                // Created by someone else between the check and the write; try the next number.
            }
        }

        throw new IOException($"Could not find a free name for '{wanted}' in {folder}.");
    }

    /// <summary>"New Text Document.txt" becomes "New Text Document (2).txt"; the first attempt keeps the name.</summary>
    public static string Numbered(string name, int attempt, bool isFolder)
    {
        if (attempt <= 1)
        {
            return name;
        }

        var extension = isFolder ? string.Empty : ExtensionOf(name);
        var stem = name[..^extension.Length];
        return $"{stem} ({attempt}){extension}";
    }

    private static string ExtensionOf(string name)
    {
        var dot = name.LastIndexOf('.');
        return dot > 0 ? name[dot..] : string.Empty;
    }

    private static void ValidateName(string name)
    {
        if (name.Length == 0 || name.Length > 255 || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name.EndsWith('.') || ReservedName().IsMatch(name))
        {
            throw new ArgumentException($"'{name}' is not a valid file name.", nameof(name));
        }
    }

    private bool TryCreate(NewItemContent content, string path)
    {
        switch (content)
        {
            case FolderContent:
                if (_fileSystem.Directory.Exists(path) || _fileSystem.File.Exists(path))
                {
                    return false;
                }

                _fileSystem.Directory.CreateDirectory(path);
                return true;

            case FileTemplateContent template:
                if (_fileSystem.File.Exists(path) || _fileSystem.Directory.Exists(path))
                {
                    return false;
                }

                _fileSystem.File.Copy(template.TemplatePath, path, overwrite: false);
                return true;

            default:
                if (_fileSystem.File.Exists(path) || _fileSystem.Directory.Exists(path))
                {
                    return false;
                }

                using (var stream = _fileSystem.File.Open(path, FileMode.CreateNew, FileAccess.Write))
                {
                    if (content is FileDataContent data)
                    {
                        stream.Write(data.Bytes);
                    }
                }

                return true;
        }
    }

    [GeneratedRegex(@"^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(\..*)?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ReservedName();
}
