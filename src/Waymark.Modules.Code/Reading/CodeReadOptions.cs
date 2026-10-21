using System.Globalization;
using Microsoft.Extensions.Configuration;

namespace Waymark.Modules.Code.Reading;

/// <summary>Limits for code_outline, code_method and code_read.</summary>
public sealed record CodeReadOptions(int MaxOutlineFiles, int MaxMethodsPerCall, int MaxReadBytes, int MaxMatchLines)
{
    public const int DefaultMaxOutlineFiles = 20;
    public const int DefaultMaxMethodsPerCall = 20;
    public const int DefaultMaxReadBytes = 2_000_000;
    public const int DefaultMaxMatchLines = 500;

    public static CodeReadOptions FromConfiguration(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        return new CodeReadOptions(
            ReadPositive(configuration, "MaxOutlineFiles", DefaultMaxOutlineFiles),
            ReadPositive(configuration, "MaxMethodsPerCall", DefaultMaxMethodsPerCall),
            ReadPositive(configuration, "MaxReadBytes", DefaultMaxReadBytes),
            ReadPositive(configuration, "MaxMatchLines", DefaultMaxMatchLines));
    }

    private static int ReadPositive(IConfiguration configuration, string key, int fallback)
    {
        var name = $"Waymark:Code:Read:{key}";
        var value = configuration[name];
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
