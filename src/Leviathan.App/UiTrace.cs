using System.Diagnostics;

namespace Leviathan.App;

/// <summary>
/// Opt-in timing of UI-thread work, for hunting stalls (spec §3.2: nothing on the UI thread may take over 50 ms). Set the
/// environment variable <c>LEVIATHAN_UI_TRACE</c> to a file path and every scope below, plus every reported stall, is appended
/// with its time since process start. Off by default: no file is touched and the scopes cost a few nanoseconds.
/// </summary>
internal static class UiTrace
{
    private static readonly string? Path = Environment.GetEnvironmentVariable("LEVIATHAN_UI_TRACE");
    private static readonly long Origin = Process.GetCurrentProcess().StartTime.Ticks;
    private static readonly Lock Gate = new();

    public static bool Enabled => !string.IsNullOrEmpty(Path);

    /// <summary>Times the work until the returned scope is disposed; writes a line when it took at least <paramref name="thresholdMs"/>.</summary>
    public static Scope Begin(string label, double thresholdMs = 0) => new(label, thresholdMs);

    /// <summary>Records a point in time.</summary>
    public static void Mark(string text) => Write(text);

    internal static void Write(string text)
    {
        if (!Enabled)
        {
            return;
        }

        var sinceStart = (DateTime.Now.Ticks - Origin) / (double)TimeSpan.TicksPerMillisecond;
        lock (Gate)
        {
            File.AppendAllText(Path!, $"{sinceStart,9:F1} ms  [t{Environment.CurrentManagedThreadId,2}]  {text}{Environment.NewLine}");
        }
    }

    internal readonly struct Scope(string label, double thresholdMs) : IDisposable
    {
        private readonly long _start = Enabled ? Stopwatch.GetTimestamp() : 0;

        public void Dispose()
        {
            if (!Enabled)
            {
                return;
            }

            var ms = Stopwatch.GetElapsedTime(_start).TotalMilliseconds;
            if (ms >= thresholdMs)
            {
                Write($"{label}: {ms:F1} ms");
            }
        }
    }
}
