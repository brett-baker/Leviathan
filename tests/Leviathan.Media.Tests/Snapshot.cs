using System.Text.Json;
using System.Text.Json.Serialization;

namespace Leviathan.Media.Tests;

/// <summary>
/// A small snapshot check: the parsed result is written as indented JSON and compared with the committed <c>.verified.json</c> next to the
/// sample. A difference fails with both texts. To accept a deliberate change, run the tests once with <c>LEVIATHAN_UPDATE_SNAPSHOTS=1</c>
/// and read the diff in git before committing it. (A package for this was not worth a dependency: this is 30 lines.)
/// </summary>
internal static class Snapshot
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
        NewLine = "\n",
    };

    public static string SamplePath(string name) => Path.Combine(AppContext.BaseDirectory, "Samples", name);

    public static string Read(string sampleName) => File.ReadAllText(SamplePath(sampleName));

    public static void Verify(string sampleName, object actual)
    {
        var text = JsonSerializer.Serialize(actual, Options) + "\n";
        var verified = SamplePath(sampleName.Replace(".json", ".verified.json", StringComparison.Ordinal));
        if (Environment.GetEnvironmentVariable("LEVIATHAN_UPDATE_SNAPSHOTS") == "1")
        {
            // Written into the source tree, so the change shows up in git.
            var source = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "Samples", Path.GetFileName(verified)));
            File.WriteAllText(source, text);
            return;
        }

        Assert.True(File.Exists(verified), $"No snapshot for {sampleName}. Run once with LEVIATHAN_UPDATE_SNAPSHOTS=1, read the result, and commit it.");
        Assert.Equal(File.ReadAllText(verified).ReplaceLineEndings("\n"), text);
    }
}
