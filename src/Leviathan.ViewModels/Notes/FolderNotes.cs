namespace Leviathan.ViewModels.Notes;

/// <summary>Where the notes are kept between runs (spec §15.6): one small JSON file in the data folder, never inside the folders themselves.</summary>
public interface IFolderNotesStorage
{
    /// <summary>The saved notes, folder path to text; empty when there are none or the file is damaged.</summary>
    IReadOnlyDictionary<string, string> Load();

    /// <summary>Saves all notes, replacing what was there.</summary>
    void Save(IReadOnlyDictionary<string, string> notes);
}

/// <summary>
/// Short notes attached to folders (spec §15.6), shown in the details pane. They live in Leviathan's own data, keyed by the folder's
/// path (case-insensitive, no trailing separator), so a share or a read-only folder gets notes without anything written to it.
/// An empty note removes the entry. <see cref="Move"/> and <see cref="Remove"/> keep notes in step with a renamed or deleted folder.
/// </summary>
public sealed class FolderNotes
{
    /// <summary>The longest note kept; the details pane is a glance, not a document.</summary>
    public const int MaxLength = 2000;

    private readonly IFolderNotesStorage _storage;
    private readonly Lock _lock = new();
    private readonly Dictionary<string, string> _notes;

    /// <summary>Loads the saved notes.</summary>
    public FolderNotes(IFolderNotesStorage storage)
    {
        ArgumentNullException.ThrowIfNull(storage);
        _storage = storage;
        _notes = new Dictionary<string, string>(storage.Load(), StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Raised after a note was set, moved or removed.</summary>
    public event EventHandler? Changed;

    /// <summary>How many folders have a note.</summary>
    public int Count
    {
        get
        {
            lock (_lock)
            {
                return _notes.Count;
            }
        }
    }

    /// <summary>The note of <paramref name="folder"/>; empty when it has none.</summary>
    public string Get(string folder)
    {
        lock (_lock)
        {
            return _notes.TryGetValue(Key(folder), out var text) ? text : string.Empty;
        }
    }

    /// <summary>True when <paramref name="folder"/> has a note.</summary>
    public bool Has(string folder) => Get(folder).Length > 0;

    /// <summary>Sets the note (trimmed and cut to <see cref="MaxLength"/>); blank text removes it.</summary>
    public void Set(string folder, string text)
    {
        var key = Key(folder);
        var clean = (text ?? string.Empty).Trim();
        if (clean.Length > MaxLength)
        {
            clean = clean[..MaxLength];
        }

        lock (_lock)
        {
            if (clean.Length == 0)
            {
                if (!_notes.Remove(key))
                {
                    return;
                }
            }
            else
            {
                if (_notes.TryGetValue(key, out var old) && old == clean)
                {
                    return;
                }

                _notes[key] = clean;
            }

            Save();
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>A folder was renamed or moved: its note, and those of folders inside it, follow.</summary>
    public void Move(string from, string to)
    {
        var source = Key(from);
        var target = Key(to);
        if (string.Equals(source, target, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var moved = false;
        lock (_lock)
        {
            foreach (var key in _notes.Keys.Where(k => IsSameOrInside(k, source)).ToList())
            {
                var text = _notes[key];
                _notes.Remove(key);
                _notes[target + key[source.Length..]] = text;
                moved = true;
            }

            if (moved)
            {
                Save();
            }
        }

        if (moved)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>A folder was deleted: its note, and those of folders inside it, go.</summary>
    public void Remove(string folder)
    {
        var source = Key(folder);
        bool removed;
        lock (_lock)
        {
            var doomed = _notes.Keys.Where(k => IsSameOrInside(k, source)).ToList();
            foreach (var key in doomed)
            {
                _notes.Remove(key);
            }

            removed = doomed.Count > 0;
            if (removed)
            {
                Save();
            }
        }

        if (removed)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    private static bool IsSameOrInside(string key, string folder) =>
        key.StartsWith(folder, StringComparison.OrdinalIgnoreCase) && (key.Length == folder.Length || key[folder.Length] == '\\');

    private static string Key(string folder)
    {
        ArgumentNullException.ThrowIfNull(folder);
        var trimmed = folder.TrimEnd('\\', '/');
        return trimmed.Length == 0 ? folder : trimmed;
    }

    private void Save()
    {
        try
        {
            _storage.Save(_notes);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A note that cannot be written stays for this run; losing a glance note never stops the app.
        }
    }
}
