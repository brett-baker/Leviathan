using Leviathan.Core.Model;
using Leviathan.Core.Paths;
using Leviathan.ViewModels.Views;

namespace Leviathan.ViewModels.Tests;

public sealed class FolderViewRulesTests
{
    private static LocationPath P(string path) => LocationPath.Parse(path);

    private static FolderViewSettings Layout(ViewLayout layout) => new() { Layout = layout };

    [Fact]
    public void Resolve_NoRules_ReturnsGlobalDefault()
    {
        var rules = new FolderViewRules();

        Assert.Equal(ViewLayout.Details, rules.Resolve(P(@"C:\Videos"), FolderKind.Videos).Layout);
    }

    [Fact]
    public void Resolve_ThisFolderRule_AppliesOnlyToThatFolder()
    {
        var rules = new FolderViewRules();
        rules.Set(P(@"C:\Photos"), ViewScope.ThisFolder, Layout(ViewLayout.Thumbnails));

        Assert.Equal(ViewLayout.Thumbnails, rules.Resolve(P(@"C:\Photos"), FolderKind.General).Layout);
        Assert.Equal(ViewLayout.Details, rules.Resolve(P(@"C:\Photos\2026"), FolderKind.General).Layout);
    }

    [Fact]
    public void Resolve_SubfoldersRule_AppliesToFolderAndDescendants_CaseInsensitively()
    {
        var rules = new FolderViewRules();
        rules.Set(P(@"C:\Photos"), ViewScope.ThisFolderAndSubfolders, Layout(ViewLayout.Thumbnails));

        Assert.Equal(ViewLayout.Thumbnails, rules.Resolve(P(@"c:\photos"), FolderKind.General).Layout);
        Assert.Equal(ViewLayout.Thumbnails, rules.Resolve(P(@"C:\Photos\2026\Oct"), FolderKind.General).Layout);
        Assert.Equal(ViewLayout.Details, rules.Resolve(P(@"C:\PhotosOther"), FolderKind.General).Layout);
    }

    [Fact]
    public void Resolve_NearestAncestorWins()
    {
        var rules = new FolderViewRules();
        rules.Set(P(@"C:\Media"), ViewScope.ThisFolderAndSubfolders, Layout(ViewLayout.List));
        rules.Set(P(@"C:\Media\Photos"), ViewScope.ThisFolderAndSubfolders, Layout(ViewLayout.Thumbnails));

        Assert.Equal(ViewLayout.Thumbnails, rules.Resolve(P(@"C:\Media\Photos\2026"), FolderKind.General).Layout);
        Assert.Equal(ViewLayout.List, rules.Resolve(P(@"C:\Media\Music"), FolderKind.General).Layout);
    }

    [Fact]
    public void Resolve_ExactFolderBeatsAncestorSubtreeRule()
    {
        var rules = new FolderViewRules();
        rules.Set(P(@"C:\Media"), ViewScope.ThisFolderAndSubfolders, Layout(ViewLayout.List));
        rules.Set(P(@"C:\Media\Inbox"), ViewScope.ThisFolder, Layout(ViewLayout.Details));

        Assert.Equal(ViewLayout.Details, rules.Resolve(P(@"C:\Media\Inbox"), FolderKind.General).Layout);
        Assert.Equal(ViewLayout.List, rules.Resolve(P(@"C:\Media\Inbox\Sub"), FolderKind.General).Layout);
    }

    [Fact]
    public void Resolve_KindRule_AppliesWhereNoFolderRuleDoes_AndFolderRulesBeatIt()
    {
        var rules = new FolderViewRules();
        rules.SetForKind(FolderKind.Videos, Layout(ViewLayout.Tiles));
        rules.Set(P(@"C:\Special"), ViewScope.ThisFolder, Layout(ViewLayout.List));

        Assert.Equal(ViewLayout.Tiles, rules.Resolve(P(@"C:\Any"), FolderKind.Videos).Layout);
        Assert.Equal(ViewLayout.List, rules.Resolve(P(@"C:\Special"), FolderKind.Videos).Layout);
        Assert.Equal(ViewLayout.Details, rules.Resolve(P(@"C:\Any"), FolderKind.Music).Layout);
    }

    [Fact]
    public void Resolve_RuleOnOtherDriveOrServer_DoesNotLeak()
    {
        var rules = new FolderViewRules();
        rules.Set(P(@"C:\Media"), ViewScope.ThisFolderAndSubfolders, Layout(ViewLayout.List));

        Assert.Equal(ViewLayout.Details, rules.Resolve(P(@"D:\Media"), FolderKind.General).Layout);
        Assert.Equal(ViewLayout.Details, rules.Resolve(P(@"\\nas\media\x"), FolderKind.General).Layout);
    }

