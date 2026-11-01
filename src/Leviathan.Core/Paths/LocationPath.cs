using System.Diagnostics.CodeAnalysis;
using System.Text;

namespace Leviathan.Core.Paths;

/// <summary>
/// An immutable, normalized location: a drive path, a UNC path, an <c>sftp://</c>/<c>ftp://</c>/<c>nfs://</c> URI
/// or a <c>shell:</c> location. Parsing is purely textual, so it behaves the same on every OS
/// (the tests run on Linux CI as well as Windows).
/// </summary>
/// <remarks>Spec §5.2: the address bar accepts all of these forms and splits them into breadcrumb segments.</remarks>
public sealed class LocationPath : IEquatable<LocationPath>
{
    /// <summary>Win32 MAX_PATH. Paths at or above this length need the extended-length prefix.</summary>
    public const int MaxPath = 260;

    private const string ExtendedPrefix = @"\\?\";
    private const string ExtendedUncPrefix = @"\\?\UNC\";

    private readonly string[] _segments;

    private LocationPath(LocationKind kind, string root, string[] segments, string? server, string? share, int? port, string? user)
    {
        Kind = kind;
        Root = root;
        _segments = segments;
        Server = server;
        Share = share;
        Port = port;
        User = user;
        Value = Build(kind, root, segments);
    }

    /// <summary>The kind of location.</summary>
    public LocationKind Kind { get; }

    /// <summary>
    /// The root, e.g. <c>C:\</c>, <c>\\server\share</c>, <c>\\server</c>, <c>sftp://user@host:2222</c> or <c>shell:Downloads</c>.
    /// </summary>
    public string Root { get; }

    /// <summary>Path segments below <see cref="Root"/>, in order.</summary>
    public IReadOnlyList<string> Segments => _segments;

    /// <summary>The full normalized text, suitable for display and for copying.</summary>
    public string Value { get; }

    /// <summary>Server or host name for UNC and URI locations; otherwise null.</summary>
    public string? Server { get; }

    /// <summary>Share name for UNC paths; null for a bare <c>\\server</c>.</summary>
    public string? Share { get; }

    /// <summary>Explicit port for URI locations, if given.</summary>
    public int? Port { get; }

    /// <summary>User name embedded in a URI location, if given.</summary>
    public string? User { get; }

    /// <summary>True when this is the root of its kind (no segments).</summary>
    public bool IsRoot => _segments.Length == 0;

    /// <summary>True for locations reached over the network (UNC, SFTP, FTP, NFS).</summary>
    public bool IsNetwork => Kind is LocationKind.Unc or LocationKind.Sftp or LocationKind.Ftp or LocationKind.Ftps or LocationKind.Nfs;

    /// <summary>The last segment, or the root itself for a root location.</summary>
    public string Name => _segments.Length > 0 ? _segments[^1] : RootDisplayName;

    private string RootDisplayName => Kind switch
    {
        LocationKind.Unc when Share is not null => Share,
        LocationKind.Unc => Server!,
        LocationKind.Drive => Root.TrimEnd('\\'),
        _ => Root,
    };

    /// <summary>
    /// The parent location, or null at the top. The parent of a share root (<c>\\nas\media</c>) is the server
    /// (<c>\\nas</c>), so "Up" can list a server's shares.
    /// </summary>
    public LocationPath? Parent
    {
        get
        {
            if (_segments.Length > 0)
            {
                return new LocationPath(Kind, Root, _segments[..^1], Server, Share, Port, User);
            }

            if (Kind == LocationKind.Unc && Share is not null)
            {
                return new LocationPath(LocationKind.Unc, @"\\" + Server, [], Server, null, null, null);
            }

            return null;
        }
    }

    /// <summary>Parses a location or throws <see cref="FormatException"/>.</summary>
    public static LocationPath Parse(string input)
    {
        if (TryParse(input, out var path, out var error))
        {
            return path;
        }

        throw new FormatException(error);
    }

    /// <summary>Parses a location without throwing.</summary>
    public static bool TryParse(string? input, [NotNullWhen(true)] out LocationPath? path) => TryParse(input, out path, out _);

