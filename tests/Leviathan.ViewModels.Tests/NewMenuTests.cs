using System.IO.Abstractions.TestingHelpers;
using Leviathan.Core.Paths;
using Leviathan.Core.Platform;
using Leviathan.Testing;
using Leviathan.ViewModels.NewMenu;

namespace Leviathan.ViewModels.Tests;

public sealed class NewMenuTests
{
    private const string Templates = @"C:\Users\b\AppData\Roaming\Leviathan\Templates";

    private static NewMenuOptions Options() => new(
        Templates,
        [@"C:\Windows\ShellNew", @"C:\Users\b\AppData\Roaming\Microsoft\Windows\Templates"],
        text => text.Replace("%SystemRoot%", @"C:\Windows", StringComparison.OrdinalIgnoreCase));

    private static async Task<IReadOnlyList<NewItemTemplate>> Load(FakeRegistry registry, MockFileSystem? fs = null) =>
        await new ShellNewCatalog(registry, fs ?? new MockFileSystem(), Options()).GetTemplatesAsync(CancellationToken.None);

    private static FakeRegistry WordRegistry(FakeRegistry? registry = null)
    {
        registry ??= new FakeRegistry();
        registry.Set(RegistryRoot.ClassesRoot, ".docx", null, "Word.Document.12");
        registry.Set(RegistryRoot.ClassesRoot, "Word.Document.12", null, "Microsoft Word Document");
        registry.Set(RegistryRoot.ClassesRoot, @".docx\Word.Document.12\ShellNew", "FileName", "winword.docx");
        return registry;
    }

    [Fact]
    public async Task Catalog_AlwaysStartsWithTheBuiltInItems()
    {
        var items = await Load(new FakeRegistry());

        Assert.Equal(["Folder", "Text document", "Rich text document", "Bitmap image", "Compressed (zipped) folder"], items.Select(i => i.DisplayName));
        Assert.All(items, i => Assert.Equal(NewItemSource.BuiltIn, i.Source));
    }

    [Fact]
    public async Task Catalog_ReadsAppsRegisteredThroughShellNew_WithTheirFileTypeName()
    {
        var fs = new MockFileSystem();
        fs.AddFile(@"C:\Windows\ShellNew\winword.docx", new MockFileData("template"));

        var items = await Load(WordRegistry(), fs);

        var word = Assert.Single(items, i => i.Source == NewItemSource.Registry);
        Assert.Equal("Microsoft Word Document", word.DisplayName);
        Assert.Equal(".docx", word.Extension);
        Assert.Equal("New Microsoft Word Document.docx", word.DefaultName);
        Assert.Equal(new FileTemplateContent(@"C:\Windows\ShellNew\winword.docx"), word.Content);
    }

    [Fact]
    public async Task Catalog_FindsShellNewDirectlyUnderTheExtension_AndNullFileAndDataEntries()
    {
        var registry = new FakeRegistry()
            .Set(RegistryRoot.ClassesRoot, @".log\ShellNew", "NullFile", "")
            .Set(RegistryRoot.ClassesRoot, @".md\ShellNew", "Data", new byte[] { 1, 2, 3 })
            .Set(RegistryRoot.ClassesRoot, @".csv\ShellNew", "Data", "a,b");

        var items = await Load(registry);

        Assert.IsType<EmptyFileContent>(items.Single(i => i.Extension == ".log").Content);
        Assert.Equal([1, 2, 3], ((FileDataContent)items.Single(i => i.Extension == ".md").Content).Bytes);
        Assert.Equal("a,b"u8.ToArray(), ((FileDataContent)items.Single(i => i.Extension == ".csv").Content).Bytes);
        Assert.Equal("LOG file", items.Single(i => i.Extension == ".log").DisplayName);
    }

