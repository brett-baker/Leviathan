using Leviathan.Core.Paths;

namespace Leviathan.ViewModels.Transfers;

/// <summary>What a paste does with the items on the clipboard.</summary>
public enum ClipboardOperation
{
    /// <summary>Paste puts copies in the folder and leaves the originals alone.</summary>
    Copy,

    /// <summary>Paste moves the items into the folder (they were cut).</summary>
    Cut,
}

/// <summary>Files and folders on the clipboard and whether they were cut or copied.</summary>
/// <param name="Items">The items, in the order they were selected.</param>
/// <param name="Operation">Cut or copy.</param>
public sealed record ClipboardItems(IReadOnlyList<LocationPath> Items, ClipboardOperation Operation);

/// <summary>
/// The Windows clipboard seen as a list of files (spec §6.1: Cut, Copy and Paste with Explorer and other applications). The seam
/// keeps WinRT out of the view models: commands decide <em>what</em> to cut, copy and paste, the implementation talks to Windows.
/// </summary>
public interface IFileClipboard
{
    /// <summary>Raised when the clipboard changes, from this or any other application, so the Paste button can enable or disable itself.</summary>
    event EventHandler? Changed;

    /// <summary>True when the clipboard holds files or folders. Cheap, so the command bar can ask it on every refresh.</summary>
    bool HasFiles { get; }

    /// <summary>Puts <paramref name="items"/> on the clipboard for a later paste.</summary>
    void Set(IReadOnlyList<LocationPath> items, ClipboardOperation operation);

    /// <summary>Empties the clipboard. A cut is pasted once: the items are on their way, so the clipboard stops offering them (as Explorer does).</summary>
    void Clear();

    /// <summary>Reads the files on the clipboard, or null when there are none.</summary>
    Task<ClipboardItems?> GetAsync(CancellationToken cancellationToken);
}
