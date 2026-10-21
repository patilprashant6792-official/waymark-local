namespace Waymark.Modules.Files;

/// <summary>One file to move or rename.</summary>
public sealed record MoveEntry(string? From, string? To);

/// <summary>Outcome for one path of a move, delete or create operation. Note carries a harmless remark, for example the new location.</summary>
public sealed record FileOperationResult(string Path, bool Success, string? Error = null, string? Note = null)
{
    public static FileOperationResult Ok(string path, string? note = null) => new(path, true, null, note);

    public static FileOperationResult Failure(string path, string error) => new(path, false, error);
}

/// <summary>A request that is refused for a reason the AI client can act on. The message is safe to return.</summary>
internal sealed class FileOperationException(string message) : Exception(message);
