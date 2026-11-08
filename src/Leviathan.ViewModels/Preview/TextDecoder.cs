using System.Text;

namespace Leviathan.ViewModels.Preview;

/// <summary>The text of a file ready to show (spec §9.1: "first 2 MB, encoding detection").</summary>
/// <param name="Text">The decoded text; empty when <paramref name="IsBinary"/>.</param>
/// <param name="EncodingName">What the bytes were taken to be ("UTF-8", "UTF-16 LE", "Windows-1252"…).</param>
/// <param name="IsBinary">True when the bytes are not text (NUL bytes in something that is not UTF-16/32), so the pane shows no text.</param>
public sealed record DecodedText(string Text, string EncodingName, bool IsBinary);

/// <summary>
/// Turns the first bytes of a file into text without being told the encoding: a byte-order mark wins, then UTF-16 without a mark (many NULs
/// in a regular pattern), then UTF-8 if the bytes are valid UTF-8, else Windows-1252, which is what an old Windows text file nearly always
/// is. A file with NULs that fits none of those is binary. Pure and total: any bytes give a result, never an exception.
/// </summary>
public static class TextDecoder
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    /// <summary>
    /// Decodes <paramref name="bytes"/>. <paramref name="truncated"/> says the bytes are only the start of a longer file, so a
    /// multi-byte character cut off at the end is not taken as a sign the file is not UTF-8.
    /// </summary>
    public static DecodedText Decode(ReadOnlySpan<byte> bytes, bool truncated)
    {
        if (bytes.Length == 0)
        {
            return new DecodedText(string.Empty, "UTF-8", false);
        }

        if (bytes.StartsWith(new byte[] { 0xFF, 0xFE, 0x00, 0x00 }))
        {
            return new DecodedText(Encoding.UTF32.GetString(bytes[4..]), "UTF-32 LE", false);
        }

        if (bytes.StartsWith(new byte[] { 0x00, 0x00, 0xFE, 0xFF }))
        {
            return new DecodedText(new UTF32Encoding(true, false).GetString(bytes[4..]), "UTF-32 BE", false);
        }

        if (bytes.StartsWith(new byte[] { 0xEF, 0xBB, 0xBF }))
        {
            return new DecodedText(Encoding.UTF8.GetString(bytes[3..]), "UTF-8 with BOM", false);
        }

        if (bytes.StartsWith(new byte[] { 0xFF, 0xFE }))
        {
            return new DecodedText(Encoding.Unicode.GetString(Even(bytes[2..])), "UTF-16 LE", false);
        }

        if (bytes.StartsWith(new byte[] { 0xFE, 0xFF }))
        {
            return new DecodedText(Encoding.BigEndianUnicode.GetString(Even(bytes[2..])), "UTF-16 BE", false);
        }

        if (LooksLikeUtf16(bytes, out var bigEndian))
        {
            var text = bigEndian ? Encoding.BigEndianUnicode.GetString(Even(bytes)) : Encoding.Unicode.GetString(Even(bytes));
            return new DecodedText(text, bigEndian ? "UTF-16 BE" : "UTF-16 LE", false);
        }

        if (bytes[..Math.Min(bytes.Length, 8192)].Contains((byte)0))
        {
            return new DecodedText(string.Empty, "binary", true);
        }

        try
        {
            var valid = truncated ? TrimPartialCharacter(bytes) : bytes;
            return new DecodedText(StrictUtf8.GetString(valid), "UTF-8", false);
        }
        catch (DecoderFallbackException)
        {
            return new DecodedText(Windows1252().GetString(bytes), "Windows-1252", false);
        }
    }

    private static ReadOnlySpan<byte> Even(ReadOnlySpan<byte> bytes) => bytes[..(bytes.Length & ~1)];

    // ASCII-ish text in UTF-16 has a zero in every other byte; require it in most of the pairs and none in the other half.
    private static bool LooksLikeUtf16(ReadOnlySpan<byte> bytes, out bool bigEndian)
    {
        bigEndian = false;
        var n = Math.Min(bytes.Length, 4096) & ~1;
        if (n < 4)
        {
            return false;
        }

        int evenZeros = 0, oddZeros = 0;
        for (var i = 0; i < n; i += 2)
        {
            evenZeros += bytes[i] == 0 ? 1 : 0;
            oddZeros += bytes[i + 1] == 0 ? 1 : 0;
        }

        var pairs = n / 2;
        if (oddZeros * 10 >= pairs * 7 && evenZeros == 0)
        {
            return true;
        }

        if (evenZeros * 10 >= pairs * 7 && oddZeros == 0)
        {
            bigEndian = true;
            return true;
        }

        return false;
    }

    // Drops up to three bytes at the end that start a multi-byte UTF-8 character the cut removed the rest of.
    private static ReadOnlySpan<byte> TrimPartialCharacter(ReadOnlySpan<byte> bytes)
    {
        for (var back = 1; back <= Math.Min(3, bytes.Length); back++)
        {
            var b = bytes[^back];
            if ((b & 0xC0) == 0x80)
            {
                continue; // a continuation byte: keep looking for the lead
            }

            var needed = b >= 0xF0 ? 4 : b >= 0xE0 ? 3 : b >= 0xC0 ? 2 : 1;
            return needed > back ? bytes[..^back] : bytes;
        }

        return bytes;
    }

    private static Encoding Windows1252()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return Encoding.GetEncoding(1252);
    }
}