    [Fact]
    public async Task Catalog_SkipsShortcutTypes_CommandEntries_AndTemplatesThatAreMissing()
    {
        var registry = new FakeRegistry()
            .Set(RegistryRoot.ClassesRoot, @".lnk\ShellNew", "NullFile", "")
            .Set(RegistryRoot.ClassesRoot, @".url\ShellNew", "NullFile", "")
            .Set(RegistryRoot.ClassesRoot, @".bfc\ShellNew", "Command", "briefcase.exe")
            .Set(RegistryRoot.ClassesRoot, @".gone\ShellNew", "FileName", "missing.gone")
            .Set(RegistryRoot.ClassesRoot, @".txt2\ShellNew", "Unrelated", "x");

        var items = await Load(registry);

        Assert.All(items, i => Assert.Equal(NewItemSource.BuiltIn, i.Source));
    }

    [Fact]
    public async Task Catalog_BuiltInsWin_OverARegisteredEntryForTheSameExtension()
    {
        var registry = new FakeRegistry().Set(RegistryRoot.ClassesRoot, @".txt\ShellNew", "NullFile", "");

        var items = await Load(registry);

        Assert.Single(items, i => i.Extension == ".txt");
        Assert.Equal(NewItemSource.BuiltIn, items.Single(i => i.Extension == ".txt").Source);
    }

    [Fact]
    public async Task Catalog_RegisteredItemsAreSortedByName()
    {
        var registry = new FakeRegistry()
            .Set(RegistryRoot.ClassesRoot, @".zzz\ShellNew", "NullFile", "")
            .Set(RegistryRoot.ClassesRoot, @".aaa\ShellNew", "NullFile", "");

        var registered = (await Load(registry)).Where(i => i.Source == NewItemSource.Registry).Select(i => i.Extension);

        Assert.Equal([".aaa", ".zzz"], registered);
    }

    [Fact]
    public async Task Catalog_UserTemplates_AreEveryFileInTheTemplatesFolder()
    {
        var fs = new MockFileSystem();
        fs.AddFile(Templates + @"\Notes.md", new MockFileData("# notes"));
        fs.AddFile(Templates + @"\.gitignore", new MockFileData("bin/"));
        fs.AddFile(Templates + @"\desktop.ini", new MockFileData("x"));

        var user = (await Load(new FakeRegistry(), fs)).Where(i => i.Source == NewItemSource.UserTemplate).ToList();

        Assert.Equal([".gitignore", "Notes"], user.Select(i => i.DisplayName));
        Assert.Equal(new[] { ".gitignore", "Notes.md" }, user.Select(i => i.DefaultName).Order().ToArray());
        Assert.Equal(new FileTemplateContent(Templates + @"\Notes.md"), user.Single(i => i.DisplayName == "Notes").Content);
    }

    [Fact]
    public async Task Catalog_WithoutATemplatesFolder_IsStillFine()
    {
        var items = await Load(new FakeRegistry());

        Assert.DoesNotContain(items, i => i.Source == NewItemSource.UserTemplate);
    }

    [Fact]
    public async Task Catalog_AbsoluteAndEnvironmentFileNames_AreResolved()
    {
        var fs = new MockFileSystem();
        fs.AddFile(@"C:\Windows\Custom\blank.xyz", new MockFileData("x"));
        var registry = new FakeRegistry().Set(RegistryRoot.ClassesRoot, @".xyz\ShellNew", "FileName", @"%SystemRoot%\Custom\blank.xyz");

        var items = await Load(registry, fs);

        Assert.Equal(new FileTemplateContent(@"C:\Windows\Custom\blank.xyz"), items.Single(i => i.Extension == ".xyz").Content);
    }

