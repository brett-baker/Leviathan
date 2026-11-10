using Leviathan.Core.Model;
using Leviathan.Core.Paths;
using Leviathan.Testing;

namespace Leviathan.Search.Core.Tests;

/// <summary>Matching a parsed query against files (spec §13.1).</summary>
public sealed class QueryMatcherTests
{
    // Wednesday 2026-11-11, noon UTC; the zone is UTC so dates are predictable.
    private static readonly DateTimeOffset Now = new(2026, 11, 11, 12, 0, 0, TimeSpan.Zero);

    private sealed class Facts(IReadOnlyList<string>? tags = null, MediaFacts? media = null, bool content = false) : ISearchFacts
    {
        public int TagReads { get; private set; }

        public int MediaReads { get; private set; }

        public ValueTask<IReadOnlyList<string>> GetTagsAsync(FileEntry entry, CancellationToken cancellationToken)
        {
            TagReads++;
            return ValueTask.FromResult(tags ?? []);
        }

        public ValueTask<MediaFacts?> GetMediaAsync(FileEntry entry, CancellationToken cancellationToken)
        {
            MediaReads++;
            return ValueTask.FromResult(media);
        }

        public ValueTask<bool> ContentContainsAsync(FileEntry entry, string text, CancellationToken cancellationToken) => ValueTask.FromResult(content);
    }

    private static FileEntry File(string path, long size = 0, DateTimeOffset? modified = null, DateTimeOffset? created = null, FileEntryKind kind = FileEntryKind.File)
    {
        var location = LocationPath.Parse(path);
        return new FileEntry(location.Name, location, kind, size, created ?? Now, modified ?? Now, FileAttributes.Normal);
    }

    private static QueryMatcher Matcher(string text) =>
        new(QueryParser.Parse(text), new ManualTimeProvider(Now), TimeZoneInfo.Utc);

    private static bool Matches(string query, FileEntry entry, ISearchFacts? facts = null) =>
        Matcher(query).MatchesAsync(entry, facts ?? NoSearchFacts.Instance, CancellationToken.None).AsTask().GetAwaiter().GetResult();

    [Fact]
    public void EmptyQuery_MatchesEverything()
    {
        Assert.True(Matcher("").MatchesEverything);
        Assert.True(Matches("", File(@"C:\a.txt")));
    }

    [Theory]
    [InlineData("holiday", true)]
    [InlineData("HOLIDAY", true)]
    [InlineData("holiday 2025", true)]
    [InlineData("holiday 2024", false)]
    [InlineData("\"holiday 2025\"", false)]
    [InlineData("-holiday", false)]
    [InlineData("-draft", true)]
    public void PlainWords_AreSubstringsOfTheName_AllRequired(string query, bool expected)
    {
        Assert.Equal(expected, Matches(query, File(@"C:\Videos\Summer holiday_2025.mp4")));
    }

    [Fact]
    public void QuotedPhrase_MatchesTheWholePhrase()
    {
        Assert.True(Matches("\"holiday_2025\"", File(@"C:\Videos\Summer holiday_2025.mp4")));
    }

    [Theory]
    [InlineData("IMG_*.jpg", @"C:\p\IMG_0001.jpg", true)]
    [InlineData("IMG_*.jpg", @"C:\p\my IMG_0001.jpg", false)]
    [InlineData("IMG_????.jpg", @"C:\p\IMG_0001.jpg", true)]
    [InlineData("IMG_????.jpg", @"C:\p\IMG_01.jpg", false)]
    [InlineData("*.JPG", @"C:\p\a.jpg", true)]
    [InlineData("a+b*", @"C:\p\a+b (1).txt", true)]
    public void Wildcards_DescribeTheWholeName(string query, string path, bool expected) =>
        Assert.Equal(expected, Matches(query, File(path)));

    [Fact]
    public void Regex_SearchesTheName_IgnoringCase()
    {
        Assert.True(Matches(@"regex:^img_\d{4}\.jpg$", File(@"C:\p\IMG_0042.jpg")));
        Assert.False(Matches(@"regex:^img_\d{4}\.jpg$", File(@"C:\p\IMG_42.jpg")));
    }

    [Fact]
    public void Extension_MatchesFilesNotFolders()
    {
        Assert.True(Matches("ext:mkv,mp4", File(@"C:\a\b.MKV")));
        Assert.False(Matches("ext:mkv", File(@"C:\a\b.mp4")));
        Assert.False(Matches("ext:mkv", File(@"C:\a\b.mkv", kind: FileEntryKind.Folder)));
    }

