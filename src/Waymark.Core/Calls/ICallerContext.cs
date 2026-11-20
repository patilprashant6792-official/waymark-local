using Waymark.Core.Memory;

namespace Waymark.Core.Calls;

/// <summary>
/// Who makes the tool call that is running now, as the server knows it (the OAuth client id, or the stdio client's name), never from the tool's arguments.
/// Outside a tool call it is 'unknown'.
/// </summary>
public interface ICallerContext
{
    string Client { get; }

    /// <summary>The caller as the author of a note: a Claude client with the client id.</summary>
    NoteAuthor AsModelAuthor();
}
