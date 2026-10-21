namespace Waymark.Core.Audit;

/// <summary>How many tool calls there were, and the estimated tokens their output added to the AI's context.</summary>
public sealed record ToolUsage(int Calls, long Tokens);

public sealed record ToolUsageByTool(string Tool, ToolUsage Usage);

/// <summary>
/// A rough token count for what a tool sent to the AI. Plain text is about four bytes per token, and the audit trail keeps the size
/// in bytes. Each call is rounded up on its own, so the figures shown for single calls add up to the totals.
/// </summary>
public static class TokenEstimate
{
    public const int BytesPerToken = 4;

    public static long FromBytes(long? bytes) =>
        bytes is > 0 ? (bytes.Value + BytesPerToken - 1) / BytesPerToken : 0;
}
