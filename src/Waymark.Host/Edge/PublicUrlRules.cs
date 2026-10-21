using System.Net;

namespace Waymark.Host.Edge;

/// <summary>What counts as a usable public address for the tunnel: an https origin on a real domain name.</summary>
public static class PublicUrlRules
{
    public static bool TryNormalize(string? input, out string origin, out string error)
    {
        origin = string.Empty;
        var text = input?.Trim() ?? string.Empty;
        if (text.Length == 0)
        {
            error = "Enter the address ngrok shows, for example https://your-name.ngrok-free.app.";
            return false;
        }

        if (!text.Contains("://", StringComparison.Ordinal))
        {
            text = "https://" + text;
        }

        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri))
        {
            error = "That does not look like a web address.";
            return false;
        }

        if (uri.Scheme != Uri.UriSchemeHttps)
        {
            error = "The address must start with https://. Claude only connects over HTTPS.";
            return false;
        }

        if (uri.UserInfo.Length != 0)
        {
            error = "Remove the user name or password from the address.";
            return false;
        }

        if (!uri.IsDefaultPort)
        {
            error = "Use the address without a port; ngrok serves HTTPS on the standard port.";
            return false;
        }

        if ((uri.AbsolutePath.Length > 1 && uri.AbsolutePath != "/") || uri.Query.Length != 0 || uri.Fragment.Length != 0)
        {
            error = "Enter only the domain, without a path.";
            return false;
        }

        var host = uri.Host;
        if (!host.Contains('.', StringComparison.Ordinal)
            || IPAddress.TryParse(host, out _)
            || host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            || Uri.CheckHostName(host) != UriHostNameType.Dns)
        {
            error = "Use the public domain name from your tunnel, not localhost or an IP address.";
            return false;
        }

        origin = uri.GetLeftPart(UriPartial.Authority);
        error = string.Empty;
        return true;
    }
}
