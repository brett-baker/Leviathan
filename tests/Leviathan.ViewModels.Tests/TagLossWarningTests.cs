using Leviathan.Core.Paths;
using Leviathan.Core.Platform;
using Leviathan.ViewModels.Tags;
using Leviathan.ViewModels.Transfers;
using Leviathan.ViewModels.Triage;

namespace Leviathan.ViewModels.Tests;

/// <summary>The "Tags may be lost" note on copies to destinations without streams (spec §11.2).</summary>
public sealed class TagLossWarningTests
{
    private sealed class Inner(bool started = true) : ITransferService
    {
        public int Calls { get; private set; }

        public Task<TransferOutcome> StartAsync(TransferRequest request, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(new TransferOutcome(started, "Copying 2 items."));
        }
    }

    private sealed class Streams(bool? answer) : IStreamSupport
    {
        public Task<bool?> SupportsStreamsAsync(LocationPath folder, CancellationToken cancellationToken) => Task.FromResult(answer);
    }

    private sealed class Tags(params string[] tagged) : ITagStore
    {
        public Task<IReadOnlySet<int>> GetAsync(string path, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlySet<int>>(tagged.Contains(path, StringComparer.OrdinalIgnoreCase) ? new HashSet<int> { 1 } : new HashSet<int>());

        public Task SetAsync(string path, IReadOnlySet<int> tags, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private static TransferRequest Request(TransferMode mode = TransferMode.Copy) =>
        new([LocationPath.Parse(@"C:\a\one.jpg"), LocationPath.Parse(@"C:\a\two.jpg")], LocationPath.Parse(@"F:\usb"), mode);

    [Fact]
    public async Task TaggedFiles_ToADestinationWithoutStreams_GetTheWarning_AndStillStart()
    {
        var inner = new Inner();
        var service = new TagLossWarningTransfers(inner, new Tags(@"C:\a\two.jpg"), new Streams(false));

        var outcome = await service.StartAsync(Request(), CancellationToken.None);

        Assert.Equal(1, inner.Calls);
        Assert.True(outcome.Started);
        Assert.Equal("Copying 2 items. Tags may be lost: usb cannot keep colour tags.", outcome.Message);
    }

    [Fact]
    public async Task UntaggedFiles_GetNoWarning()
    {
        var service = new TagLossWarningTransfers(new Inner(), new Tags(), new Streams(false));

        Assert.Equal("Copying 2 items.", (await service.StartAsync(Request(), CancellationToken.None)).Message);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(null)]
    public async Task ADestinationThatKeepsStreams_OrIsUnknown_GetsNoWarning(bool? answer)
    {
        var service = new TagLossWarningTransfers(new Inner(), new Tags(@"C:\a\one.jpg"), new Streams(answer));

        Assert.Equal("Copying 2 items.", (await service.StartAsync(Request(), CancellationToken.None)).Message);
    }

    [Fact]
    public async Task Shortcuts_AndRefusedTransfers_GetNoWarning()
    {
        var tagged = new Tags(@"C:\a\one.jpg");
        var link = new TagLossWarningTransfers(new Inner(), tagged, new Streams(false));
        var refused = new TagLossWarningTransfers(new Inner(started: false), tagged, new Streams(false));

        Assert.DoesNotContain("Tags may be lost", (await link.StartAsync(Request(TransferMode.Link), CancellationToken.None)).Message);
        Assert.DoesNotContain("Tags may be lost", (await refused.StartAsync(Request(), CancellationToken.None)).Message);
    }
}
