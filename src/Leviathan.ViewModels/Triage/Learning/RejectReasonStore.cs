using System.IO.Abstractions;
using System.Text.Json;

namespace Leviathan.ViewModels.Triage.Learning;

/// <summary>
/// The user's list of reject reasons, in <c>reject-reasons.json</c> (spec §10, learning). It starts empty and only the user adds to it.
/// Written atomically (temporary file, then rename) so a crash never leaves half a list; a file that cannot be read is treated as an
/// empty list rather than stopping triage. Thread-safe; the calls do file I/O, so callers run them off the UI thread.
/// </summary>
public sealed class RejectReasonStore(IFileSystem fileSystem, string path)
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private readonly Lock _lock = new();
    private List<RejectReason>? _reasons;

    /// <summary>Raised after the list changed (added, edited, removed or moved).</summary>
    public event EventHandler? Changed;

    /// <summary>The reasons, in the order the user keeps them (the chips and Shift+1…9 follow it).</summary>
    public IReadOnlyList<RejectReason> Reasons
    {
        get
        {
            lock (_lock)
            {
                return [.. Load()];
            }
        }
    }

    /// <summary>Adds a reason at the end. The title must be new (case ignored).</summary>
    public RejectReason Add(string title, string? description)
    {
        var clean = Clean(title);
        RejectReason added;
        lock (_lock)
        {
            var list = Load();
            RequireNew(list, clean, null);
            added = new RejectReason(Guid.NewGuid().ToString("N"), clean, (description ?? string.Empty).Trim());
            list.Add(added);
            Save(list);
        }

        Changed?.Invoke(this, EventArgs.Empty);
        return added;
    }

    /// <summary>Changes a reason's title and description; its id, and so every discard that used it, stays.</summary>
    public RejectReason Update(string id, string title, string? description)
    {
        var clean = Clean(title);
        RejectReason updated;
        lock (_lock)
        {
            var list = Load();
            var index = list.FindIndex(r => r.Id == id);
            if (index < 0)
            {
                throw new KeyNotFoundException("There is no such reject reason.");
            }

            RequireNew(list, clean, id);
            updated = list[index] with { Title = clean, Description = (description ?? string.Empty).Trim() };
            list[index] = updated;
            Save(list);
        }

        Changed?.Invoke(this, EventArgs.Empty);
        return updated;
    }

    /// <summary>Removes a reason from the list. Discards that used it keep their record; the learning still knows the id.</summary>
    public bool Remove(string id)
    {
        lock (_lock)
        {
            var list = Load();
            if (list.RemoveAll(r => r.Id == id) == 0)
            {
                return false;
            }

            Save(list);
        }

        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    /// <summary>The reason with <paramref name="id"/>, or null (removed, or never there).</summary>
    public RejectReason? Find(string? id)
    {
        if (string.IsNullOrEmpty(id))
        {
            return null;
        }

        lock (_lock)
        {
            return Load().FirstOrDefault(r => r.Id == id);
        }
    }

    private static string Clean(string title)
    {
        var clean = (title ?? string.Empty).Trim();
        if (clean.Length == 0)
        {
            throw new ArgumentException("A reject reason needs a title.", nameof(title));
        }

        return clean;
    }

    private static void RequireNew(List<RejectReason> list, string title, string? ownId)
    {
        if (list.Any(r => r.Id != ownId && string.Equals(r.Title, title, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException($"There is already a reject reason called \"{title}\".");
        }
    }

    // Callers hold the lock.
    private List<RejectReason> Load()
    {
        if (_reasons is not null)
        {
            return _reasons;
        }

        try
        {
            if (fileSystem.File.Exists(path))
            {
                var read = JsonSerializer.Deserialize<List<RejectReason>>(fileSystem.File.ReadAllText(path), Json);
                _reasons = [.. (read ?? []).Where(r => !string.IsNullOrWhiteSpace(r?.Id) && !string.IsNullOrWhiteSpace(r.Title))];
                return _reasons;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // Unreadable: start with none rather than stop the triage; the next save replaces the file.
        }

        _reasons = [];
        return _reasons;
    }

    private void Save(List<RejectReason> list)
    {
        if (fileSystem.Path.GetDirectoryName(path) is { Length: > 0 } folder)
        {
            fileSystem.Directory.CreateDirectory(folder);
        }

        var temp = path + ".tmp";
        fileSystem.File.WriteAllText(temp, JsonSerializer.Serialize(list, Json));
        fileSystem.File.Move(temp, path, overwrite: true);
    }
}
