using System.IO.Abstractions;
using System.Text.Json;
using System.Text.Json.Serialization;
using Leviathan.Core.Paths;

namespace Leviathan.ViewModels.Sessions;

/// <summary>Writes <see cref="LocationPath"/> as its text, so saved sessions are readable and survive changes to the type.</summary>
internal sealed class LocationPathJsonConverter : JsonConverter<LocationPath>
{
    public override LocationPath Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.GetString() is { } text && LocationPath.TryParse(text, out var path) ? path : throw new JsonException("Not a location.");

    public override void Write(Utf8JsonWriter writer, LocationPath value, JsonSerializerOptions options) => writer.WriteStringValue(value.Value);
}

/// <summary>
/// A small JSON file written atomically (temp file, flush, rename) and read defensively: a file that cannot be read is set aside
/// as <c>.corrupt</c> and treated as absent, so a bad write or a bad hand edit costs the saved sessions and never stops the app
/// from starting (spec §3.4, §17.1).
/// </summary>
/// <typeparam name="T">The file's content.</typeparam>
public sealed class JsonFileStore<T> : IDisposable
    where T : class
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter(), new LocationPathJsonConverter() },
    };

    private readonly IFileSystem _fileSystem;
    private readonly string _path;
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>Creates a store over <paramref name="path"/>.</summary>
    public JsonFileStore(IFileSystem fileSystem, string path)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _fileSystem = fileSystem;
        _path = path;
    }

    /// <summary>True when the last <see cref="LoadAsync"/> found an unreadable file and set it aside.</summary>
    public bool RecoveredFromCorruption { get; private set; }

    /// <inheritdoc />
    public void Dispose() => _gate.Dispose();

    /// <summary>Reads the file, or null when it is missing or unreadable.</summary>
    public async Task<T?> LoadAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            RecoveredFromCorruption = false;
            if (!_fileSystem.File.Exists(_path))
            {
                return null;
            }

            try
            {
                var text = await _fileSystem.File.ReadAllTextAsync(_path, cancellationToken).ConfigureAwait(false);
                return JsonSerializer.Deserialize<T>(text, Json);
            }
            catch (JsonException)
            {
                SetAside();
                return null;
            }
            catch (IOException)
            {
                // Locked by something else for a moment: start without it rather than wait.
                return null;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Writes <paramref name="value"/> atomically.</summary>
    public async Task SaveAsync(T value, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(value);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var directory = _fileSystem.Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(directory) && !_fileSystem.Directory.Exists(directory))
            {
                _fileSystem.Directory.CreateDirectory(directory);
            }

            var temp = _path + ".tmp";
            var bytes = JsonSerializer.SerializeToUtf8Bytes(value, Json);
            var stream = _fileSystem.File.Create(temp);
            await using (stream.ConfigureAwait(false))
            {
                await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            _fileSystem.File.Move(temp, _path, overwrite: true);
        }
        finally
        {
            _gate.Release();
        }
    }

    private void SetAside()
    {
        RecoveredFromCorruption = true;
        try
        {
            _fileSystem.File.Move(_path, _path + ".corrupt", overwrite: true);
        }
        catch (IOException)
        {
            // Leave it; the next save replaces it.
        }
    }
}
