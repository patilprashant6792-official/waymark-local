namespace Waymark.Core.Tools;

/// <summary>What a tool can do: Read tools only read, Confirm tools change files or run commands. The admin UI warns before switching Confirm tools on.</summary>
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
    /// <summary>Every tool starts on, so a new install shows the full set; the owner switches off what they do not want.</summary>
    public static bool IsOnByDefault(ToolDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        return true;
    }
}

