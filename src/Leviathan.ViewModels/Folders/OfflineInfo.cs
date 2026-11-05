using System.Globalization;

namespace Leviathan.ViewModels.Folders;

/// <summary>
/// The folder is shown from the offline cache because its share could not be reached (spec §3.3, §8.6): the rows are the last
/// listing, greyed, under a banner that says when it was made and offers Retry and Work offline (or Go online).
/// </summary>
/// <param name="Server">The server that did not answer.</param>
/// <param name="AsOf">When the listing on screen was made.</param>
/// <param name="WorkingOffline">True when the user chose to work offline, so the network was not tried at all.</param>
public sealed record OfflineInfo(string Server, DateTimeOffset AsOf, bool WorkingOffline)
{
    /// <summary>"\\nas is not responding — showing the listing as of 14:02", or the working-offline wording.</summary>
    public string Text(DateTimeOffset now, TimeZoneInfo? zone = null) => WorkingOffline
        ? $"Working offline with {Server} — showing the listing as of {AsOfText(now, zone)}"
        : $"{Server} is not responding — showing the listing as of {AsOfText(now, zone)}";

    /// <summary>"14:02" for today, "2026-11-03 14:02" for another day, in the local time zone.</summary>
    public string AsOfText(DateTimeOffset now, TimeZoneInfo? zone = null)
    {
        var local = zone ?? TimeZoneInfo.Local;
        var at = TimeZoneInfo.ConvertTime(AsOf, local);
        var today = TimeZoneInfo.ConvertTime(now, local).Date;
        return at.Date == today ? at.ToString("HH:mm", CultureInfo.InvariantCulture) : at.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
    }
}
