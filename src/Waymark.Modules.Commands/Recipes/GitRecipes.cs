namespace Waymark.Modules.Commands.Recipes;

/// <summary>
/// Turns a git action into a plan. Every command starts with switches that stop git from running configured helpers (pager, file system monitor,
/// diff and text-conversion drivers), nothing can force-push or delete untracked ignored files, and paths always come after '--'.
/// </summary>
public static class GitRecipes
{
    public static readonly IReadOnlyList<string> Actions =
    [
        "status", "diff", "log", "show", "branches", "stashes", "remotes", "add", "commit", "branch_create", "branch_switch", "stash_push", "stash_pop",
        "restore", "pull", "push", "reset_hard", "clean", "branch_delete",
    ];

    private const int MaxPaths = 100;
    private const int MaxMessageChars = 2000;

    public static CommandPlan Plan(string action, RecipeContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var plan = Build(action?.Trim().ToLowerInvariant() ?? string.Empty, context);
        context.Options.EnsureNoUnknown();
        return plan;
    }

    private static CommandPlan Build(string action, RecipeContext c) => action switch
    {
        "status" => Read(c, ["status", "--short", "--branch"]),
        "diff" => Diff(c),
        "log" => Log(c),
        "show" => Show(c),
        "branches" => Read(c, ["branch", "--all", "-vv"]),
        "stashes" => Read(c, ["stash", "list"]),
        "remotes" => Read(c, ["remote", "-v"]),
        "add" => Add(c),
        "commit" => Commit(c),
        "branch_create" => BranchCreate(c),
        "branch_switch" => BranchSwitch(c),
        "stash_push" => StashPush(c),
        "stash_pop" => Write(c, "git.stash", ["stash", "pop"], 5),
        "restore" => Restore(c),
        "pull" => Pull(c),
        "push" => Push(c),
        "reset_hard" => ResetHard(c),
        "clean" => Clean(c),
        "branch_delete" => BranchDelete(c),
        _ => throw new ArgumentException($"Unknown git action '{action}'. Actions: {string.Join(", ", Actions)}."),
    };

    private static CommandPlan Diff(RecipeContext c)
    {
        var args = new List<string> { "diff", "--no-ext-diff", "--no-textconv" };
        if (c.Options.Flag("staged"))
        {
            args.Add("--cached");
        }

        if (c.Options.Flag("stat"))
        {
            args.Add("--stat");
        }

        var revision = Revision(c, "revision");
        if (revision is not null)
        {
            args.Add(revision);
        }

        AddPaths(c, args, required: false);
        return Read(c, args);
    }

    private static CommandPlan Log(RecipeContext c)
    {
        var count = c.Options.Number("count", 1, 200) ?? 20;
        var args = new List<string> { "log", "--no-ext-diff", "--no-textconv", "-n", count.ToString(System.Globalization.CultureInfo.InvariantCulture) };
        if (c.Options.Flag("oneline", fallback: true))
        {
            args.Add("--oneline");
            args.Add("--decorate");
        }

        if (c.Options.Flag("stat"))
        {
            args.Add("--stat");
        }

        var revision = Revision(c, "revision");
        if (revision is not null)
        {
            args.Add(revision);
        }

        AddPaths(c, args, required: false);
        return Read(c, args);
    }

    private static CommandPlan Show(RecipeContext c)
    {
        var revision = Revision(c, "revision") ?? throw new ArgumentException("Option 'revision' is required, for example HEAD or a commit id.");
        var args = new List<string> { "show", "--no-ext-diff", "--no-textconv" };
        if (c.Options.Flag("stat"))
        {
            args.Add("--stat");
        }

        args.Add(revision);
        return Read(c, args);
    }

    private static CommandPlan Add(RecipeContext c)
    {
        var all = c.Options.Flag("all");
        var args = new List<string> { "add" };
        if (all)
        {
            if (c.Options.List("paths", MaxPaths).Count > 0)
            {
                throw new ArgumentException("Give either 'all' or 'paths', not both.");
            }

            args.Add("--all");
        }
        else
        {
            AddPaths(c, args, required: true);
        }

        return Write(c, "git.add", args, 2);
    }

    private static CommandPlan Commit(RecipeContext c)
    {
        var message = c.Options.RequiredText("message", MaxMessageChars, multiline: true);
        var args = new List<string> { "commit", "-m", message };
        if (c.Options.Flag("all"))
        {
            args.Add("--all");
        }

        return Write(c, "git.commit", args, 5);
    }

    private static CommandPlan BranchCreate(RecipeContext c)
    {
        var name = Branch(c, "name", required: true)!;
        var from = Revision(c, "from");
        var args = new List<string>();
        if (c.Options.Flag("switchTo"))
        {
            args.AddRange(["switch", "-c", name]);
        }
        else
        {
            args.AddRange(["branch", name]);
        }
        if (from is not null)
        {
            args.Add(from);
        }

        return Write(c, "git.branch", args, 2);
    }

    private static CommandPlan BranchSwitch(RecipeContext c) =>
        Write(c, "git.branch", ["switch", Branch(c, "name", required: true)!], 2);

