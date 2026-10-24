using System.Globalization;

namespace Waymark.Modules.Code.Skeleton;

/// <summary>Size and line facts shown next to every file in the overview tools.</summary>
internal static class FileFacts
{
    /// <summary>Marks a folder that is not opened in the tree; code_skeleton with that path opens it.</summary>
    public const string ClosedMark = "▸";

    private const int ReadBufferBytes = 64 * 1024;
    private const int ExtensionsShown = 3;
    private const string Separator = " · ";
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
    /// One-line summary of a folder that is not opened, e.g. "(3 folders · 18 files · 214KB · 2⚠️ · .cs 16, .sql 2 · App.csproj) ▸".
    /// Counts cover only what tools may see; a '≥' marks totals of a subtree the scan did not finish.
    /// </summary>
    public static string DescribeFolder(FolderNode folder, SkeletonOptions options)
    {
        ArgumentNullException.ThrowIfNull(folder);
        ArgumentNullException.ThrowIfNull(options);
        if (folder.Unreadable)
        {
            return "(this folder cannot be read)";
        }

        if (!folder.Scanned)
        {
            return $"(not scanned: the scan limit was reached) {ClosedMark}";
        }

        if (folder.TotalFiles == 0 && folder.TotalFolders == 0)
        {
            return "(no visible files)";
        }

        var atLeast = folder.Incomplete ? "≥" : string.Empty;
        var parts = new List<string>(6);
        if (folder.TotalFolders > 0)
        {
            parts.Add(string.Create(CultureInfo.InvariantCulture, $"{atLeast}{folder.TotalFolders} {Plural(folder.TotalFolders, "folder")}"));
        }

        parts.Add(folder.TotalFiles == 0
            ? "no visible files"
            : string.Create(CultureInfo.InvariantCulture, $"{atLeast}{folder.TotalFiles} {Plural(folder.TotalFiles, "file")}"));
        if (folder.TotalBytes > 0)
        {
            parts.Add(FormatSize(folder.TotalBytes));
        }

        if (folder.TotalLargeFiles > 0)
        {
            parts.Add(string.Create(CultureInfo.InvariantCulture, $"{folder.TotalLargeFiles}⚠️"));
        }

        if (folder.Extensions.Count > 0)
        {
            parts.Add(string.Join(", ", folder.Extensions
                .OrderByDescending(entry => entry.Value)
                .ThenBy(entry => entry.Key, StringComparer.Ordinal)
                .Take(ExtensionsShown)
                .Select(entry => string.Create(CultureInfo.InvariantCulture, $"{entry.Key} {entry.Value}"))));
        }

        if (folder.ProjectFile is { } project)
        {
            parts.Add(project);
        }

        if (folder.IsVendored)
        {
            parts.Add("vendored");
        }

        return $"({string.Join(Separator, parts)}) {ClosedMark}";
    }

    private static string Plural(int count, string noun) => count == 1 ? noun : noun + "s";

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