    [Theory]
    [InlineData("size:>2GB", 3L << 30, true)]
    [InlineData("size:>2GB", 2L << 30, false)]
    [InlineData("size:>=2GB", 2L << 30, true)]
    [InlineData("size:1MB..2MB", 1L << 20, true)]
    [InlineData("size:1MB..2MB", (2L << 20) + 1, false)]
    [InlineData("-size:>1KB", 10, true)]
    public void Size(string query, long bytes, bool expected) => Assert.Equal(expected, Matches(query, File(@"C:\a.bin", bytes)));

    [Fact]
    public void Size_NeverMatchesAFolder()
    {
        Assert.False(Matches("size:<1MB", File(@"C:\a", kind: FileEntryKind.Folder)));
    }

    [Theory]
    [InlineData("modified:today", "2026-11-11", true)]
    [InlineData("modified:today", "2026-11-10", false)]
    [InlineData("modified:yesterday", "2026-11-10", true)]
    [InlineData("modified:thisweek", "2026-11-09", true)]
    [InlineData("modified:lastweek", "2026-11-05", true)]
    [InlineData("modified:lastweek", "2026-11-09", false)]
    [InlineData("modified:2026", "2026-01-01", true)]
    [InlineData("modified:2025", "2026-01-01", false)]
    [InlineData("modified:>2025", "2026-01-01", true)]
    [InlineData("modified:>2026", "2026-12-31", false)]
    [InlineData("modified:>=2026-11", "2026-11-01", true)]
    [InlineData("modified:<2026-11", "2026-10-31", true)]
    [InlineData("modified:<=2026-11", "2026-11-30", true)]
    [InlineData("modified:2026-03..2026-05", "2026-05-31", true)]
    [InlineData("modified:2026-03..2026-05", "2026-06-01", false)]
    [InlineData("modified:2026-03..2026-05", "2026-02-28", false)]
    public void ModifiedDates(string query, string date, bool expected)
    {
        var modified = DateTimeOffset.Parse(date + "T08:30:00Z", System.Globalization.CultureInfo.InvariantCulture);

        Assert.Equal(expected, Matches(query, File(@"C:\a.txt", modified: modified)));
    }

    [Fact]
    public void Created_UsesTheCreatedTime_NotTheModifiedTime()
    {
        var old = new DateTimeOffset(2020, 5, 1, 0, 0, 0, TimeSpan.Zero);

        Assert.True(Matches("created:2020", File(@"C:\a.txt", created: old)));
        Assert.False(Matches("modified:2020", File(@"C:\a.txt", created: old)));
    }

    [Fact]
    public void Dates_AreJudgedInTheLocalZone()
    {
        // 23:30 UTC on the 10th is already the 11th at UTC+2.
        var late = new DateTimeOffset(2026, 11, 10, 23, 30, 0, TimeSpan.Zero);
        var plusTwo = TimeZoneInfo.CreateCustomTimeZone("plus2", TimeSpan.FromHours(2), "plus2", "plus2");
        var matcher = new QueryMatcher(QueryParser.Parse("modified:2026-11-11"), new ManualTimeProvider(Now), plusTwo);

        Assert.True(matcher.MatchesCheap(File(@"C:\a.txt", modified: late)));
    }

    [Fact]
    public void Dates_FollowTheClock_SoASavedSearchStaysCurrent()
    {
        var clock = new ManualTimeProvider(Now);
        var matcher = new QueryMatcher(QueryParser.Parse("modified:today"), clock, TimeZoneInfo.Utc);
        var file = File(@"C:\a.txt", modified: Now);

        Assert.True(matcher.MatchesCheap(file));
        clock.Advance(TimeSpan.FromDays(1));
        Assert.False(matcher.MatchesCheap(file));
    }

    [Theory]
    [InlineData("type:video", @"C:\a\x.mkv", true)]
    [InlineData("type:video", @"C:\a\x.mp3", false)]
    [InlineData("type:audio", @"C:\a\x.FLAC", true)]
    [InlineData("type:image", @"C:\a\x.jpg", true)]
    [InlineData("type:document", @"C:\a\x.pdf", true)]
    [InlineData("type:archive", @"C:\a\x.zip", true)]
    [InlineData("type:code", @"C:\a\x.cs", true)]
    [InlineData("type:file", @"C:\a\x", true)]
    [InlineData("-type:video", @"C:\a\x.mkv", false)]
    public void Type(string query, string path, bool expected) => Assert.Equal(expected, Matches(query, File(path)));

