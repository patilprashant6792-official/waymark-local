namespace Waymark.Core.Tools;

/// <summary>How a tool is treated by default: Read tools run freely, Confirm tools change files and start switched off.</summary>
public enum ToolGate
{
    Read,
    Confirm
}

/// <summary>Marks a tool method with its gate. A tool without this attribute is a Read tool.</summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
public sealed class ToolGateAttribute(ToolGate gate) : Attribute
{
    public ToolGate Gate { get; } = gate;
}

/// <summary>One registered tool as the admin UI and the call pipeline see it.</summary>
public sealed record ToolDescriptor(string Name, string Module, ToolGate Gate, int DescriptionChars);

/// <summary>The one place that says what a tool does before the owner has set its switch.</summary>
public static class ToolDefaults
{
    /// <summary>Read tools start on; Confirm tools (they change files) start off until the owner switches them on.</summary>
    public static bool IsOnByDefault(ToolDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        return descriptor.Gate == ToolGate.Read;
    }
}

