using System.Text;

namespace Waymark.Host.Terminal;

public readonly record struct TerminalCell(char Character, ushort Style);

public sealed class TerminalLine
{
    public List<TerminalCell> Cells { get; } = [];
}

/// <summary>A group of up to <see cref="AnsiBuffer.BlockSize"/> lines. Only the last block ever changes, so a page redraws only blocks whose <see cref="Version"/> moved.</summary>
public sealed class TerminalBlock(long id)
{
    public long Id { get; } = id;

    public int Version { get; internal set; }

    public List<TerminalLine> Lines { get; } = [];
}

public readonly record struct TerminalRun(string Text, TerminalStyle Style);

/// <summary>
/// A small terminal for command output. It understands what builds and git print: text, colours (16, 256 and true colour), bold and
/// underline, carriage returns that rewrite a progress line, backspace, tabs, and erasing a line. It is not a full terminal: screen
/// positioning and alternate screens are ignored. Escape sequences that arrive split over two chunks are completed by the next chunk.
/// Not thread safe; use it from one component.
/// </summary>
public sealed class AnsiBuffer(int maxLines = AnsiBuffer.DefaultMaxLines)
{
    public const int BlockSize = 100;
    public const int DefaultMaxLines = 5000;
    private const int MaxLineCells = 2000;
    private const int MaxStyles = 4096;
    private const int TabWidth = 8;

    private readonly int _maxLines = maxLines > BlockSize ? maxLines : throw new ArgumentOutOfRangeException(nameof(maxLines));
    private readonly List<TerminalStyle> _styles = [TerminalStyle.Plain];
    private readonly Dictionary<TerminalStyle, ushort> _styleIds = new() { [TerminalStyle.Plain] = 0 };
    private readonly List<TerminalBlock> _blocks = [];
    private readonly StringBuilder _sequence = new();
    private TerminalStyle _current = TerminalStyle.Plain;
    private ushort _currentId;
    private ParserState _state;
    private long _nextBlockId;
    private int _lineCount;
    private int _column;

    private enum ParserState
    {
        Text,
        Escape,
        Csi,
        Osc,
        OscEscape
    }

    public IReadOnlyList<TerminalBlock> Blocks => _blocks;

    /// <summary>How many lines scrolled out of the top because of the line limit.</summary>
    public long DroppedLines { get; private set; }

    public bool IsEmpty => _lineCount == 1 && Last.Lines[^1].Cells.Count == 0 && DroppedLines == 0;

    private TerminalBlock Last => _blocks.Count > 0 ? _blocks[^1] : StartBlock();

    public TerminalStyle StyleOf(ushort id) => id < _styles.Count ? _styles[id] : TerminalStyle.Plain;

    /// <summary>The line as runs of text that share one style.</summary>
    public IEnumerable<TerminalRun> Runs(TerminalLine line)
    {
        ArgumentNullException.ThrowIfNull(line);
        var cells = line.Cells;
        var start = 0;
        while (start < cells.Count)
        {
            var style = cells[start].Style;
            var end = start + 1;
            while (end < cells.Count && cells[end].Style == style)
            {
                end++;
            }

            var text = new StringBuilder(end - start);
            for (var i = start; i < end; i++)
            {
                text.Append(cells[i].Character);
            }

            yield return new TerminalRun(text.ToString(), StyleOf(style));
            start = end;
        }
    }

    public void Clear()
    {
        _blocks.Clear();
        _lineCount = 0;
        _column = 0;
        _state = ParserState.Text;
        _sequence.Clear();
        _current = TerminalStyle.Plain;
        _currentId = 0;
        DroppedLines = 0;
    }

    public void Write(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length == 0)
        {
            return;
        }

        _ = Last;
        foreach (var c in text)
        {
            Step(c);
        }