    /// <summary>Parses a location without throwing, returning a human-readable reason on failure.</summary>
    public static bool TryParse(string? input, [NotNullWhen(true)] out LocationPath? path, out string error)
    {
        path = null;
        error = string.Empty;

        var text = Clean(input);
        if (text.Length == 0)
        {
            error = "The path is empty.";
            return false;
        }

        if (text.StartsWith("shell:", StringComparison.OrdinalIgnoreCase))
        {
            var name = text["shell:".Length..].Trim('\\', '/', ' ');
            if (name.Length == 0)
            {
                error = "A shell location needs a name, e.g. shell:Downloads.";
                return false;
            }

            var parts = SplitSegments(name);
            if (!Normalize(parts, out var shellSegments, out error))
            {
                return false;
            }

            path = new LocationPath(LocationKind.Shell, "shell:" + shellSegments[0], shellSegments[1..], null, null, null, null);
            return true;
        }

        var schemeEnd = text.IndexOf("://", StringComparison.Ordinal);
        if (schemeEnd > 0)
        {
            return TryParseUri(text, schemeEnd, out path, out error);
        }

        if (text.StartsWith(ExtendedUncPrefix, StringComparison.OrdinalIgnoreCase))
        {
            text = @"\\" + text[ExtendedUncPrefix.Length..];
        }
        else if (text.StartsWith(ExtendedPrefix, StringComparison.Ordinal))
        {
            text = text[ExtendedPrefix.Length..];
        }

        text = text.Replace('/', '\\');

        if (text.StartsWith(@"\\", StringComparison.Ordinal))
        {
            var parts = SplitSegments(text[2..]);
            if (parts.Count == 0)
            {
                error = "A network path needs a server name, e.g. \\\\server\\share.";
                return false;
            }

            var server = parts[0];
            if (parts.Count == 1)
            {
                path = new LocationPath(LocationKind.Unc, @"\\" + server, [], server, null, null, null);
                return true;
            }

            var share = parts[1];
            if (!Normalize(parts.Skip(2), out var uncSegments, out error))
            {
                return false;
            }

            if (!ValidateSegments(uncSegments, out error))
            {
                return false;
            }

            path = new LocationPath(LocationKind.Unc, $@"\\{server}\{share}", uncSegments, server, share, null, null);
            return true;
        }

        if (text.Length >= 2 && char.IsAsciiLetter(text[0]) && text[1] == ':')
        {
            if (text.Length > 2 && text[2] != '\\')
            {
                error = $"'{text}' is a drive-relative path; use {char.ToUpperInvariant(text[0])}:\\ followed by the folder.";
                return false;
            }

            var root = char.ToUpperInvariant(text[0]) + @":\";
            if (!Normalize(SplitSegments(text[2..]), out var driveSegments, out error))
            {
                return false;
            }

            if (!ValidateSegments(driveSegments, out error))
            {
                return false;
            }

            path = new LocationPath(LocationKind.Drive, root, driveSegments, null, null, null, null);
            return true;
        }

        error = $"'{text}' is not a full path. Use a drive path (C:\\...), a network path (\\\\server\\share) or a URI (sftp://host/path).";
        return false;
    }

    /// <summary>
    /// Resolves what a user typed in the address bar: a full location, or a path relative to <paramref name="current"/>
    /// (<c>..</c>, <c>..\other</c>, <c>sub\folder</c>).
    /// </summary>
    public static bool TryResolve(string? input, LocationPath? current, [NotNullWhen(true)] out LocationPath? path, out string error)
    {
        var text = Clean(input);
        if (TryParse(text, out path, out error))
        {
            return true;
        }

        if (current is null || text.Length == 0 || text.Contains("://", StringComparison.Ordinal) || (text.Length >= 2 && text[1] == ':'))
        {
            return false;
        }

        LocationPath? result = current;
        foreach (var part in SplitSegments(text.Replace('/', '\\')))
        {
            if (part == ".")
            {
                continue;
            }

            if (part == "..")
            {
                result = result.Parent;
                if (result is null)
                {
                    error = "The path goes above the top of the location.";
                    path = null;
                    return false;
                }

                continue;
            }

            if (!TryCombine(result, part, out result, out error))
            {
                path = null;
                return false;
            }
        }

        path = result!;
        error = string.Empty;
        return true;
    }

    /// <summary>Returns the child location <paramref name="name"/> below this one.</summary>
    public LocationPath Combine(string name) =>
        TryCombine(this, name, out var child, out var error) ? child : throw new ArgumentException(error, nameof(name));

