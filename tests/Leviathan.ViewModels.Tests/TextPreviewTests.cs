using System.Text;
using Leviathan.ViewModels.Preview;

namespace Leviathan.ViewModels.Tests;

public sealed class TextDecoderTests
{
    [Fact]
    public void Decode_OfNothing_IsEmptyText() => Assert.Equal(string.Empty, TextDecoder.Decode([], false).Text);

    [Fact]
    public void Decode_OfPlainUtf8_IsUtf8()
    {
        var r = TextDecoder.Decode(Encoding.UTF8.GetBytes("héllo ✓"), false);

        Assert.Equal("héllo ✓", r.Text);
        Assert.Equal("UTF-8", r.EncodingName);
    }

    [Fact]
    public void Decode_WithAUtf8Bom_DropsTheBom()
    {
        byte[] bytes = [0xEF, 0xBB, 0xBF, .. "abc"u8];

        var r = TextDecoder.Decode(bytes, false);

        Assert.Equal("abc", r.Text);
        Assert.Equal("UTF-8 with BOM", r.EncodingName);
    }

    [Fact]
    public void Decode_OfUtf16WithABom_ReadsBothByteOrders()
    {
        byte[] le = [0xFF, 0xFE, .. Encoding.Unicode.GetBytes("hi")];
        byte[] be = [0xFE, 0xFF, .. Encoding.BigEndianUnicode.GetBytes("hi")];

        Assert.Equal("hi", TextDecoder.Decode(le, false).Text);
        Assert.Equal("hi", TextDecoder.Decode(be, false).Text);
    }

    [Fact]
    public void Decode_OfUtf16WithoutABom_IsRecognisedByItsZeros()
    {
        var r = TextDecoder.Decode(Encoding.Unicode.GetBytes("plain ascii text in utf sixteen"), false);

        Assert.Equal("plain ascii text in utf sixteen", r.Text);
        Assert.Equal("UTF-16 LE", r.EncodingName);
    }

    [Fact]
    public void Decode_OfAnOldWindowsFile_FallsBackToWindows1252()
    {
        byte[] bytes = [0x63, 0x61, 0x66, 0xE9, 0x20, 0x80]; // "café €" in Windows-1252, not valid UTF-8

        var r = TextDecoder.Decode(bytes, false);

        Assert.Equal("café €", r.Text);
        Assert.Equal("Windows-1252", r.EncodingName);
    }

    [Fact]
    public void Decode_OfABinaryFile_SaysSo()
    {
        var r = TextDecoder.Decode([0x4D, 0x5A, 0x90, 0x00, 0x03, 0x00, 0x01, 0x02, 0x00, 0xFF], false);

        Assert.True(r.IsBinary);
        Assert.Equal(string.Empty, r.Text);
    }

    [Fact]
    public void Decode_OfTruncatedUtf8_DoesNotMistakeTheCutForAnotherEncoding()
    {
        var full = Encoding.UTF8.GetBytes("abc✓"); // ✓ is three bytes
        var cut = full[..^1];

        var r = TextDecoder.Decode(cut, truncated: true);

        Assert.Equal("UTF-8", r.EncodingName);
        Assert.Equal("abc", r.Text);
    }

    [Fact]
    public void Decode_OfTheSameCutBytesInAWholeFile_IsNotUtf8()
    {
        var cut = Encoding.UTF8.GetBytes("abc✓")[..^1];

        Assert.Equal("Windows-1252", TextDecoder.Decode(cut, truncated: false).EncodingName);
    }
}

public sealed class SyntaxHighlighterTests
{
    private static string Kinds(string line, SyntaxLanguage language)
    {
        var tokens = SyntaxHighlighter.Highlight([line], language)[0];
        return string.Join(' ', tokens.Select(t => $"{t.Kind}:{line.Substring(t.Start, t.Length)}"));
    }

    [Theory]
    [InlineData(".cs", SyntaxLanguage.CLike)]
    [InlineData(".CS", SyntaxLanguage.CLike)]
    [InlineData(".py", SyntaxLanguage.Hash)]
    [InlineData(".ps1", SyntaxLanguage.Hash)]
    [InlineData(".sql", SyntaxLanguage.Sql)]
    [InlineData(".xaml", SyntaxLanguage.Xml)]
    [InlineData(".json", SyntaxLanguage.Json)]
    [InlineData(".txt", SyntaxLanguage.None)]
    public void LanguageFor_GoesByExtension(string extension, SyntaxLanguage expected) =>
        Assert.Equal(expected, SyntaxHighlighter.LanguageFor(extension));

    [Fact]
    public void CLike_FindsKeywordsStringsNumbersAndLineComments() =>
        Assert.Equal("Keyword:public Keyword:int Number:42 String:\"a//b\" Comment:// done",
            Kinds("public int x = 42 + \"a//b\"; // done", SyntaxLanguage.CLike));

