using Leviathan.ViewModels.Settings;

namespace Leviathan.ViewModels.Sessions;

/// <summary>Why a profile could not be saved.</summary>
public sealed class ProfileException : Exception
{
    /// <summary>Creates the exception.</summary>
    public ProfileException(string message)
        : base(message)
    {
    }
}

/// <summary>
/// The named profiles (spec §17.1): "Save current windows as profile…", delete, and lookup for opening one. Kept apart from Last
/// session, which is rewritten every few seconds, so a profile can never be lost to a crash during that write.
/// </summary>
public sealed class ProfileCatalog : IDisposable
{
    /// <summary>Longest profile name.</summary>
    public const int MaxNameLength = 60;

    private readonly JsonFileStore<ProfilesFile> _store;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private ProfilesFile _file = ProfilesFile.Empty;

    /// <summary>Creates the catalog.</summary>
    public ProfileCatalog(JsonFileStore<ProfilesFile> store, TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        _store = store;
        _time = time ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public void Dispose() => _gate.Dispose();

    /// <summary>Raised after profiles were saved or deleted.</summary>
    public event EventHandler? Changed;

    /// <summary>The profiles.</summary>
    public IReadOnlyList<SavedProfile> Profiles => Volatile.Read(ref _file).Profiles;

    /// <summary>Reads the file. A missing or unreadable file gives no profiles.</summary>
    public async Task LoadAsync(CancellationToken cancellationToken)
    {
        var file = await _store.LoadAsync(cancellationToken).ConfigureAwait(false);
        Volatile.Write(ref _file, file is { Profiles: not null } ? file : ProfilesFile.Empty);
    }

    /// <summary>The profile called <paramref name="name"/> (ignoring case), or null.</summary>
    public SavedProfile? Find(string? name) =>
        name is null ? null : Profiles.FirstOrDefault(p => string.Equals(p.Name, name.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>Saves <paramref name="state"/> as <paramref name="name"/>, replacing a profile of the same name.</summary>
    /// <exception cref="ProfileException">The name is empty or too long, or there is nothing to save.</exception>
    public async Task SaveAsync(string name, SessionState state, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(state);
        var clean = (name ?? string.Empty).Trim();
        if (clean.Length == 0)
        {
            throw new ProfileException("Give the profile a name.");
        }

        if (clean.Length > MaxNameLength || clean.Any(char.IsControl))
        {
            throw new ProfileException($"A profile name is at most {MaxNameLength} characters, without control characters.");
        }

        if (state.IsEmpty)
        {
            throw new ProfileException("There are no tabs to save.");
        }

        await Update(profiles =>
        {
            var saved = new SavedProfile(clean, _time.GetUtcNow(), state);
            var index = profiles.FindIndex(p => string.Equals(p.Name, clean, StringComparison.OrdinalIgnoreCase));
            if (index >= 0)
            {
                profiles[index] = saved;
            }
            else
            {
                profiles.Add(saved);
            }

            return true;
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Deletes a profile. Returns false when there was none of that name.</summary>
    public Task<bool> DeleteAsync(string name, CancellationToken cancellationToken) =>
        Update(profiles => profiles.RemoveAll(p => string.Equals(p.Name, (name ?? string.Empty).Trim(), StringComparison.OrdinalIgnoreCase)) > 0, cancellationToken);

    private async Task<bool> Update(Func<List<SavedProfile>, bool> change, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var profiles = Volatile.Read(ref _file).Profiles.ToList();
            if (!change(profiles))
            {
                return false;
            }

            var updated = new ProfilesFile(ProfilesFile.CurrentSchemaVersion, profiles);
            await _store.SaveAsync(updated, cancellationToken).ConfigureAwait(false);
            Volatile.Write(ref _file, updated);
        }
        finally
        {
            _gate.Release();
        }

        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }
}

/// <summary>What the app does when it starts.</summary>
public enum StartupAction
{
    /// <summary>One window at Home: nothing to restore.</summary>
    Fresh,

    /// <summary>Open Last session.</summary>
    LastSession,

    /// <summary>Open a named profile.</summary>
    Profile,

    /// <summary>Let the user choose.</summary>
    Ask,
}

/// <summary>The decision about startup.</summary>
/// <param name="Action">What to do.</param>
/// <param name="State">The windows to open for <see cref="StartupAction.LastSession"/> and <see cref="StartupAction.Profile"/>.</param>
/// <param name="ProfileName">The profile opened, if any.</param>
/// <param name="Note">Something the user should hear (a chosen profile that no longer exists), or null.</param>
public sealed record StartupPlan(StartupAction Action, SessionState? State, string? ProfileName, string? Note = null);

/// <summary>Chooses what to open at startup from the settings, Last session and the profiles (spec §17.1). Pure.</summary>
public static class StartupPlanner
{
    /// <summary>Decides.</summary>
    public static StartupPlan Decide(GeneralSettings general, SessionFile? lastSession, ProfileCatalog profiles)
    {
        ArgumentNullException.ThrowIfNull(general);
        ArgumentNullException.ThrowIfNull(profiles);
        var last = lastSession?.State is { IsEmpty: false } state ? state : null;
        StartupPlan FromLast(string? note = null) => last is null
            ? new StartupPlan(StartupAction.Fresh, null, null, note)
            : new StartupPlan(StartupAction.LastSession, last, null, note);

        switch (general.Startup)
        {
            case StartupMode.Profile:
                if (profiles.Find(general.StartupProfile) is { } profile)
                {
                    return new StartupPlan(StartupAction.Profile, profile.State, profile.Name);
                }

                return FromLast($"The profile \"{general.StartupProfile}\" no longer exists; opened the last session instead.");

            case StartupMode.Ask:
                return last is null && profiles.Profiles.Count == 0
                    ? new StartupPlan(StartupAction.Fresh, null, null)
                    : new StartupPlan(StartupAction.Ask, last, null);

            default:
                return FromLast();
        }
    }
}
