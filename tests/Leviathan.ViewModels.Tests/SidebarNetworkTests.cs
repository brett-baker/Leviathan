using Leviathan.Core.Paths;
using Leviathan.Core.Providers;
using Leviathan.Core.Resilience;
using Leviathan.Testing;
using Leviathan.ViewModels.Sidebar;

namespace Leviathan.ViewModels.Tests;

/// <summary>The Network section of the tree (spec §7.1): saved servers with health dots, shares below them, Nearby and Add connection.</summary>
public sealed class SidebarNetworkTests
{
    private static LocationPath P(string path) => LocationPath.Parse(path);

    private sealed class FakeSource : ISubfolderSource
    {
        public Dictionary<string, string[]> Folders { get; } = new(StringComparer.OrdinalIgnoreCase);

        public List<string> Listed { get; } = [];

        public Task<IReadOnlyList<LocationPath>> ListFoldersAsync(LocationPath location, CancellationToken cancellationToken)
        {
            Listed.Add(location.Value);
            IReadOnlyList<LocationPath> result = Folders.TryGetValue(location.Value, out var names) ? [.. names.Select(location.Combine)] : [];
            return Task.FromResult(result);
        }
    }

    private static string[] Titles(SidebarTree tree) => [.. tree.Rows.Select(r => r.Title)];

    private static SidebarNode Row(SidebarTree tree, string title) => tree.Rows.First(r => r.Title == title);

    private static IReadOnlyList<SidebarSection> NetworkSections(IEnumerable<SavedConnection> servers, IEnumerable<NearbyDevice>? nearby = null, bool searching = false) =>
        [.. SidebarSections.Build([], [], [], [], servers, nearby, searching).Where(s => s.Title == "Network")];

    [Fact]
    public void Build_PutsSavedServersFirstInNetwork_ThenNearbyAndAddConnection()
    {
        var network = NetworkSections([new SavedConnection("diskstation", "NAS"), new SavedConnection("pi")]).Single(s => s.Title == "Network");

        Assert.Equal(["NAS", "pi"], network.Places.Select(p => p.Title));
        Assert.All(network.Places, p => Assert.Equal(SidebarIcon.Server, p.Icon));
        Assert.All(network.Places, p => Assert.True(p.IsSaved));
        Assert.Equal(["diskstation", "pi"], network.Places.Select(p => p.Server));
        Assert.Equal(@"\\diskstation", network.Places[0].Location.Value);
        Assert.Equal("diskstation", network.Places[0].Detail);
        Assert.Equal(string.Empty, network.Places[1].Detail);
        Assert.Equal("Nearby", network.Groups.Single().Title);
        Assert.Equal("Add connection…", network.ActionTitle);
    }

    [Fact]
    public void NearbyGroup_LeavesOutSavedServers_AndSaysWhatItIsDoing()
    {
        var saved = new[] { new SavedConnection("nas") };

        var found = SidebarSections.NearbyGroup([new NearbyDevice("NAS"), new NearbyDevice("qnap", "QNAP TS-464"), new NearbyDevice("qnap"), new NearbyDevice("bad name")], saved, searching: false);
        var none = SidebarSections.NearbyGroup([], saved, searching: false);
        var looking = SidebarSections.NearbyGroup(null, saved, searching: true);

        Assert.Equal(["QNAP TS-464"], found.Places.Select(p => p.Title));
        Assert.All(found.Places, p => Assert.False(p.IsSaved));
        Assert.Equal("qnap", found.Places[0].Server);
        Assert.Equal("No devices found", none.EmptyText);
        Assert.Equal("Searching…", looking.EmptyText);
    }

