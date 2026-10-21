using System.Net;
using System.Globalization;
using Waymark.Core.Auth;

namespace Waymark.Host.Auth;

/// <summary>
/// The small pages the browser sees during pairing. They need no script: the waiting page reloads itself every two
/// seconds, so a strict content security policy (no scripts at all) applies.
/// </summary>
internal static class PairingPages
{
    private const string Policy = "default-src 'none'; style-src 'unsafe-inline'; base-uri 'none'; form-action 'none'; frame-ancestors 'none'";

    public static IResult Waiting(string pairingCode, string clientName, string redirectHost, DateTimeOffset expiresUtc, DateTimeOffset now, string waitPath, int adminPort)
    {
        var left = expiresUtc > now ? expiresUtc - now : TimeSpan.Zero;
        var remaining = string.Create(CultureInfo.InvariantCulture, $"{(int)left.TotalMinutes}:{left.Seconds:00}");
        var body =
            "<h1>Approve this connection</h1>" +
            $"<p><strong>{Enc(clientName)}</strong> wants to connect to Waymark.</p>" +
            $"<p class=\"code\" aria-label=\"Pairing code\">{Enc(RandomValues.FormatPairingCode(pairingCode))}</p>" +
            $"<p>On this computer, open Waymark at <strong>http://localhost:{adminPort.ToString(CultureInfo.InvariantCulture)}</strong> and approve the request only if it shows the same code.</p>" +
            $"<p class=\"muted\">After approval you are sent back to {Enc(redirectHost)}. This page updates by itself. Time left: {remaining}.</p>";
        return new HtmlResult(StatusCodes.Status200OK, Page("Approve this connection", body, waitPath), Policy);
    }

    public static IResult Message(int status, string title, string text) =>
        new HtmlResult(status, Page(title, $"<h1>{Enc(title)}</h1><p>{Enc(text)}</p>", null), Policy);

    private static string Page(string title, string bodyHtml, string? refreshPath)
    {
        var refresh = refreshPath is null ? string.Empty : $"<meta http-equiv=\"refresh\" content=\"2;url={Enc(refreshPath)}\">";
        return "<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\">" +
               "<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">" +
               refresh +
               $"<title>Waymark - {Enc(title)}</title>" +
               "<style>" +
               "body{font-family:system-ui,sans-serif;margin:0;background:#f6f7f9;color:#14171c}" +
               "main{max-width:34rem;margin:12vh auto;padding:2rem;background:#fff;border:1px solid #6b7280;border-radius:8px}" +
               ".code{font:700 2.5rem ui-monospace,Consolas,monospace;letter-spacing:.15em;margin:1.5rem 0}" +
               ".muted{color:#4f5866}" +
               "@media (prefers-color-scheme:dark){body{background:#14171c;color:#e8eaee}main{background:#1b1f27;border-color:#8a94a6}.muted{color:#9aa3b2}}" +
               "</style></head><body><main>" + bodyHtml + "</main></body></html>";
    }

    private static string Enc(string value) => WebUtility.HtmlEncode(value);
}
