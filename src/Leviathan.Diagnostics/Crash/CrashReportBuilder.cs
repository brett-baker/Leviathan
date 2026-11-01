using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Leviathan.Core.Providers;

namespace Leviathan.Diagnostics.Crash;

/// <summary>Builds <see cref="CrashReport"/>s from exceptions and the process's diagnostic state.</summary>
public sealed class CrashReportBuilder
{
    private static readonly string[] RepoRoots = ["src", "tools", "tests"];

    private readonly string _processName;
    private readonly BuildInfo _build;
    private readonly FlightRecorder? _recorder;
    private readonly ActionTrail? _actions;
    private readonly TimeProvider _time;

    /// <summary>Creates a builder.</summary>
    public CrashReportBuilder(string processName, BuildInfo build, FlightRecorder? recorder = null, ActionTrail? actions = null, TimeProvider? time = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(processName);
        _processName = processName;
        _build = build ?? throw new ArgumentNullException(nameof(build));
        _recorder = recorder;
        _actions = actions;
        _time = time ?? TimeProvider.System;
    }

    /// <summary>Reads version, commit and repository URL from an assembly's SourceLink-stamped attributes.</summary>
    public static BuildInfo ReadBuildInfo(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? assembly.GetName().Version?.ToString()
            ?? "unknown";
        var plus = informational.IndexOf('+', StringComparison.Ordinal);
        var commit = plus >= 0 ? informational[(plus + 1)..] : null;
        var repo = assembly.GetCustomAttributes<AssemblyMetadataAttribute>().FirstOrDefault(a => a.Key == "RepositoryUrl")?.Value;
        return new BuildInfo(informational, commit, repo);
    }

    /// <summary>
    /// Stable short signature for an exception: SHA-256 of the innermost exception type and its top five frames' method
    /// names (no line numbers or messages, so the same bug hashes the same across builds and data).
    /// </summary>
    public static string ComputeSignature(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        var inner = Innermost(exception);
        var text = new StringBuilder(inner.GetType().FullName);
        foreach (var frame in new StackTrace(inner, fNeedFileInfo: false).GetFrames().Take(5))
        {
            text.Append('|').Append(MethodName(frame));
        }

        return SignatureOf(text.ToString());
    }

    /// <summary>Signature for a hang: the top frames of the stuck thread.</summary>
    public static string ComputeSignature(string kind, IEnumerable<string> topMethods) =>
        SignatureOf(kind + "|" + string.Join('|', topMethods.Take(5)));

    /// <summary>Builds a report for an exception.</summary>
    public CrashReport Build(IncidentKind kind, Exception? exception, IReadOnlyDictionary<string, string>? context = null, string? userNote = null)
    {
        var now = _time.GetUtcNow();
        var exceptions = exception is null ? [] : Chain(exception).Select(Describe).ToList();
        var frames = exception is null ? [] : MapFrames(new StackTrace(Innermost(exception), fNeedFileInfo: true));
        var signature = exception is null ? SignatureOf(kind + "|" + (userNote ?? "manual")) : ComputeSignature(exception);
        var summary = BuildSummary(kind, exception, frames);

        return new CrashReport
        {
            Id = $"{now:yyyy-MM-ddTHH-mm-ssZ}_{ShortProcess()}_{Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(2))}",
            Kind = kind,
            Process = _processName,
            Timestamp = now,
            Summary = summary,
            Signature = signature,
            Build = _build,
            Exceptions = exceptions,
            Frames = frames,
            Actions = _actions?.Snapshot() ?? [],
            RecentEvents = _recorder?.Snapshot() ?? [],
            Context = context ?? new Dictionary<string, string>(),
            Environment = CollectEnvironment(),
            StartingPoints = [.. frames.Where(f => f.File is not null).Select(f => f.File!).Distinct().Take(5)],
            UserNote = userNote,
        };
    }

    /// <summary>Maps .NET stack frames to repository paths and permalinks.</summary>
    public IReadOnlyList<FrameInfo> MapFrames(StackTrace trace)
    {
        ArgumentNullException.ThrowIfNull(trace);
        var frames = new List<FrameInfo>();
        foreach (var frame in trace.GetFrames())
        {
            var file = ToRepoRelative(frame.GetFileName());
            var line = frame.GetFileLineNumber();
            int? lineNumber = line > 0 ? line : null;
            string? permalink = null;
            if (file is not null && _build.RepositoryUrl is { } repo && _build.Commit is { } commit)
            {
                permalink = $"{repo.TrimEnd('/').Replace(".git", string.Empty, StringComparison.Ordinal)}/blob/{commit}/{file}{(lineNumber is null ? string.Empty : $"#L{lineNumber}")}";
            }

            frames.Add(new FrameInfo(MethodName(frame), file, lineNumber, permalink));
        }

        return frames;
    }

