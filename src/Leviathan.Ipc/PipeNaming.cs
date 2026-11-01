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

    /// <summary>A short hash of the current user's SID (or user name off Windows).</summary>
    public static string UserHash()
    {
        var identity = OperatingSystem.IsWindows()
            ? WindowsIdentity.GetCurrent().User?.Value ?? Environment.UserName
            : Environment.UserName;
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(identity)))[..16];
    }
}
