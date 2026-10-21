namespace Waymark.Core.Security;

/// <summary>
/// The record of secrets Waymark found in output that was about to go to Claude. It is kept as audit rows (category Config) with this action;
/// the target is the tool and the arguments hold the kinds and counts, never a value.
/// </summary>
public static class ExposureLedger
{
    public const string Action = "exposure.redacted";
}