    /// <summary>
    /// Converts a build-machine path (<c>D:\a\Leviathan\src\Leviathan.Worker\X.cs</c> or a SourceLink URL) to a
    /// repository-relative path (<c>src/Leviathan.Worker/X.cs</c>).
    /// </summary>
    public static string? ToRepoRelative(string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return null;
        }

        var normalized = path.Replace('\\', '/');
        foreach (var root in RepoRoots)
        {
            var marker = "/" + root + "/";
            var index = normalized.LastIndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (index >= 0)
            {
                return normalized[(index + 1)..];
            }
        }

        return normalized;
    }

    private string BuildSummary(IncidentKind kind, Exception? exception, IReadOnlyList<FrameInfo> frames)
    {
        var process = ShortProcess();
        if (exception is null)
        {
            return kind == IncidentKind.Hang ? $"{process} stopped responding" : $"Problem reported in {process}";
        }

        var inner = Innermost(exception);
        var win32 = Win32Of(inner.HResult);
        var code = win32 is null ? string.Empty : $" ({win32}, 0x{inner.HResult:X8})";
        var where = frames.FirstOrDefault(f => f.File is not null)?.Method ?? (frames.Count > 0 ? frames[0].Method : null);
        var at = where is null ? string.Empty : $" in {where}";
        var verb = kind == IncidentKind.Hang ? "hung" : "crashed";
        return $"{process} {verb}: {inner.GetType().Name}{code}{at}";
    }

    private string ShortProcess() => _processName.Replace("Leviathan.", string.Empty, StringComparison.Ordinal).ToLowerInvariant();

    private static ExceptionInfo Describe(Exception ex) =>
        new(ex.GetType().FullName ?? ex.GetType().Name, ex.Message, $"0x{ex.HResult:X8}", Win32Of(ex.HResult));

    private static string? Win32Of(int hresult)
    {
        if ((hresult & 0xFFFF0000) != unchecked((int)0x80070000))
        {
            return null;
        }

        return LocationErrors.Win32Name(LocationErrors.Win32CodeFromHResult(hresult));
    }

    private static IEnumerable<Exception> Chain(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is AggregateException { InnerExceptions.Count: > 1 } aggregate)
            {
                yield return current;
                foreach (var inner in aggregate.InnerExceptions)
                {
                    yield return inner;
                }

                yield break;
            }

            yield return current;
        }
    }

    private static Exception Innermost(Exception exception)
    {
        var current = exception;
        while (current.InnerException is not null)
        {
            current = current.InnerException;
        }

        return current;
    }

    private static string MethodName(StackFrame frame)
    {
        var method = frame.GetMethod();
        if (method is null)
        {
            return "<unknown>";
        }

        var type = method.DeclaringType;

        // Async state machines appear as "<RunAsync>d__12.MoveNext"; report them as "Type.RunAsync".
        if (type is not null && type.Name.StartsWith('<') && type.DeclaringType is not null)
        {
            var end = type.Name.IndexOf('>', StringComparison.Ordinal);
            var name = end > 1 ? type.Name[1..end] : method.Name;
            return $"{type.DeclaringType.FullName}.{name}";
        }

        return type is null ? method.Name : $"{type.FullName}.{method.Name}";
    }

    private static string SignatureOf(string text) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..12];

    private Dictionary<string, string> CollectEnvironment() => new()
    {
        ["app.version"] = _build.Version,
        ["app.commit"] = _build.Commit ?? "unknown",
        ["os"] = RuntimeInformation.OSDescription,
        ["os.architecture"] = RuntimeInformation.OSArchitecture.ToString(),
        ["process.architecture"] = RuntimeInformation.ProcessArchitecture.ToString(),
        [".net"] = RuntimeInformation.FrameworkDescription,
        ["processors"] = System.Environment.ProcessorCount.ToString(CultureInfo.InvariantCulture),
        ["process.workingSetMB"] = (System.Environment.WorkingSet / (1024 * 1024)).ToString(CultureInfo.InvariantCulture),
        ["process.uptime"] = (DateTime.Now - Process.GetCurrentProcess().StartTime).ToString(@"d\.hh\:mm\:ss", CultureInfo.InvariantCulture),
    };
}
