namespace Waymark.Modules.Code.Skeleton;

/// <summary>What one code_skeleton call asks for.</summary>
/// <param name="FolderPath">Folder to show, relative to the workspace root; null, empty or '.' is the root.</param>
/// <param name="Depth">Levels below the folder to show; null fills the overview budget breadth-first.</param>
/// <param name="Page">Page of the folder's own entries when it holds more than SkeletonOptions.MaxChildrenShown; 1-based.</param>
public sealed record SkeletonRequest(string? FolderPath, int? Depth, int Page)
{
    public static SkeletonRequest Overview { get; } = new(null, null, 1);

    /// <summary>True when no depth was asked for: the tree is sized by the overview budget.</summary>
    public bool IsOverview => Depth is null;

    /// <exception cref="ArgumentException">The depth or the page is out of range.</exception>
    public void Validate(SkeletonOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        // No parameter names: the messages go to the AI client as they are, and '(Parameter ...)' would only add noise.
        if (Depth is { } depth && (depth < 1 || depth > options.MaxDepth))
        {
            throw new ArgumentException(
                $"depth must be between 1 and {options.MaxDepth}. Leave it out for the overview, which opens folders until its size budget is used.");
        }

        if (Page < 1)
        {
            throw new ArgumentException("page must be 1 or more.");
        }
    }
}
