namespace Waymark.Modules.Files;

public enum WriteMode
{
    /// <summary>Fails if the file already exists.</summary>
    Create,

    /// <summary>Fails if the file does not exist.</summary>
    Overwrite,

    /// <summary>Creates or replaces the file.</summary>
    Upsert,
}

/// <summary>One file to write. Give Content (an empty string makes an empty file) or From (a copy of another file or of lines of it), never both.</summary>
public sealed record WriteFileEntry(string? RelativeFilePath, string? Content, WriteMode Mode = WriteMode.Upsert, CopySource? From = null);

/// <summary>Outcome for one file. LinesAffected is set by line edits: lines replaced, removed or added. Note describes copies (where text came from, what was renamed).</summary>
public sealed record FileWriteResult(
    string RelativeFilePath,
    bool Success,
    string? Error,
    int? LineCount,
    long? SizeBytes,
    DateTimeOffset? ModifiedUtc,
    int? LinesAffected = null,
    string? Note = null)
{
    public static FileWriteResult Failure(string path, string error) => new(path, false, error, null, null, null);
}
