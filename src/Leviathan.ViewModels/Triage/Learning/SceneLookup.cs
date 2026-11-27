using System.Net;
using System.Text;
using System.Text.Json;

namespace Leviathan.ViewModels.Triage.Learning;

/// <summary>What asking one server about one file found.</summary>
/// <param name="Reached">False when the server could not be asked (down, wrong key): the file is not complete and is tried again later.</param>
/// <param name="Scene">The scene, or null when the server has no scene for the file.</param>
public sealed record SceneLookupResult(bool Reached, SceneFacts? Scene);

/// <summary>Finds the scene a server holds for a file. Read-only: nothing on the server is ever changed.</summary>
public interface ISceneLookup
{
    /// <summary>Asks <paramref name="source"/> for the scene whose file has this name (and size, when the server holds one). Never throws for a network or server problem.</summary>
    Task<SceneLookupResult> FindAsync(LearningSource source, string? secret, string path, long size, CancellationToken cancellationToken);
}

/// <summary>
/// Finds a scene in Stash by file name. Stash holds the path as the server sees it (often a container path), so the match is on the
/// file name, confirmed by size when Stash reports one, never on the whole path. Only a <c>findScenes</c> query is sent.
/// </summary>
public sealed class StashSceneLookup(Func<HttpMessageHandler>? handler = null, TimeSpan? timeout = null) : ISceneLookup
{
    private const string Query = "query($name: String!) { findScenes(scene_filter: { path: { value: $name, modifier: INCLUDES } }, filter: { per_page: 10 }) { scenes { title date rating100 studio { name } performers { name } tags { name } files { path size } } } }";

    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(10);

    /// <inheritdoc />
    public async Task<SceneLookupResult> FindAsync(LearningSource source, string? secret, string path, long size, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        var url = LearningSource.NormalizeUrl(source.Url);
        var name = FileName(path);
        if (url.Length == 0 || name.Length == 0)
        {
            return new SceneLookupResult(false, null);
        }

        using var client = handler is null ? new HttpClient() : new HttpClient(handler(), disposeHandler: true);
        using var clock = new CancellationTokenSource(timeout ?? DefaultTimeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, clock.Token);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, url + "/graphql") { Content = new StringContent(Body(name), Encoding.UTF8, "application/json") };
            if (!string.IsNullOrEmpty(secret))
            {
                request.Headers.TryAddWithoutValidation("ApiKey", secret);
            }

            using var response = await client.SendAsync(request, linked.Token).ConfigureAwait(false);
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden || !response.IsSuccessStatusCode)
            {
                return new SceneLookupResult(false, null);
            }

            return Read(source.Id, await response.Content.ReadAsStringAsync(linked.Token).ConfigureAwait(false), name, size);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new SceneLookupResult(false, null);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidOperationException)
        {
            return new SceneLookupResult(false, null);
        }
    }

    private static string FileName(string path)
    {
        var cut = path.LastIndexOfAny(['\\', '/']);
        return cut >= 0 ? path[(cut + 1)..] : path;
    }

    private static string Body(string name)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("query", Query);
            writer.WriteStartObject("variables");
            writer.WriteString("name", name);
            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static SceneLookupResult Read(string sourceId, string body, string name, long size)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            if (!document.RootElement.TryGetProperty("data", out var data)
                || data.ValueKind != JsonValueKind.Object
                || !data.TryGetProperty("findScenes", out var find)
                || !find.TryGetProperty("scenes", out var scenes)
                || scenes.ValueKind != JsonValueKind.Array)
            {
                return new SceneLookupResult(false, null);
            }

            JsonElement? best = null;
            foreach (var scene in scenes.EnumerateArray())
            {
                var rank = Match(scene, name, size);
                if (rank == 2)
                {
                    best = scene;
                    break;
                }

                if (rank == 1)
                {
                    best ??= scene;
                }
            }

            return new SceneLookupResult(true, best is { } found ? Scene(sourceId, found) : null);
        }
        catch (JsonException)
        {
            return new SceneLookupResult(false, null);
        }
    }

    // 2: the file name and the size match; 1: the file name matches (the size is unknown or differs); 0: not this file.
    private static int Match(JsonElement scene, string name, long size)
    {
        var rank = 0;
        if (scene.TryGetProperty("files", out var files) && files.ValueKind == JsonValueKind.Array)
        {
            foreach (var file in files.EnumerateArray())
            {
                if (file.TryGetProperty("path", out var p) && p.ValueKind == JsonValueKind.String
                    && string.Equals(FileName(p.GetString() ?? string.Empty), name, StringComparison.OrdinalIgnoreCase))
                {
                    var sameSize = size > 0 && file.TryGetProperty("size", out var s) && s.ValueKind == JsonValueKind.Number && s.TryGetInt64(out var bytes) && bytes == size;
                    rank = Math.Max(rank, sameSize ? 2 : 1);
                }
            }
        }

        return rank;
    }

    private static SceneFacts Scene(string sourceId, JsonElement scene) => new(
        sourceId,
        Text(scene, "title"),
        scene.TryGetProperty("studio", out var studio) && studio.ValueKind == JsonValueKind.Object ? Text(studio, "name") : null,
        Text(scene, "date"),
        scene.TryGetProperty("rating100", out var rating) && rating.ValueKind == JsonValueKind.Number && rating.TryGetInt32(out var value) ? value : null,
        Names(scene, "performers"),
        Names(scene, "tags"));

    private static string? Text(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString()) ? value.GetString() : null;

    private static List<string> Names(JsonElement scene, string property)
    {
        var names = new List<string>();
        if (scene.TryGetProperty(property, out var list) && list.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in list.EnumerateArray())
            {
                if (Text(item, "name") is { } name)
                {
                    names.Add(name);
                }
            }
        }

        return names;
    }
}
