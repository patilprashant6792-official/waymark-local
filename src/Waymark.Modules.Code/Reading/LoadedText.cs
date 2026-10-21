using System.Security.Cryptography;
using Microsoft.CodeAnalysis.Text;

namespace Waymark.Modules.Code.Reading;

/// <summary>One file read as text. Lines are split exactly as the indexer splits them, so line numbers agree with the index.</summary>
internal sealed class LoadedText
{
    private LoadedText(SourceText text, string hash, int lineCount)
    {
        Text = text;
        Hash = hash;
        LineCount = lineCount;
    }

    public SourceText Text { get; }

    /// <summary>SHA-256 of the bytes read, lower-case hex; equals the hash the indexer stores.</summary>
    public string Hash { get; }

    public int LineCount { get; }

    /// <summary>The line without its line break; <paramref name="number"/> is 1-based and must be at most <see cref="LineCount"/>.</summary>
    public string Line(int number) => Text.Lines[number - 1].ToString();

    /// <exception cref="InvalidDataException">The file is bigger than <paramref name="maxBytes"/> or looks binary.</exception>
    /// <exception cref="IOException">The file cannot be read.</exception>
    public static async Task<LoadedText> LoadAsync(string absolutePath, int maxBytes, CancellationToken ct)
    {
        byte[] content;
        await using (var stream = new FileStream(
            absolutePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 4096,
            FileOptions.Asynchronous | FileOptions.SequentialScan))
        {
            if (stream.Length > maxBytes)
            {
                throw new InvalidDataException($"The file is larger than {maxBytes} bytes. Read a range of it with a smaller request or use code_outline.");
            }

            content = new byte[(int)stream.Length];
            await stream.ReadExactlyAsync(content, ct).ConfigureAwait(false);
        }

        var hash = Convert.ToHexStringLower(SHA256.HashData(content));
        SourceText text;
        try
        {
            text = SourceText.From(content, content.Length, encoding: null, checksumAlgorithm: SourceHashAlgorithm.Sha256, throwIfBinaryDetected: true);
        }
        catch (InvalidDataException)
        {
            throw new InvalidDataException("The file looks binary and cannot be shown as text.");
        }

        return new LoadedText(text, hash, Indexing.SourceLines.Count(text));
    }
}
