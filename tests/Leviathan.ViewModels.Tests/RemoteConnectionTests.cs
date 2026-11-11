using System.IO.Abstractions.TestingHelpers;
using Leviathan.Core.Paths;
using Leviathan.Providers.Remote;
using Leviathan.ViewModels.Sidebar;

namespace Leviathan.ViewModels.Tests;

/// <summary>SFTP, FTP, FTPS and NFS connections under Network (spec §7.1).</summary>
public sealed class RemoteConnectionTests
{
    private sealed class MemoryStorage : IRemoteConnectionsStorage
    {
        public List<RemoteConnection> Saved { get; set; } = [];

        public int Saves { get; private set; }

        public IReadOnlyList<RemoteConnection> Load() => Saved;

        public void Save(IReadOnlyList<RemoteConnection> connections)
        {
            Saved = [.. connections];
            Saves++;
        }
    }

    private static RemoteConnectionInput Input(RemoteProtocol protocol = RemoteProtocol.Sftp, string host = "nas.local", string port = "", string user = "brett", string password = "pw", string key = "", string path = "", string name = "") =>
        new(protocol, host, port, user, password, key, path, name);

    [Fact]
    public void Input_BuildsAConnection_WithDefaultsLeftOut()
    {
        var connection = Input(host: " NAS.local ", name: " Media ").ToConnection(out var problem)!;

        Assert.Equal(string.Empty, problem);
        Assert.Equal(new RemoteConnection(RemoteProtocol.Sftp, "nas.local", null, "brett", null, "/", "Media"), connection);
        Assert.Equal("Media", connection.Title);
    }

