using Leviathan.Contracts;
using Leviathan.Core.Paths;
using Leviathan.ViewModels.Folders;
using Leviathan.ViewModels.Transfers;

namespace Leviathan.ViewModels.Hub;

/// <summary>The seam to the worker for the Soft-delete bin (spec §12.5): the window decides what and confirms, the worker does it.</summary>
public interface IBinService
{
    /// <summary>Everything in the bin across shares; folder sizes are not added up.</summary>
    Task<BinListing> ListAsync(CancellationToken cancellationToken);

    /// <summary>Puts the items back where they were deleted from; queues one restore job.</summary>
    Task<TransferOutcome> RestoreAsync(IReadOnlyList<BinEntryInfo> items, CancellationToken cancellationToken);

    /// <summary>Deletes the items for good; queues one purge job. The caller has confirmed.</summary>
    Task<TransferOutcome> PurgeAsync(IReadOnlyList<BinEntryInfo> items, CancellationToken cancellationToken);

    /// <summary>Applies the retention rule now (older than the age limit, then over the size limit); queues one purge job.</summary>
    Task<TransferOutcome> PurgeOldAsync(CancellationToken cancellationToken);
}

/// <summary>One row of the Soft-delete bin.</summary>
/// <param name="Entry">The item as the worker listed it.</param>
/// <param name="Name">Its name.</param>
/// <param name="Where">The folder it was deleted from.</param>
/// <param name="Deleted">How long ago, in words.</param>
/// <param name="Size">Its size, "Folder" for a folder whose size is not known.</param>
public sealed record BinRow(BinEntryInfo Entry, string Name, string Where, string Deleted, string Size);

/// <summary>
/// What the Soft-delete bin tab of the status hub shows (spec §12.5, §16.1): every soft-deleted item across shares, newest first,
/// with a line saying how much there is and which shares could not be read. Reading is the worker's, so this never touches a share.
/// </summary>
public sealed class BinModel
{
    private readonly IBinService _service;
    private readonly TimeProvider _time;
    private readonly Lock _lock = new();
    private IReadOnlyList<BinEntryInfo> _items = [];
    private IReadOnlyList<string> _unreachable = [];
    private string? _problem;

    /// <summary>Creates the model over <paramref name="service"/>.</summary>
    public BinModel(IBinService service, TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(service);
        _service = service;
        _time = time ?? TimeProvider.System;
    }

    /// <summary>Raised when the rows or the summary changed. May come from any thread the caller awaited on.</summary>
    public event EventHandler? Changed;

    /// <summary>True while the bin is being read.</summary>
    public bool IsLoading { get; private set; }

