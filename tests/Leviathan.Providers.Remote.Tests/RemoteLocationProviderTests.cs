using Leviathan.Core.Model;
using Leviathan.Core.Paths;
using Leviathan.Core.Providers;
using Leviathan.Testing;

namespace Leviathan.Providers.Remote.Tests;

/// <summary>The provider that lists SFTP and FTP servers as folders, against fake sessions (spec §7.1, §3.3).</summary>
public sealed class RemoteLocationProviderTests
{
    private static LocationPath P(string path) => LocationPath.Parse(path);

    private sealed class FakeSession(FakeServer server) : IRemoteSession
    {
        public bool IsConnected { get; set; } = true;

        public int Calls { get; private set; }

        public Task<IReadOnlyList<RemoteItem>> ListAsync(string path, CancellationToken cancellationToken)
        {
            Calls++;
            server.MaybeFail();
            if (!server.Folders.TryGetValue(path, out var items))
            {
                throw new RemoteException(RemoteFailure.NotFound, "No such folder.");
            }

            return Task.FromResult<IReadOnlyList<RemoteItem>>(items);
        }

        public Task<RemoteItem?> StatAsync(string path, CancellationToken cancellationToken) =>
            Task.FromResult(server.Folders.Values.SelectMany(v => v).FirstOrDefault(i => i.FullPath == path));

        public Task<Stream> OpenReadAsync(string path, CancellationToken cancellationToken) => Task.FromResult<Stream>(new MemoryStream([1, 2, 3]));

