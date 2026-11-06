using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using Leviathan.Contracts;

namespace Leviathan.Ipc;

/// <summary>Builds per-user pipe names, so two Windows users on one PC each get their own worker.</summary>
public static class PipeNaming
{
    /// <summary>The worker pipe name for the current user.</summary>
    public static string WorkerPipe() => PipeNames.WorkerPrefix + UserHash();

    /// <summary>The shell host pipe name for the current user.</summary>
    public static string ShellHostPipe() => PipeNames.ShellHostPrefix + UserHash();

    /// <summary>
    /// A short hash of the current user's SID (or user name off Windows). When <c>LEVIATHAN_DATA_DIR</c> is set the folder is part of
    /// the identity, so a run with its own data folder (a test, a portable copy) gets its own worker and shell host and never talks
    /// to the one the user is working with. The worker the app starts inherits the variable, so both ends agree.
    /// </summary>
    public static string UserHash() => UserHash(Environment.GetEnvironmentVariable("LEVIATHAN_DATA_DIR"));

    /// <summary>The hash for a given value of the data folder variable (null or empty: the user alone).</summary>
    public static string UserHash(string? dataFolder)
    {
        var identity = OperatingSystem.IsWindows()
            ? WindowsIdentity.GetCurrent().User?.Value ?? Environment.UserName
            : Environment.UserName;
        if (dataFolder is { Length: > 0 })
        {
            identity += "|" + Path.GetFullPath(dataFolder).TrimEnd('\\', '/').ToUpperInvariant();
        }

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(identity)))[..16];
    }
}
