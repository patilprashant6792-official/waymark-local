using System.Formats.Tar;
using System.IO.Compression;
using System.Text;

namespace Waymark.Modules.NuGet.Pypi;

/// <summary>What was unpacked from one archive.</summary>
/// <param name="Files">The Python files (.py, .pyi, py.typed) now under the target folder, relative to it, with '/' separators.</param>
/// <param name="HasPyTyped">The package says it carries its own types (a py.typed marker).</param>
/// <param name="Metadata">The text of METADATA (wheel) or PKG-INFO (sdist); null when there is none.</param>
/// <param name="TopLevel">The importable top-level names that top_level.txt lists; empty when the file is missing.</param>
internal sealed record ArchiveContents(IReadOnlyList<string> Files, bool HasPyTyped, string? Metadata, IReadOnlyList<string> TopLevel);

/// <summary>
/// Unpacks only the Python text of a wheel or a source archive into a folder. Nothing is built, imported or run. Every entry name is checked before a byte is
/// written (no '..', no drive letters, nothing outside the folder), links and special entries are ignored, and the number and the size of the files are
/// limited, so a hostile archive can neither write elsewhere nor fill the disk.
/// </summary>
internal static class ArchiveReader
{
    private const int MaxMetadataBytes = 200_000;
    private const int CopyBufferBytes = 81_920;

    // A folder with these names holds a package's own tests or tooling, not the API a caller uses.
    private static readonly HashSet<string> SkippedFolders = new(StringComparer.OrdinalIgnoreCase)
    {
        "tests", "test", "testing", "__pycache__", ".git", "docs", "examples",
    };

    public static ArchiveContents ExtractWheel(string wheelPath, string targetFolder, PypiOptions options, CancellationToken ct)
    {
        var sink = new Sink(targetFolder, options);
        using var archive = ZipFile.OpenRead(wheelPath);
        foreach (var entry in archive.Entries)
        {
            ct.ThrowIfCancellationRequested();
            if (entry.Name.Length == 0)
            {
                continue;
            }

            sink.Add(entry.FullName, entry.Length, entry.Open, isWheel: true);
        }

        return sink.Result();
    }

    /// <summary>A .tar.gz source archive. The top folder (<c>name-1.0/</c>) is dropped from the paths.</summary>
    public static ArchiveContents ExtractSdist(string archivePath, string targetFolder, PypiOptions options, CancellationToken ct)
    {
        var sink = new Sink(targetFolder, options);
        using var file = File.OpenRead(archivePath);
        using var gzip = new GZipStream(file, CompressionMode.Decompress);
        using var reader = new TarReader(gzip);
        TarEntry? entry;
        while ((entry = reader.GetNextEntry()) is not null)
        {
            ct.ThrowIfCancellationRequested();
            if (entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile) || entry.DataStream is null)
            {
                continue;
            }

            Stream stream = entry.DataStream;
            sink.Add(entry.Name, entry.Length, () => stream, isWheel: false);
        }

