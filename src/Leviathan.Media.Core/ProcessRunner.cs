using System.Diagnostics;
using System.Text;

namespace Leviathan.Media;

/// <summary>What a finished (or abandoned) child process left behind.</summary>
/// <param name="ExitCode">The exit code, or -1 when the process was killed.</param>
/// <param name="Output">Standard output, up to the limit that was asked for.</param>
/// <param name="Error">The start of standard error (a few KB), for the message that explains a failure.</param>
/// <param name="TimedOut">True when the time limit ended it.</param>
/// <param name="OutputTruncated">True when it printed more than the limit and the rest was thrown away.</param>
public sealed record ProcessOutcome(int ExitCode, string Output, string Error, bool TimedOut, bool OutputTruncated);

/// <summary>Runs a program and collects its output, behind an interface so what uses ffprobe is tested without it (spec §19).</summary>
public interface IProcessRunner
{
    /// <summary>
    /// Runs <paramref name="program"/> with <paramref name="arguments"/>. Ends it (and anything it started) when
    /// <paramref name="timeout"/> passes or <paramref name="cancellationToken"/> fires; never throws for a failing program, only for
    /// a program that cannot be started at all (<see cref="System.ComponentModel.Win32Exception"/>).
    /// </summary>
    Task<ProcessOutcome> RunAsync(string program, IReadOnlyList<string> arguments, TimeSpan timeout, int maxOutputBytes, CancellationToken cancellationToken);
}

/// <summary>
/// The real <see cref="IProcessRunner"/>: starts the program without a window at low priority (ffprobe must never compete with the UI
/// or with a copy), reads output with a size cap so a runaway cannot fill memory, and kills the whole process tree on a timeout.
/// </summary>
public sealed class SystemProcessRunner : IProcessRunner
{
    private const int ErrorLimit = 4096;

    /// <inheritdoc />
    public async Task<ProcessOutcome> RunAsync(string program, IReadOnlyList<string> arguments, TimeSpan timeout, int maxOutputBytes, CancellationToken cancellationToken)
    {
        var info = new ProcessStartInfo(program)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = info };
        process.Start();
        try
        {
            process.PriorityClass = ProcessPriorityClass.BelowNormal;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // The process may already have ended; priority is a courtesy, not a requirement.
        }

        process.StandardInput.Close();
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(timeout);
        var output = new StringBuilder();
        var error = new StringBuilder();
        var truncated = false;
        var reading = Task.WhenAll(
            Task.Run(async () => truncated = await ReadAsync(process.StandardOutput, output, maxOutputBytes, limit.Token).ConfigureAwait(false), CancellationToken.None),
            Task.Run(async () => await ReadAsync(process.StandardError, error, ErrorLimit, limit.Token).ConfigureAwait(false), CancellationToken.None));

        try
        {
            await process.WaitForExitAsync(limit.Token).ConfigureAwait(false);
            await reading.WaitAsync(limit.Token).ConfigureAwait(false);
            return new ProcessOutcome(process.ExitCode, output.ToString(), error.ToString(), TimedOut: false, truncated);
        }
        catch (OperationCanceledException)
        {
            Kill(process);
            await Task.WhenAny(reading, Task.Delay(TimeSpan.FromSeconds(2), CancellationToken.None)).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return new ProcessOutcome(-1, output.ToString(), error.ToString(), TimedOut: true, truncated);
        }
    }

    // Reads text until the end or the cap; past the cap it keeps draining so the child never blocks on a full pipe, and says so.
    private static async Task<bool> ReadAsync(StreamReader reader, StringBuilder into, int maxBytes, CancellationToken cancellationToken)
    {
        var buffer = new char[8192];
        var bytes = 0L;
        var truncated = false;
        try
        {
            int read;
            while ((read = await reader.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false)) > 0)
            {
                if (bytes < maxBytes)
                {
                    into.Append(buffer, 0, read);
                }
                else
                {
                    truncated = true;
                }

                bytes += read;
            }
        }
        catch (OperationCanceledException)
        {
            // The caller decides what a timeout means.
        }

        return truncated || bytes > maxBytes;
    }

    private static void Kill(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // Already gone.
        }
    }
}