    /// <summary>
    /// The breadcrumb trail for the address bar, from the root to this location. Each item can be clicked to navigate there.
    /// </summary>
    public IReadOnlyList<Breadcrumb> GetBreadcrumbs()
    {
        var crumbs = new List<Breadcrumb>(_segments.Length + 2);
        if (Kind == LocationKind.Unc)
        {
            var serverRoot = new LocationPath(LocationKind.Unc, @"\\" + Server, [], Server, null, null, null);
            crumbs.Add(new Breadcrumb(Server!, serverRoot));
            if (Share is not null)
            {
                crumbs.Add(new Breadcrumb(Share, new LocationPath(LocationKind.Unc, Root, [], Server, Share, null, null)));
            }
        }
        else
        {
            crumbs.Add(new Breadcrumb(RootDisplayName, new LocationPath(Kind, Root, [], Server, Share, Port, User)));
        }

        for (var i = 0; i < _segments.Length; i++)
        {
            crumbs.Add(new Breadcrumb(_segments[i], new LocationPath(Kind, Root, _segments[..(i + 1)], Server, Share, Port, User)));
        }

        return crumbs;
    }

    /// <summary>
    /// The path to hand to Win32 file APIs. Adds the extended-length prefix (<c>\\?\</c> or <c>\\?\UNC\</c>) when the path
    /// reaches <see cref="MaxPath"/> characters, so long paths work everywhere (spec §5.2).
    /// </summary>
    public string ToWin32Path(bool forceExtended = false)
    {
        if (Kind is not (LocationKind.Drive or LocationKind.Unc) || (Kind == LocationKind.Unc && Share is null))
        {
            throw new InvalidOperationException($"{Kind} locations have no Win32 path.");
        }

        if (!forceExtended && Value.Length < MaxPath)
        {
            return Value;
        }

        return Kind == LocationKind.Drive ? ExtendedPrefix + Value : ExtendedUncPrefix + Value[2..];
    }

    /// <summary>True when <paramref name="other"/> is this location or lies below it.</summary>
    public bool Contains(LocationPath other)
    {
        ArgumentNullException.ThrowIfNull(other);
        if (other.Kind != Kind || !RootComparer.Equals(other.Root, Root) || other._segments.Length < _segments.Length)
        {
            return false;
        }

        var comparer = SegmentComparer;
        for (var i = 0; i < _segments.Length; i++)
        {
            if (!comparer.Equals(_segments[i], other._segments[i]))
            {
                return false;
            }
        }

        return true;
    }

    /// <inheritdoc />
    public override string ToString() => Value;

    /// <inheritdoc />
    public bool Equals(LocationPath? other) =>
        other is not null &&
        other.Kind == Kind &&
        other._segments.Length == _segments.Length &&
        RootComparer.Equals(other.Root, Root) &&
        _segments.AsSpan().SequenceEqual(other._segments, SegmentComparer);

    /// <inheritdoc />
    public override bool Equals(object? obj) => Equals(obj as LocationPath);

