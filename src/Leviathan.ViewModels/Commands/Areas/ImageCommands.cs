// View models are UI-thread affine: the awaits in this file deliberately resume on the caller's context (crash signature
// 52a0ec04c34d, see WindowCommands.cs). UiThreadAffinityTests guards this.
#pragma warning disable CA2007

using Leviathan.Core.Model;
using Leviathan.Core.Paths;
using Leviathan.Core.Images;
using Leviathan.ViewModels.Tabs;

namespace Leviathan.ViewModels.Commands.Areas;

/// <summary>
/// Picture actions (spec §6.2): rotate, flip, resize and convert the selected pictures. Each saves a copy beside the original through
/// <see cref="IImageEditor"/>, so nothing is overwritten and nothing needs undoing; the copies are selected afterwards.
/// </summary>
internal sealed class ImageCommands(CommandScope scope) : CommandArea(scope)
{
    /// <summary>The largest and smallest resize, in percent.</summary>
    internal const int MinPercent = 1;

    /// <summary>The largest resize, in percent.</summary>
    internal const int MaxPercent = 400;

    /// <summary>Registers this area's commands.</summary>
    public void Register()
    {
        var editor = Scope.ImageEditor;
        var shell = Scope.Shell;

        static IReadOnlyList<LocationPath> Pictures(TabViewModel tab) =>
            tab.Selection.SelectedEntries.Where(e => e.Kind == FileEntryKind.File && ImageEditRules.CanEdit(e.Extension)).Select(e => e.Location).ToList();

        bool CanEdit(TabViewModel tab) =>
            editor is not null && LocationKinds.IsLocalOrShare(tab.History.Current.Kind) && tab.Selection.Count > 0 && Pictures(tab).Count == tab.Selection.Count;

        async Task EditAsync(TabViewModel tab, ImageEdit edit, CancellationToken ct)
        {
            var sources = Pictures(tab);
            if (sources.Count == 0)
            {
                shell.ShowMessage("Select a picture first.");
                return;
            }

            var made = new List<LocationPath>(sources.Count);
            string? failure = null;
            foreach (var source in sources)
            {
                try
                {
                    made.Add(await editor!.EditAsync(source, edit, ct));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    failure ??= $"{source.Name}: {ex.Message}";
                }
            }

            var verb = ImageEditRules.Verb(edit);
            shell.ShowMessage(failure is not null
                ? made.Count == 0 ? $"Could not edit the picture. {failure}" : $"{verb} {made.Count} of {sources.Count} pictures. {failure}"
                : made.Count == 1 ? $"{verb}: saved {made[0].Name}" : $"{verb}: saved {made.Count} new pictures");
            if (made.Count > 0)
            {
                await tab.ReloadAndSelectAsync(made, ct);
            }
        }

        void AddEdit(CommandId id, string title, ImageEdit edit) =>
            AddOnCurrent(id, title, "Image", (tab, _, ct) => EditAsync(tab, edit, ct), (tab, _) => CanEdit(tab));

        AddEdit(CommandIds.RotateLeft, "Rotate left", new ImageEdit(ImageEditKind.RotateLeft));
        AddEdit(CommandIds.RotateRight, "Rotate right", new ImageEdit(ImageEditKind.RotateRight));
        AddEdit(CommandIds.ImageFlipHorizontal, "Flip horizontally", new ImageEdit(ImageEditKind.FlipHorizontal));
        AddEdit(CommandIds.ImageFlipVertical, "Flip vertically", new ImageEdit(ImageEditKind.FlipVertical));

        AddOnCurrent(
            CommandIds.ImageResize,
            "Resize picture",
            "Image",
            (tab, c, ct) => EditAsync(tab, new ImageEdit(ImageEditKind.Resize, (int)c.Parameter!), ct),
            (tab, c) => CanEdit(tab) && c.Parameter is int and >= MinPercent and <= MaxPercent);
        AddOnCurrent(
            CommandIds.ImageConvertTo,
            "Convert picture",
            "Image",
            (tab, c, ct) => EditAsync(tab, new ImageEdit(ImageEditKind.Convert, Format: (ImageFormat)c.Parameter!), ct),
            (tab, c) => CanEdit(tab) && c.Parameter is ImageFormat);
    }
}
