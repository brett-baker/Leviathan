using System.Runtime.CompilerServices;
using Leviathan.Core.Model;
using Leviathan.Core.Paths;
using Leviathan.Core.Providers;
using Leviathan.Core.Resilience;
using Leviathan.Testing;

namespace Leviathan.Core.Tests;

/// <summary>A disconnected mapped drive is reconnected the first time it is used, once, quickly, and only for mapped drives (spec §3.3).</summary>
public sealed class ReconnectingLocationProviderTests
{
    private sealed class FakeConnector : IShareConnector
    {
        public List<char> Reconnected { get; } = [];

        public ConnectResult Result { get; set; } = new(ConnectOutcome.Connected);

        public TaskCompletionSource<ConnectResult>? Hang { get; set; }

        public Func<Task>? OnReconnect { get; set; }

        public async Task<ConnectResult> ReconnectDriveAsync(char driveLetter, CancellationToken cancellationToken)
        {
            Reconnected.Add(driveLetter);
            if (OnReconnect is not null)
            {
                await OnReconnect();
            }

            return Hang is null ? Result : await Hang.Task;
        }

        public Task<ConnectResult> SignInAsync(LocationPath location, nint owner, CancellationToken cancellationToken) =>
            Task.FromResult(new ConnectResult(ConnectOutcome.Cancelled));

        public Task<ConnectResult> MapDriveAsync(char driveLetter, string shareRoot, CancellationToken cancellationToken) =>
            Task.FromResult(new ConnectResult(ConnectOutcome.Connected));
    }

    /// <summary>Fails with <see cref="Failure"/> until <see cref="Healthy"/> is set, then lists one batch.</summary>
    private sealed class FlakyProvider : ILocationProvider
    {
        public bool Healthy { get; set; }

