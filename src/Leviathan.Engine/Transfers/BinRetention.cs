namespace Leviathan.Engine.Transfers;

/// <summary>
/// Which soft-deleted items the automatic purge removes (spec §12.5): everything older than the age limit, then the oldest of what
/// is left until the bin is under the size limit. Pure, so the rule is tested without a disk.
/// </summary>
public static class BinRetention
{
    /// <summary>The default age limit, in days.</summary>
    public const int DefaultDays = 30;

    /// <summary>The default size limit, in GiB (0 would mean none).</summary>
    public const int DefaultGiB = 100;

    /// <summary>
    /// The items to purge. A limit of zero or less is off. An item whose size is not known counts as zero toward the size limit, so a
    /// folder that was not measured is purged by age but never by size alone.
    /// </summary>
    public static IReadOnlyList<BinItem> Expired(IReadOnlyList<BinItem> items, DateTimeOffset now, int maxAgeDays, long maxBytes)
    {
        ArgumentNullException.ThrowIfNull(items);
        var purge = new List<BinItem>();
        var keep = new List<BinItem>();
        foreach (var item in items)
        {
            if (maxAgeDays > 0 && now - item.DeletedAt >= TimeSpan.FromDays(maxAgeDays))
            {
                purge.Add(item);
            }
            else
            {
                keep.Add(item);
            }
        }

        if (maxBytes > 0)
        {
            var total = keep.Sum(i => Math.Max(0, i.SizeBytes));
            foreach (var oldest in keep.OrderBy(i => i.DeletedAt))
            {
                if (total <= maxBytes)
                {
                    break;
                }

                purge.Add(oldest);
                total -= Math.Max(0, oldest.SizeBytes);
            }
        }

        return purge;
    }
}
