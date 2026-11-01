using System.IO.Abstractions;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Leviathan.ViewModels.Settings;

/// <summary>
/// Loads and saves <see cref="AppSettings"/> as a JSON file (spec §17.2, §3.4). Writes are atomic (temp file, flush, rename),
/// the file is backed up once a day, and a corrupt file is set aside and replaced by defaults, so a bad edit never stops
/// the app from starting. Changes apply without a restart: subscribers get <see cref="Changed"/>.
/// </summary>
public sealed class SettingsStore : IDisposable
{
    /// <summary>Backups kept next to the settings file.</summary>
    public const int BackupsKept = 7;

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly IFileSystem _fileSystem;
    private readonly string _path;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private AppSettings _current = new();

    /// <summary>Creates a store over <paramref name="path"/>.</summary>
    public SettingsStore(IFileSystem fileSystem, string path, TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _fileSystem = fileSystem;
        _path = path;
        _time = time ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public void Dispose() => _gate.Dispose();

    /// <summary>Raised after settings change, with the new value.</summary>
    public event EventHandler<AppSettings>? Changed;

    /// <summary>The settings in effect.</summary>
    public AppSettings Current => Volatile.Read(ref _current);

    /// <summary>True when the last <see cref="LoadAsync"/> found an unreadable file and fell back to defaults.</summary>
    public bool RecoveredFromCorruption { get; private set; }

    /// <summary>Reads the file. A missing file gives defaults; an unreadable one is renamed to <c>.corrupt</c> and defaults are used.</summary>
    public async Task<AppSettings> LoadAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            RecoveredFromCorruption = false;
            AppSettings loaded;
            if (!_fileSystem.File.Exists(_path))
            {
                loaded = new AppSettings();
            }
            else
            {
                try
                {
                    var text = await _fileSystem.File.ReadAllTextAsync(_path, cancellationToken).ConfigureAwait(false);
                    loaded = JsonSerializer.Deserialize<AppSettings>(text, Json) ?? throw new JsonException("Empty settings file.");
                    loaded = Normalize(loaded);
                }
                catch (JsonException)
                {
                    loaded = SetAsideCorruptFile();
                }
            }

            Volatile.Write(ref _current, loaded);
            return loaded;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Applies <paramref name="change"/> to the current settings, saves atomically and raises <see cref="Changed"/>.</summary>
    public async Task<AppSettings> UpdateAsync(Func<AppSettings, AppSettings> change, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(change);
        AppSettings updated;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            updated = Normalize(change(_current));
            await WriteAtomicAsync(updated, cancellationToken).ConfigureAwait(false);
            Volatile.Write(ref _current, updated);
        }
        finally
        {
            _gate.Release();
        }

        Changed?.Invoke(this, updated);
        return updated;
    }

    private static AppSettings Normalize(AppSettings settings) => settings with
    {
        General = settings.General ?? new GeneralSettings(),
        Appearance = settings.Appearance ?? new AppearanceSettings(),
        Files = settings.Files ?? new FilesSettings(),
        Tabs = settings.Tabs ?? new TabSettings(),
        KeyBindings = settings.KeyBindings ?? [],
    };

    private AppSettings SetAsideCorruptFile()
    {
        RecoveredFromCorruption = true;
        var corrupt = _path + ".corrupt";
        try
        {
            _fileSystem.File.Move(_path, corrupt, overwrite: true);
        }
        catch (IOException)
        {
            // Best effort: defaults are used either way.
        }

        return new AppSettings();
    }

    private async Task WriteAtomicAsync(AppSettings settings, CancellationToken cancellationToken)
    {
        var directory = _fileSystem.Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(directory))
        {
            _fileSystem.Directory.CreateDirectory(directory);
        }

        BackUpOncePerDay();

        var temp = _path + ".tmp";
        var bytes = JsonSerializer.SerializeToUtf8Bytes(settings, Json);
        var stream = _fileSystem.File.Create(temp);
        await using (stream.ConfigureAwait(false))
        {
            await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            stream.Flush(flushToDisk: true);
        }

        _fileSystem.File.Move(temp, _path, overwrite: true);
    }

    private void BackUpOncePerDay()
    {
        if (!_fileSystem.File.Exists(_path))
        {
            return;
        }

        var stamp = _time.GetUtcNow().ToString("yyyyMMdd", System.Globalization.CultureInfo.InvariantCulture);
        var backup = $"{_path}.bak-{stamp}";
        if (!_fileSystem.File.Exists(backup))
        {
            _fileSystem.File.Copy(_path, backup);
        }

        var directory = _fileSystem.Path.GetDirectoryName(_path) ?? ".";
        var pattern = _fileSystem.Path.GetFileName(_path) + ".bak-*";
        var old = _fileSystem.Directory.GetFiles(directory, pattern).OrderByDescending(f => f, StringComparer.Ordinal).Skip(BackupsKept);
        foreach (var file in old)
        {
            _fileSystem.File.Delete(file);
        }
    }
}
