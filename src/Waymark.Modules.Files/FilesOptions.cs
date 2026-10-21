using System.Globalization;
using Microsoft.Extensions.Configuration;

namespace Waymark.Modules.Files;

/// <summary>Limits for the file-changing tools.</summary>
public sealed record FilesOptions(int MaxWriteBytes, int MaxBatchEntries, int MaxPatchesPerCall, int MaxFolderEntries)
{
    public const int DefaultMaxWriteBytes = 2_000_000;
    public const int DefaultMaxBatchEntries = 50;
    public const int DefaultMaxPatchesPerCall = 200;
    public const int DefaultMaxFolderEntries = 20_000;

    public static FilesOptions FromConfiguration(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        return new FilesOptions(
            ReadPositive(configuration["Waymark:Files:MaxWriteBytes"], DefaultMaxWriteBytes, "Waymark:Files:MaxWriteBytes"),
            ReadPositive(configuration["Waymark:Files:MaxBatchEntries"], DefaultMaxBatchEntries, "Waymark:Files:MaxBatchEntries"),
            ReadPositive(configuration["Waymark:Files:MaxPatchesPerCall"], DefaultMaxPatchesPerCall, "Waymark:Files:MaxPatchesPerCall"),
            ReadPositive(configuration["Waymark:Files:MaxFolderEntries"], DefaultMaxFolderEntries, "Waymark:Files:MaxFolderEntries"));
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
