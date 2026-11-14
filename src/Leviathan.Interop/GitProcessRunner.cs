using System.ComponentModel;
using System.Diagnostics;
using Leviathan.Core.Git;

namespace Leviathan.Interop;

/// <summary>
/// <see cref="IGitRunner"/> over <c>git.exe</c> from the PATH (spec §15.6). Standard output is read to the end while the process runs, the
/// process is killed with its children after the time limit, and a PC without Git answers <see cref="GitRunResult.NotInstalled"/>.
/// </summary>
public sealed class GitProcessRunner : IGitRunner
{
    /// <inheritdoc />
    public async Task<GitRunResult> RunAsync(string workingDirectory, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var info = new ProcessStartInfo("git")
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = GitCommands.OutputEncoding,
        };
        foreach (var argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }

        info.Environment["GIT_OPTIONAL_LOCKS"] = "0";
        info.Environment["GIT_TERMINAL_PROMPT"] = "0";

        Process process;
        try
        {
            process = Process.Start(info) ?? throw new InvalidOperationException("git did not start.");
        }
        catch (Win32Exception)
        {
            return new GitRunResult(-1, string.Empty, NotInstalled: true);
        }

        using (process)
        {
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            limit.CancelAfter(timeout);
            var output = process.StandardOutput.ReadToEndAsync(limit.Token);
            var errors = process.StandardError.ReadToEndAsync(limit.Token);
            try
            {
                await process.WaitForExitAsync(limit.Token).ConfigureAwait(false);
                var text = await output.ConfigureAwait(false);
                await errors.ConfigureAwait(false);
                return new GitRunResult(process.ExitCode, text);
            }
            catch (OperationCanceledException)
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
                {
                    // It ended by itself in the meantime.
                }

                cancellationToken.ThrowIfCancellationRequested();
                return new GitRunResult(-1, string.Empty, TimedOut: true);
            }
        }
    }
}
