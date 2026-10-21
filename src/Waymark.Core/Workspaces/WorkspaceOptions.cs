using System.Globalization;
using Microsoft.Extensions.Configuration;

namespace Waymark.Core.Workspaces;

/// <summary>Limits for the folder scan done when a workspace is added.</summary>
public sealed record WorkspaceOptions(int MaxProjectFiles, int MaxDirectoriesScanned)
{
    public const int DefaultMaxProjectFiles = 500;
    public const int DefaultMaxDirectoriesScanned = 20_000;

    public static WorkspaceOptions FromConfiguration(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        return new WorkspaceOptions(
            ReadPositive(configuration["Waymark:Workspaces:MaxProjectFiles"], DefaultMaxProjectFiles, "Waymark:Workspaces:MaxProjectFiles"),
            ReadPositive(configuration["Waymark:Workspaces:MaxDirectoriesScanned"], DefaultMaxDirectoriesScanned, "Waymark:Workspaces:MaxDirectoriesScanned"));
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
