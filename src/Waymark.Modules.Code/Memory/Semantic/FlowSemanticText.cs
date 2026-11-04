using System.Text;
using Waymark.Core.Memory;

namespace Waymark.Modules.Code.Memory.Semantic;

/// <summary>
/// The text a semantic index reads for a flow. Pure, so what is embedded is the same whichever index is plugged in and a test can pin it down.
/// Names come first because they say the most per word: the flow's name with its dashes as spaces, then its summary, then what each step is called.
/// </summary>
internal static class FlowSemanticText
{
    public static FlowDocument Compose(long workspaceId, FeatureMap map)
    {
        ArgumentNullException.ThrowIfNull(map);
        var text = new StringBuilder();
        text.AppendLine(map.Name.Replace('-', ' '));
        if (map.Summary.Length > 0)
        {
            text.AppendLine(map.Summary);
        }

        if (map.DistinctReason.Length > 0)
        {
            text.AppendLine(map.DistinctReason);
        }

        foreach (var step in map.Steps)
        {
            text.Append(Label(step.Spec));
            if (step.Note.Length > 0)
            {
                text.Append(": ").Append(step.Note);
            }

            text.AppendLine();
        }

        return new FlowDocument(workspaceId, map.Id, map.Version, map.Name, text.ToString().TrimEnd());
    }

    // 'src/Orders/OrderService.cs:OrderService.Place' reads as 'OrderService.Place'; a file or a range reads as its path.
    private static string Label(string spec) =>
        FeatureComponentSpec.TryParse(spec, out var component, out _)
            ? component.Member is null
                ? component.Path
                : component.Type is null ? component.Member : $"{component.Type}.{component.Member}"
            : spec;
}
