using Leviathan.Core.Model;
using Leviathan.Core.Paths;
using Leviathan.Core.Providers;

namespace Leviathan.ViewModels.Navigation;

/// <summary>Finds out what is at a location without listing its parent folder (a single stat call in the real implementation).</summary>
public interface IEntryProbe
{
    /// <summary>Returns the kind of item at <paramref name="location"/>, or null when nothing is there.</summary>
    Task<FileEntryKind?> GetKindAsync(LocationPath location, CancellationToken cancellationToken);
}

/// <summary>Outcome of the user pressing Enter in the address bar.</summary>
public enum AddressOutcome
{
    /// <summary>Go to <see cref="AddressResult.Folder"/>.</summary>
    Navigate,

    /// <summary>The text names a file: go to its folder and select it.</summary>
    NavigateAndSelect,

    /// <summary>The text is not a valid location.</summary>
    Invalid,

    /// <summary>The location is valid but nothing is there.</summary>
    NotFound,

    /// <summary>The location could not be checked (share unreachable, access denied); the message says why.</summary>
    Failed,
}

/// <summary>Result of resolving typed address-bar text.</summary>
/// <param name="Outcome">What to do.</param>
/// <param name="Folder">The folder to show, for the navigate outcomes.</param>
/// <param name="SelectItem">The file to select, for <see cref="AddressOutcome.NavigateAndSelect"/>.</param>
/// <param name="Message">User-facing explanation for the failure outcomes.</param>
public sealed record AddressResult(AddressOutcome Outcome, LocationPath? Folder = null, LocationPath? SelectItem = null, string? Message = null);

/// <summary>
/// Turns what the user typed into a destination (spec §5.2): drive, UNC, <c>sftp://</c> and friends, environment variables,
/// <c>shell:</c> folders, relative paths, and file paths (open the folder and select the file).
/// </summary>
public sealed class AddressResolver
{
    private readonly IEntryProbe _probe;

    /// <summary>Creates a resolver that checks existence through <paramref name="probe"/>.</summary>
    public AddressResolver(IEntryProbe probe)
    {
        ArgumentNullException.ThrowIfNull(probe);
        _probe = probe;
    }

    /// <summary>Resolves <paramref name="input"/> relative to <paramref name="current"/>.</summary>
    public async Task<AddressResult> ResolveAsync(string? input, LocationPath? current, CancellationToken cancellationToken)
    {
        if (!LocationPath.TryResolve(input, current, out var location, out var error))
        {
            return new AddressResult(AddressOutcome.Invalid, Message: error);
        }

        // Shell locations are virtual folders; there is nothing to stat.
        if (location.Kind == LocationKind.Shell)
        {
            return new AddressResult(AddressOutcome.Navigate, location);
        }

        FileEntryKind? kind;
        try
        {
            kind = await _probe.GetKindAsync(location, cancellationToken).ConfigureAwait(false);
        }
        catch (LocationException ex)
        {
            return new AddressResult(AddressOutcome.Failed, Message: ex.Message);
        }

        if (kind is null)
        {
            return new AddressResult(AddressOutcome.NotFound, Message: $"{location} was not found.");
        }

        if (kind == FileEntryKind.File && location.Parent is { } parent)
        {
            return new AddressResult(AddressOutcome.NavigateAndSelect, parent, location);
        }

        return new AddressResult(AddressOutcome.Navigate, location);
    }
}
