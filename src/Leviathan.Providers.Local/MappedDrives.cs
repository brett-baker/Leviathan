using System.Collections.Concurrent;
using System.Diagnostics;
using Leviathan.Core.Paths;

namespace Leviathan.Providers.Local;

/// <summary>
/// Tells whether a drive letter is a mapped network share (<c>Y:</c> pointing at <c>\\nas\media</c>). Such a drive is a network
/// location whatever its path looks like: listing it needs the large-fetch lister (far fewer round trips) and the dead-share
/// timeout, exactly as a UNC path does (spec §3.3). The answer comes from <c>GetDriveType</c>, which does no network traffic, and
/// is remembered for a while because drive letters rarely change and this is asked on every listing.
/// </summary>
public static class MappedDrives
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(30);
    private static readonly ConcurrentDictionary<char, (bool IsNetwork, long At)> Cache = new();

    /// <summary>True when <paramref name="location"/> is on a drive letter that is mapped to a network share.</summary>
    public static bool IsMappedDrive(LocationPath location)
    {
        ArgumentNullException.ThrowIfNull(location);
        if (!OperatingSystem.IsWindows() || location.Kind != LocationKind.Drive || location.Root.Length < 2 || !char.IsAsciiLetter(location.Root[0]))
        {
            return false;
        }

        var letter = char.ToUpperInvariant(location.Root[0]);
        if (Cache.TryGetValue(letter, out var known) && Stopwatch.GetElapsedTime(known.At) < Lifetime)
        {
            return known.IsNetwork;
        }

        var isNetwork = Ask(letter);
        Cache[letter] = (isNetwork, Stopwatch.GetTimestamp());
        return isNetwork;
    }

    /// <summary>Forgets what is known about drive letters (a drive was mapped or disconnected).</summary>
    public static void Forget() => Cache.Clear();

    private static bool Ask(char letter)
    {
        try
        {
            return new DriveInfo(letter.ToString()).DriveType == DriveType.Network;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
