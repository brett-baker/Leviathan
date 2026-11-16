// View models are UI-thread affine: the awaits in this file deliberately resume on the caller's context (crash signature
// 52a0ec04c34d, see WindowCommands.cs). UiThreadAffinityTests guards this.
#pragma warning disable CA2007

using Leviathan.Core.Model;
using Leviathan.Core.Paths;
using Leviathan.ViewModels.Navigation;
using Leviathan.ViewModels.Windows;

namespace Leviathan.ViewModels.Commands.Areas;

/// <summary>Extract, compress and checksums, all worker jobs (spec §15.4).</summary>
internal sealed class ArchiveCommands(CommandScope scope) : CommandArea(scope)
{
    /// <summary>Registers this area's commands.</summary>
    public void Register()
    {
        var shell = Scope.Shell;
        var archives = Scope.ArchiveService;

        // Archives (spec §15.4): extract and compress are worker jobs. The items are the one the command was run on, else the selection.
        IReadOnlyList<LocationPath> ArchiveTargets(CommandContext c) =>
            c.Parameter is FileEntry one ? [one.Location] : Current() is { } here ? [.. here.Selection.SelectedEntries.Select(e => e.Location)] : [];
        async Task RunExtract(CommandContext c, CancellationToken ct, bool subfolder, bool pick)
        {
            var items = ArchiveTargets(c);
            var why = archives is null ? "Archives are not available yet." : Archives.ArchiveNaming.WhyNotExtract(items);
            if (archives is null || why is not null)
            {
                shell.ShowMessage(why!);
                return;
            }

            LocationPath? destination = items[0].Parent;
            if (pick)
            {
                destination = await shell.PickFolderAsync("Extract to");
            }

            if (destination is null)
            {
                return;
            }

            var outcome = await archives.ExtractAsync(items, destination, subfolder, ct);
            if (!outcome.Started)
            {
                shell.ShowMessage(outcome.Message);
            }
        }

        async Task RunCompress(CommandContext c, CancellationToken ct, bool ask)
        {
            var items = ArchiveTargets(c);
            var why = archives is null ? "Archives are not available yet." : Archives.ArchiveNaming.WhyNotCompress(items);
            if (archives is null || why is not null)
            {
                shell.ShowMessage(why!);
                return;
            }

            var choice = new Archives.CompressChoice(Archives.ArchiveNaming.Suggest(items, ".zip"), Contracts.ArchiveFormat.Zip, Contracts.ArchiveLevel.Normal, null);
            if (ask)
            {
                if (await shell.AskCompressAsync(choice.FileName) is not { } chosen)
                {
                    return;
                }

                choice = chosen with { FileName = Archives.ArchiveNaming.WithExtension(chosen.FileName, chosen.Format) };
            }

            var folder = items[0].Parent;
            if (folder is null)
            {
                return;
            }

            var outcome = await archives.CompressAsync(items, folder.Combine(choice.FileName), choice, ct);
            if (!outcome.Started)
            {
                shell.ShowMessage(outcome.Message);
            }
        }

        // Checksums (spec §15.4): of the item the command was run on, else the selection; a lone checksum file (.sha256, .md5, .sfv...) is verified.
        Add(CommandIds.ShowChecksum, "Checksums...", "Files", (c, _) => Run(() =>
        {
            var items = ArchiveTargets(c);
            if (items.Count == 0 || items.Any(i => i.Kind is not (LocationKind.Drive or LocationKind.Unc)))
            {
                shell.ShowMessage("Checksums can be made for files and folders on this PC and on Windows shares.");
                return;
            }

            var verify = items.All(i => Core.Hashing.ChecksumKinds.FromExtension(Path.GetExtension(i.Name)) is not null);
            shell.OpenChecksums(items, verify);
        }), c => Current() is not null);

        bool CanArchive(CommandContext c) => archives is not null && Current() is not null;
        Add(CommandIds.ExtractHere, "Extract here", "Archives", (c, ct) => RunExtract(c, ct, subfolder: false, pick: false), CanArchive);
        Add(CommandIds.ExtractToFolder, "Extract to folder", "Archives", (c, ct) => RunExtract(c, ct, subfolder: true, pick: false), CanArchive);
        Add(CommandIds.ExtractTo, "Extract to...", "Archives", (c, ct) => RunExtract(c, ct, subfolder: false, pick: true), CanArchive);
        Add(CommandIds.Compress, "Compress", "Archives", (c, ct) => RunCompress(c, ct, ask: false), CanArchive);
        Add(CommandIds.CompressWith, "Compress with options...", "Archives", (c, ct) => RunCompress(c, ct, ask: true), CanArchive);
    }
}
