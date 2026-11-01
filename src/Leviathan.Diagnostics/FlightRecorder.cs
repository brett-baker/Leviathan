using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace Leviathan.Diagnostics;

/// <summary>One recorded event.</summary>
/// <param name="Timestamp">When it happened (UTC).</param>
/// <param name="Process">Process name, e.g. "Leviathan.Worker".</param>
/// <param name="Level">Log level.</param>
/// <param name="Category">Logger category.</param>
/// <param name="Message">Rendered message.</param>
/// <param name="TraceId">W3C trace ID of the current activity, linking an action across processes.</param>
/// <param name="Exception">Exception text, if any.</param>
public sealed record DiagnosticEvent(
    DateTimeOffset Timestamp,
    string Process,
    LogLevel Level,
    string Category,
    string Message,
    string? TraceId,
    string? Exception);

/// <summary>
/// Always-on in-memory ring buffer of the most recent events at debug level (spec §20.1). Cheap enough to leave on;
/// its contents go into every crash bundle. Thread-safe.
/// </summary>
public sealed class FlightRecorder
{
    /// <summary>Default capacity.</summary>
    public const int DefaultCapacity = 2000;

    private readonly Lock _lock = new();
    private readonly DiagnosticEvent?[] _buffer;
    private int _next;
    private int _count;

    /// <summary>Creates a recorder.</summary>
    public FlightRecorder(string processName, int capacity = DefaultCapacity)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(processName);
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        ProcessName = processName;
        _buffer = new DiagnosticEvent?[capacity];
    }

    /// <summary>Name recorded on each event.</summary>
    public string ProcessName { get; }

    /// <summary>Capacity.</summary>
    public int Capacity => _buffer.Length;

    /// <summary>Records an event.</summary>
    public void Record(LogLevel level, string category, string message, Exception? exception = null, DateTimeOffset? timestamp = null)
    {
        var evt = new DiagnosticEvent(
            timestamp ?? DateTimeOffset.UtcNow,
            ProcessName,
            level,
            category,
            message,
            Activity.Current?.TraceId.ToHexString(),
            exception?.ToString());

        lock (_lock)
        {
            _buffer[_next] = evt;
            _next = (_next + 1) % _buffer.Length;
            _count = Math.Min(_count + 1, _buffer.Length);
        }
    }

    /// <summary>Events, oldest first.</summary>
    public IReadOnlyList<DiagnosticEvent> Snapshot()
    {
        lock (_lock)
        {
            var result = new List<DiagnosticEvent>(_count);
            var start = (_next - _count + _buffer.Length) % _buffer.Length;
            for (var i = 0; i < _count; i++)
            {
                result.Add(_buffer[(start + i) % _buffer.Length]!);
            }

            return result;
        }
    }
}

/// <summary>Feeds every log message into a <see cref="FlightRecorder"/>.</summary>
[ProviderAlias("FlightRecorder")]
public sealed class FlightRecorderLoggerProvider(FlightRecorder recorder) : ILoggerProvider
{
    private readonly FlightRecorder _recorder = recorder ?? throw new ArgumentNullException(nameof(recorder));

    /// <inheritdoc />
    public ILogger CreateLogger(string categoryName) => new RecorderLogger(_recorder, categoryName);

    /// <inheritdoc />
    public void Dispose()
    {
    }

    private sealed class RecorderLogger(FlightRecorder recorder, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Debug;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (IsEnabled(logLevel))
            {
                recorder.Record(logLevel, category, formatter(state, exception), exception);
            }
        }
    }
}

/// <summary>A user command, recorded so a debug build can replay what led to a bug (spec §20.1).</summary>
/// <param name="Timestamp">When it happened.</param>
/// <param name="Command">Command name, e.g. "Navigate", "Copy", "Tag".</param>
/// <param name="Target">What it acted on, e.g. a path.</param>
public sealed record UserAction(DateTimeOffset Timestamp, string Command, string? Target);

/// <summary>The last 50 user commands. Thread-safe.</summary>
public sealed class ActionTrail
{
    /// <summary>Number of actions kept.</summary>
    public const int Capacity = 50;

    private readonly Lock _lock = new();
    private readonly Queue<UserAction> _actions = new();
    private readonly TimeProvider _time;

    /// <summary>Creates a trail.</summary>
    public ActionTrail(TimeProvider? time = null) => _time = time ?? TimeProvider.System;

    /// <summary>Records a command.</summary>
    public void Record(string command, string? target = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(command);
        lock (_lock)
        {
            _actions.Enqueue(new UserAction(_time.GetUtcNow(), command, target));
            while (_actions.Count > Capacity)
            {
                _actions.Dequeue();
            }
        }
    }

    /// <summary>Actions, oldest first.</summary>
    public IReadOnlyList<UserAction> Snapshot()
    {
        lock (_lock)
        {
            return [.. _actions];
        }
    }
}
