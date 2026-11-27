using System.Net;
using System.Text.Json;

namespace Leviathan.ViewModels.Triage.Learning;

/// <summary>
/// Tests a Stash server by asking its GraphQL endpoint for its version with the <c>ApiKey</c> header. Read-only: nothing on the server
/// is changed. The key is sent only to the address the user typed and never appears in a message.
/// </summary>
public sealed class StashSourceTester(Func<HttpMessageHandler>? handler = null, TimeSpan? timeout = null) : ILearningSourceTester
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(10);

    /// <inheritdoc />
    public async Task<LearningTestResult> TestAsync(LearningSource source, string? secret, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        var url = LearningSource.NormalizeUrl(source.Url);
        if (url.Length == 0)
        {
            return new LearningTestResult(false, "That is not a web address. Try something like http://192.168.1.33:9999");
        }

        using var client = handler is null ? new HttpClient() : new HttpClient(handler(), disposeHandler: true);
        using var clock = new CancellationTokenSource(timeout ?? DefaultTimeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, clock.Token);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, url + "/graphql")
            {
                Content = new StringContent("{\"query\":\"{ version { version } }\"}", System.Text.Encoding.UTF8, "application/json"),
            };
            if (!string.IsNullOrEmpty(secret))
            {
                request.Headers.TryAddWithoutValidation("ApiKey", secret);
            }

            using var response = await client.SendAsync(request, linked.Token).ConfigureAwait(false);
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                return new LearningTestResult(false, string.IsNullOrEmpty(secret) ? "The server needs an API key." : "The server refused the API key.");
            }

            if (!response.IsSuccessStatusCode)
            {
                return new LearningTestResult(false, $"The server answered {(int)response.StatusCode}. Is this the address of Stash?");
            }

            var body = await response.Content.ReadAsStringAsync(linked.Token).ConfigureAwait(false);
            return Read(body);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new LearningTestResult(false, "The server did not answer in time.");
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidOperationException)
        {
            return new LearningTestResult(false, "Could not reach the server. Check the address and that it is running.");
        }
    }

    private static LearningTestResult Read(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.TryGetProperty("data", out var data)
                && data.ValueKind == JsonValueKind.Object
                && data.TryGetProperty("version", out var version)
                && version.ValueKind == JsonValueKind.Object
                && version.TryGetProperty("version", out var text)
                && text.ValueKind == JsonValueKind.String)
            {
                return new LearningTestResult(true, $"Connected to Stash {text.GetString()}.");
            }
        }
        catch (JsonException)
        {
            // Not JSON: not Stash.
        }

        return new LearningTestResult(false, "The server answered, but not like Stash. Is the address right?");
    }
}
