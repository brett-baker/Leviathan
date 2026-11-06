using System.Globalization;
using Leviathan.Core.Model;
using Leviathan.Core.Text;

namespace Leviathan.ViewModels.Folders;

/// <summary>A group a row falls into. Groups sort by <see cref="Order"/>, then <see cref="Key"/> in natural order.</summary>
/// <param name="Order">Position for ranged groups such as size and date; 0 for text groups.</param>
/// <param name="Key">Stable key, also the sort fallback.</param>
/// <param name="Title">Heading shown to the user.</param>
public readonly record struct GroupBucket(int Order, string Key, string Title);

/// <summary>One column of the Details view that can sort and group (spec §8.2, §8.3).</summary>
/// <param name="Id">Stable id stored in view settings.</param>
/// <param name="Title">Header text.</param>
/// <param name="Compare">Ascending comparison. Returns 0 for ties; the view adds name and path tie-breakers.</param>
/// <param name="Group">Maps an entry to its group at <c>now</c> (local time), or null when the column cannot group.</param>
/// <param name="Metric">The folder-size number the column shows for folders; sorting then asks the folder-size service for it.</param>
public sealed record FileColumn(string Id, string Title, Comparison<FileEntry> Compare, Func<FileEntry, DateTimeOffset, TimeZoneInfo, GroupBucket>? Group, FolderMetric? Metric = null);

/// <summary>The file-system columns. Media, image and tag columns are added with the data that feeds them (Phase 3).</summary>
public static class FileColumns
{
    private const long Kilobyte = 1024;
    private const long Megabyte = Kilobyte * 1024;
    private const long Gigabyte = Megabyte * 1024;

    /// <summary>Name, natural order (file2 before file10).</summary>
    public static FileColumn Name { get; } = new("name", "Name",
        (a, b) => NaturalStringComparer.Instance.Compare(a.Name, b.Name),
        (e, _, _) => NameBucket(e));

    /// <summary>Extension.</summary>
    public static FileColumn Extension { get; } = new("extension", "Extension",
        (a, b) => string.Compare(a.Extension, b.Extension, StringComparison.OrdinalIgnoreCase),
        (e, _, _) => TypeBucket(e));

    /// <summary>Type: folders, then by extension.</summary>
    public static FileColumn Type { get; } = new("type", "Type",
        (a, b) => string.Compare(TypeName(a), TypeName(b), StringComparison.OrdinalIgnoreCase),
        (e, _, _) => TypeBucket(e));

    /// <summary>Size in bytes. A folder's total comes from the folder-size service; the view sorts by it (<see cref="FolderMetric.TotalBytes"/>).</summary>
    public static FileColumn Size { get; } = new("size", "Size", (a, b) => a.Size.CompareTo(b.Size), (e, _, _) => SizeBucket(e), FolderMetric.TotalBytes);

    /// <summary>Bytes in the files directly inside a folder, nothing deeper.</summary>
    public static FileColumn OwnSize { get; } = new("ownsize", "Own size", (a, b) => 0, null, FolderMetric.OwnBytes);

    /// <summary>Files and folders directly inside a folder.</summary>
    public static FileColumn Items { get; } = new("items", "Items", (a, b) => 0, null, FolderMetric.Items);

    /// <summary>Files at every depth below a folder.</summary>
    public static FileColumn TotalFiles { get; } = new("totalfiles", "Total files", (a, b) => 0, null, FolderMetric.TotalFiles);

    /// <summary>Folders at every depth below a folder.</summary>
    public static FileColumn TotalFolders { get; } = new("totalfolders", "Total folders", (a, b) => 0, null, FolderMetric.TotalFolders);

    /// <summary>Last write time.</summary>
    public static FileColumn Modified { get; } = new("modified", "Date modified", (a, b) => a.Modified.CompareTo(b.Modified),
        (e, now, zone) => DateBucket(e.Modified, now, zone));

    /// <summary>Creation time.</summary>
    public static FileColumn Created { get; } = new("created", "Date created", (a, b) => a.Created.CompareTo(b.Created),
        (e, now, zone) => DateBucket(e.Created, now, zone));

    /// <summary>File-system attributes.</summary>
    public static FileColumn Attributes { get; } = new("attributes", "Attributes", (a, b) => ((int)a.Attributes).CompareTo((int)b.Attributes), null);

