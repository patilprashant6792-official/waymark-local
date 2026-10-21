namespace Waymark.Core.Security;

/// <summary>Thrown when a path is refused by the workspace boundary or the file access policy. The reason is safe to show to the AI client.</summary>
public sealed class FileAccessDeniedException : UnauthorizedAccessException
{
    public FileAccessDeniedException(string path, string reason)
        : base($"Access denied to '{path}': {reason}")
    {
        Path = path;
        Reason = reason;
    }

    public string Path { get; }

    public string Reason { get; }
}
