using System.IO.Abstractions;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Leviathan.ViewModels.Icons;

/// <summary>One icon of a pack: the SVG file that draws a catalog key.</summary>
/// <param name="File">File name inside the pack's folder.</param>
/// <param name="Color">True when the file carries its own colours (a full-colour icon): it is drawn as it is, never tinted.</param>
/// <param name="Source">Where it came from upstream, for the record (not used at run time).</param>
public sealed record IconPackEntry(string File, bool Color = false, string? Source = null);

/// <summary>
/// The <c>pack.json</c> of an icon pack (spec §4.3): who it is, its licence and which file draws each catalog key. A pack is a
/// folder with this file and the SVGs it names, so adding or replacing a pack needs no code. Written by
/// <c>tools/icons/Get-IconPacks.ps1</c>.
/// </summary>
public sealed record IconPackManifest
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>Stable id used in settings, e.g. <c>fluent</c>.</summary>
    public string Id { get; init; } = string.Empty;

    /// <summary>Name shown to the user.</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>One line about the pack.</summary>
    public string Description { get; init; } = string.Empty;

    /// <summary>Licence name, e.g. MIT or CC BY 4.0.</summary>
    public string License { get; init; } = string.Empty;

    /// <summary>The credit the licence asks for (shown in the About page and kept in <c>THIRD-PARTY-NOTICES.md</c>).</summary>
    public string Attribution { get; init; } = string.Empty;

    /// <summary>The upstream version the files were taken from.</summary>
    public string Version { get; init; } = string.Empty;

    /// <summary>Catalog key to file.</summary>
    public Dictionary<string, IconPackEntry> Icons { get; init; } = [];

    /// <summary>Reads a manifest. Throws <see cref="InvalidDataException"/> when it is not usable.</summary>
    public static IconPackManifest Parse(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        IconPackManifest? manifest;
        try
        {
            manifest = JsonSerializer.Deserialize<IconPackManifest>(json.TrimStart('﻿'), Json);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("The icon pack's pack.json is not valid JSON: " + ex.Message, ex);
        }

        if (manifest is null || string.IsNullOrWhiteSpace(manifest.Id))
        {
            throw new InvalidDataException("The icon pack's pack.json has no id.");
        }

        foreach (var (key, entry) in manifest.Icons)
        {
            // A pack is data from disk: its files stay inside its own folder.
            if (entry is null || string.IsNullOrWhiteSpace(entry.File) || entry.File.IndexOfAny(['/', '\\', ':']) >= 0 || entry.File.Contains("..", StringComparison.Ordinal))
            {
                throw new InvalidDataException($"The icon '{key}' names a file outside the pack's folder.");
            }
        }

        return manifest;
    }
}

/// <summary>An icon ready to draw: the SVG text and whether it carries its own colours.</summary>
/// <param name="Svg">The SVG document.</param>
/// <param name="IsColor">True when it must not be tinted.</param>
public sealed record PackedIcon(string Svg, bool IsColor);

/// <summary>An installed icon pack: its manifest and a way to read its SVGs (kept after the first read).</summary>
public sealed class IconPack
{
    private readonly IFileSystem _fileSystem;
    private readonly string _folder;
    private readonly Dictionary<string, PackedIcon?> _cache = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();

    /// <summary>Creates a pack over <paramref name="folder"/>.</summary>
    public IconPack(IFileSystem fileSystem, string folder, IconPackManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        ArgumentNullException.ThrowIfNull(manifest);
        _fileSystem = fileSystem;
        _folder = folder;
        Manifest = manifest;
    }

    /// <summary>The pack's manifest.</summary>
    public IconPackManifest Manifest { get; }

    /// <summary>The pack's id.</summary>
    public string Id => Manifest.Id;

    /// <summary>The pack's display name.</summary>
    public string Name => string.IsNullOrWhiteSpace(Manifest.Name) ? Manifest.Id : Manifest.Name;

    /// <summary>True when the pack draws <paramref name="key"/> (its file may still turn out unreadable).</summary>
    public bool Has(string key) => Manifest.Icons.ContainsKey(key);

    /// <summary>The icon for <paramref name="key"/>, or null when the pack has none or its file cannot be read (the caller falls back to the glyph).</summary>
    public PackedIcon? Get(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        lock (_gate)
        {
            if (_cache.TryGetValue(key, out var known))
            {
                return known;
            }

            PackedIcon? icon = null;
            if (Manifest.Icons.TryGetValue(key, out var entry))
            {
                try
                {
                    var text = _fileSystem.File.ReadAllText(_fileSystem.Path.Combine(_folder, entry.File));
                    if (text.Contains("<svg", StringComparison.OrdinalIgnoreCase))
                    {
                        icon = new PackedIcon(text.TrimStart('﻿'), entry.Color);
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // A missing or unreadable file is a missing icon, not a crash.
                }
            }

            _cache[key] = icon;
            return icon;
        }
    }
}

/// <summary>The icon packs installed with the app (spec §4.3): every folder under the icons folder that holds a usable <c>pack.json</c>.</summary>
public sealed class IconPacks
{
    /// <summary>The pack used when the setting names one that is not installed.</summary>
    public const string DefaultId = "fluent";

    private readonly List<IconPack> _packs;

    /// <summary>Creates the list from already loaded packs.</summary>
    public IconPacks(IEnumerable<IconPack> packs)
    {
        ArgumentNullException.ThrowIfNull(packs);
        _packs = [.. packs.OrderBy(p => p.Id == DefaultId ? 0 : 1).ThenBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase)];
    }

    /// <summary>The installed packs, the default first.</summary>
    public IReadOnlyList<IconPack> All => _packs;

    /// <summary>The pack with <paramref name="id"/>, else the default, else the first, else null (no packs: glyphs only).</summary>
    public IconPack? Resolve(string? id) =>
        _packs.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase))
        ?? _packs.FirstOrDefault(p => p.Id == DefaultId)
        ?? _packs.FirstOrDefault();

    /// <summary>Finds the packs under <paramref name="root"/>. A folder without a usable manifest is skipped.</summary>
    public static IconPacks Discover(IFileSystem fileSystem, string root)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        var packs = new List<IconPack>();
        if (!fileSystem.Directory.Exists(root))
        {
            return new IconPacks(packs);
        }

        foreach (var folder in fileSystem.Directory.EnumerateDirectories(root))
        {
            var manifestPath = fileSystem.Path.Combine(folder, "pack.json");
            try
            {
                if (fileSystem.File.Exists(manifestPath))
                {
                    packs.Add(new IconPack(fileSystem, folder, IconPackManifest.Parse(fileSystem.File.ReadAllText(manifestPath))));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                // A damaged pack is left out; the others still work.
            }
        }

        return new IconPacks(packs);
    }
}
