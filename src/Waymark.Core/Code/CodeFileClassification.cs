namespace Waymark.Core.Code;

/// <summary>The values stored in <see cref="CodeFileIndex.Classification"/>. One value per file; the first matching rule in this order wins.</summary>
public static class CodeFileClassification
{
    /// <summary>Declares MCP tools (a type or member carries an McpServerTool attribute).</summary>
    public const string Tools = "tools";

    /// <summary>Configures an MCP server (AddMcpServer, WithTools, MapMcp...).</summary>
    public const string McpConfiguration = "mcp-configuration";

    /// <summary>Has top-level statements (a Program.cs in the modern style).</summary>
    public const string TopLevelStatements = "top-level-statements";

    /// <summary>Registers services with the dependency injection container.</summary>
    public const string DependencyInjection = "dependency-injection";

    /// <summary>Declares an API controller.</summary>
    public const string Controller = "controller";

    /// <summary>Declares only interfaces.</summary>
    public const string Interfaces = "interfaces";

    /// <summary>Any other C# source file.</summary>
    public const string Source = "source";
}