    [Fact]
    public void Input_ThePortOfTheProtocol_IsTheDefault_AnyOtherIsKept()
    {
        Assert.Null(Input(port: "22").ToConnection(out _)!.Port);
        Assert.Equal(2222, Input(port: "2222").ToConnection(out _)!.Port);
        Assert.Null(Input(RemoteProtocol.Ftp, port: "21").ToConnection(out _)!.Port);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("two words")]
    [InlineData("user@host")]
    [InlineData("host/with/slash")]
    public void Input_ABadHost_SaysSo(string host)
    {
        Assert.Null(Input(host: host).ToConnection(out var problem));
        Assert.Contains("name or address", problem, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("70000")]
    [InlineData("abc")]
    [InlineData("-5")]
    public void Input_ABadPort_SaysSo(string port)
    {
        Assert.Null(Input(port: port).ToConnection(out var problem));
        Assert.Contains("port", problem, StringComparison.Ordinal);
    }

    [Fact]
    public void Input_SftpNeedsAUser_FtpMayBeAnonymous()
    {
        Assert.Null(Input(user: "").ToConnection(out var problem));
        Assert.Contains("user name", problem, StringComparison.Ordinal);
        Assert.Null(Input(RemoteProtocol.Ftp, user: "").ToConnection(out _)!.User);
    }

    [Fact]
    public void Input_AKeyFileIsOnlyForSftp_AndQuotesAreStripped()
    {
        Assert.Equal(@"C:\keys\id", Input(key: "\"C:\\keys\\id\"").ToConnection(out _)!.KeyFile);
        Assert.Null(Input(RemoteProtocol.Ftps, key: "k").ToConnection(out var problem));
        Assert.Contains("only for SFTP", problem, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("", "/")]
    [InlineData("home/brett", "/home/brett")]
    [InlineData(@"\home\brett", "/home/brett")]
    public void Input_ThePath_IsMadeAbsolute(string path, string expected) =>
        Assert.Equal(expected, Input(path: path).ToConnection(out _)!.Path);

    [Fact]
    public void Input_APathThatClimbsOut_IsRefused()
    {
        Assert.Null(Input(path: "/a/../b").ToConnection(out var problem));
        Assert.Contains("plain path", problem, StringComparison.Ordinal);
    }

    [Fact]
    public void Input_ASchemeTypedInTheHostBox_IsAccepted() =>
        Assert.Equal("nas.local", Input(host: "sftp://nas.local/").ToConnection(out _)!.Host);

    [Fact]
    public void Location_HasUserPortAndPath_AndParsesBack()
    {
        var connection = new RemoteConnection(RemoteProtocol.Sftp, "nas.local", 2222, "brett smith", null, "/home/my files");

        var location = connection.Location;

        Assert.Equal(LocationKind.Sftp, location.Kind);
        Assert.Equal("nas.local", location.Server);
        Assert.Equal(2222, location.Port);
        Assert.Equal("brett smith", location.User);
        Assert.Equal(["home", "my files"], location.Segments);
        Assert.Equal(location, LocationPath.Parse(location.Value));
    }

    [Fact]
    public void Location_OfAnAnonymousFtpServer_HasNoUser()
    {
        var location = new RemoteConnection(RemoteProtocol.Ftp, "ftp.example").Location;

        Assert.Equal(LocationKind.Ftp, location.Kind);
        Assert.Null(location.User);
        Assert.Empty(location.Segments);
    }

    [Fact]
    public void Endpoint_UsesTheDefaultPort_AndKeysTheSecrets()
    {
        var connection = new RemoteConnection(RemoteProtocol.Ftps, "Files.Example", null, "u");

        Assert.Equal(new RemoteEndpoint(RemoteProtocol.Ftps, "Files.Example", 21, "u"), connection.Endpoint);
        Assert.Equal("ftps://u@files.example:21", connection.Key);
    }

    [Fact]
    public void List_AddsReplacesAndRemoves_AndSavesEachTime()
    {
        var storage = new MemoryStorage();
        var list = new RemoteConnectionList(storage);
        var changes = 0;
        list.Changed += (_, _) => changes++;

        list.AddOrReplace(new RemoteConnection(RemoteProtocol.Sftp, "nas.local", null, "brett"));
        list.AddOrReplace(new RemoteConnection(RemoteProtocol.Sftp, "NAS.local", null, "brett", Name: "Media"));
        list.AddOrReplace(new RemoteConnection(RemoteProtocol.Ftp, "nas.local", null, "brett"));

        Assert.Equal(2, list.Items.Count);
        Assert.Equal("Media", list.Items[0].Name);
        Assert.Equal(3, storage.Saves);
        Assert.Equal(3, changes);

        var removed = list.Remove(LocationPath.Parse("sftp://brett@nas.local/some/folder"));

        Assert.Equal("Media", removed!.Name);
        Assert.Single(list.Items);
        Assert.Null(list.Remove(LocationPath.Parse("sftp://brett@nas.local/")));
        Assert.Null(list.Remove(LocationPath.Parse(@"C:\")));
    }

    [Fact]
    public void List_FindsTheConnectionOfALocation_AndLoadsWhatWasSaved()
    {
        var storage = new MemoryStorage { Saved = [new RemoteConnection(RemoteProtocol.Sftp, "nas.local", 2222, "brett", Name: "Media"), new RemoteConnection(RemoteProtocol.Sftp, "NAS.LOCAL", 2222, "brett")] };

        var list = new RemoteConnectionList(storage);

        Assert.Single(list.Items);
        Assert.Equal("Media", list.Find(LocationPath.Parse("sftp://brett@nas.local:2222/x/y"))!.Name);
        Assert.Null(list.Find(LocationPath.Parse("sftp://other@nas.local:2222/")));
    }

    [Fact]
    public void Sidebar_ShowsRemoteConnectionsUnderNetwork_WithTheirProtocolAndNoPassword()
    {
        var connection = new RemoteConnection(RemoteProtocol.Sftp, "nas.local", 2222, "brett", Name: "NAS files");

        var network = SidebarSections.Build([], [], [], [], remote: [connection]).Single(s => s.Title == "Network");

        var place = Assert.Single(network.Places);
        Assert.Equal("NAS files", place.Title);
        Assert.Equal(connection.Location, place.Location);
        Assert.Equal("SFTP · nas.local:2222", place.Detail);
        Assert.DoesNotContain("pw", place.Detail, StringComparison.Ordinal);
        Assert.True(place.IsSaved);
    }

    [Fact]
    public void Sidebar_TheInternetSection_SaysHowEachSyncClientIsDoing()
    {
        var one = new Leviathan.Core.Platform.CloudProviderInfo(Leviathan.Core.Platform.CloudProviderKind.OneDrive, "OneDrive", LocationPath.Parse(@"C:\Users\b\OneDrive"));
        var box = new Leviathan.Core.Platform.CloudProviderInfo(Leviathan.Core.Platform.CloudProviderKind.Box, "Box", LocationPath.Parse(@"C:\Users\b\Box"));
        var states = new Dictionary<Leviathan.Core.Platform.CloudProviderInfo, Leviathan.Core.Platform.CloudSyncState>
        {
            [one] = Leviathan.Core.Platform.CloudSyncState.Running,
            [box] = Leviathan.Core.Platform.CloudSyncState.NotRunning,
        };

        var internet = SidebarSections.Build([], [], [], [one, box], cloudStates: states).Single(s => s.Title == "Internet");

        Assert.Equal(["Sync client running", "Sync client not running"], internet.Places.Select(p => p.Detail));
        Assert.Equal(string.Empty, SidebarSections.SyncText(Leviathan.Core.Platform.CloudSyncState.Unknown));
        Assert.Equal("Sync folder missing", SidebarSections.SyncText(Leviathan.Core.Platform.CloudSyncState.FolderMissing));
        Assert.All(SidebarSections.Build([], [], [], [one]).Single(s => s.Title == "Internet").Places, p => Assert.Equal(string.Empty, p.Detail));
    }

    [Fact]
    public void Nfs_IsInstalledWhenTheClientProgramIsThere()
    {
        var with = new MockFileSystem(new Dictionary<string, MockFileData> { [@"C:\Windows\System32\mount.exe"] = new("x") });

        Assert.True(NfsSupport.IsInstalled(with, @"C:\Windows\System32"));
        Assert.False(NfsSupport.IsInstalled(new MockFileSystem(), @"C:\Windows\System32"));
        Assert.Contains("Client for NFS", NfsSupport.NotInstalledMessage, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("nas", "/volume1/media", @"\\nas\volume1\media")]
    [InlineData(@"\\nas\", @"volume1\media\", @"\\nas\volume1\media")]
    [InlineData("192.168.1.10", "export", @"\\192.168.1.10\export")]
    public void Nfs_AnExportBecomesANetworkPath(string host, string export, string expected) =>
        Assert.Equal(LocationPath.Parse(expected), NfsSupport.PathOf(host, export, out _));

    [Theory]
    [InlineData("", "/x", "name or address")]
    [InlineData("a b", "/x", "name or address")]
    [InlineData("nas", "", "export")]
    [InlineData("nas", "/", "export")]
    public void Nfs_BadInput_SaysWhatIsMissing(string host, string export, string expected)
    {
        Assert.Null(NfsSupport.PathOf(host, export, out var problem));
        Assert.Contains(expected, problem, StringComparison.Ordinal);
    }
}
