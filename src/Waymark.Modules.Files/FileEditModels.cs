namespace Waymark.Modules.Files;

public enum PatchAction
{
    /// <summary>Replace startLine..endLine with the content.</summary>
    Patch,

    /// <summary>Insert the content after startLine; 0 prepends.</summary>
    Insert,

    /// <summary>Remove startLine..endLine.</summary>
    Delete,

    /// <summary>Add the content at the end of the file.</summary>
    Append,
}

/// <summary>One line edit. Line numbers are 1-based and refer to the file as it was before any edit of the call.</summary>
public sealed record PatchOperation(PatchAction? Action, int StartLine = 0, int EndLine = 0, string? Content = null);
