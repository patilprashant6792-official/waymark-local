using System.Globalization;
using Microsoft.Extensions.Configuration;

namespace Waymark.Modules.Code.Memory;

/// <summary>
/// Settings of the flows (feature maps), under <c>Waymark:FeatureMaps</c>: whether a file tool tells the agent when a change it made touches a step of a flow,
/// and how long that check may take before it is skipped, so a file change is never held up by it.
/// </summary>
internal sealed record FlowOptions(bool EditHints, TimeSpan EditHintTimeout)
{
    public const bool DefaultEditHints = true;
    public const int DefaultEditHintTimeoutSeconds = 2;
    private const int MaxTimeoutSeconds = 10;

    public static FlowOptions FromConfiguration(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var hints = DefaultEditHints;
        var hintText = configuration["Waymark:FeatureMaps:EditHints"];
        if (!string.IsNullOrWhiteSpace(hintText) && !bool.TryParse(hintText, out hints))
        {
            throw new InvalidOperationException("Waymark:FeatureMaps:EditHints must be true or false.");
        }

        var seconds = DefaultEditHintTimeoutSeconds;
        var secondsText = configuration["Waymark:FeatureMaps:EditHintTimeoutSeconds"];
        if (!string.IsNullOrWhiteSpace(secondsText)
            && (!int.TryParse(secondsText, NumberStyles.None, CultureInfo.InvariantCulture, out seconds) || seconds < 1 || seconds > MaxTimeoutSeconds))
        {
            throw new InvalidOperationException($"Waymark:FeatureMaps:EditHintTimeoutSeconds must be a whole number from 1 to {MaxTimeoutSeconds}.");
        }

        return new FlowOptions(hints, TimeSpan.FromSeconds(seconds));
    }
}
