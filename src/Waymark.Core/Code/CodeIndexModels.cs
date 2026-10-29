namespace Waymark.Core.Code;

/// <summary>The language of an indexed file. Stored as its lower-case name; the CHECK on code_files.language mirrors it.</summary>
public enum CodeLanguage
{
    CSharp,
    Python,

    /// <summary>Reserved: the schema accepts it, no indexer exists yet.</summary>
    TypeScript,

    /// <summary>Reserved: the schema accepts it, no indexer exists yet.</summary>
    JavaScript
}

public enum CodeTypeKind
{
    Class,
    Interface,
    Struct,
    Record,
    RecordStruct,
    Enum,
    Delegate,
    TypeAlias,

    /// <summary>A TypeScript namespace or module block: it groups declarations and has no instances.</summary>
    Namespace
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
    EnumMember,

    /// <summary>A function that is not inside a type (a module-level Python function).</summary>
    Function,

    /// <summary>A variable that is not inside a type (a module-level Python assignment).</summary>
    Variable
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

/// <summary>Everything the index keeps about one source file. Replaced as a unit.</summary>
/// <param name="Usings">The using directives of a C# file, or the import statements of a Python file, one text per entry.</param>
/// <param name="Language">Which indexer produced the entry. C# when not said, so older callers keep working.</param>
public sealed record CodeFileIndex(
    CodeFileStamp Stamp,
    int LineCount,
    string Classification,
    IReadOnlyList<string> Usings,
    IReadOnlyList<CodeTypeEntry> Types,
    IReadOnlyList<CodeMemberEntry> Members,
    IReadOnlyList<CodeCallSiteEntry> CallSites,
    CodeLanguage Language = CodeLanguage.CSharp);

/// <param name="FilesByLanguage">Indexed files per language; null when the store did not count them.</param>
public sealed record CodeIndexStats(
    int Files,
    int Types,
    int Members,
    int CallSites,
    IReadOnlyDictionary<CodeLanguage, int>? FilesByLanguage = null);
