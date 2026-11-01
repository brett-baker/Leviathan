using System.Globalization;
using Leviathan.Core.Model;

namespace Leviathan.ViewModels.Folders;

/// <summary>How sizes and dates read in the file view and the status bar. Pure functions so they can be tested and used from x:Bind.</summary>
public static class DisplayFormat
{
    private static readonly string[] Units = ["bytes", "KB", "MB", "GB", "TB", "PB"];

    /// <summary>"0 bytes", "512 bytes", "1.5 KB", "5.24 GB": binary units like Explorer, three significant digits.</summary>
    public static string Size(long bytes)
    {
        if (bytes < 0)
        {
            return string.Empty;
        }

        if (bytes < 1024)
        {
            return bytes == 1 ? "1 byte" : $"{bytes} bytes";
        }

        var value = (double)bytes;
        var unit = 0;
        while (value >= 1024 && unit < Units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        var text = value switch
        {
            >= 100 => value.ToString("0", CultureInfo.CurrentCulture),
            >= 10 => value.ToString("0.#", CultureInfo.CurrentCulture),
            _ => value.ToString("0.##", CultureInfo.CurrentCulture),
        };
        return $"{text} {Units[unit]}";
    }

    /// <summary>The Size column: files show their size, folders show nothing until the folder-size service has a value.</summary>
    public static string SizeColumn(FileEntry entry) =>
        entry.Kind == FileEntryKind.File ? Size(entry.Size) : string.Empty;

    /// <summary>"2026-11-01 14:03" in the local time zone, the same everywhere in the app.</summary>
    public static string Date(DateTimeOffset value, TimeZoneInfo? zone = null) =>
        TimeZoneInfo.ConvertTime(value, zone ?? TimeZoneInfo.Local).ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

    /// <summary>"1,284 items" or "1 item".</summary>
    public static string ItemCount(int count) =>
        count == 1 ? "1 item" : string.Create(CultureInfo.CurrentCulture, $"{count:N0} items");

    /// <summary>Status bar text: "1,284 items", or "23 selected · 41.7 GB" when something is selected (spec §16).</summary>
    public static string StatusText(int total, int shown, int selected, long selectedBytes)
    {
        var items = shown == total ? ItemCount(total) : string.Create(CultureInfo.CurrentCulture, $"{shown:N0} of {total:N0} items");
        if (selected == 0)
        {
            return items;
        }

        var selection = selectedBytes > 0 ? $"{selected:N0} selected · {Size(selectedBytes)}" : $"{selected:N0} selected";
        return $"{items} · {selection}";
    }
}
