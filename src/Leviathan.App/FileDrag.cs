using Leviathan.Core.Model;
using Leviathan.Core.Paths;
using Leviathan.ViewModels.Transfers;
using Microsoft.UI.Xaml;
using Windows.ApplicationModel.DataTransfer;
using Windows.ApplicationModel.DataTransfer.DragDrop;
using Windows.Storage;

namespace Leviathan.App;

/// <summary>Items were dropped on <paramref name="Destination"/> and the window should carry the drop out.</summary>
/// <param name="Destination">The folder dropped on.</param>
/// <param name="Sources">The items dropped.</param>
/// <param name="Modifiers">The modifier keys held at the drop.</param>
internal sealed record ItemDropRequest(LocationPath Destination, IReadOnlyList<LocationPath> Sources, DropModifiers Modifiers);

/// <summary>
/// Dragging files and folders out of and into Leviathan (spec §8.4). Inside the process the dragged locations travel in
/// <see cref="Current"/> so a drop can be judged while the pointer is still moving; other applications get the same items as
/// shell storage items, created only if somebody actually drops them. Drops from other applications are read when they land.
/// </summary>
internal static class FileDrag
{
    /// <summary>The locations being dragged from one of our own views, or null when no such drag is running.</summary>
    internal static IReadOnlyList<LocationPath>? Current { get; private set; }

    /// <summary>True when a drag of files is over a target: one of ours, or storage items from another application.</summary>
    internal static bool Accepts(DragEventArgs e) =>
        Current is not null || (App.DraggedTab is null && e.DataView.Contains(StandardDataFormats.StorageItems));

    /// <summary>Fills the data package of a drag that starts on <paramref name="entries"/>.</summary>
    internal static void Begin(DragStartingEventArgs args, IReadOnlyList<FileEntry> entries)
    {
        var locations = entries.Select(e => e.Location).ToList();
        Current = locations;

        // RequestedOperation is the set of operations this source supports; the target and the modifier keys pick one of them.
        args.Data.RequestedOperation = DataPackageOperation.Copy | DataPackageOperation.Move | DataPackageOperation.Link;
        args.Data.SetText(string.Join(Environment.NewLine, locations.Select(l => l.Value)));

        // Other applications want storage items. They are made on demand, when the drop happens, not for every drag that starts.
        var local = locations.Where(l => l.Kind is LocationKind.Drive or LocationKind.Unc).ToList();
        if (local.Count > 0)
        {
            args.Data.SetDataProvider(StandardDataFormats.StorageItems, async request =>
            {
                var deferral = request.GetDeferral();
                try
                {
                    request.SetData(await ToStorageItemsAsync(local, entries));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
                {
                    UiTrace.Mark($"drag out: could not make storage items: {ex.Message}");
                }
                finally
                {
                    deferral.Complete();
                }
            });
        }
    }

    /// <summary>The drag is over (dropped, cancelled, or the source went away).</summary>
    internal static void Finish() => Current = null;

    /// <summary>The modifier keys held during the drag, as the drop rules understand them.</summary>
    internal static DropModifiers ModifiersOf(DragEventArgs e)
    {
        var result = DropModifiers.None;
        if (e.Modifiers.HasFlag(DragDropModifiers.Shift))
        {
            result |= DropModifiers.Shift;
        }

        if (e.Modifiers.HasFlag(DragDropModifiers.Control))
        {
            result |= DropModifiers.Ctrl;
        }

        if (e.Modifiers.HasFlag(DragDropModifiers.Alt))
        {
            result |= DropModifiers.Alt;
        }

        return result;
    }

    /// <summary>
    /// Judges the drop of the current drag on <paramref name="destination"/> and tells the drag source: the operation that
    /// would happen and the caption that says so (spec §8.4: "a tooltip always says which one will happen").
    /// </summary>
    internal static DropDecision Over(DragEventArgs e, LocationPath destination)
    {
        var decision = DropRules.Decide(Current, destination, ModifiersOf(e));
        e.AcceptedOperation = decision.Allowed
            ? decision.Mode switch
            {
                TransferMode.Move => DataPackageOperation.Move,
                TransferMode.Link => DataPackageOperation.Link,
                _ => DataPackageOperation.Copy,
            }
            : DataPackageOperation.None;
        e.DragUIOverride.Caption = decision.Tooltip;
        e.DragUIOverride.IsCaptionVisible = true;
        e.DragUIOverride.IsGlyphVisible = decision.Allowed;
        return decision;
    }

    /// <summary>The locations dropped: our own drag, or the storage items of another application.</summary>
    internal static async Task<IReadOnlyList<LocationPath>> SourcesAsync(DragEventArgs e)
    {
        if (Current is { } own)
        {
            return own;
        }

        if (!e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            return [];
        }

        var items = await e.DataView.GetStorageItemsAsync();
        var paths = new List<LocationPath>(items.Count);
        foreach (var item in items)
        {
            if (!string.IsNullOrEmpty(item.Path) && LocationPath.TryParse(item.Path, out var path))
            {
                paths.Add(path);
            }
        }

        return paths;
    }

    private static async Task<IReadOnlyList<IStorageItem>> ToStorageItemsAsync(List<LocationPath> locations, IReadOnlyList<FileEntry> entries)
    {
        var items = new List<IStorageItem>(locations.Count);
        foreach (var entry in entries.Where(e => locations.Contains(e.Location)))
        {
            var path = entry.Location.Value;

            // A link may point at a folder; ask the disk off the UI thread rather than trust the listing.
            var isFolder = entry.Kind == FileEntryKind.Folder || (entry.Kind == FileEntryKind.Link && await Task.Run(() => Directory.Exists(path)));
            items.Add(isFolder
                ? await StorageFolder.GetFolderFromPathAsync(path)
                : await StorageFile.GetFileFromPathAsync(path));
        }

        return items;
    }
}
