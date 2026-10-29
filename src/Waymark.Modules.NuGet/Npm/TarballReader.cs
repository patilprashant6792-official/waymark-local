using System.Formats.Tar;
using System.IO.Compression;
using System.Text;

namespace Waymark.Modules.NuGet.Npm;

/// <summary>What was unpacked from one tarball.</summary>
/// <param name="Files">The files now under the target folder, relative to it (the tarball's <c>package/</c> folder is dropped), with '/' separators.</param>
/// <param name="PackageJson">The text of package.json; null when the tarball has none.</param>
internal sealed record TarballContents(IReadOnlyList<string> Files, string? PackageJson);

/// <summary>
/// Unpacks only the declaration files (and, on request, plain scripts) of an npm tarball into a folder, plus its package.json. Nothing is installed, run or
/// built. Every entry name is checked before a byte is written (no '..', no drive letters, nothing outside the folder), links and special entries are
/// ignored, and the number and the size of the files are limited, so a hostile tarball can neither write elsewhere nor fill the disk.
/// </summary>
internal static class TarballReader
{
    private const int MaxManifestBytes = 1_000_000;
    private const int CopyBufferBytes = 81_920;
    private const int MaxScriptFiles = 3000;

    // A folder with these names holds a package's own tests or samples, not the API a caller uses.
    private static readonly HashSet<string> SkippedFolders = new(StringComparer.OrdinalIgnoreCase)
    {
        "test", "tests", "__tests__", "__mocks__", "spec", "example", "examples", "docs", "benchmark", "benchmarks", "node_modules", ".github",
    };

    /// <param name="includeScripts">Also unpack .js, .mjs and .cjs files, for a package that declares no types (its shape is then read from its source).</param>
    public static TarballContents Extract(string tarballPath, string targetFolder, NpmOptions options, bool includeScripts, CancellationToken ct)
    {
        var root = Path.GetFullPath(targetFolder) + Path.DirectorySeparatorChar;
        Directory.CreateDirectory(targetFolder);
        var files = new List<string>();
        string? packageJson = null;
        long bytes = 0;
        var scripts = 0;

        using var file = File.OpenRead(tarballPath);
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

            var segments = Segments(entry.Name);
            if (segments is null || segments.Count == 0)
            {
                continue;
            }

            var name = segments[^1];
            Stream stream = entry.DataStream;
            if (segments.Count == 1 && name == "package.json")
            {
                packageJson = ReadText(stream);
                continue;
            }

            var isDeclaration = name.EndsWith(".d.ts", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".d.mts", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".d.cts", StringComparison.OrdinalIgnoreCase);
            var isScript = includeScripts && !isDeclaration && IsScript(name);
            if ((!isDeclaration && !isScript) || entry.Length > options.MaxFileBytes || segments.Take(segments.Count - 1).Any(SkippedFolders.Contains))
            {
                continue;
            }

            if (files.Count >= options.MaxFiles || (isScript && scripts >= MaxScriptFiles))
            {
                if (isScript)
                {
                    continue;
                }

                throw new InvalidDataException($"The tarball holds more than {options.MaxFiles} declaration files (Waymark:Npm:MaxFiles).");
            }

            bytes += entry.Length;
            if (bytes > options.MaxExtractedBytes)
            {
                throw new InvalidDataException($"The declaration files of the tarball are bigger than {options.MaxExtractedBytes / 1_000_000} MB (Waymark:Npm:MaxExtractedBytes).");
            }

            var relative = string.Join('/', segments);
            var destination = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
            if (!destination.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            if (CopyLimited(stream, destination, options.MaxFileBytes))
            {
                files.Add(relative);
                if (isScript)
                {
                    scripts++;
                }
            }
        }

        return new TarballContents(files, packageJson);
    }

    private static bool IsScript(string name)
    {
        var extension = Path.GetExtension(name);
        var script = extension.Equals(".js", StringComparison.OrdinalIgnoreCase) || extension.Equals(".mjs", StringComparison.OrdinalIgnoreCase) || extension.Equals(".cjs", StringComparison.OrdinalIgnoreCase);
        return script && !name.Contains(".min.", StringComparison.OrdinalIgnoreCase) && !name.EndsWith(".bundle.js", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The entry name split into safe segments without the leading <c>package/</c> folder, or null when it is not safe.</summary>
    private static List<string>? Segments(string entryName)
    {
        var parts = entryName.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries).ToList();
        if (parts.Count > 0)
        {
            // Every npm tarball wraps its files in one folder, nearly always called 'package'.
            parts.RemoveAt(0);
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

    private static string ReadText(Stream stream)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, bufferSize: 4096, leaveOpen: true);
        var buffer = new char[MaxManifestBytes];
        var read = reader.ReadBlock(buffer, 0, buffer.Length);
        stream.CopyTo(Stream.Null);
        return new string(buffer, 0, read);
    }

    /// <summary>Copies at most <paramref name="maxBytes"/>; a file that turns out bigger than its entry said is removed again.</summary>
    private static bool CopyLimited(Stream source, string destination, long maxBytes)
    {
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