    [Fact]
    public void TypeFolder_MatchesFoldersOnly()
    {
        Assert.True(Matches("type:folder", File(@"C:\a\Photos", kind: FileEntryKind.Folder)));
        Assert.False(Matches("type:folder", File(@"C:\a\Photos.zip")));
        Assert.False(Matches("type:file", File(@"C:\a\Photos", kind: FileEntryKind.Folder)));
    }

    [Fact]
    public void Path_MatchesAnywhereInTheFullPath()
    {
        Assert.True(Matches(@"path:\\nas\inbox", File(@"\\nas\inbox\clips\a.mkv")));
        Assert.True(Matches(@"path:inbox/clips", File(@"\\nas\inbox\clips\a.mkv")));
        Assert.False(Matches(@"path:\\nas\media", File(@"\\nas\inbox\clips\a.mkv")));
    }

    [Fact]
    public void TheExampleFromTheSpec_UntriagedVideosOnTheNas()
    {
        const string Saved = @"type:video -tag:green path:\\nas\inbox";
        var clip = File(@"\\nas\inbox\a.mkv");

        Assert.True(Matches(Saved, clip, new Facts(tags: ["Red"])));
        Assert.False(Matches(Saved, clip, new Facts(tags: ["green"])));
        Assert.False(Matches(Saved, File(@"\\nas\inbox\a.txt"), new Facts()));
        Assert.False(Matches(Saved, File(@"\\nas\other\a.mkv"), new Facts()));
    }

    [Fact]
    public void Tags_AreReadOnlyForFilesThatPassedTheCheapConditions()
    {
        var facts = new Facts(tags: ["red"]);
        var matcher = Matcher("type:video tag:red");

        Assert.False(matcher.MatchesAsync(File(@"C:\a\x.txt"), facts, CancellationToken.None).AsTask().GetAwaiter().GetResult());
        Assert.Equal(0, facts.TagReads);
        Assert.True(matcher.MatchesAsync(File(@"C:\a\x.mkv"), facts, CancellationToken.None).AsTask().GetAwaiter().GetResult());
        Assert.Equal(1, facts.TagReads);
    }

    [Fact]
    public void Media_IsReadOnceForSeveralMediaFilters()
    {
        var facts = new Facts(media: new MediaFacts(TimeSpan.FromMinutes(45), 2160, ["hevc", "aac"]));

        Assert.True(Matches("duration:>30m res:>=2160 codec:hevc", File(@"C:\a\x.mkv"), facts));
        Assert.Equal(1, facts.MediaReads);
    }

    [Theory]
    [InlineData("duration:>1h")]
    [InlineData("res:<1080")]
    [InlineData("codec:av1")]
    public void Media_FiltersFail_WhenTheValueDoesNotMatch(string query) =>
        Assert.False(Matches(query, File(@"C:\a\x.mkv"), new Facts(media: new MediaFacts(TimeSpan.FromMinutes(45), 2160, ["hevc"]))));

    [Fact]
    public void Media_FiltersFail_WhenTheFileHasNotBeenProbed_AndNegationFlipsThat()
    {
        Assert.False(Matches("duration:>1s", File(@"C:\a\x.mkv"), new Facts()));
        Assert.True(Matches("-codec:hevc", File(@"C:\a\x.mkv"), new Facts()));
    }

    [Fact]
    public void Content_IsAskedOfTheFacts_ForFilesOnly()
    {
        Assert.True(Matches("content:invoice", File(@"C:\a\x.txt"), new Facts(content: true)));
        Assert.False(Matches("content:invoice", File(@"C:\a\x", kind: FileEntryKind.Folder), new Facts(content: true)));
        Assert.False(Matches("content:invoice", File(@"C:\a\x.txt"), NoSearchFacts.Instance));
    }

    [Fact]
    public void NeedsFacts_IsTrueOnlyForCostlyFilters()
    {
        Assert.False(Matcher("type:video size:>1GB").NeedsFacts);
        Assert.True(Matcher("tag:red").NeedsFacts);
        Assert.True(Matcher("codec:hevc").NeedsFacts);
    }

    [Fact]
    public async Task Cancellation_StopsTheCostlyPart()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
            await Matcher("tag:red").MatchesAsync(File(@"C:\a.txt"), new Facts(["red"]), cts.Token));
    }

    [Fact]
    public void ErrorsInTheQuery_AreIgnored_AndTheRestStillMatches()
    {
        Assert.True(Matches("holiday size:bogus", File(@"C:\holiday.mp4")));
    }
}