    /// <summary>Reads the bin again. A failure shows as a line instead of an empty bin, so an unreadable bin is never mistaken for a clean one.</summary>
    public async Task RefreshAsync(CancellationToken cancellationToken)
    {
        IsLoading = true;
        Changed?.Invoke(this, EventArgs.Empty);
        try
        {
            var listing = await _service.ListAsync(cancellationToken).ConfigureAwait(true);
            lock (_lock)
            {
                _items = listing.Items;
                _unreachable = listing.Unreachable;
                _problem = null;
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or InvalidOperationException)
        {
            lock (_lock)
            {
                _problem = $"The bin could not be read: {ex.Message}";
            }
        }
        finally
        {
            IsLoading = false;
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>The rows, newest deletion first; <paramref name="filter"/> keeps those whose name or original folder contains it.</summary>
    public IReadOnlyList<BinRow> Rows(string? filter = null)
    {
        IReadOnlyList<BinEntryInfo> items;
        lock (_lock)
        {
            items = _items;
        }

        var now = _time.GetUtcNow();
        var text = filter?.Trim();
        return [.. items
            .Select(e => ToRow(e, now))
            .Where(r => string.IsNullOrEmpty(text) || r.Name.Contains(text, StringComparison.OrdinalIgnoreCase) || r.Where.Contains(text, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(r => r.Entry.DeletedAt)];
    }

    /// <summary>The line above the list: how much is in the bin, and any problem reading it.</summary>
    public string Summary()
    {
        IReadOnlyList<BinEntryInfo> items;
        IReadOnlyList<string> unreachable;
        string? problem;
        lock (_lock)
        {
            items = _items;
            unreachable = _unreachable;
            problem = _problem;
        }

        if (problem is not null)
        {
            return problem;
        }

        var parts = new List<string>();
        if (items.Count == 0)
        {
            parts.Add("Nothing deleted from a network share is waiting here.");
        }
        else
        {
            var known = items.Where(i => i.SizeBytes >= 0).Sum(i => i.SizeBytes);
            var partial = items.Any(i => i.SizeBytes < 0);
            parts.Add($"{items.Count:N0} {(items.Count == 1 ? "item" : "items")}");
            if (known > 0 || !partial)
            {
                parts.Add((partial ? "at least " : string.Empty) + DisplayFormat.Size(known));
            }
        }

        if (unreachable.Count > 0)
        {
            parts.Add($"{unreachable.Count} trash {(unreachable.Count == 1 ? "folder" : "folders")} could not be read ({string.Join(", ", unreachable.Select(ShortName))})");
        }

        return string.Join(" · ", parts);
    }

    /// <summary>Puts the rows' items back where they were deleted from.</summary>
    public Task<TransferOutcome> RestoreAsync(IReadOnlyList<BinRow> rows, CancellationToken cancellationToken) =>
        rows.Count == 0
            ? Task.FromResult(new TransferOutcome(false, "Pick something to restore."))
            : _service.RestoreAsync([.. rows.Select(r => r.Entry)], cancellationToken);

    /// <summary>Deletes the rows' items for good (the window has asked first).</summary>
    public Task<TransferOutcome> PurgeAsync(IReadOnlyList<BinRow> rows, CancellationToken cancellationToken) =>
        rows.Count == 0
            ? Task.FromResult(new TransferOutcome(false, "Pick something to delete."))
            : _service.PurgeAsync([.. rows.Select(r => r.Entry)], cancellationToken);

    /// <summary>Applies the retention rule now.</summary>
    public Task<TransferOutcome> PurgeOldAsync(CancellationToken cancellationToken) => _service.PurgeOldAsync(cancellationToken);

    /// <summary>"just now", "5 minutes ago", "3 days ago", or the date once it is over a month.</summary>
    public static string AgeText(DateTimeOffset deleted, DateTimeOffset now)
    {
        var age = now - deleted;
        if (age < TimeSpan.FromMinutes(1))
        {
            return "just now";
        }

        if (age < TimeSpan.FromHours(1))
        {
            return Plural((int)age.TotalMinutes, "minute");
        }

        if (age < TimeSpan.FromDays(1))
        {
            return Plural((int)age.TotalHours, "hour");
        }

        return age < TimeSpan.FromDays(31) ? Plural((int)age.TotalDays, "day") : deleted.ToLocalTime().ToString("d MMM yyyy", System.Globalization.CultureInfo.CurrentCulture);
    }

    private static string Plural(int count, string unit) => $"{count} {unit}{(count == 1 ? string.Empty : "s")} ago";

    private static BinRow ToRow(BinEntryInfo e, DateTimeOffset now)
    {
        var name = LocationPath.TryParse(e.Trashed, out var trashed) ? trashed.Name : e.Trashed;
        var where = LocationPath.TryParse(e.Original, out var original) && original.Parent is { } parent ? parent.Value : e.Original;
        var size = e.SizeBytes >= 0 ? DisplayFormat.Size(e.SizeBytes) : (e.IsFolder ? "Folder" : string.Empty);
        return new BinRow(e, name, where, AgeText(e.DeletedAt, now), size);
    }

    // \\nas\media\.leviathan-trash is shown as \\nas\media.
    private static string ShortName(string root) =>
        LocationPath.TryParse(root, out var trash) && trash.Parent is { } parent ? parent.Value : root;
}
