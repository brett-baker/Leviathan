using System.Globalization;
using Leviathan.Core.Paths;
using Leviathan.Core.Platform;
using Leviathan.ViewModels.Folders;

namespace Leviathan.ViewModels.StatusBar;

/// <summary>The words of each part of the status bar (spec §16). Pure functions, so every wording is unit-tested.</summary>
public static class StatusBarText
{
    /// <summary>
    /// "1,284 items (37 folders, 1,247 files) · 12 hidden". Filtering is reported by <see cref="Filter"/>, so this always counts the
    /// whole folder. Hidden items are only counted when the listing included them.
    /// </summary>
    public static string Items(int total, int folders, int hidden)
    {
        if (total == 0)
        {
            return "0 items";
        }

        var files = total - folders;
        var text = string.Create(CultureInfo.CurrentCulture, $"{DisplayFormat.ItemCount(total)} ({Plural(folders, "folder")}, {Plural(files, "file")})");
        return hidden > 0 ? string.Create(CultureInfo.CurrentCulture, $"{text} · {hidden:N0} hidden") : text;
    }

    /// <summary>"23 selected · 41.7 GB", or empty when nothing is selected.</summary>
    public static string Selection(int selected, long selectedBytes)
    {
        if (selected == 0)
        {
            return string.Empty;
        }

        return selectedBytes > 0
            ? string.Create(CultureInfo.CurrentCulture, $"{selected:N0} selected · {DisplayFormat.Size(selectedBytes)}")
            : string.Create(CultureInfo.CurrentCulture, $"{selected:N0} selected");
    }

    /// <summary>"Filtered: *.mkv (312 of 1,284)", or empty when no filter is set.</summary>
    public static string Filter(string? filterText, int shown, int total) =>
        string.IsNullOrWhiteSpace(filterText)
            ? string.Empty
            : string.Create(CultureInfo.CurrentCulture, $"Filtered: {filterText.Trim()} ({shown:N0} of {total:N0})");

    /// <summary>"2.1 TB free of 16 TB", or empty when the drive's size is not known.</summary>
    public static string FreeSpace(DriveDetails? drive) =>
        drive is { HasSizes: true } ? $"{DisplayFormat.Size(drive.FreeBytes)} free of {DisplayFormat.Size(drive.TotalBytes)}" : string.Empty;

    /// <summary>
    /// The place a network folder lives on and whether it answers: "\\nas\media". Local folders show nothing. Protocol version and
    /// round-trip time arrive with the share health tracker's measurements (Phase 2).
    /// </summary>
    public static string Health(LocationPath location, bool offline)
    {
        ArgumentNullException.ThrowIfNull(location);
        if (!location.IsNetwork)
        {
            return string.Empty;
        }

        return offline ? $"{location.Root} · not responding" : location.Root;
    }

    /// <summary>The drive of <paramref name="drives"/> that holds <paramref name="location"/>, or null (network shares, shell folders).</summary>
    public static DriveDetails? DriveOf(LocationPath location, IEnumerable<DriveDetails> drives)
    {
        ArgumentNullException.ThrowIfNull(location);
        ArgumentNullException.ThrowIfNull(drives);
        return drives.FirstOrDefault(d => d.Root.Kind == location.Kind && string.Equals(d.Root.Root, location.Root, StringComparison.OrdinalIgnoreCase));
    }

    private static string Plural(int count, string noun) =>
        string.Create(CultureInfo.CurrentCulture, $"{count:N0} {noun}{(count == 1 ? string.Empty : "s")}");
}
