using Waymark.Core.Security;
using Waymark.Core.Workspaces;

namespace Waymark.Modules.Files;

/// <summary>A source file as it was when the call began: its bytes, its format and its decoded text.</summary>
internal sealed record SourceSnapshot(Workspace Workspace, GuardedPath Path, byte[] Bytes, TextFileFormat Format, string Text);

/// <summary>Text taken from a source, after the requested transforms. Lines are 1-based and inclusive.</summary>
internal sealed record CopyPiece(string Text, SourceSnapshot Source, int StartLine, int EndLine, bool Remove, string Summary);

/// <summary>A file whose new bytes are ready to be written; the original bytes are kept so a failed multi-file write can be undone.</summary>
internal sealed record PendingFile(Workspace Workspace, GuardedPath Path, byte[] Original, byte[] Updated);

/// <summary>The files read during one tool call, so a batch sees every source in one consistent state and reads each file once.</summary>
internal sealed class CopySession
{
    private static readonly StringComparer PathComparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    public Dictionary<string, SourceSnapshot> Snapshots { get; } = new(PathComparer);

    public static bool SamePath(string a, string b) => PathComparer.Equals(a, b);

    public static IEqualityComparer<string> Comparer => PathComparer;
}
