using Waymark.Core.Code;

namespace Waymark.Languages.TypeScript;

/// <summary>Picks the <see cref="CodeFileClassification"/> of a TypeScript or JavaScript file from its path and what was extracted. The first matching rule wins.</summary>
internal static class TypeScriptFileClassifier
{
    private static readonly HashSet<string> HttpVerbs = new(StringComparer.OrdinalIgnoreCase)
    {
        "get", "post", "put", "delete", "patch", "head", "options", "all", "use",
    };

    private static readonly HashSet<string> HttpReceivers = new(StringComparer.OrdinalIgnoreCase) { "app", "router", "server", "api", "routes" };

    public static string Classify(
        string relativePath,
        IReadOnlyList<CodeTypeEntry> types,
        IReadOnlyList<CodeMemberEntry> members,
        IReadOnlyList<CodeCallSiteEntry> calls)
    {
        var path = relativePath.Replace('\\', '/');
        var fileName = path[(path.LastIndexOf('/') + 1)..];
        var extension = Path.GetExtension(fileName);

        if (fileName.EndsWith(".d.ts", StringComparison.OrdinalIgnoreCase) || fileName.EndsWith(".d.mts", StringComparison.OrdinalIgnoreCase) || fileName.EndsWith(".d.cts", StringComparison.OrdinalIgnoreCase))
        {
            return CodeFileClassification.Declarations;
        }

        if (IsTest(path, fileName))
        {
            return CodeFileClassification.Tests;
        }

        if (IsConfiguration(fileName))
        {
            return CodeFileClassification.Configuration;
        }

        if (calls.Any(c => c.CalleeName == "McpServer") || members.Any(m => HasDecorator(m.Attributes, "tool")))
        {
            return CodeFileClassification.Tools;
        }

        if (types.Any(t => HasDecorator(t.Attributes, "Controller")) || calls.Any(IsRoute))
        {
            return CodeFileClassification.Controller;
        }

        var isJsx = extension.Equals(".tsx", StringComparison.OrdinalIgnoreCase) || extension.Equals(".jsx", StringComparison.OrdinalIgnoreCase);
        if (isJsx && (members.Any(m => m.TypeIndex is null && m.Kind == CodeMemberKind.Function && IsPublic(m.Modifiers) && IsPascalCase(m.Name))
                      || types.Any(t => t.Kind == CodeTypeKind.Class && IsPublic(t.Modifiers) && IsPascalCase(t.Name))))
        {
            return CodeFileClassification.Component;
        }

        if (types.Count > 0 && types.All(t => t.Kind is CodeTypeKind.Interface or CodeTypeKind.TypeAlias) && members.All(m => m.TypeIndex is not null))
        {
            return CodeFileClassification.Interfaces;
        }

        return CodeFileClassification.Source;
    }

    private static bool IsTest(string path, string fileName)
    {
        var stem = fileName;
        if (stem.Contains(".test.", StringComparison.OrdinalIgnoreCase) || stem.Contains(".spec.", StringComparison.OrdinalIgnoreCase) || stem.Contains(".e2e.", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return path.Split('/').SkipLast(1).Any(segment =>
            segment.Equals("__tests__", StringComparison.OrdinalIgnoreCase)
            || segment.Equals("__mocks__", StringComparison.OrdinalIgnoreCase)
            || segment.Equals("e2e", StringComparison.OrdinalIgnoreCase)
            || segment.Equals("cypress", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary><c>vite.config.ts</c>, <c>jest.config.js</c>, <c>next.config.mjs</c>, <c>eslint.config.js</c>, <c>.eslintrc.js</c>, <c>webpack.config.js</c>.</summary>
    private static bool IsConfiguration(string fileName) =>
        fileName.Contains(".config.", StringComparison.OrdinalIgnoreCase)
        || fileName.StartsWith(".eslintrc", StringComparison.OrdinalIgnoreCase)
        || fileName.StartsWith(".prettierrc", StringComparison.OrdinalIgnoreCase)
        || fileName.StartsWith("babel.config", StringComparison.OrdinalIgnoreCase);

    /// <summary>A call such as <c>app.get(...)</c> or <c>router.post(...)</c> outside any function: an Express or Fastify route table.</summary>
    private static bool IsRoute(CodeCallSiteEntry call) =>
        call.Receiver is not null && HttpReceivers.Contains(call.Receiver) && HttpVerbs.Contains(call.CalleeName);

    private static bool HasDecorator(string? attributes, string name) =>
        attributes is not null
        && attributes.Split('\n', StringSplitOptions.RemoveEmptyEntries).Any(line =>
        {
            var open = line.IndexOf('(', StringComparison.Ordinal);
            var bare = (open < 0 ? line : line[..open]).Trim();
            return bare.Equals(name, StringComparison.OrdinalIgnoreCase);
        });

    private static bool IsPublic(string? modifiers) =>
        modifiers is not null && modifiers.Split(' ').Contains("public", StringComparer.Ordinal);

    private static bool IsPascalCase(string name) => name.Length > 0 && char.IsUpper(name[0]);
}
