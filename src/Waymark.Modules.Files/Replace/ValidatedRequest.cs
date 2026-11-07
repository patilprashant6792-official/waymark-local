using Waymark.Core.Text;

namespace Waymark.Modules.Files.Replace;

/// <summary>The arguments after every check, normalised. Signature is what a plan remembers so an apply can be compared with its preview.</summary>
internal sealed record ValidatedRequest(
    string Find,
    string With,
    PathGlob? Glob,
    string? GlobText,
    IReadOnlyList<string> Paths,
    ReplaceMode Mode,
    bool CaseSensitive,
    bool WholeWord)
{
    private const char FieldSeparator = '\u001f';
    private const char ItemSeparator = '\u001e';

    public string Signature => string.Join(
        FieldSeparator,
        Mode.ToString(),
        CaseSensitive ? "cs" : "ci",
        WholeWord ? "word" : "any",
        Find,
        With,
        GlobText ?? string.Empty,
        string.Join(ItemSeparator, Paths));
}

/// <summary>Checks the arguments of file_replace before anything is read.</summary>
internal static class ReplaceRequestValidator
{
    /// <exception cref="ArgumentException">An argument is missing, too long, has a line break, or two arguments do not go together.</exception>
    public static ValidatedRequest Validate(ReplaceRequest request, ReplaceOptions options)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(options);

        var find = request.Find;
        if (string.IsNullOrEmpty(find))
        {
            throw new ArgumentException("find is required.");
        }

        CheckText(find, "find", options.MaxFindChars);
        var with = request.With ?? throw new ArgumentException("with is required (an empty string deletes the matches).");
        CheckText(with, "with", options.MaxWithChars);
        if (string.Equals(find, with, StringComparison.Ordinal))
        {
            throw new ArgumentException("find and with are the same; there is nothing to replace.");
        }

        if (request.Mode == ReplaceMode.CSharp && !request.CaseSensitive)
        {
            throw new ArgumentException("mode 'csharp' always matches the exact identifier; leave caseSensitive at true.");
        }

        var hasGlob = !string.IsNullOrWhiteSpace(request.PathGlob);
        var hasPaths = request.Paths is { Count: > 0 };
        if (hasGlob == hasPaths)
        {
            throw new ArgumentException("Give exactly one of pathGlob (use '**' for the whole workspace) and pathsJson.");
        }

        if (hasGlob)
        {
            var globText = request.PathGlob!.Trim().Replace('\\', '/');
            var glob = PathGlob.Parse(globText) ?? throw new ArgumentException("pathGlob selects nothing; use '**' for the whole workspace.");
            return new ValidatedRequest(find, with, glob, globText, [], request.Mode, request.CaseSensitive, request.WholeWord);
        }

        var paths = NormalizePaths(request.Paths!, options.MaxFiles);
        return new ValidatedRequest(find, with, null, null, paths, request.Mode, request.CaseSensitive, request.WholeWord);
    }

    private static void CheckText(string value, string name, int maxChars)
    {
        if (value.Length > maxChars)
        {
            throw new ArgumentException($"{name} is longer than {maxChars} characters.");
        }

        if (value.Contains('\n') || value.Contains('\r'))
        {
            throw new ArgumentException($"{name} must be a single line; file_replace matches within one line.");
        }
    }

    private static List<string> NormalizePaths(IReadOnlyList<string> paths, int maxFiles)
    {
        if (paths.Count > maxFiles)
        {
            throw new ArgumentException($"pathsJson names {paths.Count} files; at most {maxFiles} can be changed in one call.");
        }

        var normalized = new List<string>(paths.Count);
        foreach (var path in paths)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                throw new ArgumentException("pathsJson holds an empty path.");
            }

            normalized.Add(path.Trim().Replace('\\', '/').TrimStart('/'));
        }

        normalized.Sort(StringComparer.OrdinalIgnoreCase);
        return normalized;
    }
}