    [Fact]
    public void CLike_AStringKeepsAnEscapedQuote() =>
        Assert.Equal("String:\"a\\\"b\" Keyword:return", Kinds("\"a\\\"b\" return", SyntaxLanguage.CLike));

    [Fact]
    public void CLike_ANumberInsideAnIdentifierIsNotANumber() =>
        Assert.Equal(string.Empty, Kinds("abc123 x2", SyntaxLanguage.CLike));

    [Fact]
    public void CLike_ABlockCommentCarriesOverLines()
    {
        var t = SyntaxHighlighter.Highlight(["a /* start", "middle if", "end */ return"], SyntaxLanguage.CLike);

        Assert.Equal(TokenKind.Comment, t[0][0].Kind);
        Assert.Equal(TokenKind.Comment, t[1][0].Kind);
        Assert.Equal(9, t[1][0].Length);
        Assert.Equal(TokenKind.Comment, t[2][0].Kind);
        Assert.Equal(TokenKind.Keyword, t[2][1].Kind);
    }

    [Fact]
    public void AnUnclosedString_EndsWithTheLine()
    {
        var t = SyntaxHighlighter.Highlight(["x = \"open", "if"], SyntaxLanguage.CLike);

        Assert.Equal(TokenKind.String, t[0][0].Kind);
        Assert.Equal(TokenKind.Keyword, t[1][0].Kind);
    }

    [Fact]
    public void Hash_HashStartsAComment() =>
        Assert.Equal("Keyword:def Comment:# note", Kinds("def # note", SyntaxLanguage.Hash));

    [Fact]
    public void Sql_KeywordsIgnoreCase() =>
        Assert.Equal("Keyword:SELECT Keyword:from Comment:-- c", Kinds("SELECT from -- c", SyntaxLanguage.Sql));

    [Fact]
    public void Xml_ColoursTagsAttributeValuesAndComments()
    {
        Assert.Equal("Keyword:<Project String:\"Sdk\"", Kinds("<Project a=\"Sdk\">text \"not a string\"", SyntaxLanguage.Xml));
        Assert.Equal("Comment:<!-- hi -->", Kinds("<!-- hi -->", SyntaxLanguage.Xml));
    }

    [Fact]
    public void Json_ColoursKeysValuesAndLiterals() =>
        Assert.Equal("String:\"a\" Number:1 String:\"b\" Keyword:true", Kinds("{\"a\": 1, \"b\": true}", SyntaxLanguage.Json));

    [Fact]
    public void None_GivesNoTokens() => Assert.Empty(SyntaxHighlighter.Highlight(["if (x) { return 1; }"], SyntaxLanguage.None)[0]);

    [Fact]
    public void Highlight_GivesOneEntryPerLine_EvenForOddInput()
    {
        var t = SyntaxHighlighter.Highlight(["", "\t\t", "\"", "/*", "<", "'\\"], SyntaxLanguage.CLike);

        Assert.Equal(6, t.Length);
    }
}

public sealed class WebPreviewHtmlTests
{
    [Fact]
    public void FromMarkdown_RendersAndCarriesThePolicy()
    {
        var html = WebPreviewHtml.FromMarkdown("# Title\n\n* one\n* two\n");

        Assert.Contains("<h1", html);
        Assert.Contains("<li>one</li>", html);
        Assert.Contains(WebPreviewHtml.Policy, html);
    }

    [Fact]
    public void FromMarkdown_ShowsRawHtmlAsTextInsteadOfRunningIt()
    {
        var html = WebPreviewHtml.FromMarkdown("hi <script>alert(1)</script>");

        Assert.DoesNotContain("<script>", html);
    }

    [Fact]
    public void FromHtml_PutsThePolicyFirstInTheHead()
    {
        var html = WebPreviewHtml.FromHtml("<!doctype html><html><head><title>t</title></head><body><script>x()</script></body></html>");

        Assert.True(html.IndexOf(WebPreviewHtml.Policy, StringComparison.Ordinal) < html.IndexOf("<title>", StringComparison.Ordinal));
        Assert.StartsWith("<!doctype html>", html);
    }

    [Fact]
    public void FromHtml_WithoutAHead_AddsOne()
    {
        var html = WebPreviewHtml.FromHtml("<html><body>x</body></html>");

        Assert.Contains("<head><meta http-equiv", html);
    }

    [Fact]
    public void FromHtml_OfAFragment_IsWrappedInAPage()
    {
        var html = WebPreviewHtml.FromHtml("<p>hello</p>");

        Assert.StartsWith("<!doctype html>", html);
        Assert.Contains("<p>hello</p>", html);
        Assert.Contains(WebPreviewHtml.Policy, html);
    }
}
