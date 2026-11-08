namespace Leviathan.Contracts;

/// <summary>Information the shell host reports about itself.</summary>
/// <param name="ContractMajor">Contract major version the host speaks (the same number as the worker's).</param>
/// <param name="ContractMinor">Contract minor version.</param>
/// <param name="AppVersion">Informational version including the commit SHA.</param>
/// <param name="ProcessId">Host process ID.</param>
/// <param name="StartedAt">When the host started.</param>
public sealed record ShellHostInfo(int ContractMajor, int ContractMinor, string AppVersion, int ProcessId, DateTimeOffset StartedAt);

/// <summary>How the shell host's STA thread pool is doing (shown on the Background tab, spec §16.1).</summary>
/// <param name="PoolSize">Threads the pool keeps available.</param>
/// <param name="Busy">Threads running a request right now.</param>
/// <param name="Queued">Requests waiting for a thread.</param>
/// <param name="Completed">Requests that finished.</param>
/// <param name="Failed">Requests that threw.</param>
/// <param name="TimedOut">Requests given up on because they ran too long (a shell extension hung).</param>
/// <param name="AbandonedThreads">Threads still stuck in a request that timed out; they are replaced, not killed.</param>
/// <param name="Apartment">The apartment state a request runs in; always STA, reported so a test can prove it.</param>
public sealed record ShellHostStatus(int PoolSize, int Busy, int Queued, long Completed, long Failed, long TimedOut, int AbandonedThreads, string Apartment);

/// <summary>
/// The shell host (Leviathan.ShellHost), called by the UI over a named pipe (spec §2.2). It runs the things that load third-party
/// code into the process, shell context-menu handlers and preview handlers, so a crash or a hang in one of them costs a restart of
/// this process and never the window. Context-menu and preview calls arrive with slices F2 and F3.
/// </summary>
public interface IShellHostService
{
    /// <summary>Returns host and contract versions; also used as a heartbeat.</summary>
    Task<ShellHostInfo> PingAsync(CancellationToken cancellationToken);

    /// <summary>Runs a trivial request on the STA pool, so a healthy answer proves the pool still takes work, and reports its counters.</summary>
    Task<ShellHostStatus> GetStatusAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Asks the shell for the context menu of <see cref="ShellMenuRequest.Paths"/> and returns its top-level items, third-party
    /// handlers included. The menu stays open in the host until <see cref="CloseContextMenuAsync"/> or two idle minutes, because
    /// invoking an item must reach the same COM objects that built it.
    /// </summary>
    Task<ShellMenu> OpenContextMenuAsync(ShellMenuRequest request, CancellationToken cancellationToken);

    /// <summary>Loads the items of a submenu ("Send to", "Open with") that the shell fills only when it is about to be shown.</summary>
    Task<IReadOnlyList<ShellMenuItem>> LoadSubMenuAsync(Guid menuId, IReadOnlyList<int> path, CancellationToken cancellationToken);

    /// <summary>Runs an item of an open menu.</summary>
    Task<ShellInvokeResult> InvokeAsync(Guid menuId, ShellInvokeRequest request, CancellationToken cancellationToken);

    /// <summary>Releases an open menu. Closing a menu that is already gone is not an error.</summary>
    Task CloseContextMenuAsync(Guid menuId, CancellationToken cancellationToken);

    /// <summary>
    /// Shows the registered shell preview handler of a file (Word, Excel, PowerPoint, Outlook and whatever else registers one) in a
    /// window of the host that floats over <see cref="ShellPreviewRequest"/>'s rectangle, owned by the UI's window. The handler's code
    /// runs here, so a crash or hang in it costs a restart of this process, never the UI (spec §9.1, §3.4). When the file has no handler,
    /// the result says so instead of throwing.
    /// </summary>
    Task<ShellPreviewResult> OpenPreviewAsync(ShellPreviewRequest request, CancellationToken cancellationToken);

    /// <summary>Moves and resizes an open preview; the UI calls it when the pane or the window moves. An unknown id is ignored.</summary>
    Task MovePreviewAsync(Guid previewId, ShellRect bounds, CancellationToken cancellationToken);

    /// <summary>Closes an open preview and releases its handler. Closing a preview that is already gone is not an error.</summary>
    Task ClosePreviewAsync(Guid previewId, CancellationToken cancellationToken);
}

/// <summary>A rectangle in screen pixels.</summary>
public sealed record ShellRect(int X, int Y, int Width, int Height);

