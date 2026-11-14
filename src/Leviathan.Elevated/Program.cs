using System.IO.Abstractions;
using System.IO.Pipes;
using System.Text;
using Leviathan.Engine.Elevation;
using Leviathan.Engine.Execution;
using Leviathan.Engine.Transfers;
using Leviathan.Interop;

// Leviathan.Elevated --pipe <name> --request <encoded request>
// Runs the one request it was given (copy, move or delete; see ElevatedWork.IsAllowed), tells the app over the pipe the app opened, and exits.
if (args.Length < 2 || args[0] != "--pipe")
{
    return 2;
}

var fs = new FileSystem();
var request = args.Length == 4 && args[2] == "--request" ? ElevatedWork.Decode(args[3]) : null;
var result = request is null
    ? ElevatedResult.Failure("The administrator helper was given a request it does not accept.")
    : await ElevatedWork.RunAsync(
        request,
        [
            new CopyMoveExecutor(fs, copier: new WindowsFileCopier()),
            new DeleteExecutor(fs, new WindowsRecycleBin(), TimeProvider.System, volumes: new WindowsVolumeInfo()),
        ],
        TimeSpan.FromHours(6),
        CancellationToken.None).ConfigureAwait(false);

try
{
    using var pipe = new NamedPipeClientStream(".", args[1], PipeDirection.Out);
    await pipe.ConnectAsync(10_000).ConfigureAwait(false);
    using var writer = new StreamWriter(pipe, new UTF8Encoding(false)) { AutoFlush = true };
    await writer.WriteLineAsync(ElevatedWork.EncodeResult(result)).ConfigureAwait(false);
}
catch (Exception ex) when (ex is IOException or TimeoutException or UnauthorizedAccessException)
{
    return 3;
}

return result.Succeeded ? 0 : 1;
