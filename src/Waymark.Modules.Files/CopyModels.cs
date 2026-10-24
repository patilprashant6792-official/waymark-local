namespace Waymark.Modules.Files;

/// <summary>One literal text replacement applied to copied text. Count is the number of places that must match; leave it out to accept any number of at least one.</summary>
public sealed record ReplaceRule(string? Find, string? With, int? Count = null);

/// <summary>Renames applied to copied C# text. See Waymark.Core.Code.CSharpRewriteMap.</summary>
public sealed record CSharpRenames(Dictionary<string, string>? Namespaces = null, Dictionary<string, string>? Identifiers = null);

/// <summary>
/// Where copied text comes from instead of being typed. Workspace defaults to the workspace of the call; File defaults to the file being edited (file_edit_lines only).
/// Without StartLine and EndLine the whole file is copied. Transforms run in this order: Replace, Csharp, Indent.
/// </summary>
public sealed record CopySource(
    string? Workspace = null,
    string? File = null,
    int StartLine = 0,
    int EndLine = 0,
    string? ExpectFirstLine = null,
    string? ExpectLastLine = null,
    bool Remove = false,
    List<ReplaceRule>? Replace = null,
    CSharpRenames? Csharp = null,
    int Indent = 0);

/// <summary>A literal change to the path of every copied file, applied to the path below the source folder (for example "Legacy/" to "").</summary>
public sealed record PathRename(string? Find, string? With);

/// <summary>
/// Copies many files at once. Every file below Folder (matching Glob, relative to Folder) goes to the same path below ToFolder, after PathRename.
/// Replace, Csharp and Indent apply to every file. ConfirmCount must equal the number of files found (take it from a dryRun).
/// </summary>
public sealed record FolderCopy(
    string? Workspace = null,
    string? Folder = null,
    string? ToFolder = null,
    string? Glob = null,
    List<PathRename>? PathRename = null,
    List<ReplaceRule>? Replace = null,
    CSharpRenames? Csharp = null,
    int Indent = 0,
    WriteMode Mode = WriteMode.Create,
    int? ConfirmCount = null);
