namespace Waymark.Core.Guides;

/// <summary>
/// The guides agents can ask for: one per module that has tools, plus the overview of Waymark itself (module <c>host</c>).
/// They ship inside Waymark and change with it; only the owner's own text in the overview is editable (see <see cref="IHostPrompt"/>).
/// </summary>
public interface IGuideCatalog
{
    /// <summary>The modules that have a guide, in name order. It includes <c>host</c>, the overview.</summary>
    IReadOnlyList<string> Modules { get; }

    bool Has(string module);

    /// <summary>The guide as agents receive it; null for a module without one.</summary>
    Task<ToolGuide?> GetAsync(string module, CancellationToken ct);

    /// <summary>Only the hash of the guide. Cheap, so the call pipeline can use it on every call; null for a module without a guide.</summary>
    Task<string?> GetHashAsync(string module, CancellationToken ct);

    Task<IReadOnlyList<ToolGuide>> ListAsync(CancellationToken ct);
}
