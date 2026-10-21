using System.Diagnostics.CodeAnalysis;
using System.ComponentModel;
using System.Reflection;
using ModelContextProtocol.Server;

namespace Waymark.Core.Tools;

/// <summary>
/// Lists the tools by reading the [McpServerTool] attributes of the given assemblies once at start-up,
/// so the registry can never disagree with what the MCP server serves.
/// </summary>
public sealed class ToolRegistry : IToolRegistry
{
    private const string ModuleAssemblyPrefix = "Waymark.Modules.";
    /// <summary>The module name of tools that belong to Waymark itself; their guide is the overview of Waymark as a whole.</summary>
    public const string HostModule = "host";

    private readonly Dictionary<string, ToolDescriptor> _byName = new(StringComparer.OrdinalIgnoreCase);

    public ToolRegistry(IEnumerable<Assembly> assemblies)
    {
        ArgumentNullException.ThrowIfNull(assemblies);
        foreach (var assembly in assemblies.Distinct())
        {
            var module = ModuleName(assembly);
            foreach (var type in assembly.GetTypes().Where(t => t.GetCustomAttribute<McpServerToolTypeAttribute>() is not null))
            {
                foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
                {
                    var descriptor = Describe(method, module);
                    if (descriptor is not null && !_byName.TryAdd(descriptor.Name, descriptor))
                    {
                        throw new InvalidOperationException($"Tool name '{descriptor.Name}' is registered twice.");
                    }
                }
            }
        }

        Tools = _byName.Values.OrderBy(t => t.Module, StringComparer.Ordinal).ThenBy(t => t.Name, StringComparer.Ordinal).ToList();
    }

    public IReadOnlyList<ToolDescriptor> Tools { get; }

    public bool TryGet(string name, [NotNullWhen(true)] out ToolDescriptor? descriptor)
    {
        ArgumentNullException.ThrowIfNull(name);
        return _byName.TryGetValue(name, out descriptor);
    }

    private static ToolDescriptor? Describe(MethodInfo method, string module)
    {
        var attribute = method.GetCustomAttribute<McpServerToolAttribute>();
        if (attribute is null)
        {
            return null;
        }

        var name = attribute.Name;
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new InvalidOperationException($"{method.DeclaringType?.FullName}.{method.Name} needs an explicit tool Name.");
        }

        var gate = method.GetCustomAttribute<ToolGateAttribute>()?.Gate ?? ToolGate.Read;
        var chars = (method.GetCustomAttribute<DescriptionAttribute>()?.Description.Length ?? 0)
            + method.GetParameters().Sum(p => p.GetCustomAttribute<DescriptionAttribute>()?.Description.Length ?? 0);
        return new ToolDescriptor(name, module, gate, chars);
    }

    private static string ModuleName(Assembly assembly)
    {
        var name = assembly.GetName().Name ?? string.Empty;
        return name.StartsWith(ModuleAssemblyPrefix, StringComparison.Ordinal)
            ? name[ModuleAssemblyPrefix.Length..].ToLowerInvariant()
            : HostModule;
    }
}
