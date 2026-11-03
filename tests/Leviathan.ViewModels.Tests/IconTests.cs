using System.IO.Abstractions;
using System.IO.Abstractions.TestingHelpers;
using System.Text.RegularExpressions;
using Leviathan.ViewModels.Icons;
using Leviathan.ViewModels.Settings;

namespace Leviathan.ViewModels.Tests;

public sealed class IconCatalogTests
{
    [Fact]
    public void Keys_AreUnique_AndLowerKebabCase()
    {
        var keys = IconCatalog.All.Select(d => d.Key).ToList();

        Assert.Equal(keys.Count, keys.Distinct().Count());
        Assert.All(keys, key => Assert.Matches("^[a-z]+(-[a-z]+)*$", key));
    }

    [Fact]
    public void Accents_AreHexColours()
    {
        Assert.All(IconCatalog.All.Where(d => d.Accent is not null), d => Assert.Matches("^#[0-9A-F]{6}$", d.Accent!));
    }

    [Fact]
    public void EveryIcon_HasAGlyphToFallBackTo()
    {
        Assert.All(IconCatalog.All, d => Assert.Single(d.Glyph));
    }

    [Fact]
    public void FindByGlyph_FindsTheIcon_AndTheFirstDefinitionOfASharedGlyphWins()
    {
        Assert.Equal("delete", IconCatalog.FindByGlyph("\uE74D")?.Key);
        Assert.Equal("desktop", IconCatalog.FindByGlyph("\uE7F4")?.Key);
        Assert.Null(IconCatalog.FindByGlyph("\uE000"));
        Assert.Null(IconCatalog.FindByGlyph(null));
        Assert.Null(IconCatalog.FindByGlyph(string.Empty));
    }

    [Fact]
    public void Find_ReturnsNullForAnUnknownKey()
    {
        Assert.NotNull(IconCatalog.Find("folder"));
        Assert.Null(IconCatalog.Find("no-such-icon"));
    }
}

public sealed class IconPackTests
{
    private const string Folder = @"C:\icons\demo";

    private static MockFileSystem Disk(string manifest, params (string Name, string Content)[] files)
    {
        var disk = new MockFileSystem();
        disk.AddFile($@"{Folder}\pack.json", new MockFileData(manifest));
        foreach (var (name, content) in files)
        {
            disk.AddFile($@"{Folder}\{name}", new MockFileData(content));
        }

        return disk;
    }

    private const string Manifest = """
        { "id": "demo", "name": "Demo", "license": "MIT",
          "icons": { "delete": { "file": "delete.svg" }, "file": { "file": "file.svg", "color": true } } }
        """;

    [Fact]
    public void Parse_ReadsTheManifest_IgnoringACaseDifferenceAndABom()
    {
        var manifest = IconPackManifest.Parse('\uFEFF' + Manifest.Replace("\"id\"", "\"ID\"", StringComparison.Ordinal));

        Assert.Equal("demo", manifest.Id);
        Assert.Equal("Demo", manifest.Name);
        Assert.True(manifest.Icons["file"].Color);
        Assert.False(manifest.Icons["delete"].Color);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{ \"name\": \"no id\" }")]
    [InlineData("{ \"id\": \"x\", \"icons\": { \"a\": { \"file\": \"..\\\\a.svg\" } } }")]
    [InlineData("{ \"id\": \"x\", \"icons\": { \"a\": { \"file\": \"sub/a.svg\" } } }")]
    [InlineData("{ \"id\": \"x\", \"icons\": { \"a\": { \"file\": \"C:a.svg\" } } }")]
    [InlineData("{ \"id\": \"x\", \"icons\": { \"a\": { \"file\": \"\" } } }")]
    public void Parse_RejectsAnUnusableManifest(string json)
    {
        Assert.Throws<InvalidDataException>(() => IconPackManifest.Parse(json));
    }

