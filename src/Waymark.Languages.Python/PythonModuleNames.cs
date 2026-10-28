namespace Waymark.Languages.Python;

/// <summary>The dotted module name of a Python file, read from its path.</summary>
public static class PythonModuleNames
{
    // A folder that only holds the code and is not part of the import name (the "src layout").
    private const string SourceRoot = "src";

    /// <summary>
    /// <c>app/models/user.py</c> is <c>app.models.user</c>; <c>app/models/__init__.py</c> is <c>app.models</c>; a leading <c>src</c> folder is dropped.
    /// The result is empty for a file directly in the root named <c>__init__.py</c>.
    /// </summary>
    public static string FromPath(string relativePath)
    {
        ArgumentNullException.ThrowIfNull(relativePath);
        var parts = relativePath.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries).ToList();
        if (parts.Count == 0)
        {
            return string.Empty;
        }

        var file = parts[^1];
        parts.RemoveAt(parts.Count - 1);
        var dot = file.LastIndexOf('.');
        var stem = dot > 0 ? file[..dot] : file;
        if (!string.Equals(stem, "__init__", StringComparison.Ordinal))
        {
            parts.Add(stem);
        }

        if (parts.Count > 1 && string.Equals(parts[0], SourceRoot, StringComparison.OrdinalIgnoreCase))
        {
            parts.RemoveAt(0);
        }

        return string.Join('.', parts);
    }
}
