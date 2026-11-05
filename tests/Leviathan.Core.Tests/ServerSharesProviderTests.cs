using Leviathan.Core.Model;
using Leviathan.Core.Paths;
using Leviathan.Core.Providers;
using Leviathan.Testing;

namespace Leviathan.Core.Tests;

/// <summary>A server's root lists its shares as folders, so \\nas opens and expands like a drive (spec §7.1).</summary>
public sealed class ServerSharesProviderTests
{
    private sealed class FakeShares : IShareEnumerator
    {
        public List<ShareInfo> Shares { get; } = [];

        public Exception? Failure { get; set; }

        public List<string> Asked { get; } = [];

        public Task<IReadOnlyList<ShareInfo>> ListSharesAsync(string server, CancellationToken cancellationToken)
        {
            Asked.Add(server);
            cancellationToken.ThrowIfCancellationRequested();
            return Failure is null ? Task.FromResult<IReadOnlyList<ShareInfo>>([.. Shares]) : Task.FromException<IReadOnlyList<ShareInfo>>(Failure);
        }
    }

    private static readonly LocationPath Server = LocationPath.Parse(@"\\nas");

    private static async Task<List<FileEntry>> ListAsync(ServerSharesProvider provider, LocationPath location, CancellationToken cancellationToken = default)
    {
        var all = new List<FileEntry>();
        await foreach (var batch in provider.ListAsync(location, ListingOptions.Default, cancellationToken))
        {
            all.AddRange(batch.Items);
        }

        return all;
    }

    [Fact]
    public async Task ListAsync_GivesEachShareAsAFolderBelowTheServer_InNameOrder()
    {
        var shares = new FakeShares { Shares = { new("video"), new("Backup", "nightly"), new("media") } };

        var entries = await ListAsync(new ServerSharesProvider(shares, new ManualTimeProvider()), Server);

        Assert.Equal(["Backup", "media", "video"], entries.Select(e => e.Name));
        Assert.All(entries, e => Assert.Equal(FileEntryKind.Folder, e.Kind));
        Assert.Equal(@"\\nas\media", entries[1].Location.Value);
        Assert.Equal(["nas"], shares.Asked);
    }

    [Fact]
    public async Task ListAsync_ForAServerWithoutShares_IsEmptyNotAnError()
    {
        var entries = await ListAsync(new ServerSharesProvider(new FakeShares()), Server);

        Assert.Empty(entries);
    }

    [Theory]
    [InlineData(@"\\nas", true)]
    [InlineData(@"\\nas\media", false)]
    [InlineData(@"\\nas\media\films", false)]
    [InlineData(@"C:\", false)]
    public void CanHandle_IsOnlyTheServerRoot(string path, bool expected)
    {
        var provider = new ServerSharesProvider(new FakeShares());

        Assert.Equal(expected, provider.CanHandle(LocationPath.Parse(path)));
    }

    [Fact]
    public async Task ListAsync_WhenTheServerDoesNotAnswer_FailsAsUnreachable()
    {
        // ERROR_BAD_NETPATH (53) the way WindowsShareEnumerator reports it.
        var shares = new FakeShares { Failure = new IOException("gone") { HResult = unchecked((int)0x80070035) } };

        var error = await Assert.ThrowsAsync<LocationException>(() => ListAsync(new ServerSharesProvider(shares), Server));

        Assert.Equal(LocationError.Unreachable, error.Error);
    }

    [Fact]
    public async Task ListAsync_WhenAccessIsDenied_SaysSo()
    {
        var shares = new FakeShares { Failure = new UnauthorizedAccessException() };

        var error = await Assert.ThrowsAsync<LocationException>(() => ListAsync(new ServerSharesProvider(shares), Server));

        Assert.Equal(LocationError.AccessDenied, error.Error);
    }

    [Fact]
    public async Task ListAsync_WhenCancelled_IsNotAnEmptyServer()
    {
        using var cancel = new CancellationTokenSource();
        await cancel.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ListAsync(new ServerSharesProvider(new FakeShares()), Server, cancel.Token));
    }

    [Fact]
    public async Task ListAsync_ForAShare_IsRefused()
    {
        var provider = new ServerSharesProvider(new FakeShares());

        await Assert.ThrowsAsync<LocationException>(() => ListAsync(provider, LocationPath.Parse(@"\\nas\media")));
    }

    [Fact]
    public void ServerRoot_ContainsItsShares_AndOnlyItsOwn()
    {
        Assert.True(Server.Contains(LocationPath.Parse(@"\\nas\media\films")));
        Assert.True(Server.Contains(LocationPath.Parse(@"\\NAS\media")));
        Assert.False(Server.Contains(LocationPath.Parse(@"\\other\media")));
        Assert.False(Server.Contains(LocationPath.Parse(@"C:\")));
        Assert.False(LocationPath.Parse(@"\\nas\media").Contains(Server));
    }
}
