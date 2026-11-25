using Waymark.Core.Calls;
using Waymark.Core.Memory;

namespace Waymark.Host.Mcp;

/// <summary>
/// The caller of the tool call that is running on this async flow. <see cref="ToolPipeline"/> sets it before the tool runs and clears it after,
/// so a service deep inside a tool knows who called without trusting an argument. Outside a call it is 'unknown'.
/// </summary>
public sealed class CallerContext : ICallerContext
{
    public const string Unknown = "unknown";
    public const string StdioPrefix = "stdio:";

    private static readonly AsyncLocal<string?> Current = new();

    public string Client => Current.Value ?? Unknown;

    public NoteAuthor AsModelAuthor() => NoteAuthor.Of(NoteAuthorKind.Agent, Client);

    /// <summary>Sets the caller for this async flow and what it awaits; dispose the result to clear it again.</summary>
    public IDisposable Enter(string client)
    {
        var previous = Current.Value;
        Current.Value = string.IsNullOrWhiteSpace(client) ? null : client;
        return new Scope(previous);
    }

    /// <summary>
    /// Who calls: the OAuth client id from the bearer token (HTTP); else, over stdio, the name the client gave when it connected, as 'stdio:&lt;name&gt;'; else 'unknown'.
    /// </summary>
    /// <param name="stdioClientName">The client's name from the MCP initialize handshake (<c>McpServer.ClientInfo.Name</c>), when there is one.</param>
    public static string ClientOf(string? claimClientId, string? stdioClientName)
    {
        if (!string.IsNullOrWhiteSpace(claimClientId))
        {
            return claimClientId;
        }

        return string.IsNullOrWhiteSpace(stdioClientName) ? Unknown : StdioPrefix + stdioClientName.Trim();
    }

    private sealed class Scope(string? previous) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                Current.Value = previous;
            }
        }
    }
}
