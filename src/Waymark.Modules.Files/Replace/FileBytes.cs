using System.Security.Cryptography;

namespace Waymark.Modules.Files.Replace;

internal static class FileBytes
{
    /// <summary>Reads a whole file while editors may still hold it open.</summary>
    /// <exception cref="IOException">The file cannot be read.</exception>
    public static async Task<byte[]> ReadAsync(string absolutePath, CancellationToken ct)
    {
        await using var stream = new FileStream(
            absolutePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 4096,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var bytes = new byte[stream.Length];
        await stream.ReadExactlyAsync(bytes, ct).ConfigureAwait(false);
        return bytes;
    }

    /// <summary>SHA-256 of the bytes, lower-case hex.</summary>
    public static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
}
