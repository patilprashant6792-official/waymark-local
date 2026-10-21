using Waymark.Core.Code;

namespace Waymark.Modules.Code.Indexing;

/// <summary>Picks the <see cref="CodeFileClassification"/> of a file from what was extracted; the same patterns the old server looked for, found in syntax instead of raw text.</summary>
internal static class FileClassifier
{
    private const string ToolAttribute = "McpServerTool";
    private const string ApiControllerAttribute = "ApiController";
    private const string ControllerSuffix = "Controller";
    private const string ControllerBaseSuffix = "ControllerBase";

    private static readonly HashSet<string> McpCalls = new(StringComparer.Ordinal)
    {
        "AddMcpServer", "WithTools", "WithToolsFromAssembly", "MapMcp", "MapMcpServer"
    };

    private static readonly HashSet<string> InjectionCalls = new(StringComparer.Ordinal)
    {
        "AddSingleton", "AddScoped", "AddTransient",
        "TryAddSingleton", "TryAddScoped", "TryAddTransient",
        "AddKeyedSingleton", "AddKeyedScoped", "AddKeyedTransient",
        "AddHostedService"
    };

    public static string Classify(
        bool hasTopLevelStatements,
        IReadOnlyList<CodeTypeEntry> types,
        IReadOnlyList<CodeMemberEntry> members,
        IReadOnlyList<CodeCallSiteEntry> calls)
    {
        if (types.Any(t => ContainsTool(t.Attributes)) || members.Any(m => ContainsTool(m.Attributes)))
        {
            return CodeFileClassification.Tools;
        }

        if (calls.Any(c => McpCalls.Contains(c.CalleeName)))
        {
            return CodeFileClassification.McpConfiguration;
        }

        if (hasTopLevelStatements)
        {
            return CodeFileClassification.TopLevelStatements;
        }

        if (calls.Any(c => InjectionCalls.Contains(c.CalleeName)))
        {
            return CodeFileClassification.DependencyInjection;
        }

        if (types.Any(IsController))
        {
            return CodeFileClassification.Controller;
        }

        if (types.Count > 0 && types.All(t => t.Kind == CodeTypeKind.Interface))
        {
            return CodeFileClassification.Interfaces;
        }

        return CodeFileClassification.Source;
    }

    private static bool ContainsTool(string? attributes) =>
        attributes is not null && attributes.Contains(ToolAttribute, StringComparison.Ordinal);

    private static bool IsController(CodeTypeEntry type)
    {
        if (type.Attributes is not null && type.Attributes.Contains(ApiControllerAttribute, StringComparison.Ordinal))
        {
            return true;
        }

        return type.BaseList is not null
            && type.BaseList
                .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                .Any(b => b.EndsWith(ControllerSuffix, StringComparison.Ordinal) || b.EndsWith(ControllerBaseSuffix, StringComparison.Ordinal));
    }
}