    [Fact]
    public async Task AServerExpandsToItsShares_AndFollowsTheCurrentFolderIntoOne()
    {
        var source = new FakeSource();
        source.Folders[@"\\nas"] = ["video", "media"];
        source.Folders[@"\\nas\media"] = ["films"];
        var tree = new SidebarTree(source, new ManualTimeProvider());
        tree.SetSections(NetworkSections([new SavedConnection("nas")]));
        var server = Row(tree, "nas");

        await tree.ExpandAsync(server, CancellationToken.None);

        Assert.Equal(["media", "video"], server.Children.Select(c => c.Title));
        Assert.Equal(@"\\nas\media", server.Children[0].Location!.Value);

        // Following the view into a share that is already listed selects it under the server, without listing anything new.
        var listed = source.Listed.Count;
        await tree.SyncAsync(P(@"\\nas\media"), CancellationToken.None);

        Assert.Same(server.Children[0], tree.Selected);
        Assert.Equal(listed, source.Listed.Count);
    }

    [Fact]
    public void ServerRows_ShowTheirHealth_AndRefreshItWithoutMovingRows()
    {
        var tree = new SidebarTree(new FakeSource(), new ManualTimeProvider());
        var health = new Dictionary<string, ShareHealth>(StringComparer.OrdinalIgnoreCase) { ["nas"] = ShareHealth.Online };
        tree.SetSections(NetworkSections([new SavedConnection("nas"), new SavedConnection("pi")]), h => health.GetValueOrDefault(h));
        var before = tree.Rows.ToList();

        Assert.Equal(ShareHealth.Online, Row(tree, "nas").Health);
        Assert.Equal(ShareHealth.Unknown, Row(tree, "pi").Health);
        Assert.Null(Row(tree, "Network").Health);

        health["pi"] = ShareHealth.Unreachable;
        health["nas"] = ShareHealth.AuthenticationRequired;
        tree.RefreshHealth(h => health.GetValueOrDefault(h));

        Assert.Equal(ShareHealth.AuthenticationRequired, Row(tree, "nas").Health);
        Assert.Equal(ShareHealth.Unreachable, Row(tree, "pi").Health);
        Assert.Equal(before, tree.Rows.ToList());
    }

    [Fact]
    public async Task UpdateGroup_AddsDevicesToAnExpandedNearbyWithoutTouchingTheRestOfTheTree()
    {
        var tree = new SidebarTree(new FakeSource(), new ManualTimeProvider());
        var servers = new[] { new SavedConnection("nas") };
        tree.SetSections(NetworkSections(servers, searching: true));
        var nearby = Row(tree, "Nearby");
        Assert.False(nearby.IsExpanded);
        Assert.Equal(["Network", "nas", "Nearby", "Add connection…"], Titles(tree));

        await tree.ToggleAsync(nearby, CancellationToken.None);
        Assert.Equal(["Network", "nas", "Nearby", "Searching…", "Add connection…"], Titles(tree));

        await tree.UpdateGroupAsync("Nearby", SidebarSections.NearbyGroup([new NearbyDevice("qnap", "QNAP"), new NearbyDevice("pi")], servers, searching: false));

        Assert.Equal(["Network", "nas", "Nearby", "pi", "QNAP", "Add connection…"], Titles(tree));
        Assert.True(Row(tree, "QNAP").CanExpand);

        await tree.UpdateGroupAsync("Nearby", SidebarSections.NearbyGroup([], servers, searching: false));

        Assert.Equal(["Network", "nas", "Nearby", "No devices found", "Add connection…"], Titles(tree));
    }

    [Fact]
    public async Task UpdateGroup_OnACollapsedNearby_KeepsItCollapsed()
    {
        var tree = new SidebarTree(new FakeSource(), new ManualTimeProvider());
        tree.SetSections(NetworkSections([], searching: true));

        await tree.UpdateGroupAsync("Nearby", SidebarSections.NearbyGroup([new NearbyDevice("pi")], [], searching: false));

        Assert.Equal(["Network", "Nearby", "Add connection…"], Titles(tree));
        await tree.ToggleAsync(Row(tree, "Nearby"), CancellationToken.None);
        Assert.Equal(["Network", "Nearby", "pi", "Add connection…"], Titles(tree));
    }
}
