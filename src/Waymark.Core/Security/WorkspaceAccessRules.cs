using System.IO.Enumeration;

namespace Waymark.Core.Security;

/// <summary>
/// The enabled rules of one workspace, ready to match paths. Immutable. Folder rules with a '/' are paths from the workspace root;
/// the others match any single folder name. Matching ignores case.
/// </summary>
public sealed class WorkspaceAccessRules
{
    private readonly string[] _folders;
    private readonly string[] _fileNames;
    private readonly string[] _keywords;

    public WorkspaceAccessRules(IEnumerable<AccessRule> rules)
    {
        ArgumentNullException.ThrowIfNull(rules);
        var active = rules.Where(rule => rule.Enabled).ToList();
        _folders = Patterns(active, AccessRuleKind.BlockFolder);
        _fileNames = Patterns(active, AccessRuleKind.BlockFileName);
        _keywords = Patterns(active, AccessRuleKind.BlockKeyword);
        AllowedExtensions = new HashSet<string>(Patterns(active, AccessRuleKind.AllowExtension), StringComparer.OrdinalIgnoreCase);
    }

    public static WorkspaceAccessRules Empty { get; } = new([]);

    /// <summary>Extra file types tools may open. The built-in blocks are checked first, so these never reach a secret.</summary>
    public IReadOnlySet<string> AllowedExtensions { get; }

    /// <summary>Null when no block rule matches, otherwise the reason. <paramref name="segments"/> are the path parts without '.' entries.</summary>
    public string? EvaluateBlocks(IReadOnlyList<string> segments)
    {
        ArgumentNullException.ThrowIfNull(segments);
        if (segments.Count == 0)
        {
            return null;
        }

        var joined = string.Join('/', segments);
        foreach (var folder in _folders)
        {
            var matches = folder.Contains('/')
                ? joined.Equals(folder, StringComparison.OrdinalIgnoreCase) || joined.StartsWith(folder + '/', StringComparison.OrdinalIgnoreCase)
                : segments.Any(segment => FileSystemName.MatchesSimpleExpression(folder, segment, ignoreCase: true));
            if (matches)
            {
                return Reason("folder", folder);
            }
        }

        var fileName = segments[^1];
        foreach (var pattern in _fileNames)
        {
            if (FileSystemName.MatchesSimpleExpression(pattern, fileName, ignoreCase: true))
            {
                return Reason("file name", pattern);
            }
        }

        foreach (var keyword in _keywords)
        {
            if (joined.Contains(keyword, StringComparison.OrdinalIgnoreCase))
            {
                return Reason("word", keyword);
            }
        }

        return null;
    }

    private static string Reason(string what, string pattern) =>
        $"Blocked by your workspace rule ({what} '{pattern}'). Change it under Workspaces, Access rules.";

    private static string[] Patterns(List<AccessRule> rules, AccessRuleKind kind) =>
        rules.Where(rule => rule.Kind == kind).Select(rule => rule.Pattern).ToArray();
}
