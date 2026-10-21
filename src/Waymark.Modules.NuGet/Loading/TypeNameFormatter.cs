using System.Text;
using System.Text.RegularExpressions;

namespace Waymark.Modules.NuGet.Loading;

/// <summary>Turns CLR type names (Foo`1, Outer+Inner, System.Int32) into the way C# source spells them.</summary>
public static partial class TypeNameFormatter
{
    private static readonly Dictionary<string, string> Aliases = new(StringComparer.Ordinal)
    {
        ["System.String"] = "string",
        ["System.Int32"] = "int",
        ["System.Int64"] = "long",
        ["System.Boolean"] = "bool",
        ["System.Double"] = "double",
        ["System.Decimal"] = "decimal",
        ["System.Single"] = "float",
        ["System.Byte"] = "byte",
        ["System.SByte"] = "sbyte",
        ["System.Int16"] = "short",
        ["System.UInt16"] = "ushort",
        ["System.UInt32"] = "uint",
        ["System.UInt64"] = "ulong",
        ["System.Char"] = "char",
        ["System.Object"] = "object",
        ["System.Void"] = "void"
    };

    private static readonly string[] Placeholders = ["T", "U", "V", "W"];

    public static string Simplify(string? fullTypeName)
    {
        if (string.IsNullOrEmpty(fullTypeName))
        {
            return "void";
        }

        var name = fullTypeName.Replace('+', '.');

        if (name.Contains("System.Nullable`1", StringComparison.Ordinal))
        {
            var nullable = NullableArgument().Match(name);
            if (nullable.Success)
            {
                return $"{Simplify(nullable.Groups[1].Value)}?";
            }
        }

        var generic = ClosedGeneric().Match(name);
        if (generic.Success)
        {
            var shortBase = generic.Groups[1].Value.Split('.')[^1];
            var arguments = SplitArguments(generic.Groups[3].Value).Select(Simplify).ToList();
            return $"{shortBase}<{string.Join(", ", arguments)}>";
        }

        var open = OpenGeneric().Match(name);
        if (open.Success)
        {
            return OpenForm(open.Groups[1].Value.Split('.')[^1], open.Groups[2].Value);
        }

        var withoutAssembly = name.Split(',')[0].Trim();
        if (Aliases.TryGetValue(withoutAssembly, out var alias))
        {
            return alias;
        }

        var simple = withoutAssembly.Split('.')[^1];
        var trailing = TrailingArity().Match(simple);
        return trailing.Success ? OpenForm(trailing.Groups[1].Value, trailing.Groups[2].Value) : simple;
    }

    private static string OpenForm(string baseName, string arityText)
    {
        if (!int.TryParse(arityText, out var arity) || arity < 1 || arity > 32)
        {
            return baseName;
        }

        var names = Enumerable.Range(0, arity).Select(i => i < Placeholders.Length ? Placeholders[i] : $"T{i + 1}");
        return $"{baseName}<{string.Join(", ", names)}>";
    }

    private static List<string> SplitArguments(string text)
    {
        var result = new List<string>();
        var current = new StringBuilder();
        var depth = 0;

        foreach (var ch in text)
        {
            switch (ch)
            {
                case '[':
                    depth++;
                    if (depth > 1)
                    {
                        current.Append(ch);
                    }

                    break;
                case ']':
                    depth--;
                    if (depth > 0)
                    {
                        current.Append(ch);
                    }

                    break;
                case ',' when depth == 1:
                    result.Add(Unwrap(current.ToString()));
                    current.Clear();
                    break;
                default:
                    current.Append(ch);
                    break;
            }
        }

        if (current.Length > 0)
        {
            result.Add(Unwrap(current.ToString()));
        }

        return result;
    }

    private static string Unwrap(string argument)
    {
        var trimmed = argument.Trim();
        return trimmed.Length >= 2 && trimmed[0] == '[' && trimmed[^1] == ']' ? trimmed[1..^1] : trimmed;
    }

    [GeneratedRegex(@"System\.Nullable`1\[\[([^,]+),", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex NullableArgument();

    [GeneratedRegex(@"^([^`\[]+)`(\d+)\[\[(.+)\]\]$", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex ClosedGeneric();

    [GeneratedRegex(@"^([^`\[,]+)`(\d+)$", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex OpenGeneric();

    [GeneratedRegex(@"^([^`]+)`(\d+)$", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex TrailingArity();
}
