namespace Waymark.Languages.TypeScript;

/// <summary>The module name of a TypeScript or JavaScript file: its path without the extension, the way an import names it.</summary>
public static class TypeScriptModuleNames
{
    private static readonly string[] DeclarationSuffixes = [".d.ts", ".d.mts", ".d.cts"];

    /// <summary>
    /// <c>src/app/user.service.ts</c> is <c>app/user.service</c>; <c>components/Button/index.tsx</c> is <c>components/Button</c>; <c>types/api.d.ts</c> is <c>types/api</c>.
    /// A leading <c>src/</c> is dropped.
    /// </summary>
    public static string FromPath(string relativePath)
    {
        ArgumentNullException.ThrowIfNull(relativePath);
        var path = relativePath.Replace('\\', '/').Trim('/');
        var stripped = false;
        foreach (var suffix in DeclarationSuffixes)
        {
            if (path.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                path = path[..^suffix.Length];
                stripped = true;
                break;
            }
        }

        if (!stripped)
        {
            var dot = path.LastIndexOf('.');
            if (dot > path.LastIndexOf('/') + 1)
            {
                path = path[..dot];
            }
        }

        if (path.EndsWith("/index", StringComparison.OrdinalIgnoreCase))
        {
            path = path[..^"/index".Length];
        }

        return path.StartsWith("src/", StringComparison.OrdinalIgnoreCase) ? path[4..] : path;
    }
}
