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

/// <summary>
/// Marks a tool as internal: always on, never listed on the Tools page and never switched off by a saved row or a profile.
/// Reserved for the way in (waymark_guide), which every client needs before any other call.
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
public sealed class InternalToolAttribute : Attribute;

/// <summary>One registered tool as the admin UI and the call pipeline see it.</summary>
/// <param name="Internal">Always on and hidden from the owner's tool switches; see <see cref="InternalToolAttribute"/>.</param>
public sealed record ToolDescriptor(string Name, string Module, ToolGate Gate, int DescriptionChars, bool Internal = false);

/// <summary>The one place that says whether a tool is on.</summary>
public static class ToolDefaults
{
    /// <summary>Every tool starts on, so a new install shows the full set; the owner switches off what they do not want.</summary>
    public static bool IsOnByDefault(ToolDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        return true;
    }

    /// <summary>An internal tool is always on, whatever was saved; otherwise the owner's saved switch wins, and without one the default applies.</summary>
    public static bool IsEnabled(ToolDescriptor descriptor, bool? savedSwitch)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        return descriptor.Internal || (savedSwitch ?? IsOnByDefault(descriptor));
    }
}

