using System.Text;

namespace Leviathan.Search.Core.Tests;

/// <summary>
/// Property tests for the parser (build plan, Phase 4 tests). They use a seeded generator, so a failure repeats; FsCheck was not added
/// for these few properties (see docs/decisions.md).
/// </summary>
public sealed class QueryPropertyTests
{
    private static readonly string[] Pieces =
    [
        "a", "holiday", "IMG_*.jpg", "?", "-", "--", "\"", "\"a b\"", ":", "::", "ext:", "ext:mkv,mp4", "size:>2GB", "size:1MB..2GB", "size:..", "size:>", "modified:lastweek",
        "modified:2025-03", "created:>2024-01-01", "tag:red", "-tag:green", "type:video", "path:\"C:\\x y\"", "duration:>1h30m", "duration:1:2:3", "res:>=4k", "res:99999999999",
        "codec:hevc", "content:\"invoice\"", "regex:(", "regex:^a.*$", "x:", "C:\\Videos", "\t", "  ", "\u00e9", "\uD83D\uDE00", "..", ">", "<=", "=",
    ];

    private static string Generate(Random random)
    {
        var text = new StringBuilder();
        var count = random.Next(0, 8);
        for (var i = 0; i < count; i++)
        {
            text.Append(Pieces[random.Next(Pieces.Length)]);
            if (random.Next(3) > 0)
            {
                text.Append(' ');
            }
        }

        return text.ToString();
    }

    [Fact]
    public void Parse_NeverThrows_AndErrorsStayInsideTheText()
    {
        var random = new Random(4);
        for (var i = 0; i < 5000; i++)
        {
            var text = Generate(random);
            var query = QueryParser.Parse(text);
            foreach (var error in query.Errors)
            {
                Assert.InRange(error.Start, 0, text.Length);
                Assert.True(error.Start + error.Length <= text.Length, text);
                Assert.NotEmpty(error.Message);
            }
        }
    }

    [Fact]
    public void Parse_ArbitraryCharacters_NeverThrows()
    {
        var random = new Random(9);
        for (var i = 0; i < 5000; i++)
        {
            var chars = new char[random.Next(0, 40)];
            for (var c = 0; c < chars.Length; c++)
            {
                chars[c] = (char)random.Next(0, 0x250);
            }

            _ = QueryParser.Parse(new string(chars));
        }
    }

    [Fact]
    public void Format_ThenParse_IsStable()
    {
        var random = new Random(21);
        for (var i = 0; i < 5000; i++)
        {
            var source = Generate(random);
            var first = QueryFormatter.Format(QueryParser.Parse(source));
            var again = QueryParser.Parse(first);

            Assert.True(again.IsValid, $"'{first}' did not parse back: {string.Join("; ", again.Errors.Select(e => e.Message))}");
            Assert.True(first == QueryFormatter.Format(again), $"source [{source}] first [{first}] again [{QueryFormatter.Format(again)}]");
        }
    }

    [Fact]
    public void Parse_WithoutQuotes_EveryTokenIsKeptOrReported()
    {
        var random = new Random(77);
        for (var i = 0; i < 2000; i++)
        {
            var text = Generate(random);
            if (text.Contains('"', StringComparison.Ordinal))
            {
                continue;
            }

            var query = QueryParser.Parse(text);
            var tokens = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;

            Assert.Equal(tokens, query.Clauses.Count + query.Errors.Count);
        }
    }
}
