using System.Runtime.InteropServices;
using Leviathan.Core.Providers;

namespace Leviathan.Interop;

/// <summary>
/// <see cref="IShareEnumerator"/> over <c>NetShareEnum</c> (spec §7.1: a server's shares appear below it in the tree). The call runs on a
/// pool thread and is abandoned when the token fires, because a dead server can hold it for a minute (spec §3.3); the blocked call
/// ends harmlessly in the background. Only disk shares are returned, without administrative ones such as <c>C$</c> and <c>IPC$</c>.
/// </summary>
public sealed partial class WindowsShareEnumerator : IShareEnumerator
{
    private const uint MaxPreferred = 0xFFFFFFFF;
    private const uint TypeDisk = 0;
    private const uint TypeMask = 0xFF;
    private const uint SpecialFlag = 0x80000000;
    private const int Success = 0;
    private const int MoreData = 234;

    /// <inheritdoc />
    public async Task<IReadOnlyList<ShareInfo>> ListSharesAsync(string server, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(server);
        var work = Task.Run(() => Enumerate(server), CancellationToken.None);
        return await work.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private static unsafe List<ShareInfo> Enumerate(string server)
    {
        if (!OperatingSystem.IsWindows())
        {
            return [];
        }

        byte* buffer = null;
        try
        {
            uint read = 0;
            uint total = 0;
            uint resume = 0;
            var status = NetShareEnum("\\\\" + server, 1, &buffer, MaxPreferred, &read, &total, &resume);
            if (status != Success && status != MoreData)
            {
                // The error number rides in the HRESULT so LocationErrors can tell "server not found" from "access denied".
                throw new IOException($"{server} did not list its shares (error {status}).") { HResult = unchecked((int)0x80070000) | status };
            }

            var shares = new List<ShareInfo>((int)read);
            var items = (ShareInfo1*)buffer;
            for (var i = 0; i < read; i++)
            {
                var type = items[i].Type;
                if ((type & TypeMask) != TypeDisk || (type & SpecialFlag) != 0)
                {
                    continue;
                }

                var name = Marshal.PtrToStringUni((nint)items[i].Name);
                if (!string.IsNullOrEmpty(name) && !name.EndsWith('$'))
                {
                    var comment = Marshal.PtrToStringUni((nint)items[i].Remark);
                    shares.Add(new ShareInfo(name, string.IsNullOrWhiteSpace(comment) ? null : comment));
                }
            }

            return shares;
        }
        finally
        {
            if (buffer is not null)
            {
                _ = NetApiBufferFree(buffer);
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct ShareInfo1
    {
        public char* Name;
        public uint Type;
        public char* Remark;
    }

    [LibraryImport("netapi32.dll", EntryPoint = "NetShareEnum", StringMarshalling = StringMarshalling.Utf16)]
    private static unsafe partial int NetShareEnum(string serverName, uint level, byte** buffer, uint preferredMaximumLength, uint* entriesRead, uint* totalEntries, uint* resumeHandle);

    [LibraryImport("netapi32.dll", EntryPoint = "NetApiBufferFree")]
    private static unsafe partial int NetApiBufferFree(void* buffer);
}
