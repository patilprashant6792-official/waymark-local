using Waymark.Core.Commands;

namespace Waymark.Host.Ui;

/// <summary>
/// The groups of command operations as the owner knows them: three language toolchains (.NET, Python, Node.js) and the three that work on any project (git,
/// processes, the system shell). One list, so the Commands page and the Safety page name and order them the same way.
/// </summary>
public static class Toolchains
{
    /// <summary>The value of the picker that shows everything.</summary>
    public const string All = "all";

    public sealed record Toolchain(string Key, string Title, string Hint, bool IsLanguage);

    public static IReadOnlyList<Toolchain> Ordered { get; } =
    [
        new(CommandCatalog.DotnetFamily, ".NET", "build, test, EF Core, NuGet", true),
        new(CommandCatalog.PythonFamily, "Python", "uv and pip installs, pytest, ruff, mypy, pyright", true),
        new(CommandCatalog.NodeFamily, "Node.js", "npm installs, tsc, package.json scripts", true),
        new(CommandCatalog.GitFamily, "Git", "status, commit, push", false),
        new(CommandCatalog.ProcessFamily, "Processes and ports", "ports, stop a program, clean build output", false),
        new(CommandCatalog.ShellFamily, "System shell", "any script, always approved by you", false),
    ];

    /// <summary>The toolchain of a picker value or family key; null for <see cref="All"/>, empty or an unknown word.</summary>
    public static Toolchain? Find(string? key) =>
        string.IsNullOrWhiteSpace(key) ? null : Ordered.FirstOrDefault(t => string.Equals(t.Key, key.Trim(), StringComparison.OrdinalIgnoreCase));

    public static string TitleOf(string family) => Find(family)?.Title ?? family;

    public static IReadOnlyList<OperationInfo> OperationsOf(string family) =>
        CommandCatalog.Operations.Where(o => o.Family == family).ToList();
}
