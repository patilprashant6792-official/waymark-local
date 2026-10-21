using System.Globalization;

namespace Waymark.Host.Edge;

/// <summary>Where to find ngrok and how long to wait for it. All values have working defaults; set Waymark:Ngrok:* to override.</summary>
public sealed record NgrokOptions(string? ExecutablePath, TimeSpan StartTimeout, TimeSpan InstallTimeout)
{
    public const int DefaultStartTimeoutSeconds = 30;
    public const int DefaultInstallTimeoutSeconds = 300;

    public static NgrokOptions FromConfiguration(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var path = configuration["Waymark:Ngrok:Path"];
        return new NgrokOptions(
            string.IsNullOrWhiteSpace(path) ? null : path.Trim(),
            TimeSpan.FromSeconds(ReadSeconds(configuration["Waymark:Ngrok:StartTimeoutSeconds"], DefaultStartTimeoutSeconds, "Waymark:Ngrok:StartTimeoutSeconds")),
            TimeSpan.FromSeconds(ReadSeconds(configuration["Waymark:Ngrok:InstallTimeoutSeconds"], DefaultInstallTimeoutSeconds, "Waymark:Ngrok:InstallTimeoutSeconds")));
    }

    private static int ReadSeconds(string? value, int fallback, string name)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return fallback;
        }

        if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds) || seconds is < 5 or > 3600)
        {
            throw new InvalidOperationException($"{name} must be between 5 and 3600 seconds.");
        }

        return seconds;
    }
}