    [Fact]
    public void Get_ReadsTheFile_AndRemembersIt()
    {
        var disk = Disk(Manifest, ("delete.svg", "<svg><path d=\"M0 0\"/></svg>"), ("file.svg", "<svg/>"));
        var pack = new IconPack(disk, Folder, IconPackManifest.Parse(Manifest));

        var first = pack.Get("delete");
        disk.RemoveFile($@"{Folder}\delete.svg");
        var second = pack.Get("delete");

        Assert.NotNull(first);
        Assert.False(first.IsColor);
        Assert.Same(first, second);
        Assert.True(pack.Get("file")!.IsColor);
    }

    [Fact]
    public void Get_IsNullForAKeyTheManifestLacks_AFileThatIsMissing_OrOneThatIsNotSvg()
    {
        var disk = Disk(Manifest, ("file.svg", "this is not an svg"));
        var pack = new IconPack(disk, Folder, IconPackManifest.Parse(Manifest));

        Assert.Null(pack.Get("copy"));
        Assert.Null(pack.Get("delete"));
        Assert.Null(pack.Get("file"));
        Assert.True(pack.Has("delete"));
        Assert.False(pack.Has("copy"));
    }

    [Fact]
    public void Discover_FindsPacks_SkipsDamagedOnes_AndPutsTheDefaultFirst()
    {
        var disk = new MockFileSystem();
        disk.AddFile(@"C:\icons\zebra\pack.json", new MockFileData("{ \"id\": \"zebra\", \"name\": \"Zebra\" }"));
        disk.AddFile(@"C:\icons\fluent\pack.json", new MockFileData("{ \"id\": \"fluent\", \"name\": \"Fluent\" }"));
        disk.AddFile(@"C:\icons\broken\pack.json", new MockFileData("{ nope"));
        disk.AddDirectory(@"C:\icons\empty");

        var packs = IconPacks.Discover(disk, @"C:\icons");

        Assert.Equal(["fluent", "zebra"], packs.All.Select(p => p.Id));
    }

    [Fact]
    public void Discover_WithNoIconsFolder_GivesNoPacks()
    {
        var packs = IconPacks.Discover(new MockFileSystem(), @"C:\missing");

        Assert.Empty(packs.All);
        Assert.Null(packs.Resolve("fluent"));
    }

    [Fact]
    public void Resolve_FallsBackToTheDefault_ThenToTheFirstPack()
    {
        var fluent = new IconPack(new MockFileSystem(), Folder, new IconPackManifest { Id = "fluent" });
        var other = new IconPack(new MockFileSystem(), Folder, new IconPackManifest { Id = "other" });

        Assert.Equal("other", new IconPacks([fluent, other]).Resolve("OTHER")?.Id);
        Assert.Equal("fluent", new IconPacks([fluent, other]).Resolve("gone")?.Id);
        Assert.Equal("fluent", new IconPacks([fluent, other]).Resolve(null)?.Id);
        Assert.Equal("other", new IconPacks([other]).Resolve("gone")?.Id);
    }
}

public sealed class SvgTintTests
{
    [Fact]
    public void Apply_ReplacesTheFixedFill_OfAFluentIcon_AndKeepsNone()
    {
        const string svg = "<svg width=\"20\" fill=\"none\"><path d=\"M0 0\" fill=\"#212121\"/><path d=\"M1 1\" fill=\"#fff\"/></svg>";

        var result = SvgTint.Apply(svg, "#D13438");

        Assert.Equal("<svg width=\"20\" fill=\"none\"><path d=\"M0 0\" fill=\"#D13438\"/><path d=\"M1 1\" fill=\"#D13438\"/></svg>", result);
    }

    [Fact]
    public void Apply_SetsTheFillOnTheRoot_WhenTheFileHasNone_AsFontAwesomeDoes()
    {
        const string svg = "<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 448 512\"><path d=\"M0 0\"/></svg>";

        var result = SvgTint.Apply(svg, "#FFFFFF");

        Assert.Equal("<svg fill=\"#FFFFFF\" xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 448 512\"><path d=\"M0 0\"/></svg>", result);
    }

