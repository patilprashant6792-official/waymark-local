using System.Text;
using Waymark.Core.Memory;

namespace Waymark.Modules.Code.Memory.Semantic;

/// <summary>
/// The text a semantic index reads for a flow. Pure, so what is embedded is the same whichever index is plugged in and a test can pin it down.
/// Names come first because they say the most per word: the flow's name with its dashes as spaces, then its summary, then what each step is called.
/// The text is capped at <see cref="MaxChars"/> (about 1,500 tokens) so a long flow still fits every provider's input limit; a flow under the cap gets
/// exactly the text it always had, so its stored vector stays valid.
/// </summary>
internal static class FlowSemanticText
{
    internal const int MaxChars = 6000;

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
            var line = step.Note.Length > 0 ? $"{Label(step.Spec)}: {step.Note}" : Label(step.Spec);
            if (text.Length + line.Length + Environment.NewLine.Length > MaxChars)
            {
                break;
            }

            text.AppendLine(line);
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
