using System.Diagnostics.CodeAnalysis;

namespace Waymark.Core.Tools;

public interface IToolRegistry
{
    /// <summary>Every tool, ordered by module then name.</summary>
    IReadOnlyList<ToolDescriptor> Tools { get; }

    bool TryGet(string name, [NotNullWhen(true)] out ToolDescriptor? descriptor);
}