    private static CommandPlan StashPush(RecipeContext c)
    {
        var args = new List<string> { "stash", "push" };
        var message = c.Options.Text("message", 200);
        if (message is not null)
        {
            args.Add("-m");
            args.Add(message);
        }

        if (c.Options.Flag("includeUntracked"))
        {
            args.Add("--include-untracked");
        }

        return Write(c, "git.stash", args, 2);
    }

    private static CommandPlan Restore(RecipeContext c)
    {
        var args = new List<string> { "restore" };
        if (c.Options.Flag("staged"))
        {
            args.Add("--staged");
        }

        AddPaths(c, args, required: true);
        return Write(c, "git.restore", args, 2);
    }

    private static CommandPlan Pull(RecipeContext c)
    {
        var args = new List<string> { "pull", "--ff-only" };
        var remote = Remote(c);
        var branch = Branch(c, "branch", required: false);
        if (branch is not null && remote is null)
        {
            throw new ArgumentException("Option 'branch' needs 'remote' as well.");
        }

        if (remote is not null)
        {
            args.Add(remote);
        }

        if (branch is not null)
        {
            args.Add(branch);
        }

        return Write(c, "git.pull", args, 10);
    }

    // There is deliberately no way to force a push or to push a refspec: only a plain branch name is accepted.
    private static CommandPlan Push(RecipeContext c)
    {
        var remote = Remote(c) ?? "origin";
        var branch = Branch(c, "branch", required: false);
        var setUpstream = c.Options.Flag("setUpstream");
        if (setUpstream && branch is null)
        {
            throw new ArgumentException("Option 'setUpstream' needs 'branch'.");
        }

        var args = new List<string> { "push" };
        if (setUpstream)
        {
            args.Add("--set-upstream");
        }

        args.Add(remote);
        if (branch is not null)
        {
            args.Add(branch);
        }

        return Write(c, "git.push", args, 10);
    }

    private static CommandPlan ResetHard(RecipeContext c)
    {
        var revision = Revision(c, "revision") ?? "HEAD";
        return Write(c, "git.reset_hard", ["reset", "--hard", revision], 2);
    }

    // Never -x: ignored files (local settings, build output the user keeps) stay.
    private static CommandPlan Clean(RecipeContext c)
    {
        var args = new List<string> { "clean", "--force", "-d" };
        if (c.Options.Flag("dryRun", fallback: true))
        {
            args.Add("--dry-run");
        }

        AddPaths(c, args, required: false);
        return Write(c, "git.clean", args, 2);
    }

    private static CommandPlan BranchDelete(RecipeContext c) =>
        Write(c, "git.branch_delete", ["branch", "-D", Branch(c, "name", required: true)!], 2);

    private static CommandPlan Read(RecipeContext c, List<string> args)
    {
        var all = Prefix(color: true);
        all.AddRange(args);
        return Make(c, "git.read", all, 2);
    }

    private static CommandPlan Write(RecipeContext c, string operation, List<string> args, int defaultMinutes)
    {
        var all = Prefix(color: false);
        all.AddRange(args);
        return Make(c, operation, all, defaultMinutes);
    }

    private static List<string> Prefix(bool color)
    {
        var prefix = new List<string>
        {
            "--no-pager", "-c", "core.fsmonitor=false", "-c", "core.quotepath=false", "-c", "protocol.ext.allow=never",
        };
        if (color)
        {
            prefix.Add("-c");
            prefix.Add("color.ui=always");
        }

        return prefix;
    }

    private static void AddPaths(RecipeContext c, List<string> args, bool required)
    {
        var paths = c.Options.List("paths", MaxPaths, 400);
        if (paths.Count == 0)
        {
            if (required)
            {
                throw new ArgumentException("Option 'paths' needs at least one path.");
            }

            return;
        }

        args.Add("--");
        foreach (var path in paths)
        {
            args.Add(c.RelativePath(path));
        }
    }

    private static string? Revision(RecipeContext c, string key) =>
        c.Options.Token(key, RecipeRules.GitRevision(), "a commit id, branch, tag or HEAD~1 style name");

    private static string? Remote(RecipeContext c) =>
        c.Options.Token("remote", RecipeRules.GitRemote(), "a remote name such as origin");

    private static string? Branch(RecipeContext c, string key, bool required)
    {
        var name = required ? c.Options.RequiredText(key, 100) : c.Options.Text(key, 100);
        if (name is null)
        {
            return null;
        }

        return RecipeRules.IsSafeBranchName(name)
            ? name
            : throw new ArgumentException($"Option '{key}' is not a plain branch name (letters, digits, '.', '_', '-' and '/', no '..').");
    }

    private static CommandPlan Make(RecipeContext c, string operation, List<string> args, int defaultMinutes)
    {
        var minutes = c.Options.Number("timeoutMinutes", 1, 30) ?? defaultMinutes;
        return new CommandPlan(operation, "git", args, TimeSpan.FromMinutes(minutes));
    }
}