        return sink.Result();
    }

    private sealed class Sink(string targetFolder, PypiOptions options)
    {
        private readonly string _root = Path.GetFullPath(targetFolder) + Path.DirectorySeparatorChar;
        private readonly List<string> _files = [];
        private readonly List<string> _topLevel = [];
        private bool _hasPyTyped;
        private string? _metadata;
        private long _bytes;

        public void Add(string entryName, long length, Func<Stream> open, bool isWheel)
        {
            var segments = Segments(entryName, isWheel);
            if (segments is null || segments.Count == 0)
            {
                return;
            }

            var name = segments[^1];
            if (IsMetadata(segments, name, isWheel))
            {
                ReadMetadata(name, open);
                return;
            }

            var isTypedMarker = string.Equals(name, "py.typed", StringComparison.Ordinal);
            var extension = Path.GetExtension(name);
            var isPython = string.Equals(extension, ".py", StringComparison.OrdinalIgnoreCase) || string.Equals(extension, ".pyi", StringComparison.OrdinalIgnoreCase);
            if ((!isPython && !isTypedMarker) || length > options.MaxFileBytes || segments.Take(segments.Count - 1).Any(SkippedFolders.Contains))
            {
                return;
            }

            if (_files.Count >= options.MaxFiles)
            {
                throw new InvalidDataException($"The archive holds more than {options.MaxFiles} Python files (Waymark:Pypi:MaxFiles).");
            }

            _bytes += length;
            if (_bytes > options.MaxExtractedBytes)
            {
                throw new InvalidDataException($"The Python files of the archive are bigger than {options.MaxExtractedBytes / 1_000_000} MB (Waymark:Pypi:MaxExtractedBytes).");
            }

            var relative = string.Join('/', segments);
            var destination = Path.GetFullPath(Path.Combine(_root, relative.Replace('/', Path.DirectorySeparatorChar)));
            if (!destination.StartsWith(_root, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            if (!CopyLimited(open, destination, options.MaxFileBytes))
            {
                return;
            }

            _files.Add(relative);
            if (isTypedMarker)
            {
                _hasPyTyped = true;
            }
        }

        public ArchiveContents Result() => new(_files, _hasPyTyped, _metadata, _topLevel);

        private static bool IsMetadata(List<string> segments, string name, bool isWheel) =>
            isWheel
                ? segments.Count == 2 && segments[0].EndsWith(".dist-info", StringComparison.OrdinalIgnoreCase) && (name is "METADATA" or "top_level.txt")
                : (segments.Count == 1 && name == "PKG-INFO") || (segments.Count == 2 && segments[0].EndsWith(".egg-info", StringComparison.OrdinalIgnoreCase) && (name is "PKG-INFO" or "top_level.txt"));

        private void ReadMetadata(string name, Func<Stream> open)
        {
            using var stream = open();
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, bufferSize: 4096, leaveOpen: true);
            var buffer = new char[MaxMetadataBytes];
            var read = reader.ReadBlock(buffer, 0, buffer.Length);
            var text = new string(buffer, 0, read);
            stream.CopyTo(Stream.Null);
            if (name == "top_level.txt")
            {
                _topLevel.AddRange(text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
            }
            else
            {
                _metadata ??= text;
            }
        }

        /// <summary>The path split into safe segments, or null when the entry is not wanted or is not safe.</summary>
        private static List<string>? Segments(string entryName, bool isWheel)
        {
            var parts = entryName.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries).ToList();
            if (!isWheel && parts.Count > 0)
            {
                // name-1.0/src/pkg/mod.py -> src/pkg/mod.py
                parts.RemoveAt(0);
            }

            if (isWheel && parts.Count > 2 && parts[0].EndsWith(".data", StringComparison.OrdinalIgnoreCase))
            {
                // pkg-1.0.data/purelib/x.py and platlib/x.py are the package's own files; scripts, headers and data are not.
                if (!parts[1].Equals("purelib", StringComparison.OrdinalIgnoreCase) && !parts[1].Equals("platlib", StringComparison.OrdinalIgnoreCase))
                {
                    return null;
                }

                parts.RemoveRange(0, 2);
            }

            var invalid = Path.GetInvalidFileNameChars();
            foreach (var part in parts)
            {
                if (part == "." || part == ".." || part.Contains(':', StringComparison.Ordinal) || part.IndexOfAny(invalid) >= 0)
                {
                    return null;
                }
            }

            return parts;
        }

        /// <summary>Copies at most <paramref name="maxBytes"/>; a file that turns out bigger than its entry said is removed again.</summary>
        private static bool CopyLimited(Func<Stream> open, string destination, long maxBytes)
        {
            using var source = open();
            using var target = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None);
            var buffer = new byte[CopyBufferBytes];
            long total = 0;
            int read;
            while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
            {
                total += read;
                if (total > maxBytes)
                {
                    target.Dispose();
                    File.Delete(destination);

                    // A tar reader moves on from where this stream ends, so what is left of the entry is read and dropped.
                    source.CopyTo(Stream.Null);
                    return false;
                }

                target.Write(buffer, 0, read);
            }

            return true;
        }
    }
}
