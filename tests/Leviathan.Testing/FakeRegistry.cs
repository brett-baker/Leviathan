using Leviathan.Core.Platform;

namespace Leviathan.Testing;

/// <summary>An in-memory registry: keys are added with their values, subkeys appear automatically.</summary>
public sealed class FakeRegistry : IRegistryView
{
    private readonly Dictionary<(RegistryRoot, string), Dictionary<string, object>> _keys = [];

    public FakeRegistry Set(RegistryRoot root, string path, string? name, object value)
    {
        var key = (root, path.Trim('\\'));
        if (!_keys.TryGetValue(key, out var values))
        {
            _keys[key] = values = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        }

        values[name ?? string.Empty] = value;

        // Make every parent exist so subkey enumeration works.
        var parent = path.Trim('\\');
        while (parent.Contains('\\', StringComparison.Ordinal))
        {
            parent = parent[..parent.LastIndexOf('\\')];
            _keys.TryAdd((root, parent), new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase));
        }

        return this;
    }

    public IReadOnlyList<string> GetSubKeyNames(RegistryRoot root, string path)
    {
        var trimmed = path.Trim('\\');
        var prefix = trimmed.Length == 0 ? string.Empty : trimmed + "\\";
        return [.. _keys.Keys
            .Where(k => k.Item1 == root && k.Item2.Length > prefix.Length && k.Item2.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .Select(k => k.Item2[prefix.Length..].Split('\\')[0])
            .Distinct(StringComparer.OrdinalIgnoreCase)];
    }

    public IReadOnlyList<string> GetValueNames(RegistryRoot root, string path) =>
        _keys.TryGetValue((root, path.Trim('\\')), out var values) ? [.. values.Keys.Where(k => k.Length > 0)] : [];

    public object? GetValue(RegistryRoot root, string path, string? name) =>
        _keys.TryGetValue((root, path.Trim('\\')), out var values) && values.TryGetValue(name ?? string.Empty, out var value) ? value : null;
}
