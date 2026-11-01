using System.IO.Abstractions.TestingHelpers;
using Leviathan.Testing;
using Leviathan.ViewModels.Settings;

namespace Leviathan.ViewModels.Tests;

public sealed class SettingsStoreTests
{
    private const string Path = @"C:\data\settings.json";

    private static (SettingsStore Store, MockFileSystem Fs, ManualTimeProvider Time) Make(string? existing = null)
    {
        var fs = new MockFileSystem();
        if (existing is not null)
        {
            fs.AddFile(Path, new MockFileData(existing));
        }

        var time = new ManualTimeProvider(new DateTimeOffset(2026, 11, 1, 12, 0, 0, TimeSpan.Zero));
        return (new SettingsStore(fs, Path, time), fs, time);
    }

    [Fact]
    public async Task LoadAsync_MissingFile_GivesDefaults()
    {
        var (store, _, _) = Make();

        var settings = await store.LoadAsync(CancellationToken.None);

        Assert.Equal(StartupMode.LastSession, settings.General.Startup);
        Assert.True(settings.Files.ShowExtensions);
        Assert.False(store.RecoveredFromCorruption);
    }

    [Fact]
    public async Task UpdateAsync_WritesFile_RoundTripsAndRaisesChanged()
    {
        var (store, fs, _) = Make();
        AppSettings? seen = null;
        store.Changed += (_, s) => seen = s;

        await store.UpdateAsync(s => s with { Appearance = s.Appearance with { Theme = ThemeMode.Dark, Density = Density.Compact } }, CancellationToken.None);

        Assert.Equal(ThemeMode.Dark, seen?.Appearance.Theme);
        Assert.Contains("\"Dark\"", fs.File.ReadAllText(Path));
        var reloaded = new SettingsStore(fs, Path);
        var settings = await reloaded.LoadAsync(CancellationToken.None);
        Assert.Equal(ThemeMode.Dark, settings.Appearance.Theme);
        Assert.Equal(Density.Compact, settings.Appearance.Density);
    }

    [Fact]
    public async Task UpdateAsync_LeavesNoTempFileBehind()
    {
        var (store, fs, _) = Make();

        await store.UpdateAsync(s => s with { Files = s.Files with { ShowHidden = true } }, CancellationToken.None);

        Assert.False(fs.File.Exists(Path + ".tmp"));
        Assert.True(fs.File.Exists(Path));
    }

    [Fact]
    public async Task LoadAsync_CorruptFile_IsSetAsideAndDefaultsUsed()
    {
        var (store, fs, _) = Make("{ this is not json");

        var settings = await store.LoadAsync(CancellationToken.None);

        Assert.True(store.RecoveredFromCorruption);
        Assert.Equal(new AppSettings().General, settings.General);
        Assert.False(fs.File.Exists(Path));
        Assert.Equal("{ this is not json", fs.File.ReadAllText(Path + ".corrupt"));
    }

    [Fact]
    public async Task LoadAsync_CommentsTrailingCommasUnknownPropertiesAndNullSections_AreTolerated()
    {
        var (store, _, _) = Make("""
            {
              // hand edited
              "futureSetting": 42,
              "general": null,
              "appearance": { "theme": "light", },
            }
            """);

        var settings = await store.LoadAsync(CancellationToken.None);

        Assert.Equal(ThemeMode.Light, settings.Appearance.Theme);
        Assert.NotNull(settings.General);
        Assert.False(store.RecoveredFromCorruption);
    }

    [Fact]
    public async Task UpdateAsync_BacksUpOncePerDay()
    {
        var (store, fs, time) = Make();
        await store.UpdateAsync(s => s, CancellationToken.None);
        await store.UpdateAsync(s => s with { Files = s.Files with { ShowHidden = true } }, CancellationToken.None);
        await store.UpdateAsync(s => s with { Files = s.Files with { ShowSystem = true } }, CancellationToken.None);

        Assert.Single(fs.Directory.GetFiles(@"C:\data", "settings.json.bak-*"));

        time.Advance(TimeSpan.FromDays(1));
        await store.UpdateAsync(s => s with { Tabs = s.Tabs with { CtrlTabUsesMruOrder = true } }, CancellationToken.None);

        Assert.Equal(2, fs.Directory.GetFiles(@"C:\data", "settings.json.bak-*").Length);
    }

    [Fact]
    public async Task UpdateAsync_KeepsOnlyTheNewestBackups()
    {
        var (store, fs, time) = Make();
        await store.UpdateAsync(s => s, CancellationToken.None);
        for (var day = 0; day < SettingsStore.BackupsKept + 3; day++)
        {
            time.Advance(TimeSpan.FromDays(1));
            await store.UpdateAsync(s => s with { General = s.General with { NewTabLocation = "C:\\" + day } }, CancellationToken.None);
        }

        var backups = fs.Directory.GetFiles(@"C:\data", "settings.json.bak-*");
        Assert.Equal(SettingsStore.BackupsKept, backups.Length);
    }

    [Fact]
    public async Task UpdateAsync_Cancelled_DoesNotChangeCurrent()
    {
        var (store, _, _) = Make();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.UpdateAsync(s => s with { Files = s.Files with { ShowHidden = true } }, cts.Token));

        Assert.False(store.Current.Files.ShowHidden);
    }

    [Fact]
    public async Task UpdateAsync_ConcurrentUpdates_AreSerializedAndNoneIsLost()
    {
        var (store, _, _) = Make();

        var tasks = Enumerable.Range(0, 20).Select(i =>
            store.UpdateAsync(s => { s.KeyBindings["cmd" + i] = "Ctrl+" + (char)('A' + (i % 26)); return s; }, CancellationToken.None));
        await Task.WhenAll(tasks);

        Assert.Equal(20, store.Current.KeyBindings.Count);
    }
}
