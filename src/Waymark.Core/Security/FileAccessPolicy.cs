using System.IO.Enumeration;
using Microsoft.Extensions.Configuration;

namespace Waymark.Core.Security;

/// <summary>
/// Decides which workspace-relative paths tools may read or change. Pure: no file system access.
/// Order of checks: restricted folders, restricted file names, sensitive keywords anywhere in the path, extension allow-list.
/// </summary>
public sealed class FileAccessPolicy
{
    public const string AdditionalExtensionsKey = "Waymark:Files:AdditionalAllowedExtensions";

    private const int MaxExtensionLength = 16;

    private static readonly HashSet<string> BlockedDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", ".ssh", "node_modules", "bin", "obj", "packages", ".vs", ".idea", "backups", "logs",
    };

    private static readonly HashSet<string> BlockedFileNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ".env", "connectionstrings.config", "web.config", "app.config", "id_rsa", "id_rsa.pub", "gcp-key.json",
    };

    private static readonly string[] BlockedNameGlobs =
    [
        "appsettings*.json", ".env.*", "*.pem", "*.pfx", "*.p12", "*.key", "*.cert",
        "*.db", "*.sqlite", "*.mdf", "*.ldf",
    ];

    private static readonly string[] SensitiveKeywords = ["secret", "password", "credential", "apikey", "token", "private"];

    private static readonly string[] DefaultAllowedExtensions =
    [
        ".cs", ".vb", ".fs", ".csx",
        ".csproj", ".vbproj", ".fsproj", ".sln", ".slnx", ".props", ".targets", ".config",
        ".md", ".txt", ".rst",
        ".html", ".htm", ".css", ".js", ".ts", ".jsx", ".tsx", ".razor", ".cshtml",
        ".ps1", ".sh", ".bat", ".cmd",
        ".xml", ".yml", ".yaml", ".toml", ".json", ".sql", ".resx", ".xaml",
        ".dockerignore", ".editorconfig", ".gitignore", ".gitattributes",
    ];

    private readonly HashSet<string> _allowedExtensions;

    /// <param name="additionalExtensions">Extra extensions to allow, each like ".abc". Blocked folders, names and keywords still win.</param>
    public FileAccessPolicy(IEnumerable<string>? additionalExtensions = null)
    {
        _allowedExtensions = new HashSet<string>(DefaultAllowedExtensions, StringComparer.OrdinalIgnoreCase);
        foreach (var extension in additionalExtensions ?? [])
        {
            _allowedExtensions.Add(NormalizeExtension(extension));
        }
    }

    public static FileAccessPolicy FromConfiguration(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var raw = configuration[AdditionalExtensionsKey];
        var extensions = string.IsNullOrWhiteSpace(raw)
            ? []
            : raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return new FileAccessPolicy(extensions);
    }

    /// <summary>The built-in blocked folder names. Shown read-only in the admin UI; workspace rules cannot remove them.</summary>
    public static IReadOnlyCollection<string> BaselineFolders => BlockedDirectories;

    /// <summary>The built-in blocked file names and patterns.</summary>
    public static IReadOnlyCollection<string> BaselineFileNames => [.. BlockedFileNames, .. BlockedNameGlobs];

    /// <summary>The built-in words that block a path.</summary>
    public static IReadOnlyCollection<string> BaselineKeywords => SensitiveKeywords;

    /// <summary>The file types allowed everywhere (built in plus the configured extras).</summary>
    public IReadOnlyCollection<string> AllowedExtensions => _allowedExtensions;

    /// <summary>Null when the path is allowed, otherwise the reason it is refused. An empty path is the workspace root and is allowed.</summary>
    public string? Evaluate(string? relativePath) => Evaluate(relativePath, WorkspaceAccessRules.Empty);

    /// <summary>
    /// Like <see cref="Evaluate(string?)"/> with one workspace's rules. The built-in blocks are checked first and cannot be lifted;
    /// the rules then block more, or allow one more file type.
    /// </summary>
    public string? Evaluate(string? relativePath, WorkspaceAccessRules rules)
    {
        ArgumentNullException.ThrowIfNull(rules);
        var segments = (relativePath ?? string.Empty)
            .Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries)
            .Where(segment => segment != ".")
            .ToArray();
        if (segments.Length == 0)
        {
            return null;
        }

        foreach (var segment in segments)
        {
            if (BlockedDirectories.Contains(segment))
            {
                return $"'{segment}' is a restricted folder (build output, version control, private keys or logs).";
            }
        }

        var fileName = segments[^1];
        if (BlockedFileNames.Contains(fileName) || BlockedNameGlobs.Any(glob => FileSystemName.MatchesSimpleExpression(glob, fileName, ignoreCase: true)))
        {
            return "This file type holds configuration secrets, keys or databases.";
        }

        var joined = string.Join('/', segments);
        foreach (var keyword in SensitiveKeywords)
        {
            if (joined.Contains(keyword, StringComparison.OrdinalIgnoreCase))
            {
                return $"The path contains '{keyword}', which marks sensitive content.";
            }
        }

        var ruleReason = rules.EvaluateBlocks(segments);
        if (ruleReason is not null)
        {
            return ruleReason;
        }

        var extension = Path.GetExtension(fileName);
        if (extension.Length > 0 && !_allowedExtensions.Contains(extension) && !rules.AllowedExtensions.Contains(extension))
        {
            return $"The extension '{extension}' is not on the allowed list.";
        }

        return null;
    }

    /// <exception cref="FileAccessDeniedException">The path is refused.</exception>
    public void EnsureAllowed(string? relativePath) => EnsureAllowed(relativePath, WorkspaceAccessRules.Empty);

    /// <exception cref="FileAccessDeniedException">The path is refused.</exception>
    public void EnsureAllowed(string? relativePath, WorkspaceAccessRules rules)
    {
        var reason = Evaluate(relativePath, rules);
        if (reason is not null)
        {
            throw new FileAccessDeniedException(relativePath ?? string.Empty, reason);
        }
    }

    private static string NormalizeExtension(string? extension)
    {
        var value = extension?.Trim() ?? string.Empty;
        if (value.Length < 2 || value.Length > MaxExtensionLength || value[0] != '.' || !value.Skip(1).All(char.IsAsciiLetterOrDigit))
        {
            throw new ArgumentException($"'{value}' is not a valid extension. Use a dot followed by letters or digits, for example .proto.", nameof(extension));
        }

        return value.ToLowerInvariant();
    }
}