        public Task WriteAsync(string path, Stream content, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task RenameAsync(string from, string to, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task DeleteAsync(string path, bool isFolder, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task CreateFolderAsync(string path, CancellationToken cancellationToken) => Task.CompletedTask;

        public bool Disposed { get; private set; }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            IsConnected = false;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FakeServer : IRemoteSessionFactory
    {
        public Dictionary<string, List<RemoteItem>> Folders { get; } = new()
        {
            ["/"] = [new RemoteItem("media", "/media", true, 0, DateTimeOffset.UnixEpoch), new RemoteItem(".hidden", "/.hidden", false, 5, DateTimeOffset.UnixEpoch), new RemoteItem("a.txt", "/a.txt", false, 12, new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero))],
            ["/media"] = [new RemoteItem("clip.mkv", "/media/clip.mkv", false, 1000, DateTimeOffset.UnixEpoch)],
            ["/media/a b"] = [],
        };

        public List<FakeSession> Sessions { get; } = [];

        public int Connects { get; private set; }

        public Queue<RemoteException> Failures { get; } = [];

        public RemoteException? ConnectFailure { get; set; }

        public void MaybeFail()
        {
            if (Failures.Count > 0)
            {
                throw Failures.Dequeue();
            }
        }

        public Task<IRemoteSession> ConnectAsync(RemoteEndpoint endpoint, CancellationToken cancellationToken)
        {
            Connects++;
            if (ConnectFailure is { } failure)
            {
                throw failure;
            }

            var session = new FakeSession(this);
            Sessions.Add(session);
            return Task.FromResult<IRemoteSession>(session);
        }
    }

    private static RemoteLocationProvider Provider(FakeServer server, ManualTimeProvider? time = null) =>
        new(new Dictionary<RemoteProtocol, IRemoteSessionFactory> { [RemoteProtocol.Sftp] = server, [RemoteProtocol.Ftp] = server }, time);

    private static async Task<List<FileEntry>> ListAsync(RemoteLocationProvider provider, string location, ListingOptions? options = null)
    {
        var entries = new List<FileEntry>();
        await foreach (var batch in provider.ListAsync(P(location), options ?? ListingOptions.Default, CancellationToken.None))
        {
            entries.AddRange(batch.Items);
        }

        return entries;
    }

    [Fact]
    public async Task List_MapsTheServerItemsToEntries_WithPathsKindsSizesAndTimes()
    {
        var provider = Provider(new FakeServer());

        var entries = await ListAsync(provider, "sftp://user@nas.example/", new ListingOptions { IncludeHidden = true });

        var folder = entries.Single(e => e.Name == "media");
        Assert.Equal(FileEntryKind.Folder, folder.Kind);
        Assert.Equal(P("sftp://user@nas.example/media"), folder.Location);
        var file = entries.Single(e => e.Name == "a.txt");
        Assert.Equal(FileEntryKind.File, file.Kind);
        Assert.Equal(12, file.Size);
        Assert.Equal(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero), file.Modified);
    }

    [Fact]
    public async Task List_DotFilesAreHidden_UnlessAsked()
    {
        var provider = Provider(new FakeServer());

        Assert.DoesNotContain(await ListAsync(provider, "sftp://user@nas.example/"), e => e.Name == ".hidden");
        Assert.Contains(await ListAsync(provider, "sftp://user@nas.example/", new ListingOptions { IncludeHidden = true }), e => e.Name == ".hidden" && e.IsHidden);
    }

    [Fact]
    public async Task List_AFolderBelowTheTop_UsesAServerPathWithSlashes()
    {
        var provider = Provider(new FakeServer());

        var entries = await ListAsync(provider, "sftp://user@nas.example:2222/media");

        Assert.Equal(["clip.mkv"], entries.Select(e => e.Name));
        Assert.Equal(P("sftp://user@nas.example:2222/media/clip.mkv"), entries[0].Location);
    }

    [Fact]
    public async Task List_AnEmptyFolder_StillEndsWithAFinalBatch()
    {
        var provider = Provider(new FakeServer());
        var batches = new List<ListingBatch>();

        await foreach (var batch in provider.ListAsync(P("sftp://user@nas.example/media/a b"), ListingOptions.Default, CancellationToken.None))
        {
            batches.Add(batch);
        }

        Assert.True(Assert.Single(batches).IsFinal);
    }

    [Fact]
    public async Task OneSessionIsKeptPerServer_AndReused()
    {
        var server = new FakeServer();
        var provider = Provider(server);

        await ListAsync(provider, "sftp://user@nas.example/");
        await ListAsync(provider, "sftp://user@nas.example/media");
        await ListAsync(provider, "sftp://other@nas.example/");

        Assert.Equal(2, server.Connects);
        Assert.Equal(2, provider.OpenConnections);
    }

    [Fact]
    public async Task APassingFailure_DropsTheSession_AndTriesOnceMoreOnANewOne()
    {
        var server = new FakeServer();
        var provider = Provider(server);
        await ListAsync(provider, "sftp://user@nas.example/");
        server.Failures.Enqueue(new RemoteException(RemoteFailure.Timeout, "slow"));

        var entries = await ListAsync(provider, "sftp://user@nas.example/media");

        Assert.Single(entries);
        Assert.Equal(2, server.Connects);
        Assert.True(server.Sessions[0].Disposed);
    }

    [Fact]
    public async Task APassingFailureThatRepeats_IsAnErrorThatCanBeRetriedLater()
    {
        var server = new FakeServer();
        var provider = Provider(server);
        await ListAsync(provider, "sftp://user@nas.example/");
        server.Failures.Enqueue(new RemoteException(RemoteFailure.Unreachable, "down"));
        server.Failures.Enqueue(new RemoteException(RemoteFailure.Unreachable, "still down"));

        var error = await Assert.ThrowsAsync<LocationException>(() => ListAsync(provider, "sftp://user@nas.example/media"));

        Assert.Equal(LocationError.Unreachable, error.Error);
        Assert.True(error.IsTransient);
        Assert.Equal(0, provider.OpenConnections);
    }

    [Theory]
    [InlineData(RemoteFailure.AuthenticationRequired, LocationError.AuthenticationRequired)]
    [InlineData(RemoteFailure.UntrustedServer, LocationError.AuthenticationRequired)]
    [InlineData(RemoteFailure.NotFound, LocationError.NotFound)]
    [InlineData(RemoteFailure.AccessDenied, LocationError.AccessDenied)]
    [InlineData(RemoteFailure.Timeout, LocationError.Timeout)]
    [InlineData(RemoteFailure.Unreachable, LocationError.Unreachable)]
    public async Task FailuresAreClassifiedForTheTab(RemoteFailure failure, LocationError expected)
    {
        var server = new FakeServer { ConnectFailure = new RemoteException(failure, "nope") };

        var error = await Assert.ThrowsAsync<LocationException>(() => ListAsync(Provider(server), "sftp://user@nas.example/"));

        Assert.Equal(expected, error.Error);
        Assert.Equal("nope", error.Message);
        Assert.Equal(1, server.Connects); // sign-in and trust failures are not retried
    }

    [Fact]
    public async Task APermanentFailure_IsNotRetried_AndTheSessionStays()
    {
        var server = new FakeServer();
        var provider = Provider(server);

        var error = await Assert.ThrowsAsync<LocationException>(() => ListAsync(provider, "sftp://user@nas.example/missing"));

        Assert.Equal(LocationError.NotFound, error.Error);
        Assert.Equal(1, server.Connects);
        Assert.Equal(1, provider.OpenConnections);
    }

    [Fact]
    public async Task IdleConnections_AreClosedAfterAWhile()
    {
        var server = new FakeServer();
        var time = new ManualTimeProvider();
        var provider = Provider(server, time);
        await ListAsync(provider, "sftp://user@nas.example/");

        await provider.CloseIdleAsync();
        Assert.Equal(1, provider.OpenConnections);

        time.Advance(TimeSpan.FromMinutes(5));
        await provider.CloseIdleAsync();

        Assert.Equal(0, provider.OpenConnections);
        Assert.True(server.Sessions[0].Disposed);
    }

    [Fact]
    public async Task Cancelling_IsNotAnError()
    {
        var provider = Provider(new FakeServer());
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var _ in provider.ListAsync(P("sftp://user@nas.example/"), ListingOptions.Default, cts.Token))
            {
            }
        });
    }

    [Fact]
    public void CanHandle_OnlyTheProtocolsItHasAFactoryFor()
    {
        var provider = Provider(new FakeServer());

        Assert.True(provider.CanHandle(P("sftp://u@h/")));
        Assert.True(provider.CanHandle(P("ftp://u@h/")));
        Assert.False(provider.CanHandle(P("ftps://u@h/")));
        Assert.False(provider.CanHandle(P(@"\\nas\media")));
        Assert.False(provider.CanHandle(P(@"C:\")));
    }

    [Fact]
    public void EndpointAndPathMapping_UseDefaultPortsAndSlashes()
    {
        var endpoint = RemoteLocationProvider.EndpointOf(P("sftp://Brett@NAS.example/a/b"));

        Assert.Equal(new RemoteEndpoint(RemoteProtocol.Sftp, "nas.example", 22, "Brett"), endpoint);
        Assert.Equal("/a/b", RemoteLocationProvider.RemotePathOf(P("sftp://Brett@NAS.example/a/b")));
        Assert.Equal("/", RemoteLocationProvider.RemotePathOf(P("sftp://Brett@NAS.example/")));
        Assert.Equal(21, RemoteLocationProvider.EndpointOf(P("ftp://h/")).Port);
        Assert.Equal(2121, RemoteLocationProvider.EndpointOf(P("ftp://h:2121/")).Port);
    }

    [Fact]
    public async Task Operations_GoThroughTheSameSession()
    {
        var server = new FakeServer();
        var provider = Provider(server);

        await provider.CreateFolderAsync(P("sftp://u@h/new"), CancellationToken.None);
        await provider.WriteAsync(P("sftp://u@h/new/a.part"), new MemoryStream([1]), CancellationToken.None);
        await provider.RenameAsync(P("sftp://u@h/new/a.part"), P("sftp://u@h/new/a"), CancellationToken.None);
        await provider.DeleteAsync(P("sftp://u@h/new/a"), isFolder: false, CancellationToken.None);
        await using var read = await provider.OpenReadAsync(P("sftp://u@h/a.txt"), CancellationToken.None);

        Assert.Equal(1, server.Connects);
        Assert.Equal(3, read.Length);
    }
}
