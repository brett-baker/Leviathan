using Leviathan.Core.Paths;
using Leviathan.ViewModels.Transfers;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;

namespace Leviathan.App;

/// <summary>
/// The Windows clipboard as a list of files (spec §6.1). What Leviathan puts there is what Explorer puts there: shell storage
/// items plus the paths as text, with the requested operation telling a cut from a copy, so files move between the two programs in
/// both directions. Storage items are made only if another application pastes; a paste inside Leviathan uses the list it already
/// holds and never asks the shell. Use on the UI thread: the WinRT clipboard needs it.
/// </summary>
internal sealed class WindowsFileClipboard : IFileClipboard
{
    // Our packages carry this format. ContentChanged arrives later, on the dispatcher, so a flag set around SetContent cannot tell our
    // own change from somebody else's; the marker can.
    private const string OwnFormat = "Leviathan.FileClipboard";

    private ClipboardItems? _own;
    private bool _hasFiles;

    public WindowsFileClipboard()
    {
        Clipboard.ContentChanged += OnContentChanged;
        _hasFiles = ReadHasFiles();
    }

    /// <inheritdoc />
    public event EventHandler? Changed;

    /// <inheritdoc />
    public bool HasFiles => _hasFiles;

    /// <inheritdoc />
    public void Set(IReadOnlyList<LocationPath> items, ClipboardOperation operation)
    {
        ArgumentNullException.ThrowIfNull(items);
        if (items.Count == 0)
        {
            return;
        }

        var package = new DataPackage
        {
            RequestedOperation = operation == ClipboardOperation.Cut ? DataPackageOperation.Move : DataPackageOperation.Copy,
        };
        package.SetText(string.Join(Environment.NewLine, items.Select(i => i.Value)));
        package.SetDataProvider(StandardDataFormats.StorageItems, async request =>
        {
            var deferral = request.GetDeferral();
            try
            {
                request.SetData(await ToStorageItemsAsync(items));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                UiTrace.Mark($"clipboard: could not make storage items: {ex.Message}");
            }
            finally
            {
                deferral.Complete();
            }
        });

        package.SetData(OwnFormat, "1");
        Clipboard.SetContent(package);
        _own = new ClipboardItems(items, operation);
        _hasFiles = true;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <inheritdoc />
    public void Clear()
    {
        _own = null;
        try
        {
            Clipboard.Clear();
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException)
        {
            UiTrace.Mark($"clipboard: could not clear: {ex.Message}");
        }

        _hasFiles = false;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <inheritdoc />
    public async Task<ClipboardItems?> GetAsync(CancellationToken cancellationToken)
    {
        if (_own is { } own)
        {
            return own;
        }

        DataPackageView view;
        try
        {
            view = Clipboard.GetContent();
            if (!view.Contains(StandardDataFormats.StorageItems))
            {
                return null;
            }

            var items = await view.GetStorageItemsAsync();
            var paths = new List<LocationPath>(items.Count);
            foreach (var item in items)
            {
                if (!string.IsNullOrEmpty(item.Path) && LocationPath.TryParse(item.Path, out var path))
                {
                    paths.Add(path);
                }
            }

            if (paths.Count == 0)
            {
                return null;
            }

            // Explorer's cut asks for a move and nothing else; a copy offers copy (and sometimes link).
            var cut = view.RequestedOperation.HasFlag(DataPackageOperation.Move) && !view.RequestedOperation.HasFlag(DataPackageOperation.Copy);
            return new ClipboardItems(paths, cut ? ClipboardOperation.Cut : ClipboardOperation.Copy);
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or IOException or UnauthorizedAccessException)
        {
            // The clipboard is owned by another application for a moment, or the source went away.
            UiTrace.Mark($"clipboard: could not read: {ex.Message}");
            return null;
        }
    }

    private void OnContentChanged(object? sender, object e)
    {
        // Somebody else wrote to the clipboard: what we held is no longer on it.
        if (!HoldsOwnFormat())
        {
            _own = null;
        }

        _hasFiles = ReadHasFiles();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private static bool HoldsOwnFormat()
    {
        try
        {
            return Clipboard.GetContent().Contains(OwnFormat);
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            return false;
        }
    }

    private static bool ReadHasFiles()
    {
        try
        {
            return Clipboard.GetContent().Contains(StandardDataFormats.StorageItems);
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            return false;
        }
    }

    private static async Task<IReadOnlyList<IStorageItem>> ToStorageItemsAsync(IReadOnlyList<LocationPath> locations)
    {
        var items = new List<IStorageItem>(locations.Count);
        foreach (var location in locations)
        {
            // Another program cannot read a file on a server through a path; only Leviathan can paste those.
            if (location.Kind is not (LocationKind.Drive or LocationKind.Unc))
            {
                continue;
            }

            var path = location.Value;
            var isFolder = await Task.Run(() => Directory.Exists(path));
            items.Add(isFolder
                ? await StorageFolder.GetFolderFromPathAsync(path)
                : await StorageFile.GetFileFromPathAsync(path));
        }

        return items;
    }
}
