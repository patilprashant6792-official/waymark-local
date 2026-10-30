using System.Text;
using System.Text.RegularExpressions;

namespace Waymark.Core.Workspaces;

/// <summary>
/// The GitHub repository a workspace folder is the root of. The address is rebuilt from the owner and the name,
/// so it can never carry a user name, a password or a token that was written into the remote URL.
/// </summary>
public sealed record GitHubRepo(string Owner, string Name, string? Branch)
{
    public string FullName => $"{Owner}/{Name}";

    public string Url => $"https://github.com/{Owner}/{Name}";
}

/// <summary>
/// Finds out whether a workspace folder is the root of a git checkout whose remote is on github.com.
/// It reads the files in .git itself: no git process runs, nothing is changed, and a parent folder is never searched.
/// </summary>
public static partial class GitHubRepoDetector
{
    private const long MaxFileBytes = 256 * 1024;
    private const int MaxSegmentLength = 100;
    private const int MaxBranchLength = 200;
    private const string PointerPrefix = "gitdir:";
    private const string HeadPrefix = "ref: refs/heads/";

    private static readonly string[] AllowedSchemes = ["https", "http", "ssh", "git"];

    /// <summary>The repository of the workspace folder, or null when the folder is not a git checkout, has no GitHub remote, or cannot be read.</summary>
    public static GitHubRepo? Detect(string? workspacePath)
    {
        if (string.IsNullOrWhiteSpace(workspacePath))
        {
            return null;
        }

        try
        {
            return DetectCore(workspacePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or System.Security.SecurityException)
        {
            // A folder that cannot be read is simply not shown as a repository.
            return null;
        }
    }

    /// <summary>
    /// Reads a git remote address in any form git writes: https://github.com/o/r.git, ssh://git@github.com/o/r.git,
    /// git@github.com:o/r.git, with or without .git, a port, credentials or a trailing slash. Only github.com matches.
    /// </summary>
    public static bool TryParseGitHubUrl(string? url, out string owner, out string name)
    {
        owner = string.Empty;
        name = string.Empty;
        if (string.IsNullOrWhiteSpace(url))
        {
            return false;
        }

        var text = url.Trim();
        string host;
        string path;
        var schemeEnd = text.IndexOf("://", StringComparison.Ordinal);
        if (schemeEnd > 0)
        {
            if (!AllowedSchemes.Contains(text[..schemeEnd], StringComparer.OrdinalIgnoreCase))
            {
                return false;
            }

            var rest = text[(schemeEnd + 3)..];
            var slash = rest.IndexOf('/');
            if (slash < 0)
            {
                return false;
            }

            host = HostOf(rest[..slash]);
            path = rest[(slash + 1)..];
        }
        else
        {
            // The short ssh form user@host:path. The first ':' must come before any '/', or it is a local path such as C:/work/repo.
            var colon = text.IndexOf(':');
            var slash = text.IndexOf('/');
            if (colon <= 0 || (slash >= 0 && slash < colon))
            {
                return false;
            }

            host = HostOf(text[..colon]);
            path = text[(colon + 1)..];
        }

        return IsGitHubHost(host) && TryParsePath(path, out owner, out name);
    }

    private static GitHubRepo? DetectCore(string workspacePath)
    {
        var dotGit = Path.Combine(workspacePath, ".git");
        string gitDir;
        if (Directory.Exists(dotGit))
        {
            gitDir = dotGit;
        }
        else if (File.Exists(dotGit))
        {
            // A linked worktree or a submodule: .git is a file that points to the real git folder.
            var target = ResolvePointer(dotGit, workspacePath);
            if (target is null)
            {
                return null;
            }

            gitDir = target;
        }
        else
        {
            return null;
        }

        var config = ReadText(Path.Combine(ResolveCommonDir(gitDir), "config"));
        if (config is null || !TryFindRemote(config, out var owner, out var name))
        {
            return null;
        }

        return new GitHubRepo(owner, name, ReadBranch(gitDir));
    }

    private static string? ResolvePointer(string dotGitFile, string workspacePath)
    {
        var line = FirstLine(ReadText(dotGitFile));
        if (line is null || !line.StartsWith(PointerPrefix, StringComparison.Ordinal))
        {
            return null;
        }

        var target = line[PointerPrefix.Length..].Trim();
        if (target.Length == 0)
        {
            return null;
        }

        var full = Path.GetFullPath(target, workspacePath);
        return Directory.Exists(full) ? full : null;
    }

    /// <summary>A linked worktree keeps its own HEAD but shares the config of the main repository, named in its commondir file.</summary>
    private static string ResolveCommonDir(string gitDir)
    {
        var line = FirstLine(ReadText(Path.Combine(gitDir, "commondir")));
        if (!string.IsNullOrEmpty(line))
        {
            var full = Path.GetFullPath(line, gitDir);
            if (Directory.Exists(full))
            {
                return full;
            }
        }

        return gitDir;
    }

    private static string? ReadBranch(string gitDir)
    {
        var line = FirstLine(ReadText(Path.Combine(gitDir, "HEAD")));
        if (line is null || !line.StartsWith(HeadPrefix, StringComparison.Ordinal))
        {
            return null;
        }

        var branch = line[HeadPrefix.Length..].Trim();
        return branch.Length is > 0 and <= MaxBranchLength && !branch.Any(char.IsControl) ? branch : null;
    }

    private static string? FirstLine(string? text)
    {
        if (text is null)
        {
            return null;
        }

        var end = text.IndexOfAny(['\r', '\n']);
        return (end < 0 ? text : text[..end]).Trim();
    }

    /// <summary>The text of a small file, or null when it is missing or larger than any real git file.</summary>
    private static string? ReadText(string path)
    {
        var info = new FileInfo(path);
        return info.Exists && info.Length <= MaxFileBytes ? File.ReadAllText(path, Encoding.UTF8) : null;
    }

    /// <summary>The GitHub repository of the remote called origin; when origin is not on GitHub, the first remote that is.</summary>
    private static bool TryFindRemote(string config, out string owner, out string name)
    {
        var urls = new List<(string Remote, string Url)>();
        string? remote = null;
        foreach (var raw in config.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] is '#' or ';')
            {
                continue;
            }

            if (line[0] == '[')
            {
                var header = RemoteSection().Match(line);
                remote = header.Success ? header.Groups["name"].Value : null;
                continue;
            }

            var equals = line.IndexOf('=');
            if (remote is null || equals <= 0 || !line[..equals].Trim().Equals("url", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var value = CleanValue(line[(equals + 1)..]);
            if (value.Length > 0)
            {
                urls.Add((remote, value));
            }
        }

        foreach (var (_, url) in urls.OrderBy(entry => entry.Remote == "origin" ? 0 : 1))
        {
            if (TryParseGitHubUrl(url, out owner, out name))
            {
                return true;
            }
        }

        owner = string.Empty;
        name = string.Empty;
        return false;
    }

    /// <summary>Removes the quotes around a value and a trailing comment that starts after a space.</summary>
    private static string CleanValue(string text)
    {
        var value = text.Trim();
        if (value.Length >= 2 && value[0] == '"')
        {
            var close = value.IndexOf('"', 1);
            return close > 0 ? value[1..close] : value[1..];
        }

        for (var i = 1; i < value.Length; i++)
        {
            if (value[i] is '#' or ';' && char.IsWhiteSpace(value[i - 1]))
            {
                return value[..i].TrimEnd();
            }
        }

        return value;
    }

    /// <summary>The host of an address authority: no user name, password or port.</summary>
    private static string HostOf(string authority)
    {
        var host = authority;
        var at = host.LastIndexOf('@');
        if (at >= 0)
        {
            host = host[(at + 1)..];
        }

        var colon = host.LastIndexOf(':');
        return (colon >= 0 ? host[..colon] : host).Trim();
    }

    private static bool IsGitHubHost(string host) =>
        host.Equals("github.com", StringComparison.OrdinalIgnoreCase) || host.Equals("www.github.com", StringComparison.OrdinalIgnoreCase);

    private static bool TryParsePath(string path, out string owner, out string name)
    {
        owner = string.Empty;
        name = string.Empty;
        var trimmed = path.Trim().Trim('/');
        if (trimmed.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = trimmed[..^4].TrimEnd('/');
        }

        var parts = trimmed.Split('/');
        if (parts.Length != 2 || !IsValidSegment(parts[0]) || !IsValidSegment(parts[1]))
        {
            return false;
        }

        owner = parts[0];
        name = parts[1];
        return true;
    }

    private static bool IsValidSegment(string segment) =>
        segment.Length is > 0 and <= MaxSegmentLength
        && segment is not "." and not ".."
        && segment.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.');

    [GeneratedRegex("^\\[\\s*remote\\s+\"(?<name>[^\"]+)\"\\s*\\]", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 200)]
    private static partial Regex RemoteSection();
}
