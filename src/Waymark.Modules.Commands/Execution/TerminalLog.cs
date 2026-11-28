using System.Globalization;
using System.Text;

namespace Waymark.Modules.Commands.Execution;

/// <summary>One finished line of a terminal's output, as plain text, with its number counted from the first line the run printed.</summary>
public readonly record struct LogLine(long Number, string Text);

/// <summary>
/// Lines read from a terminal log: the lines, how many lines between the asked-for start and the first one shown were left out (a read of the newest
/// lines), the number of the last line printed so far, the first line still stored, and how many lines (or grep hits) come after the last one shown.
/// </summary>
public sealed record LogRead(IReadOnlyList<LogLine> Lines, long Skipped, long Total, long OldestKept, long After = 0);

/// <summary>
/// The output of a run that keeps running in a terminal. Everything the program prints goes to disk as it arrives (with colours, as the Console shows
/// it), so nothing is lost when Waymark stops and a run of hours keeps all of it; the file rotates at a size limit and keeps a few older files.
/// It also keeps the most recent lines as plain numbered text, which is what Claude reads: a line number is the cursor of its next read.
/// Every finished line ends with a line break on disk too, and an index beside the log (run-N.log.lines) records the first line of each file, so
/// a line has the same number whether it is read from memory, from the files while the program runs, or after it ended.
/// </summary>
public sealed class TerminalLog : IDisposable
{
    private const int MaxPartialChars = 8_000;
    private const int MaxStoredLineChars = 8_000;
    private const string IndexSuffix = ".lines";
    private static readonly UTF8Encoding Utf8 = new(false);

    private readonly object _gate = new();
    private readonly long _maxBytes;
    private readonly int _files;
    private readonly int _ringLines;
    private readonly LinkedList<LogLine> _ring = new();
    private readonly StringBuilder _partial = new();

    // The first line of each file: [0] the file written now, [i] the file rotated i times; 0 for a file that does not exist yet.
    private readonly long[] _starts;
    private FileStream? _stream;
    private long _fileBytes;
    private bool _disposed;

