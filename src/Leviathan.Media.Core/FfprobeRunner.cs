using System.ComponentModel;
using System.Text.Json;

namespace Leviathan.Media;

/// <summary>How a probe ended.</summary>
public enum ProbeStatus
{
    /// <summary>ffprobe answered with JSON.</summary>
    Ok,

    /// <summary>It did not finish in time; the file is treated as unreadable until it changes (spec §9.3).</summary>
    TimedOut,

    /// <summary>It exited with an error or printed something that is not JSON: the file is not media, or is damaged.</summary>
    Failed,

    /// <summary>It printed more than the limit allows (a file with an enormous list of chapters or tags).</summary>
    TooLarge,

    /// <summary>ffprobe is not installed where Leviathan looks; nothing was tried, and nothing should be marked unreadable.</summary>
    ToolMissing,
}

/// <summary>The outcome of one probe: the raw JSON when it worked, otherwise why not.</summary>
/// <param name="Status">How it ended.</param>
/// <param name="Json">ffprobe's JSON (<see cref="ProbeStatus.Ok"/> only).</param>
/// <param name="Message">A short reason for the other statuses, safe to show and to log.</param>
public sealed record ProbeResult(ProbeStatus Status, string? Json, string? Message)
{
    /// <summary>True when the file is known to be unreadable, so it should be remembered and not tried again until it changes.</summary>
    public bool IsUnreadable => Status is ProbeStatus.TimedOut or ProbeStatus.Failed or ProbeStatus.TooLarge;
}

/// <summary>Limits for a probe.</summary>
public sealed record ProbeOptions
{
    /// <summary>The time one file gets (spec §9.3: 10 s).</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>The most JSON accepted; real files print a few KB, a file with thousands of chapters a few MB.</summary>
    public int MaxOutputBytes { get; init; } = 8 * 1024 * 1024;

    /// <summary>
    /// How much of a network file ffprobe may read to find its streams: enough for the container header of the usual formats, far less
    /// than the file (spec §9.3). Local files use ffprobe's own limits.
    /// </summary>
    public long NetworkProbeBytes { get; init; } = 4 * 1024 * 1024;

    /// <summary>The same limit in microseconds of media for ffprobe's analysis of a network file.</summary>
    public long NetworkAnalyzeMicroseconds { get; init; } = 3_000_000;
}

/// <summary>
/// Runs ffprobe on one file and returns its JSON (spec §9.3): <c>-show_format -show_streams -show_chapters -of json</c>, a time limit,
/// low priority, and a cap on what it may print. It decides nothing about caching or quarantine; the callers do, from the status.
/// </summary>
public sealed class FfprobeRunner(IProcessRunner runner, Func<FfmpegTools> tools, ProbeOptions? options = null)
{
    private readonly ProbeOptions _options = options ?? new ProbeOptions();

    /// <summary>The arguments for <paramref name="path"/>; public so the exact command is testable and can be shown in diagnostics.</summary>
    public IReadOnlyList<string> Arguments(string path, bool isNetwork)
    {
        List<string> arguments =
        [
            "-v", "error",
            "-hide_banner",
            "-show_format", "-show_streams", "-show_chapters",
            "-of", "json",
        ];
        if (isNetwork)
        {
            arguments.AddRange(["-probesize", _options.NetworkProbeBytes.ToString(System.Globalization.CultureInfo.InvariantCulture)]);
            arguments.AddRange(["-analyzeduration", _options.NetworkAnalyzeMicroseconds.ToString(System.Globalization.CultureInfo.InvariantCulture)]);
        }

        // "file:" stops ffmpeg reading a name such as "concat:a|b" or "http://…" as a protocol instead of as the file it is.
        arguments.AddRange(["-i", "file:" + path]);
        return arguments;
    }

    /// <summary>Probes <paramref name="path"/> (a Win32 path). <paramref name="isNetwork"/> limits how much of the file is read.</summary>
    public async Task<ProbeResult> ProbeAsync(string path, bool isNetwork, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        if (tools().FfprobePath is not { } ffprobe)
        {
            return new ProbeResult(ProbeStatus.ToolMissing, null, "ffprobe was not found. Set the ffmpeg folder in Settings.");
        }

        ProcessOutcome outcome;
        try
        {
            outcome = await runner.RunAsync(ffprobe, Arguments(path, isNetwork), _options.Timeout, _options.MaxOutputBytes, cancellationToken).ConfigureAwait(false);
        }
        catch (Win32Exception ex)
        {
            return new ProbeResult(ProbeStatus.ToolMissing, null, $"ffprobe could not be started: {ex.Message}");
        }

        if (outcome.TimedOut)
        {
            return new ProbeResult(ProbeStatus.TimedOut, null, $"ffprobe did not answer within {_options.Timeout.TotalSeconds:0} s.");
        }

        if (outcome.OutputTruncated)
        {
            return new ProbeResult(ProbeStatus.TooLarge, null, "ffprobe printed more than the limit allows.");
        }

        if (outcome.ExitCode != 0)
        {
            return new ProbeResult(ProbeStatus.Failed, null, FirstLine(outcome.Error) ?? $"ffprobe exited with code {outcome.ExitCode}.");
        }

        return IsJsonObject(outcome.Output)
            ? new ProbeResult(ProbeStatus.Ok, outcome.Output, null)
            : new ProbeResult(ProbeStatus.Failed, null, "ffprobe did not print JSON.");
    }

    private static bool IsJsonObject(string text)
    {
        try
        {
            using var document = JsonDocument.Parse(text);
            return document.RootElement.ValueKind == JsonValueKind.Object;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string? FirstLine(string text) =>
        text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
}
