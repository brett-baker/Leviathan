using System.IO.Abstractions.TestingHelpers;
using System.Net;
using Leviathan.ViewModels.Settings;
using Leviathan.ViewModels.Triage.Learning;

namespace Leviathan.ViewModels.Tests;

/// <summary>Learning is optional and a server is optional: the switch, the list of servers, what is saved and how a connection is tested.</summary>
public sealed class LearningSettingsTests
{
    private const string Path = @"C:\data\settings.json";

    // ---- the switch and the list ----

    [Fact]
    public void Learning_IsOffByDefault_WithNoServers()
    {
        var learning = new AppSettings().Learning;

        Assert.False(learning.Enabled);
        Assert.Empty(learning.Sources);
        Assert.Empty(learning.Active);
    }

    [Fact]
    public void Active_AreTheEnabledServers_OnlyWhileLearningIsOn()
    {
        var on = LearningSource.Create(LearningSourceKind.Stash, "Home", "192.168.1.33:9999");
        var off = on with { Id = "b", Enabled = false };
        var settings = new LearningSettings { Enabled = true, Sources = [on, off] };

        Assert.Equal([on], settings.Active);
        Assert.Empty((settings with { Enabled = false }).Active);
    }

    [Theory]
    [InlineData("192.168.1.33:9999", "http://192.168.1.33:9999")]
    [InlineData("  http://nas:9999/  ", "http://nas:9999")]
    [InlineData("https://stash.example.com/graphql", "https://stash.example.com")]
    [InlineData("http://nas:9999/graphql/", "http://nas:9999")]
    [InlineData("", "")]
    [InlineData("   ", "")]
    [InlineData("ftp://nas", "")]
    [InlineData("http://", "")]
    public void NormalizeUrl_GivesAnAddressTheClientCanUse_OrNothing(string typed, string expected)
    {
        Assert.Equal(expected, LearningSource.NormalizeUrl(typed));
    }

    [Fact]
    public void Cleaned_DropsBlankAndDuplicateIds_AndTidiesAddressAndName()
    {
        var settings = new LearningSettings
        {
            Sources =
            [
                new("a", LearningSourceKind.Stash, "  ", "nas:9999"),
                new("a", LearningSourceKind.Stash, "Copy", "other:1"),
                new("", LearningSourceKind.Stash, "No id", "x:1"),
                new("c", (LearningSourceKind)99, "Unknown kind", "x:1"),
                new("d", LearningSourceKind.Stash, "Bad address", "ftp://x"),
            ],
        };

        var cleaned = settings.Cleaned().Sources;

        Assert.Equal(["a", "d"], cleaned.Select(s => s.Id));
        Assert.Equal("Stash", cleaned[0].Name);
        Assert.Equal("http://nas:9999", cleaned[0].Url);
        Assert.Equal(string.Empty, cleaned[1].Url);
    }

    [Fact]
    public void AServerWithNoUsableAddress_IsNeverActive()
    {
        var settings = new LearningSettings { Enabled = true, Sources = [new("d", LearningSourceKind.Stash, "Bad", "ftp://x")] }.Cleaned();

        Assert.Empty(settings.Active);
    }

    // ---- saved with the rest of the settings ----

    [Fact]
    public async Task TheSwitchAndTheServers_AreSaved_AndComeBackAfterARestart()
    {
        var disk = new MockFileSystem();
        using var store = new SettingsStore(disk, Path);
        await store.LoadAsync(CancellationToken.None);
        var source = LearningSource.Create(LearningSourceKind.Stash, "Home Stash", "192.168.1.33:9999");
        await store.UpdateAsync(s => s with { Learning = new LearningSettings { Enabled = true, Sources = [source] } }, CancellationToken.None);

        using var reopened = new SettingsStore(disk, Path);
        var learning = (await reopened.LoadAsync(CancellationToken.None)).Learning;

        Assert.True(learning.Enabled);
        Assert.Equal(source, Assert.Single(learning.Sources));
    }