        Last.Version++;
    }

    private void Step(char c)
    {
        switch (_state)
        {
            case ParserState.Escape:
                Escape(c);
                return;
            case ParserState.Csi:
                Csi(c);
                return;
            case ParserState.Osc:
                if (c == '\u0007')
                {
                    _state = ParserState.Text;
                }
                else if (c == '\u001b')
                {
                    _state = ParserState.OscEscape;
                }

                return;
            case ParserState.OscEscape:
                _state = c == '\\' ? ParserState.Text : ParserState.Osc;
                return;
            default:
                Text(c);
                return;
        }
    }

    private void Text(char c)
    {
        switch (c)
        {
            case '\u001b':
                _state = ParserState.Escape;
                break;
            case '\n':
                NewLine();
                break;
            case '\r':
                _column = 0;
                break;
            case '\b':
                _column = Math.Max(0, _column - 1);
                break;
            case '\t':
                var target = (_column / TabWidth + 1) * TabWidth;
                while (_column < target)
                {
                    Put(' ');
                }

                break;
            default:
                if (!char.IsControl(c))
                {
                    Put(c);
                }

                break;
        }
    }

    private void Escape(char c)
    {
        if (c == '[')
        {
            _sequence.Clear();
            _state = ParserState.Csi;
        }
        else if (c == ']')
        {
            _state = ParserState.Osc;
        }
        else
        {
            _state = ParserState.Text;
        }
    }

    private void Csi(char c)
    {
        if (c is >= '@' and <= '~')
        {
            _state = ParserState.Text;
            Execute(c, _sequence.ToString());
            _sequence.Clear();
        }
        else if (_sequence.Length < 64)
        {
            _sequence.Append(c);
        }
        else
        {
            _state = ParserState.Text;
            _sequence.Clear();
        }
    }

    private void Execute(char final, string parameters)
    {
        var numbers = Numbers(parameters);
        switch (final)
        {
            case 'm':
                ApplyGraphics(numbers);
                break;
            case 'K':
                EraseLine(numbers.Count > 0 ? numbers[0] : 0);
                break;
            case 'C':
                _column += Math.Max(1, numbers.Count > 0 ? numbers[0] : 1);
                break;
            case 'D':
                _column = Math.Max(0, _column - Math.Max(1, numbers.Count > 0 ? numbers[0] : 1));
                break;
            case 'G':
                _column = Math.Max(0, (numbers.Count > 0 ? numbers[0] : 1) - 1);
                break;
            default:
                // Cursor positioning, screen clearing, modes: not needed for command output.
                break;
        }

        _column = Math.Min(_column, MaxLineCells);
    }

    private static List<int> Numbers(string parameters)
    {
        var numbers = new List<int>();
        var clean = parameters.TrimStart('?', '>', '=');
        foreach (var part in clean.Split(';', ':'))
        {
            numbers.Add(int.TryParse(part, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var value) ? value : 0);
        }

        return numbers;
    }

    private void ApplyGraphics(List<int> codes)
    {
        if (codes.Count == 0)
        {
            codes.Add(0);
        }

        var style = _current;
        for (var i = 0; i < codes.Count; i++)
        {
            var code = codes[i];
            switch (code)
            {
                case 0:
                    style = TerminalStyle.Plain;
                    break;
                case 1:
                    style = style with { Bold = true };
                    break;
                case 2:
                    style = style with { Dim = true };
                    break;
                case 3:
                    style = style with { Italic = true };
                    break;
                case 4:
                    style = style with { Underline = true };
                    break;
                case 7:
                    style = style with { Inverse = true };
                    break;
                case 22:
                    style = style with { Bold = false, Dim = false };
                    break;
                case 23:
                    style = style with { Italic = false };
                    break;
                case 24:
                    style = style with { Underline = false };
                    break;
                case 27:
                    style = style with { Inverse = false };
                    break;
                case >= 30 and <= 37:
                    style = style with { Foreground = code - 30 };
                    break;
                case 39:
                    style = style with { Foreground = -1 };
                    break;
                case >= 40 and <= 47:
                    style = style with { Background = code - 40 };
                    break;
                case 49:
                    style = style with { Background = -1 };
                    break;
                case >= 90 and <= 97:
                    style = style with { Foreground = code - 90 + 8 };
                    break;
                case >= 100 and <= 107:
                    style = style with { Background = code - 100 + 8 };
                    break;
                case 38 or 48:
                    var color = ReadExtendedColor(codes, ref i);
                    if (color is { } value)
                    {
                        style = code == 38 ? style with { Foreground = value } : style with { Background = value };
                    }

                    break;
                default:
                    break;
            }
        }

        _current = style;
        _currentId = IdOf(style);
    }

    // 38;5;n picks from the 256 colour palette and 38;2;r;g;b is a true colour. i is left on the last number used.
    private static int? ReadExtendedColor(List<int> codes, ref int i)
    {
        if (i + 1 >= codes.Count)
        {
            return null;
        }

        var mode = codes[i + 1];
        if (mode == 5 && i + 2 < codes.Count)
        {
            var index = codes[i + 2];
            i += 2;
            return index is >= 0 and <= 255 ? index : null;
        }

        if (mode == 2 && i + 4 < codes.Count)
        {
            var red = Math.Clamp(codes[i + 2], 0, 255);
            var green = Math.Clamp(codes[i + 3], 0, 255);
            var blue = Math.Clamp(codes[i + 4], 0, 255);
            i += 4;
            return TerminalStyle.TrueColorFlag | (red << 16) | (green << 8) | blue;
        }

        i = codes.Count;
        return null;
    }

    private ushort IdOf(TerminalStyle style)
    {
        if (_styleIds.TryGetValue(style, out var known))
        {
            return known;
        }

        if (_styles.Count >= MaxStyles)
        {
            return 0;
        }

        var id = (ushort)_styles.Count;
        _styles.Add(style);
        _styleIds[style] = id;
        return id;
    }

    private void EraseLine(int mode)
    {
        var cells = CurrentLine.Cells;
        switch (mode)
        {
            case 1:
                for (var i = 0; i < Math.Min(_column + 1, cells.Count); i++)
                {
                    cells[i] = new TerminalCell(' ', 0);
                }

                break;
            case 2:
                cells.Clear();
                break;
            default:
                if (_column < cells.Count)
                {
                    cells.RemoveRange(_column, cells.Count - _column);
                }

                break;
        }
    }

    private TerminalLine CurrentLine => Last.Lines[^1];

    private void Put(char c)
    {
        if (_column >= MaxLineCells)
        {
            NewLine();
        }

        var cells = CurrentLine.Cells;
        while (cells.Count < _column)
        {
            cells.Add(new TerminalCell(' ', 0));
        }

        var cell = new TerminalCell(c, _currentId);
        if (_column < cells.Count)
        {
            cells[_column] = cell;
        }
        else
        {
            cells.Add(cell);
        }

        _column++;
    }

    private void NewLine()
    {
        var block = Last;
        _column = 0;
        if (block.Lines.Count >= BlockSize)
        {
            block.Version++;
            block = StartBlock();
        }
        else
        {
            block.Lines.Add(new TerminalLine());
            _lineCount++;
        }

        while (_lineCount > _maxLines + BlockSize && _blocks.Count > 1)
        {
            var dropped = _blocks[0];
            _blocks.RemoveAt(0);
            _lineCount -= dropped.Lines.Count;
            DroppedLines += dropped.Lines.Count;
        }
    }

    private TerminalBlock StartBlock()
    {
        var block = new TerminalBlock(_nextBlockId++);
        block.Lines.Add(new TerminalLine());
        _blocks.Add(block);
        _lineCount++;
        return block;
    }
}