    public TerminalLog(string path, long maxBytes, int files, int ringLines = 5000)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        LogPath = path;
        _maxBytes = Math.Max(64 * 1024, maxBytes);
        _files = Math.Max(1, files);
        _ringLines = Math.Max(100, ringLines);
        _starts = new long[_files];
        _starts[0] = 1;
    }

    /// <summary>The file written now.</summary>
    public string LogPath { get; }

    /// <summary>Bytes written in all, across rotations.</summary>
    public long Bytes { get; private set; }

    /// <summary>How many lines were finished so far: the number of the last line.</summary>
    public long LineCount { get; private set; }

    /// <summary>When anything (the program, or a line Waymark wrote) was last written.</summary>
    public DateTimeOffset? LastTextUtc { get; private set; }

    /// <summary>The start of a line not finished yet, as plain text (a prompt that waits for an answer often ends without a line break).</summary>
    public string PendingText
    {
        get
        {
            lock (_gate)
            {
                return _partial.Length == 0 ? string.Empty : AnsiText.Plain(_partial.ToString());
            }
        }
    }

    /// <summary>The paths of the older files this log rotated into, newest first.</summary>
    public static IReadOnlyList<string> RotatedPaths(string path, int files)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return Enumerable.Range(1, Math.Max(0, files - 1)).Select(i => Rotated(path, i)).ToList();
    }

    /// <summary>Every file stored beside the log itself: the rotated files and the line index. They are deleted together with the log.</summary>
    public static IReadOnlyList<string> CompanionPaths(string path, int files) => [.. RotatedPaths(path, files), path + IndexSuffix];

    /// <summary>Writes the text and returns the lines it finished, as plain text with their numbers.</summary>
    public IReadOnlyList<LogLine> Append(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return [];
        }

        lock (_gate)
        {
            if (_disposed)
            {
                return [];
            }

            Write(text);
            LastTextUtc = DateTimeOffset.UtcNow;
            var finished = new List<LogLine>();
            var start = 0;
            for (var i = 0; i < text.Length; i++)
            {
                if (text[i] != '\n')
                {
                    continue;
                }

                _partial.Append(text, start, i - start);
                finished.Add(Finish());
                start = i + 1;
            }

            if (start < text.Length)
            {
                _partial.Append(text, start, text.Length - start);
            }

            // A program that never ends its line (a progress bar, minified output) must not grow the buffer without end. The file gets the same
            // line break, so the line numbers read from disk match.
            if (_partial.Length > MaxPartialChars)
            {
                Write("\n");
                finished.Add(Finish());
            }

            return finished;
        }
    }

    /// <summary>
    /// The newest lines after a line number: at most max of them, and in Skipped how many new lines before them were left out. This is what a read
    /// of what is new wants. ReadPage reads the lines in order, from the files when they are older than memory.
    /// </summary>
    public LogRead Read(long sinceLine, int max)
    {
        max = Math.Max(1, max);
        lock (_gate)
        {
            var after = _ring.Where(line => line.Number > sinceLine).ToList();
            var shown = after.Skip(Math.Max(0, after.Count - max)).ToList();
            var skipped = Math.Max(0, LineCount - Math.Max(sinceLine, 0) - shown.Count);
            return new LogRead(shown, skipped, LineCount, OldestStored());
        }
    }

    /// <summary>
    /// The next lines after a line number, oldest first, at most max, and in After how many come after them: a page. Lines still in memory come
    /// from there, older ones from the files on disk, as far back as the kept files reach.
    /// </summary>
    public LogRead ReadPage(long sinceLine, int max) => Scan(sinceLine, max, grep: null);

    /// <summary>The lines after a line number that contain the text (any letter case), oldest first, at most max; After counts the further hits.</summary>
    public LogRead GrepPage(string text, long sinceLine, int max)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        return Scan(sinceLine, max, text);
    }

    /// <summary>A page of a stored log (the program ended, or Waymark restarted since): the same line numbers as while it ran.</summary>
    public static LogRead ReadStored(string path, int files, long sinceLine, int max, string? grep = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var sources = StoredSources(path, Math.Max(1, files));
        try
        {
            return ScanSources(sources, sinceLine, Math.Max(1, max), grep, knownTotal: null);
        }
        finally
        {
            foreach (var source in sources)
            {
                source.Stream.Dispose();
            }
        }
    }

    /// <summary>The last lines of a stored log, with their numbers; Skipped counts the stored lines before them.</summary>
    public static LogRead TailStored(string path, int files, int max)
    {
        max = Math.Max(1, max);
        var probe = ReadStored(path, files, long.MaxValue - 1, 1);
        var since = Math.Max(probe.OldestKept - 1, probe.Total - max);
        var page = ReadStored(path, files, since, max);
        return page with { Skipped = Math.Max(0, since - (probe.OldestKept - 1)) };
    }

    public void Flush()
    {
        lock (_gate)
        {
            try
            {
                _stream?.Flush();
            }
            catch (IOException)
            {
                // The disk is full or gone; the run goes on and the Console still shows its output.
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            if (_partial.Length > 0)
            {
                Write("\n");
                Finish();
            }

            _disposed = true;
            try
            {
                _stream?.Flush();
            }
            catch (IOException)
            {
                // Nothing more can be done for the file.
            }

            _stream?.Dispose();
            _stream = null;
        }
    }

    // The first line any kept file still holds.
    private long OldestStored()
    {
        for (var i = _files - 1; i >= 1; i--)
        {
            if (_starts[i] > 0 && File.Exists(Rotated(LogPath, i)))
            {
                return _starts[i];
            }
        }

        return Bytes > 0 && File.Exists(LogPath) ? _starts[0] : _ring.First?.Value.Number ?? LineCount + 1;
    }

    private LogRead Scan(long sinceLine, int max, string? grep)
    {
        max = Math.Max(1, max);
        List<Source> sources;
        long total;
        lock (_gate)
        {
            var oldestInMemory = _ring.First?.Value.Number ?? LineCount + 1;
            if (sinceLine + 1 >= oldestInMemory || Bytes == 0)
            {
                var after = _ring.Where(l => l.Number > sinceLine && (grep is null || l.Text.Contains(grep, StringComparison.OrdinalIgnoreCase))).ToList();
                var shown = after.Take(max).ToList();
                return new LogRead(shown, 0, LineCount, OldestStored(), after.Count - shown.Count);
            }

            // Older than memory: the files, as they are now. They are opened here, so a rotation while they are read changes nothing.
            try
            {
                _stream?.Flush();
            }
            catch (IOException)
            {
                // Read what is there.
            }

            sources = OpenSources();
            total = LineCount;
        }

        try
        {
            return ScanSources(sources, sinceLine, max, grep, total);
        }
        finally
        {
            foreach (var source in sources)
            {
                source.Stream.Dispose();
            }
        }
    }

    // Oldest file first, each with its first line and the length to read (what was written when it was opened).
    private List<Source> OpenSources()
    {
        var sources = new List<Source>();
        for (var i = _files - 1; i >= 0; i--)
        {
            var path = i == 0 ? LogPath : Rotated(LogPath, i);
            if (_starts[i] <= 0 || !File.Exists(path))
            {
                continue;
            }

            try
            {
                var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                sources.Add(new Source(stream, stream.Length, _starts[i]));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A file that cannot be opened is left out; the lines of the others keep their numbers.
            }
        }

        return sources;
    }

    private static List<Source> StoredSources(string path, int files)
    {
        var starts = ReadIndex(path + IndexSuffix, files);
        var sources = new List<Source>();
        for (var i = files - 1; i >= 0; i--)
        {
            var file = i == 0 ? path : Rotated(path, i);
            if (starts[i] <= 0 || !File.Exists(file))
            {
                continue;
            }

            try
            {
                var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                sources.Add(new Source(stream, stream.Length, starts[i]));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Left out, as above.
            }
        }

        return sources;
    }

    // One "index start" pair per line. A log that never rotated has no index: its one file starts at line 1.
    private static long[] ReadIndex(string indexPath, int files)
    {
        var starts = new long[files];
        starts[0] = 1;
        try
        {
            if (!File.Exists(indexPath))
            {
                return starts;
            }

            foreach (var line in File.ReadAllLines(indexPath))
            {
                var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 2
                    && int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var index) && index >= 0 && index < files
                    && long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var start) && start > 0)
                {
                    starts[index] = start;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Without the index only the newest file is read, as starting at line 1.
        }

        return starts;
    }

    // One pass over the files: a line is the text up to a line break, numbered from the first line of the file it starts in. A file that begins
    // inside a line (its older part was rotated away) gives that line's end under its number. The text after the last break counts as a line only
    // when the program ended (knownTotal null); while it runs it is the line being written. While it runs, a page of plain lines stops reading as
    // soon as it is full: the total is known from memory.
    private static LogRead ScanSources(List<Source> sources, long sinceLine, int max, string? grep, long? knownTotal)
    {
        var shown = new List<LogLine>();
        long after = 0;
        long number = 1;
        long oldest = 0;
        var keeping = false;
        var trailing = false;
        var current = new StringBuilder();
        var buffer = new byte[64 * 1024];
        var chars = new char[Utf8.GetMaxCharCount(buffer.Length)];
        foreach (var source in sources)
        {
            number = source.FirstLine;
            if (oldest == 0)
            {
                oldest = source.FirstLine;
            }

            keeping = number > sinceLine;
            var decoder = Utf8.GetDecoder();
            var left = source.Length;
            while (left > 0)
            {
                var read = source.Stream.Read(buffer, 0, (int)Math.Min(buffer.Length, left));
                if (read <= 0)
                {
                    break;
                }

                left -= read;
                var count = decoder.GetChars(buffer, 0, read, chars, 0, flush: left == 0);
                var start = 0;
                for (var i = 0; i < count; i++)
                {
                    if (chars[i] != '\n')
                    {
                        continue;
                    }

                    if (keeping)
                    {
                        Keep(current, chars, start, i - start);
                        Emit(current, number, grep, max, shown, ref after);
                    }

                    current.Clear();
                    number++;
                    keeping = number > sinceLine;
                    start = i + 1;
                    if (grep is null && knownTotal is { } known && shown.Count >= max)
                    {
                        return new LogRead(shown, 0, known, oldest, Math.Max(0, known - shown[^1].Number));
                    }
                }

                if (count > 0)
                {
                    trailing = start < count;
                }

                if (keeping)
                {
                    Keep(current, chars, start, count - start);
                }
            }
        }

        if (knownTotal is { } total)
        {
            // Lines finished after the files were opened were not read; a plain page counts them as coming after, so the next page asks for them.
            if (grep is null)
            {
                after += Math.Max(0, total - (number - 1));
            }

            return new LogRead(shown, 0, total, oldest == 0 ? total + 1 : oldest, after);
        }

        if (trailing && keeping && current.Length > 0)
        {
            Emit(current, number, grep, max, shown, ref after);
        }

        var stored = trailing ? number : number - 1;
        return new LogRead(shown, 0, Math.Max(0, stored), oldest == 0 ? 1 : oldest, after);
    }

    private static void Keep(StringBuilder current, char[] chars, int start, int length)
    {
        var room = MaxStoredLineChars - current.Length;
        if (room > 0 && length > 0)
        {
            current.Append(chars, start, Math.Min(room, length));
        }
    }

    private static void Emit(StringBuilder current, long number, string? grep, int max, List<LogLine> shown, ref long after)
    {
        var text = AnsiText.Plain(current.ToString()).TrimEnd('\r');
        if (grep is not null && !text.Contains(grep, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (shown.Count < max)
        {
            shown.Add(new LogLine(number, text));
        }
        else
        {
            after++;
        }
    }

    private static string Rotated(string path, int index)
    {
        var folder = Path.GetDirectoryName(path) ?? string.Empty;
        var name = Path.GetFileNameWithoutExtension(path);
        var extension = Path.GetExtension(path);
        return Path.Combine(folder, string.Create(CultureInfo.InvariantCulture, $"{name}.{index}{extension}"));
    }

    private LogLine Finish()
    {
        var plain = AnsiText.Plain(_partial.ToString()).TrimEnd('\r');
        _partial.Clear();
        var line = new LogLine(++LineCount, plain);
        _ring.AddLast(line);
        while (_ring.Count > _ringLines)
        {
            _ring.RemoveFirst();
        }

        return line;
    }

    private void Write(string text)
    {
        var bytes = Utf8.GetBytes(text);
        Bytes += bytes.Length;
        try
        {
            if (_stream is null)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(LogPath) ?? ".");
                _stream = new FileStream(LogPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
                _fileBytes = _stream.Length;
            }

            if (_fileBytes + bytes.Length > _maxBytes && _fileBytes > 0)
            {
                Rotate();
            }

            _stream!.Write(bytes);
            _fileBytes += bytes.Length;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The disk is full or the folder is gone; the run goes on and the Console still shows its output.
            _stream?.Dispose();
            _stream = null;
        }
    }

    private void Rotate()
    {
        _stream?.Dispose();
        _stream = null;
        for (var i = _files - 1; i >= 1; i--)
        {
            var source = i == 1 ? LogPath : Rotated(LogPath, i - 1);
            var target = Rotated(LogPath, i);
            if (File.Exists(source))
            {
                File.Move(source, target, overwrite: true);
            }

            _starts[i] = _starts[i - 1];
        }

        if (_files == 1)
        {
            File.Delete(LogPath);
        }

        // The new file starts with the line being written now (it may continue a line the older file began).
        _starts[0] = LineCount + 1;
        WriteIndex();
        _stream = new FileStream(LogPath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        _fileBytes = 0;
    }

    private void WriteIndex()
    {
        var text = new StringBuilder();
        for (var i = 0; i < _files; i++)
        {
            if (_starts[i] > 0)
            {
                text.Append(CultureInfo.InvariantCulture, $"{i} {_starts[i]}\n");
            }
        }

        try
        {
            File.WriteAllText(LogPath + IndexSuffix, text.ToString(), Utf8);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Without the index, a read after the program ended numbers the newest file from line 1; the reads while it runs still match.
        }
    }

    private sealed record Source(FileStream Stream, long Length, long FirstLine);
}
