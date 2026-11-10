using System.Text;
using System.Text.RegularExpressions;
using Waymark.Core.Code;
using Waymark.Core.Security;

namespace Waymark.Core.Text;

/// <summary>
/// The words a member's body uses (identifiers split into parts, plus words in comments and strings), as one space-separated, de-duplicated,
/// capped text. This is what lets a search for "refresh token rotate" find a method whose name says none of it.
/// Secrets are redacted before any word is taken (the scanner, then a mask for values that follow a name such as password or apiKey); only bare lower-case words are kept, never lines or sentences.
/// </summary>
public static partial class MemberTerms
{
    private const int MaskTimeoutMilliseconds = 100;
    public const int MinWordLength = 3;
    public const int MaxWordLength = 32;

    /// <summary>Longest run of letters, digits and underscores that is still read as an identifier; longer runs are encoded blobs.</summary>
    private const int MaxRunLength = 80;

    private const int MaxSliceLines = 600;
    private const int MaxSliceChars = 40_000;

    private static readonly HashSet<string> StopWords = new(StringComparer.Ordinal)
    {
        "the", "and", "for", "not", "with", "from", "that", "this", "are", "was", "has", "have", "will", "can", "but", "you", "its",
        "var", "new", "null", "true", "false", "void", "int", "long", "bool", "string", "object", "char", "byte", "double", "float", "decimal",
        "public", "private", "protected", "internal", "static", "readonly", "const", "sealed", "abstract", "virtual", "override", "partial",
        "class", "struct", "interface", "enum", "record", "namespace", "using", "return", "await", "async", "task", "else", "foreach",
        "while", "switch", "case", "break", "continue", "try", "catch", "finally", "throw", "out", "ref", "default", "get", "set", "value",
        "def", "self", "none", "pass", "import", "lambda", "elif", "yield", "raise", "except", "cls", "print",
        "function", "let", "export", "extends", "implements", "typeof", "undefined", "number", "boolean", "any", "promise", "require",
        "summary", "param", "returns", "paramref", "cref", "typeparam", "see", "seealso", "remarks", "inheritdoc"
    };

    /// <summary>
    /// The words of <paramref name="text"/>, redacted first. Null when the text has none. At most <paramref name="maxTerms"/> words are kept,
    /// in order of first use, so the start of a body outweighs its tail.
    /// </summary>
    public static string? Extract(string? text, int maxTerms)
    {
        if (string.IsNullOrWhiteSpace(text) || maxTerms < 1)
        {
            return null;
        }

        string clean;
        try
        {
            clean = MaskSecretValues(SensitiveDataScanner.Redact(text).Text);
        }
        catch (RegexMatchTimeoutException)
        {
            return null;
        }
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new StringBuilder();
        var count = 0;
        var i = 0;
        while (i < clean.Length && count < maxTerms)
        {
            if (!IsIdentifierChar(clean[i]))
            {
                i++;
                continue;
            }

            var start = i;
            while (i < clean.Length && IsIdentifierChar(clean[i]))
            {
                i++;
            }

            var length = i - start;
            if (length < MinWordLength || length > MaxRunLength)
            {
                continue;
            }

            var run = clean.Substring(start, length);
            count = Add(IdentifierWords.Sanitize(run), seen, result, count, maxTerms);
            foreach (var part in IdentifierWords.Parts(run))
            {
                if (count >= maxTerms)
                {
                    break;
                }

                count = Add(part, seen, result, count, maxTerms);
            }
        }

        return count == 0 ? null : result.ToString();
    }

    /// <summary>
    /// Returns <paramref name="file"/> with <see cref="CodeMemberEntry.Terms"/> filled for each member from its own lines of <paramref name="text"/>.
    /// The file is returned as it is when body terms are switched off or it has no members.
    /// </summary>
    public static CodeFileIndex Annotate(CodeFileIndex file, string text, CodeIndexLimits limits)
    {
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(limits);
        if (!limits.IndexBodyTerms || file.Members.Count == 0 || limits.MaxTermsPerMember < 1)
        {
            return file;
        }

        var lines = text.Split('\n');
        var members = new List<CodeMemberEntry>(file.Members.Count);
        foreach (var member in file.Members)
        {
            members.Add(member with { Terms = Extract(Slice(lines, member.StartLine, member.EndLine), limits.MaxTermsPerMember) });
        }

        return file with { Members = members };
    }

    private static string? Slice(string[] lines, int startLine, int endLine)
    {
        var start = startLine - 1;
        if (start < 0 || start >= lines.Length)
        {
            return null;
        }

        var end = Math.Min(Math.Min(endLine - 1, lines.Length - 1), start + MaxSliceLines - 1);
        if (end < start)
        {
            return null;
        }

        var slice = string.Join('\n', lines, start, end - start + 1);
        return slice.Length > MaxSliceChars ? slice[..MaxSliceChars] : slice;
    }

    private static int Add(string word, HashSet<string> seen, StringBuilder result, int count, int maxTerms)
    {
        if (count >= maxTerms || word.Length < MinWordLength || word.Length > MaxWordLength || StopWords.Contains(word) || !HasLetter(word) || !seen.Add(word))
        {
            return count;
        }

        if (result.Length > 0)
        {
            result.Append(' ');
        }

        result.Append(word);
        return count + 1;
    }

    private static bool HasLetter(string word)
    {
        foreach (var c in word)
        {
            if (c is >= 'a' and <= 'z')
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsIdentifierChar(char c) => c == '_' || (c is >= 'a' and <= 'z') || (c is >= 'A' and <= 'Z') || (c is >= '0' and <= '9');

    // After the scanner: a quoted value that follows a secret-like name, in plain code (password = "...") or escaped inside a string ({\"apiPassword\": \"...\"}).
    // The name stays (it is a useful word), the value goes. A short value (under 3 characters) is not a secret worth hiding.
    [GeneratedRegex(@"(?<keep>(?:pass(?:word|wd|phrase)|pwd|secret|token|api[_-]?key|credential|private[_-]?key)\w*\\?[""']?[ \t]*(?:=>|=|:)[ \t]*)@?\$?\\?[""'][^""'\r\n]{3,}\\?[""']", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, MaskTimeoutMilliseconds)]
    private static partial Regex QuotedSecretValue();

    // The same name followed by a bare value, as in a word-for-word line of a string or comment: password = hunter2.
    [GeneratedRegex(@"(?<keep>(?:pass(?:word|wd|phrase)|pwd|secret|api[_-]?key)\w*[ \t]*[:=][ \t]*)[^\s""',;)\\]{4,}", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, MaskTimeoutMilliseconds)]
    private static partial Regex BareSecretValue();

    private static string MaskSecretValues(string text) => BareSecretValue().Replace(QuotedSecretValue().Replace(text, "${keep} "), "${keep} ");
}