/// <summary>What a shell preview is for.</summary>
/// <param name="Path">The file (a full Windows path).</param>
/// <param name="OwnerWindow">Handle of the UI window that owns the preview window, so it stays above it and goes with it when it is minimised.</param>
/// <param name="Bounds">Where the preview goes, in screen pixels.</param>
public sealed record ShellPreviewRequest(string Path, long OwnerWindow, ShellRect Bounds);

/// <summary>What became of a preview request.</summary>
/// <param name="Id">Names the preview in later calls; <see cref="Guid.Empty"/> when there is none.</param>
/// <param name="Handler">The registered handler's CLSID, when there is one.</param>
/// <param name="Error">Why there is no preview (no handler registered, handler refused the file), or null.</param>
public sealed record ShellPreviewResult(Guid Id, string? Handler, string? Error);

/// <summary>What a context menu is for.</summary>
/// <param name="Paths">The selected items (all on the same machine; Windows paths).</param>
/// <param name="Background">True to build the menu of the empty space inside the single folder in <paramref name="Paths"/> (New, View, Git Bash here), not of the folder as an item.</param>
/// <param name="Extended">True for the Shift menu: the shell adds its extended verbs ("Open command window here", "Copy as path").</param>
public sealed record ShellMenuRequest(IReadOnlyList<string> Paths, bool Background, bool Extended);

/// <summary>An open context menu.</summary>
/// <param name="Id">Names the menu in later calls.</param>
/// <param name="Items">Top-level items in the order the shell gave them.</param>
public sealed record ShellMenu(Guid Id, IReadOnlyList<ShellMenuItem> Items);

/// <summary>What a menu entry is.</summary>
public enum ShellMenuItemKind
{
    /// <summary>A command.</summary>
    Item,

    /// <summary>A divider line.</summary>
    Separator,

    /// <summary>An entry that opens a submenu.</summary>
    SubMenu,
}

/// <summary>A menu bitmap as tightly packed, top-down BGRA pixels.</summary>
/// <param name="Width">Width in pixels.</param>
/// <param name="Height">Height in pixels.</param>
/// <param name="Bgra">Four bytes per pixel.</param>
public sealed record ShellMenuIcon(int Width, int Height, byte[] Bgra);

/// <summary>One entry of a shell context menu.</summary>
/// <param name="Position">Where the entry sits in its menu; a submenu is addressed by the positions of the entries leading to it.</param>
/// <param name="Id">The command id to invoke, or -1 for a separator.</param>
/// <param name="Kind">Command, separator or submenu.</param>
/// <param name="Label">Text as the shell gives it: <c>&amp;</c> marks the mnemonic and a tab splits off the shortcut text.</param>
/// <param name="Verb">The canonical verb ("open", "properties", "copy") when the handler has one, else null.</param>
/// <param name="Enabled">False when the entry is greyed.</param>
/// <param name="Checked">True when the entry is ticked.</param>
/// <param name="IsDefault">True for the bold default entry.</param>
/// <param name="Icon">The entry's bitmap, when it has one.</param>
/// <param name="Children">The submenu's entries once loaded; null before <see cref="IShellHostService.LoadSubMenuAsync"/>.</param>
public sealed record ShellMenuItem(
    int Position,
    int Id,
    ShellMenuItemKind Kind,
    string Label,
    string? Verb,
    bool Enabled,
    bool Checked,
    bool IsDefault,
    ShellMenuIcon? Icon,
    IReadOnlyList<ShellMenuItem>? Children);

/// <summary>Which item to run and how.</summary>
/// <param name="ItemId">The <see cref="ShellMenuItem.Id"/>, or -1 to run <paramref name="Verb"/> instead.</param>
/// <param name="Verb">A canonical verb to run when <paramref name="ItemId"/> is -1.</param>
/// <param name="WorkingDirectory">Folder the command runs in; null for the shell's choice.</param>
/// <param name="OwnerWindow">Handle of the window dialogs should belong to, or 0.</param>
public sealed record ShellInvokeRequest(int ItemId, string? Verb, string? WorkingDirectory, long OwnerWindow);

/// <summary>What became of an invoke.</summary>
/// <param name="Succeeded">True when the shell accepted the command. A command that opens a dialog counts as accepted while the dialog is still open.</param>
/// <param name="Error">Why not, when it did not succeed.</param>
public sealed record ShellInvokeResult(bool Succeeded, string? Error);
