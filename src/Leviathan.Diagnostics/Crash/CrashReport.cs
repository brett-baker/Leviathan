namespace Leviathan.Diagnostics.Crash;

/// <summary>What produced a bundle.</summary>
public enum IncidentKind
{
    /// <summary>Unhandled exception or native crash.</summary>
    Crash,

    /// <summary>The UI thread or a worker stopped responding.</summary>
    Hang,

    /// <summary>The user chose Help › Report a problem.</summary>
    Report,
}

/// <summary>One exception in the chain.</summary>
/// <param name="Type">Full type name.</param>
/// <param name="Message">Message.</param>
/// <param name="HResult">HRESULT as hex, e.g. 0x80070040.</param>
/// <param name="Win32Error">Win32 error name when the HRESULT carries one, e.g. ERROR_NETNAME_DELETED.</param>
public sealed record ExceptionInfo(string Type, string Message, string HResult, string? Win32Error);

/// <summary>One stack frame, mapped to the repository.</summary>
/// <param name="Method">Declaring type and method.</param>
/// <param name="File">Repository-relative source path, when known.</param>
/// <param name="Line">Line number, when known.</param>
/// <param name="Permalink">GitHub permalink at the build's commit, when known.</param>
public sealed record FrameInfo(string Method, string? File, int? Line, string? Permalink);

/// <summary>Build provenance used for permalinks.</summary>
/// <param name="Version">Informational version.</param>
/// <param name="Commit">Git commit SHA, when known.</param>
/// <param name="RepositoryUrl">Repository URL, when known.</param>
public sealed record BuildInfo(string Version, string? Commit, string? RepositoryUrl);

/// <summary>Everything in a crash bundle's report.json (versioned schema) and the basis of report.md.</summary>
public sealed record CrashReport
{
    /// <summary>Schema version of report.json.</summary>
    public const int CurrentSchemaVersion = 1;

    /// <summary>Schema version.</summary>
    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    /// <summary>Bundle ID: timestamp, process and a short random suffix.</summary>
    public required string Id { get; init; }

    /// <summary>Incident kind.</summary>
    public required IncidentKind Kind { get; init; }

    /// <summary>Process that produced it.</summary>
    public required string Process { get; init; }

    /// <summary>When it happened (UTC).</summary>
    public required DateTimeOffset Timestamp { get; init; }

    /// <summary>One-line summary.</summary>
    public required string Summary { get; init; }

    /// <summary>Short stable hash of exception type + top frames; groups repeats of the same bug.</summary>
    public required string Signature { get; init; }

    /// <summary>Build provenance.</summary>
    public required BuildInfo Build { get; init; }

    /// <summary>Exception chain, outermost first.</summary>
    public IReadOnlyList<ExceptionInfo> Exceptions { get; init; } = [];

    /// <summary>Stack of the innermost exception (or of the hung thread).</summary>
    public IReadOnlyList<FrameInfo> Frames { get; init; } = [];

    /// <summary>Last user actions, oldest first.</summary>
    public IReadOnlyList<UserAction> Actions { get; init; } = [];

    /// <summary>Events around the failure, oldest first.</summary>
    public IReadOnlyList<DiagnosticEvent> RecentEvents { get; init; } = [];

    /// <summary>Free-form context: active jobs, location, share health.</summary>
    public IReadOnlyDictionary<string, string> Context { get; init; } = new Dictionary<string, string>();

    /// <summary>Environment facts: OS build, runtime versions, video engine.</summary>
    public IReadOnlyDictionary<string, string> Environment { get; init; } = new Dictionary<string, string>();

    /// <summary>Source files implicated by the stack, most relevant first.</summary>
    public IReadOnlyList<string> StartingPoints { get; init; } = [];

    /// <summary>User's description, for Report incidents.</summary>
    public string? UserNote { get; init; }
}
