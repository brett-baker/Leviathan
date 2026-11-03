using Leviathan.Core.Paths;

namespace Leviathan.Providers.Local;

/// <summary>
/// Watches local folders with <see cref="FileSystemWatcher"/> (spec §3.1). Network locations and mapped drives are not watched:
/// change notifications over SMB are unreliable and chatty, and those folders are checked when visited instead (spec §3.3).
/// Events are debounced (<see cref="ChangeDebouncer"/>); a watcher that fails (folder gone, buffer overflow) reports one change so
/// the view looks again.
/// </summary>
public sealed class LocalFolderWatcher : IFolderWatcher
{
    private readonly TimeProvider _time;

    /// <summary>Creates the watcher service.</summary>
    /// <param name="time">Clock for the debounce.</param>
    public LocalFolderWatcher(TimeProvider? time = null) => _time = time ?? TimeProvider.System;

    /// <summary>How long after the first event of a burst the change is reported.</summary>
    public TimeSpan Delay { get; init; } = TimeSpan.FromMilliseconds(300);

    /// <summary>The least time between two reports for one folder.</summary>
    public TimeSpan MinGap { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>Decides whether a location is on a mapped network drive; replaced in tests.</summary>
    public Func<LocationPath, bool> IsMappedDrive { get; init; } = MappedDrives.IsMappedDrive;

    /// <inheritdoc />
    public IDisposable Watch(LocationPath folder, Action changed)
    {
        ArgumentNullException.ThrowIfNull(folder);
        ArgumentNullException.ThrowIfNull(changed);
        if (!IsWatchable(folder))
        {
            return Nothing.Instance;
        }

        ChangeDebouncer? debouncer = null;
        FileSystemWatcher? watcher = null;
        try
        {
            debouncer = new ChangeDebouncer(_time, Delay, MinGap, changed);
            watcher = new FileSystemWatcher(folder.ToWin32Path())
            {
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.Size | NotifyFilters.LastWrite | NotifyFilters.Attributes,
                IncludeSubdirectories = false,
                InternalBufferSize = 64 * 1024,
            };
            var signal = debouncer.Signal;
            watcher.Created += (_, _) => signal();
            watcher.Deleted += (_, _) => signal();
            watcher.Changed += (_, _) => signal();
            watcher.Renamed += (_, _) => signal();
            watcher.Error += (_, _) => signal();
            watcher.EnableRaisingEvents = true;
            return new Subscription(watcher, debouncer);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            watcher?.Dispose();
            debouncer?.Dispose();
            return Nothing.Instance;
        }
    }

    private bool IsWatchable(LocationPath folder) =>
        !folder.IsNetwork && folder.Kind is LocationKind.Drive && !IsMappedDrive(folder);

    private sealed class Subscription(FileSystemWatcher watcher, ChangeDebouncer debouncer) : IDisposable
    {
        public void Dispose()
        {
            watcher.EnableRaisingEvents = false;
            watcher.Dispose();
            debouncer.Dispose();
        }
    }

    private sealed class Nothing : IDisposable
    {
        public static readonly Nothing Instance = new();

        public void Dispose()
        {
        }
    }
}
