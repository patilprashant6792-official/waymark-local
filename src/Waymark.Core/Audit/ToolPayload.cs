namespace Waymark.Core.Audit;

/// <summary>
/// What one tool call was asked and what it answered, kept for the call's details page. Both texts have been through the secret hiding: the
/// input as the AI sent it, the output as the AI received it (after the size limit, without the guide text Waymark attaches). A text that was
/// longer than the limit is cut and flagged; <see cref="InputChars"/> and <see cref="OutputChars"/> are the sizes before the cut.
/// </summary>
public sealed record ToolPayload(string Input, bool InputCut, int InputChars, string Output, bool OutputCut, int OutputChars);

/// <summary>The kept part of a call without its output text, which is read on demand (see <see cref="IAuditStore.GetPayloadOutputAsync"/>).</summary>
public sealed record StoredToolPayload(string Input, bool InputCut, int InputChars, bool OutputCut, int OutputChars, DateTimeOffset StoredUtc);

/// <summary>How many calls have a kept input and output, and about how much text that is.</summary>
public sealed record ToolPayloadStats(int Calls, long Chars);

/// <summary>The limits and choices for keeping tool inputs and outputs.</summary>
public static class ToolPayloadLimits
{
    public const int MaxInputChars = 100_000;
    public const int MaxOutputChars = 200_000;
    public const int DefaultDays = 14;

    /// <summary>The retention periods offered in the admin app.</summary>
    public static readonly IReadOnlyList<int> DayChoices = [1, 3, 7, 14, 30];
}
