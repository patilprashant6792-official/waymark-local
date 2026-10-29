namespace Waymark.Core.Code;

/// <summary>The values stored in <see cref="CodeFileIndex.Classification"/>. One value per file; the first matching rule in this order wins.</summary>
public static class CodeFileClassification
{
    /// <summary>A UI component file: a .tsx or .jsx file that declares exported PascalCase functions or classes.</summary>
    public const string Component = "component";
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

    /// <summary>Test code: a test file name or folder (<c>test_x.py</c>, <c>tests/</c>, <c>conftest.py</c>).</summary>
    public const string Tests = "tests";

    /// <summary>Type declarations only, no code (a Python <c>.pyi</c> stub).</summary>
    public const string Declarations = "declarations";

    /// <summary>Settings of a project or an application (<c>settings.py</c>, <c>config.py</c>).</summary>
    public const string Configuration = "configuration";

    /// <summary>Any other source file.</summary>
    public const string Source = "source";
}
