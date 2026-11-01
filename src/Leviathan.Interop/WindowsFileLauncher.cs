using System.ComponentModel;
using System.Diagnostics;
using Leviathan.Core.Paths;
using Leviathan.Core.Platform;

namespace Leviathan.Interop;

/// <summary>Starts a process. Behind an interface so launch commands can be tested without opening real programs.</summary>
public interface IProcessStarter
{
    /// <summary>Starts the process described by <paramref name="info"/> off the caller's thread.</summary>
    Task<LaunchResult> StartAsync(ProcessStartInfo info, CancellationToken cancellationToken);
}

/// <summary><see cref="IProcessStarter"/> over <see cref="Process"/>.</summary>
public sealed class ProcessStarter : IProcessStarter
{
    /// <summary>Win32 error 1223: the user declined the elevation prompt.</summary>
    private const int ErrorCancelled = 1223;

    /// <inheritdoc />
    public Task<LaunchResult> StartAsync(ProcessStartInfo info, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(info);
        cancellationToken.ThrowIfCancellationRequested();

        // ShellExecute can wait on a UAC prompt or a slow association handler, so it never runs on the caller's thread.
        return Task.Run(
            () =>
            {
                try
                {
                    using var process = Process.Start(info);
                    return process is null && !info.UseShellExecute ? new LaunchResult(false, "Windows did not start the program.") : LaunchResult.Ok;
                }
                catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorCancelled)
                {
                    return new LaunchResult(false, null, Cancelled: true);
                }
                catch (Win32Exception ex)
                {
                    return new LaunchResult(false, ex.Message);
                }
                catch (InvalidOperationException ex)
                {
                    return new LaunchResult(false, ex.Message);
                }
            },
            cancellationToken);
    }
}

/// <summary>
/// <see cref="IFileLauncher"/> over <c>ShellExecute</c> and the usual terminal programs (spec §6.2, §15.6). Paths are passed
/// in their long-path-safe Win32 form, and the working directory is the item's folder so relative references in the opened
/// program resolve the way they do from Explorer.
/// </summary>
public sealed class WindowsFileLauncher : IFileLauncher
{
    private readonly IProcessStarter _starter;

    /// <summary>Creates a launcher that starts processes through <paramref name="starter"/>.</summary>
    public WindowsFileLauncher(IProcessStarter? starter = null) => _starter = starter ?? new ProcessStarter();

    /// <inheritdoc />
    public Task<LaunchResult> OpenAsync(LocationPath path, CancellationToken cancellationToken) =>
        _starter.StartAsync(Shell(path, verb: null), cancellationToken);

    /// <inheritdoc />
    public Task<LaunchResult> RunAsAdministratorAsync(LocationPath path, CancellationToken cancellationToken) =>
        _starter.StartAsync(Shell(path, verb: "runas"), cancellationToken);

    /// <inheritdoc />
    public Task<LaunchResult> ShowPropertiesAsync(LocationPath path, CancellationToken cancellationToken) =>
        _starter.StartAsync(Shell(path, verb: "properties"), cancellationToken);

    /// <inheritdoc />
    public Task<LaunchResult> OpenWithAsync(LocationPath path, string executable, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executable);
        var info = new ProcessStartInfo(executable) { UseShellExecute = true, WorkingDirectory = WorkingDirectoryOf(path) };
        info.ArgumentList.Add(path.ToWin32Path());
        return _starter.StartAsync(info, cancellationToken);
    }

    /// <inheritdoc />
    public Task<LaunchResult> ChooseAppAsync(LocationPath path, CancellationToken cancellationToken)
    {
        var info = new ProcessStartInfo("rundll32.exe") { UseShellExecute = false };
        info.ArgumentList.Add("shell32.dll,OpenAs_RunDLL " + path.ToWin32Path());
        return _starter.StartAsync(info, cancellationToken);
    }

    /// <inheritdoc />
    public Task<LaunchResult> OpenTerminalAsync(LocationPath folder, TerminalKind kind, CancellationToken cancellationToken) =>
        _starter.StartAsync(TerminalInfo(folder, kind), cancellationToken);

    /// <summary>Builds the command for a terminal. Public for tests and for a "copy command" feature.</summary>
    public static ProcessStartInfo TerminalInfo(LocationPath folder, TerminalKind kind)
    {
        ArgumentNullException.ThrowIfNull(folder);
        var directory = folder.ToWin32Path();
        switch (kind)
        {
            case TerminalKind.WindowsTerminal:
                var wt = new ProcessStartInfo("wt.exe") { UseShellExecute = false };
                wt.ArgumentList.Add("-d");
                wt.ArgumentList.Add(directory);
                return wt;

            case TerminalKind.PowerShell:
                var powershell = new ProcessStartInfo("powershell.exe") { UseShellExecute = false, WorkingDirectory = directory };
                powershell.ArgumentList.Add("-NoExit");
                return powershell;

            case TerminalKind.CommandPrompt:
                // cmd cannot start in a UNC folder; pushd maps a temporary drive letter for it.
                var cmd = new ProcessStartInfo("cmd.exe") { UseShellExecute = false };
                if (folder.Kind == LocationKind.Unc)
                {
                    cmd.ArgumentList.Add("/K");
                    cmd.ArgumentList.Add("pushd \"" + directory + "\"");
                }
                else
                {
                    cmd.WorkingDirectory = directory;
                }

                return cmd;

            case TerminalKind.Wsl:
                var wsl = new ProcessStartInfo("wsl.exe") { UseShellExecute = false };
                wsl.ArgumentList.Add("--cd");
                wsl.ArgumentList.Add(directory);
                return wsl;

            default:
                throw new ArgumentOutOfRangeException(nameof(kind));
        }
    }

    private static ProcessStartInfo Shell(LocationPath path, string? verb)
    {
        var info = new ProcessStartInfo(path.ToWin32Path()) { UseShellExecute = true, WorkingDirectory = WorkingDirectoryOf(path) };
        if (verb is not null)
        {
            info.Verb = verb;
        }

        return info;
    }

    private static string WorkingDirectoryOf(LocationPath path) =>
        (path.Parent ?? path).ToWin32Path();
}
