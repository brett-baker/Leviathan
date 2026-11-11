using System.IO.Abstractions.TestingHelpers;
using Leviathan.Core.Paths;
using Leviathan.Core.Platform;
using Leviathan.ViewModels.Folders;
using Leviathan.ViewModels.Transfers;

namespace Leviathan.ViewModels.Tests;

/// <summary>Renaming, making folders and dropping on an SFTP / FTP / FTPS server (spec §7.1).</summary>
public sealed class ServerItemsTests
{
    private sealed class FolderOnlyCatalog : INewItemCatalog
    {
        public Task<IReadOnlyList<NewItemTemplate>> GetTemplatesAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<NewItemTemplate>>([.. NewMenu.ShellNewCatalog.BuiltIn]);
    }

    private sealed class FakeServer : IRemoteItems
    {
        public HashSet<string> Names { get; } = new(StringComparer.OrdinalIgnoreCase);

        public List<string> Calls { get; } = [];

        public bool Handles(LocationPath location) => location.Kind is LocationKind.Sftp or LocationKind.Ftp or LocationKind.Ftps;

        public Task<bool> ExistsAsync(LocationPath location, CancellationToken cancellationToken) => Task.FromResult(Names.Contains(location.Value));

        public Task RenameAsync(LocationPath from, LocationPath to, CancellationToken cancellationToken)
        {
            Calls.Add($"rename {from.Value} -> {to.Value}");
            return Task.CompletedTask;
        }

        public Task CreateFolderAsync(LocationPath location, CancellationToken cancellationToken)
        {
            Calls.Add($"mkdir {location.Value}");
            Names.Add(location.Value);
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task Rename_OnAServer_GoesToTheServer_NotTheDisk()
    {
        var server = new FakeServer();
        var service = new RenameService(new MockFileSystem(), server);

        var renamed = await service.RenameAsync(LocationPath.Parse("sftp://nas/media/a.txt"), "b.txt", CancellationToken.None);

        Assert.Equal("b.txt", renamed.Name);
        Assert.Single(server.Calls);
        Assert.StartsWith("rename ", server.Calls[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task Rename_OnAServer_ToANameInUse_IsRefused_AndNothingChanges()
    {
        var server = new FakeServer();
        server.Names.Add(LocationPath.Parse("sftp://nas/media/b.txt").Value);
        var service = new RenameService(new MockFileSystem(), server);

        await Assert.ThrowsAsync<IOException>(() => service.RenameAsync(LocationPath.Parse("sftp://nas/media/a.txt"), "b.txt", CancellationToken.None));

        Assert.Empty(server.Calls);
    }

    [Fact]
    public async Task NewFolder_OnAServer_TakesTheFirstFreeName()
    {
        var server = new FakeServer();
        var folder = LocationPath.Parse("ftp://nas/media");
        server.Names.Add(folder.Combine("New folder").Value);
        var service = new NewMenu.NewItemService(new FolderOnlyCatalog(), new NewMenu.NewItemCreator(new MockFileSystem())) { Remote = server };

        var made = await service.CreateAsync(NewMenu.NewItemService.FolderTemplate.Id, folder, CancellationToken.None);

        Assert.Equal("New folder (2)", made.Name);
        Assert.Equal([$"mkdir {made.Value}"], server.Calls);
    }

    [Theory]
    [InlineData("sftp://nas/media")]
    [InlineData("ftp://nas/media")]
    [InlineData("ftps://nas/media")]
    public void Decide_DroppingOnAServer_IsAllowed_AndAlwaysCrossesVolumes(string destination)
    {
        var decision = DropRules.Decide([LocationPath.Parse(@"C:\a\1.txt")], LocationPath.Parse(destination), DropModifiers.None);

        Assert.True(decision.Allowed);
        Assert.Equal(TransferMode.Copy, decision.Mode);
    }

    [Fact]
    public void Decide_MovingWithinOneServer_IsAMoveNotACopy()
    {
        var decision = DropRules.Decide([LocationPath.Parse("sftp://nas/a/1.txt")], LocationPath.Parse("sftp://nas/b"), DropModifiers.None);

        Assert.Equal(TransferMode.Move, decision.Mode);
    }
}
