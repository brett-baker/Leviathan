using Leviathan.Core.Paths;
using Leviathan.Interop;

namespace Leviathan.Interop.Tests;

/// <summary>Which destinations can hold colour tags (spec §11.2).</summary>
public sealed class WindowsStreamSupportTests
{
    [Fact]
    public async Task TheTempFolderVolume_IsAskedAndAnswered()
    {
        var folder = LocationPath.Parse(Path.GetTempPath());

        var answer = await new WindowsStreamSupport().SupportsStreamsAsync(folder, CancellationToken.None);

        Assert.NotNull(answer); // NTFS says true; whatever the volume is, the question is answered
    }

    [Theory]
    [InlineData("sftp://nas/home")]
    [InlineData("ftp://nas/pub")]
    public async Task ProtocolsWithoutStreams_AreNotAsked(string url)
    {
        Assert.False(await new WindowsStreamSupport().SupportsStreamsAsync(LocationPath.Parse(url), CancellationToken.None));
    }

    [Fact]
    public async Task AServerRoot_HasNoVolume_SoItIsUnknown()
    {
        Assert.Null(await new WindowsStreamSupport().SupportsStreamsAsync(LocationPath.Parse(@"\\nas"), CancellationToken.None));
    }
}
