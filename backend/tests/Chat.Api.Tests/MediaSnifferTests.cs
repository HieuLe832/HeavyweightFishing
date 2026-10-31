using Chat.Api.Files;

namespace Chat.Api.Tests;

public class MediaSnifferTests
{
    [Fact]
    public void Images_report_their_type_and_size()
    {
        Assert.Equal(new SniffResult("image/png", 640, 480), MediaSniffer.Detect(TestFiles.Png(640, 480)));
        Assert.Equal(new SniffResult("image/jpeg", 1024, 768), MediaSniffer.Detect(TestFiles.Jpeg(1024, 768)));
        Assert.Equal(new SniffResult("image/gif", 300, 200), MediaSniffer.Detect(TestFiles.Gif(300, 200)));
        Assert.Equal(new SniffResult("image/webp", 800, 600), MediaSniffer.Detect(TestFiles.WebPExtended(800, 600)));
        Assert.Equal(new SniffResult("image/webp", 320, 240), MediaSniffer.Detect(TestFiles.WebPLossy(320, 240)));
        Assert.Equal(new SniffResult("image/webp", 1000, 700), MediaSniffer.Detect(TestFiles.WebPLossless(1000, 700)));
    }

    [Fact]
    public void Pdf_and_plain_text_are_allowed_without_a_size()
    {
        Assert.Equal(new SniffResult("application/pdf", null, null), MediaSniffer.Detect(TestFiles.Pdf()));
        Assert.Equal(new SniffResult("text/plain", null, null), MediaSniffer.Detect(TestFiles.Text("héllo\twörld\r\n日本語")));
    }

    public static TheoryData<string, byte[]> Refused => new()
    {
        { "empty", [] },
        { "windows executable", [0x4D, 0x5A, 0x90, 0x00, 0x03, 0x00, 0x00, 0x00, 0x04] },
        { "binary that is not utf-8", [0xFF, 0xFE, 0xFD, 0xFC, 0x80, 0x81] },
        { "text with a NUL", [0x68, 0x69, 0x00, 0x21] },
        { "png signature without a header", [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0] },
        { "png with zero width", TestFiles.Png(0, 5) },
        { "jpeg that ends before any frame", [0xFF, 0xD8, 0xFF, 0xD9, 0, 0, 0, 0] },
    };

    [Theory]
    [MemberData(nameof(Refused))]
    public void Everything_else_is_refused(string name, byte[] bytes) => Assert.Null(MediaSniffer.Detect(bytes));
}

public class MediaSnifferMarkupTests
{
    [Theory]
    [InlineData("<svg xmlns=\"http://www.w3.org/2000/svg\"><script>alert(1)</script></svg>")]
    [InlineData("<!doctype html><script>alert(1)</script>")]
    [InlineData("GIF89a")] // a gif signature with no header is only text
    public void Markup_and_broken_signatures_are_plain_text_never_their_claimed_type(string content)
    {
        Assert.Equal(new SniffResult("text/plain", null, null), MediaSniffer.Detect(System.Text.Encoding.UTF8.GetBytes(content)));
    }
}
