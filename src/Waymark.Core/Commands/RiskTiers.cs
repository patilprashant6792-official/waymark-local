namespace Waymark.Core.Commands;

/// <summary>What an operation can do to the computer and the data on it, from the safest to the furthest reaching.</summary>
public enum RiskTier
{
    /// <summary>Only reads.</summary>
    Look,

    /// <summary>Changes files, git state or processes in the workspace.</summary>
    Change,

    /// <summary>Starts code: a build, a test, the app or a script. That code runs with the owner's user rights.</summary>
    RunsCode,

    /// <summary>Sends data to, or fetches code from, another computer.</summary>
    SendsOut
}

/// <summary>The one place that says how risky an operation is. The Commands page, the approval card and the Safety page all read it.</summary>
public static class RiskTiers
{
    private static readonly HashSet<string> NetworkOperations = new(StringComparer.Ordinal)
    {
        "git.push", "git.pull", "dotnet.tool_restore", "dotnet.package_add",
        "python.install", "python.package_add", "python.venv",
    };

    // These start the project's own code: tests, the app, EF design-time code. That code can be code Claude has just written, and no file
    // fingerprint covers it, so these operations ask by default.
    private static readonly HashSet<string> ProjectCodeOperations = new(StringComparer.Ordinal)
    {
        "dotnet.test", "dotnet.run", "dotnet.ef_read", "dotnet.ef_migrations_add", "dotnet.ef_migrations_remove", "dotnet.ef_database_update",
        "python.test", "python.run", "python.typecheck", "python.list",
    };

    public static RiskTier Of(OperationInfo operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        if (operation.Key == CommandCatalog.ShellRun
            || ((operation.Family == CommandCatalog.DotnetFamily || operation.Family == CommandCatalog.PythonFamily) && operation.DriftSensitive))
        {
            return RiskTier.RunsCode;
        }

        if (NetworkOperations.Contains(operation.Key))
        {
            return RiskTier.SendsOut;
        }

        return operation.Mutating ? RiskTier.Change : RiskTier.Look;
    }

    /// <summary>True when the operation starts the project's own code (not just the build tooling).</summary>
    public static bool RunsProjectCode(OperationInfo operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        return ProjectCodeOperations.Contains(operation.Key) || operation.Key == CommandCatalog.ShellRun;
    }

    public static string Label(RiskTier tier) => tier switch
    {
        RiskTier.Look => "Reads",
        RiskTier.Change => "Changes",
        RiskTier.RunsCode => "Runs code",
        RiskTier.SendsOut => "Sends out",
        _ => throw new ArgumentOutOfRangeException(nameof(tier), tier, "Unknown risk tier.")
    };

    public static string Hint(RiskTier tier) => tier switch
    {
        RiskTier.Look => "Only reads. What it reads can reach Claude.",
        RiskTier.Change => "Changes files, git state or processes in your workspace.",
        RiskTier.RunsCode => "Starts code that runs with your user rights: it can read your keys and reach the internet.",
        RiskTier.SendsOut => "Sends data to, or fetches code from, another computer.",
        _ => throw new ArgumentOutOfRangeException(nameof(tier), tier, "Unknown risk tier.")
    };
}
