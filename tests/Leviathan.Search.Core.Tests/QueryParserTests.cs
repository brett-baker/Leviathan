using System.Globalization;

namespace Leviathan.Search.Core.Tests;

/// <summary>The query syntax of spec §13.1.</summary>
public sealed class QueryParserTests
{
    private static T Single<T>(string text, out bool negated)
        where T : QueryTerm
    {
        var query = QueryParser.Parse(text);
        Assert.True(query.IsValid, string.Join("; ", query.Errors.Select(e => e.Message)));
        var clause = Assert.Single(query.Clauses);
        negated = clause.Negated;
        return Assert.IsType<T>(clause.Term);
    }

    private static T Single<T>(string text)
        where T : QueryTerm => Single<T>(text, out _);

    [Fact]
    public void Parse_NullAndBlank_AreEmptyAndValid()
    {
        Assert.True(QueryParser.Parse(null).IsEmpty);
        var blank = QueryParser.Parse("   ");
        Assert.True(blank.IsEmpty);
        Assert.True(blank.IsValid);
    }

    [Fact]
    public void Parse_PlainWords_AreNameTermsCombinedWithAnd()
    {
        var query = QueryParser.Parse("holiday  video");

        Assert.Equal(["holiday", "video"], query.Clauses.Select(c => ((NameTerm)c.Term).Text));
    }

    [Fact]
    public void Parse_QuotedPhrase_IsOneTerm()
    {
        var term = Single<NameTerm>("\"summer holiday\"");

        Assert.Equal("summer holiday", term.Text);
        Assert.True(term.Quoted);
    }

    [Fact]
    public void Parse_Wildcards_AreDetected()
    {
        Assert.True(Single<NameTerm>("IMG_*.jpg").HasWildcards);
        Assert.False(Single<NameTerm>("IMG").HasWildcards);
    }

    [Fact]
    public void Parse_LeadingMinus_Excludes()
    {
        Single<NameTerm>("-draft", out var negated);

        Assert.True(negated);
    }

    [Fact]
    public void Parse_LoneMinus_IsJustText()
    {
        var term = Single<NameTerm>("-", out var negated);

        Assert.False(negated);
        Assert.Equal("-", term.Text);
    }

    [Fact]
    public void Parse_DriveLetter_IsTextNotAFilter()
    {
        Assert.Equal(@"C:\Videos", Single<NameTerm>(@"C:\Videos").Text);
    }

    [Fact]
    public void Parse_Extensions_AreLowerCasedAndDeduplicated()
    {
        var filter = Single<ExtensionFilter>("ext:MKV,.mp4,mkv");

        Assert.Equal(["mkv", "mp4"], filter.Extensions);
    }

    [Theory]
    [InlineData("size:>2GB", Comparison.Greater, 2L << 30)]
    [InlineData("size:>=1.5MB", Comparison.GreaterOrEqual, 1572864L)]
    [InlineData("size:<500kb", Comparison.Less, 500L << 10)]
    [InlineData("size:100", Comparison.Equal, 100L)]
    [InlineData("size:=2TiB", Comparison.Equal, 2L << 40)]
    public void Parse_Size_UsesBinaryUnits(string text, Comparison op, long bytes)
    {
        var size = Single<SizeFilter>(text).Size;

        Assert.Equal(op, size.Op);
        Assert.Equal(bytes, size.Value);
    }

    [Fact]
    public void Parse_SizeRange_HasBothEnds()
    {
        var size = Single<SizeFilter>("size:1MB..2GB").Size;

        Assert.Equal(Comparison.Between, size.Op);
        Assert.Equal(1L << 20, size.Value);
        Assert.Equal(2L << 30, size.Upper);
        Assert.True(size.Test(5L << 20));
        Assert.False(size.Test(3L << 30));
    }

    [Theory]
    [InlineData("size:abc")]
    [InlineData("size:5XB")]
    [InlineData("size:2GB..1MB")]
    [InlineData("size:99999999999TB")]
    public void Parse_BadSize_IsAnErrorNotAnException(string text)
    {
        var query = QueryParser.Parse(text);

        Assert.False(query.IsValid);
        Assert.Empty(query.Clauses);
        Assert.Equal(0, query.Errors[0].Start);
        Assert.Equal(text.Length, query.Errors[0].Length);
    }

    [Theory]
    [InlineData("modified:lastweek", DateKeyword.LastWeek)]
    [InlineData("modified:today", DateKeyword.Today)]
    [InlineData("created:LastYear", DateKeyword.LastYear)]
    public void Parse_DateKeyword(string text, DateKeyword keyword)
    {
        var filter = Single<DateFilter>(text);

        Assert.Equal(keyword, filter.Value.Keyword);
        Assert.Equal(text.StartsWith("created", StringComparison.Ordinal) ? DateField.Created : DateField.Modified, filter.Field);
    }

    [Theory]
    [InlineData("created:2025", DatePrecision.Year, "2025-01-01", "2026-01-01")]
    [InlineData("created:2025-03", DatePrecision.Month, "2025-03-01", "2025-04-01")]
    [InlineData("created:2024-02-29", DatePrecision.Day, "2024-02-29", "2024-03-01")]
    public void Parse_AbsoluteDate_NamesASpan(string text, DatePrecision precision, string start, string end)
    {
        var filter = Single<DateFilter>(text);

        Assert.Equal(precision, filter.Value.Precision);
        var span = filter.Value.Span(new DateOnly(2030, 1, 1));
        Assert.Equal(DateOnly.Parse(start, CultureInfo.InvariantCulture), span.Start);
        Assert.Equal(DateOnly.Parse(end, CultureInfo.InvariantCulture), span.End);
    }

