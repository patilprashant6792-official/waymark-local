using System.Text;

namespace Waymark.Modules.Files;

/// <summary>How a text file is stored: encoding with or without a byte order mark, and its line ending.</summary>
internal sealed record TextFileFormat(Encoding Encoding, string? Newline)
{
    private const int SampleBytes = 64 * 1024;
    private const string Lf = "\n";
    private const string Crlf = "\r\n";

    /// <summary>New files are UTF-8 without a byte order mark; the content keeps the line endings it came with.</summary>
    public static TextFileFormat ForNewFile { get; } = new(new UTF8Encoding(false), null);

    /// <summary>Reads the start of an existing file to learn its encoding and the line ending most of it uses.</summary>
    /// <exception cref="NotSupportedException">The file is UTF-32, which the tools cannot write back safely.</exception>
    public static TextFileFormat Detect(string absolutePath)
    {
        var buffer = new byte[SampleBytes];
        int read;
        using (var stream = new FileStream(absolutePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            read = stream.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);
        }

        return Analyze(buffer.AsSpan(0, read));
    }

    /// <summary>Same as <see cref="Detect(string)"/> for a file that is already in memory.</summary>
    /// <exception cref="NotSupportedException">The file is UTF-32, which the tools cannot write back safely.</exception>
    public static TextFileFormat Detect(ReadOnlySpan<byte> content) => Analyze(content[..Math.Min(content.Length, SampleBytes)]);

    private static TextFileFormat Analyze(ReadOnlySpan<byte> head)
    {
        var (encoding, preambleLength) = DetectEncoding(head);
        var sample = encoding.GetString(head[preambleLength..]);
        var crlf = CountOccurrences(sample, Crlf);
        var lf = CountOccurrences(sample, Lf) - crlf;
        var newline = crlf == 0 && lf == 0 ? null : crlf > lf ? Crlf : Lf;
        return new TextFileFormat(encoding, newline);
    }

    /// <summary>The bytes to store: byte order mark (if the encoding has one) followed by the text in this format's line ending.</summary>
    public byte[] Encode(string content)
    {
        var text = Newline is null ? content : content.Replace(Crlf, Lf, StringComparison.Ordinal).Replace(Lf, Newline, StringComparison.Ordinal);
        var preamble = Encoding.GetPreamble();
        var body = Encoding.GetBytes(text);
        var bytes = new byte[preamble.Length + body.Length];
        preamble.CopyTo(bytes, 0);
        body.CopyTo(bytes, preamble.Length);
        return bytes;
    }

    /// <summary>The text of a whole file, without its byte order mark.</summary>
    /// <exception cref="DecoderFallbackException">The bytes are not valid text in this encoding (a binary file); rewriting it would corrupt it.</exception>
    public string DecodeStrict(ReadOnlySpan<byte> bytes)
    {
        var strict = (Encoding)Encoding.Clone();
        strict.DecoderFallback = DecoderFallback.ExceptionFallback;
        var preamble = strict.GetPreamble();
        return strict.GetString(bytes.StartsWith(preamble) ? bytes[preamble.Length..] : bytes);
    }

    private static (Encoding Encoding, int PreambleLength) DetectEncoding(ReadOnlySpan<byte> head)
    {
        if (head.StartsWith(new byte[] { 0xFF, 0xFE, 0x00, 0x00 }) || head.StartsWith(new byte[] { 0x00, 0x00, 0xFE, 0xFF }))
        {
            throw new NotSupportedException("The file is UTF-32; only UTF-8 and UTF-16 files can be rewritten.");
        }

        if (head.StartsWith(new byte[] { 0xEF, 0xBB, 0xBF }))
        {
            return (new UTF8Encoding(true), 3);
        }

        if (head.StartsWith(new byte[] { 0xFF, 0xFE }))
        {
            return (new UnicodeEncoding(bigEndian: false, byteOrderMark: true), 2);
        }

        if (head.StartsWith(new byte[] { 0xFE, 0xFF }))
        {
            return (new UnicodeEncoding(bigEndian: true, byteOrderMark: true), 2);
        }

        return (new UTF8Encoding(false), 0);
    }

    private static int CountOccurrences(string text, string value)
    {
        var count = 0;
        var index = text.IndexOf(value, StringComparison.Ordinal);
        while (index >= 0)
        {
            count++;
            index = text.IndexOf(value, index + value.Length, StringComparison.Ordinal);
        }

        return count;
    }
}
