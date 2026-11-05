using Leviathan.ViewModels.Sidebar;

namespace Leviathan.ViewModels.Tests;

public sealed class SavedConnectionTests
{
    private sealed class MemoryStorage : ISavedConnectionsStorage
    {
        public List<SavedConnection> Stored { get; set; } = [];

        public bool FailToSave { get; set; }

        public int Saves { get; private set; }

        public IReadOnlyList<SavedConnection> Load() => Stored;

        public void Save(IReadOnlyList<SavedConnection> connections)
        {
            if (FailToSave)
            {
                throw new IOException("disk full");
            }

            Saves++;
            Stored = [.. connections];
        }
    }

    [Fact]
    public void Add_KeepsServersInTheOrderAdded_AndSavesThem()
    {
        var storage = new MemoryStorage();
        var list = new SavedConnectionList(storage);

        Assert.True(list.Add("diskstation", "NAS"));
        Assert.True(list.Add("192.168.1.10"));

        Assert.Equal(["diskstation", "192.168.1.10"], list.Items.Select(c => c.Host));
        Assert.Equal("NAS", list.Items[0].Title);
        Assert.Equal("192.168.1.10", list.Items[1].Title);
        Assert.Equal(2, storage.Stored.Count);
    }

    [Theory]
    [InlineData("DISKSTATION")]
    [InlineData(@"\\diskstation")]
    [InlineData(@"\\diskstation\")]
    [InlineData("  diskstation ")]
    public void Add_OfAServerAlreadySaved_ChangesNothing(string again)
    {
        var storage = new MemoryStorage();
        var list = new SavedConnectionList(storage);
        list.Add("diskstation");
        var saves = storage.Saves;

        Assert.False(list.Add(again));

        Assert.Single(list.Items);
        Assert.Equal(saves, storage.Saves);
        Assert.True(list.Contains(again));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(@"\\")]
    [InlineData(@"nas\media")]
    [InlineData("two words")]
    [InlineData("a:b")]
    [InlineData("nas*")]
    public void Add_OfSomethingThatIsNotAServerName_IsRefused(string host)
    {
        var list = new SavedConnectionList(new MemoryStorage());

        Assert.False(list.Add(host));

        Assert.Empty(list.Items);
    }

    [Fact]
    public void Add_StoresTheServerWithoutBackslashes()
    {
        var list = new SavedConnectionList(new MemoryStorage());

        list.Add(@"\\nas");

        Assert.Equal("nas", list.Items[0].Host);
        Assert.Equal(@"\\nas", list.Items[0].Location.Value);
    }

    [Fact]
    public void RemoveAndRename_ChangeTheList()
    {
        var list = new SavedConnectionList(new MemoryStorage());
        list.Add("nas");
        list.Add("pi");

        list.Rename("PI", "Raspberry");
        Assert.Equal("Raspberry", list.Items[1].Title);
        list.Rename("pi", " ");
        Assert.Equal("pi", list.Items[1].Title);

        Assert.True(list.Remove("nas"));
        Assert.False(list.Remove("nas"));
        Assert.Equal(["pi"], list.Items.Select(c => c.Host));
    }

    [Fact]
    public void Constructor_LoadsWhatWasSaved_DropsBadAndDuplicateEntries()
    {
        var storage = new MemoryStorage { Stored = [new("nas"), new("NAS"), new("bad name"), new("pi", "Pi")] };

        var list = new SavedConnectionList(storage);

        Assert.Equal(["nas", "pi"], list.Items.Select(c => c.Host));
    }

    [Fact]
    public void Changed_IsRaisedForEveryChange_AndNotForNoOps()
    {
        var list = new SavedConnectionList(new MemoryStorage());
        var raised = 0;
        list.Changed += (_, _) => raised++;

        list.Add("nas");
        list.Add("nas");
        list.Remove("nas");
        list.Remove("nas");

        Assert.Equal(2, raised);
    }

    [Fact]
    public void AFailedSave_StillChangesTheListForThisRun()
    {
        var list = new SavedConnectionList(new MemoryStorage { FailToSave = true });

        Assert.True(list.Add("nas"));

        Assert.Single(list.Items);
    }

    [Theory]
    [InlineData("diskstation", "diskstation", null, @"\\diskstation")]
    [InlineData(@"\\diskstation", "diskstation", null, @"\\diskstation")]
    [InlineData(@"\\192.168.1.10\media", "192.168.1.10", "media", @"\\192.168.1.10\media")]
    [InlineData("//nas/media", "nas", "media", @"\\nas\media")]
    [InlineData("smb://nas/media/films", "nas", "media", @"\\nas\media\films")]
    [InlineData("  SMB://NAS  ", "NAS", null, @"\\NAS")]
    public void AddConnectionInput_ReadsEveryWayOfWritingAnSmbAddress(string text, string host, string? share, string location)
    {
        var input = AddConnectionInput.Parse(text, out var error);

        Assert.NotNull(input);
        Assert.Equal(string.Empty, error);
        Assert.Equal(host, input.Host);
        Assert.Equal(share, input.Share);
        Assert.Equal(share is not null, input.HasShare);
        Assert.Equal(location, input.Location.Value);
        Assert.Equal(share is null ? null : $@"\\{host}\{share}", input.ShareRoot);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("two words")]
    [InlineData(@"\\nas*")]
    [InlineData("sftp://nas/home")]
    [InlineData("ftp://nas")]
    public void AddConnectionInput_ExplainsWhatIsWrong(string text)
    {
        var input = AddConnectionInput.Parse(text, out var error);

        Assert.Null(input);
        Assert.NotEmpty(error);
    }
}
