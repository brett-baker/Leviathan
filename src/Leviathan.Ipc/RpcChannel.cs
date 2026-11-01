using StreamJsonRpc;

namespace Leviathan.Ipc;

/// <summary>Creates the JSON-RPC connection used on both ends of a pipe.</summary>
public static class RpcChannel
{
    /// <summary>
    /// Wraps a duplex stream in JSON-RPC with System.Text.Json and header-delimited framing. File data never travels
    /// over this channel, so a text format is fine; MessagePack can be swapped in later without contract changes.
    /// </summary>
    public static JsonRpc Create(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var formatter = new SystemTextJsonFormatter();
        var handler = new HeaderDelimitedMessageHandler(stream, stream, formatter);
        return new JsonRpc(handler);
    }
}
