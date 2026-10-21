using Microsoft.CodeAnalysis.Text;

namespace Waymark.Modules.Code.Indexing;

internal static class SourceLines
{
    /// <summary>Lines as an editor shows them: a final line break does not start another line, an empty file has none.</summary>
    public static int Count(SourceText text)
    {
        if (text.Length == 0)
        {
            return 0;
        }

        var last = text[text.Length - 1];
        var endsWithBreak = last is '\n' or '\r' or '\u0085' or '\u2028' or '\u2029';
        return endsWithBreak ? text.Lines.Count - 1 : text.Lines.Count;
    }
}