    /// <summary>Tags. Sorts as equal until the tag index exists (Phase 3).</summary>
    public static FileColumn Tags { get; } = new("tags", "Tags", (_, _) => 0, null);

    /// <summary>Every column.</summary>
    public static IReadOnlyList<FileColumn> All { get; } = [Name, Extension, Type, Size, OwnSize, Items, TotalFiles, TotalFolders, Modified, Created, Attributes, Tags];

    /// <summary>Finds a column by id (case-insensitive).</summary>
    public static bool TryGet(string id, out FileColumn column)
    {
        column = All.FirstOrDefault(c => c.Id.Equals(id, StringComparison.OrdinalIgnoreCase))!;
        return column is not null;
    }

    /// <summary>True for folders and for links that point at folders.</summary>
    public static bool IsFolderLike(FileEntry entry) =>
        entry.Kind == FileEntryKind.Folder || (entry.Kind == FileEntryKind.Link && entry.Attributes.HasFlag(FileAttributes.Directory));

    /// <summary>"Folder", "File" or "MKV file": what the Type column shows.</summary>
    public static string TypeName(FileEntry entry) =>
        IsFolderLike(entry) ? "Folder" : entry.Extension.Length == 0 ? "File" : entry.Extension.TrimStart('.').ToUpperInvariant() + " file";

    private static GroupBucket NameBucket(FileEntry entry)
    {
        var first = entry.Name.Length > 0 ? char.ToUpperInvariant(entry.Name[0]) : '#';
        return char.IsLetter(first) ? new GroupBucket(0, first.ToString(), first.ToString()) : new GroupBucket(-1, "#", "#");
    }

    private static GroupBucket TypeBucket(FileEntry entry)
    {
        if (IsFolderLike(entry))
        {
            return new GroupBucket(-1, "Folder", "Folders");
        }

        var extension = entry.Extension.TrimStart('.').ToLowerInvariant();
        return extension.Length == 0 ? new GroupBucket(1, "", "No extension") : new GroupBucket(0, extension, extension.ToUpperInvariant() + " files");
    }

    private static GroupBucket SizeBucket(FileEntry entry)
    {
        if (IsFolderLike(entry))
        {
            return new GroupBucket(6, "folder", "Folders");
        }

        return entry.Size switch
        {
            0 => new GroupBucket(0, "empty", "Empty"),
            < 16 * Kilobyte => new GroupBucket(1, "tiny", "Tiny (under 16 KB)"),
            < Megabyte => new GroupBucket(2, "small", "Small (under 1 MB)"),
            < 128 * Megabyte => new GroupBucket(3, "medium", "Medium (under 128 MB)"),
            < Gigabyte => new GroupBucket(4, "large", "Large (under 1 GB)"),
            _ => new GroupBucket(5, "huge", "Huge (1 GB and over)"),
        };
    }

    private static GroupBucket DateBucket(DateTimeOffset value, DateTimeOffset now, TimeZoneInfo zone)
    {
        var date = TimeZoneInfo.ConvertTime(value, zone).Date;
        var today = TimeZoneInfo.ConvertTime(now, zone).Date;
        var daysAgo = (today - date).Days;
        var weekStart = today.AddDays(-(((int)today.DayOfWeek + 6) % 7));
        var lastWeekStart = weekStart.AddDays(-7);
        var monthStart = new DateTime(today.Year, today.Month, 1);
        var yearStart = new DateTime(today.Year, 1, 1);

        if (date > today)
        {
            return new GroupBucket(-1, "future", "Later");
        }

        if (daysAgo == 0)
        {
            return new GroupBucket(0, "today", "Today");
        }

        if (daysAgo == 1)
        {
            return new GroupBucket(1, "yesterday", "Yesterday");
        }

        if (date >= weekStart)
        {
            return new GroupBucket(2, "week", "Earlier this week");
        }

        if (date >= lastWeekStart)
        {
            return new GroupBucket(3, "lastweek", "Last week");
        }

        if (date >= monthStart)
        {
            return new GroupBucket(4, "month", "Earlier this month");
        }

        if (date >= yearStart)
        {
            return new GroupBucket(5, "year", "Earlier this year");
        }

        // Older years, newest first.
        return new GroupBucket(100 + (9999 - date.Year), date.Year.ToString(CultureInfo.InvariantCulture), date.Year.ToString(CultureInfo.InvariantCulture));
    }
}
