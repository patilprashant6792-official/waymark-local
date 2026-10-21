using System.Text;

namespace Waymark.Core.Tools;

/// <summary>
/// A bounded markdown writer for tool output. Whole lines are added until the next one would not fit;
/// from then on nothing is added and the result ends with a short note telling the reader to narrow the request.
/// </summary>
public sealed class OutputBudget(int maxChars)
{
    private readonly StringBuilder _text = new();
    private readonly int _maxChars = maxChars > 0 ? maxChars : throw new ArgumentOutOfRangeException(nameof(maxChars));

    public bool IsTruncated { get; private set; }

    public int Length => _text.Length;

    /// <summary>Adds the line and a line break. False (and the output is marked cut) when it does not fit.</summary>
    public bool TryAppendLine(string line = "")
    {
        ArgumentNullException.ThrowIfNull(line);
        if (IsTruncated || _text.Length + line.Length + 1 > _maxChars)
        {
            IsTruncated = true;
            return false;
        }

        _text.Append(line).Append('\n');
        return true;
    }

    public string Build() =>
        IsTruncated
            ? $"{_text}\n_Output cut at {_maxChars} characters. Narrow the request (folder, pattern, page) to see the rest._\n"
            : _text.ToString();
}
