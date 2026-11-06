using Leviathan.Contracts;
using Leviathan.Core.Paths;

namespace Leviathan.Engine.Execution;

/// <summary>
/// Checks a <see cref="JobRequest"/> before it is queued, so a malformed request is refused with a clear message at the door
/// instead of failing halfway through a copy (spec §12.1). Pure, and shared by the worker and the tests.
/// </summary>
public static class JobRequestValidator
{
    /// <summary>Returns why <paramref name="request"/> cannot be queued, or null when it can.</summary>
    public static string? Validate(JobRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        // The automatic purge applies a rule to the whole bin, so it names no items itself.
        if (request.Sources.Count == 0 && !(request.Kind == JobKind.Purge && request.PurgePolicy is not null))
        {
            return "A job needs at least one source.";
        }

        foreach (var source in request.Sources)
        {
            if (!LocationPath.TryParse(source, out _))
            {
                return $"'{source}' is not a valid location.";
            }
        }

        switch (request.Kind)
        {
            case JobKind.Copy or JobKind.Move or JobKind.Link:
                return ValidateDestination(request);
            case JobKind.Rename:
                return ValidateRename(request);
            case JobKind.Delete:
                return request.Destination is null ? null : "A delete job has no destination.";
            case JobKind.Restore:
                return ValidateRestore(request);
            default:
                return null;
        }
    }

    private static string? ValidateDestination(JobRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Destination) || !LocationPath.TryParse(request.Destination, out var destination))
        {
            return $"A {request.Kind.ToString().ToLowerInvariant()} job needs a destination folder.";
        }

        foreach (var text in request.Sources)
        {
            var source = LocationPath.Parse(text);
            if (source.Contains(destination) && request.Kind != JobKind.Link)
            {
                return $"{source.Name} cannot be put inside itself.";
            }
        }

        return null;
    }

    private static string? ValidateRestore(JobRequest request)
    {
        if (request.Targets is not { } targets || targets.Count != request.Sources.Count)
        {
            return "A restore job needs one target for each source.";
        }

        return targets.Any(t => !LocationPath.TryParse(t, out _)) ? "A restore target is not a valid location." : null;
    }

    private static string? ValidateRename(JobRequest request)
    {
        if (request.Sources.Count != 1)
        {
            return "A rename job takes exactly one item.";
        }

        var name = request.NewName;
        if (string.IsNullOrWhiteSpace(name) || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name.EndsWith('.') || name.EndsWith(' '))
        {
            return "That is not a valid name.";
        }

        return null;
    }
}