        public Exception Failure { get; set; } = new LocationException(LocationError.Unreachable, LocationPath.Parse(@"Y:\"), "Y: is not responding.");

        public int Calls { get; private set; }

        public string Name => "flaky";

        public bool CanHandle(LocationPath location) => true;

        public async IAsyncEnumerable<ListingBatch> ListAsync(LocationPath location, ListingOptions options, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            Calls++;
            await Task.Yield();
            if (!Healthy)
            {
                throw Failure;
            }

            yield return new ListingBatch([], true);
        }
    }

    private static readonly LocationPath Mapped = LocationPath.Parse(@"Y:\media");

    private static async Task<int> ListAsync(ReconnectingLocationProvider provider, LocationPath location)
    {
        var batches = 0;
        await foreach (var batch in provider.ListAsync(location, ListingOptions.Default, CancellationToken.None))
        {
            _ = batch;
            batches++;
        }

        return batches;
    }

    private static (ReconnectingLocationProvider Provider, FakeConnector Connector, FlakyProvider Inner, ManualTimeProvider Time) Make(Func<LocationPath, bool>? isMapped = null)
    {
        var connector = new FakeConnector();
        var inner = new FlakyProvider();
        var time = new ManualTimeProvider();
        return (new ReconnectingLocationProvider(inner, connector, isMapped ?? (l => l.Root.StartsWith('Y')), time), connector, inner, time);
    }

    [Fact]
    public async Task ADisconnectedMappedDrive_IsReconnected_AndTheListingIsTriedAgain()
    {
        var (provider, connector, inner, _) = Make();
        connector.OnReconnect = () =>
        {
            inner.Healthy = true;
            return Task.CompletedTask;
        };

        var batches = await ListAsync(provider, Mapped);

        Assert.Equal(1, batches);
        Assert.Equal(['Y'], connector.Reconnected);
        Assert.Equal(2, inner.Calls);
    }

    [Fact]
    public async Task ASignInFailure_IsAlsoReconnected()
    {
        var (provider, connector, inner, _) = Make();
        inner.Failure = new LocationException(LocationError.AuthenticationRequired, Mapped, "Y: needs you to sign in.");
        connector.OnReconnect = () =>
        {
            inner.Healthy = true;
            return Task.CompletedTask;
        };

        await ListAsync(provider, Mapped);

        Assert.Equal(['Y'], connector.Reconnected);
    }

    [Fact]
    public async Task IfReconnectingFails_TheOriginalErrorComesOut_AfterOneAttempt()
    {
        var (provider, connector, inner, _) = Make();
        connector.Result = new ConnectResult(ConnectOutcome.Failed, "The network is down.");

        var error = await Assert.ThrowsAsync<LocationException>(() => ListAsync(provider, Mapped));

        Assert.Equal(LocationError.Unreachable, error.Error);
        Assert.Single(connector.Reconnected);
        Assert.Equal(1, inner.Calls);
    }

    [Fact]
    public async Task ReconnectingThatDoesNotHelp_IsNotRepeated_AndTheNextTryWaitsForTheGap()
    {
        var (provider, connector, inner, time) = Make();

        await Assert.ThrowsAsync<LocationException>(() => ListAsync(provider, Mapped));
        await Assert.ThrowsAsync<LocationException>(() => ListAsync(provider, Mapped));
        Assert.Single(connector.Reconnected);

        time.Advance(provider.MinGap + TimeSpan.FromSeconds(1));
        await Assert.ThrowsAsync<LocationException>(() => ListAsync(provider, Mapped));

        Assert.Equal(2, connector.Reconnected.Count);
        Assert.True(inner.Calls >= 3);
    }

    [Fact]
    public async Task ReconnectingThatSucceedsButStillFails_DoesNotLoop()
    {
        var (provider, connector, inner, _) = Make();

        await Assert.ThrowsAsync<LocationException>(() => ListAsync(provider, Mapped));

        // One reconnect, one retry, then the error: never a loop.
        Assert.Single(connector.Reconnected);
        Assert.Equal(2, inner.Calls);
    }

    [Fact]
    public async Task AReconnectThatHangs_IsGivenUpOn_SoTheListingStillFailsFast()
    {
        var (provider, connector, _, time) = Make();
        connector.Hang = new TaskCompletionSource<ConnectResult>();

        var listing = Assert.ThrowsAsync<LocationException>(() => ListAsync(provider, Mapped));
        while (connector.Reconnected.Count == 0)
        {
            await Task.Delay(5);
        }

        time.Advance(provider.ReconnectTimeout + TimeSpan.FromMilliseconds(1));

        var error = await listing;
        Assert.Equal(LocationError.Unreachable, error.Error);
    }

    [Theory]
    [InlineData(LocationError.NotFound)]
    [InlineData(LocationError.AccessDenied)]
    public async Task OtherErrors_AreNotWorthReconnecting(LocationError kind)
    {
        var (provider, connector, inner, _) = Make();
        inner.Failure = new LocationException(kind, Mapped, "no");

        await Assert.ThrowsAsync<LocationException>(() => ListAsync(provider, Mapped));

        Assert.Empty(connector.Reconnected);
    }

    [Fact]
    public async Task LocalDrivesAndSharePaths_PassStraightThrough()
    {
        var (provider, connector, inner, _) = Make(isMapped: _ => false);

        await Assert.ThrowsAsync<LocationException>(() => ListAsync(provider, LocationPath.Parse(@"C:\data")));
        await Assert.ThrowsAsync<LocationException>(() => ListAsync(provider, LocationPath.Parse(@"\\nas\media")));

        Assert.Empty(connector.Reconnected);
        Assert.Equal(2, inner.Calls);
    }

    [Fact]
    public async Task AHealthyDrive_IsListedOnce_WithoutAnyReconnect()
    {
        var (provider, connector, inner, _) = Make();
        inner.Healthy = true;

        Assert.Equal(1, await ListAsync(provider, Mapped));

        Assert.Empty(connector.Reconnected);
        Assert.Equal(1, inner.Calls);
    }
}