    [Fact]
    public async Task Catalog_Cancelled_Throws()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var registry = new FakeRegistry().Set(RegistryRoot.ClassesRoot, @".aaa\ShellNew", "NullFile", "");

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new ShellNewCatalog(registry, new MockFileSystem(), Options()).GetTemplatesAsync(cts.Token));
    }

    private static (NewItemCreator Creator, MockFileSystem Fs) Creator()
    {
        var fs = new MockFileSystem();
        fs.AddDirectory(@"C:\work");
        return (new NewItemCreator(fs), fs);
    }

    private static NewItemTemplate Builtin(string id) => ShellNewCatalog.BuiltIn.Single(t => t.Id == id);

    private static LocationPath Work => LocationPath.Parse(@"C:\work");

    [Fact]
    public void Create_Folder_UsesTheDefaultName()
    {
        var (creator, fs) = Creator();

        var created = creator.Create(Builtin("builtin:folder"), Work);

        Assert.Equal(@"C:\work\New folder", created.Value);
        Assert.True(fs.Directory.Exists(created.Value));
    }

    [Fact]
    public void Create_TakenNames_AreNumbered_BeforeTheExtension()
    {
        var (creator, fs) = Creator();

        var first = creator.Create(Builtin("builtin:txt"), Work);
        var second = creator.Create(Builtin("builtin:txt"), Work);
        var third = creator.Create(Builtin("builtin:txt"), Work);

        Assert.Equal(@"C:\work\New Text Document.txt", first.Value);
        Assert.Equal(@"C:\work\New Text Document (2).txt", second.Value);
        Assert.Equal(@"C:\work\New Text Document (3).txt", third.Value);
        Assert.Equal(0, fs.FileInfo.New(first.Value).Length);
    }

    [Fact]
    public void Create_Folders_AreNumberedWithoutTreatingADotAsAnExtension()
    {
        var (creator, fs) = Creator();
        fs.AddDirectory(@"C:\work\v1.2");
        var template = new NewItemTemplate("x", "Folder", string.Empty, "v1.2", NewItemSource.BuiltIn, new FolderContent());

        Assert.Equal(@"C:\work\v1.2 (2)", creator.Create(template, Work).Value);
    }

    [Fact]
    public void Create_DataContent_WritesTheBytes()
    {
        var (creator, fs) = Creator();

        var created = creator.Create(Builtin("builtin:zip"), Work);

        Assert.Equal(22, fs.File.ReadAllBytes(created.Value).Length);
        Assert.Equal("PK"u8.ToArray(), fs.File.ReadAllBytes(created.Value)[..2]);
    }

    [Fact]
    public void Create_Template_CopiesTheTemplateFile()
    {
        var (creator, fs) = Creator();
        fs.AddFile(@"C:\tpl\readme.md", new MockFileData("# hello"));
        var template = new NewItemTemplate("t", "readme", ".md", "readme.md", NewItemSource.UserTemplate, new FileTemplateContent(@"C:\tpl\readme.md"));

        var created = creator.Create(template, Work);

        Assert.Equal("# hello", fs.File.ReadAllText(created.Value));
        Assert.True(fs.File.Exists(@"C:\tpl\readme.md"));
    }

    [Fact]
    public void Create_WithAGivenName_UsesItAndNumbersIfTaken()
    {
        var (creator, fs) = Creator();
        fs.AddFile(@"C:\work\notes.txt", new MockFileData("existing"));

        var created = creator.Create(Builtin("builtin:txt"), Work, "  notes.txt ");

        Assert.Equal(@"C:\work\notes (2).txt", created.Value);
        Assert.Equal("existing", fs.File.ReadAllText(@"C:\work\notes.txt"));
    }

    [Theory]
    [InlineData("a<b.txt")]
    [InlineData("a:b.txt")]
    [InlineData("ends-with-dot.")]
    [InlineData("CON")]
    [InlineData("nul.txt")]
    [InlineData("COM1.log")]
    public void Create_InvalidNames_AreRejected(string name)
    {
        var (creator, _) = Creator();

        Assert.Throws<ArgumentException>(() => creator.Create(Builtin("builtin:txt"), Work, name));
    }

    [Theory]
    [InlineData("New.txt", 1, false, "New.txt")]
    [InlineData("New.txt", 2, false, "New (2).txt")]
    [InlineData("archive.tar.gz", 3, false, "archive.tar (3).gz")]
    [InlineData(".gitignore", 2, false, ".gitignore (2)")]
    [InlineData("v1.2", 2, true, "v1.2 (2)")]
    public void Numbered_PutsTheCounterBeforeTheLastExtension(string name, int attempt, bool folder, string expected)
    {
        Assert.Equal(expected, NewItemCreator.Numbered(name, attempt, folder));
    }
}
