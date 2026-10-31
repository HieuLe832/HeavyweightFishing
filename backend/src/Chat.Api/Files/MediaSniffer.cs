using System.Text;

namespace Chat.Api.Files;

/// <param name="Width">Only for images.</param>
public record SniffResult(string MimeType, int? Width, int? Height)
{
    public bool IsImage => MimeType.StartsWith("image/", StringComparison.Ordinal);
}

/// <summary>
/// Decides what a file is from its bytes, never from what the client claims. Looks at the signature and header only; it
/// does not decode the image, so downloads are served with nosniff and a sandboxing CSP. SVG and HTML are not allowed.
/// </summary>
public static class MediaSniffer
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static SniffResult? Detect(ReadOnlySpan<byte> b)
    {
        if (b.Length == 0) return null;
        return Png(b) ?? Jpeg(b) ?? Gif(b) ?? WebP(b) ?? Pdf(b) ?? PlainText(b);
    }

    private static int BigEndian16(ReadOnlySpan<byte> b, int at) => (b[at] << 8) | b[at + 1];
    private static int LittleEndian16(ReadOnlySpan<byte> b, int at) => b[at] | (b[at + 1] << 8);

    private static SniffResult? Image(string mime, int width, int height) =>
        width > 0 && height > 0 ? new SniffResult(mime, width, height) : null;

    private static SniffResult? Png(ReadOnlySpan<byte> b)
    {
        ReadOnlySpan<byte> signature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
        if (b.Length < 24 || !b[..8].SequenceEqual(signature) || !b.Slice(12, 4).SequenceEqual("IHDR"u8)) return null;
        var width = (b[16] << 24) | (b[17] << 16) | (b[18] << 8) | b[19];
        var height = (b[20] << 24) | (b[21] << 16) | (b[22] << 8) | b[23];
        return Image("image/png", width, height);
    }

    private static SniffResult? Gif(ReadOnlySpan<byte> b)
    {
        if (b.Length < 10 || !(b[..6].SequenceEqual("GIF87a"u8) || b[..6].SequenceEqual("GIF89a"u8))) return null;
        return Image("image/gif", LittleEndian16(b, 6), LittleEndian16(b, 8));
    }

    private static SniffResult? Jpeg(ReadOnlySpan<byte> b)
    {
        if (b.Length < 4 || b[0] != 0xFF || b[1] != 0xD8 || b[2] != 0xFF) return null;
        var i = 2;
        while (i + 4 <= b.Length)
        {
            if (b[i] != 0xFF) return null;
            var marker = b[i + 1];
            if (marker == 0xFF) { i++; continue; } // fill byte
            // Start-of-frame markers carry the size; C4, C8 and CC share the range but are not frames.
            if (marker is >= 0xC0 and <= 0xCF and not (0xC4 or 0xC8 or 0xCC))
                return i + 9 <= b.Length ? Image("image/jpeg", BigEndian16(b, i + 7), BigEndian16(b, i + 5)) : null;
            if (marker is 0xD9 or 0xDA) return null; // end or scan before any frame header
            i += 2 + BigEndian16(b, i + 2);
        }
        return null;
    }

    private static SniffResult? WebP(ReadOnlySpan<byte> b)
    {
        if (b.Length < 20 || !b[..4].SequenceEqual("RIFF"u8) || !b.Slice(8, 4).SequenceEqual("WEBP"u8)) return null;
        var chunk = b.Slice(12, 4);
        if (chunk.SequenceEqual("VP8 "u8) && b.Length >= 30 && b[23] == 0x9D && b[24] == 0x01 && b[25] == 0x2A)
            return Image("image/webp", LittleEndian16(b, 26) & 0x3FFF, LittleEndian16(b, 28) & 0x3FFF);
        if (chunk.SequenceEqual("VP8L"u8) && b.Length >= 25 && b[20] == 0x2F)
            return Image("image/webp", 1 + (b[21] | ((b[22] & 0x3F) << 8)), 1 + ((b[22] >> 6) | (b[23] << 2) | ((b[24] & 0x0F) << 10)));
        if (chunk.SequenceEqual("VP8X"u8) && b.Length >= 30)
            return Image("image/webp", 1 + (b[24] | (b[25] << 8) | (b[26] << 16)), 1 + (b[27] | (b[28] << 8) | (b[29] << 16)));
        return null;
    }

    private static SniffResult? Pdf(ReadOnlySpan<byte> b) =>
        b.Length >= 8 && b[..5].SequenceEqual("%PDF-"u8) ? new SniffResult("application/pdf", null, null) : null;

    /// <summary>Valid UTF-8 with no control characters other than tab and line breaks, so no binary passes as text.</summary>
    private static SniffResult? PlainText(ReadOnlySpan<byte> b)
    {
        string text;
        try
        {
            text = StrictUtf8.GetString(b);
        }
        catch (DecoderFallbackException)
        {
            return null;
        }
        foreach (var c in text)
            if (c < 0x20 && c is not ('\t' or '\n' or '\r') || c == 0x7F) return null;
        return new SniffResult("text/plain", null, null);
    }
}
