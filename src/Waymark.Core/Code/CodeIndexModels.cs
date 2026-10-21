namespace Waymark.Core.Code;

public enum CodeTypeKind
{
    Class,
    Interface,
    Struct,
    Record,
    RecordStruct,
    Enum,
    Delegate
}

public enum CodeMemberKind
{
    Method,
    Constructor,
    Property,
    Field,
    Event,
    Indexer,
    Operator,
    EnumMember
}

/// <summary>What the indexer saw on disk for one file; used to decide whether the file must be parsed again.</summary>
public sealed record CodeFileStamp(string RelativePath, string Hash, long Size, DateTimeOffset ModifiedUtc);

public sealed record CodeTypeEntry(
    CodeTypeKind Kind,
    string Name,
    string FullName,
    string? Namespace,
    string? BaseList,
    string? Modifiers,
    string? Attributes,
    string? DocSummary,
    int StartLine,
    int EndLine);

/// <param name="TypeIndex">Position of the owning type in <see cref="CodeFileIndex.Types"/>; null for a member outside any type.</param>
public sealed record CodeMemberEntry(
    int? TypeIndex,
    CodeMemberKind Kind,
    string Name,
    string Signature,
    string? ReturnType,
    string? Modifiers,
    string? Attributes,
    string? DocSummary,
    int StartLine,
    int EndLine);

/// <summary>
/// An invocation found by name; the callee is not resolved to a symbol. <paramref name="ReceiverType"/> is the simple name
/// of the receiver's declared type when this file alone tells it, null when it is unknown.
/// </summary>
/// <param name="ReceiverTypeGuessed">True when the type is only a guess: a capitalised name that no declaration explains, taken for a static type.</param>
public sealed record CodeCallSiteEntry(
    string? CallerType,
    string? CallerMember,
    string CalleeName,
    string? Receiver,
    int Line,
    string? ReceiverType = null,
    bool ReceiverTypeGuessed = false);

/// <summary>Everything the index keeps about one C# file. Replaced as a unit.</summary>
public sealed record CodeFileIndex(
    CodeFileStamp Stamp,
    int LineCount,
    string Classification,
    IReadOnlyList<string> Usings,
    IReadOnlyList<CodeTypeEntry> Types,
    IReadOnlyList<CodeMemberEntry> Members,
    IReadOnlyList<CodeCallSiteEntry> CallSites);

public sealed record CodeIndexStats(int Files, int Types, int Members, int CallSites);
