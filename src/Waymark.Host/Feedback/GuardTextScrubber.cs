using Waymark.Host.Mcp;

namespace Waymark.Host.Feedback;

/// <summary>The same hiding tool results get (vault values, then values with a recognisable shape), reused for feedback text.</summary>
public sealed class GuardTextScrubber(ToolResultGuard guard) : IOutboundTextScrubber
{
    public Task<string> HideKnownSecretsAsync(string text, CancellationToken ct) => guard.CleanTextAsync(text, ct);
}