    [Fact]
    public void DateKeywords_ResolveAgainstToday()
    {
        var wednesday = new DateOnly(2026, 11, 11);

        Assert.Equal((new DateOnly(2026, 11, 9), new DateOnly(2026, 11, 16)), DateOperand.FromKeyword(DateKeyword.ThisWeek).Span(wednesday));
        Assert.Equal((new DateOnly(2026, 11, 2), new DateOnly(2026, 11, 9)), DateOperand.FromKeyword(DateKeyword.LastWeek).Span(wednesday));
        Assert.Equal((new DateOnly(2026, 10, 1), new DateOnly(2026, 11, 1)), DateOperand.FromKeyword(DateKeyword.LastMonth).Span(wednesday));
        Assert.Equal((new DateOnly(2025, 1, 1), new DateOnly(2026, 1, 1)), DateOperand.FromKeyword(DateKeyword.LastYear).Span(wednesday));
        Assert.Equal((new DateOnly(2026, 11, 10), wednesday), DateOperand.FromKeyword(DateKeyword.Yesterday).Span(wednesday));
    }

    [Theory]
    [InlineData("modified:2025-13")]
    [InlineData("modified:2025-02-30")]
    [InlineData("modified:soon")]
    [InlineData("modified:25")]
    public void Parse_BadDate_IsAnError(string text) => Assert.False(QueryParser.Parse(text).IsValid);

    [Fact]
    public void Parse_DateRange()
    {
        var filter = Single<DateFilter>("modified:2025-01..2025-03");

        Assert.Equal(Comparison.Between, filter.Op);
        Assert.Equal(DatePrecision.Month, filter.Upper!.Precision);
    }

    [Fact]
    public void Parse_TagTypePathCodecContent()
    {
        Assert.Equal("red", Single<TagFilter>("tag:red").Name);
        Assert.Equal(FileTypeKind.Video, Single<TypeFilter>("type:Videos").Kind);
        Assert.Equal(@"\\nas\inbox", Single<PathFilter>(@"path:\\nas\inbox").Text);
        Assert.Equal("hevc", Single<CodecFilter>("codec:HEVC").Codec);
        Assert.Equal("invoice 2025", Single<ContentFilter>("content:\"invoice 2025\"").Text);
    }

    [Theory]
    [InlineData("duration:>30m", Comparison.Greater, 1800)]
    [InlineData("duration:<=1h30m", Comparison.LessOrEqual, 5400)]
    [InlineData("duration:90", Comparison.Equal, 90)]
    [InlineData("duration:1:30:00", Comparison.Equal, 5400)]
    [InlineData("duration:2:15", Comparison.Equal, 135)]
    [InlineData("duration:1.5h", Comparison.Equal, 5400)]
    public void Parse_Duration(string text, Comparison op, int seconds)
    {
        var duration = Single<DurationFilter>(text).Duration;

        Assert.Equal(op, duration.Op);
        Assert.Equal(TimeSpan.FromSeconds(seconds), duration.Value);
    }

    [Theory]
    [InlineData("duration:soon")]
    [InlineData("duration:5x")]
    [InlineData("duration:1:xx")]
    [InlineData("duration:1:2:3:4")]
    public void Parse_BadDuration_IsAnError(string text) => Assert.False(QueryParser.Parse(text).IsValid);

    [Theory]
    [InlineData("res:>=2160", Comparison.GreaterOrEqual, 2160)]
    [InlineData("res:4k", Comparison.Equal, 2160)]
    [InlineData("res:<1080p", Comparison.Less, 1080)]
    public void Parse_Resolution(string text, Comparison op, int height)
    {
        var constraint = Single<ResolutionFilter>(text).Height;

        Assert.Equal(op, constraint.Op);
        Assert.Equal(height, constraint.Value);
    }

    [Fact]
    public void Parse_Regex_IsCheckedToCompile()
    {
        Assert.Equal(@"^IMG_\d+\.jpg$", Single<RegexTerm>(@"regex:^IMG_\d+\.jpg$").Pattern);
        var bad = QueryParser.Parse("regex:(unclosed");
        Assert.False(bad.IsValid);
        Assert.Contains("regular expression", bad.Errors[0].Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_UnknownFilter_IsAnErrorThatPointsAtTheToken()
    {
        var query = QueryParser.Parse("holiday colour:red 4k");

        var error = Assert.Single(query.Errors);
        Assert.Equal(8, error.Start);
        Assert.Equal("colour:red".Length, error.Length);
        Assert.Equal(2, query.Clauses.Count);
    }

    [Fact]
    public void Parse_UnclosedQuote_IsAnError()
    {
        var query = QueryParser.Parse("ok \"never closed");

        Assert.Equal(3, Assert.Single(query.Errors).Start);
        Assert.Single(query.Clauses);
    }

    [Fact]
    public void Parse_EmptyFilterValue_IsAnError()
    {
        Assert.False(QueryParser.Parse("size:").IsValid);
        Assert.False(QueryParser.Parse("ext:,,").IsValid);
    }

    [Fact]
    public void Parse_TheExampleFromTheSpec()
    {
        var query = QueryParser.Parse(@"type:video -tag:green path:\\nas\inbox");

        Assert.True(query.IsValid);
        Assert.Equal(3, query.Clauses.Count);
        Assert.True(query.Clauses[1].Negated);
        Assert.Equal(@"type:video -tag:green path:\\nas\inbox", QueryFormatter.Format(query));
    }
}