    [Fact]
    public async Task TheSettingsFile_NeverHoldsAKey()
    {
        var disk = new MockFileSystem();
        using var store = new SettingsStore(disk, Path);
        await store.LoadAsync(CancellationToken.None);
        await store.UpdateAsync(s => s with { Learning = new LearningSettings { Enabled = true, Sources = [LearningSource.Create(LearningSourceKind.Stash, "Home", "nas:9999")] } }, CancellationToken.None);

        var text = disk.File.ReadAllText(Path);

        Assert.DoesNotContain("apikey", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("secret", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AnOldFile_WithoutLearning_HasItOff()
    {
        var disk = new MockFileSystem();
        disk.AddFile(Path, new MockFileData("{ \"schemaVersion\": 1 }"));
        using var store = new SettingsStore(disk, Path);

        var learning = (await store.LoadAsync(CancellationToken.None)).Learning;

        Assert.False(learning.Enabled);
        Assert.Empty(learning.Sources);
    }

    // ---- testing a Stash server ----

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public HttpRequestMessage? Seen { get; private set; }

        public string? Key { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Seen = request;
            Key = request.Headers.TryGetValues("ApiKey", out var values) ? values.Single() : null;
            return Task.FromResult(respond(request));
        }
    }

    private static LearningSource Stash(string url = "nas:9999") => LearningSource.Create(LearningSourceKind.Stash, "Home", url);

    private static Task<LearningTestResult> Test(Handler handler, string? key = "k", string url = "nas:9999") =>
        new StashSourceTester(() => handler).TestAsync(Stash(url), key, CancellationToken.None);

    private static HttpResponseMessage Json(string body, HttpStatusCode code = HttpStatusCode.OK) =>
        new(code) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") };

    [Fact]
    public async Task Test_AStashThatAnswers_IsConnected_AndNamesItsVersion()
    {
        var handler = new Handler(_ => Json("{\"data\":{\"version\":{\"version\":\"v0.31.1\"}}}"));

        var result = await Test(handler);

        Assert.True(result.Ok);
        Assert.Contains("v0.31.1", result.Message);
        Assert.Equal("http://nas:9999/graphql", handler.Seen!.RequestUri!.ToString());
        Assert.Equal("k", handler.Key);
    }

    [Fact]
    public async Task Test_WithNoKey_SendsNoKeyHeader()
    {
        var handler = new Handler(_ => Json("{\"data\":{\"version\":{\"version\":\"v1\"}}}"));

        await Test(handler, key: null);

        Assert.Null(handler.Key);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task Test_ARefusedKey_SaysSo_WithoutRepeatingTheKey(HttpStatusCode code)
    {
        var result = await Test(new Handler(_ => new HttpResponseMessage(code)), key: "super-secret-key");

        Assert.False(result.Ok);
        Assert.Contains("key", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("super-secret-key", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Test_ANoKeyAtAll_AskedFor()
    {
        var result = await Test(new Handler(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized)), key: null);

        Assert.False(result.Ok);
        Assert.Contains("needs an API key", result.Message);
    }

    [Fact]
    public async Task Test_AnotherKindOfServer_IsNotStash()
    {
        var result = await Test(new Handler(_ => Json("<html>hello</html>")));

        Assert.False(result.Ok);
        Assert.Contains("not like Stash", result.Message);
    }

    [Fact]
    public async Task Test_AServerError_ShowsTheStatus()
    {
        var result = await Test(new Handler(_ => new HttpResponseMessage(HttpStatusCode.NotFound)));

        Assert.False(result.Ok);
        Assert.Contains("404", result.Message);
    }

    [Fact]
    public async Task Test_AServerThatCannotBeReached_IsAResult_NotAnException()
    {
        var result = await Test(new Handler(_ => throw new HttpRequestException("no route")));

        Assert.False(result.Ok);
        Assert.Contains("Could not reach", result.Message);
    }

    [Fact]
    public async Task Test_NotAnAddress_IsRefusedWithoutAsking()
    {
        var handler = new Handler(_ => Json("{}"));

        var result = await Test(handler, url: "ftp://nope");

        Assert.False(result.Ok);
        Assert.Null(handler.Seen);
    }
}
