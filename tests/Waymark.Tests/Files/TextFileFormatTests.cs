using System.Text;
using Waymark.Modules.Files;
using Waymark.Tests.Support;

namespace Waymark.Tests.Files;

public sealed class TextFileFormatTests
{
    private static readonly byte[] Utf8Bom = [0xEF, 0xBB, 0xBF];

    private static byte[] Concat(byte[] first, byte[] second) => [.. first, .. second];

    private static byte[] Utf8(string text) => new UTF8Encoding(false).GetBytes(text);

    [Fact]
    public void ANewFileIsUtf8WithoutAByteOrderMarkAndKeepsTheLineEndingsItCameWith()
    {
        var format = TextFileFormat.ForNewFile;

        Assert.Null(format.Newline);
        Assert.Empty(format.Encoding.GetPreamble());
        Assert.Equal(Utf8("a\r\nb\nc"), format.Encode("a\r\nb\nc"));
    }

    [Theory]
    [InlineData("a\nb\n", "\n")]
    [InlineData("a\r\nb\r\n", "\r\n")]
    [InlineData("a\r\nb\r\nc\n", "\r\n")]
    [InlineData("a\r\nb\nc\n", "\n")]
    [InlineData("a\r\nb\n", "\n")]
    public void TheLineEndingUsedByMostLinesWins(string text, string expected) =>
        Assert.Equal(expected, TextFileFormat.Detect(Utf8(text)).Newline);

    [Theory]
    [InlineData("")]
    [InlineData("no line break at all")]
    public void AFileWithoutLineBreaksHasNoLineEnding(string text) =>
        Assert.Null(TextFileFormat.Detect(Utf8(text)).Newline);

    [Fact]
    public void Utf8WithAByteOrderMarkIsRecognised()
    {
        var format = TextFileFormat.Detect(Concat(Utf8Bom, Utf8("a\r\nb")));

        Assert.Equal(Utf8Bom, format.Encoding.GetPreamble());
        Assert.Equal("\r\n", format.Newline);
    }

    [Fact]
    public void Utf8WithoutAByteOrderMarkIsTheDefault()
    {
        var format = TextFileFormat.Detect(Utf8("abc"));

        Assert.Empty(format.Encoding.GetPreamble());
        Assert.Equal(65001, format.Encoding.CodePage);
    }

    [Fact]
    public void Utf16LittleAndBigEndianAreRecognised()
    {
        var little = TextFileFormat.Detect(Concat([0xFF, 0xFE], Encoding.Unicode.GetBytes("a\nb\n")));
        var big = TextFileFormat.Detect(Concat([0xFE, 0xFF], Encoding.BigEndianUnicode.GetBytes("a\r\nb\r\n")));

        Assert.Equal([0xFF, 0xFE], little.Encoding.GetPreamble());
        Assert.Equal("\n", little.Newline);
        Assert.Equal([0xFE, 0xFF], big.Encoding.GetPreamble());
        Assert.Equal("\r\n", big.Newline);
    }

    [Theory]
    [InlineData(new byte[] { 0xFF, 0xFE, 0x00, 0x00, 0x61, 0x00, 0x00, 0x00 })]
    [InlineData(new byte[] { 0x00, 0x00, 0xFE, 0xFF, 0x00, 0x00, 0x00, 0x61 })]
    public void Utf32IsRefusedBecauseItCannotBeWrittenBackSafely(byte[] bytes)
    {
        var exception = Assert.Throws<NotSupportedException>(() => TextFileFormat.Detect(bytes));

        Assert.Contains("UTF-32", exception.Message);
    }

    [Fact]
    public void OnlyTheFirstSixtyFourKilobytesAreSampled()
    {
        var body = Concat(Enumerable.Repeat((byte)'a', 64 * 1024).ToArray(), Utf8("\r\n\r\n\r\n"));

        Assert.Null(TextFileFormat.Detect(body).Newline);
    }

    [Fact]
    public void DetectingFromAPathReadsTheFile()
    {
        using var dir = new TempDirectory();
        var path = dir.PathOf("crlf.txt");
        File.WriteAllBytes(path, Concat(Utf8Bom, Utf8("a\r\nb\r\n")));

        var format = TextFileFormat.Detect(path);

        Assert.Equal(Utf8Bom, format.Encoding.GetPreamble());
        Assert.Equal("\r\n", format.Newline);
    }

    [Fact]
    public void DetectingAMissingFileFails()
    {
        using var dir = new TempDirectory();

        Assert.Throws<FileNotFoundException>(() => TextFileFormat.Detect(dir.PathOf("missing.txt")));
    }

    [Fact]
    public void EncodeConvertsEveryLineBreakToTheFilesOwnEnding()
    {
        var crlf = new TextFileFormat(new UTF8Encoding(false), "\r\n");
        var lf = new TextFileFormat(new UTF8Encoding(false), "\n");

        Assert.Equal(Utf8("a\r\nb\r\nc"), crlf.Encode("a\nb\r\nc"));
        Assert.Equal(Utf8("a\nb\nc"), lf.Encode("a\nb\r\nc"));
    }

    [Fact]
    public void EncodeWritesTheByteOrderMarkOfItsEncoding()
    {
        var utf8 = new TextFileFormat(new UTF8Encoding(true), "\n");
        var utf16 = new TextFileFormat(new UnicodeEncoding(false, true), "\n");

        Assert.Equal(Concat(Utf8Bom, Utf8("x")), utf8.Encode("x"));
        Assert.Equal([0xFF, 0xFE, 0x78, 0x00], utf16.Encode("x"));
    }

    [Fact]
    public void EncodeExactChangesNoLineEnding()
    {
        var crlf = new TextFileFormat(new UTF8Encoding(true), "\r\n");

        Assert.Equal(Concat(Utf8Bom, Utf8("a\nb\r\nc")), crlf.EncodeExact("a\nb\r\nc"));
        Assert.Throws<ArgumentNullException>(() => crlf.EncodeExact(null!));
    }

    [Fact]
    public void DecodeStrictDropsTheByteOrderMarkWhenThereIsOne()
    {
        var format = new TextFileFormat(new UTF8Encoding(true), "\n");

        Assert.Equal("hi", format.DecodeStrict(Concat(Utf8Bom, Utf8("hi"))));
        Assert.Equal("hi", format.DecodeStrict(Utf8("hi")));
    }

    [Fact]
    public void DecodeStrictRefusesBytesThatAreNotText()
    {
        var format = new TextFileFormat(new UTF8Encoding(false), "\n");

        Assert.Throws<DecoderFallbackException>(() => format.DecodeStrict([0xC3, 0x28]));
    }

    [Fact]
    public void AFileSurvivesDetectDecodeAndEncode()
    {
        var original = Concat(Utf8Bom, Utf8("caf\u00e9\r\nna\u00efve\r\n"));

        var format = TextFileFormat.Detect(original);
        var text = format.DecodeStrict(original);

        Assert.Equal("caf\u00e9\r\nna\u00efve\r\n", text);
        Assert.Equal(original, format.Encode(text));
        Assert.Equal(original, format.EncodeExact(text));
    }
}
