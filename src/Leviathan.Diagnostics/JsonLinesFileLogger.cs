using System.Diagnostics;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace Leviathan.Diagnostics;

/// <summary>
/// Structured log as JSON lines, one file per process per day, kept for 7 days (spec §20.1). Writes on a background
/// task so logging never blocks the caller. Each line carries the trace ID that links an action across processes.
/// </summary>
[ProviderAlias("JsonFile")]
public sealed class JsonLinesFileLoggerProvider : ILoggerProvider
{
    private static readonly JsonSerializerOptions JsonOptions = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private readonly string _folder;
    private readonly string _process;
    private readonly TimeProvider _time;
    private readonly Channel<string> _lines = Channel.CreateBounded<string>(new BoundedChannelOptions(10_000) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });
    private readonly Task _writer;

    /// <summary>Creates the provider.</summary>
    public JsonLinesFileLoggerProvider(string folder, string processName, TimeProvider? time = null, TimeSpan? retention = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        ArgumentException.ThrowIfNullOrWhiteSpace(processName);
        _folder = folder;
        _process = processName;
        _time = time ?? TimeProvider.System;
        Directory.CreateDirectory(folder);
        DeleteOldLogs(retention ?? TimeSpan.FromDays(7));
        _writer = Task.Run(WriteLoopAsync);
    }

    /// <summary>Default folder: %LOCALAPPDATA%\Leviathan\logs.</summary>
    public static string DefaultFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Leviathan", "logs");

    /// <inheritdoc />
    public ILogger CreateLogger(string categoryName) => new JsonLogger(this, categoryName);

    /// <inheritdoc />
    public void Dispose()
    {
        _lines.Writer.TryComplete();
        _writer.Wait(TimeSpan.FromSeconds(2));
    }

    private void Enqueue(LogLevel level, string category, string message, Exception? exception)
    {
        var line = JsonSerializer.Serialize(
            new
            {
                t = _time.GetUtcNow(),
                p = _process,
                pid = Environment.ProcessId,
                l = level.ToString(),
                c = category,
                m = message,
                trace = Activity.Current?.TraceId.ToHexString(),
                span = Activity.Current?.SpanId.ToHexString(),
                ex = exception?.ToString(),
            },
            JsonOptions);
        _lines.Writer.TryWrite(line);
    }

    private async Task WriteLoopAsync()
    {
        StreamWriter? writer = null;
        var currentDay = DateOnly.MinValue;
        try
        {
            await foreach (var line in _lines.Reader.ReadAllAsync().ConfigureAwait(false))
            {
                var today = DateOnly.FromDateTime(_time.GetUtcNow().UtcDateTime);
                if (writer is null || today != currentDay)
                {
                    if (writer is not null)
                    {
                        await writer.DisposeAsync().ConfigureAwait(false);
                    }

                    currentDay = today;
                    var path = Path.Combine(_folder, $"{_process}-{today:yyyyMMdd}.jsonl");
                    writer = new StreamWriter(new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite)) { AutoFlush = false };
                }

                await writer.WriteLineAsync(line).ConfigureAwait(false);
                if (_lines.Reader.Count == 0)
                {
                    await writer.FlushAsync().ConfigureAwait(false);
                }
            }
        }
        catch (IOException)
        {
            // Logging must never take the process down.
        }
        finally
        {
            if (writer is not null)
            {
                await writer.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private void DeleteOldLogs(TimeSpan retention)
    {
        try
        {
            var cutoff = _time.GetUtcNow().UtcDateTime - retention;
            foreach (var file in Directory.EnumerateFiles(_folder, $"{_process}-*.jsonl"))
            {
                if (File.GetLastWriteTimeUtc(file) < cutoff)
                {
                    File.Delete(file);
                }
            }
        }
        catch (IOException)
        {
            // Best effort.
        }
        catch (UnauthorizedAccessException)
        {
            // Best effort.
        }
    }

    private sealed class JsonLogger(JsonLinesFileLoggerProvider owner, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (IsEnabled(logLevel))
            {
                owner.Enqueue(logLevel, category, formatter(state, exception), exception);
            }
        }
    }
}
