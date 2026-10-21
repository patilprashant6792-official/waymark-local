using System.Globalization;
using Waymark.Core.Audit;
using Waymark.Host.Components.Shared;

namespace Waymark.Host.Ui;

/// <summary>Text for times, sizes and outcomes, so every page says them the same way.</summary>
public static class DisplayFormat
{
    public static string Ago(DateTimeOffset? moment, DateTimeOffset now)
    {
        if (moment is not { } value)
        {
            return "never";
        }

        var age = now - value;
        return age switch
        {
            { TotalSeconds: < 5 } => "just now",
            { TotalMinutes: < 1 } => string.Create(CultureInfo.InvariantCulture, $"{(int)age.TotalSeconds} s ago"),
            { TotalHours: < 1 } => string.Create(CultureInfo.InvariantCulture, $"{(int)age.TotalMinutes} min ago"),
            { TotalHours: < 48 } => string.Create(CultureInfo.InvariantCulture, $"{(int)age.TotalHours} h ago"),
            _ => string.Create(CultureInfo.InvariantCulture, $"{(int)age.TotalDays} days ago")
        };
    }

    public static string Clock(DateTimeOffset moment) =>
        moment.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture);

    public static string DateAndClock(DateTimeOffset moment) =>
        moment.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

    public static string Duration(int? milliseconds) =>
        milliseconds is not { } ms ? "-" : ms < 1000 ? $"{ms} ms" : (ms / 1000d).ToString("0.0", CultureInfo.InvariantCulture) + " s";

    public static string Bytes(int? bytes) => bytes switch
    {
        null => "-",
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => (bytes.Value / 1024d).ToString("0.0", CultureInfo.InvariantCulture) + " KB",
        _ => (bytes.Value / (1024d * 1024d)).ToString("0.0", CultureInfo.InvariantCulture) + " MB"
    };

    /// <summary>
    /// The tokens a call's output added to the AI's context. A leading "~" marks an estimate from the size, used for calls
    /// recorded before tokens were counted.
    /// </summary>
    public static string Tokens(int? resultTokens, int? resultBytes) => (resultTokens, resultBytes) switch
    {
        ({ } counted, _) => Count(counted),
        (null, { } size) => "~" + Count(TokenEstimate.FromBytes(size)),
        _ => "-"
    };

    /// <summary>A byte count of any size, as B, KB, MB or GB.</summary>
    public static string Size(long bytes) => bytes switch
    {
        < 1024 => string.Create(CultureInfo.InvariantCulture, $"{bytes} B"),
        < 1024 * 1024 => (bytes / 1024d).ToString("0.0", CultureInfo.InvariantCulture) + " KB",
        < 1024L * 1024 * 1024 => (bytes / (1024d * 1024d)).ToString("0.0", CultureInfo.InvariantCulture) + " MB",
        _ => (bytes / (1024d * 1024d * 1024d)).ToString("0.00", CultureInfo.InvariantCulture) + " GB"
    };

    public static string Count(long value) => value.ToString("N0", CultureInfo.InvariantCulture);

    public static (StatusState State, string Text) Outcome(AuditOutcome outcome) => outcome switch
    {
        AuditOutcome.Ok => (StatusState.Healthy, "OK"),
        AuditOutcome.Denied => (StatusState.Attention, "Denied"),
        _ => (StatusState.Failed, "Error")
    };

    /// <summary>A pairing code is shown in two groups of three so it is easy to compare by eye.</summary>
    public static string PairingCode(string code) =>
        code.Length == 6 ? code[..3] + " " + code[3..] : code;
}
