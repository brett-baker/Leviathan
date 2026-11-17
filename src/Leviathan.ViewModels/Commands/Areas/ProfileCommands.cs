// View models are UI-thread affine: the awaits in this file deliberately resume on the caller's context (crash signature
// 52a0ec04c34d, see WindowCommands.cs). UiThreadAffinityTests guards this.
#pragma warning disable CA2007

using Leviathan.Core.Paths;
using Leviathan.ViewModels.Navigation;
using Leviathan.ViewModels.Windows;

namespace Leviathan.ViewModels.Commands.Areas;

/// <summary>New windows and startup profiles (spec §17.1).</summary>
internal sealed class ProfileCommands(CommandScope scope) : CommandArea(scope)
{
    /// <summary>Registers this area's commands.</summary>
    public void Register()
    {
        var windows = Scope.Windows;
        var shell = Scope.Shell;
        var home = Scope.Home;
        var profiles = Scope.Profiles;

        Add(CommandIds.WindowNew, "New window", "Windows", (c, _) =>
        {
            windows.OpenWindow(LocationPath.TryParse(c.Target, out var parsed) ? parsed : home());
            return Task.CompletedTask;
        }, shortcut: "Ctrl+N", icon: "NewWindow");

        // Startup profiles (spec §17.1). Saving and opening are the user's own actions, so a refusal says why in the status bar.
        async Task ReportProfile(Func<Task<string>> action)
        {
            try
            {
                shell.ShowMessage(await action());
            }
            catch (Sessions.ProfileException ex)
            {
                shell.ShowMessage(ex.Message);
            }
        }

        AddUsing(profiles, CommandIds.ProfileSave, "Save current windows as profile", "Profiles", (profiles, c, ct) => ReportProfile(async () =>
        {
            await profiles.SaveCurrentAsync(c.Target!, ct);
            return $"Saved the open windows as the profile \"{c.Target!.Trim()}\".";
        }), (_, c) => !string.IsNullOrWhiteSpace(c.Target));
        AddUsing(profiles, CommandIds.ProfileOpen, "Open profile", "Profiles", (profiles, c, _) => ReportProfile(() =>
        {
            var opened = profiles.Open(c.Target!, replace: c.Parameter is not false);
            return Task.FromResult($"Opened the profile \"{c.Target!.Trim()}\" ({opened} window{(opened == 1 ? string.Empty : "s")}).");
        }), (profiles, c) => profiles.Catalog.Find(c.Target) is not null);
        AddUsing(profiles, CommandIds.ProfileDelete, "Delete profile", "Profiles", (profiles, c, ct) => ReportProfile(async () =>
            await profiles.DeleteAsync(c.Target!, ct) ? $"Deleted the profile \"{c.Target!.Trim()}\"." : "There is no such profile."),
            (profiles, c) => profiles.Catalog.Find(c.Target) is not null);
    }
}
