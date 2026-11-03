using Leviathan.ViewModels.Windows;

namespace Leviathan.ViewModels.Sessions;

/// <summary>
/// Saves the open windows as a named profile and opens a profile into the running app (spec §17.1): "Save current windows as
/// profile…" and "open a profile, replacing the current windows or adding to them". Run on the UI thread: it reads and creates
/// window view models.
/// </summary>
public sealed class ProfileService
{
    private readonly WindowManager _windows;
    private readonly ProfileCatalog _catalog;
    private readonly SessionRecorder? _recorder;

    /// <summary>Creates the service. The recorder, when given, is told a session is running again after a profile opens.</summary>
    public ProfileService(WindowManager windows, ProfileCatalog catalog, SessionRecorder? recorder = null)
    {
        ArgumentNullException.ThrowIfNull(windows);
        ArgumentNullException.ThrowIfNull(catalog);
        _windows = windows;
        _catalog = catalog;
        _recorder = recorder;
    }

    /// <summary>The catalog the service works on.</summary>
    public ProfileCatalog Catalog => _catalog;

    /// <summary>Saves the windows that are open now as <paramref name="name"/>.</summary>
    /// <exception cref="ProfileException">The name is not usable or there are no tabs.</exception>
    public Task SaveCurrentAsync(string name, CancellationToken cancellationToken) =>
        _catalog.SaveAsync(name, SessionCapture.Capture(_windows), cancellationToken);

    /// <summary>
    /// Opens the profile. With <paramref name="replace"/> its windows open first and the windows that were open then close, so the
    /// app never has zero windows in between (the last window closing would end it). Returns the number of windows opened.
    /// </summary>
    /// <exception cref="ProfileException">There is no profile of that name.</exception>
    public int Open(string name, bool replace)
    {
        var profile = _catalog.Find(name) ?? throw new ProfileException($"There is no profile called \"{name}\".");
        var previous = _windows.Windows.ToList();
        var opened = SessionCapture.Restore(_windows, profile.State);
        if (replace)
        {
            foreach (var window in previous)
            {
                _windows.Close(window);
            }
        }

        _recorder?.Resume();
        _recorder?.MarkDirty();
        return opened;
    }

    /// <summary>Deletes a profile. Returns false when there was none of that name.</summary>
    public Task<bool> DeleteAsync(string name, CancellationToken cancellationToken) => _catalog.DeleteAsync(name, cancellationToken);
}