    [Fact]
    public void Apply_LeavesGradientReferencesAlone()
    {
        const string svg = "<svg fill=\"none\"><path fill=\"url(#paint0)\"/></svg>";

        Assert.Equal(svg, SvgTint.Apply(svg, "#000000"));
    }
}

/// <summary>The packs that ship with the app are complete and consistent with the catalog.</summary>
public sealed class ShippedIconPackTests
{
    private static string IconsFolder()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "src", "Leviathan.App", "Assets", "Icons")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return Path.Combine(directory.FullName, "src", "Leviathan.App", "Assets", "Icons");
    }

    public static TheoryData<string> PackIds => ["fluent", "fontawesome"];

    [Theory]
    [MemberData(nameof(PackIds))]
    public void ThePack_DrawsEveryCatalogKey_WithRealSvgFiles(string id)
    {
        var packs = IconPacks.Discover(new FileSystem(), IconsFolder());
        var pack = packs.All.Single(p => p.Id == id);

        var missing = IconCatalog.All.Where(d => pack.Get(d.Key) is null).Select(d => d.Key).ToList();
        var unknown = pack.Manifest.Icons.Keys.Where(k => IconCatalog.Find(k) is null).ToList();

        Assert.Empty(missing);
        Assert.Empty(unknown);
        Assert.False(string.IsNullOrWhiteSpace(pack.Manifest.License));
        Assert.False(string.IsNullOrWhiteSpace(pack.Manifest.Attribution));
    }

    [Theory]
    [MemberData(nameof(PackIds))]
    public void ThePack_HasItsLicenceFile(string id)
    {
        Assert.True(File.Exists(Path.Combine(IconsFolder(), id, "LICENSE.txt")));
    }

    [Fact]
    public void ASingleColourIcon_ComesOutTintedWithNoFixedColourLeft()
    {
        var packs = IconPacks.Discover(new FileSystem(), IconsFolder());
        foreach (var pack in packs.All)
        {
            foreach (var key in pack.Manifest.Icons.Keys.Where(k => !pack.Manifest.Icons[k].Color))
            {
                var tinted = SvgTint.Apply(pack.Get(key)!.Svg, "#123456");

                Assert.DoesNotMatch(new Regex("fill=\"#(?!123456)[0-9a-fA-F]{3,8}\""), tinted);
                Assert.Contains("#123456", tinted, StringComparison.Ordinal);
            }
        }
    }
}

public sealed class IconPackSettingTests
{
    [Fact]
    public void TheDefaultPack_IsFluent_AndMatchesTheDefaultTheCatalogFallsBackTo()
    {
        Assert.Equal(IconPacks.DefaultId, new AppearanceSettings().IconPack);
    }

    [Fact]
    public async Task TheChosenPack_IsSaved_AndComesBackAfterARestart()
    {
        var disk = new MockFileSystem();
        using var store = new SettingsStore(disk, @"C:\data\settings.json");
        await store.LoadAsync(CancellationToken.None);
        await store.UpdateAsync(s => s with { Appearance = s.Appearance with { IconPack = "fontawesome" } }, CancellationToken.None);

        using var reopened = new SettingsStore(disk, @"C:\data\settings.json");
        var settings = await reopened.LoadAsync(CancellationToken.None);

        Assert.Equal("fontawesome", settings.Appearance.IconPack);
    }

    [Fact]
    public async Task AnOldSettingsFile_WithoutThePack_GetsTheDefault()
    {
        var disk = new MockFileSystem();
        disk.AddFile(@"C:\data\settings.json", new MockFileData("{ \"Appearance\": { \"Theme\": \"Dark\" } }"));
        using var store = new SettingsStore(disk, @"C:\data\settings.json");

        var settings = await store.LoadAsync(CancellationToken.None);

        Assert.Equal("fluent", settings.Appearance.IconPack);
        Assert.Equal(ThemeMode.Dark, settings.Appearance.Theme);
    }
}
