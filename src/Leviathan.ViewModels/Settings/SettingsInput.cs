using System.Globalization;
using Leviathan.Search;

namespace Leviathan.ViewModels.Settings;

/// <summary>Reading and wording for the settings pages (spec §17.2), kept out of the controls so it is unit-tested.</summary>
public static class SettingsInput
{
    /// <summary>Reads "22:00" or "6:30" as a time of day; false for anything else, including 24:00 and negative values.</summary>
    public static bool TryParseTimeOfDay(string? text, out TimeSpan time)
    {
        time = default;
        if (!TimeSpan.TryParseExact((text ?? string.Empty).Trim(), ["h\\:mm", "hh\\:mm"], CultureInfo.InvariantCulture, out var parsed)
            || parsed < TimeSpan.Zero || parsed >= TimeSpan.FromDays(1))
        {
            return false;
        }

        time = parsed;
        return true;
    }

    /// <summary>"22:00" for a time of day.</summary>
    public static string FormatTimeOfDay(TimeSpan time) => time.ToString("hh\\:mm", CultureInfo.InvariantCulture);

    /// <summary>One line about a chosen index folder: crawling now, waiting and why, or when it was last listed and when it is due.</summary>
    public static string IndexStatus(IndexRootStatus status, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(status);
        if (status.IsCrawling)
        {
            return status.Progress is { } p ? $"Listing now: {p.FoldersDone:N0} folders, {p.ItemsSeen:N0} items" : "Listing now";
        }

        if (status.IsWaiting)
        {
            return status.Message is { Length: > 0 } why ? $"Waiting: {why}" : "Waiting its turn";
        }

        var parts = new List<string>
        {
            status.LastCrawled is { } last ? $"Listed {Ago(now - last)}" : "Not listed yet",
        };
        if (status.NextCrawl is { } next)
        {
            parts.Add(next <= now ? "due now" : $"next in {Span(next - now)}");
        }

        if (status.Message is { Length: > 0 } message)
        {
            parts.Add(message);
        }

        return string.Join(" · ", parts);
    }

    private static string Ago(TimeSpan age) => age < TimeSpan.FromMinutes(1) ? "just now" : Span(age) + " ago";

    private static string Span(TimeSpan span) =>
        span.TotalDays >= 1 ? $"{(int)span.TotalDays} d" : span.TotalHours >= 1 ? $"{(int)span.TotalHours} h" : $"{Math.Max(1, (int)span.TotalMinutes)} min";
}