    /// <inheritdoc />
    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Kind);
        hash.Add(Root, RootComparer);
        foreach (var segment in _segments)
        {
            hash.Add(segment, SegmentComparer);
        }

        return hash.ToHashCode();
    }

    /// <summary>Equality operator.</summary>
    public static bool operator ==(LocationPath? left, LocationPath? right) => left is null ? right is null : left.Equals(right);

    /// <summary>Inequality operator.</summary>
    public static bool operator !=(LocationPath? left, LocationPath? right) => !(left == right);

    /// <summary>Windows paths and hosts compare case-insensitively; paths on SFTP/FTP/NFS servers are case-sensitive.</summary>
    private StringComparer SegmentComparer =>
        Kind is LocationKind.Drive or LocationKind.Unc or LocationKind.Shell ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private static StringComparer RootComparer => StringComparer.OrdinalIgnoreCase;

    private static bool TryCombine(LocationPath parent, string name, [NotNullWhen(true)] out LocationPath? child, out string error)
    {
        child = null;
        if (string.IsNullOrWhiteSpace(name) || name.Contains('\\') || name.Contains('/') || name is "." or "..")
        {
            error = $"'{name}' is not a valid file or folder name.";
            return false;
        }

        if (parent.Kind == LocationKind.Unc && parent.Share is null)
        {
            child = new LocationPath(LocationKind.Unc, $@"\\{parent.Server}\{name}", [], parent.Server, name, null, null);
            error = string.Empty;
            return true;
        }

        if (!ValidateSegments([name], out error) && parent.Kind is LocationKind.Drive or LocationKind.Unc)
        {
            return false;
        }

        child = new LocationPath(parent.Kind, parent.Root, [.. parent._segments, name], parent.Server, parent.Share, parent.Port, parent.User);
        error = string.Empty;
        return true;
    }

    private static bool TryParseUri(string text, int schemeEnd, [NotNullWhen(true)] out LocationPath? path, out string error)
    {
        path = null;
        var scheme = text[..schemeEnd].ToLowerInvariant();
        LocationKind? kind = scheme switch
        {
            "sftp" => LocationKind.Sftp,
            "ftp" => LocationKind.Ftp,
            "ftps" => LocationKind.Ftps,
            "nfs" => LocationKind.Nfs,
            _ => null,
        };

        if (kind is null)
        {
            error = $"'{scheme}://' locations are not supported. Use sftp://, ftp://, ftps:// or nfs://.";
            return false;
        }

        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || string.IsNullOrEmpty(uri.Host))
        {
            error = $"'{text}' is not a valid {scheme}:// address.";
            return false;
        }

        var user = string.IsNullOrEmpty(uri.UserInfo) ? null : Uri.UnescapeDataString(uri.UserInfo.Split(':')[0]);
        int? port = uri.IsDefaultPort || uri.Port < 0 ? null : uri.Port;
        var host = uri.Host.ToLowerInvariant();

        var root = new StringBuilder().Append(scheme).Append("://");
        if (user is not null)
        {
            root.Append(user).Append('@');
        }

        root.Append(host);
        if (port is not null)
        {
            root.Append(':').Append(port.Value);
        }

        var rawSegments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Uri.UnescapeDataString);
        if (!Normalize(rawSegments, out var segments, out error))
        {
            return false;
        }

        path = new LocationPath(kind.Value, root.ToString(), segments, host, null, port, user);
        return true;
    }

    private static string Clean(string? input)
    {
        var text = (input ?? string.Empty).Trim();
        if (text.Length >= 2 && text[0] == '"' && text[^1] == '"')
        {
            text = text[1..^1].Trim();
        }

        if (text.Contains('%', StringComparison.Ordinal))
        {
            text = Environment.ExpandEnvironmentVariables(text);
        }

        return text;
    }

    private static List<string> SplitSegments(string text) =>
        [.. text.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];

    private static bool Normalize(IEnumerable<string> parts, out string[] segments, out string error)
    {
        var stack = new List<string>();
        foreach (var part in parts)
        {
            if (part == ".")
            {
                continue;
            }

            if (part == "..")
            {
                if (stack.Count == 0)
                {
                    segments = [];
                    error = "The path goes above the top of the location.";
                    return false;
                }

                stack.RemoveAt(stack.Count - 1);
                continue;
            }

            stack.Add(part);
        }

        segments = [.. stack];
        error = string.Empty;
        return true;
    }

    private static readonly System.Buffers.SearchValues<char> InvalidWindowsNameChars = System.Buffers.SearchValues.Create("<>:\"|?*");

    private static bool ValidateSegments(IEnumerable<string> segments, out string error)
    {
        foreach (var segment in segments)
        {
            if (segment.AsSpan().IndexOfAny(InvalidWindowsNameChars) >= 0 || segment.Any(char.IsControl))
            {
                error = $"'{segment}' contains characters Windows does not allow in names (< > : \" | ? *).";
                return false;
            }
        }

        error = string.Empty;
        return true;
    }

    private static string Build(LocationKind kind, string root, string[] segments)
    {
        if (segments.Length == 0)
        {
            return root;
        }

        return kind switch
        {
            LocationKind.Drive => root + string.Join('\\', segments),
            LocationKind.Unc => root + "\\" + string.Join('\\', segments),
            LocationKind.Shell => root + "\\" + string.Join('\\', segments),
            _ => root + "/" + string.Join('/', segments.Select(Uri.EscapeDataString)),
        };
    }
}

/// <summary>One clickable segment of the address bar.</summary>
/// <param name="Label">Text shown for the segment.</param>
/// <param name="Location">Where clicking the segment navigates.</param>
public sealed record Breadcrumb(string Label, LocationPath Location);
