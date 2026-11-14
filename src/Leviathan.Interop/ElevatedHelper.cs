using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using Leviathan.Contracts;
using Leviathan.Engine.Elevation;

namespace Leviathan.Interop;

/// <summary>
/// Starts <c>Leviathan.Elevated.exe</c> through the Windows administrator prompt for one request and waits for its answer on a pipe this side
/// opened first (spec §15.6). Only that request runs elevated; the helper exits when it is done. If the prompt is declined the result says so
/// and nothing has run. The pipe is for the current user only, so another user's program cannot answer in the helper's place.
/// </summary>
public sealed class ElevatedHelper(IProcessStarter starter, string helperPath, Func<string>? pipeName = null)
{
    /// <summary>Asks for permission, runs <paramref name="request"/> elevated and returns what it did.</summary>
    public async Task<ElevatedResult> RunAsync(JobRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        string encoded;
        try
        {
            encoded = ElevatedWork.Encode(request);
        }
        catch (InvalidOperationException ex)
        {
            return ElevatedResult.Failure(ex.Message);
        }

        var name = pipeName?.Invoke() ?? $"Leviathan.Elevated.{Guid.NewGuid():N}";
        using var pipe = new NamedPipeServerStream(name, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        var info = new ProcessStartInfo(helperPath) { UseShellExecute = true, Verb = "runas", Arguments = $"--pipe {name} --request {encoded}", WorkingDirectory = Path.GetDirectoryName(helperPath) ?? string.Empty };
        var launched = await starter.StartAsync(info, cancellationToken).ConfigureAwait(false);
        if (!launched.Started)
        {
            return ElevatedResult.Failure(launched.Cancelled ? "Administrator permission was not given, so nothing was changed." : launched.Error ?? "The administrator helper could not be started.");
        }

        try
        {
            await pipe.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
            using var reader = new StreamReader(pipe, new UTF8Encoding(false));
            return ElevatedWork.DecodeResult(await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false));
        }
        catch (OperationCanceledException)
        {
            return ElevatedResult.Failure("Waiting for the administrator helper was stopped.");
        }
        catch (IOException ex)
        {
            return ElevatedResult.Failure($"The administrator helper's answer was lost: {ex.Message}");
        }
    }
}
