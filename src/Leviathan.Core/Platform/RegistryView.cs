using System.Runtime.Versioning;
using Microsoft.Win32;

namespace Leviathan.Core.Platform;

/// <summary>Registry hives the detectors read.</summary>
public enum RegistryRoot
{
    /// <summary>HKEY_CURRENT_USER.</summary>
    CurrentUser,

    /// <summary>HKEY_LOCAL_MACHINE.</summary>
    LocalMachine,

    /// <summary>HKEY_CLASSES_ROOT.</summary>
    ClassesRoot,
}

/// <summary>Read-only registry access, so detection can be unit-tested with an in-memory registry.</summary>
public interface IRegistryView
{
    /// <summary>Names of the subkeys of <paramref name="path"/>; empty when the key does not exist.</summary>
    IReadOnlyList<string> GetSubKeyNames(RegistryRoot root, string path);

    /// <summary>Names of the values of <paramref name="path"/>; empty when the key does not exist.</summary>
    IReadOnlyList<string> GetValueNames(RegistryRoot root, string path);

    /// <summary>Reads a value; <paramref name="name"/> of null or empty reads the key's default value. Null when missing.</summary>
    object? GetValue(RegistryRoot root, string path, string? name);
}

/// <summary><see cref="IRegistryView"/> over the Windows registry.</summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsRegistryView : IRegistryView
{
    /// <inheritdoc />
    public IReadOnlyList<string> GetSubKeyNames(RegistryRoot root, string path)
    {
        using var key = Open(root, path);
        return key?.GetSubKeyNames() ?? [];
    }

    /// <inheritdoc />
    public IReadOnlyList<string> GetValueNames(RegistryRoot root, string path)
    {
        using var key = Open(root, path);
        return key?.GetValueNames() ?? [];
    }

    /// <inheritdoc />
    public object? GetValue(RegistryRoot root, string path, string? name)
    {
        using var key = Open(root, path);
        return key?.GetValue(name ?? string.Empty);
    }

    private static RegistryKey? Open(RegistryRoot root, string path)
    {
        try
        {
            var hive = root switch
            {
                RegistryRoot.CurrentUser => Registry.CurrentUser,
                RegistryRoot.LocalMachine => Registry.LocalMachine,
                _ => Registry.ClassesRoot,
            };
            return path.Length == 0 ? hive : hive.OpenSubKey(path);
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            // A key we may not read is the same as a key that is not there for detection purposes.
            return null;
        }
    }
}