    [Fact]
    public void Clear_RemovesRulesForThatFolderOnly_AndRaisesChanged()
    {
        var rules = new FolderViewRules();
        rules.Set(P(@"C:\A"), ViewScope.ThisFolder, Layout(ViewLayout.List));
        rules.Set(P(@"C:\A"), ViewScope.ThisFolderAndSubfolders, Layout(ViewLayout.List));
        rules.Set(P(@"C:\B"), ViewScope.ThisFolder, Layout(ViewLayout.List));
        var raised = 0;
        rules.Changed += (_, _) => raised++;

        Assert.True(rules.Clear(P(@"C:\A")));
        Assert.False(rules.Clear(P(@"C:\A")));

        Assert.Equal(1, raised);
        Assert.Equal(ViewLayout.Details, rules.Resolve(P(@"C:\A\x"), FolderKind.General).Layout);
        Assert.Equal(ViewLayout.List, rules.Resolve(P(@"C:\B"), FolderKind.General).Layout);
    }

    [Fact]
    public void Snapshot_ThenLoad_RoundTripsAllScopes()
    {
        var rules = new FolderViewRules();
        rules.Set(P(@"C:\A"), ViewScope.ThisFolder, Layout(ViewLayout.List));
        rules.Set(P(@"C:\B"), ViewScope.ThisFolderAndSubfolders, Layout(ViewLayout.Thumbnails));
        rules.SetForKind(FolderKind.Music, Layout(ViewLayout.Tiles));

        var copy = new FolderViewRules();
        copy.Load(rules.Snapshot());

        Assert.Equal(3, copy.Snapshot().Count);
        Assert.Equal(ViewLayout.List, copy.Resolve(P(@"C:\A"), FolderKind.General).Layout);
        Assert.Equal(ViewLayout.Thumbnails, copy.Resolve(P(@"C:\B\c"), FolderKind.General).Layout);
        Assert.Equal(ViewLayout.Tiles, copy.Resolve(P(@"C:\Z"), FolderKind.Music).Layout);
    }

    [Fact]
    public void Load_SkipsMalformedRules()
    {
        var rules = new FolderViewRules();

        rules.Load(
        [
            new FolderViewRule(ViewScope.ThisFolder, null, null, Layout(ViewLayout.List)),
            new FolderViewRule(ViewScope.AllFoldersOfType, P(@"C:\A"), null, Layout(ViewLayout.List)),
        ]);

        Assert.Empty(rules.Snapshot());
    }

    [Fact]
    public void Set_WithTypeScope_Throws()
    {
        Assert.Throws<ArgumentException>(() => new FolderViewRules().Set(P(@"C:\A"), ViewScope.AllFoldersOfType, Layout(ViewLayout.List)));
    }

    private static FileEntry File(string name) =>
        new(name, P(@"C:\x").Combine(name), FileEntryKind.File, 1, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, FileAttributes.Normal);

    private static FileEntry Folder(string name) =>
        new(name, P(@"C:\x").Combine(name), FileEntryKind.Folder, 0, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, FileAttributes.Directory);

    [Fact]
    public void Detect_MostlyVideos_IsVideos_IgnoringFolders()
    {
        var entries = new[] { File("a.mkv"), File("b.MP4"), File("c.mov"), File("notes.txt"), Folder("sub"), Folder("sub2") };

        Assert.Equal(FolderKind.Videos, FolderKindDetector.Detect(entries));
    }

    [Theory]
    [InlineData("a.jpg", "b.png", "c.cr3", FolderKind.Pictures)]
    [InlineData("a.flac", "b.mp3", "c.m4a", FolderKind.Music)]
    [InlineData("a.pdf", "b.docx", "c.xlsx", FolderKind.Documents)]
    public void Detect_DominantType_NamesTheFolder(string a, string b, string c, FolderKind expected)
    {
        Assert.Equal(expected, FolderKindDetector.Detect([File(a), File(b), File(c)]));
    }

    [Fact]
    public void Detect_MixedOrEmpty_IsGeneral()
    {
        Assert.Equal(FolderKind.General, FolderKindDetector.Detect([File("a.mkv"), File("b.jpg"), File("c.mp3"), File("d.zip")]));
        Assert.Equal(FolderKind.General, FolderKindDetector.Detect([]));
        Assert.Equal(FolderKind.General, FolderKindDetector.Detect([Folder("only")]));
    }
}
