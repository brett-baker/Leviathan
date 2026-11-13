using Leviathan.Contracts;
using Leviathan.Core.Archives;
using Leviathan.Core.Paths;
using Leviathan.ViewModels.Transfers;

namespace Leviathan.ViewModels.Archives;

/// <summary>What the user chose in the "Compress with options" dialog (spec §15.4).</summary>
/// <param name="FileName">The archive's file name, with its .zip or .7z extension.</param>
/// <param name="Format">Zip or 7z.</param>
/// <param name="Level">How hard to compress.</param>
/// <param name="Password">A password, or null for none.</param>
public sealed record CompressChoice(string FileName, ArchiveFormat Format, ArchiveLevel Level, string? Password);

/// <summary>
/// Starts extract and compress jobs (spec §15.4, §12.3). The seam to the worker: the window decides <em>what</em> to pack or unpack
/// and where; the worker does it, journaled, with progress in the Operations panel. Both return once the job is queued.
/// </summary>
public interface IArchiveService
{
    /// <summary>Queues a job that unpacks <paramref name="archives"/> into <paramref name="destination"/> (a folder named after each archive inside it when <paramref name="intoSubfolder"/>).</summary>
    Task<TransferOutcome> ExtractAsync(IReadOnlyList<LocationPath> archives, LocationPath destination, bool intoSubfolder, CancellationToken cancellationToken);

    /// <summary>Queues a job that packs <paramref name="items"/> into the new archive file <paramref name="archive"/>.</summary>
    Task<TransferOutcome> CompressAsync(IReadOnlyList<LocationPath> items, LocationPath archive, CompressChoice choice, CancellationToken cancellationToken);
}

/// <summary>Names and rules for the compress and extract commands (spec §15.4). Pure, so the choices are tested.</summary>
public static class ArchiveNaming
{
    /// <summary>
    /// The archive name offered for <paramref name="items"/>: one item gives its own name (without a file extension), several give the
    /// name of the folder they are in; <paramref name="extension"/> is ".zip" or ".7z".
    /// </summary>
    public static string Suggest(IReadOnlyList<LocationPath> items, string extension)
    {
        ArgumentNullException.ThrowIfNull(items);
        if (items.Count == 0)
        {
            return "Archive" + extension;
        }

        var stem = items.Count == 1 ? StemOf(items[0]) : items[0].Parent?.Name ?? "Archive";
        return CleanName(stem) + extension;
    }

    /// <summary>Why the command cannot run on <paramref name="items"/> (a message for the user), or null when it can.</summary>
    public static string? WhyNotCompress(IReadOnlyList<LocationPath> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        if (items.Count == 0)
        {
            return "Select something to compress.";
        }

        if (items.Any(i => i.Kind is not (LocationKind.Drive or LocationKind.Unc) || i.IsRoot))
        {
            return "Only files and folders on this PC or on a Windows share can be compressed.";
        }

        return items.Any(ArchiveKinds.IsInside) ? "Extract items out of an archive before compressing them." : null;
    }

    /// <summary>Why the extract command cannot run on <paramref name="archives"/>, or null when it can.</summary>
    public static string? WhyNotExtract(IReadOnlyList<LocationPath> archives)
    {
        ArgumentNullException.ThrowIfNull(archives);
        if (archives.Count == 0)
        {
            return "Select an archive to extract.";
        }

        if (archives.Any(a => a.Kind is not (LocationKind.Drive or LocationKind.Unc) || ArchiveKinds.IsInside(a) || !ArchiveKinds.IsBrowsableName(a.Name)))
        {
            return "Only archive files on this PC or on a Windows share can be extracted.";
        }

        return null;
    }

    /// <summary>The format a file name asks for: .7z is 7-Zip, anything else a zip.</summary>
    public static ArchiveFormat FormatOf(string fileName) => fileName.EndsWith(".7z", StringComparison.OrdinalIgnoreCase) ? ArchiveFormat.SevenZip : ArchiveFormat.Zip;

    /// <summary>Gives <paramref name="fileName"/> the extension of <paramref name="format"/>, replacing a .zip or .7z it already has.</summary>
    public static string WithExtension(string fileName, ArchiveFormat format)
    {
        var wanted = format == ArchiveFormat.SevenZip ? ".7z" : ".zip";
        var name = fileName.Trim();
        if (name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".7z", StringComparison.OrdinalIgnoreCase))
        {
            name = name[..name.LastIndexOf('.')];
        }

        return CleanName(name) + wanted;
    }

    private static string StemOf(LocationPath item)
    {
        var name = item.Name;
        var dot = name.LastIndexOf('.');
        return dot > 0 ? name[..dot] : name;
    }

    private static string CleanName(string name)
    {
        var cleaned = string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)).Trim().TrimEnd('.');
        return cleaned.Length == 0 ? "Archive" : cleaned;
    }
}
