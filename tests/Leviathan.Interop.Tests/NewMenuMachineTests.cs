using System.IO.Abstractions;
using Leviathan.Core.Paths;
using Leviathan.Core.Platform;
using Leviathan.ViewModels.NewMenu;

namespace Leviathan.Interop.Tests;

/// <summary>Reads this PC's real ShellNew registrations; the only writes are into a temp folder that is deleted afterwards.</summary>
public sealed class NewMenuMachineTests : IDisposable
{
    private readonly string _temp = Path.Combine(Path.GetTempPath(), "leviathan-new-" + Guid.NewGuid().ToString("N"));

    public NewMenuMachineTests() => Directory.CreateDirectory(_temp);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_temp, recursive: true);
        }
        catch (IOException)
        {
            // Best effort.
        }
    }

    [Fact]
    public async Task ThisMachine_Catalog_HasBuiltInsAndOnlyUsableRegisteredItems()
    {
        var catalog = new ShellNewCatalog(new WindowsRegistryView(), new FileSystem(), NewMenuOptions.ForThisMachine());

        var items = await catalog.GetTemplatesAsync(CancellationToken.None);

        Assert.True(items.Count >= ShellNewCatalog.BuiltIn.Count);
        Assert.Equal(items.Count, items.Select(i => i.Id).Distinct().Count());
        foreach (var item in items.Where(i => i.Source == NewItemSource.Registry))
        {
            Assert.StartsWith(".", item.Extension, StringComparison.Ordinal);
            Assert.False(string.IsNullOrWhiteSpace(item.DisplayName));
            if (item.Content is FileTemplateContent template)
            {
                Assert.True(File.Exists(template.TemplatePath), $"template for {item.Extension} should exist: {template.TemplatePath}");
            }
        }
    }

    [Fact]
    public void Creator_OnTheRealFileSystem_MakesFilesAndFoldersWithoutOverwriting()
    {
        var creator = new NewItemCreator(new FileSystem());
        var folder = LocationPath.Parse(_temp);

        var folderItem = creator.Create(ShellNewCatalog.BuiltIn.Single(t => t.Id == "builtin:folder"), folder);
        var text1 = creator.Create(ShellNewCatalog.BuiltIn.Single(t => t.Id == "builtin:txt"), folder);
        var text2 = creator.Create(ShellNewCatalog.BuiltIn.Single(t => t.Id == "builtin:txt"), folder);
        var bitmap = creator.Create(ShellNewCatalog.BuiltIn.Single(t => t.Id == "builtin:bmp"), folder);

        Assert.True(Directory.Exists(folderItem.Value));
        Assert.True(File.Exists(text1.Value));
        Assert.EndsWith("(2).txt", text2.Value, StringComparison.Ordinal);
        Assert.Equal(58, new FileInfo(bitmap.Value).Length);
    }
}
