using Leviathan.Core.Model;
using Leviathan.Core.Paths;

namespace Leviathan.ViewModels.Commands;

/// <summary>What a window's commands need from the UI layer that view models cannot do themselves.</summary>
public interface IShellActions
{
    /// <summary>Moves keyboard focus into the address bar and selects its text (Ctrl+L).</summary>
    void FocusAddressBar();

    /// <summary>Opens the diagnostics window (worker connection, job queue, crash reports).</summary>
    void OpenDiagnostics();

    /// <summary>Moves keyboard focus into the filter box of the command bar (Ctrl+Shift+F).</summary>
    void FocusFilterBox();

    /// <summary>Moves keyboard focus into the search box of the navigation row (Ctrl+F, spec §13).</summary>
    void FocusSearchBox();

    /// <summary>
    /// Starts or stops keeping <paramref name="folder"/> in the search index (spec §13: "folders you opt in") and returns true when it is indexed afterwards.
    /// The window owns the index, so the command only says which folder.
    /// </summary>
    bool ToggleSearchIndex(LocationPath folder);

    /// <summary>Opens the space map of a folder, drive or share in its own window (Ctrl+Shift+M, spec §14).</summary>
    void OpenSpaceMap(LocationPath folder);

    /// <summary>Shows a short message in the status bar, e.g. why an address could not be opened.</summary>
    void ShowMessage(string message);

    /// <summary>Opens the contact sheet of one video in its own window (spec §10.4).</summary>
    void OpenContactSheet(FileEntry video);

    /// <summary>Opens the large floating preview of the selection, or closes it when it is open (Space, spec §9).</summary>
    void ToggleQuickPreview();

    /// <summary>Opens triage mode over the videos of the folder view, starting at the selected one (spec §10.1).</summary>
    void StartTriage();

    /// <summary>Opens the inbox chooser: a saved, named queue over a folder, worked in sessions of N (spec §10.1).</summary>
    void StartInboxTriage();

    /// <summary>Finds duplicate files by name in the folder view and offers to select the extra copies (spec §12.7).</summary>
    void FindDuplicates();

    /// <summary>Opens the duplicate-files window for the current folder: identical contents, grouped, with rules to pick the copy to keep (spec §15.2).</summary>
    void FindDuplicateFiles();

    /// <summary>Opens the bulk-rename dialog for the selected items: ordered steps, a live preview, presets and one undo (spec §15.3).</summary>
    void BulkRename();

    /// <summary>Asks for a folder and makes a contact sheet of each of <paramref name="videos"/> into it, in the background (spec §10.4).</summary>
    void ExportContactSheets(IReadOnlyList<FileEntry> videos);

    /// <summary>Asks a yes/no question (for example before deleting for good) and returns true when the user confirmed.</summary>
    Task<bool> ConfirmAsync(string title, string message, string confirmText);

    /// <summary>Opens the checksums window for <paramref name="items"/> (files and folders); with <paramref name="verify"/> they are checksum files to check. Shells without one do nothing.</summary>
    void OpenChecksums(IReadOnlyList<LocationPath> items, bool verify)
    {
    }

    /// <summary>Opens the compare and sync window for two folders (spec §15.5); either may be null when the person has to choose.</summary>
    void OpenSync(LocationPath? left, LocationPath? right)
    {
    }

    /// <summary>Opens the command palette (<see cref="Palette.PaletteMode.Commands"/>) or the go-to palette (<see cref="Palette.PaletteMode.GoTo"/>). Shells without one do nothing.</summary>
    void OpenPalette(Palette.PaletteMode mode)
    {
    }

    /// <summary>Opens a saved sync pair: its folders, mode and settings, compared at once so the preview is waiting (spec §15.5).</summary>
    void OpenSyncPair(Guid id)
    {
    }

    /// <summary>Asks for a folder (Extract to...); null when the user cancels. Services that cannot ask return null.</summary>
    Task<LocationPath?> PickFolderAsync(string title) => Task.FromResult<LocationPath?>(null);

    /// <summary>Asks how to compress (format, level, password, name); null when the user cancels. Services that cannot ask return null.</summary>
    Task<Archives.CompressChoice?> AskCompressAsync(string suggestedName) => Task.FromResult<Archives.CompressChoice?>(null);
}
