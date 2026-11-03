namespace Leviathan.ViewModels.Folders;

/// <summary>What a <see cref="ListingNotice"/> is about.</summary>
public enum ListingNoticeKind
{
    /// <summary>Rows came from the cache and the real folder is being listed to check them: they may still change.</summary>
    Checking,

    /// <summary>The check ended and changed the rows.</summary>
    Updated,

    /// <summary>The check could not be done; the rows are the cached ones.</summary>
    RefreshFailed,
}

/// <summary>
/// A line for the status bar about a folder shown from the cache (spec §3.3). Rows that appear instantly were remembered, not just
/// listed, so the window says while it checks them, what the check changed, and when it could not check.
/// </summary>
/// <param name="Kind">What it is about.</param>
/// <param name="Text">The words.</param>
public sealed record ListingNotice(ListingNoticeKind Kind, string Text)
{
    /// <summary>How long "Updated: ..." stays up.</summary>
    public static readonly TimeSpan UpdatedShownFor = TimeSpan.FromSeconds(6);

    /// <summary>Shown while the real folder is being listed behind a cached listing.</summary>
    public static ListingNotice Checking(CacheSource source, TimeSpan age) => new(
        ListingNoticeKind.Checking,
        source == CacheSource.Disk
            ? $"Showing the saved listing from {Age(age)} while the folder is checked; items may change"
            : $"Showing the listing from {Age(age)} while the folder is checked; items may change");

    /// <summary>Shown when the check changed something.</summary>
    public static ListingNotice Updated(string what) => new(ListingNoticeKind.Updated, $"Updated: {what}");

    /// <summary>Shown when the folder could not be checked.</summary>
    public static ListingNotice RefreshFailed(string reason, TimeSpan age) => new(
        ListingNoticeKind.RefreshFailed,
        $"Could not check the folder ({reason.TrimEnd('.')}); showing the listing from {Age(age)}");

    /// <summary>"just now", "40 seconds ago", "5 minutes ago", "3 hours ago", "4 days ago".</summary>
    public static string Age(TimeSpan age)
    {
        if (age < TimeSpan.FromSeconds(5))
        {
            return "just now";
        }

        return age.TotalSeconds < 60 ? $"{(int)age.TotalSeconds} seconds ago"
            : age.TotalMinutes < 60 ? Plural((int)age.TotalMinutes, "minute")
            : age.TotalHours < 48 ? Plural((int)age.TotalHours, "hour")
            : Plural((int)age.TotalDays, "day");
    }

    private static string Plural(int count, string unit) => $"{count} {unit}{(count == 1 ? string.Empty : "s")} ago";
}
