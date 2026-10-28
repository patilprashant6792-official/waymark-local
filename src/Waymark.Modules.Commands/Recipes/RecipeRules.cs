using System.Text.RegularExpressions;

namespace Waymark.Modules.Commands.Recipes;

/// <summary>The allow-list patterns for every free-text value that ends up in a command. A value that does not match is refused before anything starts.</summary>
public static partial class RecipeRules
{
    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._-]{0,99}$", RegexOptions.None, 100)]
    public static partial Regex PackageId();

    [GeneratedRegex(@"^[0-9][0-9A-Za-z.+-]{0,49}$", RegexOptions.None, 100)]
    public static partial Regex PackageVersion();

    [GeneratedRegex(@"^[A-Za-z][A-Za-z0-9_]{0,31}$", RegexOptions.None, 100)]
    public static partial Regex Configuration();

    [GeneratedRegex(@"^[A-Za-z][A-Za-z0-9.\-]{1,39}$", RegexOptions.None, 100)]
    public static partial Regex Framework();

    [GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_.\-]{0,99}$", RegexOptions.None, 100)]
    public static partial Regex ProjectName();

    [GeneratedRegex(@"^[A-Za-z0-9_.,()=!~|&<> \-]{1,200}$", RegexOptions.None, 100)]
    public static partial Regex TestFilter();

    [GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_]{0,99}$", RegexOptions.None, 100)]
    public static partial Regex MigrationName();

    [GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_.]{0,99}$", RegexOptions.None, 100)]
    public static partial Regex ContextName();

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9 ._\-]{0,49}$", RegexOptions.None, 100)]
    public static partial Regex LaunchProfile();

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._/\-]{0,99}$", RegexOptions.None, 100)]
    public static partial Regex GitBranch();

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._/\-~^@{}]{0,99}$", RegexOptions.None, 100)]
    public static partial Regex GitRevision();

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._-]{0,49}$", RegexOptions.None, 100)]
    public static partial Regex GitRemote();

    /// <summary>An exact Python version of a package (PEP 440): digits, letters, '.', '+', '!', '_' and '-'; it must start with a digit so it can never be read as an option.</summary>
    [GeneratedRegex(@"^[0-9][0-9A-Za-z.+!_-]{0,49}$", RegexOptions.None, 100)]
    public static partial Regex PythonVersion();

    /// <summary>A dependency group or extra name.</summary>
    [GeneratedRegex(@"^[A-Za-z][A-Za-z0-9._-]{0,49}$", RegexOptions.None, 100)]
    public static partial Regex PythonGroup();

    /// <summary>A dotted module name for python -m.</summary>
    [GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_]{0,49}(\.[A-Za-z_][A-Za-z0-9_]{0,49}){0,9}$", RegexOptions.None, 100)]
    public static partial Regex PythonModule();

    /// <summary>The expression of pytest -k or -m: names, 'and', 'or', 'not', parentheses and brackets. It cannot start with '-'.</summary>
    [GeneratedRegex(@"^[A-Za-z0-9_(][A-Za-z0-9_.()\[\] \-]{0,199}$", RegexOptions.None, 100)]
    public static partial Regex PytestExpression();

    /// <summary>The part of a pytest node id after the file: <c>TestClass::test_name[param]</c>.</summary>
    [GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_\[\]\-.,=+: ]{0,199}$", RegexOptions.None, 100)]
    public static partial Regex PytestNodeId();

    /// <summary>The name of the folder of a virtual environment, one folder below the project.</summary>
    [GeneratedRegex(@"^[A-Za-z0-9._-]{1,50}$", RegexOptions.None, 100)]
    public static partial Regex FolderName();

    /// <summary>A Python version request for uv venv: 3, 3.12 or 3.12.4.</summary>
    [GeneratedRegex(@"^3(\.[0-9]{1,2}){0,2}$", RegexOptions.None, 100)]
    public static partial Regex PythonVersionRequest();

    /// <summary>The names git itself refuses, which also cover the ones that would be read as options or paths.</summary>
    public static bool IsSafeBranchName(string name) =>
        GitBranch().IsMatch(name)
        && !name.Contains("..", StringComparison.Ordinal)
        && !name.Contains("//", StringComparison.Ordinal)
        && !name.EndsWith('/')
        && !name.EndsWith('.')
        && !name.EndsWith(".lock", StringComparison.OrdinalIgnoreCase);
}
