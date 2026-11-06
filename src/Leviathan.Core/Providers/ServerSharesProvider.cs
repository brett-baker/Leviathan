using System.Runtime.CompilerServices;
using Leviathan.Core.Model;
using Leviathan.Core.Paths;

namespace Leviathan.Core.Providers;

/// <summary>A disk share a server offers.</summary>
/// <param name="Name">Share name, such as <c>media</c>.</param>
/// <param name="Comment">The server's remark about it, or null.</param>
public sealed record ShareInfo(string Name, string? Comment = null);

/// <summary>Asks a server which disk shares it offers. Behind an interface so the Win32 call can be faked (spec §18).</summary>
public interface IShareEnumerator
{
    /// <summary>
    /// The visible disk shares of <paramref name="server"/> (no printers, no administrative <c>$</c> shares). Throws
    /// <see cref="IOException"/> or <see cref="UnauthorizedAccessException"/> when the server cannot be asked; the caller classifies it.
    /// </summary>
    Task<IReadOnlyList<ShareInfo>> ListSharesAsync(string server, CancellationToken cancellationToken);
}

/// <summary>
/// Lists a server's shares as if they were folders, so <c>\\nas</c> opens in the file view and expands in the tree like a drive does
/// (spec §7, §7.1: "drives, servers and shares expand like folders"). It handles only server roots; the shares themselves belong to the
/// local provider. One call, no recursion, so it costs a single round-trip (spec §7.2).
/// </summary>
public sealed class ServerSharesProvider : ILocationProvider
{
    private readonly IShareEnumerator _shares;
    private readonly TimeProvider _time;

    /// <summary>Creates the provider.</summary>
    public ServerSharesProvider(IShareEnumerator shares, TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(shares);
        _shares = shares;
        _time = time ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public string Name => "server";

    /// <inheritdoc />
    public bool CanHandle(LocationPath location) => location is { Kind: LocationKind.Unc, Share: null };

    /// <inheritdoc />
    public async IAsyncEnumerable<ListingBatch> ListAsync(LocationPath location, ListingOptions options, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(location);
        ArgumentNullException.ThrowIfNull(options);
        if (!CanHandle(location))
        {
            throw new LocationException(LocationError.Unknown, location, $"{Name} cannot list {location}.");
        }

        IReadOnlyList<ShareInfo> shares;
        try
        {
            shares = await _shares.ListSharesAsync(location.Server!, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or TimeoutException)
        {
            var failure = LocationErrors.Wrap(ex, location);

            // A server that answers but will not list its shares has refused the session we have (no guest access, or the wrong
            // account): that is "sign in", as Explorer treats it, not a dead end. The folder view then offers Windows' sign-in dialog.
            if (failure.Error == LocationError.AccessDenied)
            {
                throw new LocationException(LocationError.AuthenticationRequired, location, $"{location.Server} needs you to sign in.", ex);
            }

            throw failure;
        }

        var now = _time.GetUtcNow();
        var entries = shares
            .Where(s => !string.IsNullOrWhiteSpace(s.Name))
            .OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
            .Select(s => new FileEntry(s.Name, location.Combine(s.Name), FileEntryKind.Folder, 0, now, now, FileAttributes.Directory))
            .ToList();
        yield return new ListingBatch(entries, true);
    }
}
