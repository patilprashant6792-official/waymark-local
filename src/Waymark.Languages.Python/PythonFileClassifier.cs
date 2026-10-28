using Waymark.Core.Code;

namespace Waymark.Languages.Python;

/// <summary>Picks the <see cref="CodeFileClassification"/> of a Python file from its path and what was extracted. The first matching rule wins.</summary>
internal static class PythonFileClassifier
{
    private static readonly HashSet<string> RouteMethods = new(StringComparer.Ordinal)
    {
        "get", "post", "put", "delete", "patch", "head", "options", "route", "websocket", "api_route",
    };

    private static readonly string[] ViewSuffixes = ["View", "ViewSet", "APIView", "Resource"];

    private static readonly HashSet<string> ConfigurationFiles = new(StringComparer.OrdinalIgnoreCase)
    {
        "settings.py", "config.py", "conf.py", "setup.py",
    };

    public static string Classify(
        string relativePath,
        IReadOnlyList<CodeTypeEntry> types,
        IReadOnlyList<CodeMemberEntry> members,
        IReadOnlyList<CodeCallSiteEntry> calls)
    {
        var path = relativePath.Replace('\\', '/');
        var fileName = path[(path.LastIndexOf('/') + 1)..];

        if (fileName.EndsWith(".pyi", StringComparison.OrdinalIgnoreCase))
        {
            return CodeFileClassification.Declarations;
        }

        if (IsTestPath(path, fileName))
        {
            return CodeFileClassification.Tests;
        }

        if (members.Any(m => HasDecoratorNamed(m.Attributes, "tool")))
        {
            return CodeFileClassification.Tools;
        }

        if (calls.Any(c => c.CalleeName == "FastMCP"))
        {
            return CodeFileClassification.McpConfiguration;
        }

        if (fileName.Equals("urls.py", StringComparison.OrdinalIgnoreCase)
            || members.Any(m => IsRoute(m.Attributes))
            || types.Any(IsView))
        {
            return CodeFileClassification.Controller;
        }

        if (ConfigurationFiles.Contains(fileName))
        {
            return CodeFileClassification.Configuration;
        }

        if (types.Count > 0 && types.All(t => t.Kind == CodeTypeKind.Interface))
        {
            return CodeFileClassification.Interfaces;
        }

        return CodeFileClassification.Source;
    }

    private static bool IsTestPath(string path, string fileName)
    {
        if (fileName.StartsWith("test_", StringComparison.OrdinalIgnoreCase)
            || fileName.EndsWith("_test.py", StringComparison.OrdinalIgnoreCase)
            || fileName.Equals("conftest.py", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return path.Split('/').SkipLast(1).Any(segment => segment.Equals("tests", StringComparison.OrdinalIgnoreCase) || segment.Equals("test", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>A decorator whose last name segment is <paramref name="name"/>: <c>mcp.tool()</c> and <c>tool</c> both name "tool".</summary>
    private static bool HasDecoratorNamed(string? attributes, string name) =>
        attributes is not null && Decorators(attributes).Any(d => LastSegment(d) == name);

    /// <summary>An HTTP route: <c>@app.get("/x")</c>, <c>@router.post</c>, <c>@bp.route</c> (a receiver and a verb), or Django REST's <c>@api_view</c>.</summary>
    private static bool IsRoute(string? attributes)
    {
        if (attributes is null)
        {
            return false;
        }

        return Decorators(attributes).Any(d => d == "api_view" || (d.Contains('.', StringComparison.Ordinal) && RouteMethods.Contains(LastSegment(d))));
    }

    private static bool IsView(CodeTypeEntry type) =>
        type.BaseList is not null
        && type.BaseList
            .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Any(b => ViewSuffixes.Any(suffix => LastSegment(b).EndsWith(suffix, StringComparison.Ordinal)));

    /// <summary>The decorators of an entry without their arguments, one per line of the stored attribute text.</summary>
    private static IEnumerable<string> Decorators(string attributes) =>
        attributes
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(line =>
            {
                var open = line.IndexOf('(', StringComparison.Ordinal);
                return (open < 0 ? line : line[..open]).Trim();
            });

    private static string LastSegment(string dotted)
    {
        var bracket = dotted.IndexOf('[', StringComparison.Ordinal);
        var bare = bracket < 0 ? dotted : dotted[..bracket];
        return bare[(bare.LastIndexOf('.') + 1)..];
    }
}
