using System.Globalization;
using Microsoft.Extensions.Configuration;

namespace Waymark.Core.Tools;

/// <summary>Limits applied to every tool call.</summary>
public sealed record ToolPipelineOptions(TimeSpan CallTimeout, int MaxOutputChars)
{
    public const int DefaultCallTimeoutSeconds = 60;
    public const int DefaultMaxOutputChars = 60_000;

    public static ToolPipelineOptions FromConfiguration(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var seconds = ReadPositive(configuration["Waymark:Tools:CallTimeoutSeconds"], DefaultCallTimeoutSeconds, "Waymark:Tools:CallTimeoutSeconds");
        var chars = ReadPositive(configuration["Waymark:Tools:MaxOutputChars"], DefaultMaxOutputChars, "Waymark:Tools:MaxOutputChars");
        return new ToolPipelineOptions(TimeSpan.FromSeconds(seconds), chars);
    }

    private static int ReadPositive(string? value, int fallback, string name)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return fallback;
        }

        if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var number) || number < 1)
        {
            throw new InvalidOperationException($"{name} must be a positive whole number.");
        }

        return number;
    }
}
