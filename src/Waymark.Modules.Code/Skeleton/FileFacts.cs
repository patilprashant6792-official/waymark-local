using System.Globalization;

namespace Waymark.Modules.Code.Skeleton;

/// <summary>Size and line facts shown next to every file in the overview tools.</summary>
internal static class FileFacts
{
    private const int ReadBufferBytes = 64 * 1024;
    private static readonly string[] SizeUnits = ["B", "KB", "MB", "GB"];

    public static string FormatSize(long bytes)
    {
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < SizeUnits.Length - 1)
        {
            unit++;
            value /= 1024;
        }

        return string.Create(CultureInfo.InvariantCulture, $"{value:0.#}{SizeUnits[unit]}");
    }

    public static string FormatUtc(DateTimeOffset value) =>
        value.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

    /// <summary>The size mark: warning sign above the large-file limit, check mark otherwise.</summary>
    public static string SizeMark(long size, SkeletonOptions options) =>
        size > options.LargeFileBytes ? "⚠️" : "✓";

    /// <summary>"(1.2KB, 34 lines) ✓"; the line count is left out when it is not known.</summary>
    public static string Describe(long size, int? lines, SkeletonOptions options)
    {
        var lineText = lines is null ? string.Empty : string.Create(CultureInfo.InvariantCulture, $", {lines} lines");
        return $"({FormatSize(size)}{lineText}) {SizeMark(size, options)}";
    }

    /// <summary>
    /// Counts lines without blocking editors. Null when the file is larger than <paramref name="maxBytes"/> or cannot be read.
    /// </summary>
    public static async Task<int?> CountLinesAsync(string path, long size, int maxBytes, CancellationToken ct)
    {
        if (size > maxBytes)
        {
            return null;
        }

        if (size == 0)
        {
            return 0;
        }

        try
        {
            var streamOptions = new FileStreamOptions
            {
                Mode = FileMode.Open,
                Access = FileAccess.Read,
                Share = FileShare.ReadWrite | FileShare.Delete,
                Options = FileOptions.Asynchronous | FileOptions.SequentialScan,
                BufferSize = 0,
            };
            await using var stream = new FileStream(path, streamOptions);
            var buffer = new byte[ReadBufferBytes];
            var lines = 0;
            byte last = 0;
            int read;
            while ((read = await stream.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                lines += buffer.AsSpan(0, read).Count((byte)'\n');
                last = buffer[read - 1];
            }

            return last == (byte)'\n' ? lines : lines + 1;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
